using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Options;

public sealed record SwingUnitEstimate(long Operations, long PeakBytes, int Steps, int MaximumPriceNodes,
    int MaximumStates, int MaximumChoices, long StateSlots, long Moves, long Branches, long PricedChoices,
    long Payoffs, long Normalizers, long PreflightVisits, DateTimeOffset CalculationAt)
{
    public string Policy => SwingUnitResources.Policy;
}

internal static class SwingUnitResources
{
    internal const string Policy = "swing_trinomial_6h_unit_reference_v2";
    internal const long MaximumOperations = 100_000_000;
    internal const long MaximumBytes = 256L << 20;
    internal const long Interval = 6 * TimeSpan.TicksPerHour;
    internal const int IntegerBits = 4096;
    private const int StateBytes = 4096;
    private const int LatticeBytes = 96;
    private const int EdgeBytes = 1536;

    internal static SwingUnitEstimate Booking(SwingQuantityRight right, ExactQuantity reference, DateTimeOffset at,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var periods = SwingFeasibility.Resolve(right, reference, [], at);
        // Elapsed days without elections can later reserve unknown quantities and
        // counts. Drop their terminal restriction for this structural upper bound.
        var structural = periods.Select(p => p.Days.Any(d => d.Day.ExerciseAt <= at)
            ? p with { AppliedLower = null, AppliedUpper = null } : p).ToImmutableArray();
        var possibleStochastic = structural.Where(p => !SwingChoice.Forced(p,
            p.Days.Count(d => d.Day.ExerciseAt > at))).ToHashSet();
        return Estimate(right, structural, at, possibleStochastic, token);
    }

    internal static SwingUnitEstimate Estimate(SwingQuantityRight right, ImmutableArray<SwingPeriodBudget> periods,
        DateTimeOffset at, IReadOnlySet<SwingPeriodBudget> stochastic, CancellationToken token)
    {
        var arithmetic = new SwingPlanArithmetic(IntegerBits);
        var clock = new Clock(right.Days, at);
        int previous = 0, maxStates = 1, maxChoices = 0;
        long slots = 0, moves = 0, branches = 0, choices = 0, payoffs = 0, normalizers = 0, rollingCapacity = 0, maximumEdges = 0;
        var months = new HashSet<(int, int)>();
        long preflight = right.Days.Length;
        int totalDays = 0;
        foreach (var period in periods)
        {
            var days = period.Days.Count(d => d.Election == null && d.Day.ExerciseAt > at);
            if (days == 0) continue;
            var bounds = SwingIndexBounds.From(period, days, arithmetic);
            var changes = bounds.Last.IsZero ? 0 : Math.Min(days, period.AvailableChanges ?? days);
            int depth = 0;
            slots++;
            foreach (var day in period.Days)
            {
                token.ThrowIfCancellationRequested();
                if (day.Election != null || day.Day.ExerciseAt <= at) continue;
                var (states, actions, fan) = Layer(depth);
                Require(states, MaximumBytes / StateBytes, "state slots");
                Require(actions, MaximumOperations, "candidate visits");
                long stateCount = (long)states, actionsCount = (long)actions;
                maxStates = Math.Max(maxStates, (int)states);
                maxChoices = Math.Max(maxChoices, (int)fan);
                moves += actionsCount;
                depth++;
                totalDays++;
                var successor = Layer(depth).States;
                Require(successor, MaximumBytes / StateBytes, "state slots");
                slots += (long)successor;
                maxStates = Math.Max(maxStates, (int)successor);
                if (stochastic.Contains(period))
                {
                    int step = clock.Index(day.Day.ExerciseAt);
                    long nodes = 2L * step + 1;
                    branches += 3L * ((long)step * step - (long)previous * previous) * stateCount;
                    choices += nodes * actionsCount;
                    payoffs += nodes * (bounds.Last >= actionsCount ? actionsCount : (long)bounds.Last + 1);
                    normalizers += step;
                    months.Add((day.Day.Day.Year, day.Day.Day.Month));
                    var active = Math.Max(stateCount, (long)successor);
                    rollingCapacity = Math.Max(rollingCapacity, (nodes + 2) * active);
                    maximumEdges = Math.Max(maximumEdges, actionsCount);
                    previous = step;
                }
                Require(Usage(), MaximumOperations, "operations");
                Require(Bytes(), MaximumBytes, "modeled live bytes");
            }

            // For a fixed number h of nonbase prefix moves, intersect its interval
            // with possible suffix completion. Combining all parent levels in each
            // interval only enlarges the per-parent admissible action range.
            (BigInteger States, BigInteger Actions, BigInteger Fan) Layer(int d)
            {
                int remaining = days - d;
                BigInteger states = 0, actions = 0, fan = 0;
                bool counted = right.ChangeCount.HasValue;
                for (int h = 0; h <= (counted ? Math.Min(d, changes) : 0); h++)
                {
                    token.ThrowIfCancellationRequested();
                    preflight++;
                    int allowance = counted ? changes - h : remaining;
                    BigInteger minimum = 0, maximum = 0;
                    if (right.HasTotal)
                    {
                        int variable = counted ? h : d;
                        var fixedPrefix = arithmetic.Product(d - variable, bounds.Rest);
                        var prefixHigh = arithmetic.Add(fixedPrefix, arithmetic.Product(variable, bounds.Last));
                        int k = Math.Min(remaining, allowance);
                        var fixedSuffix = arithmetic.Product(remaining - k, bounds.Rest);
                        var suffixMax = arithmetic.Add(fixedSuffix, arithmetic.Product(k, bounds.Last));
                        minimum = BigInteger.Max(fixedPrefix, arithmetic.Add(bounds.Lower, -suffixMax));
                        maximum = BigInteger.Min(prefixHigh, arithmetic.Add(bounds.Upper, -fixedSuffix));
                        if (minimum > maximum) continue;
                    }
                    var span = maximum - minimum;
                    if (span >= MaximumBytes / StateBytes) throw SwingPlanGrid.Scope("state slots", MaximumBytes / StateBytes);
                    long width = (long)span + 1;
                    states += width;
                    if (remaining == 0) continue;
                    BigInteger perState = 1;
                    if (allowance != 0)
                    {
                        var low = BigInteger.Zero;
                        var high = bounds.Last;
                        if (right.HasTotal)
                        {
                            var restK = Math.Min(remaining - 1, counted ? allowance - 1 : remaining - 1);
                            var fixedRest = arithmetic.Product(remaining - 1 - restK, bounds.Rest);
                            var maximumRest = arithmetic.Add(fixedRest, arithmetic.Product(restK, bounds.Last));
                            low = BigInteger.Max(low, arithmetic.Add(arithmetic.Add(bounds.Lower, -maximum), -maximumRest));
                            high = BigInteger.Min(high, arithmetic.Add(arithmetic.Add(bounds.Upper, -minimum), -fixedRest));
                        }
                        if (high - low >= MaximumOperations) throw SwingPlanGrid.Scope("candidate visits", MaximumOperations);
                        perState = high >= low ? (long)(high - low) + 1 : 0;
                        if (counted && (bounds.Rest < low || bounds.Rest > high)) perState++;
                    }
                    fan = BigInteger.Max(fan, perState);
                    Require(perState, MaximumOperations, "candidate visits");
                    actions += width * perState;
                }
                return (states, actions, fan);
            }
        }
        Require(Usage(), MaximumOperations, "operations");
        Require(Bytes(), MaximumBytes, "modeled live bytes");
        return new(Usage(), Bytes(), previous, 2 * previous + 1, maxStates, maxChoices, slots, moves, branches, choices, payoffs, normalizers, preflight, at);

        long Usage() => preflight + 4 * slots + 3 * moves + branches + choices + payoffs + normalizers +
            (2L * previous + 1) * months.Count + previous + totalDays;
        // 4096-bit integers use 512-byte payloads. Per state covers shared
        // levels, two hash tables, move bounds, scores, and plan backpointers.
        long Bytes() => ChargeBytes(slots, totalDays, previous, (2L * previous + 1) * months.Count, rollingCapacity, maximumEdges);
    }

    internal static long ChargeBytes(long slots, int days, int steps, long growthNodes, long rollingCapacity, long maximumEdges)
    {
        return 32768 + StateBytes * slots + 8192L * days + 256L * steps + LatticeBytes * (growthNodes + 2 * rollingCapacity) + EdgeBytes * maximumEdges;
    }

    internal static void Require(BigInteger amount, long limit, string dimension)
    {
        if (amount > limit) throw SwingPlanGrid.Scope(dimension, limit);
    }

    internal sealed class Clock
    {
        private readonly long origin, first;
        private readonly long[] splits;
        internal Clock(ImmutableArray<SwingDay> calendar, DateTimeOffset at)
        {
            origin = at.UtcTicks;
            var future = calendar.Where(d => d.ExerciseAt.UtcTicks > origin).Select(d => d.ExerciseAt.UtcTicks - origin);
            first = (future.FirstOrDefault() - 1) % Interval + 1;
            splits = [.. future.Where(t => (t - first) % Interval != 0)];
        }
        internal int Index(DateTimeOffset deadline)
        {
            var elapsed = deadline.UtcTicks - origin;
            long regular = elapsed < first ? 0 : (elapsed - first) / Interval + 1;
            int where = Array.BinarySearch(splits, elapsed);
            long index = regular + (where < 0 ? ~where : where + 1);
            // All prefix nodes must propagate one state and three branches.
            Require(3 * (BigInteger)index * index, MaximumOperations, "operations");
            return (int)index;
        }
        internal long[] Times(DateTimeOffset last)
        {
            var result = new long[Index(last) + 1];
            int extra = 0;
            long tick = first;
            for (int t = 1; t < result.Length; t++)
            {
                if (extra < splits.Length && splits[extra] < tick) result[t] = splits[extra++];
                else { result[t] = tick; tick += Interval; }
            }
            return result;
        }
    }
}
