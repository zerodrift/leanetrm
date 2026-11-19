using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;

internal static class VolSurfaceChecks
{
    public static void Run()
    {
        Check("normal distribution independent references, symmetry, inverse grid and tails", NormalDistributionContract);
        Check("surface axes explicit points, shared identities, fitted coordinates and coverage", AxisContract);
        Check("surface height domain contains both ends of every plotted stalk", StalkHeightDomain);
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
        Check("a series is in the money by its strike against the underlying settlement", InTheMoneyFollowsSettlement);
        Check("the at-the-money strike is the listed strike nearest the settlement, the higher on a tie", AtTheMoneyIsNearestStrike);
    }

    private static void NormalDistributionContract()
    {
        foreach (var (x, expected) in new[] { ("0", "0.5"), ("1", "0.841344746068542948585232545632"),
            ("2", "0.977249868051820792799717362833"), ("5", "0.999999713348428120806088326248"),
            ("-8", "0.000000000000000622096057427178"), ("-10", "0.000000000000000000000007619853") })
            Near(NormalDistribution.Cdf(L(x)), L(expected), L("0.000000000000000000000000000002"), "normal reference " + x);
        Near(NormalDistribution.Inverse(L("0.1")), L("-1.281551565544600466965103329449"), Tolerance25, "inverse reference");
        Near(NormalDistribution.Inverse(L("0.025")), L("-1.95996398454005423552459443052"), Tolerance25, "inverse tail reference");
        Near(NormalDistribution.Inverse(L("0.00000000000000000001")), L("-9.262340089798407573717356977875"), Tolerance25, "inverse far tail");
        for (var i = -100; i <= 100; i++)
        {
            var x = L(i.ToString(CultureInfo.InvariantCulture)) / L("10");
            Expect(NormalDistribution.Cdf(L("0") - x) == L("1") - NormalDistribution.Cdf(x), "Normal symmetry.");
        }
        foreach (var p in new[] { "0.000000000000000000000000000001", "0.0000000001", "0.001", "0.01", "0.1", "0.25", "0.5", "0.9", "0.999", "0.99999999999999999999" })
            Expect(NormalDistribution.Cdf(NormalDistribution.Inverse(L(p))) == L(p), "Normal inverse roundtrip, including the smallest representable tail: " + p);
        Expect(NormalDistribution.Cdf(L("-12")) == L("0") && NormalDistribution.Cdf(L("12")) == L("1"), "Rounded tails.");
        foreach (var p in new[] { "0", "1", "-0.1", "1.1" })
            Refuses("normal_probability", () => NormalDistribution.Inverse(L(p)));
    }

    private static void Refuses(string code, Action action)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch (MarketDataException e) when (e.Code == code) { }
    }

    private static void AxisContract()
    {
        var input = Expiries("100").Select(e => e with { YearFraction = L("0.25") }).ToImmutableArray();
        var surface = VolSurfaces.Build(Model, input, BothRights(Expiry1, Strikes), Interval) with
            { PlotInputs = new SurfacePlotInputs("USD/unit", "american_futures_option_crr_v1", SurfaceAxes.NumericalPolicy, NormalDistribution.ImplementationId) };
        var axes = SurfaceAxes.Declare(surface);
        Expect(axes.Select(a => a.Id).SequenceEqual(["strike_v1", "log_moneyness_v1", "quick_delta_v1"]), "Declared axes.");
        var delta = axes.Single(a => a.Id == "quick_delta_v1");
        Expect(delta.Preset.Points.Select(p => p.Label).SequenceEqual(["10P", "25P", "ATM", "25C", "10C"]), "Put/call labels.");
        var projected = SurfaceAxes.Project(surface, new("quick_delta_v1", ["0.75", "0.5", "0.25", "0.1"]));
        Expect(projected.Projection!.X.Reversed, "Quick-delta coordinates display in ascending strike order.");
        var nodes = projected.Expiries.Single().Nodes;
        Expect(nodes.Length == 4 && nodes.Single(n => n.AxisValue == L("0.5")).LogMoneyness == L("0"), "Explicit nodes and ATM.");
        foreach (var n in nodes.Where(n => n.Covered))
        {
            var k = n.LogMoneyness!.Value;
            var vol = n.Fitted!.Value;
            Near(NormalDistribution.Cdf((L("0") - k) / (vol * L("0.5"))), n.AxisValue, Tolerance20, "covered delta");
            Near(n.Strike!.Value, L("100") * k.Exp(), Tolerance25, "implied strike");
        }
        Expect(nodes.Any(n => !n.Covered && n.Reason?.Code == "vol_axis_outside_fit"), "Uncovered delta reason.");
        foreach (var text in new[] { "100", "3.28", "999960000000000000000000" })
        {
            var forward = L(text); var low = forward * L("0.8"); var high = forward * L("1.000001");
            var atmSurface = surface with { Series = [], Expiries = [surface.Expiries.Single() with
                { Futures = forward, Parameters = new(L("0.45"), L("0"), L("0")), Grid = [
                    new(low.ToExact(), (low / forward).Ln(), L("0.45")), new(high.ToExact(), (high / forward).Ln(), L("0.45"))] }] };
            foreach (var (axis, point) in new[] { (SurfaceAxes.LogMoneyness, "0"), (SurfaceAxes.QuickDelta, "0.5") })
            {
                var atm = SurfaceAxes.Project(atmSurface, new(axis, [point])).Expiries.Single().Nodes.Single();
                Expect(atm.Covered && atm.Strike == forward, "ATM is covered at exactly its retained forward, including the supported upper range.");
            }
        }
        var ks = SurfaceAxes.Project(surface, new("log_moneyness_v1", ["-0.1", "0", "0.05", "0.5"]));
        foreach (var n in ks.Expiries.Single().Nodes.Where(n => n.Covered))
            Near(n.Fitted!.Value, L("0.45") - L("0.10") * n.AxisValue + L("0.80") * n.AxisValue * n.AxisValue, Tolerance20, "k smile");
        var outside = ks.Expiries.Single().Nodes.Last();
        Expect(outside is { Covered: false, Fitted: null, Reason.Code: "vol_axis_outside_fit" } && outside.Strike is not null,
            "An uncovered k retains its implied strike without an extrapolated volatility.");
        Near(outside.Strike!.Value, L("164.872127070012814684865078781"), Tolerance20, "uncovered implied strike reference");
        foreach (var s in projected.Series.Where(s => s.AxisValue is not null))
            Near(s.AxisValue!.Value, NormalDistribution.Cdf((L("0") - s.LogMoneyness!.Value) /
                (Model.Evaluate(surface.Expiries.Single().Parameters!, s.LogMoneyness.Value) * L("0.5"))), Tolerance25, "stalk fitted-smile delta");
        foreach (var points in new string[]?[] { null, [], ["0"], ["1"], ["NaN"], ["0.5", "0.50"] })
            Refuses("vol_axis_points", () => SurfaceAxes.Project(surface, new("quick_delta_v1", points)));
        Refuses("vol_axis_unknown", () => SurfaceAxes.Project(surface, new("unknown", ["0.5"])));
        StoredProjectionInputs(surface);
        var unbounded = surface with { Expiries = [surface.Expiries.Single() with
            { Parameters = new(L("0.45"), L("-2.965204437011203721094152958718"), L("0")) }] };
        Expect(SurfaceAxes.Project(unbounded, new("quick_delta_v1", ["0.75"])).Expiries.Single().Nodes.Single().Reason?.Code == "vol_axis_root_range",
            "An unrepresentable root is a gap, not a failed read.");
        Expect(SurfaceAxes.Project(surface, new("strike_v1", ["0.000000000000000000000000000001", "999999999999999999999999"]))
            .Expiries.Single().Nodes.All(n => n.Reason?.Code == "vol_axis_outside_fit"), "Extreme strikes are outside coverage without division overflow.");
        foreach (var (forward, k, expected, tolerance) in new[] {
            ("0.0000000001", "56", "209165949601299.615390707115721467", "0.000000000000001"),
            ("100000000000000000000000", "-80", "0.000000000001804851387845415172", "0.000000000000000000000000000001") })
        {
            var scaled = surface with { Expiries = [surface.Expiries.Single() with { Futures = L(forward) }], Series = [] };
            var node = SurfaceAxes.Project(scaled, new("log_moneyness_v1", [k])).Expiries.Single().Nodes.Single();
            Expect(!node.Covered && node.Strike is not null, "Representable implied strike survives exponential scaling.");
            Near(node.Strike!.Value, L(expected), L(tolerance), "scaled implied strike reference");
        }
        var flat = surface with { Expiries = [surface.Expiries.Single() with { Parameters = new(L("0.45"), L("0"), L("0")) }] };
        var almostFlat = flat with { Expiries = [flat.Expiries.Single() with { Parameters = new(L("0.45"), L("0"), L("0.000000000001")) }] };
        var flatNode = SurfaceAxes.Project(flat, new("quick_delta_v1", ["0.75"])).Expiries.Single().Nodes.Single();
        var curvedNode = SurfaceAxes.Project(almostFlat, new("quick_delta_v1", ["0.75"])).Expiries.Single().Nodes.Single();
        Near(flatNode.LogMoneyness!.Value, L("-0.151760193794118392220501078272"), Tolerance25, "linear root independent reference");
        Near(curvedNode.LogMoneyness!.Value, flatNode.LogMoneyness.Value, L("0.000000000001"), "continuous curvature branch");
        var tiny = surface with { Expiries = [surface.Expiries.Single() with
            { Parameters = new(L("0.008100000000000000000000000001"), L("-0.18"), L("1")) }],
            Series = [surface.Series.First() with { LogMoneyness = L("0.09") }] };
        Expect(SurfaceAxes.Project(tiny, new("quick_delta_v1", ["0.5"])).Series.Single().AxisValue == L("0"), "Far standardized tail saturates before division.");
        var noRoot = surface with { Expiries = [surface.Expiries.Single() with { Parameters = new(L("0.6"), L("0"), L("10")) }] };
        Expect(SurfaceAxes.Project(noRoot, new("quick_delta_v1", ["0.9"])).Expiries.Single().Nodes.Single().Reason?.Code == "vol_axis_no_real_root", "No real root.");
    }

    private static void StoredProjectionInputs(VolSurfaceResult surface)
    {
        var expiry = surface.Expiries.Single();
        var policies = surface.PlotInputs!;
        var request = new SurfaceAxisRequest(SurfaceAxes.LogMoneyness, ["0"]);
        foreach (var partial in new[] {
            surface with { Expiries = [null!] }, surface with { StrikeAxis = default }, surface with { Expiries = default },
            surface with { Series = default }, surface with { PlotInputs = null }, surface with { ModelId = "unsupported" },
            surface with { PlotInputs = policies with { StrikeUnit = "" } }, surface with { PlotInputs = policies with { OptionPolicy = "unsupported" } },
            surface with { PlotInputs = policies with { AxisNumericalPolicy = "unsupported" } },
            surface with { PlotInputs = policies with { NormalImplementation = "unsupported" } },
            surface with { Expiries = [expiry with { YearFraction = null }] }, surface with { Expiries = [expiry with { YearFraction = L("0") }] },
            surface with { Expiries = [expiry with { Futures = null }] }, surface with { Expiries = [expiry with { Futures = L("-1") }] },
            surface with { Expiries = [expiry with { Parameters = null }] }, surface with { Expiries = [expiry with { Grid = default }] },
            surface with { Expiries = [expiry with { Grid = [null!] }] }, surface with { Expiries = [expiry, expiry] },
            surface with { Series = [null!] }, surface with { Series = [surface.Series[0] with { Series = null! }] },
            surface with { Expiries = [] }
        })
        {
            Expect(!SurfaceAxes.TryDeclare(partial, out _), "A partial stored revision requires an explicit refit.");
            Refuses("vol_axis_inputs_missing", () => SurfaceAxes.Project(partial, request));
        }
        var expired = expiry with { Status = "option_expired", YearFraction = null, Futures = null, Parameters = null, Grid = default };
        var result = SurfaceAxes.Project(surface with { StrikeAxis = [], Expiries = [expired] }, request);
        Expect(result.Expiries.Single().Nodes.IsEmpty && result.Series.All(s => s.AxisValue is null), "An expired row needs no live fit inputs.");
        var gap = expiry with { Status = "vol_fit_insufficient_strikes", Futures = null, Parameters = null, Grid = default };
        var gapped = SurfaceAxes.Project(surface with { Expiries = [gap] }, request);
        Expect(gapped.Expiries.Single().TimeLabel is not null && gapped.Expiries.Single().Nodes.IsEmpty, "A live gap retains its time without fit coefficients.");
        var declarationsOnly = surface with { StrikeAxis = [], Expiries = [], Series = [] };
        Expect(SurfaceAxes.Declare(declarationsOnly).Length == 3, "A product with no fit still offers its declared display axes.");
    }

    private static void StalkHeightDomain()
    {
        var members = new[] { "-0.9", "-0.8", "-0.7", "0.7", "0.8", "0.9" }.Select(text =>
        {
            var k = L(text);
            return new SurfaceSeries(text, k.CompareTo(L("0")) < 0 ? OptionRight.Put : OptionRight.Call,
                (L("100") * k.Exp()).ToExact(), Expiry1, "f", null, "solved", L("3") - L("3") * k * k);
        }).Append(new("atm-put", OptionRight.Put, ExactDecimal.Parse("100"), Expiry1, "f", null, "solved", L("1")));
        var surface = VolSurfaces.Build(Model, [new(Expiry1, "f", L("100")) { YearFraction = L("0.25") }], [..members], Interval) with
            { PlotInputs = new("USD/unit", "american_futures_option_crr_v1", SurfaceAxes.NumericalPolicy, NormalDistribution.ImplementationId) };
        var projected = SurfaceAxes.Project(surface, new(SurfaceAxes.LogMoneyness, ["0.8"]));
        var ticks = projected.Projection!.Height.Ticks.Select(t => t.Value).ToArray();
        var stalks = projected.Series.Where(s => s.AxisValue is not null && s.Fitted is not null && s.RichCheap is not null && s.Series.MarketVolatility is not null);
        Expect(stalks.Any(s => s.Series.Code == "atm-put" && !s.FitPoint && s.Fitted!.Value.CompareTo(L("2.9")) > 0), "The sparse selection omits a high fitted stalk base.");
        foreach (var stalk in stalks)
            foreach (var end in new[] { stalk.Fitted!.Value, stalk.Series.MarketVolatility!.Value })
                Expect(end.CompareTo(ticks.Min()) >= 0 && end.CompareTo(ticks.Max()) <= 0, "The server height ticks enclose each plotted stalk's base and tip.");
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

    private static void InTheMoneyFollowsSettlement()
    {
        var surface = Build(BothRights(Expiry1, Strikes).AddRange(BothRights(Expiry2, Strikes, "97")), null, "97");
        foreach (var strike in Strikes)
        {
            var below = decimal.Parse(strike, CultureInfo.InvariantCulture) < 97;
            Expect(Result(surface, OptionRight.Call, strike, Expiry2).InTheMoney == below, $"Call {strike} against 97.");
            Expect(Result(surface, OptionRight.Put, strike, Expiry2).InTheMoney == !below, $"Put {strike} against 97.");
        }
        var level = Build(BothRights(Expiry1, Strikes));
        Expect(Result(level, OptionRight.Call, "100").InTheMoney == false && Result(level, OptionRight.Put, "100").InTheMoney == false,
            "A strike at the settlement is not in the money.");
        Expect(surface.Series.Where(r => r.Series.ExpiryAt == Expiry1).All(r => r.InTheMoney is null), "Without an underlying settlement nothing is stated.");
    }

    private static void AtTheMoneyIsNearestStrike()
    {
        ExactDecimal? Nearest(string? futures) => Build(BothRights(Expiry1, Strikes, futures ?? "100"), futures).Expiries.Single().AtTheMoneyStrike;
        Expect(Nearest("97") == ExactDecimal.Parse("95"), "97 is nearest 95.");
        Expect(Nearest("98") == ExactDecimal.Parse("100"), "98 is nearest 100.");
        Expect(Nearest("97.5") == ExactDecimal.Parse("100"), "A tie takes the higher strike.");
        Expect(Nearest("300") == ExactDecimal.Parse("110"), "Beyond the listed strikes the nearest is the last.");
        Expect(Nearest(null) is null, "Without an underlying settlement there is none.");
    }

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
