using System.Collections.Immutable;
using OpenEtrm.MarketData;
using Npgsql;

namespace OpenEtrm.Api;

// An optional gas market has its own exact definitions and dated price selections.
// It never changes the primary market or borrows its price pins.
internal static class GasMarketSelection
{
 internal static bool Configured(PreviewManifest manifest)=>manifest.GasMarket is not null||(manifest.RetainedGasMarkets?.Length??0)>0;
 internal static async Task<(PreviewData Data,InputFrame Frame)> Read(PreviewDatabase database,NpgsqlConnection connection,InputFrame frame,CancellationToken ct,Guid? trade=null)
 {
  Guid? revision=null;
  if(trade is {} id)
  {
   var rows=await PreviewDatabase.Rows(connection,"SELECT json_build_object('definition_revision_id',i.definition_revision_id)::text FROM trading.gas_trade_revisions r JOIN trading.gas_instruments i USING(catalog_id,instrument_id) WHERE(r.catalog_id,r.trade_id)=($1,$2) ORDER BY r.revision DESC LIMIT 1",[database.Manifest.CatalogId,id],ct);
   if(rows.Length!=1)throw new MarketDataException("gas_market_context","The selected trade has no visible retained gas market.");
   revision=rows[0].Id("definition_revision_id");
  }
  var selected=Select(database.Manifest,frame,revision);
  var corrections=(revision is null?database.Manifest.GasMarket is not null:revision!=database.Manifest.RevisionId)&&frame.ResolvesLatestCorrection;
  var data=await database.Read(connection,ct,selectedRevision:selected.Revision,observationIds:selected.Frame.ObservationIds,
   observationScope:corrections?ObservationReadScope.FactHistory:ObservationReadScope.ExactPins);
  return(data,corrections?data.SelectPinned(selected.Frame):selected.Frame);
 }
 internal static (Guid Revision,InputFrame Frame) Select(PreviewManifest manifest,InputFrame frame,Guid? requiredRevision=null)
 {
  if(requiredRevision==manifest.RevisionId)return(manifest.RevisionId,frame);
  var market=manifest.GasMarket;
  var declared=(market is null?Enumerable.Empty<PreviewGasMarket>():[market]).Concat(manifest.RetainedGasMarkets??[]).ToArray();
  if(declared.Any(m=>m.RevisionId==Guid.Empty||m.RevisionId==manifest.RevisionId)||declared.Select(m=>m.RevisionId).Distinct().Count()!=declared.Length)
   throw new MarketDataException("gas_market_context","Declare each independent gas revision once, separately from the primary market.");
  if(requiredRevision is {} required)
  {
   var retained=declared.Where(m=>m.RevisionId==required).ToArray();
   if(retained.Length!=1)throw new MarketDataException("gas_market_context","The retained trade requires one explicitly configured gas market selection.");
   market=retained[0];
  }
  if(market is null)return(manifest.RevisionId,frame);
  var matches=(market.Frames??[]).Where(f=>f.At==frame.At).ToArray();
  if(market.RevisionId==Guid.Empty||matches.Length!=1)
   throw new MarketDataException("gas_market_context","The selected date requires one explicitly configured gas market selection.");
  var source=matches[0];
  if(source.ObservationIds is null||source.CurveContracts is null||source.ObservationIds.Distinct().Count()!=source.ObservationIds.Length)
   throw new MarketDataException("gas_market_context","The gas market selection contains duplicate price inputs.");
  var selected=frame with{Name="Selected gas market",ObservationIds=[..source.ObservationIds],
   CurveContracts=source.CurveContracts.ToImmutableDictionary(p=>p.Key,p=>p.Value.ToImmutableArray()),
   FrozenObservationIds=source.FrozenObservationIds is {} frozen?[..frozen]:default,
   OriginCloseId=null,SwapMarketSelections=null,PpaBundle=null};
  return(market.RevisionId,selected);
 }
}
