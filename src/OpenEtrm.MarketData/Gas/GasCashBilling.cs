using System.Numerics;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasBillingTerms(GasDailyContract Day, UnitPrice Price, ExactDecimal Direction,
    DateTimeOffset PaymentAt, int DecimalPlaces, string RoundingPolicy);
public sealed record GasBillingCalculation(ExactMoney ExactAmount, Money Amount, Money Contribution);

// One cash membership and one retained native day. This does not recognize title,
// create an invoice/payment, or replace contractual portfolio valuation.
public static class GasCashBilling
{
    public const string Implementation = "gas_final_daily_fixed_cash_v1";
    public const string RoundingImplementation = "currency_minor_units_to_even_v1";

    public static GasBillingCalculation Calculate(GasBillingTerms terms, GasSelectedQuantity selected,
        DateTimeOffset at, Money? previousRoundedTotal)
    {
        if (terms is null || terms.Day is null || terms.Price is null || selected is null ||
            terms.Day.Quantity is null || selected.Quantity is null || !TradingInstant.IsSupported(at) || !TradingInstant.IsSupported(terms.PaymentAt) ||
            terms.Day.Start >= terms.Day.End || at < terms.Day.End || terms.PaymentAt < terms.Day.End ||
            terms.Direction != ExactDecimal.Parse("1") && terms.Direction != ExactDecimal.Parse("-1") ||
            terms.RoundingPolicy != RoundingImplementation || terms.DecimalPlaces is < 0 or > 12 ||
            selected.Kind != "allocated" || selected.RevisionId is null || selected.RevisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(selected.Evidence) || selected.Quantity.Specification != terms.Day.Quantity.Specification ||
            BigInteger.Parse(selected.Quantity.Numerator) < 0 || terms.Price.Unit.Quantity != selected.Quantity.Specification)
            throw new MarketDataException("gas_billing_input", "Supply a completed native day, final accepted quantity, exact fixed cash terms and explicit currency rounding.");
        var exact = selected.Quantity.Times(terms.Price).Scale(terms.Direction);
        var rounded = exact.ToMoney(new AmountFormat(terms.DecimalPlaces, CashTieRule.ToEven, false, false));
        return new(exact, rounded, CashRestatement.Contribution(rounded, previousRoundedTotal));
    }
}
