using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Values the retained contract through the ordinary gas editors. Daily delivery
// revisions are operational facts, not a second holding or an implicit PV policy.
internal static class GasBlotter
{
 internal static async Task<GasRun> Run(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
  ImmutableArray<Guid> books,ImmutableArray<Guid> portfolios,CancellationToken ct,TradePositionSelection? position=null)=>
  new(connection,database,frame,await GasPositions.Read(connection,database.Manifest,books,portfolios,ct,position));
 internal sealed class GasRun(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,ImmutableArray<GasPosition> positions):IBlotterProductRun
 {
  public int Positions=>positions.Length;
  public async IAsyncEnumerable<ImmutableArray<JsonObject>> Units([EnumeratorCancellation]CancellationToken ct)
  {
   var sources=new Dictionary<Guid,(PreviewData Data,InputFrame Frame)>();
   foreach(var position in positions)
   {
    var row=position.Holding();
    try
    {
     if(!sources.TryGetValue(position.Definition,out var source))
     {source=await GasMarketSelection.Read(database,connection,frame,ct,position.Trade);sources.Add(position.Definition,source);}
     var value=position.Head.GetProperty("composition").GetBoolean()
      ?await new GasCompositionStore(database).Editor(connection,source.Data,source.Frame,position.Trade,ct)
      :await new PhysicalGasStore(database).Editor(connection,source.Data,source.Frame,position.Trade,ct);
     var result=JsonSerializer.SerializeToElement(value,TradingBlotter.Json).GetProperty("valuation");
     row["presentValue"]=result.Text("presentValue");
    }
    catch(MarketDataException error)when(ValuationStatus.PositionSpecific(error)||error.Code is "gas_market_context" or "gas_delivery_lifecycle")
    {row["unavailableReason"]=error.Code;}
    yield return [row];
   }
  }
 }
}
