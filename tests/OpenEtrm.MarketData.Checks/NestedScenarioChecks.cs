using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class NestedScenarioChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string name,Action test){test();count++;Console.WriteLine($"PASS nested scenarios: {name}");}
        void Equal<T>(T actual,T expected) where T:notnull
        {if(!actual.Equals(expected))throw new Exception($"Expected {expected}; got {actual}.");}
        void Reject(string code,Action test)
        {try{test();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception($"Expected {code}.");}
        var f=new Fixture();var model=f.Model();var snapshot=f.Snapshot();
        var power=new CurveRequest(f.C("local_power"),f.K(5));var gas=new CurveRequest(f.C("future"),f.K(1));
        QuoteShift Shift(string size="0.1",int series=1,string quote="gas_out")=>new(new(f.S(series),InputUse.Variable),f.R(quote),
            PriceUnit.FromQuote(f.Quotes.Single(q=>q.Key==f.R(quote))),ExactDecimal.Parse(size),QuoteShift.Convention);
        QuoteBump Bump()=>new(Shift().Input,Shift().Quote,Shift().Unit,ExactDecimal.Parse("0.1"),QuoteBump.Convention);
        var basis=model.Build(snapshot,[power,gas]);
        string Price(CurveBuild market,CurveRequest request)=>market.Values.Single(v=>v.Request==request).Price.Value.ToString();
        Check("children compose on their parent while parent, siblings and observed facts remain intact",()=>
        {
            var outer=model.WithShifts(basis,[Shift()]);
            var inner=model.WithShifts(outer,[Shift("1",5,"power_basis")]);
            var same=model.WithShifts(outer,[Shift("0.2")]);
            Equal(Price(basis,power),"29.1");Equal(Price(outer,power),"29.8");
            Equal(Price(inner,power),"30.8");Equal(Price(same,power),"31.2");
            Equal(Price(outer,gas),"3.1");Equal(Price(same,gas),"3.3");
            Equal(snapshot.Selections[0].Observation.Value.ToString(),"3");
            Equal(ReferenceEquals(inner.Snapshot,snapshot),true);
            Equal(inner.Scenario.ParentId!,outer.Scenario.Id);Equal(basis.Scenario.IsBase,true);
        });
        Check("scenario receipts are deterministic and distinguish identical snapshots under different shocks",()=>
        {
            var a=model.WithShifts(basis,[Shift()]);var b=model.WithShifts(basis,[Shift()]);
            Equal(a.Scenario.Id,b.Scenario.Id);Equal(a.Scenario.Id==basis.Scenario.Id,false);
            Equal(a.Scenario.Id==model.WithShifts(basis,[Shift("0.2")]).Scenario.Id,false);
            var reordered=model.Build(snapshot with{Selections=snapshot.Selections.Reverse().ToImmutableArray()},[power,gas]);
            Equal(reordered.Scenario.Id,basis.Scenario.Id);
            var different=model.Build(snapshot with{ValuationAt=snapshot.ValuationAt.AddTicks(1)},[power,gas]);
            Equal(different.Scenario.Id==basis.Scenario.Id,false);
            var altered=snapshot with{Selections=snapshot.Selections.Select(s=>s.Input==Shift().Input?
                s with{Observation=s.Observation with{Value=ExactDecimal.Parse("3.1")}}:s).ToImmutableArray()};
            Equal(model.Build(altered,[power,gas]).Scenario.Id==basis.Scenario.Id,false);
        });
        Check("parallel siblings are isolated and failed children preserve the parent",()=>
        {
            var outer=model.WithShifts(basis,[Shift()]);
            var children=Task.WhenAll(Enumerable.Range(1,12).Select(n=>Task.Run(()=>
                model.WithShifts(outer,[Shift(n.ToString(),5,"power_basis")])))).GetAwaiter().GetResult();
            for(var n=0;n<children.Length;n++)Equal(Price(children[n],power),(ExactDecimal.Parse("29.8")+ExactDecimal.Parse((n+1).ToString())).ToString());
            Reject("price_domain",()=>model.WithShifts(outer,[Shift("999")]));
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try{model.WithShifts(outer,[Shift()],cancelled.Token);throw new Exception("Expected cancellation.");}
            catch(OperationCanceledException){}
            Equal(Price(outer,power),"29.8");Equal(Price(basis,power),"29.1");
        });
        Check("signed shifts validate conventions, units, contexts, frozen inputs and cumulative domains",()=>
        {
            Reject("scenario_shift",()=>model.WithShifts(basis,[]));
            Reject("scenario_shift",()=>model.WithShifts(basis,[Shift("0")]));
            Reject("scenario_shift",()=>model.WithShifts(basis,[Shift() with{ConventionId=""}]));
            Reject("scenario_shift",()=>model.WithShifts(basis,[Shift(),Shift()]));
            Reject("risk_factor",()=>model.WithShifts(basis,[Shift() with{Input=new(f.S(1),InputUse.FrozenFact)}]));
            Reject("risk_factor",()=>model.WithShifts(basis,[Shift() with{Unit=PriceUnit.FromQuote(f.Quotes[2])}]));
            Reject("risk_factor",()=>model.WithShifts(basis,[Shift() with{Input=new(f.S(6),InputUse.Variable)}]));
            Reject("snapshot_pin",()=>f.Model().WithShifts(basis,[Shift()]));
            var nearLimit=model.WithShifts(model.Build(snapshot,[gas]),[Shift("900")]);
            Reject("price_domain",()=>model.WithShifts(nearLimit,[Shift("900")]));
            Equal(Price(model.WithShifts(model.WithShifts(basis,[Shift()]),[Shift("-0.1")]),gas),"3");
        });
        Check("composed scenarios agree with independently rebuilt snapshots and reuse unaffected nodes",()=>
        {
            var outer=model.WithShifts(basis,[Shift()]);var inner=model.WithShifts(outer,[Shift("1",5,"power_basis")]);
            var reference=snapshot with{Id=Guid.NewGuid(),Selections=snapshot.Selections.Select(s=>s.Input.Series==f.S(1)?
                s with{Observation=s.Observation with{Value=ExactDecimal.Parse("3.1")}}:s.Input.Series==f.S(5)?
                s with{Observation=s.Observation with{Value=ExactDecimal.Parse("3")}}:s).ToImmutableArray()};
            var full=model.Build(reference,[power,gas]);
            Equal(Price(inner,power),Price(full,power));Equal(Price(inner,gas),Price(full,gas));
            Equal(ReferenceEquals(inner.Values.Single(v=>v.Request==gas),outer.Values.Single(v=>v.Request==gas)),true);
            Equal(inner.EvaluatedConstituents,2);Equal(inner.ReusedConstituents,7);
        });
        Check("nonlinear risk is measured about the complete stressed baseline across books",()=>
        {
            var contract=new UnitPricingContract(Guid.NewGuid(),"synthetic_square_under_stress_v1",[power],f.R("power_quantity"),f.R("currency"));
            var instrument=new RiskChecks.TestPricer(contract,i=>new(i.Price(power).Value*i.Price(power).Value,contract.Currency));
            var books=new[]{Guid.NewGuid(),Guid.NewGuid()};
            var positions=books.Select(book=>new RiskPosition(Guid.NewGuid(),Guid.NewGuid(),book,contract.Id,
                new(ExactDecimal.Parse("2"),contract.Quantity),new(new Money(default,contract.Currency),contract.Quantity),new(default,contract.Currency))).ToImmutableArray();
            var portfolio=new ValuationPortfolio([instrument],positions,new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
            var outer=model.WithShifts(basis,[Shift()]);var inner=model.WithShifts(outer,[Shift("1",5,"power_basis")]);
            var result=RiskEngine.Calculate(inner,portfolio,[Bump()],RiskEngine.NumericalPolicy);
            Equal(result.Scenario.Id,inner.Scenario.Id);Equal(result.Work.UnitPricerCalls,3);
            // Seven nodes survive the last stress layer; four survive each of the two gas bumps.
            Equal(result.Work.ReusedCurveNodes,15);
            foreach(var row in result.Factors.Single().Positions)
            {
                Equal(row.Base.Amount.ToString(),"1897.28");
                Equal(row.UpPnl.Amount.ToString(),"87.22");Equal(row.DownPnl.Amount.ToString(),"-85.26");
                Equal(row.Delta.Value.ToString(),"862.4");Equal(row.Gamma.Value.ToString(),"196");
            }
            // Squaring creates a mixed gas/basis term: 2 holdings * 2 * 0.7 * 1.
            var pureGas=ExactDecimal.Parse("29.8")*ExactDecimal.Parse("29.8");
            var pureBasis=ExactDecimal.Parse("30.1")*ExactDecimal.Parse("30.1");
            var original=ExactDecimal.Parse("29.1")*ExactDecimal.Parse("29.1");
            Equal((result.Factors[0].Positions[0].Base.Amount-(pureGas+pureBasis-original)*ExactDecimal.Parse("2")).ToString(),"2.8");
            Equal(Price(outer,power),"29.8");
        });
        Console.WriteLine($"{count} nested scenario checks passed.");return count;
    }
}
