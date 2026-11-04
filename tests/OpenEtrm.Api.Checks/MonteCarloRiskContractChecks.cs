using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;

internal static class MonteCarloRiskContractChecks
{
    internal static void Run()
    {
        // Use an existing explicit historical envelope solely to obtain its
        // transport fixture; a Monte Carlo request must have its own method.
        var context=new FuturesReadRequest(Guid.NewGuid(),"trading_workspace_v1","retained",Guid.NewGuid());
        var history=new PortfolioHistoryRequest(new(context,[Guid.NewGuid()],[],[Guid.NewGuid()]),null!,null!,default,
            "2026-01-01","2026-02-01","end_of_day","reject","reject",new('a',64));
        var p=new MonteCarloRiskPolicy(MonteCarloRiskPolicy.Version1,MonteCarloRiskPolicy.Numerics,ParametricRiskPolicy.Sample,
            ParametricRiskPolicy.Centered,ParametricRiskPolicy.Zero,ParametricRiskPolicy.Session,HistoricalRiskPolicy.Quantile,
            HistoricalRiskPolicy.Tail,HistoricalRiskPolicy.Valuation,"0.99",MonteCarloRiskPolicy.Gaussian,MonteCarloRiskPolicy.Generator,
            MonteCarloRiskPolicy.ShockRounding,"42",4096,MonteCarloRiskPolicy.Uncertainty);
        var spec=new PortfolioMonteCarloRequest(history,p);var outer=PortfolioRiskPreview.MonteCarloRequest(spec);
        PortfolioRiskPreview.ValidateMonteCarlo(outer,spec);
        if(outer.Detail!="holdings"||!outer.Bumps.IsEmpty||outer.Historical is not null||outer.Parametric is not null||outer.MonteCarlo!=spec)
            throw new Exception("Full Monte Carlo became a historical/FD envelope.");
        foreach(var bad in new[]{outer with{Detail="books"},outer with{TradeIds=[]},outer with{Historical=new(history,null!)}})
            Reject(()=>PortfolioRiskPreview.ValidateMonteCarlo(bad,spec));
        Reject(()=>PortfolioRiskPreview.ValidateMonteCarlo(outer,spec with{History=history with{ExpectedManifestId=null}}));
        var json=JsonSerializer.Serialize(outer,RiskJobs.Json);
        if(JsonSerializer.Serialize(JsonSerializer.Deserialize<PortfolioRiskRequest>(json,RiskJobs.Json),RiskJobs.Json)!=json)
            throw new Exception("Monte Carlo request does not roundtrip exactly.");
        try{JsonSerializer.Deserialize<PortfolioMonteCarloRequest>(JsonSerializer.Serialize(spec,RiskJobs.Json)[..^1]+",\"unknown\":true}",RiskJobs.Json);throw new Exception("Unknown field accepted.");}
        catch(JsonException){}
        if(JsonSerializer.Serialize(outer with{MonteCarlo=null},RiskJobs.Json).Contains("monteCarlo",StringComparison.Ordinal))
            throw new Exception("Absent Monte Carlo changed legacy request bytes.");
        Console.WriteLine("PASS Monte Carlo API: exclusive full-holdings method, exact scope/roundtrip and unknown-field refusal");
        var ordinary=PortfolioRiskPreview.MonteCarloPathCapacity(2,0,0,1);
        var options=PortfolioRiskPreview.MonteCarloPathCapacity(2,0,50000,1);
        var holdings=PortfolioRiskPreview.MonteCarloPathCapacity(500,0,0,1);
        if(ordinary!=16384||options!=1999||holdings!=499||PortfolioRiskPreview.MonteCarloPathCapacity(2,2_000_000,0,1)!=999)
            throw new Exception("Visible simulation capacity does not include the frozen base and complete native/option/PPA work.");
        Console.WriteLine("PASS Monte Carlo setup: selected-scope path capacity admits base plus complete scenario work");
        var analysis=new PcaHybridPolicy(PcaHybridPolicy.Version1,PcaHybridPolicy.Decomposition,PcaHybridPolicy.Scaling,PcaHybridPolicy.Residual,
            PcaHybridPolicy.Qualification,PcaHybridPolicy.Directions,1,1,"0.0001","0","0.001");
        var selected=spec with{Analysis=analysis};PortfolioRiskPreview.ValidateMonteCarlo(outer with{MonteCarlo=selected},selected);
        foreach(var invalid in new[]{analysis with{ValuationPolicy="panel_validation"},analysis with{ValuationPolicy="full_reference_qualification_v1"},analysis with{ValuationPolicy=null!},analysis with{RetainedComponents=0},analysis with{LossRelative="000.1"},analysis with{LossAbsolute="-1"}})
        {try{PortfolioRiskPreview.ValidateMonteCarlo(outer,spec with{Analysis=invalid});throw new Exception("Invalid PCA policy succeeded.");}catch(MarketDataException error)when(error.Code=="risk_pca_hybrid_policy"){}}
        PcaHybridRisk.ValidatePolicy(analysis with{LossAbsolute="-0.0",LossRelative="0.00100"});
        if(PortfolioRiskPreview.MonteCarloPathCapacity(2,0,50000,1,1+PcaHybridRisk.FittingScenarios(1,1))!=1986)
            throw new Exception("PCA setup did not reserve the complete fitting base and sibling work before simulation PV.");
        Console.WriteLine("PASS PCA / hybrid API: explicit optional full-reference policy, tolerance syntax parity and complete fitting work capacity");
        foreach(var code in new[]{"risk_historical_exposures","risk_historical_numeric","risk_parametric_history","risk_history_manifest","risk_dependency","risk_parametric_delta","access_denied","risk_pca_hybrid_policy"})
            if(PortfolioRiskPreview.PcaCandidateFailure(new(code,"Unavailable input")))throw new Exception("An input, policy or authority failure became optional analysis fallback.");
        foreach(var code in new[]{"risk_pca_hybrid_numeric","price_domain","option_input","numeric_range","precision_loss"})
            if(!PortfolioRiskPreview.PcaCandidateFailure(new(code,"Candidate-only numeric refusal")))throw new Exception("A candidate-only pricing refusal discarded the full reference.");
        Console.WriteLine("PASS PCA / hybrid fallback boundary: candidate numeric/domain refusal remains distinct from coverage, policy and authority failure");
        var priorWork=new RiskWork(2,2,1,3,6,0,0);
        try
        {
            PortfolioRiskPreview.UnavailablePcaReport(analysis,null,[new("future","future")],priorWork,null,true,new("price_domain","Fitting refused."));
            throw new Exception("A fitting failure substituted full PV without opt-in.");
        }
        catch(MarketDataException error)when(error.Code=="risk_pca_hybrid_qualification"){}
        foreach(var code in new[]{"risk_pca_hybrid_numeric","risk_monte_carlo_numeric","price_domain","option_input","numeric_range","precision_loss"})
        {
            try
            {
                PortfolioRiskPreview.UnavailablePcaReport(analysis,null,[new("future","future")],priorWork,null,true,new(code,"Fitting refused."));
                throw new Exception("Candidate-only failure returned a replacement without consent: "+code);
            }
            catch(MarketDataException error)when(error.Code=="risk_pca_hybrid_qualification"){}
        }
        var fallbackAnalysis=analysis with{ValuationPolicy=PcaHybridPolicy.FallbackQualification};
        PcaHybridRisk.ValidatePolicy(fallbackAnalysis);
        var failure=PortfolioRiskPreview.UnavailablePcaReport(fallbackAnalysis,null,[new("future","future"),new("option_a","option"),new("option_b","option")],priorWork,null,true,new("price_domain","A fitting direction is outside its declared domain."));
        var classifications=failure["fallbackClassifications"]!.AsArray();
        if(failure["available"]!.GetValue<bool>()||classifications.Count!=2||classifications.Any(c=>c!["method"]!.GetValue<string>()!="full_pv")||
            classifications.Sum(c=>c!["exposureIds"]!.AsArray().Count)!=3||!failure["cost"]!["attemptUnmetered"]!.GetValue<bool>())
            throw new Exception("A failed mixed fitting batch did not explicitly fall back every valuation and all shared bindings.");
        var fixture=new PpaAcceptanceFixture();var physical=fixture.Physical(volume:true);var market=fixture.Market(fixture.From.AddDays(-1));
        var nextSource=fixture.Source with{Key=new(fixture.Definitions,"second_output")};
        var nextSide=new PowerPhysicalSide(Guid.NewGuid(),fixture.Terms(new VolumeQuantity([nextSource.Key],QuantityStage.Generated,fixture.Energy.Key)),fixture.Asset.Location);
        var next=new PpaInstrument(Guid.NewGuid(),fixture.Definitions,fixture.Reference,
            physical.Memberships.SetItem(0,physical.Memberships[0] with{Side=nextSide}),physical.Discounting,physical.TermImplementation);
        var interval=fixture.Prepare(market,[physical]);
        _=interval.Session(market.Build).Value(physical,new(fixture.From,fixture.To),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
        if(interval.VolumeComputations<1)throw new Exception("The PPA accounting fixture must perform actual cached volume computation.");
        var preparation=new PortfolioValuation.Prepared(Guid.NewGuid(),null!,null!,[],[],[],[])
            {MaterializationWork=new(3,7,0,0),MaterializationScenarioCount=2};
        var completed=new RiskWork(1,1,1,13,13,3,7){PreparationScenarioCount=2,CurveGraphEvaluatedNodes=18,CurveGraphReusedReads=21,
            VolumeComputations=interval.VolumeComputations,QuantityApplications=13};
        var added=PortfolioRiskPreview.AdditionalPcaWork(preparation,completed,new(5,11,interval.VolumeComputations));
        if(added.VolumeComputations!=0||added.CurveGraphEvaluatedNodes!=13||added.CurveGraphReusedReads!=10||added.PreparationScenarioCount!=0||
            added.RebuiltCurveNodes!=0||added.ReusedCurveNodes!=0||added.UnitPricerCalls!=13||added.QuantityApplications!=13)
            throw new Exception("Prior shared volume / graph / materialization work was charged again or fitting PV work disappeared.");
        // Frozen fitting reuses prepared quantities. Exercise preservation of a
        // subsequently measured increment using a second real volume operation.
        var nextInterval=fixture.Prepare(market,[next],extraSources:[nextSource]);
        _=nextInterval.Session(market.Build).Value(next,new(fixture.From,fixture.To),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
        if(nextInterval.VolumeComputations<1)throw new Exception("The new operation must perform actual volume computation.");
        var later=PortfolioRiskPreview.AdditionalPcaWork(preparation,completed with{VolumeComputations=interval.VolumeComputations+nextInterval.VolumeComputations},new(5,11,interval.VolumeComputations));
        if(later.VolumeComputations!=nextInterval.VolumeComputations)throw new Exception("New measured volume work disappeared when previous cached work was subtracted.");
        Console.WriteLine("PASS PCA / hybrid failed-fit receipt and added work: all mixed bindings fall back; real PPA cached volumes are counted once");

    }
    static void Reject(Action action){try{action();}catch(MarketDataException e)when(e.Code=="risk_monte_carlo_scope"){return;}throw new Exception("Expected Monte Carlo scope refusal.");}
}
