using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class RiskChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string name,Action test){test();count++;Console.WriteLine($"PASS risk: {name}");}
        void Equal<T>(T actual,T expected) where T:notnull
        {if(!actual.Equals(expected))throw new Exception($"Expected {expected}; got {actual}.");}
        void Reject(string code,Action test)
        {try{test();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception($"Expected {code}.");}
        var f=new Fixture();var model=f.Model();var snapshot=f.Snapshot();
        var power=new CurveRequest(f.C("local_power"),f.K(5));var future=new CurveRequest(f.C("future"),f.K(1));
        QuoteBump Bump(int series=1,string size="0.1",string quote="gas_out")=>new(new(f.S(series),InputUse.Variable),f.R(quote),
            PriceUnit.FromQuote(f.Quotes.Single(q=>q.Key==f.R(quote))),ExactDecimal.Parse(size),QuoteBump.Convention);
        Check("temporary deal exact repricing retains nonzero curvature and signed cash changes",()=>
        {
            var run=DealRisk.Calculate(model,snapshot,[future],market=>
            {
                var p=market.Values.Single(v=>v.Request==future).Price.Value;
                return ExactMoney.FromMoney(new(p*p,f.R("currency")));
            },DealRisk.BumpPolicy,DealRisk.NumericalPolicy);
            var value=run.Factors.Single().Values;
            Equal(run.ScenarioCount,3);Equal(value.Base.ToMoney().Amount.ToString(),"9");
            Equal(value.Delta.Value.ToMoney().Amount.ToString(),"6");
            Equal(value.Gamma.Value.ToMoney().Amount.ToString(),"2");
            Equal(value.Delta.Unit.Order,1);Equal(value.Gamma.Unit.Order,2);
        });
        Check("one shared gas overlay propagates to power without editing observations",()=>
        {
            var run=model.BuildScenarios(snapshot,[power,future],[Bump()]);var pair=run.Factors.Single();
            Equal(run.Base.Values[0].Price.Value.ToString(),"29.1");
            Equal(pair.Up.Values[0].Price.Value.ToString(),"29.8");Equal(pair.Down.Values[0].Price.Value.ToString(),"28.4");
            Equal(snapshot.Selections[0].Observation.Value.ToString(),"3");
            Equal(ReferenceEquals(pair.Up.Snapshot,snapshot),true);
            Equal(run.Base.EvaluatedConstituents,9);Equal(pair.Up.EvaluatedConstituents,5);Equal(pair.Up.ReusedConstituents,4);
            Equal(pair.Down.EvaluatedConstituents,5);Equal(pair.Down.ReusedConstituents,4);
        });
        Check("cached base work is distinct from explicit scenario-parent reuse",()=>
        {
            var cache=new OpenEtrm.MarketData.Calculation.ConnectedCalculationCache(4,1000);
            var cached=f.Model(calculations:cache);
            var cold=cached.Build(snapshot,[power,future]);
            Equal(cold.EvaluatedConstituents,9);Equal(cold.ReusedConstituents,0);
            var run=cached.BuildScenarios(snapshot,[power,future],[Bump()]);
            Equal(run.Base.Evaluations.Length,9);
            Equal(run.Base.EvaluatedConstituents,0);Equal(run.Base.ReusedConstituents,0);
            Equal(run.Base.Values[0].Price.Value.ToString(),"29.1");
            foreach(var scenario in new[]{run.Factors[0].Up,run.Factors[0].Down})
            {Equal(scenario.EvaluatedConstituents,5);Equal(scenario.ReusedConstituents,4);}
            Equal(run.Factors[0].Up.Values[0].Price.Value.ToString(),"29.8");
            Equal(run.Factors[0].Down.Values[0].Price.Value.ToString(),"28.4");
        });
        Check("scenario settlement quotes can move off clearing increments without becoming new facts",()=>
        {
            var run=model.BuildScenarios(snapshot,[future],[Bump(size:"0.0001")]);
            Equal(run.Factors[0].Up.Values[0].Price.Value.ToString(),"3.0001");
            Equal(run.Factors[0].Up.Snapshot.Selections[0].Observation,snapshot.Selections[0].Observation);
        });
        Check("unaffected and explicit all-in inputs are reused without inferred gas exposure",()=>
        {
            var all=f.Direct("all_in",f.K(5),f.R("power_out"),SettlementKind.Index);
            var snap=snapshot with{Selections=snapshot.Selections.Add(f.Select(5,QuoteKind.Outright,"power_out","29.1"))};
            var run=f.Model(f.Curves.Add(all)).BuildScenarios(snap,[power,new(f.C("all_in"),f.K(5))],[Bump()]);
            Equal(run.Factors[0].Up.Values[1].Price.Value.ToString(),"29.1");
            Equal(ReferenceEquals(run.Base.Values[1],run.Factors[0].Up.Values[1]),true);
        });
        Check("absolute quote shocks reject missing choices, wrong units, frozen facts and price domains",()=>
        {
            foreach(var size in new[]{"0","-0.1"})Reject("risk_bump",()=>model.BuildScenarios(snapshot,[power],[Bump(size:size)]));
            Reject("risk_bump",()=>model.BuildScenarios(snapshot,[power],[Bump() with{ConventionId=""}]));
            Reject("risk_factor",()=>model.BuildScenarios(snapshot,[power],[Bump() with{Input=new(f.S(1),InputUse.FrozenFact)}]));
            Reject("risk_factor",()=>model.BuildScenarios(snapshot,[power],[Bump(6)]));
            Reject("risk_factor",()=>model.BuildScenarios(snapshot,[power],[Bump() with{Unit=PriceUnit.FromQuote(f.Quotes[2])}]));
            Reject("price_domain",()=>model.BuildScenarios(snapshot,[power],[Bump(size:"999")]));
            Reject("risk_bump",()=>model.BuildScenarios(snapshot,[power],[Bump(),Bump()]));
        });
        var contract=new UnitPricingContract(Guid.NewGuid(),"test_power_mark_v1",[power],f.R("power_quantity"),f.R("currency"));
        var pricer=new TestPricer(contract,inputs=>inputs.Price(power).Times(new Quantity(ExactDecimal.Parse("1"),f.R("power_quantity"))));
        var bookA=Guid.NewGuid();var bookB=Guid.NewGuid();
        RiskPosition Position(Guid book,string quantity,string basis="0")=>new(Guid.NewGuid(),Guid.NewGuid(),book,contract.Id,
            new(ExactDecimal.Parse(quantity),contract.Quantity),new(new Money(default,contract.Currency),contract.Quantity),new(ExactDecimal.Parse(basis),contract.Currency));
        var positions=ImmutableArray.Create(Position(bookA,"10000","200000"),Position(bookB,"-2000","-40000"));
        ValuationPortfolio Portfolio(ImmutableArray<Instrument> items,ImmutableArray<RiskPosition> held)=>
            new(items,held,new(ValuationMeasure.ExecutionPnl,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        RiskRun Run(ImmutableArray<RiskPosition>? held=null,ImmutableArray<Instrument>? pricers=null,QuoteBump? bump=null)=>
            RiskEngine.Calculate(model,snapshot,Portfolio(pricers??[pricer],held??positions),[bump??Bump()],RiskEngine.NumericalPolicy);
        Check("black-box repricing gives the independent 7000 cash shock oracle with quantity applied once",()=>
        {
            var run=Run();var factor=run.Factors.Single();var a=factor.Positions.Single(p=>p.BookId==bookA);
            Equal(a.Base.Amount.ToString(),"91000");Equal(a.UpPnl.Amount.ToString(),"7000");Equal(a.DownPnl.Amount.ToString(),"-7000");
            Equal(a.Delta.Value.ToString(),"70000");Equal(a.Gamma.Value.ToString(),"0");
            Equal(a.Delta.Unit.Factor,PriceUnit.FromQuote(f.Quotes[0]));Equal(a.Delta.Unit.Order,1);Equal(a.Gamma.Unit.Order,2);
            Equal(factor.Totals.Single(t=>t.BookId==bookB).UpPnl.Amount.ToString(),"-1400");
            Equal(run.Work.UnitPricerCalls,3);Equal(run.Work.PositionCount,2);Equal(run.Work.UniqueValuations,1);
        });
        Check("thousands of holdings add scaling work without adding unit-pricer calls",()=>
        {
            var repeated=Enumerable.Range(0,10000).Select(_=>Position(bookA,"1")).ToImmutableArray();
            var run=Run(repeated);Equal(run.Work.PositionCount,10000);Equal(run.Work.UnitPricerCalls,3);
            Equal(run.Factors[0].Totals.Single().UpPnl.Amount.ToString(),"7000");
        });
        Check("nonlinear quantity-scalable pricer retains curvature rather than assuming market linearity",()=>
        {
            var nonlinear=new TestPricer(contract with{ModelVersion="test_square_v1"},inputs=>
            {var p=inputs.Price(power).Value;return new Money(p*p,contract.Currency);});
            var run=Run([Position(bookA,"2")],[nonlinear]);var p=run.Factors[0].Positions.Single();
            // (29.8^2 - 29.1^2) * 2; chain derivative 2 * 29.1 * 7 * 2.
            Equal(p.UpPnl.Amount.ToString(),"82.46");Equal(p.DownPnl.Amount.ToString(),"-80.5");
            Equal(p.Delta.Value.ToString(),"814.8");Equal(p.Gamma.Value.ToString(),"196");
        });
        Check("small exact bumps do not require a representable squared denominator",()=>
        {
            var p=Run(bump:Bump(size:"0.0000000001")).Factors[0].Positions.Single(p=>p.BookId==bookA);
            Equal(p.Delta.Value.ToString(),"70000");Equal(p.Gamma.Value.ToString(),"0");
        });
        Check("undeclared pricer reads and unsupported quantity behavior fail explicitly",()=>
        {
            var hidden=new TestPricer(contract,inputs=>inputs.Price(future).Times(new Quantity(ExactDecimal.Parse("1"),f.R("gas_quantity"))));
            Reject("risk_dependency",()=>Run(pricers:[hidden]));
            Reject("risk_contract",()=>Run(pricers:[new TestPricer(contract with{Dependencies=default},_=>new(default,contract.Currency))]));
            Reject("risk_contract",()=>Run(pricers:[pricer,pricer]));
            Reject("risk_policy",()=>RiskEngine.Calculate(model,snapshot,Portfolio([pricer],positions),[Bump()],""));
        });
        Check("invalid position identity, currency and quantity cannot enter shared calculations",()=>
        {
            Reject("risk_position",()=>Run([positions[0],positions[0]]));
            Reject("risk_position",()=>Run([positions[0],positions[1] with{TradeId=positions[0].TradeId}]));
            Reject("risk_position",()=>Run([positions[0] with{Quantity=new(ExactDecimal.Parse("1"),f.R("gas_quantity"))}]));
            Reject("risk_position",()=>Run([positions[0] with{FixedBasis=new(default,f.R("other_currency"))}]));
            Reject("risk_position",()=>Run([positions[0] with{UnitBasis=new(new Money(default,contract.Currency),f.R("gas_quantity"))}]));
        });
        Check("unrelated factors skip unit pricers but preserve an explicit zero result",()=>
        {
            var gasContract=contract with{Dependencies=[future],Quantity=f.R("gas_quantity")};
            var gas=new TestPricer(gasContract,i=>i.Price(future).Times(new Quantity(ExactDecimal.Parse("1"),gasContract.Quantity)));
            var p=positions[0] with{Quantity=new(ExactDecimal.Parse("2"),gasContract.Quantity),UnitBasis=new(new Money(default,contract.Currency),gasContract.Quantity)};
            var run=Run([p],[gas],Bump(5,quote:"power_basis"));
            Equal(run.Work.UnitPricerCalls,1);Equal(run.Factors[0].Positions[0].UpPnl.Amount.ToString(),"0");
            Equal(run.Factors[0].UpWork.RebuiltCurveNodes,0);Equal(run.Factors[0].UpWork.ReusedUnitValues,1);
        });
        Check("exact derivative division rejects unrepresentable results instead of rounding",()=>
        {
            Equal(ExactDecimal.Parse("1").DivideExact(ExactDecimal.Parse("8")).ToString(),"0.125");
            Reject("precision_loss",()=>ExactDecimal.Parse("1").DivideExact(ExactDecimal.Parse("3")));
        });
        Check("reuse matches independent per-position full curve builds and retained fixed bases",()=>
        {
            var held=Enumerable.Range(1,31).Select(n=>Position(n%2==0?bookA:bookB,(n%5-2).ToString(),(n*11).ToString())).ToImmutableArray();
            var run=Run(held);var result=run.Factors.Single();
            MarketSnapshot Shift(string amount)=>snapshot with{Id=Guid.NewGuid(),Selections=snapshot.Selections.Select(s=>s.Input==Bump().Input?
                s with{Observation=s.Observation with{Key=new(f.Revision.CatalogId,Guid.NewGuid()),Value=s.Observation.Value+ExactDecimal.Parse(amount)}}:s).ToImmutableArray()};
            var up=Shift("0.1");var down=Shift("-0.1");
            foreach(var position in held)
            {
                Money Value(MarketSnapshot market)=>model.Build(market,[power]).Values.Single().Price.Times(position.Quantity).Subtract(position.FixedBasis);
                var actual=result.Positions.Single(p=>p.TradeId==position.TradeId);
                Equal(actual.Base,Value(snapshot));Equal(actual.Up,Value(up));Equal(actual.Down,Value(down));
            }
            var reversed=Run(held.Reverse().ToImmutableArray());
            Equal(string.Join(",",result.Totals.Select(t=>t.UpPnl.Amount)),string.Join(",",reversed.Factors[0].Totals.Select(t=>t.UpPnl.Amount)));
        });
        Check("multiple factors have independent overlays and reuse the same pinned base",()=>
        {
            var run=RiskEngine.Calculate(model,snapshot,Portfolio([pricer],positions),[Bump(),Bump(5,quote:"power_basis")],RiskEngine.NumericalPolicy);
            Equal(run.Work.ScenarioCount,5);Equal(run.Work.UnitPricerCalls,5);
            Equal(run.Factors[1].Positions.Single(p=>p.BookId==bookA).UpPnl.Amount.ToString(),"1000");
            Equal(run.Factors[1].UpWork.RebuiltCurveNodes,2);
            Equal(run.Factors[0].Positions.Single(p=>p.BookId==bookA).UpPnl.Amount.ToString(),"7000");
        });
        Check("zero local delta retains dependency and nonlinear scenario values",()=>
        {
            var centered=new TestPricer(contract with{ModelVersion="test_centered_square_v1"},i=>
            {var difference=i.Price(power).Value-ExactDecimal.Parse("29.1");return new(difference*difference,contract.Currency);});
            var run=Run([Position(bookA,"1")],[centered]);var p=run.Factors[0].Positions.Single();
            Equal(p.Delta.Value.ToString(),"0");Equal(p.Gamma.Value.ToString(),"98");Equal(p.UpPnl.Amount.ToString(),"0.49");
            Equal(run.Work.UnitPricerCalls,3);
        });
        Check("declared output currency and intermediate precision failures cannot become successful risk",()=>
        {
            Reject("risk_currency",()=>Run(pricers:[new TestPricer(contract,_=>new(default,f.R("other_currency")))]));
            Reject("precision_loss",()=>Run([Position(bookA,"1")],[new TestPricer(contract,i=>
            {var p=i.Price(power).Value;return new(p*p,contract.Currency);})],Bump(size:"0.0000000001")));
        });
        Check("cancelled execution returns no partial successful result",()=>
        {
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            try{RiskEngine.Calculate(model,snapshot,Portfolio([pricer],positions),[Bump()],RiskEngine.NumericalPolicy,cancel.Token);}
            catch(OperationCanceledException){return;}throw new Exception("Expected cancellation.");
        });
        Check("position-scale derivatives retain exact quotients that unit-scale division could not represent",()=>
        {
            var thirds=new TestPricer(contract,i=>new(i.Price(power).Value==ExactDecimal.Parse("29.1")?default:
                i.Price(power).Value.CompareTo(ExactDecimal.Parse("29.1"))>0?ExactDecimal.Parse("1"):ExactDecimal.Parse("-1"),contract.Currency));
            var result=Run([Position(bookA,"3")],[thirds],Bump(size:"3")).Factors[0].Positions.Single();
            Equal(result.Delta.Value.ToString(),"1");Equal(result.Gamma.Value.ToString(),"0");
        });
        Check("cancellation between black-box evaluations cannot publish a partial portfolio",()=>
        {
            using var cancel=new CancellationTokenSource();var calls=0;
            var interrupted=new TestPricer(contract,i=>{if(++calls==2)cancel.Cancel();return i.Price(power).Times(new Quantity(ExactDecimal.Parse("1"),contract.Quantity));});
            try{RiskEngine.Calculate(model,snapshot,Portfolio([interrupted],positions),[Bump()],RiskEngine.NumericalPolicy,cancel.Token);}
            catch(OperationCanceledException){Equal(calls,2);return;}throw new Exception("Expected mid-run cancellation.");
        });
        Console.WriteLine($"{count} risk checks passed.");return count;
    }
    [EconomicDefinition("synthetic_unit_pricer_v1")]
    internal sealed record TestPricer:Instrument
    {
        [IdentityField] public UnitPricingContract Contract{get;}
        private readonly Func<PricingInputs,Money> evaluate;
        public override ReferenceKey Currency=>Contract.Currency;
        public override Quantity UnitQuantity=>new(ExactDecimal.Parse("1"),Contract.Quantity);
        public TestPricer(UnitPricingContract contract,Func<PricingInputs,Money> evaluate):base(contract.Id)
        {Contract=contract;this.evaluate=evaluate;}
        public override UnitPricingContract Declare(UnitValuationContext context)=>Contract;
        public override ExactMoney Value(UnitValuationContext context)=>ExactMoney.FromMoney(evaluate(context.Inputs));
        public override InstrumentValuation PV(SideValuationContext context)=>throw new MarketDataException("test_context","Select the declared synthetic unit valuation context.");
    }
}
