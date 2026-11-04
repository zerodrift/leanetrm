using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;

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
    }
    static void Reject(Action action){try{action();}catch(MarketDataException e)when(e.Code=="risk_monte_carlo_scope"){return;}throw new Exception("Expected Monte Carlo scope refusal.");}
}
