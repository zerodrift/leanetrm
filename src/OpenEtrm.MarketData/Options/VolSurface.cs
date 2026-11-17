using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Options;

// Outcome is "solved", "at_intrinsic" or the calibration error code. MarketVolatility
// is a decimal fraction and is present only for a solved series.
public sealed record SurfaceSeries(string Code, OptionRight Right, ExactDecimal Strike, DateTimeOffset ExpiryAt, string UnderlyingCode,
    ExactDecimal? Settlement, string Outcome, LatticeDecimal? MarketVolatility);

// Futures is null when the underlying settlement is missing. DaysToExpiry is the model's own time to expiry in days,
// stated by the fit that knows its valuation instant.
public sealed record SurfaceExpiryInput(DateTimeOffset ExpiryAt, string UnderlyingCode, LatticeDecimal? Futures)
{
    public ExactDecimal? DaysToExpiry { get; init; }
    public LatticeDecimal? YearFraction { get; init; }
}

// RichCheap = MarketVolatility - Fitted, for every solved series of a fitted expiry. InTheMoney is a call struck below
// the underlying settlement or a put struck above it, and is absent without that settlement or once expired. Analytics
// are the series' value and sensitivities where the fit computed them.
public sealed record SurfaceSeriesResult(SurfaceSeries Series, LatticeDecimal? LogMoneyness, LatticeDecimal? Fitted, LatticeDecimal? RichCheap, bool FitPoint,
    bool? InTheMoney)
{
    public OptionSeriesSensitivities? Analytics { get; init; }
    public LatticeDecimal? AxisValue { get; init; }
}

public sealed record SurfacePlotInputs(string StrikeUnit, string OptionPolicy, string AxisNumericalPolicy, string NormalImplementation);

public sealed record SurfaceGridPoint(ExactDecimal Strike, LatticeDecimal LogMoneyness, LatticeDecimal Fitted);

// Status is fitted, option_expired (every series of the expiry is at or past it), vol_fit_insufficient_strikes or vol_fit_nonpositive.
// AtTheMoneyStrike is the listed strike nearest the underlying settlement, the higher of two equally near; it is absent
// without that settlement or once expired.
public sealed record SurfaceExpiry(DateTimeOffset ExpiryAt, string UnderlyingCode, LatticeDecimal? Futures, string Status, SmileParameters? Parameters,
    int Points, LatticeDecimal? RmsResidual, LatticeDecimal? MaxAbsResidual, ImmutableArray<SurfaceGridPoint> Grid, ExactDecimal? AtTheMoneyStrike)
{
    public ExactDecimal? DaysToExpiry { get; init; }
    public LatticeDecimal? YearFraction { get; init; }
    public string? TimeLabel { get; init; }
    public ImmutableArray<SurfaceAxisNode> Nodes { get; init; } = [];
}

// The sensitivity selection a fit computed its series' analytics under, every member as stated, with the calendar clock
// one day was counted on and the instant that day runs to. The clock is absent where none could be read.
public sealed record SurfaceSensitivities(string Policy, int Ticks, int? PriceSteps, string AbsoluteTolerance, string DeltaRelativeTolerance,
    string GammaRelativeTolerance, ExactDecimal VolatilityBump, string? ClockSource, string? ClockZone, DateTimeOffset? DayTo);

// StrikeAxis lists the strikes of the series of every expiry that has not expired.
public sealed record VolSurfaceResult(string ModelId, ImmutableArray<ExactDecimal> StrikeAxis, ImmutableArray<SurfaceExpiry> Expiries, ImmutableArray<SurfaceSeriesResult> Series)
{
    public SurfaceSensitivities? Sensitivities { get; init; }
    public SurfacePlotInputs? PlotInputs { get; init; }
    public SurfaceProjection? Projection { get; init; }
}

public static class VolSurfaces
{
    // Each strike contributes at most one fit point: the out-of-the-money right
    // (put below the futures price, call at or above it), and only when it solved.
    // An in-the-money series never stands in for a failed out-of-the-money one.
    public static VolSurfaceResult Build(IVolModel model, ImmutableArray<SurfaceExpiryInput> expiries, ImmutableArray<SurfaceSeries> series, ExactDecimal strikeInterval)
    {
        if (strikeInterval.CompareTo(ExactDecimal.Parse("0")) <= 0)
            throw new MarketDataException("vol_surface_input", "The strike interval must be positive.");
        if (expiries.Select(e => e.ExpiryAt).Distinct().Count() != expiries.Length)
            throw new MarketDataException("vol_surface_input", "Each expiry may be listed once.");
        if (series.Select(s => (s.ExpiryAt, s.Strike, s.Right)).Distinct().Count() != series.Length)
            throw new MarketDataException("vol_surface_input", "Each series may be listed once.");
        if (series.Any(s => !expiries.Any(e => e.ExpiryAt == s.ExpiryAt && e.UnderlyingCode == s.UnderlyingCode)))
            throw new MarketDataException("vol_surface_input", "Every series needs its expiry and underlying listed.");

        var builtExpiries = new List<SurfaceExpiry>();
        var results = new List<SurfaceSeriesResult>();
        foreach (var input in expiries.OrderBy(e => e.ExpiryAt))
        {
            var members = series.Where(s => s.ExpiryAt == input.ExpiryAt).OrderBy(s => s.Strike).ThenBy(s => s.Right).ToList();
            var (expiry, memberResults) = BuildExpiry(model, input, members, strikeInterval);
            builtExpiries.Add(expiry);
            results.AddRange(memberResults);
        }
        var live = builtExpiries.Where(e => e.Status != Expired).Select(e => e.ExpiryAt).ToHashSet();
        var axis = series.Where(s => live.Contains(s.ExpiryAt)).Select(s => s.Strike).Distinct().Order().ToImmutableArray();
        return new VolSurfaceResult(model.Id, axis, [.. builtExpiries], [.. results]);
    }

    private const string Expired = "option_expired";

    private static (SurfaceExpiry, List<SurfaceSeriesResult>) BuildExpiry(IVolModel model, SurfaceExpiryInput input, List<SurfaceSeries> members, ExactDecimal strikeInterval)
    {
        if (members.Count > 0 && members.All(m => m.Outcome == Expired))
            return (Gap(input, Expired, null), [.. members.Select(m => new SurfaceSeriesResult(m, null, null, null, false, null))]);
        if (input.Futures is not { } futures)
            return (Gap(input, "vol_fit_insufficient_strikes", null), [.. members.Select(m => new SurfaceSeriesResult(m, null, null, null, false, null))]);

        bool InTheMoney(SurfaceSeries m) => LatticeDecimal.From(m.Strike).CompareTo(futures) is var side && (m.Right == OptionRight.Call ? side < 0 : side > 0);
        LatticeDecimal Distance(ExactDecimal strike) => LatticeDecimal.Max(LatticeDecimal.From(strike) - futures, futures - LatticeDecimal.From(strike));
        ExactDecimal? atTheMoney = members.Count == 0 ? null : members.Select(m => m.Strike).Distinct().OrderBy(Distance).ThenByDescending(strike => strike).First();
        var moneyness = members.ToDictionary(m => m, m => (LatticeDecimal.From(m.Strike) / futures).Ln());
        var fitSeries = members.Where(m => m.MarketVolatility is not null && m.Right == OtmRight(m.Strike, futures)).ToList();
        var fit = model.Fit([.. fitSeries.Select(m => new SmilePoint(moneyness[m], m.MarketVolatility!.Value))]);

        if (fit is not FittedSmile fitted)
            return (Gap(input, ((SmileGap)fit).Code, atTheMoney), [.. members.Select(m => new SurfaceSeriesResult(m, moneyness[m], null, null, false, InTheMoney(m)))]);

        var results = members.Select(m =>
        {
            var fittedVol = m.MarketVolatility is null ? (LatticeDecimal?)null : model.Evaluate(fitted.Parameters, moneyness[m]);
            return new SurfaceSeriesResult(m, moneyness[m], fittedVol, m.MarketVolatility - fittedVol, fitSeries.Contains(m), InTheMoney(m));
        }).ToList();
        var grid = GridStrikes(fitSeries.Select(m => m.Strike).Order().ToList(), strikeInterval)
            .Select(strike =>
            {
                var k = (LatticeDecimal.From(strike) / futures).Ln();
                return new SurfaceGridPoint(strike, k, model.Evaluate(fitted.Parameters, k));
            }).ToImmutableArray();
        var expiry = new SurfaceExpiry(input.ExpiryAt, input.UnderlyingCode, futures, "fitted", fitted.Parameters, fitted.Points, fitted.RmsResidual, fitted.MaxAbsResidual, grid,
            atTheMoney) { DaysToExpiry = input.DaysToExpiry, YearFraction = input.YearFraction };
        return (expiry, results);
    }

    private static SurfaceExpiry Gap(SurfaceExpiryInput input, string code, ExactDecimal? atTheMoney) =>
        new(input.ExpiryAt, input.UnderlyingCode, input.Futures, code, null, 0, null, null, [], atTheMoney) { DaysToExpiry = input.DaysToExpiry, YearFraction = input.YearFraction };

    private static OptionRight OtmRight(ExactDecimal strike, LatticeDecimal futures) =>
        LatticeDecimal.From(strike).CompareTo(futures) < 0 ? OptionRight.Put : OptionRight.Call;

    // Lowest to highest fitted strike in exact interval steps; the highest strike is
    // the last point even when it is off the lattice, so the grid never steps past it.
    private static IEnumerable<ExactDecimal> GridStrikes(List<ExactDecimal> fittedStrikes, ExactDecimal interval)
    {
        var highest = fittedStrikes[^1];
        for (var strike = fittedStrikes[0]; strike.CompareTo(highest) < 0; strike += interval) yield return strike;
        yield return highest;
    }
}
