using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static partial class LifecyclePortfolioProjection
{
    internal static void RequireFullOptionValue(FuturesMarketBinding underlying,PortfolioValuationPolicy pricing)
    {
        if(underlying.SettlementMethod=="cash"&&pricing.NativeFutures?.Economic is not null)
            throw Error("projection_cash_option_value","This full valuation profile requires a qualified saved-margin-due cash-option funding model, which is unavailable.");
    }
}
