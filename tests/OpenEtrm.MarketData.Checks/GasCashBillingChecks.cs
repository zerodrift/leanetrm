using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasCashBillingChecks
{
    internal static void Run()
    {
        var from=new DateOnly(2021,1,1);var to=from.AddDays(3);
        if(!GasCashBilling.SupportsEntry(ExactRatio.One,false,from,to,from,to)||
           GasCashBilling.SupportsEntry(new(21,20),false,from,to,from,to)||
           GasCashBilling.SupportsEntry(ExactRatio.One,true,from,to,from,to)||
           GasCashBilling.SupportsEntry(ExactRatio.One,false,from.AddDays(1),to,from,to))
            throw new Exception("Daily fixed billing admits only whole, full-term, share-one payments.");
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
        Entries(terms, Final, day.End);
        GasBillingReconciliationChecks.Run();
        Console.WriteLine("PASS gas cash: final native-day quantities, independent signs, exact rounding, retained originals and correction deltas");
    }
    static void Entries(GasBillingTerms terms, Func<string, GasSelectedQuantity> final, DateTimeOffset at)
    {
        var entry = new GasBillingEntryTerms(terms.Day.Day, terms.Day.Day.AddDays(2), new(2, 5), null, new(100, 1));
        terms = terms with { Price = new(ExactDecimal.Parse("2.25"), terms.Price.Unit), Direction = ExactDecimal.Parse("1") };
        GasBillingCalculation Bill(GasBillingEntryTerms e, string quantity, Money? previous = null) =>
            GasCashBilling.CalculateEntry(terms, e, final(quantity), at, previous);
        var original = Bill(entry, "80");
        Equal(original.Amount.Amount.ToString(), "72");
        var correction = Bill(entry, "60", original.Amount);
        Equal(correction.Amount.Amount.ToString(), "54");
        Equal(correction.Contribution.Amount.ToString(), "-18");
        Equal(Bill(entry with { Share = ExactRatio.One, Part = QuantityPart.Band(new(3, 10), new(7, 10)) }, "80").Amount.Amount.ToString(), "90");
        Equal(Bill(entry with { Share = ExactRatio.One, Part = QuantityPart.Shortfall(new(1, 1)) }, "80").Amount.Amount.ToString(), "45");
        // The source's 80 is already held and physically scaled. Only the
        // retained band levels use the held base; share and price apply once.
        Equal(Bill(entry with { Part = QuantityPart.Band(new(3, 20), new(7, 20)), HeldBaseQuantity = new(200, 1) }, "80").Amount.Amount.ToString(), "36");
        Equal(Bill(entry, "0").Amount.Amount.ToString(), "0");
        Reject(() => Bill(entry with { From = terms.Day.Day.AddDays(1) }, "80"));
        Reject(() => Bill(entry with { To = terms.Day.Day }, "80"));
        Reject(() => Bill(entry with { HeldBaseQuantity = SignedRatio.Zero }, "80"));
        var fractional = entry with { Share = new(1, 3) };
        terms = terms with { Price = new(ExactDecimal.Parse("3.015"), terms.Price.Unit) };
        var first = Bill(fractional, "1");
        Equal(first.ExactAmount.ToExactString(), "1.005");
        Equal(first.Amount.Amount.ToString(), "1");
        terms = terms with { Price = new(ExactDecimal.Parse("3.045"), terms.Price.Unit) };
        var next = Bill(fractional, "1", first.Amount);
        Equal(next.Amount.Amount.ToString(), "1.02");
        Equal(next.Contribution.Amount.ToString(), "0.02");
        terms = terms with { Price = new(ExactDecimal.Parse("2.5"), new PriceUnit(terms.Price.Unit.Currency, terms.Price.Unit.Quantity, ExactDecimal.Parse("0.1"))) };
        Equal(Bill(entry with { Share = new(1, 2) }, "8").Amount.Amount.ToString(), "1");
        Console.WriteLine("PASS entry billing: exact share, daily band/shortfall, held base, own dates, quote multiplier and one final half-even rounding");
    }
    static void Equal(string actual, string expected)
    { if (actual != expected) throw new Exception($"Expected {expected}, got {actual}"); }
    static void Reject(Action action)
    { try { action(); } catch (MarketDataException) { return; } throw new Exception("Expected gas billing refusal"); }
}
