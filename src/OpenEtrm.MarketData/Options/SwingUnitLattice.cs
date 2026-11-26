using System.Numerics;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Options;

internal static class SwingPriceLattice
{
    private static readonly LatticeDecimal One = Integer(1);
    private static readonly LatticeDecimal Jump = (Integer(3 * 21600) / Integer(365 * 86400)).Sqrt();
    private static LatticeDecimal Integer(long value) =>
        LatticeDecimal.From(ExactDecimal.FromScaled(value * ExactDecimal.WorkingScale));

    private sealed record UnitEdges(int[] Start, (int Action, int Next)[] Moves,
        (LatticeDecimal Physical, LatticeDecimal Fixed)[] Amounts);

    internal static SignedRatio UnitRoot(SwingUnitChoiceInputs inputs, List<SwingUnitDecision> decisions,
        SwingUnitWork work, Action<long>? reportBranches, CancellationToken token)
    {
        var clock = new SwingUnitResources.Clock(inputs.Right.Days, inputs.CalculationAt);
        var times = clock.Times(decisions[^1].Payoff.Day.ExerciseAt);
        var steps = times.Length - 1;
        work.FactorSteps = steps;
        var atStep = new SwingUnitDecision?[times.Length];
        int capacity = 0, maxStates = 1;
        foreach (var decision in decisions)
        {
            int step = clock.Index(decision.Payoff.Day.ExerciseAt);
            atStep[step] = decision;
            int before = decision.Grid.Coordinates[decision.Day].Length;
            int after = decision.Day + 1 == decision.Grid.Days.Length ? 1 : decision.Grid.Coordinates[decision.Day + 1].Length;
            capacity = Math.Max(capacity, checked((2 * step + 3) * Math.Max(before, after)));
            maxStates = Math.Max(maxStates, Math.Max(before, after));
        }

        var probabilities = new (LatticeDecimal Side, LatticeDecimal Middle)[steps];
        for (int i = 0; i < steps; i++)
        {
            token.ThrowIfCancellationRequested();
            var side = Integer(times[i + 1] - times[i]) / Integer(6 * SwingUnitResources.Interval);
            probabilities[i] = (side, One - (side + side));
        }
        // Cache exp(sigma*h*j) verbatim at its absolute factor coordinate.
        var growth = new Dictionary<LatticeDecimal, LatticeDecimal[]>();
        foreach (var group in decisions.GroupBy(d => d.Payoff.Volatility!.Value))
        {
            var jump = group.Key * Jump;
            int last = clock.Index(group.Last().Payoff.Day.ExerciseAt);
            var values = new LatticeDecimal[2 * last + 1];
            for (int j = -last; j <= last; j++)
            {
                token.ThrowIfCancellationRequested();
                values[last + j] = (jump * Integer(j)).Exp();
                if (values[last + j] == default) throw SwingChoice.Scope("A positive swing growth factor underflowed.");
            }
            work.GrowthNodes += values.Length;
            growth.Add(group.Key, values);
        }

        work.RollingCapacity = capacity;
        var next = new LatticeDecimal[capacity];
        var current = new LatticeDecimal[capacity];
        var expectation = new LatticeDecimal[maxStates];
        int nextStates = 1;
        for (int step = steps; step >= 0; step--)
        {
            token.ThrowIfCancellationRequested();
            var decision = atStep[step];
            var edges = decision == null ? null : Edges(decision, work, token);
            var prices = decision == null ? null : growth[decision.Payoff.Volatility!.Value];
            var normalizer = One;
            if (prices != null)
            {
                int center = prices.Length / 2;
                for (int t = 0; t < step; t++)
                {
                    token.ThrowIfCancellationRequested();
                    var (side, middle) = probabilities[t];
                    normalizer *= side * prices[center - 1] + middle + side * prices[center + 1];
                }
                work.Normalizers += step;
            }
            int states = edges == null ? nextStates : edges.Start.Length - 1;
            var rewards = edges == null ? null : new LatticeDecimal[edges.Amounts.Length];
            int branchBlock = 0;
            for (int node = 0; node < 2 * step + 1; node++)
            {
                token.ThrowIfCancellationRequested();
                if (step < steps)
                {
                    var (side, middle) = probabilities[step];
                    int child = node * nextStates;
                    for (int s = 0; s < nextStates; s++, child++)
                    {
                        expectation[s] = side * next[child] + middle * next[child + nextStates] + side * next[child + 2 * nextStates];
                        branchBlock += 3;
                        if (branchBlock >= 768)
                        {
                            work.BranchBlock(branchBlock, reportBranches);
                            branchBlock = 0;
                            token.ThrowIfCancellationRequested();
                        }
                    }
                }
                if (edges == null)
                {
                    Array.Copy(expectation, 0, current, node * states, states);
                    continue;
                }
                var multiplier = prices![prices.Length / 2 + node - step] / normalizer;
                if (multiplier == default) throw SwingChoice.Scope("A positive swing forward multiplier underflowed.");
                for (int r = 0; r < edges.Amounts.Length; r++)
                {
                    if ((r & 255) == 0) token.ThrowIfCancellationRequested();
                    var amount = edges.Amounts[r];
                    rewards![r] = amount.Physical * multiplier + amount.Fixed;
                }
                work.Payoffs += edges.Amounts.Length;
                for (int s = 0; s < states; s++)
                {
                    LatticeDecimal best = default;
                    for (int m = edges.Start[s]; m < edges.Start[s + 1]; m++)
                    {
                        if ((m & 255) == 0) token.ThrowIfCancellationRequested();
                        var move = edges.Moves[m];
                        var value = rewards![move.Action] + expectation[move.Next];
                        if (m == edges.Start[s] || value.CompareTo(best) > 0) best = value;
                    }
                    current[node * states + s] = best;
                }
                work.PricedChoices += edges.Moves.Length;
            }
            work.BranchBlock(branchBlock, reportBranches);
            (next, current) = (current, next);
            nextStates = states;
        }
        return new(next[0].ToExact().ScaledValue, ExactDecimal.WorkingScale);
    }

    private static UnitEdges Edges(SwingUnitDecision decision, SwingUnitWork work, CancellationToken token)
    {
        var (grid, day, payoff) = decision;
        var arithmetic = new SwingPlanArithmetic(grid.Budget.MaximumIntegerBits);
        var terms = grid.Period.Right;
        var sign = new SignedRatio(terms.Holder == ExerciseHolder.Us ? 1 : -1, 1);
        var physical = arithmetic.Multiply(payoff.PhysicalToUs, sign);
        var fixedAmount = arithmetic.Multiply(payoff.FixedToUs, sign);
        var before = grid.Coordinates[day];
        var after = grid.Coordinates[day + 1];
        var indexes = new Dictionary<LevelPlanState, int>(after.Length);
        for (int s = 0; s < after.Length; s++)
        {
            token.ThrowIfCancellationRequested();
            indexes.Add(after[s], s);
        }
        var start = new int[before.Length + 1];
        var moves = new (int, int)[checked((int)grid.ActionCounts[day])];
        var actions = new Dictionary<BigInteger, int>();
        var amounts = new List<(LatticeDecimal, LatticeDecimal)>();
        int edge = 0;
        for (int s = 0; s < before.Length; s++)
        {
            start[s] = edge;
            var choices = grid.Moves(day, before[s]);
            for (int rank = 0; rank < choices.Count; rank++)
            {
                token.ThrowIfCancellationRequested();
                var i = choices.IndexAt(rank);
                if (!actions.TryGetValue(i, out int action))
                {
                    var ratio = arithmetic.Add(terms.Minimum, arithmetic.Multiply(terms.Step, new(i, 1)));
                    action = amounts.Count;
                    actions.Add(i, action);
                    amounts.Add((Rounded(arithmetic.Multiply(physical, ratio)), Rounded(arithmetic.Multiply(fixedAmount, ratio))));
                }
                moves[edge++] = (action, day + 1 == grid.Days.Length ? 0 : indexes[grid.Successor(day, before[s], i)]);
            }
        }
        work.CompiledMoves += edge;
        work.MaximumEdges = Math.Max(work.MaximumEdges, edge);
        start[^1] = edge;
        return new(start, moves, amounts.ToArray());

        LatticeDecimal Rounded(SignedRatio amount) => LatticeDecimal.From(ExactDecimal.FromScaled(arithmetic.ToModelAmount(amount)));
    }
}
