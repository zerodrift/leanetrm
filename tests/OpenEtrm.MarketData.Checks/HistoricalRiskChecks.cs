using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;

internal static class HistoricalRiskChecks
{
    internal static void Run()
    {
        var revision=new ReferenceRevision(Guid.Parse("10000000-0000-0000-0000-000000000001"),Guid.Parse("10000000-0000-0000-0000-000000000002"));
        var usd=new ReferenceKey(revision,"usd");var eur=new ReferenceKey(revision,"eur");
        var book=Guid.NewGuid();var otherBook=Guid.NewGuid();var trade=Guid.NewGuid();var otherTrade=Guid.NewGuid();
        var a=new HistoricalExposure("a",book,trade,Guid.NewGuid(),"contractual_pv",usd,"100");
        var b=new HistoricalExposure("b",otherBook,otherTrade,Guid.NewGuid(),"contractual_pv",usd,"200");
        var policy=new HistoricalRiskPolicy(HistoricalRiskPolicy.Version1,HistoricalRiskPolicy.Numerics,HistoricalRiskPolicy.Session,
            HistoricalRiskPolicy.Sampling,HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail,HistoricalRiskPolicy.Valuation,"0.75");
        const string history="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        ImmutableArray<HistoricalObservation> Rows(params string[][] values)=>[..values[0].Select((_,i)=>new HistoricalObservation("s"+i,
            [..values.Select((v,j)=>new HistoricalScenarioValue(j==0?"a":"b",v[i]))]))];
        HistoricalRiskReport Run(ImmutableArray<HistoricalExposure> exposures,ImmutableArray<HistoricalObservation> rows,HistoricalRiskPolicy? p=null)=>
            HistoricalRisk.Calculate(history,[..rows.Select(r=>r.Id).Order(StringComparer.Ordinal)],p??policy,exposures,rows);
        var first=Run([a],Rows(["100","90","80","70","60"]));var p=first.Partitions.Single();
        Equal(p.ValueAtRisk,"30");Equal(p.ExpectedShortfall,"38");Equal(p.BaseValue,"100");
        Equal(p.Samples.Single(s=>s.Id=="s3").EsWeight,"0.2");Equal(p.Samples.Single(s=>s.Id=="s4").EsWeight,"0.8");
        if(p.Histogram.Sum(v=>v.Count)!=5)throw new Exception("The histogram lost probability mass.");
        Console.WriteLine("PASS historical risk: hand-sorted inverse-CDF quantile and fractional tail mass give VaR30/ES38");

        var tied=Rows(["100","98","96","88","60"],["200","192","194","202","210"]);
        var tiePolicy=policy with{Confidence="0.6"};var tie=Run([a,b],tied,tiePolicy).Partitions.Single();
        Equal(tie.ValueAtRisk,"10");Equal(tie.ExpectedShortfall,"20");
        Equal(tie.Books.Single(v=>v.Id==book).ValueAtRisk,"6");Equal(tie.Books.Single(v=>v.Id==otherBook).ValueAtRisk,"4");
        Equal(tie.Books.Single(v=>v.Id==book).ExpectedShortfall,"23");Equal(tie.Books.Single(v=>v.Id==otherBook).ExpectedShortfall,"-3");
        Equal(tie.Samples.Single(s=>s.Id=="s1").EsWeight,"1/6");Equal(tie.Samples.Single(s=>s.Id=="s2").VarWeight,"1/3");
        var reversed=Run([b,a],[..tied.Reverse().Select(row=>row with{Values=[..row.Values.Reverse()]})],tiePolicy).Partitions.Single();
        if(tie!=reversed&&System.Text.Json.JsonSerializer.Serialize(tie)!=System.Text.Json.JsonSerializer.Serialize(reversed))
            throw new Exception("Tied quantiles or attribution depend on sample/component insertion order.");
        Console.WriteLine("PASS historical risk: portfolio-tail weights share ties uniformly, preserve negative hedges and ignore insertion order");

        var offset=Run([a,b],Rows(["100","90","80","70","60"],["200","210","220","230","240"])).Partitions.Single();
        Equal(offset.ValueAtRisk,"0");Equal(offset.ExpectedShortfall,"0");
        if(offset.Samples.Length!=5||offset.Histogram.Length!=1||offset.Histogram[0].Count!=5)
            throw new Exception("An unchanged/duplicate scenario was dropped or drawn as a continuous density.");
        Equal(offset.Samples[0].VarWeight,"0.2");Equal(offset.Samples[0].EsWeight,"0.2");
        Console.WriteLine("PASS historical risk: net before quantiles, exact offsets, all-zero rows and duplicate probability mass");

        var negative=Run([a],Rows(["104","103","102","101"])).Partitions.Single();
        Equal(negative.ValueAtRisk,"-2");Equal(negative.ExpectedShortfall,"-1");
        var partitions=Run([a,b with{Currency=eur}],tied).Partitions;
        if(partitions.Length!=2)throw new Exception("Unlike currencies were netted.");
        if(Run([a,b with{Measure="other_pv"}],tied).Partitions.Length!=2)throw new Exception("Unlike valuation measures were netted.");
        Console.WriteLine("PASS historical risk: signed gain-only tails and independent currency/valuation partitions");

        Reject("risk_historical_samples",()=>HistoricalRisk.Calculate(history,["s0","s1"],policy,[a],Rows(["100"])));
        Reject("risk_historical_samples",()=>HistoricalRisk.Calculate(history,["s0","s1"],policy,[a],[new("s0",[new("a","100")]),new("s0",[new("a","90")])]));
        Reject("risk_historical_samples",()=>Run([a,b],Rows(["100","90"])));
        Reject("risk_historical_exposures",()=>Run([a,a],Rows(["100","90"])));
        Reject("risk_historical_exposures",()=>Run([a,a with{Id="b"}],Rows(["100","90"],["100","90"])));
        foreach(var invalid in new[]{policy with{Confidence="0.5"},policy with{Confidence="1"},policy with{Confidence="1e-2"},policy with{Horizon="ten_days"},policy with{ValuationPolicy="recalibrate_after_each_row"}})
            Reject("risk_historical_policy",()=>Run([a],Rows(["100","90"]),invalid));
        using var stop=new CancellationTokenSource();stop.Cancel();
        try{HistoricalRisk.Calculate(history,["s0","s1"],policy,[a],Rows(["100","90"]),stop.Token);throw new Exception("Cancelled distribution published.");}
        catch(OperationCanceledException){}
        Console.WriteLine("PASS historical risk: incomplete/duplicate samples, missing exposures, unknown conventions and cancellation refuse");

        var fixture=new JointScenarioRiskChecks.Fixture((x,y)=>x*y,"3");
        var samples=Enumerable.Range(0,513).Select(i=>fixture.Sample("r"+i,("-1","2"))).ToImmutableArray();
        samples=samples.SetItem(0,new("r0","Unchanged historical row",[]){Unchanged=true});
        var scenarios=fixture.Set([..samples]) with{Id=history,Version=HistoricalRiskPolicy.Version1};
        Reject("risk_scenarios",()=>RiskEngine.CalculateScenarios(fixture.Prepared,scenarios,RiskEngine.ExactNumericalPolicy));
        var priced=RiskEngine.CalculateHistoricalScenarios(fixture.Prepared,scenarios,RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
        var expectedHolding=fixture.Prepared.Holdings.Single();var pricedHolding=priced.Groups.Single().Positions.Single();
        if(expectedHolding.TradeId!=pricedHolding.TradeId||expectedHolding.BindingId!=pricedHolding.BindingId||expectedHolding.Currency!=pricedHolding.Value.Currency)
            throw new Exception("Independent prepared holding identities differ from full-PV coverage.");
        if(priced.Scenarios.Length!=513||priced.Scenarios.Select(s=>s.Id).Distinct().Count()!=513)
            throw new Exception("Repeated vectors lost distinct historical probability mass.");
        Equal(priced.Scenarios[0].Groups.Single().Totals.Single().Values.Loss.ToExactString(),"0");
        Equal(priced.Scenarios[1].Groups.Single().Totals.Single().Values.Loss.ToExactString(),"6");
        if(priced.Scenarios.Skip(1).Any(s=>s.Scenario.ParentId!=priced.BaseScenario.Id))
            throw new Exception("Historical rows accumulated shocks instead of branching from the frozen base.");
        Reject("risk_historical_policy",()=>RiskEngine.CalculateHistoricalScenarios(fixture.Prepared,scenarios with{Version="unknown"},RiskEngine.ExactNumericalPolicy));
        Console.WriteLine("PASS historical risk: bounded historical PV path retains513 siblings, nonlinear zero-Greek loss and unchanged rows without relaxing named scenarios");

        HistoricalRisk.Admit(128,2048);
        Reject("risk_historical_capacity",()=>HistoricalRisk.Admit(129,2));
        Reject("risk_historical_capacity",()=>HistoricalRisk.Admit(1,2049));
        Reject("risk_historical_capacity",()=>HistoricalRisk.Admit(0,2));
        ImmutableArray<HistoricalExposure> Holdings(int count)=>[..Enumerable.Range(0,count).Select(i=>a with{Id="holding"+i,BindingId=Guid.NewGuid()})];
        Reject("risk_historical_exposures",()=>HistoricalRisk.Calculate(history,["first","last"],policy,Holdings(4097),[]));
        Reject("risk_scenario_capacity",()=>HistoricalRisk.Calculate(history,[..Enumerable.Range(0,2048).Select(i=>"row"+i)],policy,Holdings(123),[]));
        Console.WriteLine("PASS historical risk: explicit factor/sample/native holding and complete-value bounds refuse oversized work");
    }
    static void Equal(string actual,string expected)
    {
        if(actual!=expected)throw new Exception($"Historical risk expected {expected}, got {actual}.");
    }
    static void Reject(string code,Action action)
    {
        try{action();}catch(MarketDataException e)when(e.Code==code){return;}
        throw new Exception("Expected historical risk refusal: "+code);
    }
}
