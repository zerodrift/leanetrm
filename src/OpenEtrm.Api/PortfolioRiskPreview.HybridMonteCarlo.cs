using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

public sealed partial class PortfolioRiskPreview
{
    private sealed record HybridMonteCarloRun(FullPvSamples Values,MonteCarloRiskReport Result,JsonNode Report,
        ImmutableArray<RiskWork> Work,bool AttemptUnmetered);
    internal static int HybridPathCapacity(long width,long rows,long nodes,int factors,int components,int validation,bool fallback)
    {
        if(width<1||rows<0||nodes<0||factors<1||components<1||components>Math.Min(factors,PcaHybridRisk.MaximumComponents)||validation<2)return 0;
        var fixedPv=2L+PcaHybridRisk.FittingScenarios(factors,components)+validation;
        var capacity=Math.Min((long)MonteCarloRisk.MaximumPaths,MonteCarloRisk.MaximumShockValues/factors);
        // The N-row predicted matrix is retained even though those paths are not
        // all priced. Output admission is independent of full-PV scenario work.
        capacity=Math.Min(capacity,(RiskEngine.MaxJointResultValues/width-fixedPv-(fallback?1:0))/(fallback?2:1));
        foreach(var (perScenario,limit) in new[]{(rows,CloseRiskAdmission.MaximumRowScenarios),(nodes,PortfolioJointCalculation.MaximumOptionTreeNodes)})
            if(perScenario>0)
            {
                var room=limit/perScenario-fixedPv;
                if(room<0)return 0;
                if(fallback)capacity=Math.Min(capacity,room-1);
            }
        return (int)Math.Max(0,capacity);
    }
    private static ImmutableArray<PcaValuationIdentity> PcaIdentities(Prepared prepared)=>
        [..prepared.Portfolio.Markets.SelectMany(m=>m.Connected.Holdings.Select(p=>new PcaValuationIdentity(
            HistoricalExposureId(new(p.BookId,p.TradeId,p.BindingId,m.Measures[p.Group],p.Currency)),
            Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{m.Snapshot.Definitions,p.ValuationId},RiskJobs.Json))))))];
    private static Prepared PreparePcaFit(Prepared prepared,PcaFitPlan plan)
    {
        var generation=prepared.Simulation!;var policy=prepared.Request.MonteCarlo!.Analysis!;
        var samples=plan.Samples.Select(s=>new PortfolioJointRisk.Sample(s.Id,s.Id,
            [..prepared.HistoricalAxes.Select((axis,i)=>new PortfolioCurveShift(axis,s.Changes[i])).Where(s=>s.Size!=default)])).ToImmutableArray();
        var study=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{generation.HistoryId,simulationPolicy=generation.Policy,analysisPolicy=policy,plan.PcDirections},RiskJobs.Json)));
        var set=new PortfolioScenarioSetRequest(study,MonteCarloRiskPolicy.Version1,ConnectedRiskScenarioSet.Independent,
            [..samples.Select(s=>new PortfolioScenarioRequest(s.Id,s.Label,[..s.Shifts.Select(x=>new CurveShiftRequest(x.Axis.Request,x.Size.ToString(),CurveShift.ParentAdditive))]))]);
        return prepared with{Joint=new(new(PortfolioJointRiskRequest.Version1,set,null),samples,[],[])};
    }
    private static object MonteCarloMatrix(FullPvSamples values)=>new{columns=values.Exposures.Select(e=>e.Id),rows=values.Observations.Select(row=>
    {
        var byId=row.Values.ToDictionary(v=>v.ExposureId,StringComparer.Ordinal);
        return new{row.Id,scenarioValues=values.Exposures.Select(e=>byId[e.Id].Value),
            losses=values.Exposures.Select(e=>ExactMoney.Parse(e.BaseValue,e.Currency).Subtract(ExactMoney.Parse(byId[e.Id].Value,e.Currency)).ToExactString())};
    })};
    private static long ValidateHybridDomains(Prepared prepared,CancellationToken ct)
    {
        long count=0;
        foreach(var sample in prepared.Joint!.Samples)
            foreach(var market in prepared.Portfolio.Markets)
            {
                ct.ThrowIfCancellationRequested();
                var shifts=sample.Shifts.Where(s=>s.Axis.Market==market.Snapshot.Definitions)
                    .Select(s=>new CurveShift(s.Axis.Coordinate,s.Axis.Target,s.Axis.Unit,s.Size,CurveShift.ParentAdditive)).ToImmutableArray();
                if(!shifts.IsEmpty)market.Connected.Scenario.ValidateShifts(shifts,ct);
                count+=shifts.Length;
            }
        return count;
    }
    internal static void RequireSameRiskExposures(ImmutableArray<HistoricalExposure> expected,ImmutableArray<HistoricalExposure> actual)
    {
        if(!expected.SequenceEqual(actual))
            throw new MarketDataException("risk_historical_exposures","Fitting, validation and fallback must reproduce the complete captured base holding keys, units and values.");
    }
    private static HybridMonteCarloRun CalculateHybridMonteCarlo(Prepared prepared,CancellationToken ct)
    {
        var generation=prepared.Simulation!;var spec=prepared.Request.MonteCarlo!;var execution=spec.Hybrid!;var policy=spec.Analysis!;
        HybridMonteCarloRisk.Validate(execution,generation.Policy,policy);
        var panel=HybridMonteCarloRisk.SelectPanel(generation,execution,ct);
        // Every declared quote/unit domain is admitted, including unpriced rows.
        // A generated invalid quote is fatal and cannot invoke optional fallback.
        var domainChecks=ValidateHybridDomains(prepared,ct);
        var identities=PcaIdentities(prepared);PcaCalibration? pca=null;PcaFitPlan? plan=null;
        FullPvSamples? fitted=null;FullPvSamples? validated=null;PcaHybridPrediction? prediction=null;PcaHybridComparison? comparison=null;
        MonteCarloRiskReport? candidateRisk=null;MarketDataException? refusal=null;
        ImmutableArray<HistoricalExposure>? firstExposures=null;
        var work=ImmutableArray.CreateBuilder<RiskWork>();var phases=new List<object>();var attempting=false;var unmetered=false;
        FullPvSamples Price(Prepared pass,string phase)
        {
            var prior=ParentCounters(prepared.Portfolio);attempting=true;
            var run=EvaluateFullPvSamples(pass,MonteCarloRiskPolicy.Version1,ct);attempting=false;
            if(firstExposures is {} original)RequireSameRiskExposures(original,run.Exposures);else firstExposures=run.Exposures;
            var cost=work.Count==0?run.Nominal.Work:AdditionalPcaWork(prepared.Portfolio,run.Nominal.Work,prior);
            work.Add(cost);phases.Add(new{phase,work=cost});return run;
        }
        try
        {
            pca=PcaHybridRisk.Decompose(generation,policy,ct);
            plan=PcaHybridRisk.PrepareFit(pca,[..prepared.History!.Columns.Select(c=>c.Mapping.Factor.TradingTick)],ct);
            fitted=Price(PreparePcaFit(prepared,plan),"fitting");
            prediction=PcaHybridRisk.Predict(generation,pca,plan,fitted.Exposures,fitted.Observations,ct);
            try{candidateRisk=MonteCarloRisk.Calculate(generation,fitted.Exposures,prediction.Observations,ct);}
            catch(MarketDataException error)when(error.Code=="risk_historical_numeric")
            {throw new MarketDataException("risk_pca_hybrid_numeric","The approximate distribution exceeded its exact arithmetic bound.",error);}
            var panelIds=panel.Select(s=>s.Id).ToHashSet(StringComparer.Ordinal);
            var selected=prepared.Joint!.Samples.Where(s=>panelIds.Contains(s.Id)).ToImmutableArray();
            var panelPrepared=prepared with{Joint=prepared.Joint with{Samples=selected}};
            validated=Price(panelPrepared,"validation");
            comparison=PcaHybridRisk.ComparePanel(generation,execution,pca,plan,fitted.Exposures,validated.Observations,fitted.Observations,identities,ct);
            if(comparison.Classifications.Any(c=>c.Method=="full_pv"))
                refusal=new("risk_hybrid_validation","One or more observed model, holding or native portfolio checks failed.");
        }
        catch(MarketDataException error)when(PcaCandidateFailure(error))
        {
            ct.ThrowIfCancellationRequested();refusal=error;unmetered=attempting;
        }
        // Full repricing is outside the candidate-only catch. A failure here is
        // fatal; there is no clipping, retry with other draws or changed policy.
        FullPvSamples values;MonteCarloRiskReport result;string method;
        if(refusal is not null)
        {
            PcaHybridRisk.RequireFullPvFallback(policy,refusal.Message);
            values=Price(prepared,"consented_full_pv_fallback");
            result=MonteCarloRisk.Calculate(generation,values.Exposures,values.Observations,ct);method="full_pv";
        }
        else
        {
            values=fitted! with{Observations=prediction!.Observations};result=candidateRisk!;method="pca_delta_gamma";
        }
        var classifications=identities.GroupBy(i=>i.ValuationKey,StringComparer.Ordinal).OrderBy(g=>g.Key,StringComparer.Ordinal)
            .Select(g=>new PcaClassification(g.Key,[..g.Select(i=>i.ExposureId).Order(StringComparer.Ordinal)],method,refusal is null?[]:[refusal.Code])).ToArray();
        var receipt=JsonSerializer.SerializeToNode(new{
            policy=execution,fittingPolicy=policy,appliedMethod=method,approximate=method=="pca_delta_gamma",
            fallbackApplied=refusal is not null,fallbackReasons=refusal is null?Array.Empty<object>():[new{refusal.Code,refusal.Message}],classifications,
            pca,fit=plan is null?null:new{plan.NativeSteps,plan.PcDirections,plan.Projection,plan.DirectionError,plan.ProjectionError,scenarioCount=plan.Samples.Length},
            finiteDifferenceValues=fitted?.Observations??[],models=prediction?.Models??[],maximumResidualLinearPnl=prediction?.MaximumResidualLinearPnl??[],
            validation=new{selectedSamples=panel.Select(s=>new{s.Id,s.Index}),available=comparison is not null,matrix=validated is null?null:MonteCarloMatrix(validated),comparison,
                scope="observed_panel_only_v1",observedPanelPaths=validated?.Observations.Length??0,outsideValidationPanel=generation.Samples.Length-panel.Length,
                unpricedPaths=method=="full_pv"?0:generation.Samples.Length-(validated is null?0:panel.Length),quoteDomainPaths=generation.Samples.Length,quoteDomainChecks=domainChecks},
            cost=new{phases,predictedValues=prediction is null?0L:(long)generation.Samples.Length*fitted!.Exposures.Length,attemptUnmetered=unmetered}
        },RiskJobs.Json)!;
        return new(values,result,receipt,work.ToImmutable(),unmetered);
    }
}
