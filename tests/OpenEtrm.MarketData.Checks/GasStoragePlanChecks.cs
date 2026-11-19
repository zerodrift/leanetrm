using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

// Synthetic contract: 100,000 of capacity in three ratchet bands, 1% injection fuel in kind.
// The base plan opens at 29,000, injects 2,000 then 1,500 and withdraws 1,000 on three January 2041 gas days.
// Every expected figure is worked by hand here, independently of the implementation's arithmetic.
internal static class GasStoragePlanChecks
{
    public static int Run()
    {
        var passed=0;
        void Check(string name,Action action){action();passed++;Console.WriteLine("PASS gas storage: "+name);}
        void Reject(string code,Action action)
        {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
        void Same(ExactQuantity value,string expected)
        {if(value.ToExactString()!=expected)throw new Exception($"Expected {expected}, got {value.ToExactString()}");}
        void Equal(ExactMoney value,string expected)
        {if(value.ToMoney().Amount.ToString()!=expected)throw new Exception($"Expected {expected}, got {value.ToExactString()}");}

        var f=new Fixture();var gas=f.R("gas_quantity");
        Quantity Q(string amount)=>new(ExactDecimal.Parse(amount),gas);
        ExactDecimal D(string value)=>ExactDecimal.Parse(value);
        var entitlement=new GasStorageEntitlement(f.Revision,"synthetic_storage_contract",gas,Q("0"),Q("100000"),
            [new(Q("0"),Q("30000"),Q("2000"),Q("500")),new(Q("30000"),Q("70000"),Q("1500"),Q("1000")),new(Q("70000"),Q("100000"),Q("1000"),Q("1500"))],
            D("0.01"),D("0"),GasStorageEntitlement.OpeningInventoryBand,GasStorageEntitlement.SingleDirection,GasStorageEntitlement.InKindAtPoint);
        // Gas days start at 09:00 UTC on this synthetic clock.
        DeliveryInterval GasDay(int day)=>new(Fixture.Utc("2041-01-01T09:00:00Z").AddDays(day-1),Fixture.Utc("2041-01-01T09:00:00Z").AddDays(day),new(2041,1,day),9,0,1);
        ImmutableArray<GasStorageDay> Days(params (string Injection,string Withdrawal)[] flows)=>
            [..flows.Select((flow,i)=>new GasStorageDay(GasDay(i+1),Q(flow.Injection),Q(flow.Withdrawal)))];
        GasStoragePlanResult Evaluate(ImmutableArray<GasStorageDay> days,string opening="29000",string terminalMinimum="0",string terminalMaximum="100000",
            GasStorageEntitlement? contract=null,string implementation=GasStoragePlans.Implementation)=>
            GasStoragePlans.Evaluate(contract??entitlement,new(days.IsDefaultOrEmpty?GasDay(1).Start:days[0].GasDay.Start,ExactQuantity.FromQuantity(Q(opening))),
                new(Q(terminalMinimum),Q(terminalMaximum)),days,implementation);
        var basePlan=Days(("2000","0"),("1500","0"),("0","1000"));

        Check("inventory follows each gas day's flows net of in-kind injection fuel",()=>
        {
            var result=Evaluate(basePlan);
            // 29,000 + 2,000 x 0.99 = 30,980; + 1,500 x 0.99 = 32,465; - 1,000 = 31,465.
            Same(result.Days[0].Closing,"30980");Same(result.Days[1].Closing,"32465");Same(result.Days[2].Closing,"31465");
            Same(result.Closing.Inventory,"31465");Same(result.TotalInjection,"3500");Same(result.TotalWithdrawal,"1000");
            Same(result.Days[0].InjectionFuel,"20");Same(result.Days[1].InjectionFuel,"15");Same(result.Days[2].WithdrawalFuel,"0");
            if(!result.Days.Select(d=>d.Band).SequenceEqual([0,1,1])||result.Closing.At!=GasDay(3).End||result.Implementation!=GasStoragePlans.Implementation)
                throw new Exception("Each day uses the band of its opening inventory and the plan closes at its last gas day's end.");
        });
        Check("a day's limits are those of the band its opening inventory is in",()=>
        {
            // Day one opens in the first band and may inject its 2,000 although it ends in the second.
            Evaluate(Days(("2000","0")));
            Reject("gas_storage_ratchet",()=>Evaluate(Days(("2000","0"),("1500.001","0"))));
            Reject("gas_storage_ratchet",()=>Evaluate(Days(("0","500.001"))));
            Evaluate(Days(("0","1000")),opening:"30000");
            Evaluate(Days(("0","1500")),opening:"100000");
            Reject("gas_storage_ratchet",()=>Evaluate(Days(("1000.001","0")),opening:"70000"));
        });
        Check("inventory stays inside the contracted capacity and ends inside the stated terminal range",()=>
        {
            Reject("gas_storage_inventory",()=>Evaluate(Days(("0","500")),opening:"499"));
            Reject("gas_storage_inventory",()=>Evaluate(Days(("1000","0")),opening:"99500"));
            Reject("gas_storage_inventory",()=>Evaluate(basePlan,opening:"100001"));
            Reject("gas_storage_terminal",()=>Evaluate(basePlan,terminalMinimum:"31466"));
            Reject("gas_storage_terminal",()=>Evaluate(basePlan,terminalMaximum:"31464"));
            Evaluate(basePlan,terminalMinimum:"31465",terminalMaximum:"31465");
            Reject("gas_storage_terminal",()=>Evaluate(basePlan,terminalMinimum:"5",terminalMaximum:"4"));
        });
        Check("withdrawal fuel in kind debits more inventory than is delivered",()=>
        {
            var result=Evaluate(Days(("0","980")),opening:"30000",contract:entitlement with{WithdrawalFuel=D("0.02")});
            // 980 delivered is 98% of the 1,000 taken from inventory.
            Same(result.Closing.Inventory,"29000");Same(result.Days[0].WithdrawalFuel,"20");Same(result.TotalWithdrawal,"980");
        });
        Check("a gas day flows one way, on an ordered contiguous grid, under the selected implementation",()=>
        {
            Reject("gas_storage_direction",()=>Evaluate(Days(("100","100"))));
            Reject("gas_storage_flow",()=>Evaluate(Days(("-1","0"))));
            Reject("gas_storage_grid",()=>Evaluate([basePlan[0],basePlan[2]]));
            Reject("gas_storage_grid",()=>Evaluate([basePlan[1],basePlan[0]]));
            Reject("gas_storage_grid",()=>Evaluate([]));
            Reject("gas_storage_grid",()=>Evaluate([basePlan[0] with{GasDay=basePlan[0].GasDay with{End=basePlan[0].GasDay.End.AddHours(3)}}]));
            Reject("gas_storage_implementation",()=>Evaluate(basePlan,implementation:"another_plan_v1"));
            Reject("gas_storage_opening",()=>GasStoragePlans.Evaluate(entitlement,new(GasDay(2).Start,ExactQuantity.FromQuantity(Q("29000"))),
                new(Q("0"),Q("100000")),basePlan,GasStoragePlans.Implementation));
        });
        Check("an entitlement states every policy and a ratchet grid that covers its capacity exactly",()=>
        {
            Reject("gas_storage_entitlement",()=>Evaluate(basePlan,contract:entitlement with{RatchetBasis="interpolated_v1"}));
            Reject("gas_storage_entitlement",()=>Evaluate(basePlan,contract:entitlement with{FlowPolicy="netted_v1"}));
            Reject("gas_storage_entitlement",()=>Evaluate(basePlan,contract:entitlement with{FuelBasis="cash_fuel_v1"}));
            Reject("gas_storage_entitlement",()=>Evaluate(basePlan,contract:entitlement with{Ratchets=entitlement.Ratchets.RemoveAt(1)}));
            Reject("gas_storage_entitlement",()=>Evaluate(basePlan,contract:entitlement with{Ratchets=entitlement.Ratchets.RemoveAt(2)}));
            Reject("gas_storage_entitlement",()=>Evaluate(basePlan,contract:entitlement with{InjectionFuel=D("1")}));
            Reject("gas_storage_entitlement",()=>Evaluate(basePlan,contract:entitlement with{WithdrawalFuel=D("-0.01")}));
            Reject("gas_storage_entitlement",()=>Evaluate(basePlan,contract:entitlement with{MaximumInventory=new(D("100000"),f.R("power_quantity"))}));
        });
        Check("the plan's flows value as an ordinary resource right and reprice with the gas market",()=>
        {
            var result=Evaluate(basePlan);var unbound=f.Snapshot();var snapshot=unbound.WithKnowledge(Guid.NewGuid(),unbound.ValuationAt);var one=D("1");var minus=D("-1");
            var right=new Quantity(one,f.R("right_count"));var resource=new AssetKey(f.Revision,entitlement.Code);var plan=new OperatingPlanReference(f.Revision,Guid.NewGuid(),1);
            var injection=new OperatingOutputPort(plan,resource,OperatingOutputKind.StorageInjection,gas);
            var withdrawal=new OperatingOutputPort(plan,resource,OperatingOutputKind.StorageWithdrawal,gas);
            var rule=new PaymentRule(PaymentFrequency.Monthly,PaymentDateRule.DaysAfterPeriodEnd,20,null,PaymentDayBasis.CalendarDays,null,DateRoll.Unadjusted,null,PaymentRule.Implementation);
            var calendars=new SideCalendarPolicy(CalendarBinding.NotApplicable(),CalendarBinding.NotApplicable(),CalendarBinding.NotApplicable(),SideCalendarPolicy.Implementation);
            var quote=f.Quotes[0];
            OperatingSideTerms Terms(QuoteSpecification priced,Func<GasStorageDayResult,OperatingQuantityTerm> quantity,CurveRequest? projection)=>new(right,priced,rule,ComponentKind.Payment,
                [..result.Days.Select(d=>new OperatingSidePeriod(d.Day.GasDay,new(2041,1,1),new(2041,2,1),quantity(d),projection))],OperatingSideTerms.SupportedImplementation,calendars);
            // Injected gas is bought at 3.1 and withdrawn gas sold at 3.3; each direction pays its own commodity charge.
            var bought=new FloatingSide(Guid.NewGuid(),Terms(quote,_=>new(injection,null),new CurveRequest(f.C("base_forward"),f.K(2))));
            var sold=new FloatingSide(Guid.NewGuid(),Terms(quote,_=>new(withdrawal,null),new CurveRequest(f.C("local_gas"),f.K(3))));
            var injectionCharge=new FixedSide(Guid.NewGuid(),Terms(quote,_=>new(injection,null),null),new FlatPrice(new(D("0.02"),quote)));
            var withdrawalCharge=new FixedSide(Guid.NewGuid(),Terms(quote,_=>new(withdrawal,null),null),new FlatPrice(new(D("0.03"),quote)));
            var feeQuote=quote with{Key=f.R("right_fee"),Quantity=right.Specification};
            var demand=new FixedSide(Guid.NewGuid(),new(right,feeQuote,rule,ComponentKind.Payment,[new(result.Days[0].Day.GasDay,new(2041,1,1),new(2041,2,1),new(null,right),null)],
                OperatingSideTerms.SupportedImplementation,calendars),new FlatPrice(new(D("500"),feeQuote)));
            var instrument=new OperatingRightInstrument(Guid.NewGuid(),resource,right,quote.Currency,
                [new(sold,one),new(bought,minus),new(injectionCharge,minus),new(withdrawalCharge,minus),new(demand,minus)]);
            var cells=result.Days.SelectMany(d=>new[]{
                new OperatingQuantityCell(injection,d.Day.GasDay,ExactQuantity.FromQuantity(d.Day.Injection),VolumeState.Forecast,Guid.NewGuid(),snapshot.ValuationAt),
                new OperatingQuantityCell(withdrawal,d.Day.GasDay,ExactQuantity.FromQuantity(d.Day.Withdrawal),VolumeState.Forecast,Guid.NewGuid(),snapshot.ValuationAt)}).ToImmutableArray();
            var discount=new ExplicitDiscounting(one,DiscountFactorSource.Undiscounted);
            var inputs=new OperatingQuantityInputs(snapshot,snapshot.ValuationAt,cells,IntervalKnowledgePolicy.ResolvedCutoff);
            // Both gas curves stand on the same future: 0.1 higher moves value by (1,000 - 3,500) x 0.1 = -250.
            var bump=new QuoteBump(new(f.S(1),InputUse.Variable),quote.Key,PriceUnit.FromQuote(quote),D("0.1"),QuoteBump.Convention);
            var scenarios=f.Model().BuildScenarios(snapshot,instrument.Sides.SelectMany(s=>s.Side.Dependencies).Distinct().ToImmutableArray(),[bump]);
            SideValuationContext Scenario(CurveBuild market)=>new SideValuationContext(market,discount).WithOperatingQuantities(inputs);
            // 1,000 x 3.3 - 3,500 x 3.1 - 3,500 x 0.02 - 1,000 x 0.03 - 500 = -8,150.
            Equal(instrument.PV(Scenario(scenarios.Base)).PresentValue,"-8150");
            Equal(instrument.PV(Scenario(scenarios.Factors[0].Up)).PresentValue,"-8400");Equal(instrument.PV(Scenario(scenarios.Factors[0].Down)).PresentValue,"-7900");
        });
        return passed;
    }
}
