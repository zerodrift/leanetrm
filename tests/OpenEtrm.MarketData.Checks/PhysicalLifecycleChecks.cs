using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Crude;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

// The agreed synthetic demo (docs/superpowers/specs/2026-11-07-in-term-physical-lifecycle-design.md):
// receive 100 per gas day Sep 1-Dec 31 2026, pay the daily index plus 0.00 on day 10 of the next month,
// valued at the Friday Nov 6 close. Expected values are the spec's table, recomputed by hand.
internal static class PhysicalLifecycleChecks
{
    internal static void Run()
    {
        var demo=new Demo();var paid=demo.SeptemberPaid(Demo.Close);
        var undiscounted=demo.Value(Demo.Close,paid,Demo.Undiscounted);
        Equal(undiscounted.RealizedUnsettled,"-10855");Equal(undiscounted.Unrealized,"0");Equal(undiscounted.Total,"-10855");
        Equal(demo.Headline(Demo.Close,paid,Demo.Undiscounted),"-10855");
        static bool Cash(PhysicalLifecycleRow r)=>r.Slot>=Demo.Months;
        Require(undiscounted.Rows.Count(Cash)==122&&undiscounted.Rows.Count(r=>!Cash(r))==122,"Every gas day keeps one physical and one cash row.");
        var cash=undiscounted.Rows.Where(Cash).ToDictionary(r=>r.DeliveryFrom);
        DeliveryState Delivery(int month,int day)=>cash[new(2026,month,day)].Delivery;
        SettlementState? Settled(int month,int day)=>cash[new(2026,month,day)].Settlement;
        Require(Days(9).All(d=>cash[d].Delivery==DeliveryState.Ended&&cash[d].Settlement==SettlementState.Paid&&cash[d].Outstanding.ToExactString()=="0"&&
            cash[d].Paid.ToExactString()=="-290"),"September is paid and leaves both measures.");
        Require(Days(10).Concat(Days(11).Take(5)).All(d=>cash[d].Delivery==DeliveryState.Ended&&cash[d].Settlement==SettlementState.AccruedUnpaid&&
            cash[d].QuantityBasis==PhysicalLifecycle.EstimatedQuantity),"October and Nov 1-5 accrue on the contract estimate and stay unpaid.");
        Require(Delivery(11,6)==DeliveryState.InProgress&&Settled(11,6)==SettlementState.Remaining,"The Nov 6 gas day is in progress at the close.");
        Require(Days(11).Skip(6).Concat(Days(12)).All(d=>cash[d].Delivery==DeliveryState.NotStarted&&cash[d].Settlement==SettlementState.Remaining&&
            cash[d].Accrued.ToExactString()=="0"&&cash[d].QuantityBasis==PhysicalLifecycle.ContractQuantity),"Nov 7 onward has not started.");
        Equal(Sum(Days(11).Take(5).Select(d=>cash[d].RealizedUnsettled)),"-1555");
        // Expected cash is undiscounted, by due date, and never added: Sep is paid; Nov 10 Oct; Dec 10 Nov 1-5 accrued plus Nov 6-30; Jan 10 December.
        Require(undiscounted.ExpectedCash.Select(x=>(x.DueAt.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),x.Amount.ToExactString())).SequenceEqual(
            [("2026-11-10","-9300"),("2026-12-10","-9648"),("2027-01-10","-10912")]),"Expected cash groups the unpaid amounts by due date.");
        Require(cash[new(2026,9,1)].Measures.IsEmpty&&cash[new(2026,9,1)].PaidAt==Fixture.Utc("2026-10-10T00:00:00Z")&&
            cash[new(2026,10,1)].Measures.SequenceEqual([LifecycleMeasure.RealizedUnsettled])&&cash[new(2026,11,7)].Measures.SequenceEqual([LifecycleMeasure.Unrealized]),
            "Paid days are history only; accrued days are realized unsettled; days to come are unrealized.");
        var november=demo.Evaluate(11,Demo.Close);
        // The fixed 0.00 spread adds its own fixed price fact to each day's index state.
        Require(november.Contributions.Skip(6).Take(3).All(c=>c.Amount.States[0].Prices==(SchedulePriceFacts.PublishedFixing|SchedulePriceFacts.Fixed))&&
            november.Contributions[9].Amount.States[0].Prices==(SchedulePriceFacts.Projected|SchedulePriceFacts.Fixed),"Nov 7-9 carry Friday's published fixing; Nov 10 is forecast.");
        var physical=undiscounted.Rows.Where(r=>!Cash(r)).ToDictionary(r=>r.DeliveryFrom);
        Require(physical.Values.All(r=>r.Settlement is null&&r.QuantityBasis==(r.Delivery==DeliveryState.Ended?PhysicalLifecycle.EstimatedQuantity:PhysicalLifecycle.ContractQuantity))&&
            physical.Values.Count(r=>r.Delivery==DeliveryState.Ended)==66&&physical.Values.Where(r=>r.Delivery==DeliveryState.Ended).All(r=>r.Unrealized.ToExactString()=="0"),
            "The 66 ended gas days leave the physical measure, labelled as contract estimates pending allocation.");
        // Undiscounted remaining legs: Nov 6 318 + Nov 7-9 966 + Nov 10-30 6,809 + December 10,912.
        Equal(Sum(physical.Values.Select(r=>r.Unrealized)),"19005");Equal(Sum(cash.Values.Select(r=>r.Unrealized)),"-19005");
        Require(demo.Physical[0].GenerateSchedule().Program.Document.Instructions.Any(i=>i.Output.Parameters.GetValueOrDefault("lifecycle")=="future_gas_delivery_v1")&&
            demo.Instrument.Members.All(m=>m.Program.Document.Instructions.All(i=>i.Output.Operation!="output_guard"||
                i.Output.Parameters["lifecycle"] is ScheduleLifecycles.WholeGasDayDelivery or ScheduleLifecycles.AccruedPayment)),
            "Valuation reads the retained started-delivery programs, never the Side's own future-lifecycle program.");

        var dated=new DatedFactors();
        var discounted=demo.Value(Demo.Close,paid,dated);
        Equal(discounted.RealizedUnsettled,"-10843.9431");
        Equal(demo.Headline(Demo.Close,paid,new DatedFactors()),discounted.Total.ToExactString());

        // -4,000 allocated across all 31 October days: 30 x -129 and one -130.
        var october=paid.AddRange(Days(10).Select((d,n)=>demo.Payment(Demo.Months+1,n,Fixture.Utc("2026-11-05T00:00:00Z"),Demo.Close,n<30?"-129":"-130")));
        var part=demo.Value(Demo.Close,october,new DatedFactors());
        Equal(part.RealizedUnsettled,"-6845.9031");
        var partRows=part.Rows.Where(Cash).ToDictionary(r=>r.DeliveryFrom);
        Require(Days(10).All(d=>partRows[d].Settlement==SettlementState.PartPaid)&&Days(10).Take(30).All(d=>partRows[d].Outstanding.ToExactString()=="-171")&&
            partRows[new(2026,10,31)].Outstanding.ToExactString()=="-170","A payment allocated across every October day leaves each day part paid.");

        var hidden=demo.Value(Demo.Close,demo.SeptemberPaid(Demo.Close.AddHours(1)),Demo.Undiscounted);
        Equal(hidden.RealizedUnsettled,"-19555");
        Require(hidden.Rows.Where(Cash).Where(r=>r.DeliveryFrom.Month==9).All(r=>r.Settlement==SettlementState.AccruedUnpaid&&r.RealizedUnsettled==r.Outstanding),
            "A payment known after the cutoff is invisible: September stays due at nominal.");

        // A payment whose paid instant follows the valuation is not yet paid there, and is paid at that instant afterwards.
        var octoberPaidLater=demo.Payment(Demo.Months+1,0,Fixture.Utc("2026-11-08T00:00:00Z"),Demo.Close,"-300");
        Require(demo.Value(Demo.Close,[octoberPaidLater],Demo.Undiscounted).Rows.Single(r=>Cash(r)&&r.DeliveryFrom==new DateOnly(2026,10,1)) is {Settlement:SettlementState.AccruedUnpaid,PaidAt:null}&&
            demo.Value(Fixture.Utc("2026-11-09T21:00:00Z"),[octoberPaidLater],Demo.Undiscounted).Rows.Single(r=>Cash(r)&&r.DeliveryFrom==new DateOnly(2026,10,1)) is {Settlement:SettlementState.Paid} paidRow&&
            paidRow.PaidAt==Fixture.Utc("2026-11-08T00:00:00Z"),"A payment is visible from its paid instant.");

        var corrected=demo.Value(Demo.Close,paid.Add(demo.Payment(Demo.Months+1,0,Fixture.Utc("2026-11-05T00:00:00Z"),Demo.Close,"-300")),Demo.Undiscounted,
            revised:new(new(2026,10,1),"2.90"));
        var first=corrected.Rows.Single(r=>Cash(r)&&r.DeliveryFrom==new DateOnly(2026,10,1));
        Require(first.Settlement==SettlementState.Overpaid&&first.Accrued.ToExactString()=="-290"&&first.Outstanding.ToExactString()=="10"&&
            first.RealizedUnsettled.ToExactString()=="10","A lower corrected fixing after a full payment shows the overpayment instead of refusing.");

        var zeroed=demo.Value(Demo.Close,paid,Demo.Undiscounted,revised:new(new(2026,10,2),"0")).Rows.Single(r=>Cash(r)&&r.DeliveryFrom==new DateOnly(2026,10,2));
        Require(zeroed.Settlement==SettlementState.NoAmountDue&&zeroed.Accrued.ToExactString()=="0","An ended day fixed at zero has no amount due, and is not paid.");

        var later=Fixture.Utc("2026-11-11T21:00:00Z");var watched=new DatedFactors();
        var due=demo.Value(later,demo.SeptemberPaid(Demo.Close),watched);
        var octoberDue=due.Rows.Where(r=>Cash(r)&&r.DeliveryFrom.Month==10).ToArray();
        Equal(Sum(octoberDue.Select(r=>r.RealizedUnsettled)),"-9300");
        Require(octoberDue.All(r=>r.RealizedUnsettled==r.Outstanding)&&watched.Requested.Count>0&&
            watched.Requested.All(k=>k.PaymentAt!=Fixture.Utc("2026-11-10T00:00:00Z")),"Due unpaid October cash is nominal and reads no discount factor.");

        var shocked=demo.Value(Demo.Close,paid,Demo.Undiscounted,shift:"1");
        Equal(shocked.Unrealized,"0");Equal(shocked.RealizedUnsettled,"-10855");
        var moved=undiscounted.Rows.Zip(shocked.Rows).Where(p=>p.First!=p.Second).Select(p=>(Base:p.First,Shock:p.Second)).ToArray();
        Require(moved.Length==42&&moved.All(p=>p.Base.DeliveryFrom>=new DateOnly(2026,11,10)&&p.Base.DeliveryFrom.Month==11&&p.Base.Accrued==p.Shock.Accrued&&p.Base.Outstanding==p.Shock.Outstanding),
            "A November daily shock moves only the forecast Nov 10-30 rows of both legs and no accrued row.");
        Equal(Sum(moved.Where(p=>!Cash(p.Base)).Select(p=>p.Shock.Unrealized.Subtract(p.Base.Unrealized))),"2100");
        Equal(Sum(moved.Where(p=>Cash(p.Base)).Select(p=>p.Shock.Unrealized.Subtract(p.Base.Unrealized))),"-2100");

        // Owner decision (D359): the physical leg of a fixed gas day not yet delivered reads the location curve's own mark; the payment
        // stays locked at its fixing. Nov 7 marked 3.40 against its 3.22 fixing, 100 per day received against the index: +18.00.
        var nov7=new DateOnly(2026,11,7);var marked=demo.Value(Demo.Close,paid,Demo.Undiscounted,marked:(nov7,"3.4"));
        Equal(marked.Unrealized,"18");Equal(marked.RealizedUnsettled,"-10855");Equal(marked.Total,"-10837");
        Require(marked.Rows.Single(r=>!Cash(r)&&r.DeliveryFrom==nov7).Unrealized.ToExactString()=="340"&&marked.Rows.Single(r=>Cash(r)&&r.DeliveryFrom==nov7).Unrealized.ToExactString()=="-322",
            "Nov 7 delivers at its 3.40 mark and pays its 3.22 fixing.");
        Equal(demo.Headline(Demo.Close,paid,Demo.Undiscounted,(nov7,"3.4")),"-10837");
        var physicalNovember=demo.Evaluate(11,Demo.Close,physical:true,marked:(nov7,"3.4"));
        // The location's fixed 0.00 adjustment adds its own fixed price fact. Nov 6's contract expired as its gas day started, so it
        // delivers at that contract's final settlement (delivery_mark_final_settlement_v1); Nov 7-9 at their live marks.
        Require(physicalNovember.Contributions[5].Amount.States[0].Prices==(SchedulePriceFacts.Actual|SchedulePriceFacts.Fixed)&&
            physicalNovember.Contributions.Skip(6).Take(3).All(c=>c.Amount.States[0].Prices==(SchedulePriceFacts.Projected|SchedulePriceFacts.Fixed)),"Nov 6-9 deliver at the curve, not the published fixing.");
        // Nov 6 settled finally at 3.30 against its 3.18 fixing: +12.00. Without that settlement the in-progress day refuses.
        var nov6=new DateOnly(2026,11,6);
        Equal(demo.Value(Demo.Close,paid,Demo.Undiscounted,marked:(nov6,"3.3")).Unrealized,"12");
        Reject("delivery_mark_final_settlement",()=>demo.Value(Demo.Close,paid,Demo.Undiscounted,marked:(nov6,null)));
        Reject("delivery_mark_final_settlement",()=>demo.Headline(Demo.Close,paid,Demo.Undiscounted,(nov6,null)));
        Require(demo.ModelVersion(Demo.Close,paid)=="physical_whole_day_accrual_v1/delivery_mark_final_settlement_v1/retained_gas_unit_pv_v2"&&
            demo.ModelVersion(Demo.Close,null)=="delivery_mark_final_settlement_v1/retained_gas_unit_pv_v2","The started lifecycle names its delivery mark policy in its model version.");
        // Retained risk carries both components per position under the same pinned scenarios: a bump to Nov 7's mark moves
        // unrealized alone, and the components sum to the position's value in base, up and down.
        var risk=demo.Risk(Demo.Close,paid,nov7);
        var components=risk.Factors.Single().Positions.Single().Components;var basePosition=risk.BasePositions.Single();
        Require(components.Select(c=>(c.Name,c.Base.ToExactString(),c.Up.ToExactString(),c.Down.ToExactString())).SequenceEqual(
            [(PhysicalLifecycle.UnrealizedComponent,"0","100","-100"),(PhysicalLifecycle.RealizedUnsettledComponent,"-10855","-10855","-10855")])&&
            risk.Factors.Single().Positions.Single().Values is {} v&&v.Up.ToExactString()=="-10755"&&v.Down.ToExactString()=="-10955"&&
            basePosition.Components.Select(c=>c.Value.ToExactString()).SequenceEqual(["0","-10855"]),"Risk positions carry unrealized and realized unsettled for base and each scenario.");
        // A shock to Nov 7's mark moves its physical day alone; the fixed payment does not move.
        var markShocked=demo.Value(Demo.Close,paid,Demo.Undiscounted,shift:"1",marked:(nov7,"3.4"),shocked:d=>d==nov7);
        Equal(markShocked.Unrealized,"118");
        Require(marked.Rows.Zip(markShocked.Rows).Count(p=>p.First!=p.Second)==1,"Only the physical Nov 7 row responds to its mark.");
        // A fixed, undelivered day with no curve mark refuses; the fixing is never a fallback.
        Reject("missing_observation",()=>demo.Value(Demo.Close,paid,Demo.Undiscounted,marked:(nov7,null)));
        Reject("missing_observation",()=>demo.Headline(Demo.Close,paid,Demo.Undiscounted,(nov7,null)));
        // An ended day needs no mark: Nov 5 ended before the close.
        Equal(demo.Value(Demo.Close,paid,Demo.Undiscounted,marked:(new(2026,11,5),null)).Total,"-10855");

        // Fixed 3.10 for November, paid Dec 10: 5 of 30 days have ended.
        var fixedValue=demo.FixedNovember(Demo.Close);
        var fixedRow=fixedValue.Rows.Single(r=>r.Slot==1);
        Require(fixedRow.Delivery==DeliveryState.InProgress&&fixedRow.Settlement==SettlementState.AccruedUnpaid&&fixedRow.QuantityBasis==PhysicalLifecycle.EstimatedQuantity,
            "A fixed whole-month row accrues its ended days while the month is in progress.");
        Equal(fixedRow.Accrued,"-1550");Equal(fixedRow.Unrealized,"-7750");
        Equal(fixedValue.RealizedUnsettled,"-1550");Equal(fixedValue.Unrealized,"343");

        // One fixed 3.00 row over Oct 1-Dec 1 across the October and November members: 36 of 61 days ended.
        var spanning=demo.Term(Demo.Close,[demo.Physical[1],demo.Physical[2]],"3");
        Equal(spanning.Rows.Single(r=>r.Slot==2).Accrued,"-10800");Equal(spanning.RealizedUnsettled,"-10800");
        // A November calendar average with Nov 1-9 published and the rest forecast cannot accrue its ended days.
        Reject("side_due_leg_unresolved",()=>demo.Term(Demo.Close,[demo.Physical[2]],null));
        // A retained program whose first November day carries twice the contract quantity cannot accrue a month pro rata.
        Reject("physical_lifecycle_quantity",()=>demo.FixedNovember(Demo.Close,d=>new(d.SideId,d.Family,d.EconomicIdentity,d.PricingBindingIdentity,d.SchemaVersion,d.GeneratorVersion,
            d.Rows.Select(r=>r.Key=="gas/0/quantity"?r with{Parameters=r.Parameters.SetItem("numerator","2")}:r).ToArray(),d.Edges,d.Bindings,d.Roots,d.Ports,d.Grids)));

        // October also delivered on a clock an hour later: the cash days are defined once both clocks agree a day has ended.
        var shifted=demo.Shifted(10,60);
        Equal(demo.Term(Demo.Close,[demo.Physical[1],shifted],"3").RealizedUnsettled,"-9300");
        Reject("physical_lifecycle_unsupported",()=>demo.Term(Fixture.Utc("2026-10-02T15:30:00Z"),[demo.Physical[1],shifted],"3"));
        // Review Focus 5: an old future-lifecycle holding beside the started one refuses the portfolio; it is never omitted.
        // Each position alone: the started one values, the old one refuses; held together the total refuses.
        Equal(demo.MixedPortfolio(Demo.Close,paid,started:true,old:false),"-10855");
        Reject("gas_delivery_lifecycle",()=>demo.MixedPortfolio(Demo.Close,paid,started:false,old:true));
        Reject("gas_delivery_lifecycle",()=>demo.MixedPortfolio(Demo.Close,paid,started:true,old:true));

        var before=Fixture.Utc("2026-08-28T21:00:00Z");
        var opening=demo.Value(before,[],Demo.Undiscounted);
        Equal(opening.RealizedUnsettled,"0");Equal(opening.Total,demo.Headline(before,null,Demo.Undiscounted));
        // Once its first gas day has started, a started-lifecycle program valued with no lifecycle state would drop its
        // ended days and due cash, so it refuses instead.
        Reject("gas_delivery_lifecycle",()=>demo.Headline(Fixture.Utc("2026-09-01T15:00:00Z"),null,Demo.Undiscounted));
        Equal(demo.Headline(Fixture.Utc("2026-09-01T14:59:59Z"),null,Demo.Undiscounted),demo.Value(Fixture.Utc("2026-09-01T14:59:59Z"),[],Demo.Undiscounted).Total.ToExactString());
        Reject("gas_delivery_lifecycle",()=>new Demo(ScheduleLifecycleSelection.FutureDelivery).Headline(Demo.Close,null,Demo.Undiscounted));

        Reject("payment_state",()=>demo.State([demo.Payment(Demo.Months,0,Demo.Close,Demo.Close,"-290") with{Id=Guid.Empty}]));
        var twice=demo.Payment(Demo.Months,0,Demo.Close,Demo.Close,"-145");
        // A payment and an occurrence identify one allocation; one payment is one slot, payment and knowledge instant.
        Reject("payment_state",()=>demo.State([twice,twice]));
        Reject("payment_state",()=>demo.State([twice,twice with{Occurrence=1,PaidAt=Demo.Close.AddDays(-1)}]));
        var split=demo.Value(Demo.Close,[twice,twice with{Occurrence=1}],Demo.Undiscounted).Rows.Where(r=>Cash(r)&&r.DeliveryFrom.Month==9&&r.DeliveryFrom.Day<=2).ToArray();
        Require(split.All(r=>r.Settlement==SettlementState.PartPaid&&r.PaidAt==Demo.Close),"One payment may allocate to several occurrences.");
        Reject("payment_state",()=>new PhysicalCashState(demo.Trade.Id,demo.Binding,demo.Held,[],"explicit_paid_cash_nominal_due_v1"));
        Reject("payment_state",()=>demo.Value(Demo.Close,[demo.Payment(Demo.Months+3,0,Demo.Close,Demo.Close,"-352")],Demo.Undiscounted));
        Reject("payment_state",()=>demo.Value(Demo.Close,[demo.Payment(0,0,Demo.Close,Demo.Close,"-290")],Demo.Undiscounted));
        Reject("settlement_conservation",()=>demo.Value(Demo.Close,[demo.Payment(Demo.Months,0,Demo.Close,Demo.Close,"290")],Demo.Undiscounted));
        Reject("physical_lifecycle_unsupported",demo.Crude);
        // Missing wiring is a system defect that fails the read, never one trade's refusal.
        Reject("physical_lifecycle_context",()=>demo.Unstated(Demo.Close));
        Reject("physical_lifecycle_context",()=>demo.Constructed(Demo.Close,null,knowledge:false));
        Require(!PhysicalLifecycle.TradeRefusal("physical_lifecycle_context"),"A missing lifecycle context is not a trade refusal.");
        Equal(demo.Constructed(Demo.Close,null).Total,"-19555");
        // An ended day's cash with no price fact, or a remaining day whose value is not resolved, has no lifecycle amount.
        Reject("side_due_leg_unresolved",()=>demo.Constructed(Demo.Close,(slot,n,c)=>slot==Demo.Months+1&&n==0?c with{Amount=c.Amount with{States=[c.Amount.States[0] with{Prices=SchedulePriceFacts.None}]}}:c));
        Reject("physical_lifecycle_unsupported",()=>demo.Constructed(Demo.Close,(slot,n,c)=>slot==Demo.Months+2&&n==9?c with{PresentValue=c.PresentValue with{Presence=[ScheduleCellPresence.NotDemanded]}}:c));
        Console.WriteLine("PASS physical lifecycle: realized unsettled cash and unrealized remaining delivery at the location mark, discounted until due, frozen under shocks");
    }

    private sealed class Demo
    {
        internal const int Months=4;
        internal static readonly DateTimeOffset Close=Fixture.Utc("2026-11-06T21:00:00Z");
        internal static ISideDiscounting Undiscounted=>new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        private static readonly TimeSpan Central=TimeSpan.FromHours(-6);
        private readonly Fixture f=new();
        private readonly QuoteSpecification quote;
        private readonly Curve curve;
        private readonly ImmutableArray<MarketContract> days;
        private readonly ImmutableArray<IndexFixing> fixings;
        private readonly ImmutableArray<MarketSeries> series;
        private readonly ImmutableArray<GasValuationAssignment> assignments;
        private readonly CurveModel model;
        private readonly ImmutableArray<FloatingSide> payments;
        private readonly GasDeliveryLocation location;
        internal ImmutableArray<PhysicalGasSide> Physical { get; }
        internal RetainedGasInstrument Instrument { get; }
        internal Trade Trade { get; }
        internal Guid Binding { get; }=Guid.NewGuid();
        internal Quantity Held { get; }

        // Prices per MMBtu chosen to reproduce the table: Nov 10-30 sum to 68.09 and December is 3.52.
        private static string Price(DateOnly day)=>day.Month switch{9=>"2.9",10=>"3",12=>"3.52",_=>day.Day switch{<=5=>"3.11",6=>"3.18",<=9=>"3.22",30=>"3.29",_=>"3.24"}};
        // Synthetic publisher: one final fixing at 12:00 Central on the prior weekday; Friday's prices Saturday to Monday.
        private static DateTimeOffset GasDayStart(DateOnly day)=>new DateTimeOffset(day.ToDateTime(new TimeOnly(9,0)),Central).ToUniversalTime();
        private static DateTimeOffset Published(DateOnly day)
        {
            var at=day.AddDays(-1);while(at.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)at=at.AddDays(-1);
            return new DateTimeOffset(at.ToDateTime(new TimeOnly(12,0)),Central).ToUniversalTime();
        }

        internal Demo(ScheduleLifecycleSelection lifecycle=ScheduleLifecycleSelection.StartedPhysicalDelivery)
        {
            quote=new(f.R("synthetic_gas_quote"),QuoteKind.Outright,f.R("currency"),f.Quantities[0].Key,ExactDecimal.Parse("1"),
                ExactDecimal.Parse("0.001"),ExactDecimal.Parse("0.001"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
            var dates=Enumerable.Range(0,122).Select(new DateOnly(2026,9,1).AddDays).ToArray();
            // Each daily curve contract trades until its gas day starts, then settles finally; the index publishes on its own series.
            days=[..dates.Select((d,n)=>new MarketContract(f.K(100+n),ContractKind.Forward,d,d.AddDays(1),GasDayStart(d),null))];
            var index=dates.Select((d,n)=>new MarketContract(f.K(300+n),ContractKind.Forward,d,d.AddDays(1),Published(d),null)).ToArray();
            series=[..days.Select((d,n)=>new MarketSeries(f.S(100+n),d.Key,quote.Key,null))];
            var indexSeries=index.Select((d,n)=>new MarketSeries(f.S(300+n),d.Key,quote.Key,null)).ToArray();
            curve=f.Direct("synthetic_daily_gas",days[0].Key,quote.Key,SettlementKind.Index) with{Constituents=[..days.Select(d=>d.Key)]};
            model=new(f.Revision,f.Reference,[f.Quantities[0]],[quote],[..days,..index],[curve],[],[],[..series,..indexSeries]);
            fixings=[..days.Select((d,n)=>new IndexFixing(f.R("index"),quote.Key,d.DeliveryFrom,d.DeliveryTo,Published(d.DeliveryFrom),indexSeries[n].Key,f.R("synthetic_publisher"),IndexFixing.SupportedImplementation))];
            var settlement=PriceSettlementSelection.CurveTarget(f.R("index"));
            location=new GasDeliveryLocation(f.R("pipeline"),f.R("zone"),f.R("henry_hub"));
            var clock=GasDayClock.Capture(f.R("synthetic_gas_clock"),TimeZoneInfo.CreateCustomTimeZone("synthetic_fixed_minus_six",Central,"synthetic","synthetic"),9*60);
            assignments=[..days.Select((d,n)=>new GasValuationAssignment(f.R("synthetic_day_"+n),location,f.R("gas"),d.DeliveryFrom,d.DeliveryTo,settlement,quote,
                new(new(curve.Key,d.Key),d),fixings[n],new(default,quote)))];
            var months=Enumerable.Range(0,Months).Select(m=>new DateOnly(2026,9+m,1)).ToArray();
            ImmutableArray<int> In(DateOnly month)=>[..Enumerable.Range(0,days.Length).Where(n=>days[n].DeliveryFrom.Month==month.Month)];
            Physical=[..months.Select(m=>new PhysicalGasSide(Guid.NewGuid(),location,f.R("gas"),new(clock,m,m.AddMonths(1),GasQuantityBasis.PerOperatingDay,
                new(ExactDecimal.Parse("1"),quote.Quantity),GasFuelConvention.None,default),[..In(m).Select(n=>assignments[n])]))];
            payments=[..months.Select((m,p)=>new FloatingSide(Guid.NewGuid(),settlement,quote,Physical[p].UnitQuantity,[..In(m).Select(n=>new FloatingSidePeriod(
                days[n].DeliveryFrom,days[n].DeliveryTo,fixings[n].FixingAt,new(m.AddMonths(1).AddDays(9).ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)),
                FloatingAveraging.CalendarDayArithmetic,[new(new(curve.Key,days[n].Key),days[n])],[fixings[n]],null,new UnitPrice(default,quote),null,null,
                ExactRatio.One,FloatingSide.IndexedImplementation))]))];
            BookedSideProgram Book(Side side)=>BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side,default,lifecycle));
            Instrument=new(Guid.NewGuid(),[..Physical.Select(s=>new RetainedGasMember(s.Id,ExactDecimal.Parse("1"),ExactRatio.One,Book(s))),
                ..payments.Select(s=>new RetainedGasMember(s.Id,ExactDecimal.Parse("-1"),ExactRatio.One,Book(s)))],assignments);
            Held=new(ExactDecimal.Parse("100"),Instrument.UnitQuantity.Specification);
            Trade=new(Guid.NewGuid(),Guid.NewGuid(),[new(Binding,Instrument,Held)]);
        }

        // The same month's delivery on a gas-day clock starting `minutes` later.
        internal PhysicalGasSide Shifted(int month,int minutes)
        {
            var side=Physical[month-9];var clock=GasDayClock.Capture(f.R("synthetic_gas_clock_shifted"),TimeZoneInfo.CreateCustomTimeZone("synthetic_fixed_minus_six_shifted",Central,"synthetic","synthetic"),9*60+minutes);
            var q=side.Quantities;
            return new(Guid.NewGuid(),location,f.R("gas"),new(clock,q.From,q.To,q.Basis,q.Receipt,q.FuelConvention,q.FuelFraction),side.Assignments);
        }
        internal CashOccurrencePayment Payment(int slot,int occurrence,DateTimeOffset paidAt,DateTimeOffset knownAt,string amount)=>
            new(Guid.NewGuid(),slot,occurrence,paidAt,knownAt,ExactMoney.Parse(amount,quote.Currency));
        internal ImmutableArray<CashOccurrencePayment> SeptemberPaid(DateTimeOffset knownAt)=>
            [..Enumerable.Range(0,30).Select(n=>Payment(Months,n,Fixture.Utc("2026-10-10T00:00:00Z"),knownAt,"-290"))];
        internal PhysicalCashState State(ImmutableArray<CashOccurrencePayment> paid)=>new(Trade.Id,Binding,Held,paid,PhysicalCashState.Policy);

        // Fixings visible at the instant; each curve contract's live mark, or its final settlement once expired, equal to the
        // fixing unless `marked` sets one day's (a null price leaves that day with neither); the knowledge cutoff is the close.
        private MarketSnapshot Snapshot(DateTimeOffset at,(DateOnly Day,string Price)? revised,(DateOnly Day,string? Price)? marked=null)
        {
            SelectedObservation Observation(SeriesKey key,InputUse use,ObservationRole role,ReferenceKey source,DateTimeOffset observed,string value)=>new(new(key,use),
                new(new(f.Revision.CatalogId,Guid.NewGuid()),key,source,role,PublicationState.Final,observed,ExactDecimal.Parse(value)));
            var selections=Enumerable.Range(0,days.Length).SelectMany(n=>
            {
                var day=days[n].DeliveryFrom;var mark=marked?.Day==day?marked.Value.Price:Price(day);var live=at<days[n].Expiry;
                return (fixings[n].FixingAt<=at?[Observation(fixings[n].Series,InputUse.FrozenFact,ObservationRole.IndexFixing,f.R("synthetic_publisher"),fixings[n].FixingAt,revised?.Day==day?revised.Value.Price:Price(day))]:Array.Empty<SelectedObservation>())
                    .Concat(mark is null?[]:[live?Observation(series[n].Key,InputUse.Variable,ObservationRole.TraderMark,f.R("source"),at,mark):
                        Observation(series[n].Key,InputUse.FrozenFact,ObservationRole.FinalContractSettlement,f.R("source"),days[n].Expiry,mark)]);
            });
            return new MarketSnapshot(Guid.NewGuid(),f.Revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[..selections]).WithKnowledge(Guid.NewGuid(),at);
        }

        private (PreparedConnectedPortfolioRisk Prepared,ValuationPortfolio Portfolio) Prepare(ImmutableArray<Trade> trades,DateTimeOffset at,IBindingValuationState? state,ISideDiscounting discount,(DateOnly,string)? revised=null,(DateOnly,string?)? marked=null)
        {
            var snapshot=Snapshot(at,revised,marked);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discount)
                {BindingStates=state is null?ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty:ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(state.TradeId,state.BindingId),state)};
            var portfolio=ValuationPortfolio.FromTrades(trades,setup);
            var dependencies=portfolio.Declare(snapshot).SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
            var market=model.Build(snapshot,dependencies);
            var exported=model.Compile(snapshot,dependencies,false,market.Graph.Plan.Context);
            var bindings=dependencies.ToImmutableDictionary(r=>r,r=>exported.Points[(r,InputUse.Variable)]);
            return (RiskEngine.PrepareConnected(ConnectedValuation.Prepare(market.Graph,market.Snapshot),bindings,[portfolio],market),portfolio);
        }
        internal PhysicalLifecycleValue Value(DateTimeOffset at,ImmutableArray<CashOccurrencePayment> paid,ISideDiscounting discount,(DateOnly,string)? revised=null,string? shift=null,
            (DateOnly,string?)? marked=null,Func<DateOnly,bool>? shocked=null)=>Lifecycle(Trade,Instrument,State(paid),at,discount,revised,shift,marked,shocked);
        // A shock is a +shift scenario on the selected daily marks, by default every forecast November mark, over the unshocked base.
        private PhysicalLifecycleValue Lifecycle(Trade trade,RetainedGasInstrument instrument,PhysicalCashState state,DateTimeOffset at,ISideDiscounting discount,(DateOnly,string)? revised,string? shift,
            (DateOnly,string?)? marked=null,Func<DateOnly,bool>? shocked=null)
        {
            var (prepared,portfolio)=Prepare([trade],at,state,discount,revised,marked);
            if(shift is not null)prepared=RiskEngine.WithShifts(prepared,[..days.Where((d,n)=>shocked?.Invoke(d.DeliveryFrom)??d.DeliveryFrom.Month==11&&fixings[n].FixingAt>at).Select(d=>
                new CurveShift(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(curve.Key,d.Key),PriceUnit.FromQuote(quote),ExactDecimal.Parse(shift),CurveShift.ParentAdditive))]);
            var context=new UnitValuationContext(prepared.Market.Snapshot,portfolio.Setup,prepared.Market).For(prepared.Contracts[0][0],null,state);
            return instrument.EvaluateLifecycle(context);
        }
        // Lifecycle valuation of the held binding with no binding state.
        internal void Unstated(DateTimeOffset at)
        {
            var (prepared,portfolio)=Prepare([Trade],at,null,Undiscounted);
            Instrument.EvaluateLifecycle(new UnitValuationContext(prepared.Market.Snapshot,portfolio.Setup,prepared.Market).For(prepared.Contracts[0][0],null,null));
        }
        // The lifecycle of every member's own evaluation, unpaid, after an optional change to contribution n of a slot.
        internal PhysicalLifecycleValue Constructed(DateTimeOffset at,Func<int,int,ScheduleContribution,ScheduleContribution>? change,bool knowledge=true)
        {
            var snapshot=Snapshot(at,null);var market=model.Build(snapshot,Instrument.Declare(new(snapshot,new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,Undiscounted))).Dependencies);
            var sources=new ScheduleMarketSources(market.Graph.Plan.Context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,PendingActualsPolicy.RequireActuals,Instrument.PriceLookups,[],assignments);
            var members=Instrument.Members.Select((m,slot)=>
            {
                var evaluation=sources.Evaluate(m.Program,m.SideId,market,Undiscounted);
                if(change is not null)evaluation=evaluation with{Contributions=[..evaluation.Contributions.Select((c,n)=>change(slot,n,c))]};
                return new LifecycleMember(m.Program,evaluation,slot<Months,v=>v.Scale(m.Direction).Scale(Held.Amount));
            });
            var valued=knowledge?snapshot:new MarketSnapshot(snapshot.Id,f.Revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,snapshot.Selections);
            return PhysicalLifecycle.Evaluate([..members],State([]),valued,quote.Currency);
        }
        // The holding's headline is the ordinary unit value scaled by the booked quantity.
        internal string Headline(DateTimeOffset at,ImmutableArray<CashOccurrencePayment>? paid,ISideDiscounting discount,(DateOnly,string?)? marked=null)
        {
            var (prepared,_)=Prepare([Trade],at,paid is {} p?State(p):null,discount,marked:marked);
            return RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value.ToExactString();
        }
        // The same terms retained with the future-delivery lifecycles, held beside the started trade with no lifecycle state.
        internal string MixedPortfolio(DateTimeOffset at,ImmutableArray<CashOccurrencePayment> paid,bool started,bool old)
        {
            BookedSideProgram Old(Side side)=>BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side,default,ScheduleLifecycleSelection.FutureDelivery));
            var previous=new RetainedGasInstrument(Guid.NewGuid(),[..Physical.Select(s=>new RetainedGasMember(s.Id,ExactDecimal.Parse("1"),ExactRatio.One,Old(s))),
                ..payments.Select(s=>new RetainedGasMember(s.Id,ExactDecimal.Parse("-1"),ExactRatio.One,Old(s)))],assignments);
            var future=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),previous,Held)]);
            var (prepared,_)=Prepare([..started?[Trade]:Array.Empty<Trade>(),..old?[future]:Array.Empty<Trade>()],at,started?State(paid):null,Undiscounted);
            return RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value.ToExactString();
        }
        internal string ModelVersion(DateTimeOffset at,ImmutableArray<CashOccurrencePayment>? paid)
        {
            var snapshot=Snapshot(at,null);var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,Undiscounted);
            return Instrument.Declare(new UnitValuationContext(snapshot,setup).ForCoverage(null,paid is {} p?State(p):null)).ModelVersion;
        }
        // One symmetric +/-1 bump to the selected day's curve mark through the connected risk engine.
        internal ConnectedBookRiskRun Risk(DateTimeOffset at,ImmutableArray<CashOccurrencePayment> paid,DateOnly day)
        {
            var (prepared,_)=Prepare([Trade],at,State(paid),Undiscounted);var n=days.IndexOf(days.Single(d=>d.DeliveryFrom==day));
            return RiskEngine.CalculateExact(prepared,[new ConnectedRiskBump(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(curve.Key,days[n].Key),PriceUnit.FromQuote(quote),
                ExactDecimal.Parse("1"),ConnectedRiskBump.Convention)],RiskEngine.ExactNumericalPolicy).Groups.Single();
        }
        // The retained November payment program evaluated through the same sources the instrument uses.
        internal BookedSideEvaluation Evaluate(int month,DateTimeOffset at,bool physical=false,(DateOnly,string?)? marked=null)
        {
            var snapshot=Snapshot(at,null,marked);var market=model.Build(snapshot,Instrument.Declare(new(snapshot,new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,Undiscounted))).Dependencies);
            var sources=new ScheduleMarketSources(market.Graph.Plan.Context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,PendingActualsPolicy.RequireActuals,Instrument.PriceLookups,[],assignments);
            var member=Instrument.Members[(physical?0:Months)+month-9];
            return sources.Evaluate(member.Program,member.SideId,market,Undiscounted);
        }
        internal PhysicalLifecycleValue FixedNovember(DateTimeOffset at,Func<ScheduleDefinition,ScheduleDefinition>? physical=null)=>Term(at,[Physical[2]],"3.10",physical);
        // One dated cash row over the whole term of the selected physical members, paid Dec 10.
        internal PhysicalLifecycleValue Term(DateTimeOffset at,ImmutableArray<PhysicalGasSide> members,string? fixedPrice,Func<ScheduleDefinition,ScheduleDefinition>? physical=null)
        {
            var from=members[0].Quantities.From;var to=members[^1].Quantities.To;var count=to.DayNumber-from.DayNumber;var payment=Fixture.Utc("2026-12-10T00:00:00Z");
            var term=Enumerable.Range(0,days.Length).Where(n=>days[n].DeliveryFrom>=from&&days[n].DeliveryFrom<to).ToArray();
            Side price=fixedPrice is null?new FloatingSide(Guid.NewGuid(),PriceSettlementSelection.CurveTarget(f.R("index")),quote,members[0].UnitQuantity,
                    [new(from,to,fixings[term[0]].FixingAt,payment,FloatingAveraging.CalendarDayArithmetic,[..term.Select(n=>new SwapProjection(new(curve.Key,days[n].Key),days[n]))],
                        [..term.Select(n=>fixings[n])],null,new UnitPrice(default,quote),null,null,new(count,1),FloatingSide.IndexedImplementation)]):
                new FixedSide(Guid.NewGuid(),new FlatPrice(new UnitPrice(ExactDecimal.Parse(fixedPrice),quote)),quote,members[0].UnitQuantity,[new(from,to,payment,new(count,1),null)]);
            BookedSideProgram Book(Side side)=>BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side,default,ScheduleLifecycleSelection.StartedPhysicalDelivery));
            var instrument=new RetainedGasInstrument(Guid.NewGuid(),[..members.Select(m=>{var d=ScheduleDefinitionCompiler.Generate(m,default,ScheduleLifecycleSelection.StartedPhysicalDelivery);
                    return new RetainedGasMember(m.Id,ExactDecimal.Parse("1"),ExactRatio.One,BookedSideProgram.Compile(physical?.Invoke(d)??d));}),
                new(price.Id,ExactDecimal.Parse("-1"),ExactRatio.One,Book(price))],[..members.SelectMany(m=>m.Assignments)]);
            var binding=Guid.NewGuid();var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(binding,instrument,Held)]);
            return Lifecycle(trade,instrument,new(trade.Id,binding,Held,[],PhysicalCashState.Policy),at,Undiscounted,null,null);
        }
        internal void Crude()
        {
            var (_,_,_,_,cash,crude)=ScheduleProgramDemandChecks.Sides();
            BookedSideProgram Book(Side side)=>BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side));
            var instrument=new RetainedCrudeInstrument(Guid.NewGuid(),crude.Assignment,[new(crude.Id,ExactDecimal.Parse("1"),Book(crude)),new(cash.Id,ExactDecimal.Parse("-1"),Book(cash))]);
            var binding=Guid.NewGuid();var held=new Quantity(ExactDecimal.Parse("1"),instrument.UnitQuantity.Specification);
            var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(binding,instrument,held)]);
            var state=new PhysicalCashState(trade.Id,binding,held,[],PhysicalCashState.Policy);
            ValuationPortfolio.FromTrades([trade],new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,Undiscounted)
                {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),state)});
        }
    }

    // Illustrative payment-date factors; gas-day value dates are left at one. A due date is never requested.
    private sealed class DatedFactors:ISideDiscounting
    {
        internal List<SidePaymentKey> Requested { get; }=[];
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)
        {
            if(payments.Any(p=>p.PaymentAt<=snapshot.ValuationAt))throw new Exception("A due payment requested a discount factor.");
            Requested.AddRange(payments);
            return [..payments.Select(p=>new SidePaymentFactor(p,ExactDecimal.Parse(p.PaymentAt.ToString("yyyy-MM-dd'T'HH:mm",System.Globalization.CultureInfo.InvariantCulture) switch
                {"2026-11-10T00:00"=>"0.99951","2026-12-10T00:00"=>"0.99582","2027-01-10T00:00"=>"0.99202",_=>"1"}),snapshot,DiscountFactorSource.Explicit))];
        }
    }

    private static IEnumerable<DateOnly> Days(int month)=>Enumerable.Range(0,month==9||month==11?30:31).Select(n=>new DateOnly(2026,month,1).AddDays(n));
    private static string Sum(IEnumerable<ExactMoney> values)=>values.Aggregate((a,b)=>a.Add(b)).ToExactString();
    private static void Equal(ExactMoney actual,string expected)=>Equal(actual.ToExactString(),expected);
    private static void Equal(string actual,string expected)
    {if(actual!=expected)throw new Exception($"Expected {expected}, got {actual}.");}
    private static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
