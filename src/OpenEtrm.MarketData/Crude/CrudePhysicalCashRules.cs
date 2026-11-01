using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using static OpenEtrm.MarketData.Crude.CrudeReceiptCash;
namespace OpenEtrm.MarketData.Crude;

internal static class CrudePhysicalCashRules
{
    internal static void RequireTerms(CrudePhysicalCashTerms terms, string implementation = CrudeReceiptCash.Implementation, string considerationDirection = "-1")
    {
        if (terms is null || terms.Implementation != implementation || terms.TradeId == Guid.Empty || terms.OwnerLegalEntityId == Guid.Empty ||
            terms.Location is null || terms.Quantity is null || terms.Delivery is null || terms.Consideration?.Price is null ||
            terms.Consideration.Price.Value.CompareTo(default) <= 0 || terms.Consideration.Direction != ExactDecimal.Parse(considerationDirection) ||
            terms.Quantity.Dimension.Code != "volume" || terms.StandardTemperature != ExactDecimal.Parse("60") || terms.TemperatureScale != "fahrenheit")
            throw Error("crude_receipt_terms", "Select the explicit fixed-price physical cash contract with explicit standard volume, ownership and cash terms.");
        foreach (var reference in new[] { terms.Quantity.Key, terms.Quantity.Commodity, terms.Quantity.MeasurementBasis,
            terms.Quantity.Unit, terms.Quantity.Dimension, terms.Consideration.Price.Unit.Currency })
        {
            Money.RequireKey(reference);
            if (reference.Revision != terms.Quantity.Key.Revision)
                throw Error("crude_receipt_terms", "All receipt quantity and currency references must retain the exact selected revision.");
        }
        Money.RequireKey(terms.Grade);
        if (terms.Grade.Revision != terms.Quantity.Key.Revision || terms.Location.Facility.Revision != terms.Quantity.Key.Revision)
            throw Error("crude_receipt_terms", "The receipt contract requires exact references from one selected revision.");
        RequireQuantity(terms.ContractQuantity, terms.Quantity.Key, "crude_receipt_terms");
        RequireTime(terms.PaymentAt, "crude_receipt_terms");
        if (terms.PaymentAt < terms.Delivery.To) throw Error("crude_receipt_terms", "Retain the original contractual payment date.");
        RequireRounding(terms.Rounding);
        foreach (var rate in terms.HandlingFee is null ? new[] { terms.Consideration } : new[] { terms.Consideration, terms.HandlingFee })
            if (rate.Price is null || rate.Price.Value.CompareTo(default) < 0 || !Sign(rate.Direction) || rate.Price.Unit.Quantity != terms.Quantity.Key || rate.Price.Unit.Multiplier != ExactDecimal.Parse("1") ||
                rate.Price.Unit.Currency != terms.Consideration.Price.Unit.Currency)
                throw Error("crude_receipt_terms", "Cash components require their independent explicit signs and exact native units/currency.");
    }
    internal static void RequirePaymentAmount(Money? amount, ReferenceKey currency, ExactDecimal direction, CrudeCashRounding rounding)
    {
        if (amount is null || amount.Currency != currency || amount.Amount == default ||
            (amount.Amount.CompareTo(default) > 0) != (direction.CompareTo(default) > 0) ||
            Round(ExactMoney.FromMoney(amount), rounding) != amount)
            throw Error("crude_payment_amount", "Payments require the component currency, direction and cash precision explicitly.");
    }

    internal static void RequireRounding(CrudeCashRounding rounding)
    {
        if (rounding is null || rounding.Implementation != CrudeReceiptCash.RoundingImplementation || rounding.DecimalPlaces is < 0 or > 12 || rounding.TieRule != CashTieRule.ToEven)
            throw Error("crude_cash_rounding", "Select explicit currency precision and the supported cash rounding version.");
    }
    internal static Money Round(ExactMoney value, CrudeCashRounding rounding) => value.ToMoney(new AmountFormat(rounding.DecimalPlaces, rounding.TieRule, false, false));
    internal static bool Sign(ExactDecimal value) => value == ExactDecimal.Parse("1") || value == ExactDecimal.Parse("-1");
    internal static ExactDecimal Magnitude(ExactDecimal value) => value.CompareTo(default) < 0 ? default(ExactDecimal) - value : value;
    internal static ExactDecimal Sum(IEnumerable<ExactDecimal> values) => values.Aggregate(default(ExactDecimal), (sum, value) => sum + value);
    internal static void RequireQuantity(Quantity quantity, ReferenceKey specification, string code)
    { if (quantity is null || quantity.Specification != specification || quantity.Amount.CompareTo(default) <= 0) throw Error(code, "Use a positive quantity in the exact contractual standard-volume specification."); }
    internal static void RequireTime(DateTimeOffset at, string code)
    { if (!TradingInstant.IsSupported(at)) throw Error(code, "Use a supported exact UTC instant."); }
    internal static void RequireFacts<T>(ImmutableArray<T> facts, Func<T, Guid> key) where T : class
    {
        if (facts.IsDefault || facts.Length > 10000 || facts.Any(f => f is null) || facts.Any(f => key(f) == Guid.Empty) || facts.Select(key).Distinct().Count() != facts.Length)
            throw Error("crude_receipt_facts", "Supply explicit bounded facts with distinct durable identities.");
    }
    internal static MarketDataException Error(string code, string message) => new(code, message);
}
