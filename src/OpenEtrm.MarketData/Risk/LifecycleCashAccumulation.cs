using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Risk;

// Horizon cash funding is a portfolio choice. It is independent of the native
// curves that carry unpaid product obligations and physical residuals.
public sealed record LifecycleCashAccumulation(string Implementation,ImmutableDictionary<ReferenceKey,IProjectionCurrencyCurve> Curves)
{
    public const string Version="explicit_portfolio_currency_cash_accumulation_v1";
    internal void Require(MarketSnapshot opening,LifecycleProjectionPolicy policy,ImmutableArray<ILifecycleProjection> components)
    {
        var currencies=components.Select(c=>c.Currency).ToHashSet();
        if(Implementation!=Version||Curves is null||
            (policy.CashAccumulation==LifecycleProjectionPolicy.Nominal?!Curves.IsEmpty:!currencies.SetEquals(Curves.Keys))||
            Curves.Any(p=>p.Value is not {} curve||curve.Currency!=p.Key||curve.TimingPolicy!=RateDiscountTiming.Version||
                curve.SourceSnapshot is not {} source||source.Knowledge!=opening.Knowledge||source.ValuationAt!=opening.ValuationAt||
                source.MarketCutoff!=opening.MarketCutoff||source.Id==Guid.Empty||!source.HasCoherentSelections()||
                source.SelectionPolicy!=opening.SelectionPolicy||source.Definitions.CatalogId!=opening.Definitions.CatalogId||source.Definitions.RevisionId==Guid.Empty||
                curve.CurveIdentity is not {} key||key.Curve.Revision!=source.Definitions||string.IsNullOrWhiteSpace(key.Curve.Code)||
                key.MappingId==Guid.Empty||key.DefinitionId==Guid.Empty||key.DefinitionVersion<1))
            throw LifecycleProjection.Error("projection_cash_policy","Explicit cash accumulation requires an empty nominal selection or one exact captured native curve for every component currency.");
    }
}
