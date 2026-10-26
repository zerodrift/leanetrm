using System.Collections.Immutable;
using System.Numerics;
using System.Text.RegularExpressions;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Shared.Analytics;

public sealed record OverlayPoint(DateTimeOffset At, string? Value);
public sealed record OverlayRequest(string Code, IReadOnlyDictionary<string, int> Parameters);
public sealed record OverlayValue(DateTimeOffset At, string? Value, string? Reason);
public sealed record OverlayResult(string Code, IReadOnlyDictionary<string, int> Parameters, string Policy,
    IReadOnlyDictionary<string, ImmutableArray<OverlayValue>> Series);
public sealed record OverlayParameter(string Name, string Label, int Minimum, int Maximum);
public sealed record OverlayDefinition(string Code, string Label, ImmutableArray<OverlayParameter> Parameters, ImmutableArray<string> SeriesNames);

// Code is overlay_parameter (request shape) or overlay_input (series content).
public sealed class OverlayException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Chart overlays over an ordered series of exact decimals. Arithmetic is exact and
// signed; each reported value is rounded once, half to even, at 18 decimal places.
public static partial class Overlays
{
    public const string Policy = "exact_rational_half_even_18_v1";
    private const string Simple = "simple_moving_average_v1", Exponential = "exponential_moving_average_v1", Range = "rolling_range_v1";
    private const string Incomplete = "window_incomplete";
    private const int MaxRequests = 4, Scale = 18;

    private static ImmutableArray<OverlayParameter> WindowParameter { get; } = [new("window", "Window", 2, 250)];

    public static ImmutableArray<OverlayDefinition> Catalog { get; } =
    [
        new(Simple, "Simple moving average", WindowParameter, ["value"]),
        new(Exponential, "Exponential moving average", WindowParameter, ["value"]),
        new(Range, "Rolling high and low", WindowParameter, ["high", "low"]),
    ];

    public static ImmutableArray<OverlayResult> Compute(ImmutableArray<OverlayPoint> series, ImmutableArray<OverlayRequest> requests)
    {
        if (requests.Length > MaxRequests)
            throw new OverlayException("overlay_parameter", $"At most {MaxRequests} overlays can be requested.");
        var windows = requests.Select(Window).ToArray();
        var values = Parse(series);
        return [.. requests.Zip(windows, (request, window) => new OverlayResult(request.Code,
            request.Parameters.ToDictionary(p => p.Key, p => p.Value), Policy, Evaluate(request.Code, window, series, values)))];
    }

    private static int Window(OverlayRequest request)
    {
        var definition = Catalog.FirstOrDefault(d => d.Code == request.Code)
            ?? throw new OverlayException("overlay_parameter", $"Unknown overlay {request.Code}.");
        var allowed = definition.Parameters.Single();
        if (request.Parameters.Count != 1 || !request.Parameters.TryGetValue(allowed.Name, out var window))
            throw new OverlayException("overlay_parameter", $"{request.Code} requires exactly the parameter {allowed.Name}.");
        if (window < allowed.Minimum || window > allowed.Maximum)
            throw new OverlayException("overlay_parameter", $"{allowed.Name} must be {allowed.Minimum} to {allowed.Maximum}.");
        return window;
    }

    private static Ratio?[] Parse(ImmutableArray<OverlayPoint> series)
    {
        for (var i = 1; i < series.Length; i++)
            if (series[i].At <= series[i - 1].At)
                throw new OverlayException("overlay_input", "Series instants must be strictly increasing.");
        return [.. series.Select(p => p.Value is null ? (Ratio?)null : Ratio.Parse(p.Value))];
    }

    private static IReadOnlyDictionary<string, ImmutableArray<OverlayValue>> Evaluate(
        string code, int window, ImmutableArray<OverlayPoint> series, Ratio?[] values)
    {
        ImmutableArray<OverlayValue> Build(Func<int, Ratio?> at) =>
            [.. series.Select((p, i) => at(i) is { } v ? new OverlayValue(p.At, v.Format(), null) : new OverlayValue(p.At, null, Incomplete))];
        // The window ending at i, or empty when it is short or touches a missing point.
        Ratio[] Window(int i) => i >= window - 1 && values.Skip(i - window + 1).Take(window).All(v => v is not null)
            ? [.. values.Skip(i - window + 1).Take(window).Select(v => v!.Value)] : [];

        switch (code)
        {
            case Simple:
                return Single(Build(i => Window(i) is { Length: > 0 } w ? Ratio.Sum(w) / window : null));
            case Range:
                return new Dictionary<string, ImmutableArray<OverlayValue>>
                {
                    ["high"] = Build(i => Window(i) is { Length: > 0 } w ? w.Max() : null),
                    ["low"] = Build(i => Window(i) is { Length: > 0 } w ? w.Min() : null),
                };
            default:
                var ema = new Ratio?[values.Length];
                var alpha = new Ratio(2, window + 1);
                var retained = new Ratio(window - 1, window + 1);
                for (var i = 0; i < values.Length; i++)
                {
                    if (values[i] is not { } x) continue;
                    if (i > 0 && ema[i - 1] is { } previous) ema[i] = alpha * x + retained * previous;
                    else if (Window(i) is { Length: > 0 } w) ema[i] = Ratio.Sum(w) / window;
                }
                return Single(Build(i => ema[i]));
        }
    }

    private static Dictionary<string, ImmutableArray<OverlayValue>> Single(ImmutableArray<OverlayValue> value) => new() { ["value"] = value };

    // Signed reduced ratio with a positive denominator; ExactRatio is positive-only.
    private readonly partial record struct Ratio : IComparable<Ratio>
    {
        private readonly BigInteger numerator, denominator;

        public Ratio(BigInteger numerator, BigInteger denominator)
        {
            var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
            (this.numerator, this.denominator) = (numerator / gcd, denominator / gcd);
        }

        public static Ratio Parse(string text)
        {
            if (!Decimal().IsMatch(text))
                throw new OverlayException("overlay_input", $"'{text}' is not a plain decimal number.");
            var places = text.Contains('.') ? text.Length - text.IndexOf('.') - 1 : 0;
            return new(BigInteger.Parse(text.Replace(".", "")), BigInteger.Pow(10, places));
        }

        public static Ratio Sum(IEnumerable<Ratio> terms) => terms.Aggregate(new Ratio(0, 1), (sum, term) => sum + term);
        public static Ratio operator +(Ratio a, Ratio b) => new(a.numerator * b.denominator + b.numerator * a.denominator, a.denominator * b.denominator);
        public static Ratio operator *(Ratio a, Ratio b) => new(a.numerator * b.numerator, a.denominator * b.denominator);
        public static Ratio operator /(Ratio a, int divisor) => new(a.numerator, a.denominator * divisor);
        public int CompareTo(Ratio other) => (numerator * other.denominator).CompareTo(other.numerator * denominator);

        public string Format()
        {
            var scaled = IntegerRounding.RoundQuotient(numerator * BigInteger.Pow(10, Scale), denominator, MidpointRounding.ToEven);
            var digits = BigInteger.Abs(scaled).ToString().PadLeft(Scale + 1, '0');
            var whole = digits[..^Scale];
            var fraction = digits[^Scale..].TrimEnd('0');
            return (scaled.Sign < 0 ? "-" : "") + (fraction.Length == 0 ? whole : $"{whole}.{fraction}");
        }

        [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
        private static partial Regex Decimal();
    }
}
