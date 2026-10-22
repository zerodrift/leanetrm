using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;

internal static class BookValuationChecks
{
    public static int Run()
    {
        var f=new Fixture();var model=f.Model();var snapshot=f.Snapshot();
        var request=new CurveRequest(f.C("future"),f.K(1));
        var calls=0;
        var instrument=new Probe(Guid.NewGuid(),f.R("currency"),f.R("gas_quantity"),[request],inputs=>
        {calls++;return inputs.Price(request).ExactTimes(new Quantity(ExactDecimal.Parse("1"),f.R("gas_quantity")));});
        var a=Guid.NewGuid();var b=Guid.NewGuid();
        RiskPosition Position(Guid book,string quantity)=>new(Guid.NewGuid(),Guid.NewGuid(),book,instrument.Id,
            new(ExactDecimal.Parse(quantity),instrument.UnitQuantity.Specification),
            new(new Money(default,instrument.Currency),instrument.UnitQuantity.Specification),new(default,instrument.Currency));
        var portfolio=new ValuationPortfolio([instrument],[Position(a,"2"),Position(a,"-1"),Position(b,"3")],new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        try
        {
            _=new ValuationPortfolio([instrument],[Position(a,"1") with{Coverage=new(snapshot.ValuationAt,snapshot.ValuationAt.AddDays(1))}],portfolio.Setup);
            throw new Exception("A legacy instrument must not silently ignore selected delivery coverage.");
        }
        catch(MarketDataException e)when(e.Code=="ppa_segment_coverage"){}
        var bump=new QuoteBump(new(f.S(1),InputUse.Variable),f.R("gas_out"),PriceUnit.FromQuote(f.Quotes.Single(q=>q.Key==f.R("gas_out"))),ExactDecimal.Parse("0.1"),QuoteBump.Convention);
        var reported=portfolio.RiskFactors(model,snapshot);
        if(instrument.FactorDeclarations!=1||reported.Length!=2||reported.Any(b=>b.Factors.Length!=1||b.Factors[0].Input!=bump.Input))
            throw new Exception("Books must ask each shared instrument for its standardized market factor list once.");
        var valid=reported[0].Factors[0];var extra=model.QuoteFactors(snapshot).First(factor=>factor.Input!=valid.Input);
        foreach(var invalid in new ImmutableArray<QuoteFactor>[] {[],[valid,extra],[valid with{Observation=new(f.Revision.CatalogId,Guid.NewGuid())}],[valid with{Quote=f.R("different_quote")} ]})
        {
            var dishonest=new Probe(Guid.NewGuid(),instrument.Currency,instrument.UnitQuantity.Specification,[request],_=>throw new Exception("Discovery must not price instruments."),_=>invalid);
            var fenced=new ValuationPortfolio([dishonest],[Position(a,"1") with{ValuationId=dishonest.Id},],portfolio.Setup);
            try{fenced.RiskFactors(model,snapshot);throw new Exception("A factor declaration cannot omit, add or reinterpret pinned inputs.");}
            catch(MarketDataException e)when(e.Code=="risk_contract"){}
        }
        var result=RiskEngine.Calculate(model,snapshot,portfolio,[bump],RiskEngine.NumericalPolicy);
        if(calls!=3||result.Work.UniqueValuations!=1||result.Work.UnitPricerCalls!=3)
            throw new Exception("One shared instrument must value once per base/up/down across both books.");
        var rows=result.Factors.Single().Positions;
        foreach(var row in rows)
        {
            var held=portfolio.Positions.Single(p=>p.BindingId==row.BindingId);
            if(row.Delta.Value!=held.Quantity.Amount||row.UpPnl.Amount!=held.Quantity.Amount*ExactDecimal.Parse("0.1"))
                throw new Exception("External signed quantities must be applied independently after shared unit valuation.");
        }
        var summary=RiskEngine.Calculate(model,snapshot,portfolio,[bump],RiskEngine.NumericalPolicy,detail:RiskDetail.Books);
        if(summary.Work.QuantityApplications!=6||summary.Work.RepeatedInstrumentReferences!=2||!summary.Factors[0].Positions.IsEmpty||!summary.Factors[0].Totals.SequenceEqual(result.Factors[0].Totals))
            throw new Exception("Explicit book-only risk must preserve the full report totals without holding-row materialization.");
        var otherCalls=0;
        var other=new Probe(Guid.NewGuid(),instrument.Currency,instrument.UnitQuantity.Specification,[request],inputs=>
        {otherCalls++;return inputs.Price(request).ExactTimes(new Quantity(ExactDecimal.Parse("2"),instrument.UnitQuantity.Specification));});
        var otherPortfolio=new ValuationPortfolio([other],[Position(a,"4") with{ValuationId=other.Id}],portfolio.Setup);
        var prepared=RiskEngine.Prepare(model,snapshot,[portfolio,otherPortfolio]);
        if(prepared.Factors.Length!=1||prepared.Factors[0].Input!=bump.Input||otherCalls!=0)
            throw new Exception("A shared risk plan must discover only declared factors without calling a pricer.");
        var before=calls;
        var groups=RiskEngine.Calculate(prepared,[bump],RiskEngine.NumericalPolicy,detail:RiskDetail.Books);
        if(calls-before!=3||otherCalls!=3||groups.Groups.Length!=2||groups.Work.UnitPricerCalls!=6||
            groups.Work.RebuiltCurveNodes!=summary.Work.RebuiltCurveNodes||groups.Work.ReusedCurveNodes!=summary.Work.ReusedCurveNodes||
            groups.Groups[0].BaseValues.Single(v=>v.BookId==a).Value.ToMoney().Amount.ToString()!="3"||
            groups.Groups[1].BaseValues.Single().Value.ToMoney().Amount.ToString()!="24"||
            groups.Groups[1].Factors.Single().Totals.Single().Delta.Value.ToString()!="8")
            throw new Exception("Different valuation groups must share curve scenarios and retain independently scaled base values and risk.");
        var noBumps=RiskEngine.Calculate(prepared,[],RiskEngine.NumericalPolicy,detail:RiskDetail.Books);
        if(!noBumps.Groups[0].Factors.IsEmpty||noBumps.Work.ScenarioCount!=1||noBumps.Groups[1].BaseValues.Single().Value.ToMoney().Amount.ToString()!="24")
            throw new Exception("A base-only report must retain book values even when there are no selected live shocks.");
        try{RiskEngine.Prepare(model,snapshot,[portfolio,portfolio]);throw new Exception("The same unit and holding cannot be counted in two measures.");}
        catch(MarketDataException e)when(e.Code=="risk_contract"){}
        var fractional=new Probe(Guid.NewGuid(),instrument.Currency,instrument.UnitQuantity.Specification,[request],inputs=>
            ExactMoney.FromMoney(new(inputs.Price(request).Value.CompareTo(ExactDecimal.Parse("3")) switch
                {0=>default,>0=>ExactDecimal.Parse("0.1"),_=>ExactDecimal.Parse("-0.1")},instrument.Currency)));
        var fractionalPortfolio=new ValuationPortfolio([fractional],[Position(a,"1") with{ValuationId=fractional.Id}],portfolio.Setup);
        var exact=RiskEngine.CalculateExact(RiskEngine.Prepare(model,snapshot,[fractionalPortfolio]),[bump with{Size=ExactDecimal.Parse("0.3")}],RiskEngine.ExactNumericalPolicy);
        var fractionalValues=exact.Groups.Single().Factors.Single().Totals.Single().Values;
        if(fractionalValues.Base.Numerator!="0"||fractionalValues.Base.Denominator!="1"||
            fractionalValues.Up.Numerator!="1"||fractionalValues.Up.Denominator!="10"||
            fractionalValues.Down.Numerator!="-1"||fractionalValues.Down.Denominator!="10"||
            fractionalValues.Delta.Value.Numerator!="1"||fractionalValues.Delta.Value.Denominator!="3"||fractionalValues.Gamma.Value.Numerator!="0")
            throw new Exception("Exact book repricing must retain nonterminating cash and sensitivities without changing external quantity or rounding.");
        var cashCalls=0;
        var cash=new Probe(Guid.NewGuid(),instrument.Currency,instrument.UnitQuantity.Specification,[],_=>
        {cashCalls++;return ExactMoney.FromMoney(new(ExactDecimal.Parse("7"),instrument.Currency));});
        var cashPosition=Position(a,"2") with{ValuationId=cash.Id};
        var cashRisk=RiskEngine.Calculate(model,snapshot,new ValuationPortfolio([cash],[cashPosition],
            new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)),[bump],RiskEngine.NumericalPolicy);
        if(cashCalls!=1||cashRisk.Factors[0].Positions[0].Base.Amount.ToString()!="14"||cashRisk.Factors[0].Positions[0].Delta.Value!=default)
            throw new Exception("An explicit market-independent unit instrument must value once and retain zero risk.");
        foreach(var large in new[]{true,false})
        {
            var offset=new Probe(Guid.NewGuid(),instrument.Currency,instrument.UnitQuantity.Specification,[request],inputs=>
                ExactMoney.FromMoney(new(large?ExactDecimal.Parse("900000000000000000000000"):
                    inputs.Price(request).Value.CompareTo(ExactDecimal.Parse("3"))>0?ExactDecimal.Parse("0.000000000000000001"):default,instrument.Currency)));
            var offsetPortfolio=new ValuationPortfolio([offset],[Position(a,"2") with{ValuationId=offset.Id},Position(a,"-2") with{ValuationId=offset.Id}],
                new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
            var offsetRisk=RiskEngine.Calculate(model,snapshot,offsetPortfolio,[bump with{Size=ExactDecimal.Parse("0.3")}],RiskEngine.NumericalPolicy,detail:RiskDetail.Books);
            var total=offsetRisk.Factors[0].Totals.Single();
            if(total.Base.Amount!=default||total.Up.Amount!=default||total.Down.Amount!=default||total.Delta.Value!=default||total.Gamma.Value!=default)
                throw new Exception("Book-only exact offsets must aggregate before bounded amount/derivative projection.");
        }
        var small=new Probe(Guid.NewGuid(),instrument.Currency,instrument.UnitQuantity.Specification,[],
            _=>ExactMoney.FromMoney(new(ExactDecimal.Parse("0.000000000000000001"),instrument.Currency)));
        var netLarge=new ValuationPortfolio([small],[Position(a,"900000000000000000000000") with{ValuationId=small.Id},Position(a,"900000000000000000000000") with{ValuationId=small.Id}],
            new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        var largeSummary=RiskEngine.Calculate(model,snapshot,netLarge,[bump],RiskEngine.NumericalPolicy,detail:RiskDetail.Books);
        if(largeSummary.Factors[0].Totals[0].Base.Amount.ToString()!="1800000"||largeSummary.Work.QuantityApplications!=1)
            throw new Exception("Net quantity aggregation must remain exact beyond the working decimal range and scale once per book/unit.");
        using(var cancelled=new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                RiskEngine.Calculate(model,snapshot with{Definitions=default},portfolio,[bump],RiskEngine.NumericalPolicy,cancelled.Token);
                throw new Exception("A pre-cancelled risk operation must stop before reading market inputs.");
            }
            catch(OperationCanceledException){}
        }
        var covered=new PpaAcceptanceFixture();var originalPpa=covered.Physical();var amendedPpa=covered.Physical("50");
        var coveredTrade=Guid.NewGuid();var coveredBook=Guid.NewGuid();var split=covered.From.AddHours(10);
        RiskPosition Covered(PpaInstrument ppa,DateTimeOffset from,DateTimeOffset to)=>new(coveredTrade,Guid.NewGuid(),coveredBook,ppa.Id,
            new(ExactDecimal.Parse("80"),ppa.UnitQuantity.Specification),new(new Money(default,ppa.Currency),ppa.UnitQuantity.Specification),new(default,ppa.Currency)){Coverage=new(from,to)};
        var firstCovered=Covered(originalPpa,covered.From,split);var nextCovered=Covered(amendedPpa,split,covered.To);
        _=new ValuationPortfolio([originalPpa,amendedPpa],[firstCovered,nextCovered],new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        try
        {
            _=new ValuationPortfolio([originalPpa,amendedPpa],[firstCovered,nextCovered with{Coverage=new(covered.From,covered.To)}],
                new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
            throw new Exception("A trade must not double count overlapping retained and amended PPA coverage, even with different instrument identities.");
        }
        catch(MarketDataException e)when(e.Code=="ppa_segment_coverage"){}
        Console.WriteLine("PASS book valuation: unit Instrument.Value is shared across trades/books; external quantities retain exact scenario results");
        return 19;
    }
    [EconomicDefinition("synthetic_probe_unit_v1")]
    internal sealed record Probe:Instrument
    {
        [IdentityField] public string ModelVersion=>"synthetic_probe_unit_v1";
        public override ReferenceKey Currency{get;}
        public override Quantity UnitQuantity{get;}
        public ImmutableArray<CurveRequest> Inputs{get;}
        public int FactorDeclarations{get;private set;}
        public override ImmutableArray<QuoteFactor> MarketFactors(UnitValuationContext context)
        {FactorDeclarations++;return factors is null?base.MarketFactors(context):factors(context);}
        private readonly Func<PricingInputs,ExactMoney> evaluate;
        private readonly Func<UnitValuationContext,ImmutableArray<QuoteFactor>>? factors;
        public Probe(Guid id,ReferenceKey currency,ReferenceKey quantity,ImmutableArray<CurveRequest> dependencies,Func<PricingInputs,ExactMoney> evaluate,Func<UnitValuationContext,ImmutableArray<QuoteFactor>>? factors=null):base(id)
        {Currency=currency;UnitQuantity=new(ExactDecimal.Parse("1"),quantity);Inputs=dependencies;this.evaluate=evaluate;this.factors=factors;}
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,ModelVersion,Inputs,UnitQuantity.Specification,Currency);
        public override ExactMoney Value(UnitValuationContext context)=>evaluate(context.Inputs);
        public override InstrumentValuation PV(SideValuationContext context)=>throw new Exception("The selected probe uses its explicitly declared unit valuation method.");
    }
}
