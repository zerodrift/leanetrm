using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// The report uses the portfolio's retained executor and exact market groups.
// A source or lifecycle failure occurs before this product yields any rows.
internal static class LngBlotter
{
 internal static async Task<LngRun> Run(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
  ImmutableArray<Guid> books,ImmutableArray<Guid> portfolios,CancellationToken ct,TradePositionSelection? position=null)=>
  new(connection,database,frame,await LngPositions.Read(connection,database.Manifest,books,portfolios,ct,position));
 internal sealed class LngRun(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,ImmutableArray<LngPosition> positions):IBlotterProductRun
 {
  public int Positions=>positions.Length;
  public async IAsyncEnumerable<ImmutableArray<JsonObject>> Units([EnumeratorCancellation]CancellationToken ct)
  {
   if(positions.IsEmpty)yield break;
   var values=await LngPortfolio.Values(connection,database,frame,positions,ct);
   if(values.Count!=positions.Length||!positions.Select(p=>p.Binding).ToHashSet().SetEquals(values.Keys))
    throw new MarketDataException("lng_portfolio","Every held LNG instrument must have exactly one complete retained value.");
   foreach(var position in positions)
   {
    var row=position.Holding();row["presentValue"]=values[position.Binding].ToExactString();
    yield return [row];
   }
  }
 }
}
