using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Options;

public sealed record SwingPayoffDay(SwingDay Day, SignedRatio Uncertain, SignedRatio Certain, LatticeDecimal? Volatility);

public sealed record SwingChoiceInputs(ImmutableArray<SwingPayoffDay> Days, int DailyMinimumLots, int DailyMaximumLots,
    int MinimumLots, int MaximumLots, int CommittedLots, DateTimeOffset CalculationAt, string NumericalPolicy);

public sealed record SwingWorkCounts(int TimeSteps, int MaximumPriceNodes, int MaximumVolumeStates, long EvaluatedActions);

public sealed record SwingChoiceResult(SignedRatio Value, SignedRatio IntrinsicValue, ImmutableArray<int> IntrinsicLots,
    SwingWorkCounts Work);

public static class SwingChoice
{
    public static SwingChoiceResult Value(SwingChoiceInputs inputs, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Validate(inputs);
        var volumes = new SwingVolumeGrid(inputs, cancellation);
        if (!volumes.UniqueSchedule && inputs.Days.Any(d => d.Volatility is null))
            throw new MarketDataException("exercise_marks", "Each undecided swing month requires an explicit volatility.");
        var work = new SwingWorkCounts(0, 1, volumes.States.Max(s => s.Count), volumes.ActionCounts.Sum());
        var deterministic = volumes.UniqueSchedule || inputs.Days.All(d => d.Volatility == default(LatticeDecimal));
        var grid = deterministic ? null : SwingPriceLattice.Grid(inputs);
        if (grid is not null) work = CountWork(grid, volumes, work);
        var (intrinsic, lots) = volumes.Intrinsic(cancellation);
        if (grid is null) return new(intrinsic, intrinsic, lots, work);
        try
        {
            var value = Induct(inputs, grid, grid.Payoffs(inputs, cancellation), volumes, cancellation);
            return new(value, intrinsic, lots, work);
        }
        catch (MarketDataException e) when (e.Code == "numeric_range")
        {
            throw new MarketDataException("swing_numerical_scope", "Swing valuation exceeds the supported numeric magnitude.", e);
        }
    }

    private static void Validate(SwingChoiceInputs inputs)
    {
        if (inputs is null || inputs.Days.IsDefault || inputs.CalculationAt == default || !TradingInstant.IsSupported(inputs.CalculationAt))
            throw new MarketDataException("swing_model_scope", "Supply ordered swing day inputs and a supported UTC calculation instant.");
        if (inputs.NumericalPolicy != SwingPricingBinding.SupportedNumericalPolicy || inputs.Days.Length > 31 ||
            (long)inputs.DailyMaximumLots - inputs.DailyMinimumLots + 1 > 33)
            throw Scope("Select the supported swing numerical policy with at most 31 days and 33 quantities per day.");
        if (inputs.DailyMinimumLots < 0 || inputs.DailyMaximumLots < inputs.DailyMinimumLots || inputs.MinimumLots < 0 ||
            inputs.MaximumLots < inputs.MinimumLots || inputs.CommittedLots < 0)
            throw new MarketDataException("swing_feasibility", "Supply ordered nonnegative daily, term and committed lots.");
        SwingDay? previous = null;
        var marks = new Dictionary<(int Year, int Month), LatticeDecimal>();
        foreach (var payoff in inputs.Days)
        {
            if (payoff?.Day is not { } day || day.Day == default || day.ExerciseAt == default ||
                !TradingInstant.IsSupported(day.ExerciseAt) || payoff.Uncertain.Denominator.Sign <= 0 ||
                payoff.Certain.Denominator.Sign <= 0 ||
                previous is not null && (day.Day <= previous.Day || day.ExerciseAt <= previous.ExerciseAt))
                throw new MarketDataException("swing_model_scope", "Swing needs ordered distinct days, deadlines and signed per-lot economics.");
            if (day.ExerciseAt <= inputs.CalculationAt)
                throw new MarketDataException("swing_determination", "An undecided swing deadline has elapsed.");
            if (payoff.Volatility is { } vol)
            {
                var month = (day.Day.Year, day.Day.Month);
                if (vol.CompareTo(default) < 0 || marks.TryGetValue(month, out var existing) && existing != vol)
                    throw new MarketDataException("exercise_marks", "A swing month requires one consistent nonnegative volatility.");
                marks[month] = vol;
            }
            previous = day;
        }
    }

    internal static MarketDataException Scope(string detail) => new("swing_numerical_scope", detail);

    private static SwingWorkCounts CountWork(SwingPriceLattice grid, SwingVolumeGrid volumes, SwingWorkCounts intrinsic)
    {
        var nodes = 1;
        var actions = intrinsic.EvaluatedActions;
        var decided = 0;
        for (var t = 0; t < grid.DecisionDays.Length; t++)
        {
            var day = grid.DecisionDays[t];
            var perNode = day < 0 ? volumes.States[decided].Count : volumes.ActionCounts[day];
            if (day >= 0) decided++;
            actions += nodes * perNode;
            if (actions > 20_000_000) throw Scope("Swing valuation exceeds 20,000,000 reachable node/state/actions.");
            if (t < grid.Transitions.Length) nodes += grid.Transitions[t].Length - 1;
        }
        return intrinsic with { TimeSteps = grid.Transitions.Length, MaximumPriceNodes = nodes, EvaluatedActions = actions };
    }

    // The tiny independent tree supplies transition probabilities in child-node order and
    // per-lot payoffs for decision layers. Empty payoff arrays identify nondecision layers.
    internal static SignedRatio Induct(SwingChoiceInputs inputs, LatticeDecimal[][] transitions, LatticeDecimal[][] payoffs)
    {
        var nextDay = 0;
        var grid = new SwingPriceLattice(transitions, [..payoffs.Select(p => p.Length == 0 ? -1 : nextDay++)]);
        var volumes = new SwingVolumeGrid(inputs, default);
        return Induct(inputs, grid, payoffs, volumes, default);
    }

    private static SignedRatio Induct(SwingChoiceInputs inputs, SwingPriceLattice grid, LatticeDecimal[][] payoffs,
        SwingVolumeGrid volumes, CancellationToken cancellation)
    {
        var dayCount = inputs.Days.Length;
        var nodes = 1 + grid.Transitions.Sum(p => p.Length - 1);
        var lotFactors = Enumerable.Range(inputs.DailyMinimumLots, inputs.DailyMaximumLots - inputs.DailyMinimumLots + 1)
            .Select(k => SwingPriceLattice.Integer(k)).ToArray();
        LatticeDecimal[] next = [];
        for (var t = grid.Transitions.Length; t >= 0; t--)
        {
            var after = volumes.States[dayCount];
            var decision = grid.DecisionDays[t] >= 0;
            if (decision) dayCount--;
            var before = volumes.States[dayCount];
            var current = new LatticeDecimal[nodes * before.Count];
            var continuation = new LatticeDecimal[after.Count];
            for (var node = 0; node < nodes; node++)
            {
                cancellation.ThrowIfCancellationRequested();
                // At the final deadline only feasible terminal states exist; each has zero continuation.
                if (t < grid.Transitions.Length)
                    Expectation(node, grid.Transitions[t], next, continuation);
                if (!decision)
                {
                    continuation.CopyTo(current, node * before.Count);
                    continue;
                }
                var payoff = payoffs[t][node];
                for (var i = 0; i < before.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var used = before.First + i;
                    var choices = volumes.Choices(dayCount, used);
                    var best = default(LatticeDecimal);
                    for (var offset = 0; offset < choices.Count; offset++)
                    {
                        var k = choices.First + offset;
                        var candidate = lotFactors[k - inputs.DailyMinimumLots] * payoff + continuation[used + k - after.First];
                        if (offset == 0 || candidate.CompareTo(best) > 0) best = candidate;
                    }
                    current[node * before.Count + i] = best;
                }
            }
            next = current;
            if (t > 0) nodes -= grid.Transitions[t - 1].Length - 1;
        }
        var rounded = next[0].ToExact();
        return new(rounded.ScaledValue, ExactDecimal.WorkingScale);
    }

    private static void Expectation(int node, LatticeDecimal[] probabilities, LatticeDecimal[] next,
        LatticeDecimal[] continuation)
    {
        for (var i = 0; i < continuation.Length; i++)
        {
            var value = default(LatticeDecimal);
            for (var b = 0; b < probabilities.Length; b++)
                value += probabilities[b] * next[(node + b) * continuation.Length + i];
            continuation[i] = value;
        }
    }
}
