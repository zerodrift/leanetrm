using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;

namespace OpenEtrm.MarketData.Curves;

public sealed record CurveDomain(DateOnly From,DateOnly To,bool EndInclusive);
public abstract record CurveInputReference;
public sealed record PricePointInput(CurvePointKey Point,ExactDecimal Weight):CurveInputReference;
public sealed record RatePillarInput(SeriesKey Series,DateOnly Maturity,ObservationKey Observation):CurveInputReference;

// Concrete policies retain their own interpolation, boundary and analytic terms.
// A model or fitter produces curve state; it is not an alternative lookup entry.
public abstract record CurveEvaluationPolicy(string Implementation,string AnalyticsImplementation);
public sealed record CurveLookupResult<TValue>(CurveKey Curve,CurveEvaluationPolicy Policy,
    ImmutableArray<CurveInputReference> Inputs,TValue Value);

// Resolve once: the evaluator and its reported dependencies consume the very
// same immutable selection. Implementations operate on already authorized inputs.
public abstract class CurveBase<TRequest,TValue,TSelection> where TRequest:notnull where TValue:notnull where TSelection:notnull
{
    public CurveKey Key{get;}
    public abstract CurveEvaluationPolicy EvaluationPolicy{get;}
    public abstract ImmutableArray<CurveDomain> Coverage{get;}
    protected CurveBase(CurveKey key)=>Key=key;
    protected abstract TSelection ResolveQuery(TRequest request);
    protected abstract ImmutableArray<CurveInputReference> RequiredInputs(TSelection selection);
    protected abstract TValue EvaluateResolved(TSelection selection);
    public CurveLookupResult<TValue> Lookup(TRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selection=ResolveQuery(request);
        if(selection is null)throw new MarketDataException("curve_query","A lookup must resolve an explicit query selection.");
        var inputs=RequiredInputs(selection);
        if(inputs.IsDefault||inputs.Any(input=>input is null))throw new MarketDataException("curve_inputs","A lookup must declare its resolved inputs, including an explicit empty set when appropriate.");
        var policy=EvaluationPolicy;
        if(policy is null||string.IsNullOrWhiteSpace(policy.Implementation)||string.IsNullOrWhiteSpace(policy.AnalyticsImplementation))
            throw new MarketDataException("curve_policy","A lookup must identify its lookup and analytical implementations explicitly.");
        var value=EvaluateResolved(selection);
        if(value is null)throw new MarketDataException("curve_value","A lookup cannot return a missing value as a successful result.");
        return new(Key,policy,inputs,value);
    }
    public ImmutableArray<CurveLookupResult<TValue>> LookupMany(ImmutableArray<TRequest> requests)
    {
        if(requests.IsDefault||requests.Length>366)throw new MarketDataException("curve_batch","Supply an explicit batch of at most 366 requests.");
        var unique=new Dictionary<TRequest,CurveLookupResult<TValue>>();
        return [..requests.Select(q=>unique.TryGetValue(q,out var prior)?prior:unique[q]=Lookup(q))];
    }
}
