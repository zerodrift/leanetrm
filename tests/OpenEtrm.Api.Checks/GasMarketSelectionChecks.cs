using System.Collections.Immutable;
using OpenEtrm.Api;
internal static class GasMarketSelectionChecks
{
 internal static void Run()
 {
  var primary=Guid.NewGuid();var selected=Guid.NewGuid();var oldPin=Guid.NewGuid();var gasPin=Guid.NewGuid();var at=DateTimeOffset.Parse("2032-10-04T20:00:00Z");
  var frame=new InputFrame(Guid.NewGuid(),"Working",at,[oldPin],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty){OriginCloseId=Guid.NewGuid()};
  var manifest=new PreviewManifest("local-synthetic-preview",Guid.NewGuid(),primary,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),[],null,FuturesConvention:"contract_lot_execution_pnl_v2");
  var baseline=PhysicalMarketSelection.Gas.Select(manifest,frame);
  Require(baseline.Revision==primary&&ReferenceEquals(baseline.Frame,frame),"Unconfigured gas reads retain the original explicit market.");
  var context=Guid.NewGuid();frame=frame with{ContextId=context};
  var declared=new PreviewSeedFrame(at,[gasPin],new(){["gas"]=[Guid.NewGuid()]},[gasPin]);
  var configured=manifest with{GasMarket=new(selected,[declared])};
  var result=PhysicalMarketSelection.Gas.Select(configured,frame);
  Require(result.Revision==selected&&result.Frame.ObservationIds.SequenceEqual([gasPin]),"Gas reads select only their declared price inputs.");
  Require(result.Frame.Id==frame.Id&&result.Frame.At==at&&result.Frame.ContextId==context,"Selection preserves the dated certified view.");
  Require(result.Frame.FrozenObservationIds.SequenceEqual([gasPin]),"Only explicitly declared gas pins retain their frozen treatment.");
  Require(result.Frame.OriginCloseId is null&&!result.Frame.ObservationIds.Contains(oldPin),"A financial close cannot certify an independent gas market.");
  Require(manifest.RevisionId==primary&&frame.ObservationIds.SequenceEqual([oldPin]),"Selection does not mutate the financial market.");
  var retained=PhysicalMarketSelection.Gas.Select(configured,frame,primary);
  Require(retained.Revision==primary&&ReferenceEquals(retained.Frame,frame),"Existing primary-market gas trades keep their original source after configuration.");
  var previous=Guid.NewGuid();var previousPin=Guid.NewGuid();
  var history=configured with{RetainedGasMarkets=[new(previous,[declared with{ObservationIds=[previousPin]}])]};
  var oldGas=PhysicalMarketSelection.Gas.Select(history,frame,previous);
  Require(oldGas.Revision==previous&&oldGas.Frame.ObservationIds.SequenceEqual([previousPin]),"Older gas trades use their explicitly retained price selections.");
  try{PhysicalMarketSelection.Gas.Select(configured,frame,previous);throw new Exception("An undeclared retained source was accepted.");}catch(OpenEtrm.MarketData.MarketDataException e)when(e.Code=="gas_market_context"){}
  Reject(configured with{RetainedGasMarkets=[configured.GasMarket!]},frame);
  Reject(configured with{GasMarket=new(selected,[])},frame);
  Reject(configured with{GasMarket=new(selected,[declared,declared])},frame);
  Reject(configured with{GasMarket=new(Guid.Empty,[declared])},frame);
  Reject(configured with{GasMarket=new(primary,[declared])},frame);
  Reject(configured,frame with{At=at.AddDays(1)});
  Console.WriteLine("PASS scoped gas market: exact dates and pins, retained view, unchanged financial market and missing/ambiguous refusal");
 }
 static void Reject(PreviewManifest manifest,InputFrame frame){try{PhysicalMarketSelection.Gas.Select(manifest,frame);}catch(OpenEtrm.MarketData.MarketDataException e)when(e.Code=="gas_market_context"){return;}throw new Exception("An incomplete gas market silently fell back.");}
 static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
}
