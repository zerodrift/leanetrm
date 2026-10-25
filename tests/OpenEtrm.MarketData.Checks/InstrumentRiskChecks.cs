using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class InstrumentRiskChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string name,Action test){test();count++;Console.WriteLine($"PASS instrument risk: {name}");}
        void Equal(string actual,string expected){if(actual!=expected)throw new Exception($"Expected {expected}; got {actual}.");}
        void Reject(string code,Action test)
        {try{test();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception($"Expected {code}.");}
        var f=new Fixture();var model=f.Model();var snapshot=f.Snapshot();
        var gas=new CurveRequest(f.C("future"),f.K(1));
        var currency=f.R("currency");var quantity=f.R("gas_quantity");
        var unit=PriceUnit.FromQuote(f.Quotes.Single(q=>q.Key==f.R("gas_out")));
        var factor=new InputKey(f.S(1),InputUse.Variable);
        var shift=new QuoteShift(factor,f.R("gas_out"),unit,ExactDecimal.Parse("0.1"),QuoteShift.Convention);
        var bump=new QuoteBump(factor,f.R("gas_out"),unit,ExactDecimal.Parse("0.1"),QuoteBump.Convention);
        var bookA=Guid.NewGuid();var bookB=Guid.NewGuid();
        RiskPosition Hold(Instrument instrument,Guid book,string amount)=>new(Guid.NewGuid(),Guid.NewGuid(),book,instrument.Id,
            new(ExactDecimal.Parse(amount),quantity),new(new Money(default,currency),quantity),new(default,currency));
        ValuationPortfolio Portfolio(Instrument instrument,ImmutableArray<RiskPosition> positions)=>new([instrument],positions,
            new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        Check("a declared nonlinear PV model needs no risk adapter or artificial discount provider",()=>
        {
            var calls=0;
            var instrument=new PvModel(Guid.NewGuid(),currency,quantity,[gas],context=>
            {
                calls++;var p=context.Price(gas);
                return new UnitPrice(p.Value*p.Value,p.Unit).ExactTimes(new(ExactDecimal.Parse("1"),quantity));
            });
            var portfolio=Portfolio(instrument,[Hold(instrument,bookA,"2"),Hold(instrument,bookA,"-1"),Hold(instrument,bookB,"3")]);
            var basis=model.Build(snapshot,[gas]);var parent=model.WithShifts(basis,[shift]);
            var child=model.WithShifts(parent,[shift with{Size=ExactDecimal.Parse("0.2")}]);
            var prepared=RiskEngine.WithShifts(RiskEngine.WithShifts(RiskEngine.Prepare(basis,[portfolio]),[shift]),
                [shift with{Size=ExactDecimal.Parse("0.2")}]);
            var detailed=RiskEngine.CalculateExact(prepared,[bump],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
            if(calls!=3||detailed.Work.UnitPricerCalls!=3)throw new Exception("Each unit PV must be shared across every held book for each scenario.");
            if(detailed.Work.PreparationScenarioCount!=2||detailed.Work.RebuiltCurveNodes!=5||detailed.Work.ScenarioCount!=3)
                throw new Exception("Work must include both nested preparations separately from priced scenario count.");
            var group=detailed.Groups.Single();var result=group.Factors.Single();
            if(group.Detail!=RiskDetail.Holdings||group.BasePositions.Length!=3||result.Positions.Length!=3||group.Scenario.Id!=child.Scenario.Id)
                throw new Exception("Exact holding detail must retain identities, base values and the selected nested scenario.");
            foreach(var holding in portfolio.Positions)
            {
                var row=result.Positions.Single(p=>p.BindingId==holding.BindingId);
                var q=holding.Quantity.Amount;
                // Independent oracle: V(q,p)=q*p^2 at p=3.3 and p+-0.1.
                var expectedBase=ExactMoney.FromMoney(new(ExactDecimal.Parse("10.89")*q,currency));
                var expectedUp=ExactMoney.FromMoney(new(ExactDecimal.Parse("11.56")*q,currency));
                var expectedDown=ExactMoney.FromMoney(new(ExactDecimal.Parse("10.24")*q,currency));
                if(row.Values.Base!=expectedBase||row.Values.Up!=expectedUp||row.Values.Down!=expectedDown||
                    group.BasePositions.Single(p=>p.BindingId==holding.BindingId).Value!=expectedBase)
                    throw new Exception("Holding cash must equal independent full repricing without decimal projection.");
                var expectedDelta=ExactMoney.FromMoney(new(q*ExactDecimal.Parse("6.6"),currency));
                if(row.Values.Delta.Value!=expectedDelta)throw new Exception("Nonlinear delta must retain the externally applied holding quantity.");
                var expectedGamma=ExactMoney.FromMoney(new(q*ExactDecimal.Parse("2"),currency));
                Equal(row.Values.Gamma.Value.Numerator,expectedGamma.Numerator);Equal(row.Values.Gamma.Value.Denominator,expectedGamma.Denominator);
            }
            var summary=RiskEngine.CalculateExact(prepared,[bump],RiskEngine.ExactNumericalPolicy);
            if(!summary.Groups.Single().Factors.Single().Totals.SequenceEqual(result.Totals)||
                !summary.Groups.Single().BasePositions.IsEmpty||!summary.Groups.Single().Factors.Single().Positions.IsEmpty)
                throw new Exception("Exact holding and summary outputs must reconcile without materializing unused detail.");
            var baseOnly=RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
            if(baseOnly.Groups.Single().BasePositions.Length!=3||baseOnly.Work.ScenarioCount!=1)
                throw new Exception("Base-only exact calculations must retain all requested holding values.");
            Equal(parent.Values.Single().Price.Value.ToString(),"3.1");Equal(basis.Values.Single().Price.Value.ToString(),"3");
            Reject("risk_detail",()=>RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy,detail:(RiskDetail)99));
            var otherMeasure=new ValuationPortfolio([instrument],portfolio.Positions,
                new(ValuationMeasure.ExecutionPnl,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
            Reject("valuation_measure",()=>RiskEngine.CalculateExact(RiskEngine.Prepare(model,snapshot,[otherMeasure]),[],RiskEngine.ExactNumericalPolicy));
        });
        Check("PV models cannot read undeclared prices or return another scenario's valuation",()=>
        {
            var hidden=new PvModel(Guid.NewGuid(),currency,quantity,[],context=>context.Price(gas).ExactTimes(new(ExactDecimal.Parse("1"),quantity)));
            Reject("risk_dependency",()=>RiskEngine.CalculateExact(RiskEngine.Prepare(model,snapshot,[Portfolio(hidden,[Hold(hidden,bookA,"1")])]),[],RiskEngine.ExactNumericalPolicy));
            var stale=new PvModel(Guid.NewGuid(),currency,quantity,[gas],context=>context.Price(gas).ExactTimes(new(ExactDecimal.Parse("1"),quantity))){ReuseResult=true};
            Reject("instrument_context",()=>RiskEngine.CalculateExact(RiskEngine.Prepare(model,snapshot,[Portfolio(stale,[Hold(stale,bookA,"1")])]),[bump],RiskEngine.ExactNumericalPolicy));
        });
        Check("rational holding detail preserves fixed actuals and projected swap sensitivity under nested stress",()=>
        {
            var fixing=new IndexFixingChecks.FixingFixture();var swap=fixing.Swap();var pinned=fixing.Snapshot(fixing.Fixings[1].FixingAt,actuals:2);
            var original=fixing.Model.Build(pinned,swap.DependenciesAt(pinned));var live=fixing.Model.QuoteFactors(pinned).Single();
            var move=new QuoteShift(live.Input,live.Quote,live.Unit,ExactDecimal.Parse("1"),QuoteShift.Convention);
            var stressed=fixing.Model.WithShifts(fixing.Model.WithShifts(original,[move]),[move]);
            var held=new RiskPosition(Guid.NewGuid(),Guid.NewGuid(),bookA,swap.Id,new(ExactDecimal.Parse("1"),swap.UnitQuantity.Specification),
                new(new Money(default,swap.Currency),swap.UnitQuantity.Specification),new(default,swap.Currency));
            var portfolio=new ValuationPortfolio([swap],[held],new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
                new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
            var missingDiscount=new ValuationPortfolio([swap],[held],portfolio.Setup with{Discounting=null});
            Reject("side_context",()=>RiskEngine.CalculateExact(RiskEngine.Prepare(stressed,[missingDiscount]),[],RiskEngine.ExactNumericalPolicy));
            var run=RiskEngine.CalculateExact(RiskEngine.Prepare(stressed,[portfolio]),[new(live.Input,live.Quote,live.Unit,live.TradingTick,QuoteBump.Convention)],
                RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
            var values=run.Groups.Single().Factors.Single().Positions.Single().Values;
            Equal(values.Base.Numerator,"20");Equal(values.Base.Denominator,"3");
            Equal(values.Delta.Value.Numerator,"1");Equal(values.Delta.Value.Denominator,"3");
            if(values!=run.Groups.Single().Factors.Single().Totals.Single().Values)throw new Exception("One holding must reconcile exactly with its book.");
            Equal(string.Join(",",stressed.Snapshot.Selections.Where(s=>s.Input.Use==InputUse.FrozenFact).Select(s=>s.Observation.Value.ToString())),"10,20");
        });
        Console.WriteLine($"{count} instrument risk checks passed.");return count;
    }
    private sealed record PvModel:Instrument
    {
        public override bool SupportsQuantityScaling=>true;
        public override ReferenceKey Currency{get;}
        public override Quantity UnitQuantity{get;}
        private readonly ImmutableArray<CurveRequest> dependencies;
        private readonly Func<SideValuationContext,ExactMoney> price;
        private InstrumentValuation? previous;
        public bool ReuseResult{get;init;}
        public PvModel(Guid id,ReferenceKey currency,ReferenceKey quantity,ImmutableArray<CurveRequest> dependencies,Func<SideValuationContext,ExactMoney> price):base(id)
        {Currency=currency;UnitQuantity=new(ExactDecimal.Parse("1"),quantity);this.dependencies=dependencies;this.price=price;}
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,"synthetic_nonlinear_pv_v1",dependencies,UnitQuantity.Specification,Currency);
        public override InstrumentValuation PV(SideValuationContext context)
        {
            if(ReuseResult&&previous is not null)return previous;
            return previous=new(this,context,price(context),"synthetic_nonlinear_pv_v1");
        }
    }
}
