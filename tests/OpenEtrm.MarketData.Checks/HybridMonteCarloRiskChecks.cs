using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.Shared.Numerics;

internal static class HybridMonteCarloRiskChecks
{
    internal static MonteCarloRiskPolicy Policy(int paths=128)=>MonteCarloRiskChecks.Policy(paths) with{
        Convention=MonteCarloRiskPolicy.Hybrid,ValuationPolicy=MonteCarloRiskPolicy.HybridValuation,
        SamplingUncertainty=MonteCarloRiskPolicy.HybridUncertainty};
    internal static HybridMonteCarloPolicy Execution(int count=64,string seed="1")=>new(HybridMonteCarloPolicy.Version1,
        HybridMonteCarloPolicy.IndexPanel,count,seed,HybridMonteCarloPolicy.WholePortfolio);
    internal static PcaHybridPolicy Fitting(int kept=2,bool fallback=false)=>PcaHybridRiskChecks.Policy(kept) with{
        ValuationPolicy=fallback?PcaHybridPolicy.PanelFallbackQualification:PcaHybridPolicy.PanelQualification};
    internal static void Run()
    {
        var fixture=new ParametricRiskChecks.Fixture();
        var generation=MonteCarloRisk.Generate(fixture.History,Policy());
        var reference=MonteCarloRisk.Generate(fixture.History,MonteCarloRiskChecks.Policy(128));
        if(generation.Samples.Zip(reference.Samples).Any(pair=>pair.First.Id!=pair.Second.Id||pair.First.Index!=pair.Second.Index||!pair.First.Changes.SequenceEqual(pair.Second.Changes)))
            throw new Exception("Hybrid valuation changed the shared native shock stream.");
        foreach(var invalid in new[]{Policy() with{Convention=MonteCarloRiskPolicy.Version1},Policy() with{ValuationPolicy=HistoricalRiskPolicy.Valuation},
            Policy() with{SamplingUncertainty=MonteCarloRiskPolicy.Uncertainty}})
            Reject("risk_monte_carlo_policy",()=>MonteCarloRisk.Generate(fixture.History,invalid));
        Console.WriteLine("PASS hybrid MC: complete explicit mode tuple and every unchanged full-reference native shock");
        foreach(var (seed,expected) in new[]{("0",new[]{24,36,42,61,84,87,103,109}),("1",new[]{8,23,26,37,58,67,75,81}),
            ("18446744073709551615",new[]{55,62,71,73,97,105,109,122})})
        {
            var selected=HybridMonteCarloRisk.SelectPanel(generation,Execution(8,seed));
            if(!selected.Select(s=>s.Index).SequenceEqual(expected))throw new Exception("Panel indices differ from independent Python SHA256/byte-layout goldens.");
            var larger=HybridMonteCarloRisk.SelectPanel(generation,Execution(16,seed));
            if(selected.Any(s=>!larger.Contains(s)))throw new Exception("A larger explicit panel replaced its previously selected indices.");
        }
        var panel=HybridMonteCarloRisk.SelectPanel(generation,Execution());
        var changed=MonteCarloRisk.Generate(fixture.History,Policy() with{Seed="42"});
        if(!HybridMonteCarloRisk.SelectPanel(changed,Execution()).Select(s=>s.Index).SequenceEqual(panel.Select(s=>s.Index)))
            throw new Exception("Index selection depends on simulated shocks or the pricing stream.");
        if(!HybridMonteCarloRisk.SelectPanel(generation,Execution(128)).Select(s=>s.Id).SequenceEqual(generation.Samples.Select(s=>s.Id)))
            throw new Exception("All-path validation changed canonical sample order.");
        foreach(var invalid in new[]{Execution(1),Execution(129),Execution(seed:"01"),Execution(seed:"-1"),Execution(seed:"18446744073709551616"),
            Execution() with{ValidationPolicy="select_candidate_tails"},Execution() with{FallbackScope="replace_tail_rows"}})
            Reject("risk_hybrid_policy",()=>HybridMonteCarloRisk.Validate(invalid,Policy(),Fitting()));
        Reject("risk_monte_carlo_samples",()=>HybridMonteCarloRisk.SelectPanel(generation with{Samples=generation.Samples.RemoveAt(0)},Execution()));
        Console.WriteLine("PASS hybrid panel: independent index goldens, seed boundaries, nested counts, unchanged shock independence and canonical coverage");
        var exposure=new HistoricalExposure("quadratic",Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"present_value",fixture.Currency,"0");
        var identities=ImmutableArray.Create(new PcaValuationIdentity(exposure.Id,"shared"));
        var pca=PcaHybridRisk.Decompose(generation,Fitting());
        var plan=PcaHybridRisk.PrepareFit(pca,[ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01")]);
        string Value(ImmutableArray<ExactDecimal> x)
        {
            var a=Number(x[0].ToString());var b=Number(x[1].ToString());
            return ExactNumberText.Format(a+b*new SignedRatio(2,1)+a*a*new SignedRatio(3,1)+a*b*new SignedRatio(2,1)+b*b*new SignedRatio(4,1));
        }
        HistoricalObservation Price(string id,ImmutableArray<ExactDecimal> x)=>new(id,[new(exposure.Id,Value(x))]);
        var fitted=plan.Samples.Select(s=>Price(s.Id,s.Changes)).ToImmutableArray();
        var predicted=PcaHybridRisk.Predict(generation,pca,plan,[exposure],fitted);
        var full=generation.Samples.Select(s=>Price(s.Id,s.Changes)).ToImmutableArray();
        for(var i=0;i<full.Length;i++)PcaHybridRiskChecks.Near(PcaHybridRiskChecks.Value(predicted.Observations[i].Values[0].Value),PcaHybridRiskChecks.Value(full[i].Values[0].Value),1e-8);
        var compared=PcaHybridRisk.ComparePanel(generation,Execution(),pca,plan,[exposure],panel.Select(s=>Price(s.Id,s.Changes)).ToImmutableArray(),fitted,identities);
        if(compared.Classifications.Any(c=>c.Method!="pca_delta_gamma")||compared.Candidate.Single().Samples.Length!=64)
            throw new Exception("Exact retained quadratic failed its observed panel checks.");
        var all=PcaHybridRisk.ComparePanel(generation,Execution(128),pca,plan,[exposure],full,fitted,identities);
        if(all.Classifications.Any(c=>c.Method!="pca_delta_gamma")||!all.Candidate.Single().Samples.Select(s=>s.Id).SequenceEqual(generation.Samples.Select(s=>s.Id)))
            throw new Exception("All-path comparison lost the original full reference coverage.");
        Reject("risk_pca_hybrid_policy",()=>PcaHybridRisk.Compare(generation,pca,plan,[exposure],full,fitted,identities));
        Reject("risk_hybrid_policy",()=>PcaHybridRisk.ComparePanel(generation,Execution(),pca with{Policy=PcaHybridRiskChecks.Policy(2)},plan,[exposure],full,fitted,identities));
        Console.WriteLine("PASS hybrid valuation: independent exact quadratic full-N values, complete panel/model coverage and separate reference/panel modes");
        var reduced=MonteCarloRisk.Calculate(generation,[exposure],predicted.Observations);
        var fallbackReduced=MonteCarloRisk.Calculate(generation,[exposure],full);
        if(reduced.Diagnostics.Any(d=>d.ValueAtRiskInterval is not null)||fallbackReduced.Diagnostics.Any(d=>d.ValueAtRiskInterval is not null)||
            JsonSerializer.Serialize(reduced,new JsonSerializerOptions(JsonSerializerDefaults.Web)).Contains("valueAtRiskInterval",StringComparison.Ordinal))
            throw new Exception("Sample-dependent validation inherited a full-reference sampling coverage claim.");
        if(MonteCarloRisk.Calculate(reference,[exposure],full).Diagnostics.Any(d=>d.ValueAtRiskInterval is null))
            throw new Exception("The existing full-PV sampling diagnostic changed.");
        Console.WriteLine("PASS hybrid uncertainty: descriptive prefixes only, no inherited95% interval even on full-PV fallback; R6 unchanged");
        var truncated=PcaHybridRisk.Decompose(generation,Fitting(1));
        var small=PcaHybridRisk.PrepareFit(truncated,[ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01")]);
        var smallFit=small.Samples.Select(s=>Price(s.Id,s.Changes)).ToImmutableArray();
        var panelFull=panel.Select(s=>Price(s.Id,s.Changes)).ToImmutableArray();
        Reject("risk_pca_hybrid_qualification",()=>PcaHybridRisk.ComparePanel(generation,Execution(),truncated,small,[exposure],panelFull,smallFit,identities));
        var opted=PcaHybridRisk.ComparePanel(generation,Execution(),truncated with{Policy=Fitting(1,true)},small,[exposure],panelFull,smallFit,identities);
        if(opted.Classifications.All(c=>c.Method!="full_pv"))throw new Exception("Opted-in omitted curvature failure disappeared.");
        Reject("risk_historical_samples",()=>PcaHybridRisk.ComparePanel(generation,Execution(),pca,plan,[exposure],panelFull.RemoveAt(0),fitted,identities));
        Console.WriteLine("PASS hybrid validation: omitted nonlinear residual refuses by default, opt-in retains reasons, incomplete full-PV panel refuses");
        var omitted=generation.Samples.Where(s=>!panel.Any(p=>p.Id==s.Id)).Take(2).ToArray();
        HistoricalObservation Spike(string id,ImmutableArray<ExactDecimal> x)
        {
            var value=Number(Value(x));if(omitted.Any(s=>s.Changes.SequenceEqual(x)))value+=new SignedRatio(50000,1);
            return new(id,[new(exposure.Id,ExactNumberText.Format(value))]);
        }
        var spikeFit=plan.Samples.Select(s=>Spike(s.Id,s.Changes)).ToImmutableArray();
        var observed=PcaHybridRisk.ComparePanel(generation,Execution(),pca,plan,[exposure],panel.Select(s=>Spike(s.Id,s.Changes)).ToImmutableArray(),spikeFit,identities);
        var spikeFull=generation.Samples.Select(s=>Spike(s.Id,s.Changes)).ToImmutableArray();
        if(observed.Classifications.Any(c=>c.Method!="pca_delta_gamma")||MonteCarloRisk.Calculate(generation,[exposure],spikeFull).Partitions.Single().ExpectedShortfall==reduced.Partitions.Single().ExpectedShortfall)
            throw new Exception("The deliberately unpriced black-box spikes must demonstrate that panel success is not a tail guarantee.");
        Reject("risk_pca_hybrid_qualification",()=>PcaHybridRisk.ComparePanel(generation,Execution(128),pca,plan,[exposure],spikeFull,spikeFit,identities));
        using var stop=new CancellationTokenSource();stop.Cancel();
        try{HybridMonteCarloRisk.SelectPanel(generation,Execution(),stop.Token);throw new Exception("Cancelled panel succeeded.");}catch(OperationCanceledException){}
        Console.WriteLine("PASS hybrid limitation: unchanged off-panel black-box spikes can escape sampled validation; complete validation detects them");
        var calls=0;var domains=new JointScenarioRiskChecks.Fixture((x,_)=>{calls++;return x;});var before=calls;
        domains.Prepared.Scenario.ValidateShifts([domains.Shift(0,"1")]);
        Reject("price_domain",()=>domains.Prepared.Scenario.ValidateShifts([domains.Shift(0,"1000001")]));
        if(calls!=before)throw new Exception("Domain admission invoked instrument PV.");
        if(domains.Prepared.Scenario.Depth!=0)throw new Exception("Domain admission changed the captured parent.");
        Console.WriteLine("PASS hybrid domains: declared quote bounds refuse without pricing a trade or changing the parent");

    }
    static SignedRatio Number(string text)=>PcaHybridRiskChecks.Number(text);
    static void Reject(string code,Action action)
    {
        try{action();}catch(MarketDataException error)when(error.Code==code){return;}
        throw new Exception("Expected structured refusal "+code);
    }
}
