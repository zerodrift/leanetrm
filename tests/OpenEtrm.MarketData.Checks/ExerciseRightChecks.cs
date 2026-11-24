using System.Numerics;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.Shared.Numerics;

internal static class ExerciseRightChecks
{
    private static readonly LatticeDecimal ThirtyDays = L("30") / L("365");

    public static void Run()
    {
        Check("undiscounted holder values match the synthetic reference cases", ReferenceValues);
        Check("expiry returns exact nonnegative intrinsic value", ExpiryValues);
        Check("two stochastic legs with a fee preserve the installed spread call", MatchesSpreadCall);
        Check("the 30th input digit can change half-even rounding at the call output", CallRounding);
        Check("unrepresentable call inputs and exact results raise their contracted codes", ExactBoundaries);
        Check("malformed inputs are refused even if the payoff is determined", RejectsMalformedInputs);
    }

    private static void ReferenceValues()
    {
        var caseA = Both("0");
        var caseB = Both("-1000");
        var caseC = ReceivesOnly("-30000");
        var caseD = ReceivesOnly("0");
        var caseE = new HolderChoiceInputs(R("0"), R("30000"), R("-1000"), null, L("0.40"), null, ThirtyDays);
        var caseF = Both("1000");
        var caseG = new HolderChoiceInputs(R("0"), R("32000"), R("30000"), null, L("0.50"), null, ThirtyDays);
        var caseH = ReceivesOnly("1000");
        var caseI = new HolderChoiceInputs(R("30000"), R("32000"), R("1000"), L("0.40"), L("0.50"), L("0.90"), ThirtyDays);
        var caseJ = ReceivesOnly("-35000");
        var caseK = ReceivesOnly("-900000") with { Receives = R("960000") };

        Near("a", caseA, "2163.3174");
        Near("b", caseB, "1412.5473");
        Near("c", caseC, "2947.4419");
        Equal("d", E("32000"), HolderChoice.Value(caseD));
        Equal("e", E("0"), HolderChoice.Value(caseE));
        Near("f", caseF, "3050.4865");
        Near("g", caseG, "947.4419");
        Equal("h", E("33000"), HolderChoice.Value(caseH));
        Equal("i, exact parity with b", HolderChoice.Value(caseB) - E("1000"), HolderChoice.Value(caseI));
        Near("j", caseJ, "775.4967");
        Near("k", caseK, "88423.2569");
    }

    private static void ExpiryValues()
    {
        Equal("c at expiry", E("2000"), HolderChoice.Value(ReceivesOnly("-30000") with { Years = default }));
        var caseG = new HolderChoiceInputs(R("0"), R("32000"), R("30000"), null, L("0.50"), null, default);
        Equal("g at expiry", E("0"), HolderChoice.Value(caseG));

        var fractional = Both("0") with { Receives = new(7, 3), Pays = new(1, 3), Years = default };
        Equal("exact netting at expiry", E("2"), HolderChoice.Value(fractional));
    }

    private static void MatchesSpreadCall()
    {
        var inputs = new HolderChoiceInputs(R("50"), R("32"), R("-5"), L("0.50"), L("0.40"), L("0.30"), L("0.5"));
        var actual = HolderChoice.Value(inputs);
        var installed = KirkSpreadCall.UnitPrice(new(L("50"), L("32"), L("5"), L("0.50"), L("0.40"), L("0.30"),
            L("0.5"), L("1"))).ToExact();
        Equal("installed spread call", installed, actual);
        var rounded = actual.ToString(new AmountFormat(6, CashTieRule.ToEven, false, false));
        if (rounded != "14.783218")
            throw new Exception($"The installed spread call to six places is {rounded}; expected 14.783218.");
    }

    private static void CallRounding()
    {
        var midpoint = ReceivesOnly("-1") with
        {
            Receives = R("1.0000000000000000005"),
            ReceivesVolatility = L("0"),
        };
        var above = midpoint with { Receives = R("1.000000000000000000500000000001") };
        Equal("half-even midpoint", E("0"), HolderChoice.Value(midpoint));
        Equal("one 30th-place unit above midpoint", E("0.000000000000000001"), HolderChoice.Value(above));
    }

    private static void ExactBoundaries()
    {
        var fractional = ReceivesOnly("0") with { Receives = new(1, 3) };
        Refuses("precision_loss", fractional);
        Refuses("holder_choice_inputs", fractional with { Certain = R("-1") });
        Refuses("holder_choice_inputs", Both("0") with { Certain = new(-1, 3) });
        Refuses("holder_choice_inputs", ReceivesOnly("-1") with { Receives = new(BigInteger.Pow(10, 24), 1) });
        Refuses("holder_choice_inputs", Both("0") with { Pays = new(BigInteger.Pow(10, 60), 1) });
    }

    private static void RejectsMalformedInputs()
    {
        var both = Both("-1000");
        var receives = ReceivesOnly("0");
        foreach (var inputs in new[]
        {
            both with { Receives = R("-1") },
            both with { Pays = R("-1") },
            receives with { PaysVolatility = L("0") },
            both with { ReceivesVolatility = null },
            both with { PaysVolatility = null },
            both with { Correlation = null },
            receives with { Correlation = L("0") },
            both with { Years = L("-1") },
            receives with { ReceivesVolatility = L("-0.01") },
            both with { PaysVolatility = L("-0.01"), Years = default },
            both with { Correlation = L("1.01"), Years = default },
            both with { Correlation = L("-1.01") },
            both with { Certain = default },
            null!,
        })
            Refuses("holder_choice_inputs", inputs);
    }

    private static void Refuses(string code, HolderChoiceInputs inputs)
    {
        try
        {
            _ = HolderChoice.Value(inputs);
            throw new Exception($"Inputs {inputs} must be refused with {code}.");
        }
        catch (MarketDataException e) when (e.Code == code) { }
    }

    private static HolderChoiceInputs Both(string certain) =>
        new(R("32000"), R("30000"), R(certain), L("0.50"), L("0.40"), L("0.90"), ThirtyDays);

    private static HolderChoiceInputs ReceivesOnly(string certain) =>
        new(R("32000"), R("0"), R(certain), L("0.50"), null, null, ThirtyDays);

    private static void Near(string label, HolderChoiceInputs inputs, string reference)
    {
        var expected = E(reference);
        var actual = HolderChoice.Value(inputs);
        var tolerance = E("0.0001");
        if (actual.CompareTo(expected - tolerance) < 0 || actual.CompareTo(expected + tolerance) > 0)
            throw new Exception($"Case {label}: expected {expected} within {tolerance}, got {actual}.");
    }

    private static void Equal(string label, ExactDecimal expected, ExactDecimal actual)
    {
        if (expected != actual)
            throw new Exception($"Case {label}: expected exactly {expected}, got {actual}.");
    }

    private static SignedRatio R(string value) => SignedRatio.TryParse(value, out var ratio)
        ? ratio
        : throw new ArgumentException("Expected a plain decimal.", nameof(value));

    private static LatticeDecimal L(string value) => LatticeDecimal.Parse(value);
    private static ExactDecimal E(string value) => ExactDecimal.Parse(value);

    private static void Check(string label, Action run) { run(); Console.WriteLine($"PASS exercise right: {label}"); }
}
