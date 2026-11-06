using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasCashBillingChecks
{
    internal static void Run()
    {
        var f = new CommodityLookupFixture(1);
        var start = new DateTimeOffset(2021, 1, 1, 6, 0, 0, TimeSpan.Zero);
        ExactQuantity Q(string value) => ExactQuantity.FromQuantity(new(ExactDecimal.Parse(value), f.Quote.Quantity));
        var day = new GasDailyContract("gas/0/quantity", 0, 0, new(2021, 1, 1), start, start.AddDays(1), Q("100"));
        GasSelectedQuantity Final(string value) => GasDailyQuantitySelection.Select(day,
            [new(Guid.NewGuid(), "allocated", "final", Q(value), "Synthetic accepted statement", "manual_daily_v1")],
            GasDailyQuantitySelection.BillingPolicy, day.End, new ExactRatio(100, 1));
        var terms = new GasBillingTerms(day, new(ExactDecimal.Parse("3"), f.Quote), ExactDecimal.Parse("-1"),
            day.End.AddDays(30), 2, "currency_minor_units_to_even_v1");
        var original = GasCashBilling.Calculate(terms, Final("100"), day.End, null);
        Equal(original.Amount.Amount.ToString(), "-300");
        Equal(original.Contribution.Amount.ToString(), "-300");
        var corrected = GasCashBilling.Calculate(terms, Final("98"), day.End.AddMonths(2), original.Amount);
        Equal(corrected.Amount.Amount.ToString(), "-294");
        Equal(corrected.Contribution.Amount.ToString(), "6");
        Equal(original.Amount.Amount.ToString(), "-300");
        Equal(GasCashBilling.Calculate(terms, Final("0"), day.End, corrected.Amount).Contribution.Amount.ToString(), "294");
        Equal(GasCashBilling.Calculate(terms with { Price = new(ExactDecimal.Parse("-3"), f.Quote) }, Final("98"), day.End, null).Amount.Amount.ToString(), "294");
        var tiny = terms with { Price = new(ExactDecimal.Parse("0.005"), f.Quote) };
        var first = GasCashBilling.Calculate(tiny, Final("1"), day.End, null);
        var second = GasCashBilling.Calculate(tiny, Final("2"), day.End, first.Amount);
        Equal(first.Amount.Amount.ToString(), "0");
        Equal(second.Amount.Amount.ToString(), "-0.01");
        Equal(second.Contribution.Amount.ToString(), "-0.01");
        Reject(() => GasCashBilling.Calculate(terms, Final("100"), start, null));
        Reject(() => GasCashBilling.Calculate(terms, Final("100") with { RevisionId = null }, day.End, null));
        Reject(() => GasCashBilling.Calculate(terms with { RoundingPolicy = "unknown" }, Final("100"), day.End, null));
        Reject(() => GasCashBilling.Calculate(terms with { Direction = default }, Final("100"), day.End, null));
        Console.WriteLine("PASS gas cash: final native-day quantities, independent signs, exact rounding, retained originals and correction deltas");
    }
    static void Equal(string actual, string expected)
    { if (actual != expected) throw new Exception($"Expected {expected}, got {actual}"); }
    static void Reject(Action action)
    { try { action(); } catch (MarketDataException) { return; } throw new Exception("Expected gas billing refusal"); }
}
