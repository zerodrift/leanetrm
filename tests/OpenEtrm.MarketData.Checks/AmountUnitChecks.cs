using System.Collections.Immutable;
using OpenEtrm.MarketData;

internal static class AmountUnitChecks
{
    public static int Run()
    {
        var passed = 0;
        void Check(string name, Action test) { test(); passed++; Console.WriteLine("PASS amounts and units: " + name); }
        void Equal(string actual, string expected) { if (actual != expected) throw new Exception($"Expected {expected}; got {actual}."); }
        void Reject(string code, Action test)
        {
            try { test(); } catch (MarketDataException e) when (e.Code == code) { return; }
            throw new Exception("Expected " + code);
        }
        ExactDecimal D(string value) => ExactDecimal.Parse(value);
        var revision = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
        ReferenceKey R(string code) => new(revision, code);
        var money = new AmountFormat(2, CashTieRule.ToEven, true, false);
        var precise = new AmountFormat(6, CashTieRule.ToEven, false, false);
        var units = ImmutableArray.Create(
            new UnitDefinition(R("mwh"), R("energy"), "MWh"), new UnitDefinition(R("kwh"), R("energy"), "kWh"),
            new UnitDefinition(R("gj"), R("energy"), "GJ"), new UnitDefinition(R("barrel"), R("volume"), "bbl"),
            new UnitDefinition(R("gallon"), R("volume"), "US gal"), new UnitDefinition(R("kg"), R("mass"), "kg"),
            new UnitDefinition(R("tonne"), R("mass"), "t"), new UnitDefinition(R("mw"), R("power"), "MW"));
        QuantitySpecification Q(string code, string unit, string dimension, string commodity = "energy_product", string basis = "delivered") =>
            new(R(code), R(commodity), R(basis), R(unit), R(dimension));
        var quantities = ImmutableArray.Create(Q("q_mwh", "mwh", "energy"), Q("q_kwh", "kwh", "energy"), Q("q_gj", "gj", "energy"),
            Q("other_commodity", "mwh", "energy", "other_product"), Q("other_basis", "mwh", "energy", basis: "other_basis"),
            Q("q_barrel", "barrel", "volume", "liquid", "observed_volume"), Q("q_gallon", "gallon", "volume", "liquid", "observed_volume"),
            Q("q_tonne", "tonne", "mass", "bulk", "net_mass"), Q("q_kg", "kg", "mass", "bulk", "net_mass"), Q("q_mw", "mw", "power"));
        var conversions = ImmutableArray.Create(new UnitConversion(R("mwh"), R("kwh"), D("1000"), D("1")),
            new UnitConversion(R("mwh"), R("gj"), D("18"), D("5")), new UnitConversion(R("barrel"), R("gallon"), D("42"), D("1")),
            new UnitConversion(R("tonne"), R("kg"), D("1000"), D("1")));
        var converter = new UnitConverter(units, conversions, quantities);
        Quantity A(string value, string code) => new(D(value), R(code));
        Check("money formatting uses decimal ties and grouping without changing its amount", () =>
        {
            var value = new Money(D("12345678901234567890.125"), R("usd"));
            Equal(value.ToString(money, new(R("usd"), "USD")), "12,345,678,901,234,567,890.12 USD");
            Equal(value.Amount.ToString(), "12345678901234567890.125");
            Equal(new Money(D("-0.005"), R("usd")).ToString(money, new(R("usd"), "USD")), "0.00 USD");
            Equal(D("-1.235").ToString(money), "-1.24");
            Reject("display_reference", () => value.ToString(money, new(R("eur"), "EUR")));
        });
        Check("quantity and price text carry their resolved unit labels", () =>
        {
            Equal(A("1200.1234567", "q_mwh").ToString(precise, new(R("q_mwh"), "MWh")), "1200.123457 MWh");
            var price = new UnitPrice(D("3.1"), new PriceUnit(R("usd"), R("q_mwh"), D("1")));
            Equal(price.ToString(new(3, CashTieRule.ToEven, false, false), new(R("usd"), "USD"), new(R("q_mwh"), "MWh")), "3.100 USD/MWh");
            Reject("display_reference", () => price.ToString(precise, new(R("usd"), "USD"), new(R("q_kwh"), "kWh")));
        });
        Check("display labels count Unicode scalars and preserve accepted symbols exactly", () =>
        {
            var eightyAscii = new string('u', 80);
            var eightySupplementary = string.Concat(Enumerable.Repeat("\U0001F50C", 80));
            foreach (var symbol in new[] { "MWh", "m\u00B3", "\u00B0C", " MWh ", eightyAscii, eightySupplementary })
            {
                var label = new AmountLabel(R("q_mwh"), symbol);
                Equal(A("1", "q_mwh").ToString(precise, label), "1.000000 " + symbol);
                var catalog = new UnitConverter([new(R("mwh"), R("energy"), symbol)], [], [Q("q_mwh", "mwh", "energy")]);
                Equal(catalog.Symbol(R("q_mwh")), symbol);
            }
            Reject("display_reference", () => new AmountLabel(R("q_mwh"), eightyAscii + "u"));
            Reject("display_reference", () => new AmountLabel(R("q_mwh"), eightySupplementary + "\U0001F50C"));
        });
        Check("unit catalogs reject symbols that cannot label an amount", () =>
        {
            var invalid = new[] { null!, "", " ", "\u00A0\u1680\u2000\u200A\u2028\u2029\u202F\u205F\u3000", "M\u0001Wh", "M\u007FWh", "M\u0085Wh",
                "\uD800", "\uDC00", "x\uD800y", new string('u', 81), string.Concat(Enumerable.Repeat("\U0001F50C", 81)) };
            foreach (var symbol in invalid)
            {
                Reject("display_reference", () => new AmountLabel(R("q_mwh"), symbol));
                Reject("unit_conversion", () => new UnitConverter([new(R("mwh"), R("energy"), symbol)], [], [Q("q_mwh", "mwh", "energy")]));
            }
        });
        Check("tick precision counts required decimal places rather than tick multiples", () =>
        {
            if (AmountFormat.ForTick(D("0.025")).DecimalPlaces != 3 || AmountFormat.ForTick(D("0.0005")).DecimalPlaces != 4 || AmountFormat.ForTick(D("5")).DecimalPlaces != 0) throw new Exception("Wrong tick display scale.");
            Equal(D("3.1").ToString(AmountFormat.ForTick(D("0.025"))), "3.100");
            Reject("display_precision", () => AmountFormat.ForTick(default));
            Reject("display_precision", () => new AmountFormat(19, CashTieRule.ToEven, true, false));
        });
        Check("quantity conversion supports exact scaling, inverse and chained conversions", () =>
        {
            var source = A("2.5", "q_mwh");
            var target = source.ConvertTo(R("q_kwh"), converter);
            Equal(target.Amount.ToString(), "2500");
            Equal(target.ConvertTo(source.Specification, converter).Amount.ToString(), "2.5");
            Equal(A("1000", "q_kwh").ConvertTo(R("q_gj"), converter).Amount.ToString(), "3.6");
            Equal(A("-2", "q_barrel").ConvertTo(R("q_gallon"), converter).Amount.ToString(), "-84");
            Equal(A("1.25", "q_tonne").ConvertTo(R("q_kg"), converter).Amount.ToString(), "1250");
            if (source.ConvertTo(source.Specification, converter).Specification != source.Specification) throw new Exception("Identity conversion changed reference.");
        });
        Check("fractional conversion requires explicit rounding and rounds only the final result", () =>
        {
            Reject("precision_loss", () => A("1", "q_gj").ConvertTo(R("q_mwh"), converter));
            Equal(A("1", "q_gj").ConvertTo(R("q_mwh"), converter, precise).Amount.ToString(), "0.277778");
            Equal(A("-1", "q_gj").ConvertTo(R("q_mwh"), converter, precise).Amount.ToString(), "-0.277778");
            Equal(A("0", "q_gj").ConvertTo(R("q_mwh"), converter).Amount.ToString(), "0");
        });
        Check("price conversion preserves price times quantity money", () =>
        {
            var price = new UnitPrice(D("40"), new PriceUnit(R("usd"), R("q_mwh"), D("1")));
            var converted = price.ConvertTo(R("q_kwh"), converter);
            Equal(converted.Value.ToString(), "0.04");
            Equal(price.Times(A("2", "q_mwh")).Amount.ToString(), converted.Times(A("2000", "q_kwh")).Amount.ToString());
            Equal(converted.ConvertTo(R("q_mwh"), converter).Value.ToString(), "40");
        });
        Check("price times quantity enforces precision only after all unit factors", () =>
        {
            var tiny = new UnitPrice(D("0.000000000000000001"), new PriceUnit(R("usd"), R("q_mwh"), D("0.01")));
            Equal(tiny.Times(A("100", "q_mwh")).Amount.ToString(), "0.000000000000000001");
            var large = new UnitPrice(D("100000000000000000000000"), new PriceUnit(R("usd"), R("q_mwh"), D("100")));
            Equal(large.Times(A("0.01", "q_mwh")).Amount.ToString(), "100000000000000000000000");
        });
        Check("same dimension never erases commodity, basis, revision or duration requirements", () =>
        {
            Reject("quantity_conversion", () => A("1", "q_mwh").ConvertTo(R("other_commodity"), converter));
            Reject("quantity_conversion", () => A("1", "q_mwh").ConvertTo(R("other_basis"), converter));
            Reject("quantity_conversion", () => A("1", "q_mw").ConvertTo(R("q_mwh"), converter));
            Reject("quantity_conversion", () => A("1", "q_barrel").ConvertTo(R("q_mwh"), converter));
            Reject("missing_reference", () => A("1", "q_mwh").ConvertTo(new(new(Guid.NewGuid(), Guid.NewGuid()), "q_kwh"), converter));
            Reject("missing_reference", () => A("1", "unknown").ConvertTo(R("q_kwh"), converter));
        });
        Check("contradictory conversion cycles and cross-dimension definitions are rejected", () =>
        {
            Reject("unit_conversion", () => new UnitConverter(units, conversions.Add(new(R("kwh"), R("gj"), D("1"), D("1"))), quantities));
            Reject("unit_conversion", () => new UnitConverter(units, conversions.Add(new(R("mwh"), R("mw"), D("1"), D("1"))), quantities));
            Reject("unit_conversion", () => new UnitConverter(units, conversions.Add(new(R("mwh"), R("gj"), D("0"), D("1"))), quantities));
        });
        Check("rational display and signed half ties have explicit deterministic rules", () =>
        {
            Equal(precise.FormatFraction(1, 3), "0.333333");
            Equal(money.FormatFraction(-245, 200), "-1.22");
            Equal(new AmountFormat(2, CashTieRule.AwayFromZero, false, false).FormatFraction(-245, 200), "-1.23");
            Equal(new AmountFormat(4, CashTieRule.ToEven, false, true).FormatFraction(1, 2), "0.5");
            Reject("numeric_division", () => precise.FormatFraction(1, 0));
            var exact = ExactMoney.FromMoney(new Money(D("1"), R("usd")));
            Equal(exact.ToString(money, new(R("usd"), "USD")), "1.00 USD");
        });
        Check("scaled quote prices convert inversely without changing quote scale or cash", () =>
        {
            var price = new UnitPrice(D("4000"), new PriceUnit(R("usd"), R("q_mwh"), D("0.01")));
            var converted = price.ConvertTo(R("q_kwh"), converter);
            Equal(converted.Value.ToString(), "4");
            Equal(converted.Unit.Multiplier.ToString(), "0.01");
            Equal(converted.Times(A("2000", "q_kwh")).Amount.ToString(), "80");
            Reject("numeric_range", () => A("100000000000000000000000", "q_mwh").ConvertTo(R("q_kwh"), converter));
            var isolated = new UnitConverter(units, [], quantities);
            Reject("quantity_conversion", () => A("1", "q_mwh").ConvertTo(R("q_kwh"), isolated));
            Reject("unit_conversion", () => new UnitConverter(units.Add(null!), conversions, quantities));
            Reject("unit_conversion", () => new UnitConverter(units, conversions.Add(null!), quantities));
            Reject("unit_conversion", () => new UnitConverter(units, conversions, quantities.Add(null!)));
        });
        Check("catalog precision bounds are deterministic under input ordering", () =>
        {
            var longUnits = Enumerable.Range(0, 61).Select(n => new UnitDefinition(R($"unit_{n:D3}"), R("energy"), $"U{n}")).ToImmutableArray();
            var links = Enumerable.Range(0, 60).Select(n => new UnitConversion(longUnits[n].Key, longUnits[n+1].Key, D("100000000000000000000000"), D("1"))).ToImmutableArray();
            Reject("unit_conversion", () => new UnitConverter(longUnits, links, []));
            var reordered = longUnits.Skip(30).Concat(longUnits.Take(30)).ToImmutableArray();
            Reject("unit_conversion", () => new UnitConverter(reordered, links.Reverse().ToImmutableArray(), []));
        });
        return passed;
    }
}
