using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;

internal static class FuturesOptionChecks
{
    public static void Run()
    {
        Check("lattice functions match the independent oracle to one unit in the 30th digit", LatticeFunctionsMatchOracle);
        Check("lattice domain and range errors are structured", LatticeErrors);
        Check("lattice converts to exact decimals with half-even rounding", LatticeExactConversion);
        Check("lattice arithmetic rounds half-even once per operation", LatticeArithmetic);
    }

    private static void LatticeFunctionsMatchOracle()
    {
        var oracle = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "futures-option-oracle.json")))!;
        var count = 0;
        foreach (var (name, function) in new (string, Func<LatticeDecimal, LatticeDecimal>)[]
                 { ("exp", v => v.Exp()), ("ln", v => v.Ln()), ("sqrt", v => v.Sqrt()) })
            foreach (var row in oracle[name]!.AsArray())
            {
                var x = row!["x"]!.GetValue<string>();
                var expected = LatticeDecimal.Parse(row["y"]!.GetValue<string>());
                var actual = function(LatticeDecimal.Parse(x));
                var difference = (actual - expected).ToString();
                if (difference is not ("0" or "0.000000000000000000000000000001" or "-0.000000000000000000000000000001"))
                    throw new Exception($"{name}({x}) expected {expected}; got {actual}.");
                count++;
            }
        if (count < 40) throw new Exception($"Oracle must hold at least 40 rows; found {count}.");
    }

    private static void LatticeErrors()
    {
        Reject("lattice_domain", () => _ = LatticeDecimal.Parse("0").Ln());
        Reject("lattice_domain", () => _ = LatticeDecimal.Parse("-1").Sqrt());
        Reject("lattice_domain", () => _ = LatticeDecimal.Parse("1") / LatticeDecimal.Parse("0"));
        Reject("lattice_domain", () => _ = LatticeDecimal.Parse("2").Pow(-1));
        Reject("numeric_range", () => _ = LatticeDecimal.Parse("100").Exp());
        Reject("numeric_range", () => _ = LatticeDecimal.Parse("1000000000000").Pow(2));
        Reject("numeric_format", () => _ = LatticeDecimal.Parse("1e5"));
        Equal(LatticeDecimal.Parse("-100").Exp().ToString(), "0");
    }

    private static void LatticeExactConversion()
    {
        var value = ExactDecimal.Parse("1.25");
        if (LatticeDecimal.From(value).ToExact() != value) throw new Exception("1.25 must round-trip.");
        var third = LatticeDecimal.Parse("1") / LatticeDecimal.Parse("3");
        Equal(third.ToString(), "0.333333333333333333333333333333");
        Equal(third.ToExact().ToString(), "0.333333333333333333");
        Equal(LatticeDecimal.Parse("0.000000000000000000500000000000").ToExact().ToString(), "0");
        Equal(LatticeDecimal.Parse("0.000000000000000001500000000000").ToExact().ToString(), "0.000000000000000002");
        Equal(LatticeDecimal.Parse("-0.000000000000000002500000000000").ToExact().ToString(), "-0.000000000000000002");
    }

    private static void LatticeArithmetic()
    {
        var tiny = LatticeDecimal.Parse("0.000000000000000000000000000001");
        var half = LatticeDecimal.Parse("0.5");
        Equal((tiny * half).ToString(), "0");                         // 0.5 ulp ties to even (0)
        Equal((tiny * LatticeDecimal.Parse("1.5")).ToString(), "0.000000000000000000000000000002"); // 1.5 ulp ties to even (2)
        Equal((LatticeDecimal.Parse("2") / LatticeDecimal.Parse("3")).ToString(), "0.666666666666666666666666666667");
        Equal(LatticeDecimal.Parse("1.1").Pow(3).ToString(), "1.331");
        Equal(LatticeDecimal.Parse("7").Pow(0).ToString(), "1");
        Equal(LatticeDecimal.Max(LatticeDecimal.Parse("-1"), LatticeDecimal.Parse("0.5")).ToString(), "0.5");
        Equal((LatticeDecimal.Parse("1") - LatticeDecimal.Parse("1.5")).ToString(), "-0.5");
    }

    private static void Check(string label, Action run) { run(); Console.WriteLine($"PASS {label}"); }
    private static void Equal(string actual, string expected)
    { if (actual != expected) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Reject(string code, Action action)
    { try { action(); } catch (MarketDataException error) when (error.Code == code) { return; } throw new Exception($"Expected {code}."); }
}
