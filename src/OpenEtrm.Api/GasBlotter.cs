using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

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
   var sources=new Dictionary<Guid,(PreviewData Data,InputFrame Frame)>();var composition=new GasCompositionStore(database);
   // The run's positions share one read of the saved custom curve versions their projections and markets qualify against.
   var savedVersions=new CustomCurves.SavedVersions(database);
   foreach(var position in positions)
   {
    var row=position.Holding();
    try
    {
     if(!sources.TryGetValue(position.Definition,out var source))
     {source=await PhysicalMarketSelection.Gas.Read(database,connection,frame,ct,position.Trade);sources.Add(position.Definition,source);}
     // A single-point trade is valued as the composition it is, from its own retained programs (GasCompositionStore.ReadSinglePoint).
     var execution=position.Head.GetProperty("composition").GetBoolean()
      ?await composition.Load(connection,source.Data,source.Frame,position.Trade,ct)
      :await composition.LoadSinglePoint(connection,source.Data,source.Frame,position.Trade,ct);
     // The retained programs, not the valuation's outcome, select the measure: a refusing started trade keeps its own.
     // Started compositions admit only their explicit no-discounting selection, which the label states.
     if(execution.Started){row["measure"]=GasPositions.LifecycleMeasure;row["measureLabel"]=GasPositions.LifecycleLabel;if(execution.Terms.Discounting==false)row["discounting"]="undiscounted";}
     var valuation=await composition.Value(connection,source.Data,source.Frame,execution,ct,savedVersions:savedVersions);
     row["presentValue"]=valuation.PresentValue;
     // A started-delivery trade's headline is its labelled sum, with both components shown.
     if(valuation.Measures is {} measures)
     {row["unrealized"]=measures.Unrealized;row["realizedUnsettled"]=measures.RealizedUnsettled;}
    }
    catch(MarketDataException error)when(ValuationStatus.PositionSpecific(error)||error.Code is "gas_market_context" or "gas_delivery_lifecycle"||PhysicalLifecycle.TradeRefusal(error.Code))
    {row["unavailableReason"]=error.Code;}
    yield return [row];
   }
  }
 }
}
