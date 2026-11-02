using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;

internal static class VolSurfaceChecks
{
    public static void Run()
    {
        Check("quadratic smile recovers known parameters", RecoversKnownParameters);
        Check("rich cheap equals a deliberate offset", DeliberateOffset);
        Check("fewer than five strikes is a gap", FewStrikes);
        Check("one-sided strikes is a gap", OneSidedStrikes);
        Check("nonpositive smile is a gap", NonpositiveSmile);
        Check("fit is deterministic", Deterministic);
        Check("unknown vol model is refused", UnknownModel);
    }

    private static readonly string[] Moneyness = ["-0.3", "-0.2", "-0.1", "0", "0.1", "0.2", "0.3"];
    private static readonly IVolModel Model = VolModels.Require(VolModels.QuadraticLogMoneyness);
    private static readonly LatticeDecimal Tolerance25 = L("0.0000000000000000000000001");
    private static readonly LatticeDecimal Tolerance20 = L("0.00000000000000000001");

    private static LatticeDecimal L(string value) => LatticeDecimal.Parse(value);
    private static LatticeDecimal Abs(LatticeDecimal value) => LatticeDecimal.Max(value, L("0") - value);

    private static ImmutableArray<SmilePoint> Quadratic(string a, string b, string c, string[] ks) =>
        [.. ks.Select(k => new SmilePoint(L(k), L(a) + L(b) * L(k) + L(c) * L(k) * L(k)))];

    private static FittedSmile Fitted(SmileFit fit) => fit as FittedSmile ?? throw new Exception($"Expected a fitted smile; got {fit}.");

    private static void Near(LatticeDecimal actual, LatticeDecimal expected, LatticeDecimal tolerance, string what)
    { if (Abs(actual - expected).CompareTo(tolerance) > 0) throw new Exception($"{what}: expected {expected}; got {actual}."); }

    private static void RecoversKnownParameters()
    {
        var fit = Fitted(Model.Fit(Quadratic("0.45", "-0.10", "0.80", Moneyness)));
        Near(fit.Parameters.A, L("0.45"), Tolerance25, "a");
        Near(fit.Parameters.B, L("-0.10"), Tolerance25, "b");
        Near(fit.Parameters.C, L("0.80"), Tolerance25, "c");
        Near(fit.RmsResidual, L("0"), Tolerance25, "rms");
        Near(fit.MaxAbsResidual, L("0"), Tolerance25, "max residual");
        if (fit.Points != 7) throw new Exception($"Expected 7 points; got {fit.Points}.");
    }

    private static void DeliberateOffset()
    {
        var points = Quadratic("0.45", "-0.10", "0.80", Moneyness);
        var offset = points.Select(p => p.LogMoneyness == L("0.1") ? p with { Volatility = p.Volatility + L("0.02") } : p).ToImmutableArray();
        var fit = Fitted(Model.Fit(offset));

        var residuals = offset.Select(p => p.Volatility - Model.Evaluate(fit.Parameters, p.LogMoneyness)).ToArray();
        var rich = offset.Select((p, i) => (p, i)).Single(x => x.p.LogMoneyness == L("0.1")).i;
        if (residuals[rich].CompareTo(L("0")) <= 0) throw new Exception($"Expected a positive residual at k=0.1; got {residuals[rich]}.");
        if (residuals.Any(r => Abs(r).CompareTo(Abs(residuals[rich])) > 0)) throw new Exception("Expected k=0.1 to carry the largest absolute residual.");

        // Independent solve of the normal equations in System.Decimal (28 digits).
        var xs = offset.Select(p => decimal.Parse(p.LogMoneyness.ToString(), CultureInfo.InvariantCulture)).ToArray();
        var ys = offset.Select(p => decimal.Parse(p.Volatility.ToString(), CultureInfo.InvariantCulture)).ToArray();
        var m = new decimal[3, 4];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++) m[r, c] = xs.Sum(x => Pow(x, r + c));
            m[r, 3] = xs.Zip(ys).Sum(t => Pow(t.First, r) * t.Second);
        }
        for (var col = 0; col < 3; col++)
        {
            var pivot = Enumerable.Range(col, 3 - col).MaxBy(r => Math.Abs(m[r, col]));
            for (var c = 0; c < 4; c++) (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            for (var r = 0; r < 3; r++)
            {
                if (r == col) continue;
                var factor = m[r, col] / m[col, col];
                for (var c = col; c < 4; c++) m[r, c] -= factor * m[col, c];
            }
        }
        LatticeDecimal Solved(int row) => L((m[row, 3] / m[row, row]).ToString("0.####################", CultureInfo.InvariantCulture));
        Near(fit.Parameters.A, Solved(0), Tolerance20, "a");
        Near(fit.Parameters.B, Solved(1), Tolerance20, "b");
        Near(fit.Parameters.C, Solved(2), Tolerance20, "c");
    }

    private static decimal Pow(decimal x, int n) { var v = 1m; for (var i = 0; i < n; i++) v *= x; return v; }

    private static void ExpectGap(SmileFit fit, string code)
    {
        if (fit is not SmileGap gap || gap.Code != code) throw new Exception($"Expected gap {code}; got {fit}.");
    }

    private static void FewStrikes() =>
        ExpectGap(Model.Fit(Quadratic("0.45", "-0.10", "0.80", ["-0.2", "-0.1", "0", "0.1"])), "vol_fit_insufficient_strikes");

    private static void OneSidedStrikes() =>
        ExpectGap(Model.Fit(Quadratic("0.45", "-0.10", "0.80", ["0.05", "0.1", "0.15", "0.2", "0.25"])), "vol_fit_insufficient_strikes");

    private static void NonpositiveSmile() =>
        ExpectGap(Model.Fit(Quadratic("0.05", "0", "-2", ["-0.3", "-0.2", "-0.1", "0", "0.1", "0.2", "0.3"])), "vol_fit_nonpositive");

    private static void Deterministic()
    {
        var points = Quadratic("0.45", "-0.10", "0.80", Moneyness);
        if (Model.Fit(points) != Model.Fit(points)) throw new Exception("Expected identical fits.");
    }

    private static void UnknownModel()
    {
        try { VolModels.Require("smile_x_v9"); throw new Exception("Expected an unknown model error."); }
        catch (MarketDataException e) when (e.Code == "vol_model_unknown") { }
    }

    private static void Check(string label, Action run) { run(); Console.WriteLine($"PASS {label}"); }
}
