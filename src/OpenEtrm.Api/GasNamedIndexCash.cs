using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class GasNamedIndexCash
{
 internal static FloatingSide Resolve(PreviewData data,PhysicalGasSide physical,GasCashRequest cash,DateOnly from,DateOnly to)
 {
  var terms=cash.Indexed??throw GasIndexedCash.Terms();
  if(terms.ObservationConvention is not(GasIndexedCash.Observation or GasIndexedCash.Monthly)||string.IsNullOrWhiteSpace(terms.Index))
   throw GasIndexedCash.Terms();
  var quote=physical.Assignments[0].Quote;var index=new ReferenceKey(data.ReferenceRevision,terms.Index);
  var curves=Curves(data,physical.Product.Code,quote.Key.Code,terms.Index);
  if(curves.Length!=1)throw Coverage("The named index requires one curve with the exact commodity, quote and index settlement target.");
  var code=curves[0].Text("code");var curve=new CurveKey(data.DefinitionRevision,code);
  var members=data.Definitions["curve_members"].Where(m=>m.Text("curve_code")==code).Select(m=>m.Id("contract_id")).ToHashSet();
  var contracts=data.Contracts.Where(c=>members.Contains(c.Key.Id)).ToArray();
  (SwapProjection,IndexFixing) Publication(DateOnly day)
  {
   var matches=contracts.Where(c=>GasIndexedCash.Covers(c,day,terms.ObservationConvention==GasIndexedCash.Monthly)).ToArray();
   if(matches.Length!=1)throw Coverage("Each payment day requires one exact daily or containing calendar-month contract on its named index curve.");
   var contract=matches[0];
   return(new(new(curve,contract.Key),contract),GasIndexedCash.Fixing(data,index,quote,contract,"gas_index_coverage"));
  }
  return GasIndexedCash.Build(data,physical,cash,from,to,Publication,terms.ForecastCurve?.ToBinding(),GasIndexedCash.Terms);
 }
 internal static JsonElement[] Curves(PreviewData data,string product,string quote,string index)=>
  data.Definitions["curves"].Where(curve=>curve.Text("commodity_code")==product&&curve.Text("quote_code")==quote&&
   data.ReferenceRows("settlement_targets","code",curve.Text("settlement_target_code")).ToArray() is [var target]&&
   target.Text("kind")=="index"&&target.Text("index_a_code")==index&&target.OptionalText("index_b_code") is null).ToArray();
 internal static IEnumerable<object> Choices(PreviewData data)=>
  data.Definitions["curves"].Select(c=>(Product:c.Text("commodity_code"),Quote:c.Text("quote_code"))).Distinct()
   .SelectMany(pair=>data.References["price_index_definitions"].Where(index=>Curves(data,pair.Product,pair.Quote,index.Text("code")).Length==1)
    .Select(index=>(object)new{product=pair.Product,quote=pair.Quote,code=index.Text("code"),name=index.Text("code")}));
 static MarketDataException Coverage(string message)=>new("gas_index_coverage",message);
}
