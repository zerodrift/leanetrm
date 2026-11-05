using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Risk;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record MonteCarloRiskPolicy(string Convention,string NumericalPolicy,string Estimator,string Centering,string Drift,
    string Horizon,string QuantilePolicy,string TailPolicy,string ValuationPolicy,string Confidence,string Distribution,
    string RandomGenerator,string ShockPolicy,string Seed,int Paths,string SamplingUncertainty)
{
    public const string Version1="dated_factor_full_pv_monte_carlo_v1",Numerics="exact_ldl_binary64_polar_v1",
        Gaussian="joint_additive_gaussian_v1",Generator="sha256_counter_polar_log_series_v1",
        ShockRounding="binary64_exact_to_native_18_places_to_even_v1",Uncertainty="binomial_order_statistic_binary64_95_v1",
        Hybrid="dated_factor_hybrid_monte_carlo_v1",HybridValuation="pca_delta_gamma_frozen_nonfactor_inputs_v1",
        HybridUncertainty="empirical_prefix_diagnostics_no_interval_v1";
}
public sealed record MonteCarloSample(string Id,int Index,ImmutableArray<ExactDecimal> Changes);
public sealed record MonteCarloGeneration(string HistoryId,MonteCarloRiskPolicy Policy,ParametricCalibration Calibration,
    ImmutableArray<ImmutableArray<string>> Lower,ImmutableArray<string> Diagonal,ImmutableArray<MonteCarloSample> Samples,
    bool Available,ImmutableArray<string> UnavailableReasons);
public sealed record MonteCarloConvergence(int Paths,string ValueAtRisk,string ExpectedShortfall,string TailSampleMass);
public sealed record MonteCarloQuantileInterval(string Confidence,int LowerRank,int UpperRank,string? Lower,string? Upper);
public sealed record MonteCarloDiagnostic(string Measure,ReferenceKey Currency,ImmutableArray<MonteCarloConvergence> Convergence,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] MonteCarloQuantileInterval? ValueAtRiskInterval);
public sealed record MonteCarloRiskReport(MonteCarloRiskPolicy Policy,string HistoryId,int Samples,ImmutableArray<HistoricalPartition> Partitions,
    ImmutableArray<MonteCarloDiagnostic> Diagnostics,bool Available,ImmutableArray<string> UnavailableReasons);

/// <summary>Counter-addressed Gaussian scenarios. Generation never reads or mutates pricing state.</summary>
public static class MonteCarloRisk
{
    public const int MaximumPaths=16384,MaximumFactors=128;
    public const long MaximumShockValues=2_000_000;
    public static void ValidatePolicy(MonteCarloRiskPolicy p)
    {
        var full=p is not null&&p.Convention==MonteCarloRiskPolicy.Version1&&p.ValuationPolicy==HistoricalRiskPolicy.Valuation&&p.SamplingUncertainty==MonteCarloRiskPolicy.Uncertainty;
        var hybrid=p is not null&&p.Convention==MonteCarloRiskPolicy.Hybrid&&p.ValuationPolicy==MonteCarloRiskPolicy.HybridValuation&&p.SamplingUncertainty==MonteCarloRiskPolicy.HybridUncertainty;
        if(p is null||!full&&!hybrid||p.NumericalPolicy!=MonteCarloRiskPolicy.Numerics||
            p.Estimator!=ParametricRiskPolicy.Sample||p.Centering!=ParametricRiskPolicy.Centered||
            p.Drift is not(ParametricRiskPolicy.Zero or ParametricRiskPolicy.Mean)||p.Horizon!=ParametricRiskPolicy.Session||
            p.QuantilePolicy!=HistoricalRiskPolicy.Quantile||p.TailPolicy!=HistoricalRiskPolicy.Tail||
            p.Distribution!=MonteCarloRiskPolicy.Gaussian||p.RandomGenerator!=MonteCarloRiskPolicy.Generator||p.ShockPolicy!=MonteCarloRiskPolicy.ShockRounding||
            p.Paths is <2 or >MaximumPaths||!ulong.TryParse(p.Seed,NumberStyles.None,CultureInfo.InvariantCulture,out var seed)||seed.ToString(CultureInfo.InvariantCulture)!=p.Seed)
            throw Error("risk_monte_carlo_policy","Select a complete full-PV or hybrid additive Gaussian one-session policy, a canonical unsigned 64-bit seed and 2–16384 paths.");
        try{HistoricalRisk.ValidatePolicy(new(HistoricalRiskPolicy.Version1,HistoricalRiskPolicy.Numerics,p.Horizon,HistoricalRiskPolicy.Sampling,
            p.QuantilePolicy,p.TailPolicy,HistoricalRiskPolicy.Valuation,p.Confidence));
            if(Real(Number(p.Confidence))<=.5)throw Error("risk_monte_carlo_policy","Confidence must remain greater than 0.5 in the selected binary64 diagnostic policy.");}
        catch(MarketDataException e){throw new MarketDataException("risk_monte_carlo_policy",e.Message,e);}
    }
    public static MonteCarloGeneration Generate(AlignedFactorHistory history,MonteCarloRiskPolicy policy,int start=0,int? count=null,
        CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();ValidatePolicy(policy);
        if(history is null)throw Error("risk_monte_carlo_history","Supply the complete captured factor history.");
        var length=count??policy.Paths;
        if(start<0||length<1||start>policy.Paths-length||history.Columns.Length>MaximumFactors||
            (long)history.Columns.Length*policy.Paths>MaximumShockValues)
            throw Error("risk_monte_carlo_capacity","The complete simulation admits 128 factors, 2000000 native shock values and bounded paths within the selected run.");
        var calibration=ParametricRisk.Calibrate(history,cancellation);
        if(!history.Available||calibration.Samples<2)return new(history.Id,policy,calibration,[],[],[],false,["insufficient_common_history"]);
        var d=calibration.Columns.Length;
        var covariance=calibration.Covariance.Select(row=>row.Select(Number).ToArray()).ToArray();
        var lower=Enumerable.Range(0,d).Select(_=>Enumerable.Repeat(SignedRatio.Zero,d).ToArray()).ToArray();var diagonal=new SignedRatio[d];
        // Exact common-row covariance is PSD. A zero pivot has an exactly zero
        // residual column; no epsilon, jitter, pivot discard or matrix repair.
        for(var j=0;j<d;j++)
        {
            cancellation.ThrowIfCancellationRequested();lower[j][j]=new(1,1);
            var pivot=covariance[j][j];
            for(var k=0;k<j;k++)pivot=Bound(pivot-lower[j][k]*lower[j][k]*diagonal[k]);
            if(pivot.Numerator.Sign<0)throw Error("risk_monte_carlo_covariance","The exact joint covariance is not positive semidefinite.");
            diagonal[j]=pivot;
            for(var i=j+1;i<d;i++)
            {
                var residual=covariance[i][j];
                for(var k=0;k<j;k++)residual=Bound(residual-lower[i][k]*lower[j][k]*diagonal[k]);
                if(pivot.Numerator.IsZero)
                {
                    if(!residual.Numerator.IsZero)throw Error("risk_monte_carlo_covariance","A zero exact variance has a nonzero covariance residual.");
                    lower[i][j]=SignedRatio.Zero;
                }
                else lower[i][j]=Bound(new(residual.Numerator*pivot.Denominator,residual.Denominator*pivot.Numerator));
            }
        }
        for(var i=0;i<d;i++)for(var j=0;j<=i;j++)
        {
            cancellation.ThrowIfCancellationRequested();var reconstructed=SignedRatio.Zero;
            for(var k=0;k<=j;k++)reconstructed=Bound(reconstructed+lower[i][k]*diagonal[k]*lower[j][k]);
            if(reconstructed!=covariance[i][j])throw Error("risk_monte_carlo_covariance","Exact LDL reconstruction differs from captured covariance.");
        }
        // Retained exact coefficients must also fit the common 4096-bit text
        // transport. Admit this before generation and before any scenario PV.
        string Coefficient(SignedRatio value)
        {
            if(value.Numerator.GetBitLength()>4096||value.Denominator.GetBitLength()>4096)
                throw Error("risk_monte_carlo_numeric","The exact decomposition exceeds its 4096-bit retained coefficient bound.");
            return ExactNumberText.Format(value);
        }
        var retainedLower=lower.Select(row=>row.Select(Coefficient).ToImmutableArray()).ToImmutableArray();
        var retainedDiagonal=diagonal.Select(Coefficient).ToImmutableArray();
        var l=lower.Select(row=>row.Select(Real).ToArray()).ToArray();var scales=diagonal.Select(v=>Math.Sqrt(Real(v))).ToArray();
        var means=calibration.Means.Select(v=>policy.Drift==ParametricRiskPolicy.Zero?0:Real(Number(v))).ToArray();
        var samples=ImmutableArray.CreateBuilder<MonteCarloSample>(length);var seed=ulong.Parse(policy.Seed,CultureInfo.InvariantCulture);
        for(var index=start;index<start+length;index++)
        {
            cancellation.ThrowIfCancellationRequested();var normal=new double[d];
            for(var pair=0;pair<(d+1)/2;pair++)
            {
                var (x,y)=Normal(seed,index,pair);normal[2*pair]=x;
                if(2*pair+1<d)normal[2*pair+1]=y;
            }
            var changes=ImmutableArray.CreateBuilder<ExactDecimal>(d);
            for(var i=0;i<d;i++)
            {
                var shift=means[i];
                for(var j=0;j<=i;j++)shift+=l[i][j]*scales[j]*normal[j];
                changes.Add(NativeShock(shift));
            }
            samples.Add(new("mc_"+index.ToString("D5",CultureInfo.InvariantCulture),index,changes.MoveToImmutable()));
        }
        return new(history.Id,policy,calibration,retainedLower,retainedDiagonal,samples.MoveToImmutable(),true,[]);
    }
    public static MonteCarloRiskReport Calculate(MonteCarloGeneration generation,ImmutableArray<HistoricalExposure> exposures,
        ImmutableArray<HistoricalObservation> observations,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();ValidatePolicy(generation.Policy);
        if(!generation.Available)return new(generation.Policy,generation.HistoryId,0,[],[],false,generation.UnavailableReasons);
        if(generation.Samples.Length!=generation.Policy.Paths||!generation.Samples.Select(s=>s.Index).SequenceEqual(Enumerable.Range(0,generation.Policy.Paths)))
            throw Error("risk_monte_carlo_samples","Reduction requires every simulation index exactly once in generation order.");
        var result=EmpiricalRisk.Calculate(generation.HistoryId,[..generation.Samples.Select(s=>s.Id)],generation.Policy.Confidence,exposures,observations,cancellation);
        var sizes=new[]{Math.Max(2,generation.Policy.Paths/4),Math.Max(2,generation.Policy.Paths/2),generation.Policy.Paths}.Distinct().Order().ToArray();
        var byId=observations.ToDictionary(o=>o.Id,StringComparer.Ordinal);
        var prefix=sizes.Select(n=>n==generation.Policy.Paths?result:EmpiricalRisk.Calculate(generation.HistoryId,
            [..generation.Samples.Take(n).Select(s=>s.Id)],generation.Policy.Confidence,exposures,
            [..generation.Samples.Take(n).Select(s=>byId[s.Id])],cancellation)).ToArray();
        var diagnostics=result.Partitions.Select((p,i)=>new MonteCarloDiagnostic(p.Measure,p.Currency,
            [..prefix.Select(r=>new MonteCarloConvergence(r.Samples,r.Partitions[i].ValueAtRisk,r.Partitions[i].ExpectedShortfall,r.Partitions[i].TailSampleMass))],
            generation.Policy.SamplingUncertainty==MonteCarloRiskPolicy.Uncertainty?QuantileInterval(p.Samples.Select(s=>s.Loss).ToArray(),generation.Policy.Confidence,cancellation):null)).ToImmutableArray();
        return new(generation.Policy,generation.HistoryId,result.Samples,result.Partitions,diagnostics,true,[]);
    }
    // Pointwise binomial order-statistic bounds, conditional on this fixed fitted
    // model. Direct stable tails, with a conservative 1e-12 margin at each cutoff.
    // No finite bound exists when its rank is 0 or n+1. This is not an ES interval.
    public static MonteCarloQuantileInterval QuantileInterval(string[] losses,string confidence,CancellationToken cancellation=default)
    {
        var n=losses.Length;if(n is <2 or >MaximumPaths)throw Error("risk_monte_carlo_samples","Supply 2–16384 losses for sampling diagnostics.");
        var probability=Real(Number(confidence));
        if(probability<=.5||probability>.9999)throw Error("risk_monte_carlo_policy","The sampling quantile must be in (0.5,0.9999].");
        var mode=(int)Math.Floor((n+1)*probability);var weights=new double[n+1];weights[mode]=1;
        for(var k=mode;k>0;k--)weights[k-1]=weights[k]*k/(n-k+1)*(1-probability)/probability;
        for(var k=mode;k<n;k++)weights[k+1]=weights[k]*(n-k)/(k+1)*probability/(1-probability);
        var total=weights.Sum();var lower=0;var upper=n+1;var tail=0d;const double cutoff=.025-1e-12;
        for(var k=0;k<=n;k++){cancellation.ThrowIfCancellationRequested();tail+=weights[k]/total;if(tail<=cutoff)lower=k+1;else break;}
        tail=0;
        for(var k=n;k>=0;k--){tail+=weights[k]/total;if(tail<=cutoff)upper=k;else break;}
        var sorted=losses.Select(Number).Order().ToArray();
        return new("0.95",lower,upper,lower==0?null:ExactNumberText.Format(sorted[lower-1]),upper==n+1?null:ExactNumberText.Format(sorted[upper-1]));
    }
    private static (double X,double Y) Normal(ulong seed,int sample,int pair)
    {
        Span<byte> input=stackalloc byte[44];Encoding.ASCII.GetBytes("OpenETRM MC polar v1",input);
        BinaryPrimitives.WriteUInt64LittleEndian(input[20..],seed);BinaryPrimitives.WriteInt32LittleEndian(input[28..],sample);
        BinaryPrimitives.WriteInt32LittleEndian(input[32..],pair);input[40..].Clear();
        Span<byte> hash=stackalloc byte[32];
        for(var attempt=0;attempt<128;attempt++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(input[36..],attempt);SHA256.HashData(input,hash);
            // Midpoints of 2^52 equal cells are strictly inside (0,1), including
            // at the binary64 upper endpoint. Two distinct digest words supply x/y.
            var x=2*((BinaryPrimitives.ReadUInt64LittleEndian(hash)>>12)+.5)/4503599627370496d-1;
            var y=2*((BinaryPrimitives.ReadUInt64LittleEndian(hash[8..])>>12)+.5)/4503599627370496d-1;
            var radius=x*x+y*y;if(radius<=0||radius>=1)continue;
            var scale=Math.Sqrt(-2*Log(radius)/radius);return(x*scale,y*scale);
        }
        throw Error("risk_monte_carlo_numeric","The bounded polar rejection generator did not produce a sample.");
    }
    private static double Log(double value)
    {
        // Range reduction to [1,2) and the rapidly convergent positive atanh
        // series. Fixed ordinary operations avoid platform libm log/cos versions.
        var exponent=Math.ILogB(value);var m=Math.ScaleB(value,-exponent);var t=(m-1)/(m+1);var square=t*t;
        var term=t;var sum=t;
        for(var k=3;k<=129;k+=2){term*=square;sum+=term/k;}
        return 2*sum+exponent*0.693147180559945309417232121458176568;
    }
    private static ExactDecimal NativeShock(double value)
    {
        if(!double.IsFinite(value))throw Error("risk_monte_carlo_numeric","The simulated native shock is not finite.");
        if(value==0)return default;
        var bits=BitConverter.DoubleToUInt64Bits(value);var exponent=(int)((bits>>52)&2047);var mantissa=new BigInteger(bits&0xfffffffffffffUL);
        if(exponent!=0)mantissa+=BigInteger.One<<52;
        var power=exponent==0?-1074:exponent-1075;
        var n=mantissa*ExactDecimal.WorkingScale;var denominator=BigInteger.One;
        if(power>=0)n<<=power;else denominator<<=-power;
        var rounded=IntegerRounding.RoundQuotient(n,denominator,MidpointRounding.ToEven);
        if(rounded.IsZero)throw Error("risk_monte_carlo_numeric","A nonzero native shock rounds to zero at the selected 18-place boundary.");
        try{return ExactDecimal.FromScaled((bits>>63)==0?rounded:-rounded);}
        catch(MarketDataException e){throw new MarketDataException("risk_monte_carlo_numeric","The simulated shock exceeds the native quote range.",e);}
    }
    private static SignedRatio Number(string text){var(n,d)=ExactNumberText.Parse(text);return Bound(new(n,d));}
    private static SignedRatio Bound(SignedRatio value)
    {
        if(value.Numerator.GetBitLength()>16384||value.Denominator.GetBitLength()>16384)
            throw Error("risk_monte_carlo_numeric","The exact covariance decomposition exceeds its 16384-bit arithmetic bound.");
        return value;
    }
    internal static double Real(SignedRatio value)
    {
        if(value.Numerator.IsZero)return 0;
        var n=BigInteger.Abs(value.Numerator);var ns=(int)Math.Max(0,n.GetBitLength()-53);var ds=(int)Math.Max(0,value.Denominator.GetBitLength()-53);
        var result=Math.ScaleB((double)(n>>ns)/(double)(value.Denominator>>ds),ns-ds)*value.Numerator.Sign;
        if(!double.IsFinite(result)||result==0)throw Error("risk_monte_carlo_numeric","A nonzero exact calibration input cannot cross the binary64 boundary.");
        return result;
    }
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
