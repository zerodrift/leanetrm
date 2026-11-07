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
        Equal(undiscounted.OutstandingSettlement,"-10855");Equal(undiscounted.RemainingDeliveryMtm,"0");Equal(undiscounted.Total,"-10855");
        Equal(demo.Headline(Demo.Close,paid,Demo.Undiscounted),"-10855");
        static bool Cash(PhysicalLifecycleRow r)=>r.Slot>=Demo.Months;
        Require(undiscounted.Rows.Count(Cash)==122&&undiscounted.Rows.Count(r=>!Cash(r))==122,"Every gas day keeps one physical and one cash row.");
        var cash=undiscounted.Rows.Where(Cash).ToDictionary(r=>r.DeliveryFrom);
        DeliveryState Delivery(int month,int day)=>cash[new(2026,month,day)].Delivery;
        SettlementState Settled(int month,int day)=>cash[new(2026,month,day)].Settlement;
        Require(Days(9).All(d=>cash[d].Delivery==DeliveryState.Ended&&cash[d].Settlement==SettlementState.Paid&&cash[d].Outstanding.ToExactString()=="0"&&
            cash[d].Paid.ToExactString()=="-290"),"September is paid and leaves both measures.");
        Require(Days(10).Concat(Days(11).Take(5)).All(d=>cash[d].Delivery==DeliveryState.Ended&&cash[d].Settlement==SettlementState.AccruedUnpaid&&
            cash[d].QuantityBasis==PhysicalLifecycle.EstimatedQuantity),"October and Nov 1-5 accrue on the contract estimate and stay unpaid.");
        Require(Delivery(11,6)==DeliveryState.InProgress&&Settled(11,6)==SettlementState.Remaining,"The Nov 6 gas day is in progress at the close.");
        Require(Days(11).Skip(6).Concat(Days(12)).All(d=>cash[d].Delivery==DeliveryState.NotStarted&&cash[d].Settlement==SettlementState.Remaining&&
            cash[d].Accrued.ToExactString()=="0"&&cash[d].QuantityBasis==PhysicalLifecycle.ContractQuantity),"Nov 7 onward has not started.");
        Equal(Sum(Days(11).Take(5).Select(d=>cash[d].OutstandingPv)),"-1555");
        var november=demo.Evaluate(11,Demo.Close);
        // The fixed 0.00 spread adds its own fixed price fact to each day's index state.
        Require(november.Contributions.Skip(6).Take(3).All(c=>c.Amount.States[0].Prices==(SchedulePriceFacts.PublishedFixing|SchedulePriceFacts.Fixed))&&
            november.Contributions[9].Amount.States[0].Prices==(SchedulePriceFacts.Projected|SchedulePriceFacts.Fixed),"Nov 7-9 carry Friday's published fixing; Nov 10 is forecast.");
        var physical=undiscounted.Rows.Where(r=>!Cash(r)).ToDictionary(r=>r.DeliveryFrom);
        Require(physical.Values.All(r=>r.QuantityBasis==(r.Delivery==DeliveryState.Ended?PhysicalLifecycle.EstimatedQuantity:PhysicalLifecycle.ContractQuantity))&&
            physical.Values.Count(r=>r.Delivery==DeliveryState.Ended)==66&&physical.Values.Where(r=>r.Delivery==DeliveryState.Ended).All(r=>r.RemainingPv.ToExactString()=="0"),
            "The 66 ended gas days leave the physical measure, labelled as contract estimates pending allocation.");
        // Undiscounted remaining legs: Nov 6 318 + Nov 7-9 966 + Nov 10-30 6,809 + December 10,912.
        Equal(Sum(physical.Values.Select(r=>r.RemainingPv)),"19005");Equal(Sum(cash.Values.Select(r=>r.RemainingPv)),"-19005");
        Require(demo.Physical[0].GenerateSchedule().Program.Document.Instructions.Any(i=>i.Output.Parameters.GetValueOrDefault("lifecycle")=="future_gas_delivery_v1")&&
            demo.Instrument.Members.All(m=>m.Program.Document.Instructions.All(i=>i.Output.Operation!="output_guard"||
                i.Output.Parameters["lifecycle"] is ScheduleLifecycles.WholeGasDayDelivery or ScheduleLifecycles.AccruedPayment)),
            "Valuation reads the retained started-delivery programs, never the Side's own future-lifecycle program.");

        var dated=new DatedFactors();
        var discounted=demo.Value(Demo.Close,paid,dated);
        Equal(discounted.OutstandingSettlement,"-10843.9431");
        Equal(demo.Headline(Demo.Close,paid,new DatedFactors()),discounted.Total.ToExactString());

        // -4,000 against October: thirteen whole days and 100 of Oct 14.
        var october=paid.AddRange(Days(10).Take(14).Select((d,n)=>demo.Payment(Demo.Months+1,n,Fixture.Utc("2026-11-05T00:00:00Z"),Demo.Close,n<13?"-300":"-100")));
        var part=demo.Value(Demo.Close,october,new DatedFactors());
        Equal(part.OutstandingSettlement,"-6845.9031");
        var partRows=part.Rows.Where(Cash).ToDictionary(r=>r.DeliveryFrom);
        Require(Days(10).Take(13).All(d=>partRows[d].Settlement==SettlementState.Paid)&&partRows[new(2026,10,14)].Settlement==SettlementState.PartPaid&&
            partRows[new(2026,10,14)].Outstanding.ToExactString()=="-200"&&Days(10).Skip(14).All(d=>partRows[d].Settlement==SettlementState.AccruedUnpaid),
            "A partial payment pays whole days and leaves the rest of its allocation outstanding.");

        var hidden=demo.Value(Demo.Close,demo.SeptemberPaid(Demo.Close.AddHours(1)),Demo.Undiscounted);
        Equal(hidden.OutstandingSettlement,"-19555");
        Require(hidden.Rows.Where(Cash).Where(r=>r.DeliveryFrom.Month==9).All(r=>r.Settlement==SettlementState.AccruedUnpaid&&r.OutstandingPv==r.Outstanding),
            "A payment known after the cutoff is invisible: September stays due at nominal.");

        var corrected=demo.Value(Demo.Close,paid.Add(demo.Payment(Demo.Months+1,0,Fixture.Utc("2026-11-05T00:00:00Z"),Demo.Close,"-300")),Demo.Undiscounted,
            revised:new(new(2026,10,1),"2.90"));
        var first=corrected.Rows.Single(r=>Cash(r)&&r.DeliveryFrom==new DateOnly(2026,10,1));
        Require(first.Settlement==SettlementState.Overpaid&&first.Accrued.ToExactString()=="-290"&&first.Outstanding.ToExactString()=="10"&&
            first.OutstandingPv.ToExactString()=="10","A lower corrected fixing after a full payment shows the overpayment instead of refusing.");

        var later=Fixture.Utc("2026-11-11T21:00:00Z");var watched=new DatedFactors();
        var due=demo.Value(later,demo.SeptemberPaid(Demo.Close),watched);
        var octoberDue=due.Rows.Where(r=>Cash(r)&&r.DeliveryFrom.Month==10).ToArray();
        Equal(Sum(octoberDue.Select(r=>r.OutstandingPv)),"-9300");
        Require(octoberDue.All(r=>r.OutstandingPv==r.Outstanding)&&watched.Requested.Count>0&&
            watched.Requested.All(k=>k.PaymentAt!=Fixture.Utc("2026-11-10T00:00:00Z")),"Due unpaid October cash is nominal and reads no discount factor.");

        var shocked=demo.Value(Demo.Close,paid,Demo.Undiscounted,shift:"1");
        Equal(shocked.RemainingDeliveryMtm,"0");Equal(shocked.OutstandingSettlement,"-10855");
        var moved=undiscounted.Rows.Zip(shocked.Rows).Where(p=>p.First!=p.Second).Select(p=>(Base:p.First,Shock:p.Second)).ToArray();
        Require(moved.Length==42&&moved.All(p=>p.Base.DeliveryFrom>=new DateOnly(2026,11,10)&&p.Base.DeliveryFrom.Month==11&&p.Base.Accrued==p.Shock.Accrued&&p.Base.Outstanding==p.Shock.Outstanding),
            "A November daily shock moves only the forecast Nov 10-30 rows of both legs and no accrued row.");
        Equal(Sum(moved.Where(p=>!Cash(p.Base)).Select(p=>p.Shock.RemainingPv.Subtract(p.Base.RemainingPv))),"2100");
        Equal(Sum(moved.Where(p=>Cash(p.Base)).Select(p=>p.Shock.RemainingPv.Subtract(p.Base.RemainingPv))),"-2100");

        // Fixed 3.10 for November, paid Dec 10: 5 of 30 days have ended.
        var fixedValue=demo.FixedNovember(Demo.Close);
        var fixedRow=fixedValue.Rows.Single(r=>r.Slot==1);
        Require(fixedRow.Delivery==DeliveryState.InProgress&&fixedRow.Settlement==SettlementState.AccruedUnpaid&&fixedRow.QuantityBasis==PhysicalLifecycle.EstimatedQuantity,
            "A fixed whole-month row accrues its ended days while the month is in progress.");
        Equal(fixedRow.Accrued,"-1550");Equal(fixedRow.RemainingPv,"-7750");
        Equal(fixedValue.OutstandingSettlement,"-1550");Equal(fixedValue.RemainingDeliveryMtm,"343");

        // A retained program whose first November day carries twice the contract quantity cannot accrue a month pro rata.
        Reject("physical_lifecycle_quantity",()=>demo.FixedNovember(Demo.Close,d=>new(d.SideId,d.Family,d.EconomicIdentity,d.PricingBindingIdentity,d.SchemaVersion,d.GeneratorVersion,
            d.Rows.Select(r=>r.Key=="gas/0/quantity"?r with{Parameters=r.Parameters.SetItem("numerator","2")}:r).ToArray(),d.Edges,d.Bindings,d.Roots,d.Ports,d.Grids)));

        var before=Fixture.Utc("2026-08-28T21:00:00Z");
        var opening=demo.Value(before,[],Demo.Undiscounted);
        Equal(opening.OutstandingSettlement,"0");Equal(opening.Total,demo.Headline(before,null,Demo.Undiscounted));
        Reject("gas_delivery_lifecycle",()=>new Demo(ScheduleLifecycleSelection.FutureDelivery).Headline(Demo.Close,null,Demo.Undiscounted));

        Reject("payment_state",()=>demo.State([demo.Payment(Demo.Months,0,Demo.Close,Demo.Close,"-290") with{Id=Guid.Empty}]));
        var twice=demo.Payment(Demo.Months,0,Demo.Close,Demo.Close,"-145");
        Reject("payment_state",()=>demo.State([twice,twice with{Occurrence=1}]));
        Reject("payment_state",()=>new PhysicalCashState(demo.Trade.Id,demo.Binding,demo.Held,[],"explicit_paid_cash_nominal_due_v1"));
        Reject("payment_state",()=>demo.Value(Demo.Close,[demo.Payment(Demo.Months+3,0,Demo.Close,Demo.Close,"-352")],Demo.Undiscounted));
        Reject("payment_state",()=>demo.Value(Demo.Close,[demo.Payment(0,0,Demo.Close,Demo.Close,"-290")],Demo.Undiscounted));
        Reject("settlement_conservation",()=>demo.Value(Demo.Close,[demo.Payment(Demo.Months,0,Demo.Close,Demo.Close,"290")],Demo.Undiscounted));
        Reject("physical_lifecycle_unsupported",demo.Crude);
        Console.WriteLine("PASS physical lifecycle: accrued, paid and outstanding cash with remaining delivery MTM, discounted until due, frozen under shocks");
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
        private readonly ImmutableArray<GasValuationAssignment> assignments;
        private readonly CurveModel model;
        private readonly ImmutableArray<FloatingSide> payments;
        internal ImmutableArray<PhysicalGasSide> Physical { get; }
        internal RetainedGasInstrument Instrument { get; }
        internal Trade Trade { get; }
        internal Guid Binding { get; }=Guid.NewGuid();
        internal Quantity Held { get; }

        // Prices per MMBtu chosen to reproduce the table: Nov 10-30 sum to 68.09 and December is 3.52.
        private static string Price(DateOnly day)=>day.Month switch{9=>"2.9",10=>"3",12=>"3.52",_=>day.Day switch{<=5=>"3.11",6=>"3.18",<=9=>"3.22",30=>"3.29",_=>"3.24"}};
        // Synthetic publisher: one final fixing at 12:00 Central on the prior weekday; Friday's prices Saturday to Monday.
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
            days=[..dates.Select((d,n)=>new MarketContract(f.K(100+n),ContractKind.Forward,d,d.AddDays(1),Published(d),null))];
            var series=days.Select((d,n)=>new MarketSeries(f.S(100+n),d.Key,quote.Key,null)).ToImmutableArray();
            curve=f.Direct("synthetic_daily_gas",days[0].Key,quote.Key,SettlementKind.Index) with{Constituents=[..days.Select(d=>d.Key)]};
            model=new(f.Revision,f.Reference,[f.Quantities[0]],[quote],days,[curve],[],[],series);
            fixings=[..days.Select((d,n)=>new IndexFixing(f.R("index"),quote.Key,d.DeliveryFrom,d.DeliveryTo,Published(d.DeliveryFrom),series[n].Key,f.R("synthetic_publisher"),IndexFixing.SupportedImplementation))];
            var settlement=PriceSettlementSelection.CurveTarget(f.R("index"));
            var location=new GasDeliveryLocation(f.R("pipeline"),f.R("zone"),f.R("henry_hub"));
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

        internal CashOccurrencePayment Payment(int slot,int occurrence,DateTimeOffset paidAt,DateTimeOffset knownAt,string amount)=>
            new(Guid.NewGuid(),slot,occurrence,paidAt,knownAt,ExactMoney.Parse(amount,quote.Currency));
        internal ImmutableArray<CashOccurrencePayment> SeptemberPaid(DateTimeOffset knownAt)=>
            [..Enumerable.Range(0,30).Select(n=>Payment(Months,n,Fixture.Utc("2026-10-10T00:00:00Z"),knownAt,"-290"))];
        internal PhysicalCashState State(ImmutableArray<CashOccurrencePayment> paid)=>new(Trade.Id,Binding,Held,paid,PhysicalCashState.Policy);

        // Fixings visible at the instant and marks for every day not yet fixed; the knowledge cutoff is the close.
        private MarketSnapshot Snapshot(DateTimeOffset at,(DateOnly Day,string Price)? revised)
        {
            SelectedObservation Observation(int n,InputUse use,ObservationRole role,DateTimeOffset observed,string value)=>new(new(fixings[n].Series,use),
                new(new(f.Revision.CatalogId,Guid.NewGuid()),fixings[n].Series,use==InputUse.FrozenFact?f.R("synthetic_publisher"):f.R("source"),role,PublicationState.Final,observed,ExactDecimal.Parse(value)));
            var selections=Enumerable.Range(0,days.Length).Select(n=>fixings[n].FixingAt<=at?
                Observation(n,InputUse.FrozenFact,ObservationRole.IndexFixing,fixings[n].FixingAt,revised?.Day==days[n].DeliveryFrom?revised.Value.Price:Price(days[n].DeliveryFrom)):
                Observation(n,InputUse.Variable,ObservationRole.TraderMark,at,Price(days[n].DeliveryFrom)));
            return new MarketSnapshot(Guid.NewGuid(),f.Revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[..selections]).WithKnowledge(Guid.NewGuid(),at);
        }

        private (PreparedConnectedPortfolioRisk Prepared,ValuationPortfolio Portfolio) Prepare(Trade trade,DateTimeOffset at,IBindingValuationState? state,ISideDiscounting discount,(DateOnly,string)? revised=null)
        {
            var snapshot=Snapshot(at,revised);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discount)
                {BindingStates=state is null?ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty:ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(state.TradeId,state.BindingId),state)};
            var portfolio=ValuationPortfolio.FromTrades([trade],setup);
            var dependencies=portfolio.Declare(snapshot).SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
            var market=model.Build(snapshot,dependencies);
            var exported=model.Compile(snapshot,dependencies,false,market.Graph.Plan.Context);
            var bindings=dependencies.ToImmutableDictionary(r=>r,r=>exported.Points[(r,InputUse.Variable)]);
            return (RiskEngine.PrepareConnected(ConnectedValuation.Prepare(market.Graph,market.Snapshot),bindings,[portfolio],market),portfolio);
        }
        internal PhysicalLifecycleValue Value(DateTimeOffset at,ImmutableArray<CashOccurrencePayment> paid,ISideDiscounting discount,(DateOnly,string)? revised=null,string? shift=null)
            =>Lifecycle(Trade,Instrument,State(paid),at,discount,revised,shift);
        // A shock is a +shift scenario on every forecast November daily mark over the unshocked base.
        private PhysicalLifecycleValue Lifecycle(Trade trade,RetainedGasInstrument instrument,PhysicalCashState state,DateTimeOffset at,ISideDiscounting discount,(DateOnly,string)? revised,string? shift)
        {
            var (prepared,portfolio)=Prepare(trade,at,state,discount,revised);
            if(shift is not null)prepared=RiskEngine.WithShifts(prepared,[..days.Where((d,n)=>d.DeliveryFrom.Month==11&&fixings[n].FixingAt>at).Select(d=>
                new CurveShift(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(curve.Key,d.Key),PriceUnit.FromQuote(quote),ExactDecimal.Parse(shift),CurveShift.ParentAdditive))]);
            var context=new UnitValuationContext(prepared.Market.Snapshot,portfolio.Setup,prepared.Market).For(prepared.Contracts[0][0],null,state);
            return instrument.EvaluateLifecycle(context);
        }
        // The holding's headline is the ordinary unit value scaled by the booked quantity.
        internal string Headline(DateTimeOffset at,ImmutableArray<CashOccurrencePayment>? paid,ISideDiscounting discount)
        {
            var (prepared,_)=Prepare(Trade,at,paid is {} p?State(p):null,discount);
            return RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value.ToExactString();
        }
        // The retained November payment program evaluated through the same sources the instrument uses.
        internal BookedSideEvaluation Evaluate(int month,DateTimeOffset at)
        {
            var snapshot=Snapshot(at,null);var market=model.Build(snapshot,Instrument.Declare(new(snapshot,new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,Undiscounted))).Dependencies);
            var sources=new ScheduleMarketSources(market.Graph.Plan.Context,market,null,null,IntervalKnowledgePolicy.ResolvedCutoff,PendingActualsPolicy.RequireActuals,Instrument.PriceLookups,[],assignments);
            var member=Instrument.Members[Months+month-9];
            return sources.Evaluate(member.Program,member.SideId,market,Undiscounted);
        }
        internal PhysicalLifecycleValue FixedNovember(DateTimeOffset at,Func<ScheduleDefinition,ScheduleDefinition>? physical=null)
        {
            var november=Physical[2];var quantity=november.GenerateSchedule().Quantities.TotalDelivery;
            var price=new FixedSide(Guid.NewGuid(),new FlatPrice(new UnitPrice(ExactDecimal.Parse("3.10"),quote)),quote,november.UnitQuantity,
                [new(november.Quantities.From,november.Quantities.To,Fixture.Utc("2026-12-10T00:00:00Z"),new(System.Numerics.BigInteger.Parse(quantity.Numerator),System.Numerics.BigInteger.Parse(quantity.Denominator)),null)]);
            BookedSideProgram Book(Side side)=>BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side,default,ScheduleLifecycleSelection.StartedPhysicalDelivery));
            var delivery=ScheduleDefinitionCompiler.Generate(november,default,ScheduleLifecycleSelection.StartedPhysicalDelivery);
            var instrument=new RetainedGasInstrument(Guid.NewGuid(),[new(november.Id,ExactDecimal.Parse("1"),ExactRatio.One,BookedSideProgram.Compile(physical?.Invoke(delivery)??delivery)),
                new(price.Id,ExactDecimal.Parse("-1"),ExactRatio.One,Book(price))],november.Assignments);
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
