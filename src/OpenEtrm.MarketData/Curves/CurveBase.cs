using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;

namespace OpenEtrm.MarketData.Curves;

public sealed record CurveDomain(DateOnly From,DateOnly To,bool EndInclusive);
public abstract record CurveInputReference;
public sealed record PricePointInput(CurvePointKey Point,ExactDecimal Weight):CurveInputReference;
public sealed record RatePillarInput(SeriesKey Series,DateOnly Maturity,ObservationKey Observation):CurveInputReference;

// Pure evaluators consume already resolved, authorized definitions and inputs.
// The common API does not turn rate maturities into commodity contracts.
public abstract class CurveBase<TRequest,TValue> where TRequest:notnull
{
    public CurveKey Key{get;}
    public abstract string Policy{get;}
    public abstract ImmutableArray<CurveDomain> Coverage{get;}
    protected CurveBase(CurveKey key)=>Key=key;
    public abstract void ValidateQuery(TRequest request);
    public abstract ImmutableArray<CurveInputReference> RequiredInputs(TRequest request);
    public abstract TValue Evaluate(TRequest request);
    public virtual ImmutableArray<TValue> EvaluateMany(ImmutableArray<TRequest> requests)
    {
        if(requests.IsDefault||requests.Length>366)throw new MarketDataException("curve_batch","Supply an explicit batch of at most 366 requests.");
        var unique=new Dictionary<TRequest,TValue>();
        return [..requests.Select(q=>unique.TryGetValue(q,out var prior)?prior:unique[q]=Evaluate(q))];
    }
}
