using System.Collections.Immutable;
using System.Security.Cryptography;
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
        var identities=prepared.Portfolio.Markets.SelectMany(m=>m.Connected.Holdings.Select(p=>new PcaValuationIdentity(
                HistoricalExposureId(new(p.BookId,p.TradeId,p.BindingId,m.Measures[p.Group],p.Currency)),
                Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{m.Snapshot.Definitions,p.ValuationId},RiskJobs.Json)))))).ToImmutableArray();
        PcaCalibration? pca=null;PcaFitPlan? plan=null;var attempted=false;RiskWork? work=null;
        try
        {
            pca=PcaHybridRisk.Decompose(generation,policy,ct);
            plan=PcaHybridRisk.PrepareFit(pca,[..prepared.History!.Columns.Select(c=>c.Mapping.Factor.TradingTick)],ct);
            var samples=plan.Samples.Select(s=>new PortfolioJointRisk.Sample(s.Id,s.Id,
                [..prepared.HistoricalAxes.Select((axis,i)=>new PortfolioCurveShift(axis,s.Changes[i])).Where(s=>s.Size!=default)])).ToImmutableArray();
            var study=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{generation.HistoryId,simulationPolicy=generation.Policy,analysisPolicy=policy,plan.PcDirections},RiskJobs.Json)));
            var set=new PortfolioScenarioSetRequest(study,MonteCarloRiskPolicy.Version1,ConnectedRiskScenarioSet.Independent,
                [..samples.Select(s=>new PortfolioScenarioRequest(s.Id,s.Label,[..s.Shifts.Select(x=>new CurveShiftRequest(x.Axis.Request,x.Size.ToString(),OpenEtrm.MarketData.Calculation.CurveShift.ParentAdditive))]))]);
            var fitPrepared=prepared with{Joint=new(new(PortfolioJointRiskRequest.Version1,set,null),samples,[],[])};
            var prior=ParentCounters(prepared.Portfolio);
            attempted=true;
            var fitted=EvaluateFullPvSamples(fitPrepared,MonteCarloRiskPolicy.Version1,ct);work=AdditionalPcaWork(prepared.Portfolio,fitted.Nominal.Work,prior);
            if(JsonSerializer.Serialize(reference.Exposures,RiskJobs.Json)!=JsonSerializer.Serialize(fitted.Exposures,RiskJobs.Json))
                throw new MarketDataException("risk_historical_exposures","Fitting changed the captured native holding identity or frozen base.");
            var comparison=PcaHybridRisk.Compare(generation,pca,plan,reference.Exposures,reference.Observations,fitted.Observations,identities,ct);
            return new(JsonSerializer.SerializeToNode(new{policy,pca,fit=new{plan.NativeSteps,plan.PcDirections,plan.Projection,plan.DirectionError,plan.ProjectionError,
                scenarioCount=plan.Samples.Length},finiteDifferenceValues=fitted.Observations,comparison,available=true,unavailableReasons=Array.Empty<object>(),fallbackClassifications=Array.Empty<PcaClassification>(),
                cost=new{mode=PcaHybridPolicy.Qualification,reference=reference.Nominal.Work,additional=work,attemptUnmetered=false}},RiskJobs.Json)!,work,false);
        }
        catch(MarketDataException error)when(PcaCandidateFailure(error))
        {
            ct.ThrowIfCancellationRequested();
            return new(UnavailablePcaReport(policy,pca,identities,reference.Nominal.Work,work,attempted,error),work,attempted&&work is null);
        }
    }
    internal static JsonNode UnavailablePcaReport(PcaHybridPolicy policy,PcaCalibration? pca,ImmutableArray<PcaValuationIdentity> identities,
        RiskWork reference,RiskWork? work,bool attempted,MarketDataException error)=>JsonSerializer.SerializeToNode(new{
            policy,pca,fit=(object?)null,finiteDifferenceValues=Array.Empty<object>(),comparison=(object?)null,available=false,
            unavailableReasons=new[]{new{error.Code,error.Message}},
            fallbackClassifications=identities.GroupBy(i=>i.ValuationKey,StringComparer.Ordinal).OrderBy(g=>g.Key,StringComparer.Ordinal)
                .Select(g=>new PcaClassification(g.Key,[..g.Select(i=>i.ExposureId).Order(StringComparer.Ordinal)],"full_pv",[error.Code])),
            cost=new{mode=PcaHybridPolicy.Qualification,reference,additional=work,attemptUnmetered=attempted&&work is null}},RiskJobs.Json)!;

}
