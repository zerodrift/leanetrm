using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

internal static class DraftRisk
{
    internal static object Result(PreviewData data,DealRiskRun risk,string measure,ImmutableArray<ProjectionPolicyReceipt> projectionPolicies=default)
    {
        string Currency(ReferenceKey key)=>data.References["currencies"].Single(c=>c.Text("code")==key.Code).Text("display_code");
        string Unit(PriceUnit unit)
        {
            var quantity=data.References["quantity_specifications"].Single(q=>q.Text("code")==unit.Quantity.Code);
            var symbol=data.References["units"].Single(u=>u.Text("code")==quantity.Text("unit_code")).Text("symbol");
            return $"{unit.Multiplier} {Currency(unit.Currency)} / {symbol}";
        }
        return new{unbooked=true,measure,bumpPolicy=DealRisk.BumpPolicy,numericalPolicy=DealRisk.NumericalPolicy,
            projectionPolicies=projectionPolicies.IsDefault?ImmutableArray<ProjectionPolicyReceipt>.Empty:projectionPolicies,
            coverage="commodity_quote_factors_rates_held_fixed",risk.ScenarioCount,currency=Currency(risk.Base.Currency),
            factors=risk.Factors.Select(row=>new{factor=new{seriesId=row.Factor.Input.Series.Id,quote=row.Factor.Quote.Code,
                curves=row.Factor.Curves.Select(c=>c.Code),unit=Unit(row.Factor.Unit),contract=row.Factor.Contract,
                deliveryFrom=data.Contracts.Single(c=>c.Key==row.Factor.Contract).DeliveryFrom,deliveryTo=data.Contracts.Single(c=>c.Key==row.Factor.Contract).DeliveryTo},
                bump=row.Bump.Size.ToString(),basePv=row.Values.Base.ToExactString(),upPv=row.Values.Up.ToExactString(),downPv=row.Values.Down.ToExactString(),
                upPnl=row.Values.UpPnl.ToExactString(),downPnl=row.Values.DownPnl.ToExactString(),delta=row.Values.Delta.Value.ToExactString(),gamma=row.Values.Gamma.Value.ToExactString(),
                deltaUnit=$"{Currency(row.Values.Base.Currency)} / ({Unit(row.Factor.Unit)})",gammaUnit=$"{Currency(row.Values.Base.Currency)} / ({Unit(row.Factor.Unit)})²"})};
    }
}
