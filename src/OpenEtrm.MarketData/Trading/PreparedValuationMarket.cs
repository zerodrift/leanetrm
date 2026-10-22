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
    internal abstract ImmutableHashSet<ObservationKey> Lineage(CurveRequest request,CancellationToken cancellation);
    internal abstract ImmutableArray<QuoteFactor> Factors(ImmutableArray<CurveRequest> requests,CancellationToken cancellation);
    internal abstract bool Compatible(PreparedValuationMarket prior);
    internal abstract bool Unchanged(PreparedValuationMarket prior,ImmutableArray<CurveRequest> requests,CancellationToken cancellation);
    internal abstract SideValuationContext Context(ISideDiscounting? discounting,ImmutableArray<CurveRequest> dependencies,CancellationToken cancellation);
    internal PreparedValuationMarket(){}
}
