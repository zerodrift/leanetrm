using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Options;

// Each step has node order j = -step .. +step. A decision inserted between
// regular ticks splits the transition without moving the six-hour anchors.
internal sealed record SwingPriceLattice(LatticeDecimal[][] Transitions, int[] DecisionDays)
{
    private const long IntervalTicks = 6 * TimeSpan.TicksPerHour;
    internal const int MaximumSteps = 1600;
    internal static readonly LatticeDecimal One = Integer(1);
    private static readonly LatticeDecimal Jump = (Integer(3 * 21600) / Integer(365 * 86400)).Sqrt();

    internal static LatticeDecimal Integer(long value) =>
        LatticeDecimal.From(ExactDecimal.FromScaled(value * ExactDecimal.WorkingScale));

    internal static SwingPriceLattice Grid(SwingChoiceInputs inputs)
    {
        var start = inputs.CalculationAt.UtcTicks;
        var deadlines = inputs.Days.Select(d => d.Day.ExerciseAt.UtcTicks - start).ToArray();
        var last = deadlines[^1];
        if ((last - 1) / IntervalTicks + 1 > MaximumSteps)
            throw SwingChoice.Scope("The six-hour swing grid exceeds 1,600 time steps.");
        var times = new SortedSet<long>(deadlines) { 0 };
        var firstTick = (deadlines[0] - 1) % IntervalTicks + 1;
        for (var tick = firstTick; tick <= last; tick += IntervalTicks) times.Add(tick);
        if (times.Count - 1 > MaximumSteps) throw SwingChoice.Scope("Inserting swing deadlines exceeds 1,600 time steps.");

        var steps = new LatticeDecimal[times.Count - 1][];
        var decisions = new int[times.Count];
        Array.Fill(decisions, -1);
        var ticks = times.ToArray();
        for (var day = 0; day < deadlines.Length; day++)
            decisions[Array.BinarySearch(ticks, deadlines[day])] = day;
        for (var t = 0; t < steps.Length; t++)
        {
            var side = Integer(ticks[t + 1] - ticks[t]) / Integer(6 * IntervalTicks);
            steps[t] = [side, One - (side + side), side];
        }
        return new(steps, decisions);
    }

    internal LatticeDecimal[][] Payoffs(SwingChoiceInputs inputs, CancellationToken cancellation)
    {
        var amounts = new LatticeDecimal[DecisionDays.Length][];
        Array.Fill(amounts, []);
        for (var step = 1; step < DecisionDays.Length; step++)
        {
            var d = DecisionDays[step];
            if (d < 0) continue;
            var day = inputs.Days[d];
            var uncertain = ModelAmount(day.Uncertain);
            var certain = ModelAmount(day.Certain);
            var logJump = day.Volatility!.Value * Jump;
            var up = logJump.Exp();
            var down = (default(LatticeDecimal) - logJump).Exp();
            var normalizer = One;
            for (var t = 0; t < step; t++)
            {
                cancellation.ThrowIfCancellationRequested();
                var p = Transitions[t];
                normalizer *= p[0] * down + p[1] + p[2] * up;
            }
            var nodes = new LatticeDecimal[2 * step + 1];
            for (var j = -step; j <= step; j++)
            {
                cancellation.ThrowIfCancellationRequested();
                var relative = (logJump * Integer(j)).Exp() / normalizer;
                if (relative == default) throw SwingChoice.Scope("A positive swing forward multiplier underflowed.");
                nodes[step + j] = uncertain * relative + certain;
            }
            amounts[step] = nodes;
        }
        return amounts;
    }

    private static LatticeDecimal ModelAmount(SignedRatio amount) => LatticeDecimal.From(ExactDecimal.FromScaled(
        IntegerRounding.RoundQuotient(amount.Numerator * ExactDecimal.WorkingScale, amount.Denominator, MidpointRounding.ToEven)));
}
