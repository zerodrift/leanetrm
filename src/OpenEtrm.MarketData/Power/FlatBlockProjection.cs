using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Power;

// An explicitly flat forward plan. Owners index exact source-curve/contract
// anchors, independently of product ports or a consumer's payment grouping.
// Owner -1 is confined to an explicitly declared actual-only prefix: it has no
// native forward contract and may never receive a projected price.
public sealed record FlatBlockProjection(ImmutableArray<PowerInterval> Intervals,
    ImmutableArray<(CurveRequest Request,MarketContract Contract)> Anchors,ImmutableArray<int> Owners);
