using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasSwingWorkload
{
    private sealed record Sample(string Run, string Value, SwingWorkCounts Work, double ElapsedMilliseconds,
        long AllocatedBytes, long ResidentBeforeBytes, long ResidentAfterBytes);

    internal static void Run(string days, string maximum, string scenario, string output)
    {
        if (!int.TryParse(days, out var count) || count is not (1 or 3 or 31) ||
            !int.TryParse(maximum, out var lots) || lots is not (1 or 4) || scenario is not ("base" or "price" or "volatility"))
            throw new ArgumentException("Choose 1, 3 or 31 days, 1 or 4 lots and a base, price or volatility scenario.");

        var at = new DateTimeOffset(2041, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var forward = scenario == "price" ? 325 : 320;
        const int strike = 320;
        var volatility = scenario == "volatility" ? "0.55" : "0.50";
        var inputs = new SwingChoiceInputs([..Enumerable.Range(0, count).Select(i =>
        {
            var deadline = at.AddDays(i).AddHours(6);
            return new SwingPayoffDay(new(DateOnly.FromDateTime(deadline.UtcDateTime), deadline),
                new(forward, 1), new(-strike, 1), LatticeDecimal.Parse(volatility));
        })], 0, lots, 0, lots, 0, at, SwingPricingBinding.SupportedNumericalPolicy);

        using var process = Process.GetCurrentProcess();
        Sample Measure(string run)
        {
            process.Refresh();
            var resident = process.WorkingSet64;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            var value = SwingChoice.Value(inputs);
            var elapsed = Stopwatch.GetElapsedTime(start);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            process.Refresh();
            return new(run, HalfEven18.Report(value.Value), value.Work, elapsed.TotalMilliseconds, bytes,
                resident, process.WorkingSet64);
        }

        var cold = Measure("cold");
        var warm = Measure("warm");
        if (cold.Value != warm.Value || cold.Work != warm.Work || cold.Work.MaximumVolumeStates != lots + 1 ||
            cold.Work.TimeSteps != 1 + 4 * (count - 1) || cold.Work.EvaluatedActions <= 0)
            throw new InvalidDataException("Repeated swing valuation changed its result or evaluated an unexpected grid.");

        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            days = count, dailyMinimum = 0, dailyMaximum = lots, totalMinimum = 0, totalMaximum = lots,
            committed = 0, calculationAt = at, firstDeadline = inputs.Days[0].Day.ExerciseAt,
            lastDeadline = inputs.Days[^1].Day.ExerciseAt, dailyIntervalHours = 24, scenario,
            uncertainPerLot = forward, certainPerLot = -strike, volatility, policy = inputs.NumericalPolicy,
            runtime = RuntimeInformation.FrameworkDescription, samples = new[] { cold, warm }
        }));
    }
}
