using System.Globalization;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;

internal static class SpreadOptionChecks
{
    public static void Run()
    {
        Check("a spread call equals the independent 60-digit evaluation within one unit in the 24th digit", MatchesOracle);
        Check("a spread call stays within 0.1% of the long price of the independent conditional integral", NearConditionalIntegral);
        Check("a spread call on a certain short price equals Black-76 at the combined strike", CertainShortIsBlack76);
        Check("a spread call with no remaining variance is the discounted intrinsic spread", NoVarianceIsIntrinsic);
        Check("a spread call rises with the long price and falls with the short price, the strike and correlation", Monotonic);
        Check("a spread call refuses inputs outside its lognormal contract", RejectsUnsupportedInputs);
    }

    private static JsonArray Calls() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "spread-option-oracle.json")))!["calls"]!.AsArray();
    private static string Text(JsonNode row, string name) => row[name]!.GetValue<string>();
    private static LatticeDecimal D(string value) => LatticeDecimal.Parse(value);
    private static SpreadCallInputs Inputs(JsonNode row) => new(D(Text(row, "long")), D(Text(row, "short")), D(Text(row, "strike")),
        D(Text(row, "long_volatility")), D(Text(row, "short_volatility")), D(Text(row, "correlation")), D(Text(row, "years")),
        D(Text(row, "discount")));
    private static SpreadCallInputs Base() => new(D("45"), D("28"), D("3"), D("0.6"), D("0.45"), D("0.8"), D("0.25"), D("0.99"));
    private static decimal Number(LatticeDecimal value) => decimal.Parse(value.ToExact().ToString(), CultureInfo.InvariantCulture);

    private static void MatchesOracle()
    {
        var count = 0;
        foreach (var row in Calls())
        {
            var difference = KirkSpreadCall.UnitPrice(Inputs(row!)) - D(Text(row!, "kirk"));
            if (D("0").CompareTo(difference) > 0) difference = D("0") - difference;
            if (difference.CompareTo(D("0.000000000000000000000001")) > 0)
                throw new Exception($"Spread call {row!.ToJsonString()} differs from the oracle by {difference}.");
            count++;
        }
        if (count < 10) throw new Exception($"The oracle must hold at least 10 spread calls; found {count}.");
    }

    private static void NearConditionalIntegral()
    {
        foreach (var row in Calls())
        {
            var value = Number(KirkSpreadCall.UnitPrice(Inputs(row!)));
            var exact = decimal.Parse(Text(row!, "integral"), CultureInfo.InvariantCulture);
            var bound = decimal.Parse(Text(row!, "long"), CultureInfo.InvariantCulture) / 1000m;
            if (Math.Abs(value - exact) > bound)
                throw new Exception($"Spread call {row!.ToJsonString()} is {value}; the conditional integral is {exact}.");
        }
    }

    private static void CertainShortIsBlack76()
    {
        var rows = Calls().Where(row => row!["black76"] is not null).ToArray();
        if (rows.Length == 0) throw new Exception("The oracle must hold a certain-short case.");
        foreach (var row in rows)
            if (Math.Abs(Number(KirkSpreadCall.UnitPrice(Inputs(row!))) - Number(D(Text(row!, "black76")))) > 0.000000000001m)
                throw new Exception("A certain short price must reduce the spread call to Black-76.");
    }

    private static void NoVarianceIsIntrinsic()
    {
        var still = Base() with { LongVolatility = D("0"), ShortVolatility = D("0") };
        if (KirkSpreadCall.UnitPrice(still) != D("13.86")) throw new Exception("In the money: 0.99 × (45 − 28 − 3).");
        if (KirkSpreadCall.UnitPrice(still with { Long = D("30") }) != D("0")) throw new Exception("Out of the money: zero.");
        // Equal volatilities at full correlation and a zero strike leave the price ratio certain.
        var locked = Base() with { Strike = D("0"), ShortVolatility = D("0.6"), Correlation = D("1") };
        if (KirkSpreadCall.UnitPrice(locked) != D("16.83")) throw new Exception("A certain ratio: 0.99 × (45 − 28).");
    }

    private static void Monotonic()
    {
        var value = KirkSpreadCall.UnitPrice(Base());
        void Less(SpreadCallInputs lower, string label)
        {
            if (KirkSpreadCall.UnitPrice(lower).CompareTo(value) >= 0) throw new Exception($"The call must fall with {label}.");
        }
        Less(Base() with { Long = D("44") }, "a lower long price");
        Less(Base() with { Short = D("29") }, "a higher short price");
        Less(Base() with { Strike = D("4") }, "a higher strike");
        Less(Base() with { Correlation = D("0.9") }, "a higher correlation");
        Less(Base() with { Years = D("0.1") }, "less time");
        var floor = D("0.99") * (D("45") - D("28") - D("3"));
        if (value.CompareTo(floor) <= 0 || value.CompareTo(D("0.99") * D("45")) >= 0)
            throw new Exception("The call lies above discounted intrinsic and below the discounted long price.");
    }

    private static void RejectsUnsupportedInputs()
    {
        foreach (var inputs in new[]
        {
            Base() with { Long = D("0") }, Base() with { Long = D("-5") }, Base() with { Short = D("0") },
            Base() with { Strike = D("-28") }, Base() with { Strike = D("-30") }, Base() with { LongVolatility = D("-0.1") },
            Base() with { ShortVolatility = D("-0.1") }, Base() with { Correlation = D("1.01") },
            Base() with { Correlation = D("-1.01") }, Base() with { Years = D("0") }, Base() with { Years = D("-1") },
            Base() with { Discount = D("0") }, Base() with { Discount = D("-0.5") },
        })
            try
            {
                _ = KirkSpreadCall.UnitPrice(inputs);
                throw new Exception($"Inputs {inputs} must be refused.");
            }
            catch (MarketDataException e) when (e.Code == "spread_option_inputs") { }
        try { _ = KirkSpreadCall.UnitPrice(null!); throw new Exception("Missing inputs must be refused."); }
        catch (MarketDataException e) when (e.Code == "spread_option_inputs") { }
    }

    private static void Check(string label, Action run) { run(); Console.WriteLine($"PASS spread option: {label}"); }
}
