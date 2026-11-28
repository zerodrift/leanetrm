using System.Numerics;
using OpenEtrm.Shared.Numerics;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasBillingTerms(GasDailyContract Day, UnitPrice Price, ExactDecimal Direction,
    DateTimeOffset PaymentAt, int DecimalPlaces, string RoundingPolicy);
public sealed record GasBillingCalculation(ExactMoney ExactAmount, Money Amount, Money Contribution);
public sealed record GasBillingEntryTerms(DateOnly From, DateOnly To, ExactRatio Share,
    QuantityPart? Part, SignedRatio HeldBaseQuantity);
// The published observation selected for one payment day. Final is the observation's own publication state.
public sealed record GasBillingFixing(Guid ObservationId, UnitPrice Value, DateTimeOffset ObservedAt, bool Final);

// One cash membership and one retained native day. This does not recognize title,
// create an invoice/payment, or replace contractual portfolio valuation.
public static class GasCashBilling
{
    public const string Implementation = "gas_final_daily_fixed_cash_v1";
    public const string EntryImplementation = "gas_final_daily_entry_cash_v1";
    public const string IndexedImplementation = "gas_final_daily_indexed_cash_v1";
    public const string FixingPolicy = "final_fixing_head_v1";
    public const string RoundingImplementation = "currency_minor_units_to_even_v1";

    // The unit price an indexed payment bills for one gas day: its final published fixing plus the booked spread,
    // unrounded. A forecast, a preliminary publication or a fixing outside its publication window is not a price.
    public static UnitPrice IndexedPrice(GasBillingFixing fixing, UnitPrice spread, DateTimeOffset fixingAt, DateTimeOffset at)
    {
        if (fixing is null || spread is null || fixing.Value is null || fixing.ObservationId == Guid.Empty || !fixing.Final ||
            !TradingInstant.IsSupported(fixing.ObservedAt) || fixing.ObservedAt < fixingAt || fixing.ObservedAt > at ||
            fixing.Value.Unit != spread.Unit)
            throw new MarketDataException("gas_billing_fixing", "Supply the payment day's final published fixing and the booked spread in the same quote.");
        return fixing.Value.Add(spread);
    }

    public static bool SupportsEntry(ExactRatio share,bool hasPart,DateOnly from,DateOnly to,DateOnly sourceFrom,DateOnly sourceTo) =>
        share==ExactRatio.One&&!hasPart&&from==sourceFrom&&to==sourceTo;

    public static GasBillingCalculation CalculateEntry(GasBillingTerms terms, GasBillingEntryTerms entry,
        GasSelectedQuantity selected, DateTimeOffset at, Money? previousRoundedTotal)
    {
        RequireFinal(terms, selected, at);
        if (entry is null || entry.Share is null || entry.Share.Numerator <= 0 ||
            entry.From == default || entry.From >= entry.To || terms.Day.Day < entry.From || terms.Day.Day >= entry.To ||
            entry.HeldBaseQuantity.Numerator <= 0 || entry.HeldBaseQuantity.Denominator <= 0)
            throw new MarketDataException("gas_billing_input", "Supply the retained payment dates, share and positive held trade basis.");
        var quantity = new SignedRatio(BigInteger.Parse(selected.Quantity.Numerator), BigInteger.Parse(selected.Quantity.Denominator));
        var taken = entry.Part?.Take(quantity, entry.HeldBaseQuantity) ?? quantity;
        var exact = ExactQuantity.FromRatio(taken, selected.Quantity.Specification).Scale(entry.Share).Times(terms.Price).Scale(terms.Direction);
        return Round(terms, exact, previousRoundedTotal);
    }

    public static GasBillingCalculation Calculate(GasBillingTerms terms, GasSelectedQuantity selected,
        DateTimeOffset at, Money? previousRoundedTotal)
    {
        RequireFinal(terms, selected, at);
        return Round(terms, selected.Quantity.Times(terms.Price).Scale(terms.Direction), previousRoundedTotal);
    }

    static void RequireFinal(GasBillingTerms terms, GasSelectedQuantity selected, DateTimeOffset at)
    {
        if (terms is null || terms.Day is null || terms.Price is null || selected is null ||
            terms.Day.Quantity is null || selected.Quantity is null || !TradingInstant.IsSupported(at) || !TradingInstant.IsSupported(terms.PaymentAt) ||
            terms.Day.Start >= terms.Day.End || at < terms.Day.End || PaymentDatePolicy.Date(terms.PaymentAt) < PaymentDatePolicy.Date(terms.Day.End) ||
            terms.Direction != ExactDecimal.Parse("1") && terms.Direction != ExactDecimal.Parse("-1") ||
            terms.RoundingPolicy != RoundingImplementation || terms.DecimalPlaces is < 0 or > 12 ||
            selected.Kind != "allocated" || selected.RevisionId is null || selected.RevisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(selected.Evidence) || selected.Quantity.Specification != terms.Day.Quantity.Specification ||
            BigInteger.Parse(selected.Quantity.Numerator) < 0 || terms.Price.Unit.Quantity != selected.Quantity.Specification)
            throw new MarketDataException("gas_billing_input", "Supply a completed native day, final accepted quantity, exact fixed cash terms and explicit currency rounding.");
    }

    static GasBillingCalculation Round(GasBillingTerms terms, ExactMoney exact, Money? previousRoundedTotal)
    {
        var rounded = exact.ToMoney(new AmountFormat(terms.DecimalPlaces, CashTieRule.ToEven, false, false));
        return new(exact, rounded, CashRestatement.Contribution(rounded, previousRoundedTotal));
    }
}
