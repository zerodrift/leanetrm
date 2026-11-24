using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.Shared.Numerics;

internal static class DeliveryQuantityChecks
{
    internal static void Run(){Parts();EntryDays();HeldState();WorkedExample();RetainedRoute();Build();View();}

    static void Parts()
    {
        static SignedRatio Q(int amount) => new(amount, 1);
        static void Same(SignedRatio actual, SignedRatio expected)
        { if(actual != expected) throw new Exception($"Expected exact quantity {expected}; received {actual}."); }
        var band = QuantityPart.Band(Q(0), new(4, 5));
        var above = QuantityPart.Band(new(4, 5), null);
        var shortfall = QuantityPart.Shortfall(Q(1));
        Same(band.Take(Q(9000), Q(10000)), Q(8000));
        Same(band.Take(Q(7000), Q(10000)), Q(7000));
        Same(band.Take(Q(8000), Q(10000)), Q(8000));
        Same(above.Take(Q(9000), Q(10000)), Q(1000));
        Same(above.Take(Q(8000), Q(10000)), Q(0));
        Same(above.Take(Q(7000), Q(10000)), Q(0));
        Same(shortfall.Take(Q(9000), Q(10000)), Q(1000));
        Same(shortfall.Take(Q(10000), Q(10000)), Q(0));
        Same(shortfall.Take(Q(11000), Q(10000)), Q(0));
        Same(band.Take(Q(18000), Q(20000)), Q(16000));
        // 100,000 over 30 days stays 10,000/3, above the 3,000 daily band.
        Same(QuantityPart.Band(Q(0), new(3, 100)).Take(new(10000, 3), Q(100000)), Q(3000));
        Same(QuantityPart.Shortfall(Q(0)).Take(Q(0), Q(10000)), Q(0));
        Reject("quantity_part", () => QuantityPart.Band(new(4, 5), new(4, 5)));
        Reject("quantity_part", () => QuantityPart.Shortfall(Q(-1)));
        Reject("quantity_part", () => QuantityPart.Band(Q(-1), Q(1)));
        Reject("quantity_part", () => new QuantityPart(QuantityPartKind.Shortfall, Q(1), Q(2)));
        Reject("quantity_part", () => new QuantityPart((QuantityPartKind)0, Q(0), null));
        Reject("quantity_part", () => QuantityPart.Band(default, Q(1)));
        Reject("physical_lifecycle_quantity", () => band.Take(Q(-1), Q(10000)));
        Reject("physical_lifecycle_quantity", () => band.Take(Q(10000), Q(0)));
        Require(DefinitionIdentity.For(band).Canonical == """
            {"contract":"quantity_part_v1","Kind":1,"Lower":["signed_ratio","0","1"],"Upper":["signed_ratio","4","5"]}
            """, "Part identity retains its exact reduced level ratios, including zero.");
        Require(DefinitionIdentity.For(band) == DefinitionIdentity.For(QuantityPart.Band(Q(0), new(8, 10))),
            "Equivalent reduced levels have one economic identity.");
        Console.WriteLine("PASS quantity parts: bands, shortfalls, exact boundaries, size scaling and repeating daily quantities");
    }

    static void EntryDays()
    {
        var f = new DeliveryFixture();
        var source = f.Days(("980", PhysicalLifecycle.AllocatedFinalQuantity), ("1050", PhysicalLifecycle.ScheduledQuantity),
            ("900", PhysicalLifecycle.ScheduledQuantity), (null, PhysicalLifecycle.ContractQuantity)).Where(d => d.Slot == 0).ToArray();
        var whole = EntryQuantities.For(source, null, new(1000, 1), f.From, f.To);
        Require(whole.Select(d => d.Selected).SequenceEqual([new(980, 1), new(1050, 1), new(900, 1), new(1000, 1)]) &&
            whole.All(d => d.Taken == d.Selected), "A whole entry takes exactly its selected source quantities.");
        Require(whole.Zip(source).All(p => p.First.Source == p.Second.Source && p.First.Evidence.SequenceEqual(p.Second.Evidence)),
            "Entry quantities retain the source's rank and exact revision evidence.");
        var fractions = DeliveryQuantityFractions.For(whole, day => day < f.From.AddDays(2));
        Require(fractions.Total == new SignedRatio(393, 400) && fractions.Ended == new SignedRatio(203, 400),
            "The four-day example keeps its independent total and accrued quantity fractions.");
        var partial = EntryQuantities.For(source, null, new(1000, 1), f.From.AddDays(1), f.To);
        Require(partial.Select(d => d.Day).SequenceEqual([f.From.AddDays(1), f.From.AddDays(2), f.From.AddDays(3)]),
            "A partial-term payment excludes source days outside its own dates.");
        Require(DeliveryQuantityFractions.For(partial, day => day < f.From.AddDays(2)) ==
            new DeliveryQuantityFractions(new(59, 60), new(7, 20)), "A partial term has its own contractual denominator.");
        var band = EntryQuantities.For(source, QuantityPart.Band(new(0, 1), new(19, 20)), new(1000, 1), f.From, f.To);
        Require(band.Select(d => d.Taken).SequenceEqual([new(950, 1), new(950, 1), new(900, 1), new(950, 1)]),
            "The entry's part is applied once to each day's selected quantity.");
        Reject("physical_lifecycle_quantity", () => EntryQuantities.For(source.Where(d => d.Day != f.From.AddDays(1)), null, new(1000, 1), f.From, f.To));
        Reject("physical_lifecycle_quantity", () => EntryQuantities.For([..source, source[0]], null, new(1000, 1), f.From, f.To));
        Reject("physical_lifecycle_quantity", () => EntryQuantities.For(source.Select((d, n) => n == 1 ? d with { Slot = 2 } : d), null, new(1000, 1), f.From, f.To));
        Console.WriteLine("PASS entry quantities: whole and part, exact fractions, evidence, own dates and complete unambiguous coverage");
    }

    static void View()
    {
        var f=new DeliveryFixture();var at=f.Clock.Boundary(f.From).AddHours(-1);
        var days=f.Days((null,PhysicalLifecycle.ContractQuantity),(null,PhysicalLifecycle.ContractQuantity),
            ("900",PhysicalLifecycle.ScheduledQuantity),(null,PhysicalLifecycle.ContractQuantity));var state=f.State(days);
        var inputs=f.Views(at).Select(input=>input with{FollowedDays=EntryQuantities.For(days,null,new(1000,1),f.From,f.To).ToImmutableDictionary(d=>d.Day)}).ToImmutableArray();
        var view=ScheduleViews.Build(inputs,["term","month","day"],f.Clock.Boundary);
        Require(view.Totals["delivery.quantity"].Value=="3900"&&view.Totals["delivery.amount"].Value=="12480"&&view.Totals["payment.amount"].Value=="-11700","The schedule shows selected quantity and evaluated amounts.");
        var third=view.Rows.Single(r=>r.OperatingDate==f.From.AddDays(2).ToString("yyyy-MM-dd"));
        Require(third.Cells["delivery.quantity"].Sources.SequenceEqual(["scheduled"])&&third.Cells["delivery.amount"].Value=="2880","The scheduled day shows its source and scaled amount.");
        Require(third.Cells["delivery.quantity"].QuantityEvidence.Single()==days.Single(d=>d.Slot==0&&d.Day==f.From.AddDays(2)).Evidence.Single(),"The schedule preserves the selected revision and evidence.");
        Reject("schedule_view",()=>ScheduleViews.Build([inputs[0] with{FollowedDays=inputs[0].FollowedDays!.Remove(f.To.AddDays(-1))},inputs[1]],["term","month","day"],f.Clock.Boundary));
        Reject("schedule_view",()=>ScheduleViews.Build([inputs[0] with{Recorded=ImmutableDictionary<DateOnly,ScheduleViewQuantity>.Empty},inputs[1]],["term","month","day"],f.Clock.Boundary));

        var ended=f.Days(("980",PhysicalLifecycle.AllocatedFinalQuantity),("1050",PhysicalLifecycle.ScheduledQuantity),
            ("900",PhysicalLifecycle.ScheduledQuantity),(null,PhysicalLifecycle.ContractQuantity));var during=f.Clock.Boundary(f.From.AddDays(2)).AddHours(1);
        var valued=f.SideValues(f.State(ended),during);
        var inspector=valued.Sides[0].Value.InspectSchedule(f.Programs[0].Document.ScheduleManifest,ExactDecimal.Parse("1000"),new(1,1),f.Clock.Boundary);
        var first=inspector.Read(0,20).Rows[0];Require(first.Quantity=="980"&&first.QuantityState==PhysicalLifecycle.AllocatedFinalQuantity,"Common Side inspection shows the ended day's selected quantity.");
        Require(first.QuantityEvidence.Single()==ended[0].Evidence.Single(),"Common Side inspection retains evidence.");
        Require(first.QuantityAdjustment==new ScheduleQuantityAdjustment("1000","0.98"),"Inspection explicitly explains the selected quantity against its immutable contract.");
        var cash=valued.Sides[1].Value.InspectSchedule(f.Programs[1].Document.ScheduleManifest,ExactDecimal.Parse("1000"),new(-1,1),f.Clock.Boundary).Read(0,20).Rows.Single();
        Require(cash.Quantity=="-3930"&&cash.Amount=="-11790"&&cash.Accrued=="-6090","Common cash inspection scales the contract amount exactly once.");
        Require(cash.QuantityAdjustment==new ScheduleQuantityAdjustment("-4000","0.9825"),"Multi-day payment inspection explains its whole-term selected ratio.");
        Console.WriteLine("PASS delivery quantities: schedule amounts, rank and evidence, including common Side inspection");
    }

    static void Build()
    {
        var f=new DeliveryFixture();var at=f.Clock.Boundary(f.From.AddDays(2)).AddHours(1);var scale=new ExactRatio(1000,1);
        var contract=GasDailyQuantitySelection.ReadContract(f.Programs[0],scale);
        GasQuantityObservation Observation(string kind,string status,string amount)=>new(Guid.NewGuid(),kind,status,f.Q(amount),"Synthetic daily evidence","manual_daily_v1");
        var final=Observation("allocated","final","980");
        var same=final with{Id=Guid.NewGuid(),Source="facility_allocation_adoption_v1",Evidence="Synthetic facility adoption"};
        ImmutableArray<GasQuantityHead> heads=[new(0,f.From,final),new(0,f.From,same),new(0,f.From.AddDays(1),Observation("scheduled","provisional","1050")),
            new(0,f.From.AddDays(2),Observation("scheduled","provisional","900")),new(0,f.From.AddDays(2),Observation("nominated","provisional","880"))];
        var built=GasHeldQuantities.Build([new(0,contract,scale,"delivery_schedule_v1")],heads,at);
        Require(built.Length==4&&built.All(q=>q.Slot==0),"Only the physical owner carries complete days.");
        Require(built.Select(q=>q.Selected.ToExactString()).SequenceEqual(["980","1050","900","1000"]),"The owner carries the selected quantities once for every following entry.");
        Require(built.Single(q=>q.Slot==0&&q.Day==f.From).Evidence.Length==2,"Equal winners keep both evidence references.");
        Require(built.Single(q=>q.Slot==0&&q.Day==f.To.AddDays(-1)) is {Evidence.IsEmpty:true,Source:PhysicalLifecycle.ContractQuantity},"No record is a labelled contract fallback.");
        Require(GasHeldQuantities.Build([],[],at).IsEmpty,"No opted-in members requires no inputs.");
        Reject("gas_quantity_selection",()=>GasHeldQuantities.Build([new(0,contract,scale,"delivery_schedule_v1")],[new(0,f.To,final)],at));
        Reject("gas_quantity_selection",()=>GasHeldQuantities.Build([new(0,contract,scale,"delivery_schedule_v1")],[new(3,f.From,final)],at));
        Reject("gas_quantity_selection",()=>GasHeldQuantities.Build([new(0,contract,scale,"delivery_schedule_v1"),new(0,contract,scale,"delivery_schedule_v1")],[],at));
        Reject("gas_quantity_selection",()=>GasHeldQuantities.Build([new(0,contract,scale,"unknown_v1")],[],at));
        var contractual=GasHeldQuantities.Build([new(0,contract,scale,null)],heads,at);
        Require(contractual.All(d=>d.Source==PhysicalLifecycle.ContractQuantity&&d.Selected==d.Contract&&d.Evidence.IsEmpty),"A contract-policy source supplies contract days even when operational records exist.");
        Console.WriteLine("PASS delivery quantities: source selection supplies complete owner days and explicit contract-policy inputs");
    }

    static void RetainedRoute()
    {
        var f=new DeliveryFixture(follows:true);var at=f.Clock.Boundary(f.From.AddDays(2)).AddHours(1);
        var days=f.Days(("980",PhysicalLifecycle.AllocatedFinalQuantity),("1050",PhysicalLifecycle.ScheduledQuantity),
            ("900",PhysicalLifecycle.ScheduledQuantity),(null,PhysicalLifecycle.ContractQuantity));
        var state=f.State(days);var (prepared,portfolio)=f.Prepare(at,state);
        var context=new UnitValuationContext(prepared.Market.Snapshot,portfolio.Setup,prepared.Market).For(prepared.Contracts[0][0],null,state);
        var value=f.Instrument.EvaluateLifecycle(context);Equal(value.Total,"-5710");Equal(value.RealizedUnsettled,"-6090");Equal(value.Unrealized,"380");
        Require(f.Instrument.Declare(context).ModelVersion.StartsWith(PhysicalCashState.Policy+"/"+"delivery_schedule_v1"+"/"),"The retained model names the selected quantity policy.");
        Require(!new DeliveryFixture(follows:false).Instrument.Declare(context.ForCoverage(null,f.State())).ModelVersion.Contains("delivery_schedule_v1"),"Legacy model identity stays unchanged.");
        Reject("physical_lifecycle_quantity",()=>f.Instrument.ValidateValuationState(f.State([..days.Select(d=>d with{Contract=f.Q("999"),
            Selected=d.Source==PhysicalLifecycle.ContractQuantity?f.Q("999"):d.Selected})])));
        Reject("physical_lifecycle_quantity",()=>f.Instrument.ValidateValuationState(f.State([..days.Select(d=>d with{Day=d.Day.AddDays(40)})])));
        Reject("physical_lifecycle_quantity",()=>f.Instrument.ValidateValuationState(f.State([..days.Where(d=>d.Day!=f.From.AddDays(3))])));
        var orphan=new PhysicalCashState(state.TradeId,state.BindingId,state.Quantity,state.BookId,[],PhysicalCashState.Policy,
            [..days.Select(d=>d with{Slot=1})]);
        Reject("physical_lifecycle_quantity",()=>f.Instrument.ValidateValuationState(orphan));
        var another=f.State(f.Days((null,PhysicalLifecycle.ContractQuantity),(null,PhysicalLifecycle.ContractQuantity),
            (null,PhysicalLifecycle.ContractQuantity),(null,PhysicalLifecycle.ContractQuantity)));
        var combined=f.Prepare(at,state,another).Prepared;
        Equal(RiskEngine.CalculateExact(combined,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"-11310");
        Console.WriteLine("PASS delivery quantities: retained Side parity, policy identity, complete schedule admission and separate trade reuse");
    }

    static void WorkedExample()
    {
        var f=new DeliveryFixture();var at=f.Clock.Boundary(f.From.AddDays(2)).AddHours(1);
        var plain=f.Evaluate(f.State(),at);
        Equal(plain.Unrealized,"400");Equal(plain.RealizedUnsettled,"-6000");Equal(plain.Total,"-5600");
        var days=f.Days(("980",PhysicalLifecycle.AllocatedFinalQuantity),("1050",PhysicalLifecycle.ScheduledQuantity),
            ("900",PhysicalLifecycle.ScheduledQuantity),("1000",PhysicalLifecycle.ContractQuantity));
        var state=f.State(days);var value=f.Evaluate(state,at);
        Equal(value.Unrealized,"380");Equal(value.RealizedUnsettled,"-6090");Equal(value.Total,"-5710");
        var physical=value.Rows.Where(r=>r.Slot==0).ToArray();
        Require(physical.Select(r=>r.Unrealized.ToExactString()).SequenceEqual(["0","0","2880","3200"]),"Only unended physical days retain selected value.");
        Require(physical.Select(r=>r.QuantityBasis).SequenceEqual([PhysicalLifecycle.AllocatedFinalQuantity,PhysicalLifecycle.ScheduledQuantity,
            PhysicalLifecycle.ScheduledQuantity,PhysicalLifecycle.ContractQuantity]),"Every physical row names the selected rank.");
        var cash=value.Rows.Single(r=>r.Slot==1);
        Equal(cash.Accrued,"-6090");Equal(cash.Unrealized,"-5700");Equal(cash.Expected!,"-11790");
        Require(cash.QuantityBasis==PhysicalLifecycle.SelectedQuantityPolicy,"Cash names the selected quantity policy.");
        var shocked=f.Evaluate(state,at,"3.3");Equal(shocked.Unrealized,"570");Equal(shocked.RealizedUnsettled,"-6090");
        var corrected=f.State(f.Days(("980",PhysicalLifecycle.AllocatedFinalQuantity),("1020",PhysicalLifecycle.AllocatedFinalQuantity),
            ("900",PhysicalLifecycle.ScheduledQuantity),("1000",PhysicalLifecycle.ContractQuantity)));
        Equal(f.Evaluate(corrected,at).RealizedUnsettled,"-6000");Equal(f.Evaluate(state,at).RealizedUnsettled,"-6090");
        var provisional=f.State(f.Days(("980",PhysicalLifecycle.AllocatedFinalQuantity),("1050",PhysicalLifecycle.ScheduledQuantity),
            ("950",PhysicalLifecycle.AllocatedProvisionalQuantity),("1000",PhysicalLifecycle.ContractQuantity)));
        Equal(f.Evaluate(provisional,at).Unrealized,"390");
        Reject("physical_lifecycle_quantity",()=>f.Evaluate(f.State([..days.Where(d=>d.Day!=f.From.AddDays(3))]),at));

        var payment=new CashOccurrencePayment(Guid.NewGuid(),1,0,at.AddMinutes(-1),at.AddMinutes(-1),ExactMoney.Parse("-2940",f.Currency));
        PhysicalLifecycleRow Paid(string first,string second,DateTimeOffset? instant=null)=>f.Evaluate(f.State(f.Days(
            (first,PhysicalLifecycle.AllocatedFinalQuantity),(second,PhysicalLifecycle.AllocatedFinalQuantity),
            ("900",PhysicalLifecycle.ScheduledQuantity),("1000",PhysicalLifecycle.ContractQuantity)),[payment]),instant??at).Rows.Single(r=>r.Slot==1);
        Require(Paid("900","1050").Settlement==SettlementState.PartPaid,"Accrual above paid stays part paid.");
        Require(Paid("900","0").Settlement==SettlementState.Overpaid,"Accrual below paid is overpaid.");
        var zero=Paid("0","0");Require(zero.Settlement==SettlementState.Overpaid,"Zero accrual retains an overpaid balance.");
        Equal(zero.Accrued,"0");Equal(zero.Paid,"-2940");Equal(zero.Outstanding,"2940");Equal(zero.RealizedUnsettled,"2940");
        var noFuture=f.State(f.Days(("0",PhysicalLifecycle.AllocatedFinalQuantity),("0",PhysicalLifecycle.AllocatedFinalQuantity),
            ("0",PhysicalLifecycle.ScheduledQuantity),("0",PhysicalLifecycle.ScheduledQuantity)),[payment]);
        var allZero=f.Evaluate(noFuture,at);Equal(allZero.Total,"2940");Equal(allZero.Unrealized,"0");
        Equal(f.Evaluate(noFuture,f.Clock.Boundary(f.To).AddDays(4)).Total,"2940");
        var hidden=f.State(days,[payment with{KnownAt=at.AddHours(1)}]);Equal(f.Evaluate(hidden,at).RealizedUnsettled,"-6090");
        Reject("settlement_conservation",()=>f.Evaluate(f.State(days,[payment with{Amount=ExactMoney.Parse("2940",f.Currency)}]),at));
        var period=new DeliveryFixture(3,GasQuantityBasis.PeriodTotalEqualOperatingDays);
        var periodDays=period.Days(("300",PhysicalLifecycle.ScheduledQuantity),(null,PhysicalLifecycle.ContractQuantity),(null,PhysicalLifecycle.ContractQuantity));
        Equal(period.Evaluate(period.State(periodDays),period.Clock.Boundary(period.From).AddHours(-1)).Unrealized,"580/3");
        var unequal=new DeliveryFixture(weights:[1,2,3,4]);
        var varied=unequal.State(unequal.Days(("500",PhysicalLifecycle.ScheduledQuantity),(null,PhysicalLifecycle.ContractQuantity),
            (null,PhysicalLifecycle.ContractQuantity),(null,PhysicalLifecycle.ContractQuantity)));
        var unequalValue=unequal.Evaluate(varied,at);Equal(unequalValue.RealizedUnsettled,"-7500");Equal(unequalValue.Unrealized,"1400");
        Reject("physical_lifecycle_quantity",()=>unequal.Evaluate(unequal.State(),at));
        var mixedState=new PhysicalCashState(state.TradeId,state.BindingId,state.Quantity,state.BookId,[],PhysicalCashState.Policy,days);
        var mixed=f.Evaluate(mixedState,at,includeContract:true);var contractOnly=f.Evaluate(f.State(),at,includeContract:true);
        Require(mixed.Rows.Where(r=>r.Slot>=2).SequenceEqual(contractOnly.Rows.Where(r=>r.Slot>=2)),"Contract memberships remain unchanged beside selected memberships.");
        Console.WriteLine("PASS delivery quantities: four-day value, shock, correction, exact period-total scale and zero after payment");
    }

    sealed class DeliveryFixture
    {
        readonly CommodityLookupFixture fixture=new(31);
        internal readonly DateOnly From,To;
        internal readonly GasDayClock Clock;
        internal readonly ImmutableArray<BookedSideProgram> Programs;
        internal readonly ImmutableArray<GasValuationAssignment> Assignments;
        internal readonly RetainedGasInstrument Instrument;
        readonly RetainedGasInstrument legacy;
        readonly Guid book=Guid.NewGuid();
        internal ReferenceKey Currency=>fixture.Quote.Currency;
        internal ReferenceKey Unit=>fixture.Quote.Quantity;
        internal readonly GasQuantityBasis Basis;
        readonly int[]? weights;
        internal DeliveryFixture(int count=4,GasQuantityBasis basis=GasQuantityBasis.PerOperatingDay,int[]? weights=null,bool follows=true)
        {
            Basis=basis;this.weights=weights;
            var lookups=fixture.Swap().Floating.GenerateSchedule().Rows[0].Prices.Take(count).Select(p=>p.Lookup!).ToArray();
            From=lookups[0].Contract.DeliveryFrom;To=From.AddDays(count);
            ReferenceKey R(string code)=>new(fixture.Quote.Key.Revision,code);
            Clock=GasDayClock.Capture(R("synthetic_delivery_clock"),TimeZoneInfo.Utc,360);
            var location=new GasDeliveryLocation(R("synthetic_pipeline"),R("synthetic_zone"),R("synthetic_point"));
            Assignments=[..lookups.Select((l,n)=>new GasValuationAssignment(R("synthetic_value_"+n),location,R("synthetic_product"),
                l.Contract.DeliveryFrom,l.Contract.DeliveryTo,l.Settlement,l.Quote,new(l.Market,l.Contract),l.Fixing,new(default,fixture.Quote)))];
            var gas=new PhysicalGasSide(Guid.NewGuid(),location,R("synthetic_product"),new(Clock,From,To,basis,
                new(ExactDecimal.Parse("1"),Unit),GasFuelConvention.None,default),Assignments,SyntheticSideCalendars.Physical());
            var cash=new FixedSide(Guid.NewGuid(),new FlatPrice(new UnitPrice(ExactDecimal.Parse("3"),fixture.Quote)),fixture.Quote,gas.UnitQuantity,
                [new(From,To,Clock.Boundary(To).AddDays(2),new(weights?.Sum()??(basis==GasQuantityBasis.PerOperatingDay?count:1),1),null){DeliveryCompletionAt=Clock.Boundary(To)}],
                SyntheticSideCalendars.Dated(false),gasDeliveryClock:Clock);
            BookedSideProgram Book(Side side)
            {
                var d=ScheduleDefinitionCompiler.Generate(side,lifecycle:ScheduleLifecycleSelection.StartedPhysicalDelivery);
                // Synthetic nonuniform retained quantities exercise the shared lifecycle's
                // quantity-weighted accrual, independently of the gas ticket's uniform terms.
                if(side==gas&&weights is not null)d=new(d.SideId,d.Family,d.EconomicIdentity,d.PricingBindingIdentity,d.SchemaVersion,d.GeneratorVersion,
                    d.Rows.Select(r=>r.Operation=="quantity_literal"?r with{Parameters=r.Parameters.SetItem("numerator",weights[r.Grid!.Value].ToString())}:r).ToArray(),
                    d.Edges,d.Bindings,d.Roots,d.Ports,d.Grids);
                return BookedSideProgram.Compile(d);
            }
            Programs=[Book(gas),Book(cash)];
            Instrument=new(Guid.NewGuid(),[new(gas.Id,ExactDecimal.Parse("1"),ExactRatio.One,Programs[0],follows?0:null,follows?"delivery_schedule_v1":null),
                new(cash.Id,ExactDecimal.Parse("-1"),ExactRatio.One,Programs[1],follows?0:null)],Assignments);
            legacy=new(Instrument.Id,[..Instrument.Members.Select(m=>new RetainedGasMember(m.SideId,m.Direction,m.Ratio,m.Program))],Assignments);
        }
        internal ExactQuantity Q(string amount)=>ExactQuantity.FromQuantity(new(ExactDecimal.Parse(amount),Unit));
        internal ImmutableArray<HeldDayQuantity> Days(params (string? Selected,string Source)[] values)
        {
            var physical=values.Select((v,n)=>{
                var contract=Q("1000").Scale(new ExactRatio(weights?[n]??1,Basis==GasQuantityBasis.PerOperatingDay?1:To.DayNumber-From.DayNumber));
                return new HeldDayQuantity(0,From.AddDays(n),contract,v.Selected is null?contract:Q(v.Selected),v.Source,
                    v.Source==PhysicalLifecycle.ContractQuantity?[]:[new("synthetic_daily_v1",Guid.NewGuid(),"Synthetic daily record")]);
            }).ToArray();
            return [..physical];
        }
        internal PhysicalCashState State(ImmutableArray<HeldDayQuantity> days=default,ImmutableArray<CashOccurrencePayment> payments=default)=>
            new(Guid.NewGuid(),Instrument.Id,new(ExactDecimal.Parse("1000"),Unit),book,payments.IsDefault?[]:payments,PhysicalCashState.Policy,
                days);
        internal (PreparedConnectedPortfolioRisk Prepared,ValuationPortfolio Portfolio) Prepare(DateTimeOffset at,params PhysicalCashState[] states)=>
            PrepareFor(states.All(s=>s.OwnerDays.IsEmpty)?legacy:Instrument,at,"3.2",states);
        (PreparedConnectedPortfolioRisk Prepared,ValuationPortfolio Portfolio) PrepareFor(RetainedGasInstrument instrument,DateTimeOffset at,string mark,params PhysicalCashState[] states)
        {
            var snapshot=(fixture.Snapshot with{ValuationAt=at,MarketCutoff=at,Selections=[..fixture.Snapshot.Selections.Select((s,n)=>s with{
                Observation=s.Observation with{Value=n%2==0?ExactDecimal.Parse(mark)-ExactDecimal.Parse("0.2"):ExactDecimal.Parse("0.2")}})]}).WithKnowledge(Guid.NewGuid(),at);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
                new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)){
                BindingStates=states.ToImmutableDictionary(s=>new MeasureBinding(s.TradeId,s.BindingId),s=>(IBindingValuationState)s)};
            var portfolio=ValuationPortfolio.FromTrades([..states.Select(s=>new Trade(s.TradeId,book,[new(s.BindingId,instrument,s.Quantity)]))],setup);
            var dependencies=portfolio.Declare(snapshot).SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
            var market=fixture.Model.Build(snapshot,dependencies);var exported=fixture.Model.Compile(snapshot,dependencies,false,market.Graph.Plan.Context);
            var bindings=dependencies.ToImmutableDictionary(r=>r,r=>exported.Points[(r,InputUse.Variable)]);
            return (RiskEngine.PrepareConnected(ConnectedValuation.Prepare(market.Graph,market.Snapshot),bindings,[portfolio],market),portfolio);
        }
        internal InstrumentValuation SideValues(PhysicalCashState state,DateTimeOffset at)
        {
            var (prepared,portfolio)=Prepare(at,state);
            var context=new UnitValuationContext(prepared.Market.Snapshot,portfolio.Setup,prepared.Market).For(prepared.Contracts[0][0],null,state);
            var (basis,scenario)=context.RetainedProgramMarkets(prepared.Contracts[0][0].Dependencies);
            var sources=new ScheduleMarketSources(basis.Graph.Plan.Context,basis,null,null,IntervalKnowledgePolicy.ResolvedCutoff,PendingActualsPolicy.RequireActuals,
                Instrument.PriceLookups,[],Assignments);
            var inputs=new RetainedProgramContext(sources,scenario){Cash=state,Owner=new(Instrument.Id,Instrument.EconomicIdentity),
                Geometry=PhysicalLifecycle.PrepareGeometry([Programs[0]],context.Snapshot),HeldFactors=[new(1000,1),new(-1000,1)]};
            return Instrument.PV(context.SideContextRetained(inputs));
        }
        internal ImmutableArray<SideViewInput> Views(DateTimeOffset at)=>[..Members(at,"3.2",false).Select((m,n)=>new SideViewInput(
            n==0?"delivery":"payment",m.Evaluation,m.Program,ExactDecimal.Parse("1000"),new(n==0?1:-1,1),
            [("quantity",ScheduleViewMeasure.Quantity),("price",ScheduleViewMeasure.Price),("amount",ScheduleViewMeasure.Amount)]))];
        internal PhysicalLifecycleValue Evaluate(PhysicalCashState state,DateTimeOffset at,string mark="3.2",bool includeContract=false)
        {
            var instrument=state.OwnerDays.IsEmpty?legacy:Instrument;
            if(includeContract)
            {
                var followed=!state.OwnerDays.IsEmpty;
                instrument=new(Instrument.Id,[..instrument.Members,
                    new RetainedGasMember(Instrument.Members[0].SideId,ExactDecimal.Parse("1"),ExactRatio.One,Programs[0],followed?2:null),
                    new RetainedGasMember(Instrument.Members[1].SideId,ExactDecimal.Parse("-1"),ExactRatio.One,Programs[1],followed?2:null)],Assignments);
            }
            var(prepared,portfolio)=PrepareFor(instrument,at,mark,state);
            var context=new UnitValuationContext(prepared.Market.Snapshot,portfolio.Setup,prepared.Market).For(prepared.Contracts[0][0],null,state);
            return instrument.EvaluateLifecycle(context);
        }
        ImmutableArray<LifecycleMember> Members(DateTimeOffset at,string mark,bool includeContract)
        {
            var context=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic_selected_delivery_v1",at,at,at,at);
            var programs=includeContract?Programs.AddRange(Programs):Programs;
            var members=programs.Select((program,n)=>{
                var demand=program.Inputs(context);
                var result=program.Execute(demand,"base","base",ScheduleProgramExecutionChecks.Sources(program,demand,"base","base",
                    (row,cell,scenario)=>row.Operation=="discount"?(ScheduleNumber.Parse("1"),default):
                        (ScheduleNumber.Parse(mark),ScheduleCellState.From(null,RowPriceState.Projected))));
                return new LifecycleMember(program,result,n%2==0,value=>value.Scale(ExactDecimal.Parse(n%2==0?"1000":"-1000")));
            }).ToImmutableArray();
            return members;
        }
    }
    static void Equal(ExactMoney value,string expected){if(value.ToExactString()!=expected)throw new Exception($"Expected {expected}, got {value.ToExactString()}");}

    static void HeldState()
    {
        var unit=new CommodityLookupFixture(31).Quote.Quantity;var day=new DateOnly(2026,11,1);
        ExactQuantity Q(string amount)=>ExactQuantity.FromQuantity(new(ExactDecimal.Parse(amount),unit));
        var proof=new DeliveryQuantityEvidence("synthetic_daily_v1",Guid.NewGuid(),"Synthetic scheduled quantity");
        HeldDayQuantity Held(int slot,int n,string selected,string source)=>new(slot,day.AddDays(n),Q("1000"),Q(selected),source,
            source==PhysicalLifecycle.ContractQuantity?[]:[proof]);
        PhysicalCashState State(ImmutableArray<HeldDayQuantity> days)=>
            new(Guid.NewGuid(),Guid.NewGuid(),new(ExactDecimal.Parse("1000"),unit),null,[],PhysicalCashState.Policy,days);
        ImmutableArray<HeldDayQuantity> days=[Held(0,0,"980",PhysicalLifecycle.ScheduledQuantity),Held(0,1,"1000",PhysicalLifecycle.ContractQuantity)];
        var held=State(days);
        Require(held.OwnerDays.SequenceEqual(days)&&held.OwnerDays[0].Evidence.Single()==proof,"Held state retains owner facts and evidence without membership policy copies.");
        Require(State(default).OwnerDays.IsEmpty,"Legacy state contains no selected owner quantities.");
        Reject("physical_lifecycle_quantity",()=>State([days[0],..days]));
        Reject("physical_lifecycle_quantity",()=>State(days.SetItem(0,days[0] with{Source="unknown_v1"})));
        Reject("physical_lifecycle_quantity",()=>State(days.SetItem(0,days[0] with{Contract=Q("0")})));
        Reject("physical_lifecycle_quantity",()=>State(days.SetItem(0,days[0] with{Selected=Q("-1")})));
        Reject("physical_lifecycle_quantity",()=>State(days.SetItem(0,days[0] with{Evidence=[]})));
        Reject("physical_lifecycle_quantity",()=>State(days.SetItem(1,days[1] with{Evidence=[proof]})));
        Reject("physical_lifecycle_quantity",()=>State(days.SetItem(1,days[1] with{Selected=Q("900")})));
        Console.WriteLine("PASS delivery quantities: complete held membership sources, nonnegative quantities and evidence admission");
    }
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Reject(string code,Action run){try{run();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
