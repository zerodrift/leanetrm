using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
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

        Check("a booked contract values its admitted plan through the risk engine, and refuses a plan its ratchets do not admit",()=>
        {
            var quote=f.Quotes[0];var january=(From:new DateOnly(2041,1,1),To:new DateOnly(2041,2,1));
            var rule=new PaymentRule(PaymentFrequency.Monthly,PaymentDateRule.DaysAfterPeriodEnd,20,null,PaymentDayBasis.CalendarDays,null,DateRoll.Unadjusted,null,PaymentRule.Implementation);
            var contractDays=basePlan.Select(d=>new GasStorageContractDay(d.GasDay,january.From,january.To,new(f.C("base_forward"),f.K(2)),new(f.C("local_gas"),f.K(3)))).ToImmutableArray();
            var charges=new GasStorageCharges(new(D("0.02"),quote),new(D("0.03"),quote),new(D("0.005"),quote));
            GasStorageInstrument Contract(ImmutableArray<GasStorageDay> plan)=>GasStorageInstrument.Compose(Guid.NewGuid(),entitlement,
                new(GasDay(1).Start,ExactQuantity.FromQuantity(Q("29000"))),new(Q("0"),Q("100000")),quote,rule,charges,contractDays,plan);
            var instrument=Contract(basePlan);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted));
            var portfolio=ValuationPortfolio.FromTrades([new(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),instrument,instrument.UnitQuantity)])],setup);
            var bump=new QuoteBump(new(f.S(1),InputUse.Variable),quote.Key,PriceUnit.FromQuote(quote),D("0.1"),QuoteBump.Convention);
            var run=RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),f.Snapshot(),[portfolio]),[bump],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings).Groups.Single();
            // 1,000 x 3.3 - 3,500 x 3.1 - 3,500 x 0.02 - 1,000 x 0.03 - 100,000 x 0.005 = -8,150; a 0.1 higher market moves it by -250.
            Equal(run.BaseValues.Single().Value,"-8150");
            var shocked=run.Factors.Single().Positions.Single().Values;Equal(shocked.Up,"-8400");Equal(shocked.Down,"-7900");
            Same(instrument.Plan.Closing.Inventory,"31465");
            Reject("gas_storage_ratchet",()=>Contract(Days(("2000","0"),("2000","0"),("0","1000"))));
            Reject("gas_storage_terms",()=>Contract([basePlan[0],basePlan[1]]));
            if(DefinitionIdentity.For(instrument)==DefinitionIdentity.For(Contract(Days(("2000","0"),("0","0"),("0","1000")))))throw new Exception("A different plan is a different valued schedule.");
            // Once the first gas day has begun the planned flows are no longer this profile's to value.
            var started=f.Snapshot() with{ValuationAt=GasDay(1).Start.AddHours(1)};
            Reject("gas_storage_started",()=>instrument.PV(new SideValuationContext(started,new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted))));
            // A term across two payment months pays the capacity charge once in each: an idle plan is worth -2 x 100,000 x 0.005.
            DeliveryInterval Day(DateOnly day)=>new(new(day.ToDateTime(new(9,0),DateTimeKind.Utc)),new(day.AddDays(1).ToDateTime(new(9,0),DateTimeKind.Utc)),day,9,0,1);
            var span=new[]{new DateOnly(2041,1,31),new DateOnly(2041,2,1)}.Select(day=>(Day:Day(day),Month:new DateOnly(day.Year,day.Month,1))).ToArray();
            var idle=GasStorageInstrument.Compose(Guid.NewGuid(),entitlement,new(span[0].Day.Start,ExactQuantity.FromQuantity(Q("29000"))),new(Q("0"),Q("100000")),quote,rule,charges,
                [..span.Select(d=>new GasStorageContractDay(d.Day,d.Month,d.Month.AddMonths(1),new(f.C("base_forward"),f.K(2)),new(f.C("local_gas"),f.K(3))))],
                [..span.Select(d=>new GasStorageDay(d.Day,Q("0"),Q("0")))]);
            Equal(RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),f.Snapshot(),[ValuationPortfolio.FromTrades([new(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),idle,idle.UnitQuantity)])],setup)]),
                [],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings).Groups.Single().BaseValues.Single().Value,"-1000");
        });

        // Proposals: the same contract without fuel unless stated, on a lattice of 500 from the opening inventory.
        var plain=entitlement with{InjectionFuel=D("0")};
        ImmutableArray<GasStorageDayPrice> Prices(params (string Cost,string Value)[] days)=>
            [..days.Select((day,i)=>new GasStorageDayPrice(GasDay(i+1),D(day.Cost),D(day.Value)))];
        GasStorageProposal Propose(ImmutableArray<GasStorageDayPrice> prices,string opening="29000",string terminalMinimum="29000",string terminalMaximum="29000",
            string step="500",GasStorageEntitlement? contract=null,string implementation=GasStoragePlanProposals.Implementation)=>
            GasStoragePlanProposals.Propose(contract??plain,new(prices.IsDefaultOrEmpty?GasDay(1).Start:prices[0].GasDay.Start,ExactQuantity.FromQuantity(Q(opening))),
                new(Q(terminalMinimum),Q(terminalMaximum)),prices,Q(step),implementation);
        decimal Objective(GasStorageProposal proposal)=>(decimal)proposal.Objective.Numerator/(decimal)proposal.Objective.Denominator;
        void Flows(GasStorageProposal proposal,params (string Injection,string Withdrawal)[] expected)
        {
            var actual=proposal.Plan.Days.Select(d=>(d.Day.Injection.Amount.ToString(),d.Day.Withdrawal.Amount.ToString())).ToArray();
            if(!actual.SequenceEqual(expected))throw new Exception("Proposed "+string.Join(" ",actual)+", expected "+string.Join(" ",expected));
        }
        // Every plan on the lattice, tried one by one: the same contract law written again with plain decimals.
        decimal? Search((decimal Cost,decimal Value)[] prices,int day,decimal inventory,decimal step,decimal terminalMinimum,decimal terminalMaximum,decimal injectionFuel)
        {
            if(day==prices.Length)return inventory>=terminalMinimum&&inventory<=terminalMaximum?0m:null;
            var (injectionLimit,withdrawalLimit)=inventory<30000m?(2000m,500m):inventory<70000m?(1500m,1000m):(1000m,1500m);
            decimal? best=null;
            for(var k=-200;k<=200;k++)
            {
                var point=k>0?k*step/(1m-injectionFuel):-k*step;var next=inventory+k*step;
                if(k>0&&point>injectionLimit||k<0&&point>withdrawalLimit||next<0m||next>100000m)continue;
                if(Search(prices,day+1,next,step,terminalMinimum,terminalMaximum,injectionFuel) is not {} rest)continue;
                var value=rest+(k>0?-point*prices[day].Cost:point*prices[day].Value);
                if(best is null||value>best)best=value;
            }
            return best;
        }

        Check("a proposal buys the cheapest days and sells the dearest inside the ratchets, returning to its terminal inventory",()=>
        {
            var proposal=Propose(Prices(("3","3"),("3.1","3.1"),("3.5","3.5"),("3.6","3.6")));
            // Only 2,000 can come back out over the last two days, so 2,000 goes in on the cheapest day:
            // -2,000 x 3 + 1,000 x 3.5 + 1,000 x 3.6 = 1,100.
            Flows(proposal,("2000","0"),("0","0"),("0","1000"),("0","1000"));
            if(Objective(proposal)!=1100m||proposal.Implementation!=GasStoragePlanProposals.Implementation||proposal.InventoryStep!=Q("500"))throw new Exception("Expected 1,100.");
            Same(proposal.Plan.Closing.Inventory,"29000");
        });
        Check("a spread that does not cover its costs proposes no flow",()=>
        {
            var proposal=Propose(Prices(("3.3","3.2"),("3.3","3.2"),("3.3","3.2")));
            Flows(proposal,("0","0"),("0","0"),("0","0"));
            if(Objective(proposal)!=0m)throw new Exception("An idle plan has no value.");
        });
        Check("a terminal inventory above the opening is met at the least cost, and equal plans resolve to the later, smaller flow",()=>
        {
            Flows(Propose(Prices(("3.2","1"),("3","1")),terminalMinimum:"30000",terminalMaximum:"100000"),("0","0"),("1000","0"));
            if(Objective(Propose(Prices(("3.2","1"),("3","1")),terminalMinimum:"30000",terminalMaximum:"100000"))!=-3000m)throw new Exception("Expected -3,000.");
            Flows(Propose(Prices(("3","1"),("3","1")),terminalMinimum:"30000",terminalMaximum:"30000"),("0","0"),("1000","0"));
        });
        Check("in-kind fuel is paid for in the proposal and needs a step that keeps point quantities exact",()=>
        {
            // 990 of inventory is 1,000 delivered in, and lifts 29,500 into the band that lets 990 back out:
            // -1,000 x 3 + 990 x 3.5 = 465.
            var proposal=Propose(Prices(("3","3"),("3.5","3.5")),"29500","29500","29500",step:"990",contract:entitlement);
            Flows(proposal,("1000","0"),("0","990"));
            if(Objective(proposal)!=465m)throw new Exception("Expected 465.");
            Reject("gas_storage_step",()=>Propose(Prices(("3","3"),("3.5","3.5")),step:"1000",contract:entitlement));
        });
        Check("the proposal equals an exhaustive search of every lattice plan",()=>
        {
            var cases=new[]{
                (Prices:new[]{("3.4","3.3"),("2.9","2.8"),("3.1","3"),("3.8","3.7"),("3.2","3.1")},Opening:"29000",Low:"28000",High:"31000",Step:"500",Contract:plain),
                (Prices:new[]{("2","1.9"),("2.1","2"),("2.6","2.5"),("2.7","2.6"),("2.2","2.1")},Opening:"69000",Low:"68000",High:"72000",Step:"500",Contract:plain),
                (Prices:new[]{("3","2.9"),("3.4","3.3"),("2.8","2.7"),("3.6","3.5")},Opening:"29010",Low:"28020",High:"31980",Step:"990",Contract:entitlement)};
            foreach(var c in cases)
            {
                var proposal=Propose(Prices(c.Prices),c.Opening,c.Low,c.High,c.Step,c.Contract);
                var searched=Search([..c.Prices.Select(p=>(decimal.Parse(p.Item1),decimal.Parse(p.Item2)))],0,decimal.Parse(c.Opening),decimal.Parse(c.Step),
                    decimal.Parse(c.Low),decimal.Parse(c.High),c.Contract==plain?0m:0.01m)??throw new Exception("The search found no plan.");
                if(Objective(proposal)!=searched||searched<=0m)throw new Exception($"Proposed {Objective(proposal)}, searched {searched}.");
                // The proposal is itself a plan the contract admits, worth what it says.
                var replay=GasStoragePlans.Evaluate(c.Contract,proposal.Plan.Opening,proposal.Plan.Terminal,[..proposal.Plan.Days.Select(d=>d.Day)],GasStoragePlans.Implementation);
                var worth=replay.Days.Select((d,i)=>decimal.Parse(d.Day.Withdrawal.Amount.ToString())*decimal.Parse(c.Prices[i].Item2)-decimal.Parse(d.Day.Injection.Amount.ToString())*decimal.Parse(c.Prices[i].Item1)).Sum();
                if(worth!=searched)throw new Exception("The proposed plan is not worth its stated objective.");
            }
        });
        Check("a proposal refuses what it cannot meet, bound or represent",()=>
        {
            var two=Prices(("3","3"),("3.5","3.5"));
            Reject("gas_storage_infeasible",()=>Propose(two,terminalMinimum:"60000",terminalMaximum:"100000"));
            Reject("gas_storage_implementation",()=>Propose(two,implementation:"another_search_v1"));
            Reject("gas_storage_work",()=>Propose(two,step:"0.001"));
            Reject("gas_storage_step",()=>Propose(two,step:"0"));
            Reject("gas_storage_step",()=>GasStoragePlanProposals.Propose(plain,new(GasDay(1).Start,ExactQuantity.FromQuantity(Q("29000"))),new(Q("29000"),Q("29000")),two,
                new(D("500"),f.R("power_quantity")),GasStoragePlanProposals.Implementation));
            Reject("gas_storage_terminal",()=>Propose(two,terminalMinimum:"30000",terminalMaximum:"29000"));
            Reject("gas_storage_grid",()=>Propose([two[1],two[0]]));
            Reject("gas_storage_grid",()=>Propose([]));
            Reject("gas_storage_opening",()=>GasStoragePlanProposals.Propose(plain,new(GasDay(2).Start,ExactQuantity.FromQuantity(Q("29000"))),new(Q("29000"),Q("29000")),two,Q("500"),GasStoragePlanProposals.Implementation));
        });
        return passed;
    }
}
