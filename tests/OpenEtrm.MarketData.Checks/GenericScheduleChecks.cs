using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GenericScheduleChecks
{
 [EconomicDefinition("synthetic_schedule_pack_v1")]
 sealed record SchedulePack : SidedInstrument
 {
  public override ReferenceKey Currency {get;}
  public override Quantity UnitQuantity {get;}
  internal SchedulePack(Guid id,ImmutableArray<SideMembership> sides,ReferenceKey currency,Quantity unit):base(id,sides){Currency=currency;UnitQuantity=unit;}
 }
 internal static void Run()
 {
  var f=new CommodityLookupFixture(31);var quote=f.Quote;var unit=new Quantity(ExactDecimal.Parse("1"),quote.Quantity);
  var from=new DateOnly(2041,1,1);var to=from.AddMonths(1);var payment=new DateTimeOffset(2041,2,10,0,0,0,TimeSpan.Zero);
  FixedSide Cash(string price)=>new(Guid.NewGuid(),new FlatPrice(new(ExactDecimal.Parse(price),quote)),quote,unit,[new(from,to,payment,new(31,1),null)]);
  var receive=Cash("4");var pay=Cash("3");
  // This instrument is deliberately absent from application product switches.
  var instrument=new SchedulePack(Guid.NewGuid(),[new(receive,ExactDecimal.Parse("1")),new(pay,ExactDecimal.Parse("-1"))],quote.Currency,unit);
  var snapshot=f.Snapshot.WithKnowledge(Guid.NewGuid(),f.Snapshot.ValuationAt);
  var context=new SideValuationContext(f.Model.Build(snapshot,[]),new ExplicitDiscounting(ExactDecimal.Parse("0.9"),DiscountFactorSource.Explicit));
  var value=instrument.PV(context);
  var manifests=instrument.Sides.Select(s=>new SideScheduleIdentity(s.Side.Id,ScheduleDefinitionCompiler.Generate(s.Side).Manifest)).ToImmutableArray();
  var inspections=value.InspectSchedules(manifests,ExactDecimal.Parse("2"),day=>new(day.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero));
  var first=inspections[0].Read(0,8);var second=inspections[1].Read(0,8);
  if(first.Rows.Single().PresentValue!="223.2"||second.Rows.Single().PresentValue!="-167.4"||second.Rows.Single().Quantity!="-62"||first.Rows[0].PaymentAt!=payment)
   throw new Exception("An unregistered instrument must render exact signed holding values through common Side behavior.");
  var detail=inspections[0].Calculation(first.Rows[0].Key,0,128);
  if(!detail.Rows.Any(r=>r.Operation=="discount"&&r.Value=="0.9")||!detail.Rows.Any(r=>r.Operation=="amount"&&r.Value=="124"))
   throw new Exception("The common detail must project evaluated cash and discount evidence without repricing.");
  var repeated=new SchedulePack(Guid.NewGuid(),[new(receive,ExactDecimal.Parse("1")),new(receive,ExactDecimal.Parse("-1"))],quote.Currency,unit);
  var same=new SideScheduleIdentity(receive.Id,ScheduleDefinitionCompiler.Generate(receive).Manifest);
  var repeatedRows=repeated.PV(context).InspectSchedules([same,same],ExactDecimal.Parse("2"),day=>new(day.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero));
  if(repeatedRows.Length!=2||repeatedRows[0].Read(0,1).Rows[0].PresentValue!="223.2"||repeatedRows[1].Read(0,1).Rows[0].PresentValue!="-223.2")throw new Exception("Independent memberships of one reusable Side must not collapse into a dictionary entry.");
  var floating=f.Swap();var baseMarket=f.Model.Build(snapshot,floating.Dependencies);
  var factor=f.Model.QuoteFactors(snapshot).First();var bump=new OpenEtrm.MarketData.Risk.QuoteBump(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("0.01"),OpenEtrm.MarketData.Risk.QuoteBump.Convention);
  var shocked=f.Model.Scenarios(baseMarket,[bump]).Single().Up;
  var shockedValue=floating.PV(new SideValuationContext(shocked,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
  var identities=floating.Sides.Select(s=>new SideScheduleIdentity(s.Side.Id,ScheduleDefinitionCompiler.Generate(s.Side).Manifest)).ToImmutableArray();
  try{shockedValue.InspectSchedules(identities,ExactDecimal.Parse("1"),day=>new(day.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero));throw new Exception("A shocked valuation must not masquerade as base schedule values under the same snapshot/context.");}
  catch(MarketDataException error)when(error.Code=="side_schedule_inspection"){}
  Console.WriteLine("PASS generic schedules: new instrument, base Side rows, exact signed scale, dated values and native calculation evidence without product dispatch");
 }
}
