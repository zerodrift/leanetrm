using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

internal static class GasParkLoanBlotter
{
    internal static async Task<ServiceRun> Run(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        ImmutableArray<Guid> books,ImmutableArray<Guid> portfolios,CancellationToken ct,TradePositionSelection? position=null)=>
        new(connection,database,frame,await GasParkLoanPositions.Read(connection,database,frame,books,portfolios,ct,position));

    internal sealed class ServiceRun(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,ImmutableArray<GasParkLoanPosition> positions):IBlotterProductRun
    {
        public int Positions=>positions.Length;
        public async IAsyncEnumerable<ImmutableArray<JsonObject>> Units([EnumeratorCancellation]CancellationToken ct)
        {
            var sources=new Dictionary<Guid,(PreviewData Data,InputFrame Frame)>();var store=new GasParkLoanTicketStore(database);
            foreach(var position in positions)
            {
                var row=position.Holding();
                try
                {
                    if(!sources.TryGetValue(position.Definition,out var source))
                    {
                        source=await MarketInputSelection.Resolve(database,connection,frame,new(position.Definition,position.Reference.CatalogId,position.Reference.RevisionId),[],false,ct);
                        sources.Add(position.Definition,source);
                    }
                    var snapshot=source.Data.Snapshot(source.Frame).WithKnowledge(frame.ContextId!.Value,position.Agreement.Instant("knownAt"));
                    row["presentValue"]=await store.Value(connection,source.Data,source.Frame,snapshot,position.Agreement,false,ct);
                }
                catch(MarketDataException error)when(ValuationStatus.PositionSpecific(error)||error.Code is "gas_park_loan_reconciliation" or "gas_balance_fee_advance_window" or "discount_curve")
                {row["unavailableReason"]=error.Code;}
                yield return [row];
            }
        }
    }
}
