using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// One operation owns the pinned provider. Unit contexts add the instrument's
// declaration fence; books remain independent of the curve implementation.
public abstract class PreparedValuationMarket
{
    public abstract MarketSnapshot Snapshot{get;}
    public abstract string ScenarioId{get;}
    public abstract string NumericalPolicy{get;}
    internal abstract object Identity{get;}
    internal abstract CurveBuild? Authority{get;}
    internal abstract ExactUnitPrice Price(CurveRequest request,CancellationToken cancellation);
    internal virtual UnitPrice WorkingPrice(CurveRequest request,CancellationToken cancellation)=>Price(request,cancellation).ToUnitPriceExact();
    internal abstract ImmutableArray<QuoteFactor> Factors(ImmutableArray<CurveRequest> requests,CancellationToken cancellation);
    internal abstract bool Compatible(PreparedValuationMarket prior);
    internal abstract bool Unchanged(PreparedValuationMarket prior,ImmutableArray<CurveRequest> requests,CancellationToken cancellation);
    internal abstract SideValuationContext Context(ISideDiscounting? discounting,ImmutableArray<CurveRequest> dependencies,CancellationToken cancellation);
    internal PreparedValuationMarket(){}
}

internal sealed class BuiltValuationMarket : PreparedValuationMarket
{
    private readonly CurveBuild market;
    private readonly ImmutableDictionary<CurveRequest,BuiltValue> values;
    private readonly Lazy<ImmutableArray<QuoteFactor>> factors;
    internal BuiltValuationMarket(CurveBuild market)
    {
        this.market=market;values=market.Values.ToImmutableDictionary(v=>v.Request);
        factors=new(()=>market.Model.QuoteFactors(market.Snapshot).Select(f=>f with{BaseValue=f.BaseValue+market.Scenario.Offset(f.Input)}).ToImmutableArray());
    }
    public override MarketSnapshot Snapshot=>market.Snapshot;
    public override string ScenarioId=>market.Scenario.Id;
    public override string NumericalPolicy=>market.NumericalPolicy;
    internal override object Identity=>market;
    internal override CurveBuild Authority=>market;
    internal override ExactUnitPrice Price(CurveRequest request,CancellationToken cancellation)
    {cancellation.ThrowIfCancellationRequested();return ExactUnitPrice.FromPrice(Require(request).Price);}
    internal override UnitPrice WorkingPrice(CurveRequest request,CancellationToken cancellation)
    {cancellation.ThrowIfCancellationRequested();return Require(request).Price;}
    internal override ImmutableArray<QuoteFactor> Factors(ImmutableArray<CurveRequest> requests,CancellationToken cancellation)
    {
        var observations=new HashSet<ObservationKey>();
        foreach(var request in requests){cancellation.ThrowIfCancellationRequested();observations.UnionWith(Require(request).Lineage);}
        return factors.Value.Where(f=>observations.Contains(f.Observation)).ToImmutableArray();
    }
    internal override bool Compatible(PreparedValuationMarket prior)=>prior is BuiltValuationMarket built&&ReferenceEquals(market.Model,built.market.Model)&&Snapshot.HasSameInputs(built.Snapshot);
    internal override bool Unchanged(PreparedValuationMarket prior,ImmutableArray<CurveRequest> requests,CancellationToken cancellation)
    {
        if(prior is not BuiltValuationMarket built)return false;
        foreach(var request in requests){cancellation.ThrowIfCancellationRequested();if(!ReferenceEquals(Require(request),built.Require(request)))return false;}
        return true;
    }
    internal override SideValuationContext Context(ISideDiscounting? discounting,ImmutableArray<CurveRequest> dependencies,CancellationToken cancellation)=>new(market,discounting,values,dependencies);
    private BuiltValue Require(CurveRequest request)=>values.TryGetValue(request,out var value)?value:
        throw new MarketDataException("risk_dependency","An instrument's declared market input is unavailable.");
}
