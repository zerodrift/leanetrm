using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Risk;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record PcaHybridPolicy(string Convention,string NumericalPolicy,string ScalingPolicy,string ResidualPolicy,
    string ValuationPolicy,string DirectionPolicy,int RetainedComponents,int TradingTicks,string StabilityRelative,string LossAbsolute,string LossRelative)
{
    public const string Version1="correlation_pca_hybrid_qualification_v1",Decomposition="cyclic_jacobi_binary64_residual_checks_v1",
        Scaling="sample_sd_constant_unit_v1",Residual="complete_native_shocks_all_factor_delta_v1",
        Qualification="full_reference_qualification_v1",Directions="pc_0_01_native_17_places_collinear_v1";
}
public sealed record PcaSampleProjection(string Id,ImmutableArray<string> Scores,string ResidualNorm,string ReconstructionError);
public sealed record PcaCalibration(PcaHybridPolicy Policy,ImmutableArray<string> Columns,ImmutableArray<string> Scales,
    ImmutableArray<string> Eigenvalues,ImmutableArray<string> VarianceShares,ImmutableArray<ImmutableArray<string>> Loadings,
    ImmutableArray<ImmutableArray<string>> ResidualCovariance,ImmutableArray<ImmutableArray<string>> RetainedResidualCovariance,string MatrixNorm,int Sweeps,int RoundoffNegativeEigenvalues,
    string ReconstructionError,string OrthogonalityError,string EigenResidual,string RetainedVarianceShare,
    ImmutableArray<PcaSampleProjection> Samples);

/// <summary>Diagnostics and a per-run qualification model. Never replaces or reduces the native Monte Carlo shock generator.</summary>
public static partial class PcaHybridRisk
{
    public const int MaximumComponents=8;
    internal static MarketDataException Invalid(string message)=>new("risk_pca_hybrid_policy",message);
    public static void ValidatePolicy(PcaHybridPolicy p,int? factors=null)
    {
        if(p is null||p.Convention!=PcaHybridPolicy.Version1||p.NumericalPolicy!=PcaHybridPolicy.Decomposition||
            p.ScalingPolicy!=PcaHybridPolicy.Scaling||p.ResidualPolicy!=PcaHybridPolicy.Residual||p.ValuationPolicy!=PcaHybridPolicy.Qualification||
            p.DirectionPolicy!=PcaHybridPolicy.Directions||p.RetainedComponents is <1 or >MaximumComponents||factors is {} d&&p.RetainedComponents>d||
            p.TradingTicks is <1 or >1000)
            throw Invalid("Select explicit PCA scaling, complete residual shocks, full-reference qualification, 1–8 retained components and 1–1000 trading ticks.");
        foreach(var text in new[]{p.StabilityRelative,p.LossAbsolute,p.LossRelative})
            try{if(ExactDecimal.Parse(text??"").CompareTo(default)<0)throw Invalid("Select explicit nonnegative stability and native loss-error tolerances.");}
            catch(MarketDataException e){throw Invalid(e.Message);}
    }
    public static int FittingScenarios(int factors,int components)=>checked(6*factors+6*components*components);
    public static PcaCalibration Decompose(MonteCarloGeneration generation,PcaHybridPolicy policy,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();var calibration=generation.Calibration;var d=calibration.Columns.Length;
        ValidatePolicy(policy,d);
        if(!generation.Available||d is <1 or >MonteCarloRisk.MaximumFactors||calibration.Covariance.Length!=d||calibration.Covariance.Any(r=>r.Length!=d))
            throw Invalid("PCA diagnostics require the complete available Monte Carlo calibration.");
        var covariance=calibration.Covariance.Select(row=>row.Select(RiskBinary64.Real).ToArray()).ToArray();
        var scales=Enumerable.Range(0,d).Select(i=>covariance[i][i]==0?1:Math.Sqrt(covariance[i][i])).ToArray();
        var matrix=Enumerable.Range(0,d).Select(i=>Enumerable.Range(0,d).Select(j=>i==j?(covariance[i][i]==0?0d:1d):covariance[i][j]/scales[i]/scales[j]).ToArray()).ToArray();
        if(scales.Any(s=>!double.IsFinite(s)||s<=0)||matrix.SelectMany(r=>r).Any(x=>!double.IsFinite(x)))
            throw Numeric("The sample-standard-deviation scale cannot cross the finite binary64 diagnostic boundary.");
        var a=matrix.Select(r=>r.ToArray()).ToArray();
        var v=Enumerable.Range(0,d).Select(i=>Enumerable.Range(0,d).Select(j=>i==j?1d:0d).ToArray()).ToArray();
        var norm=matrix.Max(r=>r.Sum(Math.Abs));var stop=8*2.2204460492503131e-16*Math.Max(1,norm);var sweeps=0;
        for(;sweeps<100;sweeps++)
        {
            cancellation.ThrowIfCancellationRequested();var largest=0d;
            for(var p=0;p<d;p++)for(var q=p+1;q<d;q++)
            {
                var off=a[p][q];largest=Math.Max(largest,Math.Abs(off));if(Math.Abs(off)<=stop)continue;
                var tau=(a[q][q]-a[p][p])/(2*off);
                var t=(tau<0?-1d:1d)/(Math.Abs(tau)+Math.Sqrt(1+tau*tau));var c=1/Math.Sqrt(1+t*t);var s=t*c;
                a[p][p]-=t*off;a[q][q]+=t*off;a[p][q]=a[q][p]=0;
                for(var i=0;i<d;i++)
                {
                    if(i!=p&&i!=q){var x=a[i][p];var y=a[i][q];a[i][p]=a[p][i]=c*x-s*y;a[i][q]=a[q][i]=s*x+c*y;}
                    var vp=v[i][p];var vq=v[i][q];v[i][p]=c*vp-s*vq;v[i][q]=s*vp+c*vq;
                }
            }
            if(largest<=stop)break;
        }
        if(sweeps==100)throw Numeric("The bounded cyclic Jacobi solver did not converge.");
        // Stable source-index tie ordering and largest-absolute-loading sign.
        // A repeated eigenvalue defines a subspace, not economically unique axes.
        var order=Enumerable.Range(0,d).OrderByDescending(i=>a[i][i]).ThenBy(i=>i).ToArray();
        var eigen=order.Select(i=>a[i][i]).ToArray();var vectors=v.Select(row=>order.Select(i=>row[i]).ToArray()).ToArray();
        for(var k=0;k<d;k++)
        {
            var anchor=Enumerable.Range(0,d).OrderByDescending(i=>Math.Abs(vectors[i][k])).ThenBy(i=>i).First();
            if(vectors[anchor][k]<0)for(var i=0;i<d;i++)vectors[i][k]=-vectors[i][k];
        }
        var reconstruction=0d;var orthogonality=0d;var residual=0d;
        var limit=1e-12*Math.Max(1,norm);
        for(var i=0;i<d;i++)for(var j=0;j<d;j++)
        {
            var rebuilt=0d;var dot=0d;var av=0d;
            for(var k=0;k<d;k++){rebuilt+=vectors[i][k]*eigen[k]*vectors[j][k];dot+=vectors[k][i]*vectors[k][j];av+=matrix[i][k]*vectors[k][j];}
            reconstruction=Math.Max(reconstruction,Math.Abs(rebuilt-covariance[i][j]/scales[i]/scales[j]));orthogonality=Math.Max(orthogonality,Math.Abs(dot-(i==j?1:0)));
            residual=Math.Max(residual,Math.Abs(av-vectors[i][j]*eigen[j]));
        }
        if(eigen.Any(x=>!double.IsFinite(x)||x < -limit)||reconstruction>limit||orthogonality>1e-12*d||residual>limit)
            throw Numeric("PCA eigenvalue, covariance reconstruction, orthogonality or eigen-equation qualification failed.");
        // Raw signed eigenvalues remain in the receipt and reconstruction. Only
        // the nonnegative explained-variance diagnostic treats roundoff negatives as zero.
        var mass=eigen.Sum(x=>Math.Max(0,x));var shares=eigen.Select(x=>mass==0?0:Math.Max(0,x)/mass).ToArray();
        var kept=policy.RetainedComponents;
        var retainedProjection=Enumerable.Range(0,d).Select(i=>Enumerable.Range(0,d).Select(j=>
            Enumerable.Range(0,kept).Sum(k=>vectors[i][k]*vectors[j][k])).ToArray()).ToArray();
        var residualProjection=retainedProjection.Select((row,i)=>row.Select((x,j)=>(i==j?1:0)-x).ToArray()).ToArray();
        double[][] Product(double[][] left,double[][] right)=>Enumerable.Range(0,d).Select(i=>Enumerable.Range(0,d).Select(j=>
            Enumerable.Range(0,d).Sum(k=>left[i][k]*right[k][j])).ToArray()).ToArray();
        double[][] Native(double[][] standardized)=>standardized.Select((row,i)=>row.Select((x,j)=>scales[i]*x*scales[j]).ToArray()).ToArray();
        // Cov(r) = R C R^T. The retained/residual cross covariance is also
        // retained; finite-precision projections need not separate it exactly.
        var residualCovariance=Native(Product(Product(residualProjection,matrix),residualProjection));
        var crossCovariance=Native(Product(Product(retainedProjection,matrix),residualProjection));
        var samples=generation.Samples.Select(sample=>
        {
            cancellation.ThrowIfCancellationRequested();var x=sample.Changes.Select(c=>RiskBinary64.Real(c.ToString())).ToArray();
            var scores=new double[d];
            for(var k=0;k<d;k++)for(var i=0;i<d;i++)scores[k]+=vectors[i][k]*(x[i]/scales[i]);
            var error=0d;var residualNorm=0d;
            for(var i=0;i<d;i++)
            {
                var all=0d;var retained=0d;
                for(var k=0;k<d;k++){var term=vectors[i][k]*scores[k];all+=term;if(k<kept)retained+=term;}
                var standardized=x[i]/scales[i];error=Math.Max(error,Math.Abs(all-standardized)/Math.Max(1,Math.Abs(standardized)));
                var r=standardized-retained;residualNorm+=r*r;
            }
            if(!double.IsFinite(error)||error>1e-12*d)throw Numeric("Full loading reconstruction did not recover a complete applied native shock.");
            return new PcaSampleProjection(sample.Id,[..scores.Take(kept).Select(RiskBinary64.Text)],RiskBinary64.Text(Math.Sqrt(residualNorm)),RiskBinary64.Text(error));
        }).ToImmutableArray();
        return new(policy,calibration.Columns,[..scales.Select(RiskBinary64.Text)],[..eigen.Select(RiskBinary64.Text)],
            [..shares.Select(RiskBinary64.Text)],[..vectors.Select(row=>row.Select(RiskBinary64.Text).ToImmutableArray())],
            [..residualCovariance.Select(row=>row.Select(RiskBinary64.Text).ToImmutableArray())],
            [..crossCovariance.Select(row=>row.Select(RiskBinary64.Text).ToImmutableArray())],RiskBinary64.Text(norm),sweeps,eigen.Count(x=>x<0),
            RiskBinary64.Text(reconstruction),RiskBinary64.Text(orthogonality),RiskBinary64.Text(residual),RiskBinary64.Text(shares.Take(kept).Sum()),samples);
    }
    internal static MarketDataException Numeric(string message)=>new("risk_pca_hybrid_numeric",message);
}

// All approximate coefficients retain their exact IEEE binary64 fractions. No
// decimal display rounding enters fitting, qualification or portfolio reduction.
internal static class RiskBinary64
{
    internal static SignedRatio Number(string text){var(n,d)=ExactNumberText.Parse(text);return new(n,d);}
    internal static double Real(string text)=>MonteCarloRisk.Real(Number(text));
    internal static SignedRatio Fraction(double value)
    {
        if(!double.IsFinite(value))throw PcaHybridRisk.Numeric("A PCA diagnostic or projection is not finite.");
        if(value==0)return SignedRatio.Zero;
        var bits=BitConverter.DoubleToUInt64Bits(value);var exponent=(int)((bits>>52)&2047);var n=new BigInteger(bits&0xfffffffffffffUL);
        if(exponent!=0)n+=BigInteger.One<<52;var power=exponent==0?-1074:exponent-1075;var d=BigInteger.One;
        if(power>=0)n<<=power;else d<<=-power;
        return new((bits>>63)==0?n:-n,d);
    }
    internal static string Text(double value)=>ExactNumberText.Format(Fraction(value));
}
