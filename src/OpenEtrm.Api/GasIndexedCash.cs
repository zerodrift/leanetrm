using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasIndexedCashRequest([property:JsonRequired]string Index,[property:JsonRequired]string ObservationConvention,
 [property:JsonRequired]string PaymentConvention,[property:JsonRequired]int PaymentDay,[property:JsonRequired]string Spread,[property:JsonRequired]CustomCurveProjectionInput? ForecastCurve);

// Resolve explicit daily index terms into the existing floating payment Side.
// The physical Side and payment Side retain independent programs and prices.
internal static class GasIndexedCash
{
 internal const string Observation="daily_published_index_v1",Payment="next_month_day_utc_v1";
 internal static FloatingSide Resolve(PreviewData data,PhysicalGasSide physical,GasCashRequest cash)
 {
  var terms=cash.Indexed??throw Invalid();
  if(cash.Role!="indexed_consideration"||cash.Price is not null||cash.TariffCode is not null||cash.PaymentAt is not null||
   terms.ObservationConvention!=Observation||terms.PaymentConvention!=Payment||terms.PaymentDay is <1 or >28||string.IsNullOrWhiteSpace(terms.Index)||string.IsNullOrWhiteSpace(terms.Spread))
   throw Invalid();
  var spread=ExactDecimal.Parse(terms.Spread);var limit=ExactDecimal.Parse("1000000000000");
  if(spread.CompareTo(limit)>=0||spread.CompareTo(-limit)<=0)throw Invalid();
  var quote=physical.Assignments[0].Quote;var index=new ReferenceKey(data.ReferenceRevision,terms.Index);
  if(physical.Assignments.Any(a=>a.Settlement.SelectedIndex!=index))throw Invalid();
  var curve=new CurveKey(data.DefinitionRevision,physical.Assignments[0].Projection.Market.Curve.Code);
  var members=data.Definitions["curve_members"].Where(m=>m.Text("curve_code")==curve.Code).Select(m=>m.Id("contract_id")).ToHashSet();
  var periods=ImmutableArray.CreateBuilder<FloatingSidePeriod>();
  foreach(var day in physical.GenerateSchedule().Quantities.Rows)
  {
   var to=day.OperatingDay.AddDays(1);
   var contracts=data.Contracts.Where(c=>members.Contains(c.Key.Id)&&c.Kind==ContractKind.Forward&&c.DeliveryFrom==day.OperatingDay&&c.DeliveryTo==to).ToArray();
   if(contracts.Length!=1)throw new MarketDataException("gas_index_coverage","Each indexed delivery day requires one exact daily index contract and published-fixing definition.");
   var contract=contracts[0];var settlement=PriceSettlementSelection.CurveTarget(index);
   var fixings=PriceSettlementInputs.Fixings(data,settlement,quote,[contract]);
   if(fixings.Length!=1)throw new MarketDataException("gas_index_coverage","Each indexed delivery day requires one exact published-fixing definition.");
   var nextMonth=new DateOnly(day.OperatingDay.Year,day.OperatingDay.Month,1).AddMonths(1);
   var payment=new DateTimeOffset(nextMonth.AddDays(terms.PaymentDay-1).ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
   if(payment<day.End)throw Invalid();
   periods.Add(new(day.OperatingDay,to,fixings[0].FixingAt,payment,FloatingAveraging.CalendarDayArithmetic,
    [new(new(curve,contract.Key),contract)],fixings,null,new UnitPrice(spread,quote),null,
    terms.ForecastCurve?.ToBinding(),new(BigInteger.Parse(day.Delivery.Numerator,CultureInfo.InvariantCulture),BigInteger.Parse(day.Delivery.Denominator,CultureInfo.InvariantCulture)),FloatingSide.IndexedImplementation));
  }
  return new(Guid.NewGuid(),PriceSettlementSelection.CurveTarget(index),quote,physical.UnitQuantity,periods.ToImmutable());
 }
 static MarketDataException Invalid()=>new("gas_index_terms","Select daily published index pricing, an explicit spread and a next-month UTC payment day from 1 through 28.");
}
