namespace OpenEtrm.MarketData.Trading;

// Round each determination under its retained contract before taking the delta.
// Rounding an exact difference instead changes half-minor-unit corrections.
public static class CashRestatement
{
    public static Money Contribution(Money roundedTotal, Money? previousRoundedTotal)
    {
        if (roundedTotal is null)
            throw new MarketDataException("settlement_determination", "Supply the rounded contractual total.");
        return previousRoundedTotal is null ? roundedTotal :
            ExactMoney.FromMoney(roundedTotal).Subtract(ExactMoney.FromMoney(previousRoundedTotal)).ToMoney();
    }
}
