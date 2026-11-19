using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class BasisRiskChecks
{
    public static int Run()
    {
        var f=new BasisRiskFixture(3);var portfolio=f.Portfolio(108);
        var run=RiskEngine.Calculate(f.Model,f.Snapshot,portfolio,f.Bumps,RiskEngine.NumericalPolicy);
        f.Verify(run,portfolio);
        if(run.Work.ScenarioCount!=55||run.Work.UniqueValuations!=27||run.Work.UnitPricerCalls!=27+2*66||
            run.Work.RebuiltCurveNodes!=f.Nodes+2*84)
            throw new Exception("Multi-month basis dependency reuse must have independently counted work.");
        var hedge=f.Portfolio([f.Position(0,2,BasisRiskFixture.Id(10),"100"),f.Position(1,1,BasisRiskFixture.Id(10),"-100")]);
        var hedged=RiskEngine.Calculate(f.Model,f.Snapshot,hedge,f.Bumps,RiskEngine.NumericalPolicy);f.Verify(hedged,hedge);
        if(hedged.Factors[0].Totals[0].Delta.Value!=default||hedged.Factors[1].Totals[0].Delta.Value!=default||hedged.Factors[2].Totals[0].Delta.Value.ToString()!="100")
            throw new Exception("Waha/HSC hedge must cancel common upstream gas exposure and retain the explicit inter-hub differential.");
        var independent=run.Factors[0].Positions.First(p=>p.ValuationId==f.Instruments[8].Id);
        if(independent.Delta.Value!=default)throw new Exception("An independently marked all-in Waha curve must not acquire inferred Henry Hub exposure.");
        var hedgeFactors=hedge.RiskFactors(f.Model,f.Snapshot).Single().Factors;
        if(!hedgeFactors.Select(v=>v.Input).SequenceEqual(f.Bumps.Take(3).Select(b=>b.Input)))
            throw new Exception("Book factor discovery must retain hedged upstream dependencies, exclude other months and omit unrelated power/all-in quotes.");
        var allIn=f.Portfolio([f.Position(0,8,BasisRiskFixture.Id(10),"100")]);
        if(allIn.RiskFactors(f.Model,f.Snapshot).Single().Factors.Single().Input!=f.Bumps[8].Input)
            throw new Exception("An independent all-in contract must suggest only its own observable quote.");
        void Reject(string code,Action action)
        {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        // Physical quantity and price conversions must be reciprocal. A typed
        // price shock retains its original denominator in reported derivatives.
        var mwh=f.Instruments[5].UnitQuantity.Specification;ReferenceKey R(string code)=>new(mwh.Revision,code);
        var energy=new QuantitySpecification(mwh,R("electricity"),R("energy"),R("mwh_unit"),R("energy_dimension"));
        var kwh=energy with{Key=R("kwh_quantity"),Unit=R("kwh_unit")};
        var converter=new UnitConverter([new(energy.Unit,energy.Dimension,"MWh"),new(kwh.Unit,kwh.Dimension,"kWh")],
            [new(energy.Unit,kwh.Unit,ExactDecimal.Parse("1000"),ExactDecimal.Parse("1"))],[energy,kwh]);
        var powerPrice=new UnitPrice(ExactDecimal.Parse("46.5"),f.Bumps[5].Unit);
        var amount=new Quantity(ExactDecimal.Parse("2"),mwh);
        var convertedAmount=converter.Convert(amount,kwh.Key);var convertedPrice=converter.Convert(powerPrice,kwh.Key);
        if(convertedAmount.Amount.ToString()!="2000"||convertedPrice.Value.ToString()!="0.0465"||powerPrice.Times(amount)!=convertedPrice.Times(convertedAmount))
            throw new Exception("MWh/kWh conversion must preserve exact cash with reciprocal price scaling.");
        var powerPortfolio=f.Portfolio([f.Position(0,5,BasisRiskFixture.Id(10),"2")]);
        var powerRisk=RiskEngine.Calculate(f.Model,f.Snapshot,powerPortfolio,[f.Bumps[5]],RiskEngine.NumericalPolicy).Factors[0].Totals.Single();
        if(powerRisk.Base.Amount.ToString()!="89"||powerRisk.Delta.Value.ToString()!="2"||powerRisk.UpPnl.Amount.ToString()!="0.02"||
            powerRisk.Delta.Unit.Factor.Quantity!=mwh||powerRisk.Delta.Unit.Currency!=f.Currency||powerRisk.Gamma.Unit.Order!=2)
            throw new Exception("Power risk must use external MWh once and retain currency/factor dimensions.");
        Reject("risk_position",()=>f.Portfolio([f.Position(0,5,BasisRiskFixture.Id(10),"2") with{Quantity=convertedAmount}]));
        var invalid=f.Snapshot with{ValuationAt=f.Snapshot.ValuationAt.AddTicks(1)};
        Reject("side_context",()=>new SideValuationContext(f.Model.Build(invalid,f.Requests),portfolio.Setup.Discounting!));
        Reject("side_context",()=>RiskEngine.Calculate(f.Model,invalid,portfolio,f.Bumps,RiskEngine.NumericalPolicy));
        var hidden=new HiddenDependency(f.Instruments[0]);
        var restricted=new ValuationPortfolio([hidden],[f.Position(0,0,BasisRiskFixture.Id(10),"1") with{ValuationId=hidden.Id}],portfolio.Setup);
        Reject("risk_dependency",()=>RiskEngine.Calculate(f.Model,f.Snapshot,restricted,f.Bumps,RiskEngine.NumericalPolicy));
        Console.WriteLine("PASS basis risk: four-level gas and two-level power chains, 27 factors, actual swap PV, independent all-in marks and upstream hedge cancellation");
        return 10;
    }
    private sealed record HiddenDependency(Instrument Inner):SyntheticSidedInstrument(Guid.NewGuid(),((SidedInstrument)Inner).Sides)
    {
        public override bool SupportsQuantityScaling=>true;
        public override ReferenceKey Currency=>Inner.Currency;
        public override Quantity UnitQuantity=>Inner.UnitQuantity;
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,"synthetic_missing_input_v1",[],UnitQuantity.Specification,Currency) { Time = ValuationTimeDependency.Independent };
    }

}
