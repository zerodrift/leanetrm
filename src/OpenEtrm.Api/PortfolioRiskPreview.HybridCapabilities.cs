using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

public sealed partial class PortfolioRiskPreview
{
    private static object MonteCarloProfile(bool hybrid)=>new{
        convention=hybrid?MonteCarloRiskPolicy.Hybrid:MonteCarloRiskPolicy.Version1,numericalPolicy=MonteCarloRiskPolicy.Numerics,
        estimator=ParametricRiskPolicy.Sample,centering=ParametricRiskPolicy.Centered,horizon=ParametricRiskPolicy.Session,
        quantilePolicy=HistoricalRiskPolicy.Quantile,tailPolicy=HistoricalRiskPolicy.Tail,
        valuationPolicy=hybrid?MonteCarloRiskPolicy.HybridValuation:HistoricalRiskPolicy.Valuation,
        distribution=MonteCarloRiskPolicy.Gaussian,randomGenerator=MonteCarloRiskPolicy.Generator,shockPolicy=MonteCarloRiskPolicy.ShockRounding,
        samplingUncertainty=hybrid?MonteCarloRiskPolicy.HybridUncertainty:MonteCarloRiskPolicy.Uncertainty};
    private static object HybridCapabilities(PortfolioValuation.Prepared portfolio,long width,long nodes)
    {
        var dimensions=portfolio.Factors.Length;
        var capacities=Enumerable.Range(1,Math.Min(PcaHybridRisk.MaximumComponents,dimensions))
            .SelectMany(k=>new[]{2,8,16,32,64,128,256,512,1024}.SelectMany(m=>new[]{false,true}.Select(f=>new{
                components=k,validationPaths=m,fallback=f,scopeMaxPaths=HybridPathCapacity(width,portfolio.Ppa?.Rows??0,nodes,dimensions,k,m,f)}))).ToArray();
        var initial=capacities.Where(c=>!c.fallback&&c.components<=3&&c.validationPaths<=64&&c.scopeMaxPaths>=c.validationPaths)
            .OrderByDescending(c=>c.components).ThenByDescending(c=>c.validationPaths).FirstOrDefault();
        return new{
            profile=MonteCarloProfile(true),
            executionProfile=new{convention=HybridMonteCarloPolicy.Version1,validationPolicy=HybridMonteCarloPolicy.IndexPanel,fallbackScope=HybridMonteCarloPolicy.WholePortfolio},
            fittingPolicies=new{strict=PcaHybridPolicy.PanelQualification,fullPvFallback=PcaHybridPolicy.PanelFallbackQualification},
            defaults=new{retainedComponents=initial?.components??1,validationPaths=initial?.validationPaths??2,validationSeed="1",paths=Math.Max(2,Math.Min(4096,initial?.scopeMaxPaths??0))},
            capacities};
    }
}
