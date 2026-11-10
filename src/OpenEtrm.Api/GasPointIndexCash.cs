using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class GasPointIndexCash
{
 internal static FloatingSide Resolve(PreviewData data,PhysicalGasSide physical,GasCashRequest cash,JsonElement point)
 {
  var terms=cash.Indexed!;
  if(cash.Role!="indexed_consideration"||cash.Price is not null||cash.TariffCode is not null||cash.PaymentAt is not null||terms.Index is not null||
   terms.PaymentConvention!=GasIndexedCash.Payment||terms.PaymentDay is <1 or >28||string.IsNullOrWhiteSpace(terms.Spread))throw Invalid();
  var spread=ExactDecimal.Parse(terms.Spread);var limit=ExactDecimal.Parse("1000000000000");if(spread.CompareTo(limit)>=0||spread.CompareTo(-limit)<=0)throw Invalid();
  var projection=GasPointPricing.Configured(data.DefinitionRevision.CatalogId,point)??throw Invalid();
  if(terms.ForecastCurve is not null&&terms.ForecastCurve!=projection)throw new MarketDataException("gas_location_curve_conflict","Cash projection must use the exact location forecast selected by this point setup.");
  var quote=physical.Assignments[0].Quote;var periods=ImmutableArray.CreateBuilder<FloatingSidePeriod>();
  foreach(var day in physical.GenerateSchedule().Quantities.Rows)
  {
   var to=day.OperatingDay.AddDays(1);
   var mappings=point.GetProperty("settlement_periods").EnumerateArray().Where(m=>DateWorkspace.Date(m.Text("valid_from"))<=day.OperatingDay&&DateWorkspace.Date(m.Text("valid_to"))>=to).ToArray();
   if(mappings.Length!=1)throw Invalid();var mapping=mappings[0];var code=mapping.Text("curve_code");var index=new ReferenceKey(data.ReferenceRevision,mapping.Text("index_code"));
   var curves=data.Definitions["curves"].Where(c=>c.Text("code")==code&&c.Text("commodity_code")==physical.Product.Code&&c.Text("quote_code")==quote.Key.Code).ToArray();
   if(curves.Length!=1)throw Invalid();
   var members=data.Definitions["curve_members"].Where(m=>m.Text("curve_code")==code).Select(m=>m.Id("contract_id")).ToHashSet();
   var contracts=data.Contracts.Where(c=>members.Contains(c.Key.Id)&&c.Kind==ContractKind.Forward&&c.DeliveryFrom<=day.OperatingDay&&c.DeliveryTo>=to&&
    (mapping.Text("observation_convention") switch{"daily_published_index_v1"=>c.DeliveryFrom==day.OperatingDay&&c.DeliveryTo==to,
     "monthly_published_index_v1"=>c.DeliveryFrom.Day==1&&c.DeliveryFrom.AddMonths(1)==c.DeliveryTo,_=>false})).ToArray();
   if(contracts.Length!=1)throw Invalid();var contract=contracts[0];
   var fixings=PriceSettlementInputs.Fixings(data,PriceSettlementSelection.CurveTarget(index),quote,[contract]);if(fixings.Length!=1)throw Invalid();
   var month=new DateOnly(day.OperatingDay.Year,day.OperatingDay.Month,1).AddMonths(1);
   var payment=new DateTimeOffset(month.AddDays(terms.PaymentDay-1).ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));if(payment<day.End)throw Invalid();
   periods.Add(new(day.OperatingDay,to,fixings[0].FixingAt,payment,FloatingAveraging.CalendarDayArithmetic,
    [new(new(new(data.DefinitionRevision,code),contract.Key),contract)],fixings,null,new UnitPrice(spread,quote),null,projection.ToBinding(),
    new(BigInteger.Parse(day.Delivery.Numerator,CultureInfo.InvariantCulture),BigInteger.Parse(day.Delivery.Denominator,CultureInfo.InvariantCulture)),FloatingSide.DeliveryIndexImplementation));
  }
  return new(Guid.NewGuid(),quote,physical.UnitQuantity,periods.ToImmutable(),FloatingSide.DeliveryIndexImplementation);
 }
 static MarketDataException Invalid()=>new("gas_point_index_terms","Select location publication pricing with explicit spread and payment day; every gas day requires one exact point index period and compatible publication contract.");
}
