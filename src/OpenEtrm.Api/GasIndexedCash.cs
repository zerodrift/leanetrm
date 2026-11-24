using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasIndexedCashRequest([property:JsonRequired]string? Index,[property:JsonRequired]string ObservationConvention,
 [property:JsonRequired]string PaymentConvention,[property:JsonRequired]int PaymentDay,[property:JsonRequired]string Spread,[property:JsonRequired]CustomCurveProjectionInput? ForecastCurve);

// Both point publications and named indexes produce the same daily payment
// periods and use the Side's published-or-forecast price behavior.
internal static class GasIndexedCash
{
 internal const string Observation="named_index_daily_v1",Monthly="named_index_monthly_v1",Point="point_publications_v1",Payment="next_month_day_utc_v1";
 internal static FloatingSide Resolve(PreviewData data,PhysicalGasSide physical,GasCashRequest cash,JsonElement point) =>
  Resolve(data,physical,cash,point,physical.Quantities.From,physical.Quantities.To);
 internal static FloatingSide Resolve(PreviewData data,PhysicalGasSide physical,GasCashRequest cash,JsonElement point,DateOnly from,DateOnly to) =>
  cash.Indexed?.ObservationConvention switch
  {
   Point=>GasPointIndexCash.Resolve(data,physical,cash,point,from,to),
   Observation or Monthly=>GasNamedIndexCash.Resolve(data,physical,cash,from,to),
   _=>throw Terms()
  };
 internal static FloatingSide Build(PreviewData data,PhysicalGasSide physical,GasCashRequest cash,DateOnly from,DateOnly to,
  Func<DateOnly,(SwapProjection Projection,IndexFixing Fixing)> publication,CustomCurveProjectionBinding? forecast,Func<MarketDataException> invalid)
 {
  var terms=cash.Indexed??throw invalid();
  if(cash.Role!="indexed_consideration"||cash.Price is not null||cash.TariffCode is not null||cash.PaymentAt is not null||
   terms.PaymentConvention!=Payment||terms.PaymentDay is <1 or >28||string.IsNullOrWhiteSpace(terms.Spread))throw invalid();
  if(from>=to||from<physical.Quantities.From||to>physical.Quantities.To)
   throw new MarketDataException("gas_composition_input","Payment dates must lie within the source's gas days.");
  var spread=ExactDecimal.Parse(terms.Spread);var limit=ExactDecimal.Parse("1000000000000");
  if(spread.CompareTo(limit)>=0||spread.CompareTo(-limit)<=0)throw invalid();
  var calendars=SideCalendarPolicies.Resolve(data,cash.CalendarPolicy);
  calendars.RequirePurposes(true,true,true);calendars.For(CalendarPurpose.Observation).RequireCoverage(from,to);
  var quote=physical.Assignments[0].Quote;var periods=ImmutableArray.CreateBuilder<FloatingSidePeriod>();
  foreach(var day in physical.GenerateSchedule().Quantities.Rows.Where(d=>d.OperatingDay>=from&&d.OperatingDay<to))
  {
   var selected=publication(day.OperatingDay);
   var nextMonth=new DateOnly(day.OperatingDay.Year,day.OperatingDay.Month,1).AddMonths(1);
   var payment=new DateTimeOffset(nextMonth.AddDays(terms.PaymentDay-1).ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
   PaymentDatePolicy.RequireCompletion(payment,day.End);
   periods.Add(new(day.OperatingDay,day.OperatingDay.AddDays(1),selected.Fixing.FixingAt,payment,FloatingAveraging.CalendarDayArithmetic,
    [selected.Projection],[selected.Fixing],null,new UnitPrice(spread,quote),null,forecast,
    new(BigInteger.Parse(day.Delivery.Numerator,CultureInfo.InvariantCulture),BigInteger.Parse(day.Delivery.Denominator,CultureInfo.InvariantCulture)),
    FloatingSide.DeliveryIndexImplementation){DeliveryCompletionAt=day.End});
  }
  return new(Guid.NewGuid(),quote,physical.UnitQuantity,periods.ToImmutable(),FloatingSide.DeliveryIndexImplementation,calendars,gasDeliveryClock:physical.Quantities.Clock);
 }
 internal static bool Covers(MarketContract contract,DateOnly day,bool monthly)=>contract.Kind==ContractKind.Forward&&
  (monthly?contract.DeliveryFrom.Day==1&&contract.DeliveryFrom<=day&&contract.DeliveryFrom.AddMonths(1)==contract.DeliveryTo&&contract.DeliveryTo>day:
   contract.DeliveryFrom==day&&contract.DeliveryTo==day.AddDays(1));
 internal static IndexFixing Fixing(PreviewData data,ReferenceKey index,QuoteSpecification quote,MarketContract contract,string code)
 {
  ImmutableArray<IndexFixing> fixings;
  try{fixings=PriceSettlementInputs.Fixings(data,PriceSettlementSelection.CurveTarget(index),quote,[contract]);}
  catch(MarketDataException error)when(error.Code is "index_fixing_convention_missing" or "index_fixing_definition")
  {throw new MarketDataException(code,"Each payment day requires one exact qualified index fixing definition.");}
  if(fixings.Length!=1||!data.Definitions["series"].Any(row=>row.Id("id")==fixings[0].Series.Id&&row.OptionalId("contract_id")==contract.Key.Id))
   throw new MarketDataException(code,"Each payment day requires one exact qualified index fixing definition on its projection contract.");
  return fixings[0];
 }
 internal static MarketDataException Terms()=>new("gas_index_terms","Select point publications or a named daily or monthly index, an explicit spread and a next-month UTC payment day from 1 through 28.");
}
