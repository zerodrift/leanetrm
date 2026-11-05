using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

public sealed partial class PortfolioRiskPreview
{
    private sealed record PcaHybridRun(JsonNode Report,RiskWork? AdditionalWork,bool AttemptUnmetered);
    internal static bool PcaCandidateFailure(MarketDataException error)=>error.Code is
        "risk_pca_hybrid_numeric" or "risk_monte_carlo_numeric" or "price_domain" or "option_input" or "numeric_range" or "precision_loss";
    private static ConnectedPreparationCounters ParentCounters(PortfolioValuation.Prepared portfolio)
    {
        var parts=portfolio.Markets.Select(m=>m.Connected.PreparationCounters).ToArray();
        return new(parts.Sum(p=>p.EvaluatedNodes),parts.Sum(p=>p.ReusedReads),parts.Sum(p=>p.VolumeComputations));
    }
    internal static RiskWork AdditionalPcaWork(PortfolioValuation.Prepared portfolio,RiskWork work,ConnectedPreparationCounters prior)=>work with{
        RebuiltCurveNodes=work.RebuiltCurveNodes-portfolio.MaterializationWork.RebuiltCurveNodes,
        ReusedCurveNodes=work.ReusedCurveNodes-portfolio.MaterializationWork.ReusedCurveNodes,
        PreparationScenarioCount=0,
        CurveGraphEvaluatedNodes=work.CurveGraphEvaluatedNodes-prior.EvaluatedNodes,
        CurveGraphReusedReads=work.CurveGraphReusedReads-prior.ReusedReads,
        VolumeComputations=work.VolumeComputations-prior.VolumeComputations};
    private static PcaHybridRun CalculatePcaHybrid(Prepared prepared,FullPvSamples reference,CancellationToken ct)
    {
        var generation=prepared.Simulation!;var policy=prepared.Request.MonteCarlo!.Analysis!;
        var identities=PcaIdentities(prepared);
        PcaCalibration? pca=null;PcaFitPlan? plan=null;var attempted=false;RiskWork? work=null;
        try
        {
            pca=PcaHybridRisk.Decompose(generation,policy,ct);
            plan=PcaHybridRisk.PrepareFit(pca,[..prepared.History!.Columns.Select(c=>c.Mapping.Factor.TradingTick)],ct);
            var fitPrepared=PreparePcaFit(prepared,plan);
            var prior=ParentCounters(prepared.Portfolio);
            attempted=true;
            var fitted=EvaluateFullPvSamples(fitPrepared,MonteCarloRiskPolicy.Version1,ct);work=AdditionalPcaWork(prepared.Portfolio,fitted.Nominal.Work,prior);
            RequireSameRiskExposures(reference.Exposures,fitted.Exposures);
            var comparison=PcaHybridRisk.Compare(generation,pca,plan,reference.Exposures,reference.Observations,fitted.Observations,identities,ct);
            return new(JsonSerializer.SerializeToNode(new{policy,pca,fit=new{plan.NativeSteps,plan.PcDirections,plan.Projection,plan.DirectionError,plan.ProjectionError,
                scenarioCount=plan.Samples.Length},finiteDifferenceValues=fitted.Observations,comparison,available=true,unavailableReasons=Array.Empty<object>(),fallbackClassifications=Array.Empty<PcaClassification>(),
                cost=new{mode=policy.ValuationPolicy,reference=reference.Nominal.Work,additional=work,attemptUnmetered=false}},RiskJobs.Json)!,work,false);
        }
        catch(MarketDataException error)when(PcaCandidateFailure(error))
        {
            ct.ThrowIfCancellationRequested();
            return new(UnavailablePcaReport(policy,pca,identities,reference.Nominal.Work,work,attempted,error),work,attempted&&work is null);
        }
    }
    internal static JsonNode UnavailablePcaReport(PcaHybridPolicy policy,PcaCalibration? pca,ImmutableArray<PcaValuationIdentity> identities,
        RiskWork reference,RiskWork? work,bool attempted,MarketDataException error)
    {
        PcaHybridRisk.RequireFullPvFallback(policy,error.Message);
        return JsonSerializer.SerializeToNode(new{
            policy,pca,fit=(object?)null,finiteDifferenceValues=Array.Empty<object>(),comparison=(object?)null,available=false,
            unavailableReasons=new[]{new{error.Code,error.Message}},
            fallbackClassifications=identities.GroupBy(i=>i.ValuationKey,StringComparer.Ordinal).OrderBy(g=>g.Key,StringComparer.Ordinal)
                .Select(g=>new PcaClassification(g.Key,[..g.Select(i=>i.ExposureId).Order(StringComparer.Ordinal)],"full_pv",[error.Code])),
            cost=new{mode=policy.ValuationPolicy,reference,additional=work,attemptUnmetered=attempted&&work is null}},RiskJobs.Json)!;
    }

}
