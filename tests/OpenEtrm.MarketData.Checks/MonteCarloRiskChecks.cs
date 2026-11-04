using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;

internal static class MonteCarloRiskChecks
{
    internal static MonteCarloRiskPolicy Policy(int paths=4096)=>new(MonteCarloRiskPolicy.Version1,MonteCarloRiskPolicy.Numerics,
        ParametricRiskPolicy.Sample,ParametricRiskPolicy.Centered,ParametricRiskPolicy.Zero,ParametricRiskPolicy.Session,
        HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail,HistoricalRiskPolicy.Valuation,"0.99",
        MonteCarloRiskPolicy.Gaussian,MonteCarloRiskPolicy.Generator,MonteCarloRiskPolicy.ShockRounding,"20261104",paths,MonteCarloRiskPolicy.Uncertainty);
    internal static void Run()
    {
        var f=new ParametricRiskChecks.Fixture();var p=Policy(16384);
        var generated=MonteCarloRisk.Generate(f.History,p);
        if(!generated.Available||generated.Samples.Length!=p.Paths||generated.Calibration.Rank!=2)
            throw new Exception("Full-factor simulation did not retain the admitted sample count/calibration.");
        var rows=generated.Samples.Select(s=>s.Changes.Select(x=>double.Parse(x.ToString(),CultureInfo.InvariantCulture)).ToArray()).ToArray();
        double Mean(int i)=>rows.Average(r=>r[i]);
        double Cov(int i,int j){var a=Mean(i);var b=Mean(j);return rows.Sum(r=>(r[i]-a)*(r[j]-b))/(rows.Length-1);}
        Near(Mean(0),0,.06);Near(Mean(1),0,.10);
        Near(Cov(0,0),4,.16);Near(Cov(0,1),5,.25);Near(Cov(1,1),13,.52);
        // Independent analytic N(0,7) portfolio. The 0.35 allowance is about
        // 4.5 asymptotic quantile standard errors at 16384 draws.
        var losses=rows.Select(r=>-r[0]+r[1]).Order().ToArray();
        Near(losses[(int)Math.Ceiling(.99*rows.Length)-1],2.3263478740408408*Math.Sqrt(7),.35);
        Console.WriteLine("PASS Monte Carlo: independent Gaussian means, full covariance and analytic linear portfolio quantile");
        var batchA=MonteCarloRisk.Generate(f.History,p,0,127);var batchB=MonteCarloRisk.Generate(f.History,p,127,193);
        if(SampleText(generated.Samples.Take(320))!=SampleText(batchA.Samples.Concat(batchB.Samples)))
            throw new Exception("Batch boundaries changed scenario identities or native shocks.");
        var again=MonteCarloRisk.Generate(f.History,p,0,320);
        if(SampleText(again.Samples)!=SampleText(generated.Samples.Take(320)))
            throw new Exception("Replay changed the counter-based random stream.");
        var other=MonteCarloRisk.Generate(f.History,p with{Seed="20261105"},0,320);
        if(other.Samples.Select(s=>s.Changes[0]).SequenceEqual(again.Samples.Select(s=>s.Changes[0])))
            throw new Exception("A changed explicit seed did not select a new stream.");
        foreach(var (seed,index,x,y) in new[]{
            ("20261104",0,"2.470458811881252714","0.78438435065883727"),
            ("20261104",19,"0.334769106928027693","-3.475421557708771036"),
            ("20261104",127,"-0.408149132366064926","1.56581205569233628"),
            ("0",0,"2.329660559653456087","-0.1635804630874933"),
            ("0",19,"-0.97835449284762277","-2.473687407598424315"),
            ("18446744073709551615",0,"0.394763866666995977","0.993521533553174185"),
            ("18446744073709551615",19,"1.931777414860568642","5.71486184407003428")})
        {
            var sample=seed==p.Seed?generated.Samples[index]:MonteCarloRisk.Generate(f.History,Policy(128) with{Seed=seed},index,1).Samples.Single();
            if(sample.Changes[0].ToString()!=x||sample.Changes[1].ToString()!=y)
                throw new Exception("The versioned generator changed its exact native shock vector; qualify and select a new numerical version.");
        }
        Console.WriteLine("PASS Monte Carlo: exact native golden vectors, seed boundaries, replay and arbitrary batch partitions preserve every sample and shock");
        var singular=new ParametricRiskChecks.Fixture(["10","12","12","10"],["20","22","22","20"]);
        var duplicate=MonteCarloRisk.Generate(singular.History,Policy());
        if(duplicate.Calibration.Rank!=1||duplicate.Samples.Any(s=>s.Changes[0]!=s.Changes[1]))
            throw new Exception("Singular covariance was repaired or lost its exact shared-factor relation.");
        var constant=new ParametricRiskChecks.Fixture(["1","2","3","4"],["7","7","7","7"]);
        var drift=MonteCarloRisk.Generate(constant.History,Policy() with{Drift=ParametricRiskPolicy.Mean});
        if(drift.Samples.Any(s=>s.Changes[0]!=ExactDecimal.Parse("1")||s.Changes[1]!=default))
            throw new Exception("A deterministic/zero-variance column lost its selected mean.");
        Console.WriteLine("PASS Monte Carlo: singular covariance, duplicate factors, constant columns and explicit drift");
        foreach(var invalid in new[]{Policy() with{Seed="01"},Policy() with{Seed="-1"},Policy() with{Seed="18446744073709551616"},
            Policy() with{Paths=0},Policy() with{Distribution="lognormal"},Policy() with{ShockPolicy="round_to_tick"},Policy() with{Drift=""},
            Policy() with{Confidence="0.500000000000000001"}})
            Reject("risk_monte_carlo_policy",()=>MonteCarloRisk.Generate(f.History,invalid));
        Reject("risk_monte_carlo_capacity",()=>MonteCarloRisk.Generate(f.History,Policy(),4095,2));
        using var stop=new CancellationTokenSource();stop.Cancel();
        try{MonteCarloRisk.Generate(f.History,Policy(),cancellation:stop.Token);throw new Exception("Cancelled generation succeeded.");}
        catch(OperationCanceledException){}
        Console.WriteLine("PASS Monte Carlo: explicit model, seed, numerical/batch bounds and cancellation refuse invalid generation");
        var shortRun=MonteCarloRisk.Generate(f.History,Policy(256));
        var book=Guid.NewGuid();var trade=Guid.NewGuid();var binding=Guid.NewGuid();
        var exposure=new HistoricalExposure("position",book,trade,binding,"present_value",f.Currency,"0");
        var observations=shortRun.Samples.Select(s=>new HistoricalObservation(s.Id,[new("position",s.Changes[0].ToString())])).ToImmutableArray();
        var reduced=MonteCarloRisk.Calculate(shortRun,[exposure],observations);
        if(reduced.Samples!=256||reduced.Diagnostics.Single().Convergence.Select(x=>x.Paths).SequenceEqual(new[]{64,128,256})==false)
            throw new Exception("Convergence did not use generation-order prefixes.");
        var fixture=new JointScenarioRiskChecks.Fixture((x,y)=>x.CompareTo(default)>0?x:-x,"3",differentUnits:true);
        var scenarios=fixture.Set([..shortRun.Samples.Select(s=>fixture.Sample(s.Id,(s.Changes[0].ToString(),s.Changes[1].ToString())))]) with{Id=f.History.Id,Version=MonteCarloRiskPolicy.Version1};
        var priced=RiskEngine.CalculateMonteCarloScenarios(fixture.Prepared,scenarios,RiskEngine.ExactNumericalPolicy);
        for(var i=0;i<shortRun.Samples.Length;i++)
        {
            var x=shortRun.Samples[i].Changes[0];if(x.CompareTo(default)<0)x=-x;
            if(priced.Scenarios[i].Groups.Single().Totals.Single().Values.Loss.ToExactString()!=(-x*ExactDecimal.Parse("3")).ToString()||priced.Scenarios[i].Scenario.ParentId!=priced.BaseScenario.Id)
                throw new Exception("Nonlinear full-PV samples were not independent frozen-base siblings.");
        }
        Console.WriteLine("PASS Monte Carlo: complete empirical reduction, generation prefixes and nonlinear full-PV sibling scenario oracle");
        var tiny=new ParametricRiskChecks.Fixture(["0","0.000000000000000001","0","0.000000000000000001"],["0","0","0","0"]);
        Reject("risk_monte_carlo_numeric",()=>MonteCarloRisk.Generate(tiny.History,Policy(128)));
        var high=MonteCarloRisk.QuantileInterval(Enumerable.Repeat("10",16384).ToArray(),"0.9999");
        if(high.Upper is not null||high.UpperRank!=16385||high.Lower!="10")throw new Exception("An unbounded quantile endpoint was replaced by a sample extreme.");
        var mass=MonteCarloRisk.QuantileInterval(Enumerable.Repeat("-2",128).ToArray(),"0.75");
        if(mass.Lower!="-2"||mass.Upper!="-2")throw new Exception("Point-mass sampling bounds were changed into a continuous density.");
        Console.WriteLine("PASS Monte Carlo: native shock underflow refuses, point-mass bounds and extreme-tail unbounded sampling endpoint");
        var interior=new ParametricRiskChecks.Fixture(["10","12","12","10"],["7","7","7","7"],["20","21","24","20"]);
        var three=MonteCarloRisk.Generate(interior.History,Policy(128));
        if(three.Calibration.Rank!=2||!three.Diagonal.SequenceEqual(new[]{"4","0","6.75"})||three.Samples.Any(s=>s.Changes[1]!=default)||
            interior.History.Columns[0].Mapping.Factor.Unit==interior.History.Columns[2].Mapping.Factor.Unit)
            throw new Exception("Interior zero pivots or native unlike-unit coordinates changed the full-factor model.");
        var zero=new ParametricRiskChecks.Fixture(["1","1","1","1"],["2","2","2","2"]);
        var point=MonteCarloRisk.Generate(zero.History,Policy(128));
        if(point.Calibration.Rank!=0||point.Samples.Any(s=>s.Changes.Any(x=>x!=default))||point.Samples.Select(s=>s.Id).Distinct().Count()!=128)
            throw new Exception("Zero covariance changed probability mass or manufactured random market moves.");
        Console.WriteLine("PASS Monte Carlo: exact interior zero pivot, unlike native units and zero-rank unchanged samples");
        var at=DateTimeOffset.Parse("2026-01-01T16:00:00Z",CultureInfo.InvariantCulture);
        var terms=new FuturesOptionTerms(OptionRight.Call,ExactDecimal.Parse("1"),at.AddDays(30),"american","exercise_into_future");
        var tree=OptionNumericalPolicies.Require(AmericanFuturesOptionModel.Id) with{Steps=8};
        ExactDecimal Option(ExactDecimal move)=>AmericanFuturesOptionModel.UnitPrice(terms,LatticeDecimal.Parse((ExactDecimal.Parse("1")+move).ToString()),
            LatticeDecimal.Parse("0.3"),LatticeDecimal.Parse("0.99"),at,tree,default).ToExact();
        var option=new JointScenarioRiskChecks.Fixture((x,_)=>Option(x));
        var optionSet=option.Set([..shortRun.Samples.Select(s=>option.Sample(s.Id,(s.Changes[0].ToString(),s.Changes[1].ToString())))]) with{Id=f.History.Id,Version=MonteCarloRiskPolicy.Version1};
        Reject("option_input",()=>RiskEngine.CalculateMonteCarloScenarios(option.Prepared,optionSet,RiskEngine.ExactNumericalPolicy));
        var recovered=RiskEngine.CalculateExact(option.Prepared,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value;
        if(recovered.ToExactString()!=Option(default).ToString())throw new Exception("A failed generated option path mutated its frozen parent.");
        Console.WriteLine("PASS Monte Carlo: actual positive-forward option domain failure refuses the whole run and preserves the frozen base");
    }
    internal static void ExportOracles(string path,string? casesPath=null)
    {
        var f=new ParametricRiskChecks.Fixture();
        var requests=(from n in new[]{2,3,8,16,128,2048,4096,16384}
            from confidence in new[]{"0.5000000000000001","0.6","0.75","0.95","0.99","0.999","0.9999"}
            select (n,confidence)).ToList();
        if(casesPath is not null){using var input=JsonDocument.Parse(File.ReadAllText(casesPath));requests.AddRange(input.RootElement.EnumerateArray().Select(x=>(x.GetProperty("n").GetInt32(),x.GetProperty("confidence").GetString()!)));}
        var cases=requests.Select(x=>new{x.n,x.confidence,interval=MonteCarloRisk.QuantileInterval(Enumerable.Range(1,x.n).Select(i=>i.ToString(CultureInfo.InvariantCulture)).ToArray(),x.confidence)}).ToArray();
        var generation=MonteCarloRisk.Generate(f.History,Policy(128));
        var boundaries=new[]{"0","18446744073709551615"}.Select(seed=>new{seed,samples=MonteCarloRisk.Generate(f.History,Policy(128) with{Seed=seed},0,20).Samples.Select(s=>new{s.Id,s.Index,changes=s.Changes.Select(x=>x.ToString())})});
        File.WriteAllText(path,JsonSerializer.Serialize(new{boundaries,samples=generation.Samples.Select(s=>new{s.Id,s.Index,changes=s.Changes.Select(x=>x.ToString())}),lower=generation.Lower,diagonal=generation.Diagonal,cases},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    static string SampleText(IEnumerable<MonteCarloSample> samples)=>JsonSerializer.Serialize(samples.Select(s=>new{s.Id,s.Index,Changes=s.Changes.Select(v=>v.ToString())}));
    static void Near(double actual,double expected,double tolerance)
    {if(!double.IsFinite(actual)||Math.Abs(actual-expected)>tolerance)throw new Exception($"Expected {expected} within {tolerance}, got {actual}.");}
    static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
