using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class GasPointIndexCash
{
 internal static FloatingSide Resolve(PreviewData data,PhysicalGasSide physical,GasCashRequest cash,JsonElement point,DateOnly from,DateOnly to)
 {
  var terms=cash.Indexed??throw Invalid();
  if(terms.Index is not null||terms.ObservationConvention!=GasIndexedCash.Point)throw Invalid();
  var projection=GasPointPricing.Configured(data.DefinitionRevision.CatalogId,point)??throw Invalid();
  if(terms.ForecastCurve is not null&&terms.ForecastCurve!=projection)throw new MarketDataException("gas_location_curve_conflict","Cash projection must use the exact location forecast selected by this point setup.");
  var quote=physical.Assignments[0].Quote;
  (SwapProjection,IndexFixing) Publication(DateOnly day)
  {
   var mappings=point.GetProperty("settlement_periods").EnumerateArray().Where(m=>DateWorkspace.Date(m.Text("valid_from"))<=day&&DateWorkspace.Date(m.Text("valid_to"))>=day.AddDays(1)).ToArray();
   if(mappings.Length!=1)throw Invalid();var mapping=mappings[0];var code=mapping.Text("curve_code");var index=new ReferenceKey(data.ReferenceRevision,mapping.Text("index_code"));
   var curves=data.Definitions["curves"].Where(c=>c.Text("code")==code&&c.Text("commodity_code")==physical.Product.Code&&c.Text("quote_code")==quote.Key.Code).ToArray();
   if(curves.Length!=1)throw Invalid();
   var monthly=mapping.Text("observation_convention") switch{"daily_published_index_v1"=>false,"monthly_published_index_v1"=>true,_=>throw Invalid()};
   var members=data.Definitions["curve_members"].Where(m=>m.Text("curve_code")==code).Select(m=>m.Id("contract_id")).ToHashSet();
   var contracts=data.Contracts.Where(c=>members.Contains(c.Key.Id)&&GasIndexedCash.Covers(c,day,monthly)).ToArray();
   if(contracts.Length!=1)throw Invalid();var contract=contracts[0];
   return(new(new(new(data.DefinitionRevision,code),contract.Key),contract),GasIndexedCash.Fixing(data,index,quote,contract,"gas_point_index_terms"));
  }
  return GasIndexedCash.Build(data,physical,cash,from,to,Publication,projection.ToBinding(),Invalid);
 }
 static MarketDataException Invalid()=>new("gas_point_index_terms","Select location publication pricing with explicit spread and payment day; every gas day requires one exact point index period and compatible publication contract.");
}
