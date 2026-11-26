using System.Text.Json;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static partial class GasSwingChecks
{
    private static void CancelWorkStages()
    {
        var right=Terms(1) with { Minimum=R(0),Maximum=R(512),Step=R(1),Upper=null,TotalBasis=null };
        var at=right.Days[0].ExerciseAt.AddHours(-6);
        foreach (var stage in new[] { SwingWorkStage.Graph,SwingWorkStage.ExactPlan,SwingWorkStage.Payoffs })
        {
            using var source=new CancellationTokenSource();
            var input=new SwingUnitChoiceInputs(right,SwingFeasibility.Resolve(right,Units(1),[],at),
                [new(right.Days[0],R(3),R(-3),D(stage==SwingWorkStage.Payoffs?"0.25":"0"))],R(1),at,Pricing);
            var latency=new System.Diagnostics.Stopwatch();
            try
            {
                SwingChoice.Value(input,source.Token,observeStage:visited =>
                {
                    if (visited!=stage || latency.IsRunning) return;
                    latency.Start();
                    source.Cancel();
                });
                throw new Exception($"The running {stage} did not cancel.");
            }
            catch (OperationCanceledException e) when (e.CancellationToken==source.Token) { }
            Require(latency.IsRunning && latency.Elapsed<TimeSpan.FromSeconds(1), $"Cancellation at {stage} exceeded one second.");
            Console.WriteLine($"PASS original token at an interior {stage} candidate in {latency.Elapsed.TotalMilliseconds:F2} ms.");
        }
    }

    internal static void UnitWorkload(int length, bool sampleHeap = false, string variant = "standard")
    {
        var inputs = LongInputs(length, variant == "reserved" ? 45 : -1);
        if (variant is "dense" or "dst" or "missed")
        {
            var right = inputs.Right;
            if (variant == "dense")
                right = right with { Minimum = R(7,10), Step = R(1,100), ChangeCount = null, CountRule = null };
            if (variant == "dst")
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                var first = new DateOnly(2041,3,9);
                right = right with { Days = [..Enumerable.Range(0,length).Select(i =>
                {
                    var day = first.AddDays(i);
                    var utc = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone);
                    return new SwingDay(day,new DateTimeOffset(utc).AddHours(-6));
                })] };
                Require(right.Days[2].ExerciseAt - right.Days[1].ExerciseAt == TimeSpan.FromHours(23),
                    "New York March 10 to 11 spans 23 hours.");
            }
            var at = variant == "missed" ? right.Days[0].ExerciseAt.AddHours(42) : right.Days[0].ExerciseAt.AddHours(-6);
            var periods = SwingFeasibility.Resolve(right, Units(1000), [], at);
            inputs = inputs with { Right = right, Periods = periods, CalculationAt = at,
                Days = [..periods.Where(p=>!p.Days.Any(d=>d.Day.ExerciseAt<=at)).SelectMany(p=>p.Days)
                    .Select(d=>new SwingUnitPayoffDay(d.Day,R(d.Day.Day.Month%2==1 ? 295 : 305,100),R(-3),
                        D(d.Day.Day.Month%2==1 ? "0.20" : "0.25")))] };
        }
        if (length == 1 && variant is "prefix" or "memory")
        {
            var right = Terms(1) with { Minimum = R(0), Maximum = R(variant == "memory" ? 174728 : 1), Step = R(1), Upper = null, TotalBasis = null };
            var at = right.Days[0].ExerciseAt.AddHours(-6 * (variant == "memory" ? 1 : 5771));
            inputs = new(right, SwingFeasibility.Resolve(right, Units(1), [], at), [new(right.Days[0], R(3), R(-3), D("0.01"))],
                R(1), at, Pricing);
        }
        if (sampleHeap)
        {
            // Run apart from timed measurements. Collect at interior production
            // boundaries to distinguish rooted managed storage from GC churn.
            var payload = new byte[512];
            Array.Fill(payload, (byte)255);
            var integers = new System.Numerics.BigInteger[1024];
            long allocation = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < integers.Length; i++) integers[i] = new(payload, isUnsigned: true);
            long limbBytes = (GC.GetAllocatedBytesForCurrentThread() - allocation) / integers.Length;
            GC.KeepAlive(integers);
            long baseline = GC.GetTotalMemory(true), maximum = 0, nextSample = 1;
            int samples = 0;
            var solved = SwingChoice.Value(inputs, default, branches =>
            {
                if (branches < nextSample) return;
                maximum = Math.Max(maximum, GC.GetTotalMemory(true) - baseline);
                samples++;
                nextSample = branches + 1_000_000;
            });
            Require(maximum <= solved.Estimate.PeakBytes, "Sampled rooted managed growth exceeded the modeled bound.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Length = length,
                Samples = samples,
                LargestHeapIncrease = maximum,
                LimbAllocationBytes = limbBytes,
                Modeled = solved.Work.PeakBytes,
                Estimated = solved.Estimate.PeakBytes
            }));
            return;
        }
        for (int run = 0; run < 2; run++)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            var result = SwingChoice.Value(inputs);
            bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            Require(result.Work.PeakBytes <= result.Estimate.PeakBytes && result.Work.Operations <= result.Estimate.Operations && result.Work.Branches <= result.Estimate.Branches &&
                result.Work.PricedChoices <= result.Estimate.PricedChoices && result.Work.Payoffs <= result.Estimate.Payoffs &&
                result.Work.Moves <= result.Estimate.Moves && result.Work.StateSlots <= result.Estimate.StateSlots &&
                result.Work.MaximumStates <= result.Estimate.MaximumStates && result.Work.MaximumChoices <= result.Estimate.MaximumChoices &&
                result.Work.Normalizers <= result.Estimate.Normalizers, "Structural bounds must cover every measured work category.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Length = length,
                Variant = variant,
                Run = run,
                UniqueContracts = 1,
                Scenarios = 1,
                PriceNodes = (long)(result.Work.FactorSteps + 1) * (result.Work.FactorSteps + 1),
                result.Work.GrowthNodes,
                result.Work.MaximumChoices,
                result.Work.MaximumStates,
                result.Work.RollingCapacity,
                result.Work.CompiledMoves,
                Milliseconds = timer.Elapsed.TotalMilliseconds,
                AllocatedBytes = bytes,
                Value = HalfEven18.Report(result.CountedToUs),
                result.Estimate,
                result.Work.Branches,
                result.Work.PricedChoices,
                result.Work.StateSlots,
                result.Work.Moves,
                result.Work.Payoffs,
                result.Work.Normalizers,
                result.Work.Operations,
                result.Work.PeakBytes
            }));
        }
    }
}
