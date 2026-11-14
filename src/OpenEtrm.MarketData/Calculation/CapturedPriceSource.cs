using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace OpenEtrm.MarketData.Calculation;

// An exact already-admitted source graph for a declared pricing dependency.
// The caller retains authorization separately; this is calculation evidence.
public sealed class CapturedPriceSource
{
    public CurveRequest Market{get;}
    public CurvePointKey Point{get;}
    public MarketSnapshot Snapshot=>Valuation.Snapshot;
    public string ManifestId=>Valuation.Scenario.Plan.ManifestId;
    [JsonIgnore]public ConnectedValuation Valuation{get;}
    public CapturedPriceSource(CurveRequest market,ConnectedValuation valuation,CurvePointKey point)
    {
        if(valuation is null||valuation.Scenario.Depth!=0||valuation.Scenario.CalculationAt!=valuation.Snapshot.ValuationAt||
            market.Curve.Revision!=valuation.Snapshot.Definitions||market.Contract.Id==Guid.Empty||market.Contract.Revision!=market.Curve.Revision||
            !valuation.Scenario.Plan.Points.ContainsKey(point))
            throw new MarketDataException("risk_dependency","Select the exact original admitted source graph and bound point for this declared price.");
        Market=market;Valuation=valuation;Point=point;
    }
}

public sealed partial class ConnectedCurvePlan
{
    private readonly ConditionalWeakTable<ConnectedCurvePlan,ConcurrentDictionary<CurvePointKey,bool>> sameCalculations=new();
    public bool HasSameCalculation(CurvePointKey point,ConnectedCurvePlan other,CurvePointKey otherPoint,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(other is null||point!=otherPoint||Context.ContextId!=other.Context.ContextId||Context.EffectiveAt!=other.Context.EffectiveAt||
            Context.KnownAt!=other.Context.KnownAt||Context.ValuationAt!=other.Context.ValuationAt||Context.MarketAsOf!=other.Context.MarketAsOf)return false;
        return sameCalculations.GetValue(other,_=>new()).GetOrAdd(point,_=>Same());
        bool Same()
        {
            var seen=new HashSet<CurvePointKey>();var pending=new Stack<CurvePointKey>();pending.Push(point);
            while(pending.TryPop(out var key))
            {
                cancellation.ThrowIfCancellationRequested();if(!seen.Add(key))continue;
                if(!Points.TryGetValue(key,out var a)||!other.Points.TryGetValue(key,out var b)||a.DeliveryFrom!=b.DeliveryFrom||a.DeliveryTo!=b.DeliveryTo||
                    a.Unit!=b.Unit||a.Contract!=b.Contract||a.PointDate!=b.PointDate||
                    ConnectedIdentity.Digest(new{a.Expression})!=ConnectedIdentity.Digest(new{b.Expression})||
                    !resolvedPointDependencies[key].SequenceEqual(other.resolvedPointDependencies[key]))return false;
                foreach(var dependency in resolvedPointDependencies[key])pending.Push(dependency);
            }
            return true;
        }
    }
}
