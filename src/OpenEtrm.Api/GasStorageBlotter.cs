using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Each contract is valued on its own: one without a plan in force, or already flowing, is listed with its reason
// and does not withhold the others.
internal static class GasStorageBlotter
{
    internal static async Task<ContractRun> Run(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        ImmutableArray<Guid> books,ImmutableArray<Guid> portfolios,CancellationToken ct,TradePositionSelection? position=null)=>
        new(connection,database,frame,await GasStoragePositions.Read(connection,database.Manifest,books,portfolios,ct,position));
    internal sealed class ContractRun(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,ImmutableArray<GasStoragePosition> positions):IBlotterProductRun
    {
        public int Positions=>positions.Length;
        public async IAsyncEnumerable<ImmutableArray<JsonObject>> Units([EnumeratorCancellation]CancellationToken ct)
        {
            foreach(var position in positions)
            {
                var row=position.Holding();
                try{row["presentValue"]=(await GasStoragePortfolio.Value(connection,database,frame,position,ct)).ToExactString();}
                catch(MarketDataException error)when(GasStoragePortfolio.Unpriced(error)){row["unavailableReason"]=error.Code;}
                yield return [row];
            }
        }
    }
}
