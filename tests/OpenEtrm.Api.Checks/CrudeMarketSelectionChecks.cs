using System.Collections.Immutable;
using OpenEtrm.Api;
internal static class CrudeMarketSelectionChecks
{
 internal static void Run()
 {
  var primary=Guid.NewGuid();var selected=Guid.NewGuid();var oldPin=Guid.NewGuid();var crudePin=Guid.NewGuid();var at=DateTimeOffset.Parse("2032-10-04T20:00:00Z");
  var frame=new InputFrame(Guid.NewGuid(),"Working",at,[oldPin],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty){OriginCloseId=Guid.NewGuid()};
  var manifest=new PreviewManifest("local-synthetic-preview",Guid.NewGuid(),primary,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),[],null,FuturesConvention:"contract_lot_execution_pnl_v2");
  Reject(manifest,frame);
  var context=Guid.NewGuid();frame=frame with{ContextId=context};
  var declared=new PreviewSeedFrame(at,[crudePin],new(){["crude"]=[Guid.NewGuid()]},[crudePin]);
  var configured=manifest with{CrudeMarket=new(selected,[declared])};
  var result=PhysicalMarketSelection.Crude.Select(configured,frame);
  Require(result.Revision==selected&&result.Frame.ObservationIds.SequenceEqual([crudePin]),"Crude reads select only their declared price inputs.");
  Require(result.Frame.Id==frame.Id&&result.Frame.At==at&&result.Frame.ContextId==context,"Selection preserves the dated certified view.");
  Require(result.Frame.FrozenObservationIds.SequenceEqual([crudePin]),"Only explicitly declared crude pins retain their frozen treatment.");
  Require(result.Frame.OriginCloseId is null&&!result.Frame.ObservationIds.Contains(oldPin),"A financial close cannot certify an independent crude market.");
  Require(manifest.RevisionId==primary&&frame.ObservationIds.SequenceEqual([oldPin]),"Selection does not mutate the financial market.");
  try{PhysicalMarketSelection.Crude.Select(configured,frame,primary);throw new Exception("Undeclared primary accepted.");}catch(OpenEtrm.MarketData.MarketDataException e)when(e.Code=="crude_market_context"){}
  var previous=Guid.NewGuid();var previousPin=Guid.NewGuid();
  var history=configured with{RetainedCrudeMarkets=[new(previous,[declared with{ObservationIds=[previousPin]}])]};
  var oldCrude=PhysicalMarketSelection.Crude.Select(history,frame,previous);
  Require(oldCrude.Revision==previous&&oldCrude.Frame.ObservationIds.SequenceEqual([previousPin]),"Older crude trades use their explicitly retained price selections.");
  try{PhysicalMarketSelection.Crude.Select(configured,frame,previous);throw new Exception("An undeclared retained source was accepted.");}catch(OpenEtrm.MarketData.MarketDataException e)when(e.Code=="crude_market_context"){}
  Reject(configured with{RetainedCrudeMarkets=[configured.CrudeMarket!]},frame);
  Reject(configured with{CrudeMarket=new(selected,[])},frame);
  Reject(configured with{CrudeMarket=new(selected,[declared,declared])},frame);
  Reject(configured with{CrudeMarket=new(Guid.Empty,[declared])},frame);
  Reject(configured with{CrudeMarket=new(primary,[declared])},frame);
  Reject(configured,frame with{At=at.AddDays(1)});
  var gasRevision=Guid.NewGuid();var gasPin=Guid.NewGuid();
  var mixed=configured with{GasMarket=new(gasRevision,[declared with{ObservationIds=[gasPin]}])};
  var json=System.Text.Json.JsonSerializer.Serialize(mixed,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
  var restored=System.Text.Json.JsonSerializer.Deserialize<PreviewManifest>(json,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
  var gas=PhysicalMarketSelection.Gas.Select(restored,frame);
  var crude=PhysicalMarketSelection.Crude.Select(restored,frame);
  Require(gas.Revision==gasRevision&&gas.Frame.ObservationIds.SequenceEqual([gasPin])&&
   crude.Revision==selected&&crude.Frame.ObservationIds.SequenceEqual([crudePin]),
   "The common selection contract preserves separate gas/crude manifest fields and exact pins across JSON round trips.");
  Require(ReferenceEquals(PhysicalMarketSelection.Gas.Select(restored,frame,primary).Frame,frame),
   "Gas primary-market compatibility remains an explicit product rule in a mixed manifest.");
  try{PhysicalMarketSelection.Crude.Select(restored,frame,primary);throw new Exception("Crude inherited gas primary-market behavior.");}
  catch(OpenEtrm.MarketData.MarketDataException e)when(e.Code=="crude_market_context"){}
  Console.WriteLine("PASS scoped crude market: exact dates and pins, retained view, unchanged financial market and missing/ambiguous refusal");
 }
 static void Reject(PreviewManifest manifest,InputFrame frame){try{PhysicalMarketSelection.Crude.Select(manifest,frame);}catch(OpenEtrm.MarketData.MarketDataException e)when(e.Code=="crude_market_context"){return;}throw new Exception("An incomplete crude market silently fell back.");}
 static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
}
