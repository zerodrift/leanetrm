using System.Text.Json;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasSwingOracleRunner
{
    private sealed record Day(string Uncertain, string Certain, string Volatility, int Seconds);
    private sealed record Case(Day[] Days, int Low, int High, int Minimum, int Maximum, int Committed);

    internal static void Run(string input, string output)
    {
        if (new FileInfo(input).Length > 65_536) throw new InvalidDataException("Oversized swing fixture file.");
        var cases = JsonSerializer.Deserialize<Case[]>(File.ReadAllText(input));
        if (cases is null || cases.Length is < 1 or > 32) throw new InvalidDataException("Expected 1–32 fixtures.");
        var results = new List<string>();
        var at = new DateTimeOffset(2041, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var fixture in cases)
        {
            if (fixture.Days.Length is < 1 or > 4 || fixture.Days.Any(d => d.Seconds is < 1 or > 2_764_800))
                throw new InvalidDataException("Expected 1–4 decisions within 32 days.");
            var inputs = new SwingChoiceInputs([.. fixture.Days.Select(d => new SwingPayoffDay(
                new(DateOnly.FromDateTime(at.AddSeconds(d.Seconds).UtcDateTime), at.AddSeconds(d.Seconds)),
                Read(d.Uncertain), Read(d.Certain), LatticeDecimal.Parse(d.Volatility)))], fixture.Low, fixture.High,
                fixture.Minimum, fixture.Maximum, fixture.Committed, at, SwingPricingBinding.SupportedNumericalPolicy);
            results.Add(HalfEven18.Report(SwingChoice.Value(inputs).Value));
        }
        File.WriteAllText(output, JsonSerializer.Serialize(results));
    }

    private static SignedRatio Read(string text) => text.Length <= 64 && SignedRatio.TryParse(text, out var value)
        ? value : throw new InvalidDataException("Expected a bounded decimal fixture amount.");
}
