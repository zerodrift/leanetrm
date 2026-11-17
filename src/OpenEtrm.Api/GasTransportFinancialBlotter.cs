using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

internal static class GasTransportFinancialBlotter
{
    internal static async Task<ServiceRun> Run(NpgsqlConnection c,PreviewDatabase database,InputFrame frame,
        ImmutableArray<Guid> books,ImmutableArray<Guid> portfolios,CancellationToken ct,TradePositionSelection? position=null)=>
        new(c,database,frame,await GasTransportFinancialPositions.Read(c,database,frame,books,portfolios,ct,position));

    internal sealed class ServiceRun(NpgsqlConnection c,PreviewDatabase database,InputFrame frame,ImmutableArray<GasTransportFinancialPosition> positions):IBlotterProductRun
    {
        public int Positions=>positions.Length;
        public async IAsyncEnumerable<ImmutableArray<JsonObject>> Units([EnumeratorCancellation]CancellationToken ct)
        {
            var store=new GasTransportFinancialTicketStore(database);
            foreach(var position in positions)
            {
                var row=position.Holding();
                try
                {
                    var selected=await MarketInputSelection.Resolve(database,c,frame,new(position.Definition,position.Reference.CatalogId,position.Reference.RevisionId),[],false,ct);
                    var snapshot=selected.Data.Snapshot(selected.Frame).WithKnowledge(frame.ContextId!.Value,position.Agreement.Instant("knownAt"));
                    row["presentValue"]=await store.Value(c,selected.Data,selected.Frame,snapshot,position.Agreement,false,ct);
                    row["unavailableReason"]=null;
                }
                catch(MarketDataException error)when(GasTransportFinancialTicketStore.Unpriced(error))
                {row["unavailableReason"]=position.Agreement.OptionalText("unpricedReason")??error.Code;}
                yield return [row];
            }
        }
    }
}
