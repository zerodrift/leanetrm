using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Settlement;

// Request-owned native price evidence. Construction goes through the same source
// validation and temporal resolver as valuation; no caller-supplied price totals.
public sealed class GrossIndexedPrices
{
    internal MonthlyGrossPlan Plan { get; }
    internal DateTimeOffset KnownAt { get; }
    private readonly ImmutableDictionary<(CurveKey,DateTimeOffset),IntervalPriceResolution> cells;
    private GrossIndexedPrices(MonthlyGrossPlan plan,DateTimeOffset knownAt,ImmutableDictionary<(CurveKey,DateTimeOffset),IntervalPriceResolution> cells)
    {Plan=plan;KnownAt=knownAt;this.cells=cells;}
    internal IntervalPriceResolution At(PlannedPaymentContribution row)=>cells[(((PpaFloatingSide)MonthlyGrossPreview.Member(row.Binding).Side).Price.Curve,row.Interval.Start)];

    public static GrossIndexedPrices Resolve(MonthlyGrossPlan plan,IReadOnlyDictionary<CurveKey,PreparedCurve> curves,CurveBuild market,DateTimeOffset knownAt)
    {
        var groups=plan.Contributions.GroupBy(r=>((MonthlyGrossPreview.Member(r.Binding).Side as PpaFloatingSide)
            ??throw Invalid("Only indexed physical payment plans accept index evidence.")).Price.Curve).ToArray();
        if(curves.Count!=groups.Length || market.Snapshot.ValuationAt!=plan.ValuationAt ||
            curves.Values.Any(c=>c.KnownAt!=knownAt || c.Curve is not (MarkedIntervalCurve or ShapedIntervalCurve)))
            throw Invalid("The exact plan requires only its explicitly bound native indexes at the selected temporal coordinates.");
        var resolver=new IntervalPriceResolver(market,curves);
        var result=ImmutableDictionary.CreateBuilder<(CurveKey,DateTimeOffset),IntervalPriceResolution>();
        foreach(var group in groups)
        {
            if(!curves.TryGetValue(group.Key,out var curve))throw Invalid("A payment index is missing.");
            var resolved=resolver.Prices(group.Key,plan.Identity.Month).ToDictionary(p=>p.Start);
            var required=group.Select(r=>r.Interval.Start).ToHashSet();
            if(!required.SetEquals(resolved.Keys))throw Invalid("The native index coverage must match its complete contractual contribution coverage.");
            foreach(var row in group)
            {
                var price=resolved[row.Interval.Start];var term=((PpaFloatingSide)MonthlyGrossPreview.Member(row.Binding).Side).Price;
                if(price.Interval!=row.Interval.Delivery || price.State!=IntervalPriceState.Actual || price.Price is null || price.Actual is null ||
                    price.Publication!="collected" || curve.Unit!=term.Adder.Unit)
                    throw Invalid("Every ended native interval needs a compatible eligible collected index price; estimates and missing prices refuse the occurrence.");
                result.Add((group.Key,row.Interval.Start),price);
            }
        }
        return new(plan,knownAt,result.ToImmutable());
    }
    private static MarketDataException Invalid(string message)=>new("settlement_index_evidence",message);
}
