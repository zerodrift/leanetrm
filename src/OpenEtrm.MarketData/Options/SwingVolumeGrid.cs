using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Options;

// After d decisions, intersect the reachable cumulative totals with the totals
// from which the remaining days can still satisfy the term. Everything else is absent.
internal sealed class SwingVolumeGrid
{
    internal readonly record struct Range(int First, int Last)
    {
        internal int Count => Last - First + 1;
    }

    private readonly SwingChoiceInputs inputs;
    internal Range[] States { get; }
    internal long[] ActionCounts { get; }
    internal bool UniqueSchedule => States.All(s => s.Count == 1);

    internal SwingVolumeGrid(SwingChoiceInputs inputs, CancellationToken cancellation)
    {
        this.inputs = inputs;
        var n = inputs.Days.Length;
        States = new Range[n + 1];
        ActionCounts = new long[n];
        for (var d = 0; d <= n; d++)
        {
            var first = Math.Max(inputs.CommittedLots + (long)d * inputs.DailyMinimumLots,
                inputs.MinimumLots - (long)(n - d) * inputs.DailyMaximumLots);
            var last = Math.Min(inputs.CommittedLots + (long)d * inputs.DailyMaximumLots,
                inputs.MaximumLots - (long)(n - d) * inputs.DailyMinimumLots);
            if (first > last) throw new MarketDataException("swing_feasibility", "No feasible swing schedule remains.");
            RequireStateCount(last - first + 1);
            States[d] = new((int)first, (int)last);
        }
        for (var d = 0; d < n; d++)
        {
            cancellation.ThrowIfCancellationRequested();
            var states = States[d];
            for (var index = 0; index < states.Count; index++)
                ActionCounts[d] += Choices(d, states.First + index).Count;
        }
    }

    internal static void RequireStateCount(long count)
    {
        if (count > 2049) throw SwingChoice.Scope("A swing layer exceeds 2,049 reachable volume states.");
    }

    internal Range Choices(int day, int used) => new(
        Math.Max(inputs.DailyMinimumLots, States[day + 1].First - used),
        Math.Min(inputs.DailyMaximumLots, States[day + 1].Last - used));

    internal (SignedRatio Value, ImmutableArray<int> Lots) Intrinsic(CancellationToken cancellation)
    {
        // Only retain the exact values of the next layer and the quantities needed to recover the winning schedule.
        var next = Enumerable.Repeat(SignedRatio.Zero, States[^1].Count).ToArray();
        var selected = new int[inputs.Days.Length][];
        for (var d = inputs.Days.Length - 1; d >= 0; d--)
        {
            var amount = inputs.Days[d].Uncertain + inputs.Days[d].Certain;
            var states = States[d];
            var current = new SignedRatio[states.Count];
            selected[d] = new int[states.Count];
            for (var i = 0; i < states.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var used = states.First + i;
                var choices = Choices(d, used);
                for (var offset = 0; offset < choices.Count; offset++)
                {
                    var lots = choices.First + offset;
                    var value = amount * new SignedRatio(lots, 1) + next[used + lots - States[d + 1].First];
                    // Strict comparison in ascending order retains the smallest current quantity on ties.
                    if (offset == 0 || value.CompareTo(current[i]) > 0)
                    {
                        current[i] = value;
                        selected[d][i] = lots;
                    }
                }
            }
            next = current;
        }
        var plan = ImmutableArray.CreateBuilder<int>(inputs.Days.Length);
        var total = inputs.CommittedLots;
        for (var d = 0; d < inputs.Days.Length; d++)
        {
            var lots = selected[d][total - States[d].First];
            plan.Add(lots);
            total += lots;
        }
        return (next[0], plan.MoveToImmutable());
    }
}
