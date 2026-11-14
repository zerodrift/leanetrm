using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Risk;

// Native quote identity is shared across market graphs. Graph reachability may
// differ; source evidence, quotation and the retained base level may not.
public static class QuoteFactors
{
    public static bool SameDeclaration(QuoteFactor a,QuoteFactor b)=>a.Input==b.Input&&a.Quote==b.Quote&&a.Unit==b.Unit&&a.Contract==b.Contract&&
        a.Observation==b.Observation&&a.Source==b.Source&&a.Role==b.Role&&a.BaseValue==b.BaseValue&&a.TradingTick==b.TradingTick;
    public static ImmutableArray<QuoteFactor> Union(IEnumerable<QuoteFactor> factors)
    {
        var values=factors.ToArray();
        if(values.Any(f=>f is null||f.Curves.IsDefault))throw Error();
        var groups=values.GroupBy(f=>f.Input).ToArray();
        if(groups.Any(g=>g.Any(f=>!SameDeclaration(g.First(),f))))throw Error();
        return [..groups.Select(g=>g.First() with{Curves=[..g.SelectMany(f=>f.Curves).Distinct().OrderBy(c=>c.Revision.CatalogId)
                .ThenBy(c=>c.Revision.RevisionId).ThenBy(c=>c.Code,StringComparer.Ordinal)]})
            .OrderBy(f=>f.Input.Series.Revision.CatalogId).ThenBy(f=>f.Input.Series.Revision.RevisionId).ThenBy(f=>f.Input.Series.Id)];
    }
    private static MarketDataException Error()=>new("risk_factor_conflict","One native quote input cannot retain inconsistent source observations, quotation, units or base levels across market graphs.");
}
