using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Risk;

public sealed record PcaFitSample(string Id,ImmutableArray<ExactDecimal> Changes);
public sealed record PcaFitPlan(ImmutableArray<string> NativeSteps,ImmutableArray<ImmutableArray<ExactDecimal>> PcDirections,
    ImmutableArray<ImmutableArray<string>> Projection,string DirectionError,string ProjectionError,ImmutableArray<PcaFitSample> Samples);
public sealed record PcaValuationIdentity(string ExposureId,string ValuationKey);
public sealed record PcaHoldingModel(string ExposureId,ImmutableArray<string> Delta,ImmutableArray<ImmutableArray<string>> Gamma,bool Stable);
public sealed record PcaExposureComparison(string ExposureId,string FullValueAtRisk,string FullExpectedShortfall,string CandidateValueAtRisk,string CandidateExpectedShortfall,string MaximumAbsoluteError,string MaximumTailError,string MaximumResidualLinearPnl,bool LossesPass);
public sealed record PcaClassification(string ValuationKey,ImmutableArray<string> ExposureIds,string Method,ImmutableArray<string> Reasons);
public sealed record PcaPartitionComparison(string Measure,ReferenceKey Currency,string FullValueAtRisk,string FullExpectedShortfall,
    string CandidateValueAtRisk,string CandidateExpectedShortfall,string HybridValueAtRisk,string HybridExpectedShortfall,
    string MaximumCandidateError,string MaximumCandidateTailError,bool PortfolioFallback);
public sealed record PcaHybridComparison(ImmutableArray<PcaHoldingModel> Models,ImmutableArray<PcaExposureComparison> Exposures,
    ImmutableArray<PcaClassification> Classifications,ImmutableArray<PcaPartitionComparison> Partitions,
    ImmutableArray<HistoricalPartition> Candidate,ImmutableArray<HistoricalPartition> Hybrid);

public static partial class PcaHybridRisk
{
    private static readonly SignedRatio One=new(1,1),Two=new(2,1),Four=new(4,1);
    private static SignedRatio Number(string value)=>RiskBinary64.Number(value);
    private static SignedRatio Abs(SignedRatio x)=>x.Numerator.Sign<0?new(-x.Numerator,x.Denominator):x;
    private static SignedRatio Max(SignedRatio x,SignedRatio y)=>x.CompareTo(y)>=0?x:y;
    private static SignedRatio Divide(SignedRatio x,SignedRatio y)=>x*new SignedRatio(y.Denominator,y.Numerator);
    private static string Text(SignedRatio value)
    {
        if(value.Numerator.GetBitLength()>4096||value.Denominator.GetBitLength()>4096)throw Numeric("A retained hybrid coefficient or predicted value exceeds 4096 bits.");
        return ExactNumberText.Format(value);
    }
    private static bool Within(SignedRatio a,SignedRatio b,SignedRatio absolute,SignedRatio relative)=>
        Abs(a-b).CompareTo(absolute+relative*Max(Abs(a),Abs(b)))<=0;
    private static string NativeId(int axis,int step,int sign)=>$"native/{axis}/{step}/{sign}";
    private static string PcId(int axis,int step,int sign)=>$"pc/{axis}/{step}/{sign}";
    private static string MixedId(int a,int b,int step,int corner)=>$"cross/{a}/{b}/{step}/{corner}";

    private static EmpiricalRiskResult ReduceQualification(string historyId,ImmutableArray<string> samples,string confidence,
        ImmutableArray<HistoricalExposure> exposures,ImmutableArray<HistoricalObservation> observations,CancellationToken cancellation)
    {
        try{return EmpiricalRisk.Calculate(historyId,samples,confidence,exposures,observations,cancellation);}
        catch(MarketDataException error)when(error.Code=="risk_historical_numeric")
        {throw new MarketDataException("risk_pca_hybrid_numeric","The optional empirical qualification exceeded its exact arithmetic bound.",error);}
    }

    public static PcaFitPlan PrepareFit(PcaCalibration pca,ImmutableArray<ExactDecimal> tradingTicks,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        var policy=pca.Policy;var d=pca.Columns.Length;var k=policy.RetainedComponents;ValidatePolicy(policy,d);
        if(tradingTicks.IsDefault||tradingTicks.Length!=d)throw Invalid("Every factor requires its explicit native trading tick.");
        var steps=tradingTicks.Select(t=>
        {
            try{return ParametricRisk.ResolveStep(t,policy.TradingTicks);}
            catch(MarketDataException error)when(error.Code=="risk_parametric_delta")
            {throw Numeric("A native trading-tick step cannot represent its exact half, nominal and double fitting directions.");}
        }).ToImmutableArray();
        var scales=pca.Scales.Select(RiskBinary64.Real).ToArray();var vectors=pca.Loadings.Select(row=>row.Select(RiskBinary64.Real).ToArray()).ToArray();
        var directionError=0d;const double pcStep=.01;
        var directions=Enumerable.Range(0,k).Select(axis=>Enumerable.Range(0,d).Select(i=>
        {
            var ideal=pcStep*scales[i]*vectors[i][axis];
            // Seventeen places make the exact half representable at eighteen.
            var rounded=RiskBinary64.Fraction(ideal).Round(17,MidpointRounding.ToEven);
            if(ideal!=0&&rounded.Numerator.IsZero)throw Numeric("A nonzero PC direction rounds to zero at the selected collinear native boundary.");
            var native=ExactDecimal.Parse(ExactNumberText.Format(rounded));
            _=native.DivideExact(ExactDecimal.Parse("2"));_=native*ExactDecimal.Parse("2");
            directionError=Math.Max(directionError,Math.Abs(RiskBinary64.Real(native.ToString())/scales[i]/pcStep-vectors[i][axis]));
            return native;
        }).ToImmutableArray()).ToImmutableArray();
        if(directionError>1e-10)throw Numeric("Quantization rotates a standardized PC direction beyond the explicit 1e-10 bound.");
        // Weighted least squares uses the ACTUAL quantized directions in
        // standardized coordinates. Native-unit changes do not redefine its metric.
        var e=Enumerable.Range(0,d).Select(i=>Enumerable.Range(0,k).Select(j=>RiskBinary64.Real(directions[j][i].ToString())/scales[i]/pcStep).ToArray()).ToArray();
        var augmented=Enumerable.Range(0,k).Select(i=>Enumerable.Range(0,2*k).Select(j=>j<k?Enumerable.Range(0,d).Sum(n=>e[n][i]*e[n][j]):j-k==i?1d:0d).ToArray()).ToArray();
        for(var axis=0;axis<k;axis++)
        {
            var pivot=Enumerable.Range(axis,k-axis).OrderByDescending(i=>Math.Abs(augmented[i][axis])).ThenBy(i=>i).First();
            if(Math.Abs(augmented[pivot][axis])<.5)throw Numeric("Quantized PC directions lost their qualified independent geometry.");
            (augmented[axis],augmented[pivot])=(augmented[pivot],augmented[axis]);var divisor=augmented[axis][axis];
            for(var j=0;j<2*k;j++)augmented[axis][j]/=divisor;
            for(var i=0;i<k;i++)if(i!=axis){var multiplier=augmented[i][axis];for(var j=0;j<2*k;j++)augmented[i][j]-=multiplier*augmented[axis][j];}
        }
        var projection=Enumerable.Range(0,k).Select(i=>Enumerable.Range(0,d).Select(j=>Enumerable.Range(0,k).Sum(n=>augmented[i][n+k]*e[j][n])/pcStep/scales[j]).ToArray()).ToArray();
        var projectionError=0d;
        for(var i=0;i<k;i++)for(var j=0;j<k;j++)
            projectionError=Math.Max(projectionError,Math.Abs(Enumerable.Range(0,d).Sum(n=>projection[i][n]*RiskBinary64.Real(directions[j][n].ToString()))-(i==j?1:0)));
        if(!double.IsFinite(projectionError)||projectionError>1e-10)throw Numeric("The actual-direction projection failed its reconstruction check.");
        var samples=ImmutableArray.CreateBuilder<PcaFitSample>();var multipliers=new[]{ExactDecimal.Parse("0.5"),ExactDecimal.Parse("1"),ExactDecimal.Parse("2")};
        for(var axis=0;axis<d;axis++)for(var step=0;step<3;step++)for(var sign=0;sign<2;sign++)
        {
            var x=Enumerable.Repeat(default(ExactDecimal),d).ToArray();x[axis]=steps[axis]*multipliers[step];if(sign==1)x[axis]=-x[axis];
            samples.Add(new(NativeId(axis,step,sign),[..x]));
        }
        for(var axis=0;axis<k;axis++)for(var step=0;step<3;step++)for(var sign=0;sign<2;sign++)
            samples.Add(new(PcId(axis,step,sign),[..directions[axis].Select(x=>(sign==0?x:-x)*multipliers[step])]));
        // PC axes overlap native inputs. Combine each mixed corner by canonical
        // factor index BEFORE applying one shared market scenario.
        for(var a=0;a<k;a++)for(var b=a+1;b<k;b++)for(var step=0;step<3;step++)for(var corner=0;corner<4;corner++)
        {
            cancellation.ThrowIfCancellationRequested();
            samples.Add(new(MixedId(a,b,step,corner),[..Enumerable.Range(0,d).Select(i=>
                ((corner<2?directions[a][i]:-directions[a][i])+(corner%2==0?directions[b][i]:-directions[b][i]))*multipliers[step])]));
        }
        return new([..steps.Select(x=>x.ToString())],directions,[..projection.Select(row=>row.Select(RiskBinary64.Text).ToImmutableArray())],
            RiskBinary64.Text(directionError),RiskBinary64.Text(projectionError),samples.ToImmutable());
    }

    public static PcaHybridComparison Compare(MonteCarloGeneration generation,PcaCalibration pca,PcaFitPlan plan,
        ImmutableArray<HistoricalExposure> exposures,ImmutableArray<HistoricalObservation> full,
        ImmutableArray<HistoricalObservation> fitted,ImmutableArray<PcaValuationIdentity> identities,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();ValidatePolicy(pca.Policy,generation.Calibration.Columns.Length);
        var policy=pca.Policy;var d=pca.Columns.Length;var k=policy.RetainedComponents;
        var ids=generation.Samples.Select(s=>s.Id).ToImmutableArray();
        var reference=EmpiricalRisk.Calculate(generation.HistoryId,ids,generation.Policy.Confidence,exposures,full,cancellation);
        if(identities.Length!=exposures.Length||identities.Select(i=>i.ExposureId).Distinct().Count()!=exposures.Length||
            !identities.Select(i=>i.ExposureId).ToHashSet().SetEquals(exposures.Select(e=>e.Id))||identities.Any(i=>string.IsNullOrWhiteSpace(i.ValuationKey)))
            throw Invalid("Every native exposure requires exactly one complete unit-valuation identity.");
        var exposureIds=exposures.Select(e=>e.Id).ToHashSet();
        var fitIds=plan.Samples.Select(s=>s.Id).ToHashSet();
        if(fitted.Length!=fitIds.Count||fitted.Select(s=>s.Id).Distinct().Count()!=fitIds.Count||!fitted.Select(s=>s.Id).ToHashSet().SetEquals(fitIds)||
            fitted.Any(s=>s.Values.Length!=exposures.Length||s.Values.Select(v=>v.ExposureId).Distinct().Count()!=exposures.Length||!s.Values.Select(v=>v.ExposureId).ToHashSet().SetEquals(exposureIds)))
            throw Invalid("The finite-difference matrix must contain every complete native holding under every planned shared scenario.");
        var fit=fitted.ToDictionary(s=>s.Id,s=>s.Values.ToDictionary(v=>v.ExposureId,v=>Number(v.Value)),StringComparer.Ordinal);
        var fullValues=full.ToDictionary(s=>s.Id,s=>s.Values.ToDictionary(v=>v.ExposureId,v=>Number(v.Value)),StringComparer.Ordinal);
        var stability=Number(policy.StabilityRelative);var absolute=Number(policy.LossAbsolute);var relative=Number(policy.LossRelative);
        var multipliers=new[]{new SignedRatio(1,2),One,Two};
        var models=ImmutableArray.CreateBuilder<PcaHoldingModel>();
        foreach(var exposure in exposures)
        {
            cancellation.ThrowIfCancellationRequested();var basis=Number(exposure.BaseValue);var stable=true;
            SignedRatio Estimate(Func<int,SignedRatio> calculate)
            {
                var half=calculate(0);var nominal=calculate(1);var twice=calculate(2);
                stable&=Within(half,nominal,SignedRatio.Zero,stability)&&Within(twice,nominal,SignedRatio.Zero,stability);return nominal;
            }
            var delta=Enumerable.Range(0,d).Select(axis=>Text(Estimate(step=>Divide(
                fit[NativeId(axis,step,0)][exposure.Id]-fit[NativeId(axis,step,1)][exposure.Id],Two*Number(plan.NativeSteps[axis])*multipliers[step])))).ToImmutableArray();
            var gamma=Enumerable.Range(0,k).Select(_=>Enumerable.Repeat(SignedRatio.Zero,k).ToArray()).ToArray();
            for(var axis=0;axis<k;axis++)gamma[axis][axis]=Estimate(step=>Divide(
                fit[PcId(axis,step,0)][exposure.Id]+fit[PcId(axis,step,1)][exposure.Id]-Two*basis,multipliers[step]*multipliers[step]));
            for(var a=0;a<k;a++)for(var b=a+1;b<k;b++)gamma[a][b]=gamma[b][a]=Estimate(step=>Divide(
                fit[MixedId(a,b,step,0)][exposure.Id]-fit[MixedId(a,b,step,1)][exposure.Id]-fit[MixedId(a,b,step,2)][exposure.Id]+fit[MixedId(a,b,step,3)][exposure.Id],
                Four*multipliers[step]*multipliers[step]));
            models.Add(new(exposure.Id,delta,[..gamma.Select(row=>row.Select(Text).ToImmutableArray())],stable));
        }
        var model=models.ToImmutable();var projection=plan.Projection.Select(row=>row.Select(RiskBinary64.Real).ToArray()).ToArray();
        var deltas=model.Select(m=>m.Delta.Select(Number).ToArray()).ToArray();var gammas=model.Select(m=>m.Gamma.Select(row=>row.Select(Number).ToArray()).ToArray()).ToArray();
        var maxResidual=Enumerable.Repeat(SignedRatio.Zero,exposures.Length).ToArray();
        var candidate=generation.Samples.Select(sample=>
        {
            cancellation.ThrowIfCancellationRequested();var native=sample.Changes.Select(x=>Number(x.ToString())).ToArray();
            var x=sample.Changes.Select(c=>RiskBinary64.Real(c.ToString())).ToArray();
            // Projection includes the entire applied shock, including selected mean drift.
            var coordinates=projection.Select(row=>RiskBinary64.Fraction(row.Select((coefficient,i)=>coefficient*x[i]).Sum())).ToArray();
            var residual=Enumerable.Range(0,d).Select(i=>native[i]-SignedRatio.Sum(Enumerable.Range(0,k).Select(axis=>Number(plan.PcDirections[axis][i].ToString())*coordinates[axis]))).ToArray();
            return new HistoricalObservation(sample.Id,[..exposures.Select((exposure,n)=>
            {
                var linear=SignedRatio.Sum(Enumerable.Range(0,d).Select(i=>deltas[n][i]*native[i]));
                maxResidual[n]=Max(maxResidual[n],Abs(SignedRatio.Sum(Enumerable.Range(0,d).Select(i=>deltas[n][i]*residual[i]))));
                var quadratic=SignedRatio.Zero;
                for(var a=0;a<k;a++)for(var b=0;b<k;b++)quadratic+=gammas[n][a][b]*coordinates[a]*coordinates[b]/2;
                return new HistoricalScenarioValue(exposure.Id,Text(Number(exposure.BaseValue)+linear+quadratic));
            })]);
        }).ToImmutableArray();
        var candidateResult=ReduceQualification(generation.HistoryId,ids,generation.Policy.Confidence,exposures,candidate,cancellation);
        var candidateValues=candidate.ToDictionary(s=>s.Id,s=>s.Values.ToDictionary(v=>v.ExposureId,v=>Number(v.Value)),StringComparer.Ordinal);
        var tails=reference.Partitions.ToDictionary(p=>(p.Measure,p.Currency),p=>p.Samples.Where(s=>Number(s.VarWeight).Numerator.Sign>0||Number(s.EsWeight).Numerator.Sign>0).Select(s=>s.Id).ToHashSet());
        var diagnostics=exposures.Select((exposure,n)=>
        {
            var largest=SignedRatio.Zero;var tail=SignedRatio.Zero;var pass=true;var basis=Number(exposure.BaseValue);
            foreach(var id in ids)
            {
                var expected=basis-fullValues[id][exposure.Id];var predicted=basis-candidateValues[id][exposure.Id];var error=Abs(predicted-expected);
                largest=Max(largest,error);if(tails[(exposure.Measure,exposure.Currency)].Contains(id))tail=Max(tail,error);
                pass&=Within(expected,predicted,absolute,relative);
            }
            HistoricalPartition Standalone(Dictionary<string,Dictionary<string,SignedRatio>> values)=>ReduceQualification(
                generation.HistoryId,ids,generation.Policy.Confidence,[exposure],
                [..ids.Select(id=>new HistoricalObservation(id,[new(exposure.Id,Text(values[id][exposure.Id]))]))],cancellation).Partitions.Single();
            var fullRisk=Standalone(fullValues);var candidateRisk=Standalone(candidateValues);
            pass&=Within(Number(fullRisk.ValueAtRisk),Number(candidateRisk.ValueAtRisk),absolute,relative)&&
                Within(Number(fullRisk.ExpectedShortfall),Number(candidateRisk.ExpectedShortfall),absolute,relative);
            return new PcaExposureComparison(exposure.Id,fullRisk.ValueAtRisk,fullRisk.ExpectedShortfall,candidateRisk.ValueAtRisk,candidateRisk.ExpectedShortfall,
                Text(largest),Text(tail),Text(maxResidual[n]),pass);
        }).ToImmutableArray();
        var classes=identities.GroupBy(i=>i.ValuationKey,StringComparer.Ordinal).Select(group=>
        {
            var members=group.Select(i=>i.ExposureId).ToHashSet();var reasons=ImmutableArray.CreateBuilder<string>();
            if(model.Any(m=>members.Contains(m.ExposureId)&&!m.Stable))reasons.Add("unstable_finite_differences");
            if(diagnostics.Any(e=>members.Contains(e.ExposureId)&&!e.LossesPass))reasons.Add("holding_loss_or_tail_error");
            return new PcaClassification(group.Key,[..members.Order(StringComparer.Ordinal)],reasons.Count==0?"pca_delta_gamma":"full_pv",reasons.ToImmutable());
        }).OrderBy(c=>c.ValuationKey,StringComparer.Ordinal).ToImmutableArray();
        var fallback=classes.Where(c=>c.Method=="full_pv").SelectMany(c=>c.ExposureIds).ToHashSet();
        var hybrid=candidate.Select(s=>new HistoricalObservation(s.Id,[..s.Values.Select(v=>fallback.Contains(v.ExposureId)?new HistoricalScenarioValue(v.ExposureId,Text(fullValues[s.Id][v.ExposureId])):v)])).ToImmutableArray();
        var hybridResult=ReduceQualification(generation.HistoryId,ids,generation.Policy.Confidence,exposures,hybrid,cancellation);
        bool PartitionPass(HistoricalPartition expected,HistoricalPartition predicted)=>
            Within(Number(expected.ValueAtRisk),Number(predicted.ValueAtRisk),absolute,relative)&&Within(Number(expected.ExpectedShortfall),Number(predicted.ExpectedShortfall),absolute,relative)&&
            expected.Samples.Zip(predicted.Samples).All(pair=>pair.First.Id==pair.Second.Id&&Within(Number(pair.First.Loss),Number(pair.Second.Loss),absolute,relative));
        var portfolioFallback=reference.Partitions.Zip(hybridResult.Partitions).Any(pair=>!PartitionPass(pair.First,pair.Second));
        if(portfolioFallback)
        {
            classes=[..classes.Select(c=>c with{Method="full_pv",Reasons=[..c.Reasons,"portfolio_validation_failed"]})];
            hybridResult=reference;
        }
        var comparisons=reference.Partitions.Select((p,i)=>
        {
            var proposed=candidateResult.Partitions[i];var applied=hybridResult.Partitions[i];var max=SignedRatio.Zero;var tail=SignedRatio.Zero;
            foreach(var (expected,predicted) in p.Samples.Zip(proposed.Samples))
            {var error=Abs(Number(expected.Loss)-Number(predicted.Loss));max=Max(max,error);if(tails[(p.Measure,p.Currency)].Contains(expected.Id))tail=Max(tail,error);}
            return new PcaPartitionComparison(p.Measure,p.Currency,p.ValueAtRisk,p.ExpectedShortfall,proposed.ValueAtRisk,proposed.ExpectedShortfall,
                applied.ValueAtRisk,applied.ExpectedShortfall,Text(max),Text(tail),portfolioFallback);
        }).ToImmutableArray();
        return new(model,diagnostics,classes,comparisons,candidateResult.Partitions,hybridResult.Partitions);
    }
}
