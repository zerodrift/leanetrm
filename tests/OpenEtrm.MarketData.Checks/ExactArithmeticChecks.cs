using System.Numerics;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.Shared.Numerics;

internal static class ExactArithmeticChecks
{
    public static int Run()
    {
        var revision = new ReferenceRevision(Guid.Parse("00000000-0000-0000-0000-000000000011"), Guid.Parse("00000000-0000-0000-0000-000000000012"));
        ReferenceKey Key(string code) => new(revision, code);
        var currency = Key("currency"); var quantity = Key("quantity");
        var unit = new PriceUnit(currency, quantity, ExactDecimal.Parse("1"));
        var thirdCash = ExactMoney.FromFraction(1, 3, currency);
        var thirdQuantity = new ExactQuantity(1, 3, quantity);
        var thirdPrice = ExactUnitPrice.FromFraction(1, 3, unit);
        Equal(thirdCash.Add(ExactMoney.FromFraction(2, 3, currency)).ToMoney().Amount.ToString(), "1");
        Equal(thirdQuantity.Add(new(2, 3, quantity)).ToQuantity().Amount.ToString(), "1");
        Equal(thirdPrice.Add(ExactUnitPrice.FromFraction(2, 3, unit)).ToExactString(), "1");
        Equal(thirdPrice.Subtract(ExactUnitPrice.FromFraction(1, 2, unit)).ToExactString(), "-1/6");
        Equal(thirdCash.Scale(ExactDecimal.Parse("-3")).ToExactString(), "-1");
        Equal(thirdQuantity.Scale(ExactDecimal.Parse("-3")).ToQuantity().Amount.ToString(), "-1");
        Reject("precision_loss", () => thirdCash.ToMoney());
        Reject("precision_loss", () => thirdQuantity.ToQuantity());
        Reject("precision_loss", () => thirdPrice.ToUnitPriceExact());
        Reject("currency_mismatch", () => thirdCash.Add(ExactMoney.FromFraction(1, 3, Key("other"))));
        Reject("quantity_mismatch", () => thirdQuantity.Add(new(1, 3, Key("other"))));
        Reject("unit_mismatch", () => thirdPrice.Add(ExactUnitPrice.FromFraction(1, 3, new PriceUnit(currency, Key("other"), ExactDecimal.Parse("1")))));
        var wide = BigInteger.One << 4096;
        // Unbounded cash and ordinary price intermediates are intentional; quantity and compounded prices remain bounded.
        Equal(ExactMoney.FromFraction(wide, 1, currency).Subtract(ExactMoney.FromFraction(wide - 1, 1, currency)).ToExactString(), "1");
        Equal(ExactUnitPrice.FromFraction(wide, 1, unit).Subtract(ExactUnitPrice.FromFraction(wide - 1, 1, unit)).ToExactString(), "1");
        Reject("numeric_range", () => new ExactQuantity(wide, 1, quantity));
        Reject("numeric_range", () => new ExactQuantity(wide - 1, 1, quantity).Scale(ExactDecimal.Parse("2")));
        Reject("numeric_range", () => ExactUnitPrice.FromFraction(wide - 1, 1, unit).ScaleFraction(2, 1));
        Equal(new ExactQuantity(wide, wide, quantity).ToQuantity().Amount.ToString(), "1");
        Reject("numeric_division", () => thirdQuantity.ScaleFraction(1, 0));
        Reject("numeric_division", () => thirdPrice.ScaleFraction(1, 0));
        // Value equality and hash collections must keep normalized fractions interchangeable.
        if (!new HashSet<ExactMoney> { thirdCash }.Contains(ExactMoney.FromFraction(2, 6, currency)) ||
            !new HashSet<ExactQuantity> { thirdQuantity }.Contains(new(2, 6, quantity)) ||
            !new HashSet<ExactUnitPrice> { thirdPrice }.Contains(ExactUnitPrice.FromFraction(2, 6, unit)))
            throw new Exception("Reduced fractions must retain typed value equality and compatible hashes.");
        var rounding = new AmountFormat(2, CashTieRule.ToEven, false, false);
        Equal(ExactMoney.FromFraction(2001, 200, currency).ToMoney(rounding).Amount.ToString(), "10");
        Equal(ExactMoney.FromFraction(2003, 200, currency).ToMoney(rounding).Amount.ToString(), "10.02");
        // Public JSON does not encode CLR namespace or the newly shared private rational payload.
        Equal(JsonSerializer.Serialize(thirdPrice), JsonSerializer.Serialize(new { Unit = unit, Numerator = "1", Denominator = "3" }));
        Equal(JsonSerializer.Serialize(thirdCash), JsonSerializer.Serialize(new { Currency = currency, Numerator = "1", Denominator = "3" }));
        Equal(JsonSerializer.Serialize(thirdQuantity), JsonSerializer.Serialize(new { Specification = quantity, Numerator = "1", Denominator = "3" }));
        Coherence(revision);
        Console.WriteLine("PASS shared exact amount mechanics preserve typed references, bounds, precision and wire shape");
        return 1;
    }

    private static void Coherence(ReferenceRevision revision)
    {
        ReferenceKey Key(string code) => new(revision, code);
        var energy = new QuantitySpecification(Key("energy_quantity"), Key("commodity"), Key("basis"), Key("megawatt_hour"), Key("energy"));
        var foreign = energy.Dimension with { Revision = new(revision.CatalogId, Guid.NewGuid()) };
        var clock = PowerClock.Capture(TimeZoneInfo.Utc);
        var from = new DateOnly(2026, 1, 1); var to = from.AddDays(1);
        var periods = PowerHours.Compile(from, to, clock, TimeOfUse.All(60), [], 60);
        var rows = periods.Select(p => new PowerIntervalQuantity(p.Start, p.End, new(ExactDecimal.Parse("1"), energy.Key), null)).ToArray();
        foreach (var invalid in new[] { energy with { Dimension = foreign }, energy with { Dimension = Key("") } })
        {
            Reject("power_quantity", () => PowerQuantitySchedule.Compile(new(ExactDecimal.Parse("1"), energy.Key), PowerQuantityBasis.TotalEnergy,
                invalid, invalid, from, to, clock, TimeOfUse.All(60), [], 60, PowerAllocation.FlatRate));
            Reject("power_quantity", () => PowerIntervalQuantities.Compile(periods, rows, invalid, invalid));
        }
    }
    private static void Equal(string actual, string expected)
    { if (actual != expected) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Reject(string code, Action action)
    { try { action(); } catch (MarketDataException error) when (error.Code == code) { return; } throw new Exception($"Expected {code}."); }
}
