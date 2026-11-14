using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

public sealed partial class PortfolioRiskPreview
{
    internal static void RequireStaticMethod(PortfolioRiskRequest request,IEnumerable<UnitPricingContract> contracts)
    {
        if(!request.Bumps.IsDefaultOrEmpty||request.Joint?.Derivatives is not null||request.Parametric is not null||
            request.Historical?.Comparison is not null||request.MonteCarlo?.Analysis is not null||request.MonteCarlo?.Hybrid is not null)
            RiskEngine.RequireStaticDerivatives(contracts);
    }
}
