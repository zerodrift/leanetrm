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
        Check("otm right is the fit point", OtmIsFitPoint);
        Check("at-the-forward strike uses the call", AtForwardUsesCall);
        Check("both rights get rich cheap", BothRightsRichCheap);
        Check("failed otm solve drops the strike, never the itm", FailedOtmDropsStrike);
        Check("at intrinsic and errors carry no rich cheap", NoRichCheapWithoutVolatility);
        Check("expiry without underlying is a gap, others fit", MissingUnderlyingIsGap);
        Check("grid spans fitted strikes at the listed interval", GridSpansFittedStrikes);
        Check("grid ends on the highest fitted strike off the lattice", GridEndsOnHighestStrike);
        Check("strike axis is the union across expiries", AxisIsUnion);
        Check("an expiry whose series all expired is option_expired and off the axis", ExpiredExpiryIsOffTheAxis);
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

    private static readonly DateTimeOffset Expiry1 = DateTimeOffset.Parse("2027-01-15T00:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset Expiry2 = DateTimeOffset.Parse("2027-02-15T00:00:00Z", CultureInfo.InvariantCulture);
    private static readonly ExactDecimal Interval = ExactDecimal.Parse("5");
    private static readonly string[] Strikes = ["85", "90", "95", "100", "105", "110"];

    private static LatticeDecimal Smile(string futures, string strike) =>
        Model.Evaluate(new SmileParameters(L("0.45"), L("-0.10"), L("0.80")), (L(strike) / L(futures)).Ln());

    private static SurfaceSeries Series(DateTimeOffset expiry, OptionRight right, string strike, string futures = "100", string outcome = "solved", bool volatility = true)
    {
        var withVol = volatility && outcome == "solved";
        return new SurfaceSeries($"{right}-{strike}-{expiry:MM}", right, ExactDecimal.Parse(strike), expiry, "FUT", ExactDecimal.Parse("1"), outcome,
            withVol ? Smile(futures, strike) : null);
    }

    private static ImmutableArray<SurfaceSeries> BothRights(DateTimeOffset expiry, string[] strikes, string futures = "100") =>
        [.. strikes.SelectMany(s => new[] { Series(expiry, OptionRight.Call, s, futures), Series(expiry, OptionRight.Put, s, futures) })];

    private static ImmutableArray<SurfaceExpiryInput> Expiries(string? futures1, string? futures2 = null) =>
        [new SurfaceExpiryInput(Expiry1, "FUT", futures1 is null ? null : L(futures1)),
         .. (futures2 is null ? [] : new[] { new SurfaceExpiryInput(Expiry2, "FUT", L(futures2)) })];

    private static VolSurfaceResult Build(ImmutableArray<SurfaceSeries> series, string? futures1 = "100", string? futures2 = null) =>
        VolSurfaces.Build(Model, Expiries(futures1, futures2), series, Interval);

    private static SurfaceSeriesResult Result(VolSurfaceResult surface, OptionRight right, string strike, DateTimeOffset? expiry = null) =>
        surface.Series.Single(r => r.Series.Right == right && r.Series.Strike == ExactDecimal.Parse(strike) && r.Series.ExpiryAt == (expiry ?? Expiry1));

    private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static void OtmIsFitPoint()
    {
        var surface = Build(BothRights(Expiry1, Strikes));
        foreach (var strike in new[] { "85", "90", "95" })
        {
            Expect(Result(surface, OptionRight.Put, strike).FitPoint, $"Put {strike} should be the fit point.");
            Expect(!Result(surface, OptionRight.Call, strike).FitPoint, $"Call {strike} should not be a fit point.");
        }
        foreach (var strike in new[] { "105", "110" })
        {
            Expect(Result(surface, OptionRight.Call, strike).FitPoint, $"Call {strike} should be the fit point.");
            Expect(!Result(surface, OptionRight.Put, strike).FitPoint, $"Put {strike} should not be a fit point.");
        }
        Expect(surface.Expiries.Single().Points == 6, "Expected six fit points.");
    }

    private static void AtForwardUsesCall()
    {
        var surface = Build(BothRights(Expiry1, Strikes));
        var call = Result(surface, OptionRight.Call, "100");
        Expect(call.FitPoint && !Result(surface, OptionRight.Put, "100").FitPoint, "The call at the forward should be the fit point.");
        Expect(call.LogMoneyness == L("0"), $"Expected k = 0; got {call.LogMoneyness}.");
    }

    private static void BothRightsRichCheap()
    {
        var offset = L("0.02");
        var series = BothRights(Expiry1, Strikes).Select(s => s.Right == OptionRight.Call && s.Strike == ExactDecimal.Parse("90")
            ? s with { MarketVolatility = s.MarketVolatility + offset } : s).ToImmutableArray();
        var surface = Build(series);
        var itm = Result(surface, OptionRight.Call, "90");
        Expect(!itm.FitPoint, "An ITM series is not a fit point.");
        Expect(itm.Fitted is not null && itm.RichCheap is not null, "An ITM solved series gets Fitted and RichCheap.");
        Near(itm.RichCheap!.Value, itm.Series.MarketVolatility!.Value - itm.Fitted!.Value, Tolerance25, "rich cheap");
        Near(itm.RichCheap!.Value, offset, Tolerance20, "offset recovered");
        var otm = Result(surface, OptionRight.Put, "90");
        Near(otm.RichCheap!.Value, L("0"), Tolerance20, "otm rich cheap");
    }

    private static void FailedOtmDropsStrike()
    {
        var series = BothRights(Expiry1, Strikes).Select(s => s.Right == OptionRight.Put && s.Strike == ExactDecimal.Parse("90")
            ? s with { Outcome = "option_settlement_missing", MarketVolatility = null } : s).ToImmutableArray();
        var surface = Build(series);
        Expect(Result(surface, OptionRight.Put, "90").FitPoint == false, "The failed put is not a fit point.");
        Expect(Result(surface, OptionRight.Call, "90").FitPoint == false, "The ITM call never substitutes.");
        var expiry = surface.Expiries.Single();
        Expect(expiry.Status == "fitted" && expiry.Points == 5, $"Expected fitted on 5 points; got {expiry.Status} on {expiry.Points}.");
        Expect(Result(surface, OptionRight.Put, "90").Series.Outcome == "option_settlement_missing", "The outcome is kept.");
    }

    private static void NoRichCheapWithoutVolatility()
    {
        var series = BothRights(Expiry1, Strikes)
            .Select(s => s.Right == OptionRight.Call && s.Strike == ExactDecimal.Parse("90") ? s with { Outcome = "at_intrinsic", MarketVolatility = null }
                : s.Right == OptionRight.Call && s.Strike == ExactDecimal.Parse("85") ? s with { Outcome = "option_settlement_missing", MarketVolatility = null } : s)
            .ToImmutableArray();
        var surface = Build(series);
        foreach (var (strike, outcome) in new[] { ("90", "at_intrinsic"), ("85", "option_settlement_missing") })
        {
            var result = Result(surface, OptionRight.Call, strike);
            Expect(result.Series.Outcome == outcome, $"Outcome {outcome} should be kept.");
            Expect(result.Series.MarketVolatility is null && result.Fitted is null && result.RichCheap is null, $"{outcome} carries no volatility, fit or rich cheap.");
        }
    }

    private static void MissingUnderlyingIsGap()
    {
        var series = BothRights(Expiry1, Strikes, "100").AddRange(BothRights(Expiry2, Strikes, "100"));
        var surface = Build(series, null, "100");
        var gap = surface.Expiries.Single(e => e.ExpiryAt == Expiry1);
        Expect(gap.Status == "vol_fit_insufficient_strikes" && gap.Parameters is null && gap.RmsResidual is null && gap.MaxAbsResidual is null && gap.Grid.IsEmpty,
            "An expiry without an underlying is an empty gap.");
        foreach (var result in surface.Series.Where(r => r.Series.ExpiryAt == Expiry1))
            Expect(result.LogMoneyness is null && result.Fitted is null && result.RichCheap is null && !result.FitPoint, "Series of a gap expiry carry nothing.");
        Expect(surface.Expiries.Single(e => e.ExpiryAt == Expiry2).Status == "fitted", "The other expiry still fits.");
        Expect(Result(surface, OptionRight.Call, "105", Expiry2).LogMoneyness is not null, "The fitted expiry has moneyness.");
    }

    private static void GridSpansFittedStrikes()
    {
        var surface = Build(BothRights(Expiry1, Strikes));
        var expiry = surface.Expiries.Single();
        var expected = new[] { "85", "90", "95", "100", "105", "110" };
        Expect(expiry.Grid.Select(g => g.Strike.ToString()).SequenceEqual(expected), $"Unexpected grid strikes: {string.Join(",", expiry.Grid.Select(g => g.Strike))}.");
        foreach (var point in expiry.Grid)
        {
            Near(point.LogMoneyness, (L(point.Strike.ToString()) / L("100")).Ln(), Tolerance25, "grid k");
            Expect(point.Fitted == Model.Evaluate(expiry.Parameters!, point.LogMoneyness), "Grid value equals the model evaluation.");
        }
    }

    private static void GridEndsOnHighestStrike()
    {
        var surface = Build(BothRights(Expiry1, ["85", "90", "95", "100", "105", "108"]));
        var grid = surface.Expiries.Single().Grid;
        Expect(grid.Select(g => g.Strike.ToString()).SequenceEqual(["85", "90", "95", "100", "105", "108"]), "The grid must end on the highest fitted strike, not step past it.");
    }

    private static void AxisIsUnion()
    {
        var series = BothRights(Expiry1, ["85", "90", "95", "100", "105", "110"]).AddRange(BothRights(Expiry2, ["90", "92.5", "95", "100", "105", "112.5"]));
        var surface = Build(series, "100", "100");
        var expected = new[] { "85", "90", "92.5", "95", "100", "105", "110", "112.5" };
        Expect(surface.StrikeAxis.Select(s => s.ToString()).SequenceEqual(expected), $"Unexpected axis: {string.Join(",", surface.StrikeAxis)}.");
        Expect(surface.Series.Select(r => (r.Series.ExpiryAt, r.Series.Strike)).SequenceEqual(
            surface.Series.Select(r => (r.Series.ExpiryAt, r.Series.Strike)).OrderBy(x => x.ExpiryAt).ThenBy(x => x.Strike)), "Series are ordered by expiry then strike.");
    }

    private static void ExpiredExpiryIsOffTheAxis()
    {
        var expired = new[] { "60", "65", "70", "75", "80" }.SelectMany(k => new[] { Series(Expiry1, OptionRight.Call, k, outcome: "option_expired"),
            Series(Expiry1, OptionRight.Put, k, outcome: "option_expired") });
        var surface = Build([.. expired, .. BothRights(Expiry2, Strikes)], null, "100");
        var lapsed = surface.Expiries.Single(e => e.ExpiryAt == Expiry1);
        Expect(lapsed.Status == "option_expired" && lapsed.Parameters is null && lapsed.Grid.IsEmpty, $"Expected an option_expired expiry; got {lapsed.Status}.");
        Expect(surface.StrikeAxis.Select(s => s.ToString()).SequenceEqual(Strikes), $"The axis should list only live strikes: {string.Join(",", surface.StrikeAxis)}.");
        Expect(surface.Series.Count(r => r.Series.ExpiryAt == Expiry1) == 10, "Expired series are still listed.");
        Expect(surface.Expiries.Single(e => e.ExpiryAt == Expiry2).Status == "fitted", "The live expiry still fits.");
    }

    private static void Check(string label, Action run) { run(); Console.WriteLine($"PASS {label}"); }
}
