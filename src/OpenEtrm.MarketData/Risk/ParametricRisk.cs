using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Risk;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record ParametricRiskPolicy(string Convention,string NumericalPolicy,string Estimator,string Centering,string Drift,string Horizon,string Tail,string Confidence)
{
    public const string Version1="dated_factor_delta_normal_v1",Numerics="exact_moments_binary64_normal_v1",
        Sample="equal_weight_sample_n_minus_one_v1",Centered="sample_mean_centered_v1",Zero="zero_drift_v1",Mean="sample_mean_drift_v1",
        Session="frozen_portfolio_one_adjacent_session_v1",Signed="signed_upper_loss_tail_v1";
}
public sealed record ParametricDelta(string ColumnId,PriceUnit Unit,string Value,bool Stable);
public sealed record ParametricExposure(string Id,Guid BookId,string Measure,ReferenceKey Currency,ImmutableArray<ParametricDelta> Deltas);
public sealed record ParametricCalibration(string HistoryId,int Samples,ImmutableArray<string> Columns,ImmutableArray<string> Means,
    ImmutableArray<ImmutableArray<string>> Covariance,int Rank,ImmutableArray<string> ConstantColumns,long Products,
    string Conditioning,string? CorrelationCondition)
{
    public ImmutableArray<ImmutableArray<string?>> Correlation{get;init;}=[];
}
public sealed record ParametricContribution(string Id,string Mean,string VarianceCross,string ValueAtRisk,string ExpectedShortfall);
public sealed record ParametricDensity(string Score,string Loss,string Density);
public sealed record ParametricLinearLoss(string Sample,string Loss);
public sealed record ParametricPartition(string Measure,ReferenceKey Currency,string Mean,string Variance,string StandardDeviation,string ValueAtRisk,string ExpectedShortfall,
    ImmutableArray<ParametricContribution> Books,ImmutableArray<ParametricContribution> Factors,ImmutableArray<ParametricDensity> Density,
    ImmutableArray<ParametricLinearLoss> LinearizedLosses,string VarReconciliation,string EsReconciliation);
public sealed record ParametricRiskReport(ParametricRiskPolicy Policy,ParametricCalibration Calibration,ImmutableArray<ParametricPartition> Partitions,
    bool Available,ImmutableArray<string> UnavailableReasons);

public static class ParametricRisk
{
    public const int MaximumFactors=128,MaximumExposures=4096;
    public const long MaximumProducts=2_000_000;
    public static ExactDecimal ResolveStep(ExactDecimal tick,int multiples)
    {
        try
        {
            if(tick.CompareTo(default)<=0||multiples is <1 or >1000)throw new MarketDataException("risk_parametric_delta","Invalid native tick count.");
            var step=tick*ExactDecimal.Parse(multiples.ToString(CultureInfo.InvariantCulture));
            _=step.DivideExact(ExactDecimal.Parse("2"));_=step*ExactDecimal.Parse("2");return step;
        }
        catch(MarketDataException e){throw new MarketDataException("risk_parametric_delta","Each native FD bump must admit exact positive half, nominal and double steps.",e);}
    }
    public static ParametricRiskReport Calculate(AlignedFactorHistory history,ParametricRiskPolicy policy,ImmutableArray<ParametricExposure> exposures,
        bool portfolioComplete,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();ValidatePolicy(policy);
        if(history is null)throw Error("risk_parametric_history","Supply the complete captured history manifest.");
        var d=history.Columns.Length;var n=history.Samples.Length;Admit(d,n);
        history=FactorHistory.Replay(history,cancellation);
        if(exposures.IsDefault||exposures.IsEmpty&&history.Available&&portfolioComplete||exposures.Length>MaximumExposures||exposures.Any(e=>e is null||string.IsNullOrWhiteSpace(e.Id)||e.Id.Length>128||
            e.BookId==Guid.Empty||string.IsNullOrWhiteSpace(e.Measure)||e.Measure.Length>128||e.Deltas.IsDefault||e.Deltas.Length!=d)||
            exposures.Select(e=>e.Id).Distinct(StringComparer.Ordinal).Count()!=exposures.Length)
            throw Error("risk_parametric_delta","Supply complete, distinct native book/measure/currency exposures for every captured history column.");
        var ids=history.Columns.Select(c=>c.Id).ToImmutableArray();
        var ordered=exposures.OrderBy(e=>e.Id,StringComparer.Ordinal).ToArray();
        var deltas=new Dictionary<string,SignedRatio[]>(StringComparer.Ordinal);
        foreach(var e in ordered)
        {
            Money.RequireKey(e.Currency);
            if(e.Deltas.Any(v=>v is null)||e.Deltas.Select(v=>v.ColumnId).Distinct(StringComparer.Ordinal).Count()!=d)
                throw Error("risk_parametric_delta","Every exposure must contain each history column once.");
            deltas[e.Id]=[..history.Columns.Select(c=>
            {
                var value=e.Deltas.SingleOrDefault(v=>v.ColumnId==c.Id);
                if(value is null||value.Unit!=c.Mapping.Factor.Unit)throw Error("risk_parametric_delta","Delta coordinates and native factor units must match the retained history exactly.");
                return Number(value.Value);
            })];
        }
        var reasons=ImmutableArray.CreateBuilder<string>();
        if(!history.Available)reasons.Add("insufficient_common_history");
        if(!portfolioComplete)reasons.Add("incomplete_portfolio");
        if(ordered.Any(e=>e.Deltas.Any(v=>!v.Stable)))reasons.Add("unstable_delta");
        if(n<2)return new(policy,new(history.Id,n,ids,[],[],0,[],0,"unavailable",null),[],false,reasons.ToImmutable());
        var samples=history.Samples.Select(s=>s.Changes.Select(v=>Number(v.ToString())).ToArray()).ToArray();
        var sums=Enumerable.Range(0,d).Select(i=>SignedRatio.Sum(samples.Select(s=>s[i]))).ToArray();
        var means=sums.Select(s=>s/n).ToArray();var covariance=new SignedRatio[d][];
        for(var i=0;i<d;i++)covariance[i]=new SignedRatio[d];
        for(var i=0;i<d;i++)for(var j=i;j<d;j++)
        {
            cancellation.ThrowIfCancellationRequested();
            var cross=SignedRatio.Sum(samples.Select(s=>s[i]*s[j]));
            covariance[i][j]=covariance[j][i]=Bound((cross-sums[i]*sums[j]/n)/(n-1));
        }
        var rank=Rank(covariance,cancellation);var constant=ids.Where((_,i)=>covariance[i][i].Numerator.IsZero).ToImmutableArray();
        var condition=rank==d?Condition(covariance):null;
        var calibration=new ParametricCalibration(history.Id,n,ids,[..means.Select(Text)],[..covariance.Select(row=>row.Select(Text).ToImmutableArray())],rank,
            constant,(long)n*d*d,rank<d?"rank_deficient":condition is null?"unresolved":condition>1e12?"poorly_conditioned":"full_rank",condition is {} k?Real(k):null)
            {Correlation=[..covariance.Select((row,i)=>row.Select((value,j)=>covariance[i][i].Numerator.IsZero||covariance[j][j].Numerator.IsZero?null:
                Real(Real(value)/(Math.Sqrt(Real(covariance[i][i]))*Math.Sqrt(Real(covariance[j][j]))))).ToImmutableArray())]};
        if(reasons.Count>0)return new(policy,calibration,[],false,reasons.ToImmutable());
        var confidence=Real(Number(policy.Confidence));var z=NormalQuantile(confidence);var tail=Density(z)/(1-confidence);
        var partitions=ImmutableArray.CreateBuilder<ParametricPartition>();
        foreach(var group in ordered.GroupBy(e=>(e.Measure,e.Currency)).OrderBy(g=>g.Key.Measure,StringComparer.Ordinal)
            .ThenBy(g=>g.Key.Currency.Revision.CatalogId).ThenBy(g=>g.Key.Currency.Revision.RevisionId).ThenBy(g=>g.Key.Currency.Code,StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            var total=Enumerable.Range(0,d).Select(i=>SignedRatio.Sum(group.Select(e=>deltas[e.Id][i]))).ToArray();
            var weighted=covariance.Select(row=>Dot(row,total)).ToArray();
            var variance=Dot(total,weighted);if(variance.Numerator.Sign<0)throw Error("risk_parametric_covariance","Exact common-row covariance cannot have negative portfolio variance.");
            var mean=policy.Drift==ParametricRiskPolicy.Zero?SignedRatio.Zero:SignedRatio.Zero-Dot(total,means);
            var sd=Math.Sqrt(Real(variance));var lossMean=Real(mean);var valueAtRisk=lossMean+z*sd;var es=lossMean+tail*sd;
            ParametricContribution Contribution(string id,SignedRatio[] delta)
            {
                var mu=policy.Drift==ParametricRiskPolicy.Zero?SignedRatio.Zero:SignedRatio.Zero-Dot(delta,means);var cross=Dot(delta,weighted);
                var volatility=sd==0?0:Real(cross)/sd;
                return new(id,Text(mu),Text(cross),Real(Real(mu)+z*volatility),Real(Real(mu)+tail*volatility));
            }
            var books=group.GroupBy(e=>e.BookId).OrderBy(g=>g.Key).Select(g=>Contribution(g.Key.ToString(),
                Enumerable.Range(0,d).Select(i=>SignedRatio.Sum(g.Select(e=>deltas[e.Id][i]))).ToArray())).ToImmutableArray();
            var factors=ids.Select((id,i)=>Contribution(id,Enumerable.Range(0,d).Select(j=>j==i?total[i]:SignedRatio.Zero).ToArray())).ToImmutableArray();
            var density=sd==0?ImmutableArray<ParametricDensity>.Empty:Enumerable.Range(0,81).Select(i=>
            {var point=-4+i/10d;return new ParametricDensity(Real(point),Real(lossMean+point*sd),Real(Density(point)/sd));}).ToImmutableArray();
            partitions.Add(new(group.Key.Measure,group.Key.Currency,Text(mean),Text(variance),Real(sd),Real(valueAtRisk),Real(es),books,factors,density,
                [..samples.Select((s,i)=>new ParametricLinearLoss(history.Samples[i].Id,Text(SignedRatio.Zero-Dot(total,s))))],
                Real(valueAtRisk-books.Sum(b=>double.Parse(b.ValueAtRisk,CultureInfo.InvariantCulture))),Real(es-books.Sum(b=>double.Parse(b.ExpectedShortfall,CultureInfo.InvariantCulture)))));
        }
        return new(policy,calibration,partitions.ToImmutable(),true,[]);
    }
    public static void Admit(int factors,int samples)
    {
        if(factors is <1 or >MaximumFactors||samples<0||samples>=FactorHistory.MaximumSessions||(long)factors*factors*samples>MaximumProducts)
            throw Error("risk_parametric_capacity","Reduce the explicit scope or window: at most 128 factors and 2000000 covariance sample-products are admitted.");
    }
    public static void ValidatePolicy(ParametricRiskPolicy p)
    {
        if(p is null||p.Convention!=ParametricRiskPolicy.Version1||p.NumericalPolicy!=ParametricRiskPolicy.Numerics||p.Estimator!=ParametricRiskPolicy.Sample||
            p.Centering!=ParametricRiskPolicy.Centered||p.Drift is not(ParametricRiskPolicy.Zero or ParametricRiskPolicy.Mean)||p.Horizon!=ParametricRiskPolicy.Session||p.Tail!=ParametricRiskPolicy.Signed)
            throw Error("risk_parametric_policy","Select the explicit sample-covariance, centered, one-session signed delta-normal contract and its drift policy.");
        SignedRatio alpha;
        try{alpha=Number(ExactDecimal.Parse(p.Confidence??"").ToString());}
        catch(MarketDataException e){throw new MarketDataException("risk_parametric_policy","Confidence must be a plain decimal with at most 18 fractional digits.",e);}
        if(alpha.CompareTo(new(1,2))<=0||alpha.CompareTo(new(9999,10000))>0||Real(alpha)<=.5)
            throw Error("risk_parametric_policy","Choose a confidence greater than 0.5 and at most 0.9999.");
    }
    private static MarketDataException Error(string code,string message)=>new(code,message);
    private static SignedRatio Number(string value){var(n,d)=ExactNumberText.Parse(value??"");return Bound(new(n,d));}
    private static SignedRatio Bound(SignedRatio value)
    {
        if(value.Numerator.GetBitLength()>16384||value.Denominator.GetBitLength()>16384)
            throw Error("risk_parametric_numeric","Exact statistical arithmetic exceeds the 16384-bit intermediate bound.");
        return value;
    }
    private static string Text(SignedRatio value)=>ExactNumberText.Format(value);
    private static SignedRatio Dot(SignedRatio[] a,SignedRatio[] b)=>Bound(SignedRatio.Sum(a.Select((v,i)=>v*b[i])));
    private static SignedRatio Divide(SignedRatio a,SignedRatio b)=>Bound(b.Numerator.Sign>0?new(a.Numerator*b.Denominator,a.Denominator*b.Numerator):
        new(-a.Numerator*b.Denominator,-a.Denominator*b.Numerator));
    private static double Real(SignedRatio value)
    {
        if(value.Numerator.IsZero)return 0;
        var n=BigInteger.Abs(value.Numerator);var ns=(int)Math.Max(0,n.GetBitLength()-53);var ds=(int)Math.Max(0,value.Denominator.GetBitLength()-53);
        var result=Math.ScaleB((double)(n>>ns)/(double)(value.Denominator>>ds),ns-ds)*value.Numerator.Sign;
        if(!double.IsFinite(result)||result==0)throw Error("risk_parametric_numeric","A nonzero exact result cannot be represented as a finite nonzero binary64 value.");
        return result;
    }
    private static string Real(double value)
    {
        if(!double.IsFinite(value))throw Error("risk_parametric_numeric","A Gaussian calculation produced a non-finite result.");
        if(value==0)return "0";
        var text=value.ToString("G17",CultureInfo.InvariantCulture);var at=text.IndexOf('E');if(at<0)return text;
        var mantissa=text[..at];var negative=mantissa.StartsWith('-');if(negative)mantissa=mantissa[1..];
        var point=mantissa.IndexOf('.');if(point<0)point=mantissa.Length;
        var digits=mantissa.Replace(".","");point+=int.Parse(text[(at+1)..],CultureInfo.InvariantCulture);
        var plain=point<=0?"0."+new string('0',-point)+digits:point>=digits.Length?digits+new string('0',point-digits.Length):digits[..point]+"."+digits[point..];
        return(negative?"-":"")+plain;
    }
    private static double Density(double x)
    {
        var exponent=x*x/2;
        if(!double.IsFinite(exponent)||exponent>8)throw Error("risk_parametric_numeric","The qualified normal-density domain is limited to scores from -4 to 4.");
        // exp(t/16), four squarings, then a reciprocal: positive terms avoid
        // cancellation. Fixed ordinary binary64 operations avoid native libm exp.
        var term=1d;var sum=1d;
        for(var n=1;n<=48;n++){term*=exponent/(16*n);var next=sum+term;if(next==sum)break;sum=next;}
        for(var n=0;n<4;n++)sum*=sum;
        return 1/sum/Math.Sqrt(2*Math.PI);
    }
    // Positive normal integral series, inverted by a fixed bounded bisection.
    // The admitted confidence range avoids cancellation in extreme tails.
    private static double NormalQuantile(double alpha)
    {
        double Cdf(double x)
        {
            var term=x;var sum=x;
            for(var i=1;i<=256;i++){term*=x*x/(2*i+1);var next=sum+term;if(next==sum)break;sum=next;}
            return .5+Density(x)*sum;
        }
        double low=0,high=4;
        for(var i=0;i<64;i++){var mid=(low+high)/2;if(Cdf(mid)<alpha)low=mid;else high=mid;}
        return(low+high)/2;
    }
    private static int Rank(SignedRatio[][] matrix,CancellationToken ct)
    {
        var a=matrix.Select(r=>r.ToArray()).ToArray();var rank=0;
        for(var col=0;col<a.Length;col++)
        {
            ct.ThrowIfCancellationRequested();var pivot=Enumerable.Range(rank,a.Length-rank).FirstOrDefault(i=>!a[i][col].Numerator.IsZero,-1);
            if(pivot<0)continue;(a[rank],a[pivot])=(a[pivot],a[rank]);
            for(var r=rank+1;r<a.Length;r++)
            {
                if(a[r][col].Numerator.IsZero)continue;var scale=Divide(a[r][col],a[rank][col]);
                for(var c=col;c<a.Length;c++)a[r][c]=Bound(a[r][c]-scale*a[rank][c]);
            }
            rank++;
        }
        return rank;
    }
    // Infinity-norm condition of the dimensionless correlation coordinates.
    // No inverse is used for VaR and no covariance repair is performed.
    private static double? Condition(SignedRatio[][] matrix)
    {
        var n=matrix.Length;var a=new double[n][];var inverse=new double[n][];
        for(var i=0;i<n;i++)
        {a[i]=new double[n];inverse[i]=new double[n];inverse[i][i]=1;for(var j=0;j<n;j++)a[i][j]=Real(matrix[i][j])/(Math.Sqrt(Real(matrix[i][i]))*Math.Sqrt(Real(matrix[j][j])));}
        var norm=a.Max(row=>row.Sum(Math.Abs));
        for(var col=0;col<n;col++)
        {
            var pivot=Enumerable.Range(col,n-col).MaxBy(r=>Math.Abs(a[r][col]));
            if(a[pivot][col]==0)return null;(a[pivot],a[col])=(a[col],a[pivot]);(inverse[pivot],inverse[col])=(inverse[col],inverse[pivot]);
            var divisor=a[col][col];for(var j=0;j<n;j++){a[col][j]/=divisor;inverse[col][j]/=divisor;}
            for(var r=0;r<n;r++)if(r!=col){var scale=a[r][col];for(var j=0;j<n;j++){a[r][j]-=scale*a[col][j];inverse[r][j]-=scale*inverse[col][j];}}
        }
        var value=norm*inverse.Max(row=>row.Sum(Math.Abs));return double.IsFinite(value)?value:null;
    }
}
