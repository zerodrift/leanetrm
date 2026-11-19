using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Each option is valued on its own: one that lacks marks or holds an elapsed exercise is listed with its reason
// and does not withhold the others.
internal static class HeatRateCallBlotter
{
    internal static async Task<OptionRun> Run(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        ImmutableArray<Guid> books,ImmutableArray<Guid> portfolios,CancellationToken ct,TradePositionSelection? position=null)=>
        new(connection,database,frame,await HeatRateCallPositions.Read(connection,database.Manifest,books,portfolios,ct,position));
    internal sealed class OptionRun(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,ImmutableArray<HeatRateCallPosition> positions):IBlotterProductRun
    {
        public int Positions=>positions.Length;
        public async IAsyncEnumerable<ImmutableArray<JsonObject>> Units([EnumeratorCancellation]CancellationToken ct)
        {
            foreach(var position in positions)
            {
                var row=position.Holding();
                try{row["presentValue"]=(await HeatRateCallPortfolio.Value(connection,database,frame,position,ct)).ToExactString();}
                catch(MarketDataException error)when(HeatRateCallPortfolio.Unpriced(error)){row["unavailableReason"]=error.Code;}
                yield return [row];
            }
        }
    }
}
