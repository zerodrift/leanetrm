using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class DerivativeCapabilityChecks
{
    internal static void Run()
    {
        var f=new Fixture();var source=f.Snapshot();var market=new CurveRequest(f.C("future"),f.K(1));var unit=PriceUnit.FromQuote(f.Quotes[0]);
        var instrument=new ValueOnly(Guid.NewGuid(),market,unit,true);
        ValuationPortfolio Portfolio(ValueOnly value)=>ValuationPortfolio.FromTrades([new(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),value,new(ExactDecimal.Parse("2"),unit.Quantity))])],
            new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        var portfolio=Portfolio(instrument);var native=RiskEngine.Prepare(f.Model(),source,[portfolio]);
        var built=f.Model().Build(source,[market]);var connected=RiskEngine.PrepareConnected(built.Connected,built.PricingBindings,[portfolio],built);
        if(RiskEngine.CalculateExact(native,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value.ToExactString()!="6")
            throw new Exception("The selected value-only model lost ordinary PV.");
        var shift=new CurveShift(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(market.Curve,market.Contract),unit,ExactDecimal.Parse("1"),CurveShift.ParentAdditive);
        var scenario=new ConnectedRiskScenario("up","Explicit full PV",[shift]);
        var named=RiskEngine.CalculateScenarios(connected,new("value_only","1",ConnectedRiskScenarioSet.Independent,[scenario]),RiskEngine.ExactNumericalPolicy);
        if(named.Scenarios.Single().Groups.Single().Totals.Single().Values.Value.ToExactString()!="8")throw new Exception("A model derivative restriction blocked full PV scenarios.");
        var bump=new ConnectedRiskBump(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(market.Curve,market.Contract),unit,ExactDecimal.Parse("1"),ConnectedRiskBump.Convention);
        var quote=new QuoteBump(new(f.S(1),InputUse.Variable),f.Quotes[0].Key,unit,ExactDecimal.Parse("1"),QuoteBump.Convention);
        Reject(()=>RiskEngine.CalculateExact(native,[quote],RiskEngine.ExactNumericalPolicy));
        Reject(()=>RiskEngine.Calculate(native,[quote],RiskEngine.NumericalPolicy));
        Reject(()=>RiskEngine.CalculateExact(connected,[bump],RiskEngine.ExactNumericalPolicy));
        var tolerance=new FiniteDifferenceTolerance(default,default);
        Reject(()=>RiskEngine.CalculateFiniteDifferences(connected,new(ConnectedFiniteDifferenceRequest.HalfNominalDouble,
            [new("axis",bump,tolerance,tolerance)],[]),RiskEngine.ExactNumericalPolicy));
        var onlyNamed=RiskEngine.CalculateFiniteDifferences(connected,new(ConnectedFiniteDifferenceRequest.HalfNominalDouble,[],[]){AdditionalScenarios=[scenario]},RiskEngine.ExactNumericalPolicy);
        if(!onlyNamed.Derivatives.IsEmpty)throw new Exception("A named-only request invented derivatives.");
        var legacy=Portfolio(instrument with{Restricted=false}).Declare(source).Single();
        if(JsonSerializer.Serialize(legacy).Contains("StaticDerivatives",StringComparison.Ordinal)||
            !JsonSerializer.Serialize(portfolio.Declare(source).Single()).Contains(StaticDerivativeRestriction.Version,StringComparison.Ordinal))
            throw new Exception("The selected restriction must be visible without changing legacy contract bytes.");
        var qualified=RiskEngine.Prepare(f.Model(),source,[Portfolio(instrument with{Restricted=false})]);
        if(RiskEngine.CalculateExact(qualified,[quote],RiskEngine.ExactNumericalPolicy).Groups.Single().Factors.Single().Totals.Single().Values.Delta.Value.ToExactString()!="2")
            throw new Exception("An established qualified model lost its static derivative contract.");
        var reusablePortfolio=new ValuationPortfolio([instrument with{Restricted=false}],portfolio.Positions,portfolio.Setup);
        var reusable=RiskEngine.PrepareConnected(built.Connected,built.PricingBindings,[reusablePortfolio],built);
        var existing=RiskEngine.CalculateFiniteDifferences(reusable,new(ConnectedFiniteDifferenceRequest.HalfNominalDouble,
            [new("axis",bump,tolerance,tolerance)],[]),RiskEngine.ExactNumericalPolicy);
        Reject(()=>PnlGreekRun.Capture(connected,existing));
        Reject(()=>RiskEngine.RequireStaticDerivatives([legacy with{StaticDerivatives=new("unknown","reason")}]),"risk_contract");
        Console.WriteLine("PASS derivative capability: ordinary/named full PV retained, unqualified exact and multi-step static derivatives refused");
    }
    private static void Reject(Action run,string code="risk_static_derivatives_unavailable")
    {try{run();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected model refusal "+code);}
    private sealed record ValueOnly(Guid Key,CurveRequest Market,PriceUnit Unit,bool Restricted):Instrument(Key)
    {
        public override bool SupportsQuantityScaling=>true;
        public override ReferenceKey Currency=>Unit.Currency;
        public override Quantity UnitQuantity=>new(ExactDecimal.Parse("1"),Unit.Quantity);
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,"synthetic_value_only_v1",[Market],Unit.Quantity,Unit.Currency)
            {Time=ValuationTimeDependency.Independent,StaticDerivatives=Restricted?new(StaticDerivativeRestriction.Version,"synthetic_model_derivatives_unqualified"):null};
        public override UnitValuation PV(UnitValuationContext context)=>new(context,context.Inputs.ExactPrice(Market).ExactTimes(UnitQuantity),Unit.Quantity);
    }
}
