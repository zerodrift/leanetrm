using System.Collections.Immutable;
using System.Globalization;

namespace OpenEtrm.MarketData.Options;

public sealed record SurfaceAxisRequest(string? Axis, string[]? Points);
public sealed record SurfaceAxisPoint(string Id, LatticeDecimal Value, string Label);
public sealed record SurfaceAxisPreset(string Name, ImmutableArray<SurfaceAxisPoint> Points);
public sealed record SurfaceAxisPolicy(string Id, string Label, string Unit, SurfaceAxisPreset Preset);
public sealed record SurfaceAxisReason(string Code, string Message);
public sealed record SurfaceAxisNode(string PointId, LatticeDecimal AxisValue, string Label, LatticeDecimal? LogMoneyness,
    LatticeDecimal? Strike, LatticeDecimal? Fitted, bool Covered, SurfaceAxisReason? Reason);
public sealed record SurfacePlotAxis(string Id, string Label, string Unit, ImmutableArray<SurfaceAxisPoint> Ticks);
public sealed record SurfaceProjection(string NumericalPolicy, string NormalImplementation, string Tolerance,
    ImmutableArray<SurfaceAxisPolicy> Axes, SurfacePlotAxis X, SurfacePlotAxis Depth, SurfacePlotAxis Height);

public static class SurfaceAxes
{
    public const string Strike = "strike_v1";
    public const string LogMoneyness = "log_moneyness_v1";
    public const string QuickDelta = "quick_delta_v1";
    public const string NumericalPolicy = "vol_surface_axes_decimal_v1";
    public const string Tolerance = "0.00000000000000000001";
    private static readonly LatticeDecimal Zero = L("0"), One = L("1"), Two = L("2");
    private static LatticeDecimal L(string text) => LatticeDecimal.Parse(text);

    public static ImmutableArray<SurfaceAxisPolicy> Declare(VolSurfaceResult surface)
    {
        var inputs = surface.PlotInputs ?? throw Missing();
        if (surface.StrikeAxis.IsDefault || surface.Expiries.IsDefault || surface.Series.IsDefault || string.IsNullOrWhiteSpace(inputs.StrikeUnit)) throw Missing();
        return [new(Strike, "Strike", inputs.StrikeUnit, new("Listed strikes", [..surface.StrikeAxis.Select(k => Point(Strike, LatticeDecimal.From(k)))])),
            new(LogMoneyness, "Log-moneyness", "ln(K/F)", new("−0.10 to +0.10 by 0.01", [..Enumerable.Range(-10, 21).Select(i => Point(LogMoneyness, L(i.ToString(CultureInfo.InvariantCulture)) / L("100")))])),
            new(QuickDelta, "Quick delta", "call delta", new("10P · 25P · ATM · 25C · 10C", [..new[] { "0.9", "0.75", "0.5", "0.25", "0.1" }.Select(p => Point(QuickDelta, L(p)))]))];
    }

    public static ImmutableArray<SurfaceAxisPoint> Points(SurfaceAxisRequest request)
    {
        if (request.Axis is not (Strike or LogMoneyness or QuickDelta))
            throw new MarketDataException("vol_axis_unknown", "Select a declared surface axis.");
        if (request.Points is not { Length: > 0 and <= 501 })
            throw new MarketDataException("vol_axis_points", "Supply between 1 and 501 explicit axis points.");
        var values = new List<LatticeDecimal>();
        foreach (var text in request.Points)
        {
            LatticeDecimal value;
            try { value = L(text ?? ""); }
            catch (MarketDataException) { throw new MarketDataException("vol_axis_points", "Each point must be exact decimal text within the numerical policy."); }
            if (values.Contains(value) || request.Axis == QuickDelta && (value.CompareTo(Zero) <= 0 || value.CompareTo(One) >= 0) ||
                request.Axis == Strike && value.CompareTo(Zero) <= 0)
                throw new MarketDataException("vol_axis_points", "Points must be distinct; strikes positive and quick deltas strictly between zero and one.");
            values.Add(value);
        }
        return [..(request.Axis == QuickDelta ? values.OrderDescending() : values.Order()).Select(v => Point(request.Axis, v))];
    }

    public static VolSurfaceResult Project(VolSurfaceResult surface, SurfaceAxisRequest request)
    {
        var points = Points(request);
        var axes = Declare(surface);
        if (surface.ModelId != VolModels.QuadraticLogMoneyness ||
            surface.PlotInputs!.OptionPolicy != OptionNumericalPolicies.CoxRossRubinstein ||
            surface.PlotInputs.AxisNumericalPolicy != NumericalPolicy || surface.PlotInputs.NormalImplementation != NormalDistribution.ImplementationId)
            throw new MarketDataException("vol_axis_inputs_missing", "The stored fit does not declare the supported smile and option time policy.");
        var model = VolModels.Require(surface.ModelId);
        var expiries = surface.Expiries.Select(expiry =>
        {
            if (expiry.Status == "option_expired") return expiry with { Nodes = [] };
            var t = expiry.YearFraction ?? throw Missing();
            if (t.CompareTo(Zero) <= 0) throw Missing();
            if (expiry.Status != "fitted") return expiry with { Nodes = [], TimeLabel = TimeLabel(t) };
            if (expiry.Parameters is null || expiry.Futures is null || expiry.Futures.Value.CompareTo(Zero) <= 0 || expiry.Grid.IsDefaultOrEmpty) throw Missing();
            return expiry with { TimeLabel = TimeLabel(t), Nodes = [..points.Select(p => Node(expiry, request.Axis!, p, model))] };
        }).ToImmutableArray();
        var byExpiry = expiries.ToDictionary(e => e.ExpiryAt);
        var series = surface.Series.Select(s =>
        {
            var e = byExpiry[s.Series.ExpiryAt];
            if (e.Status != "fitted" || s.LogMoneyness is not { } k) return s with { AxisValue = null };
            // A listed series beyond the OTM fit's bounds never acquires extrapolated coordinates or a stalk.
            if (!Inside(e, k)) return s with { AxisValue = null };
            var sigma = model.Evaluate(e.Parameters!, k);
            return s with { AxisValue = request.Axis switch
            {
                Strike => LatticeDecimal.From(s.Series.Strike), LogMoneyness => k,
                QuickDelta => QuickDeltaAt(k, sigma, e.YearFraction!.Value),
                _ => throw new InvalidOperationException(),
            }};
        }).ToImmutableArray();
        var selected = axes.Single(a => a.Id == request.Axis);
        var vols = expiries.SelectMany(e => e.Nodes).Where(n => n.Covered).Select(n => n.Fitted!.Value)
            .Concat(series.Where(s => s.AxisValue is not null && s.Series.MarketVolatility is not null).Select(s => s.Series.MarketVolatility!.Value)).ToArray();
        ImmutableArray<SurfaceAxisPoint> height = vols.Length == 0 ? [] : [..Enumerable.Range(0, 6).Select(i =>
        {
            var value = vols.Min() + (vols.Max() - vols.Min()) * L(i.ToString(CultureInfo.InvariantCulture)) / L("5");
            return new SurfaceAxisPoint(value.ToString(), value, (value * L("100")).ToExact().ToString(new AmountFormat(2, CashTieRule.ToEven, false, false)));
        })];
        return surface with { Expiries = expiries, Series = series, Projection = new(NumericalPolicy, NormalDistribution.ImplementationId, Tolerance, axes,
            new(selected.Id, selected.Label, selected.Unit, points),
            new("option_year_fraction_v1", "Time to expiry", "years", [..expiries.Where(e => e.YearFraction is not null).Select(e =>
                new SurfaceAxisPoint(e.ExpiryAt.ToString("O", CultureInfo.InvariantCulture), e.YearFraction!.Value, e.TimeLabel!))]),
            new("annualized_volatility_v1", "Volatility", "%", height)) };
    }

    private static SurfaceAxisNode Node(SurfaceExpiry e, string axis, SurfaceAxisPoint p, IVolModel model)
    {
        LatticeDecimal? k = null, strike = axis == Strike ? p.Value : null;
        SurfaceAxisNode Gap(string code, string message) => new(p.Id, p.Value, p.Label, k, strike, null, false, new(code, message));
        if (axis == Strike && (p.Value.CompareTo(LatticeDecimal.From(e.Grid[0].Strike)) < 0 || p.Value.CompareTo(LatticeDecimal.From(e.Grid[^1].Strike)) > 0))
        {
            // Log differences retain the coordinate even when K/F would underflow or overflow.
            k = p.Value.Ln() - e.Futures!.Value.Ln();
            return Gap("vol_axis_outside_fit", "The strike is outside this expiry's fitted range; extrapolation is prohibited.");
        }
        try
        {
            k = axis switch
            {
                Strike => (p.Value / e.Futures!.Value).Ln(), LogMoneyness => p.Value,
                QuickDelta => DeltaRoot(e.Parameters!, e.YearFraction!.Value, p.Value), _ => null,
            };
        }
        catch (MarketDataException error) when (error.Code is "numeric_range" or "numeric_division")
        {
            return Gap("vol_axis_root_range", "The quick-delta root exceeds the supported numerical range.");
        }
        if (k is null) return Gap("vol_axis_no_real_root", "The selected quick delta has no finite real root on the branch continuous from zero curvature.");
        try
        {
            var implied = (e.Futures!.Value.Ln() + k.Value).Exp();
            if (implied.CompareTo(Zero) > 0) strike ??= implied;
        }
        catch (MarketDataException error) when (error.Code == "numeric_range") { /* The k coordinate can still state an outside-fit gap. */ }
        if (!Inside(e, k.Value)) return Gap("vol_axis_outside_fit", "The point is outside this expiry's fitted strike range; extrapolation is prohibited.");
        if (strike is null) return Gap("vol_axis_root_range", "The implied strike exceeds the supported numerical range.");
        var sigma = model.Evaluate(e.Parameters!, k.Value);
        if (sigma.CompareTo(Zero) <= 0) return Gap("vol_axis_nonpositive_volatility", "The fitted volatility is not positive at this point.");
        if (axis == QuickDelta)
        {
            var reproduced = QuickDeltaAt(k.Value, sigma, e.YearFraction!.Value);
            if (LatticeDecimal.Max(reproduced - p.Value, p.Value - reproduced).CompareTo(L(Tolerance)) > 0)
                return Gap("vol_axis_invalid_root", "The quadratic root does not reproduce the selected quick delta under the numerical policy.");
        }
        return new(p.Id, p.Value, p.Label, k, strike, sigma, true, null);
    }

    private static LatticeDecimal QuickDeltaAt(LatticeDecimal k, LatticeDecimal sigma, LatticeDecimal t)
    {
        if (k == Zero) return L("0.5");
        var width = sigma * t.Sqrt();
        if (LatticeDecimal.Max(k, Zero - k).CompareTo(L("12") * width) >= 0)
            return k.CompareTo(Zero) < 0 ? One : Zero;
        return NormalDistribution.Cdf((Zero - k) / width);
    }

    private static bool Inside(SurfaceExpiry e, LatticeDecimal k) =>
        k.CompareTo(e.Grid[0].LogMoneyness) >= 0 && k.CompareTo(e.Grid[^1].LogMoneyness) <= 0;

    private static LatticeDecimal? DeltaRoot(SmileParameters p, LatticeDecimal t, LatticeDecimal delta)
    {
        if (delta == L("0.5")) return Zero;
        var zt = NormalDistribution.Inverse(delta) * t.Sqrt();
        var a = zt * p.C; var b = One + zt * p.B; var c = zt * p.A;
        // b=0 has no finite C→0 solution. Never select a different branch implicitly.
        if (b == Zero) return null;
        if (a == Zero) return (Zero - c) / b;
        var discriminant = b * b - L("4") * a * c;
        if (discriminant.CompareTo(Zero) < 0) return null;
        // Rationalized root avoids cancellation for small C, with the sign of b
        // selecting the branch that tends to -c/b as C tends to zero.
        return (Zero - Two * c) / (b + (b.CompareTo(Zero) > 0 ? discriminant.Sqrt() : Zero - discriminant.Sqrt()));
    }

    private static SurfaceAxisPoint Point(string axis, LatticeDecimal value)
    {
        var label = axis == QuickDelta ? value == L("0.5") ? "ATM" :
            value.CompareTo(L("0.5")) > 0 ? ((One - value) * L("100")).ToString() + "P" : (value * L("100")).ToString() + "C" : value.ToString();
        return new(value.ToString(), value, label);
    }
    private static string TimeLabel(LatticeDecimal t) => t.ToExact().ToString(new AmountFormat(4, CashTieRule.ToEven, false, false));
    private static MarketDataException Missing() => new("vol_axis_inputs_missing", "The stored fit lacks its own forward, model year fraction, fitted range or unit/policy. Refit explicitly; current data cannot replace stored inputs.");
}
