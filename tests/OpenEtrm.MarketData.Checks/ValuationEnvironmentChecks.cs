using OpenEtrm.MarketData;
using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;

internal static class ValuationEnvironmentChecks
{
    internal static void Run()
    {
        var ny=PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        var friday=new DateTimeOffset(2026,3,7,21,0,0,TimeSpan.Zero);
        var next=ClockChange.NextCalendarDay(friday,ny);
        if(next.At!=friday.AddHours(23)||next.Calendar is null)throw new Exception("A DST calendar day was treated as 24 elapsed hours.");
        Reject("clock_rule",()=>ClockChange.NextCalendarDay(new(2026,3,7,7,30,0,TimeSpan.Zero),ny));
        Reject("clock_rule",()=>ClockChange.NextCalendarDay(new(2026,10,31,5,30,0,TimeSpan.Zero),ny));
        var seed=new JointScenarioRiskChecks.Fixture((x,_)=>x);
        var source=seed.Prepared.Scenario.Plan.Context.ValuationAt;
        var end=source.AddDays(5);
        var unit=seed.Unit.Currency;
        // Independent polynomial oracle: price + 3 * elapsed days, quantity applied by book.
        JointScenarioRiskChecks.Fixture? f=null;
        f=new JointScenarioRiskChecks.Fixture((x,_)=>x,"2",repeat:true,unaffected:true,
            contextualPrice:c=>ExactMoney.Parse((ExactDecimal.Parse("3")*
                ExactDecimal.Parse(((c.ValuationAt-source).Ticks/TimeSpan.TicksPerDay).ToString())).ToString(),f!.Unit.Currency),
            time:ValuationTimeDependency.Between(source,end));
        var original=f.Prepared.Valuation.Snapshot;
        var calendar=ClockChange.NextCalendarDay(source,ny);
        var calendarChild=RiskEngine.WithClock(f.Prepared,calendar);
        if(calendarChild.Scenario.CalculationAt!=calendar.At)
            throw new Exception("A retained IANA calendar roll failed to reach the next local day.");
        Reject("scenario_clock",()=>RiskEngine.WithClock(f.Prepared,calendar with{
            Calendar=calendar.Calendar! with{Zone="UTC"}}));
        Reject("scenario_clock",()=>RiskEngine.WithClock(f.Prepared,calendar with{
            Calendar=calendar.Calendar! with{Fingerprint=new string('0',64)}}));
        Reject("scenario_clock",()=>RiskEngine.WithClock(f.Prepared,calendar with{
            Calendar=calendar.Calendar! with{Rules=calendar.Calendar.Rules+"invalid"}}));
        var child=RiskEngine.WithClock(f.Prepared,new(source.AddDays(1),ClockChange.FrozenInputs));
        if(!ReferenceEquals(original,child.Valuation.Snapshot)||child.Scenario.Plan!=f.Prepared.Scenario.Plan)
            throw new Exception("A clock layer changed its retained source snapshot or plan.");
        var sample=new ConnectedRiskScenario("next_day","Next day",[]){Clock=new(source.AddDays(1),ClockChange.FrozenInputs)};
        var run=RiskEngine.CalculateScenarios(f.Prepared,f.Set(sample),RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
        var group=run.Scenarios.Single().Groups.Single();
        Equal(group.Totals.Single().Values.Pnl,"12");
        if(group.UnitPricerCalls!=1||group.ReusedUnitValues!=1)throw new Exception("Clock dependencies did not preserve fungible/unaffected unit reuse.");
        if(run.Scenarios[0].Scenario.CalculationAt!=source.AddDays(1)||run.Scenarios[0].Scenario.Clock!=sample.Clock)
            throw new Exception("Clock lineage is absent from the scenario receipt.");
        var replay=RiskEngine.WithClock(f.Prepared,sample.Clock!);
        if(child.Scenario.Id!=replay.Scenario.Id||child.Scenario.Id==f.Prepared.Scenario.Id)throw new Exception("Clock identity is not deterministic and distinct.");
        var direct=RiskEngine.WithClock(f.Prepared,new(source.AddDays(2),ClockChange.FrozenInputs));
        var nested=RiskEngine.WithClock(child,new(source.AddDays(2),ClockChange.FrozenInputs));
        Equal(Base(direct),Base(nested).ToExactString());
        Equal(Base(f.Prepared),"8");
        Reject("scenario_clock",()=>RiskEngine.WithClock(f.Prepared,new(source.AddDays(-1),ClockChange.FrozenInputs)));
        Reject("scenario_clock",()=>RiskEngine.WithClock(f.Prepared,new(source.AddDays(1),"unknown")));
        Reject("valuation_time_coverage",()=>Base(RiskEngine.WithClock(f.Prepared,new(end,ClockChange.FrozenInputs))));
        var pinned=new JointScenarioRiskChecks.Fixture((x,_)=>x,time:ValuationTimeDependency.Pinned);
        Reject("valuation_time_unsupported",()=>Base(RiskEngine.WithClock(pinned.Prepared,sample.Clock!)));
        var undeclared=new JointScenarioRiskChecks.Fixture((x,_)=>x,contextualPrice:c=>ExactMoney.Parse(c.ValuationAt.Year.ToString(),unit));
        Reject("risk_dependency",()=>Base(undeclared.Prepared));
        var vol=new VolatilityInput(Guid.NewGuid(),"0.2",VolatilityInput.Annualized);
        JointScenarioRiskChecks.Fixture? v=null;
        v=new JointScenarioRiskChecks.Fixture((x,_)=>x,"3",repeat:true,unaffected:true,
            contextualPrice:c=>ExactMoney.Parse((c.Volatility(vol.Id)*c.Volatility(vol.Id)).Format(18,MidpointRounding.ToEven),v!.Unit.Currency),volatility:[vol]);
        var volSample=new ConnectedRiskScenario("vol_up","Volatility up",[]){Volatility=[new(vol.Id,ExactDecimal.Parse("0.1"),VolatilityShift.ParentAdditive)]};
        var volRun=RiskEngine.CalculateScenarios(v.Prepared,v.Set(volSample),RiskEngine.ExactNumericalPolicy);
        Equal(volRun.Scenarios.Single().Groups.Single().Totals.Single().Values.Pnl,"0.3");
        if(volRun.Scenarios.Single().Groups.Single().UnitPricerCalls!=1)throw new Exception("A vol shift did not reprice exactly one shared unit.");
        Reject("scenario_volatility",()=>RiskEngine.CalculateScenarios(v.Prepared,v.Set(volSample with{Volatility=[new(vol.Id,ExactDecimal.Parse("-0.3"),VolatilityShift.ParentAdditive)]}),RiskEngine.ExactNumericalPolicy));
        Reject("risk_dependency",()=>RiskEngine.CalculateScenarios(v.Prepared,v.Set(volSample with{Volatility=[new(Guid.NewGuid(),ExactDecimal.Parse("0.1"),VolatilityShift.ParentAdditive)]}),RiskEngine.ExactNumericalPolicy));
        var fd=RiskEngine.CalculateFiniteDifferences(v.Prepared,new(ConnectedFiniteDifferenceRequest.HalfNominalDouble,
            [new("iv",new VolatilitySensitivityInput([new(vol.Id,ExactDecimal.Parse("1"))],ExactDecimal.Parse("0.01"),VolatilitySensitivityInput.Convention),
                new(default,default),new(default,default))],[]),RiskEngine.ExactNumericalPolicy);
        Equal(fd.Derivatives.Single(d=>d.Formula==RiskEngine.CentralDelta).Groups.Single().Totals.Single().Estimate.Nominal,"2.4");
        Equal(fd.Derivatives.Single(d=>d.Formula==RiskEngine.CentralGamma).Groups.Single().Totals.Single().Estimate.Nominal,"12");
        if(fd.Derivatives.Any(d=>d.FactorUnits.Any(u=>u!=ScenarioFactorUnit.AnnualizedVolatility)))throw new Exception("Vega has incorrect native units.");
        var combined=RiskEngine.CalculateFiniteDifferences(f.Prepared,JointScenarioRiskChecks.Derivatives(f,mixed:false) with{
            AdditionalScenarios=[sample]},RiskEngine.ExactNumericalPolicy);
        if(combined.Repricing.Scenarios.Length!=combined.Request.SampleCount)
            throw new Exception("Pre-pricing admission did not count every FD and clock sample.");
        Equal(combined.Repricing.Scenarios.Single(s=>s.Id==sample.Id).Groups.Single().Totals.Single().Values.Pnl,"12");
        var deep=f.Prepared.Scenario;
        for(var n=0;n<64;n++)deep=n%2==0?deep.WithClock(new(source.AddDays(1),ClockChange.FrozenInputs)):deep.WithShifts([f.Shift(0,"0.01")]);
        Reject("scenario_depth",()=>deep.WithClock(new(source.AddDays(1),ClockChange.FrozenInputs)));
        Reject("scenario_depth",()=>deep.WithShifts([f.Shift(0,"0.01")]));
        var witnessed=f.Prepared.Scenario.WithClock(ClockChange.NextCalendarDay(source,PowerClock.Capture(TimeZoneInfo.Utc)));
        Reject("scenario_clock",()=>f.Prepared.Scenario.WithClock(witnessed.Clock! with{At=source.AddDays(2)}));
        var cold=new JointScenarioRiskChecks.Fixture((x,_)=>x);
        var coldClock=RiskEngine.WithClock(cold.Prepared,new(source.AddDays(1),ClockChange.FrozenInputs));
        var coldRun=RiskEngine.CalculateExact(coldClock,[],RiskEngine.ExactNumericalPolicy);
        if(coldRun.GraphWork.EvaluatedNodes==0)throw new Exception("A cold clock child hid its source graph work.");
        var directValuation=f.Prepared.Valuation.WithScenario(f.Prepared.Scenario.WithLayer([],null,[new(Guid.NewGuid(),ExactDecimal.Parse("0.1"),VolatilityShift.ParentAdditive)]));
        var directBindings=f.Bindings.Where(p=>f.Requests.Take(2).Contains(p.Key)).ToImmutableDictionary();
        Reject("risk_dependency",()=>directValuation.PV(f.Portfolio.Instruments[0],directBindings));
        var side=new ClockSide(Guid.NewGuid(),f.Unit.Currency,new(ExactDecimal.Parse("1"),f.Unit.Quantity));
        var first=new ClockInstrument(Guid.NewGuid(),side,ValuationTimeDependency.Between(source,end));
        var second=new ClockInstrument(Guid.NewGuid(),side,ValuationTimeDependency.Between(source,end));
        ValuationPortfolio SidePortfolio(ClockInstrument other)=>ValuationPortfolio.FromTrades([
            new Trade(Guid.NewGuid(),f.Book,[new(Guid.NewGuid(),first,first.UnitQuantity)]),
            new Trade(Guid.NewGuid(),f.Book,[new(Guid.NewGuid(),other,other.UnitQuantity)])],
            new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        var sidePrepared=RiskEngine.PrepareConnected(f.Prepared.Valuation,ImmutableDictionary<CurveRequest,CurvePointKey>.Empty,[SidePortfolio(second)]);
        Equal(Base(RiskEngine.WithClock(sidePrepared,new(source.AddDays(1),ClockChange.FrozenInputs))),"4");
        var dishonest=new ClockInstrument(Guid.NewGuid(),side,ValuationTimeDependency.Independent);
        var dishonestPrepared=RiskEngine.PrepareConnected(f.Prepared.Valuation,ImmutableDictionary<CurveRequest,CurvePointKey>.Empty,[SidePortfolio(dishonest)]);
        Reject("risk_dependency",()=>Base(dishonestPrepared));
        Equal(f.Prepared.Valuation.WithScenario(f.Prepared.Scenario.WithClock(new(source.AddDays(1),ClockChange.FrozenInputs)))
            .PV(first,ImmutableDictionary<CurveRequest,CurvePointKey>.Empty).PresentValue,"2");
        Console.WriteLine("PASS immutable valuation clock layers, complete time dependencies, exact receipts and unit reuse.");
    }
    private sealed record ClockSchedule(Guid Id,ReferenceKey Currency,Quantity Unit):SideSchedule(Id,Currency,Unit,"clock_probe_v1")
    {
        public override ImmutableArray<CurveRequest> Dependencies=>[];
        public override IReadOnlyList<ScheduleRow> Rows=>[];
        public override SideValuation Evaluate(SideValuationContext context)=>new ClockValue(this,context,ExactMoney.Parse(context.Inputs.ValuationAt.Day.ToString(),Currency));
    }
    private sealed record ClockValue(SideSchedule Own,SideValuationContext ContextValue,ExactMoney Amount):SideValuation(Own,ContextValue,Amount)
    {
        public override SideScheduleSnapshot InspectSchedule(string manifest,ExactDecimal scale,OpenEtrm.Shared.Numerics.SignedRatio coefficient,Func<DateOnly,DateTimeOffset> boundary)=>
            throw new MarketDataException("clock_probe","This check values an explicit clock-dependent Side.");
    }
    private sealed record ClockSide(Guid Identity,ReferenceKey Currency,Quantity Unit):Side(Identity)
    {
        public override ImmutableArray<CurveRequest> Dependencies=>[];
        public override SideSchedule GenerateSchedule()=>new ClockSchedule(Id,Currency,Unit);
        public override SideValuation PV(SideValuationContext context)=>GenerateSchedule().Evaluate(context);
    }
    private sealed record ClockInstrument:SidedInstrument
    {
        private readonly ValuationTimeDependency time;
        public override ReferenceKey Currency{get;}
        public override Quantity UnitQuantity{get;}
        internal ClockInstrument(Guid id,ClockSide side,ValuationTimeDependency time):base(id,[new(side,ExactDecimal.Parse("1"))])
        {Currency=side.Currency;UnitQuantity=side.Unit;this.time=time;}
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,"clock_side_probe_v1",[],UnitQuantity.Specification,Currency){Time=time};
    }
    private static ExactMoney Base(PreparedConnectedPortfolioRisk p)=>RiskEngine.CalculateExact(p,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value;
    private static void Equal(ExactMoney actual,string expected)
    {if(actual.CompareTo(ExactMoney.Parse(expected,actual.Currency))!=0)throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
