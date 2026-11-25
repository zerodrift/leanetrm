using System.Collections.Immutable;
using System.Numerics;

namespace OpenEtrm.Shared.Numerics;

public readonly record struct LevelPlanState(int Level, int Count);

// Levels are inclusive. Count ranges from zero to MaximumCount, with one coordinate when count is unused.
public readonly record struct LevelPlanBounds(int MinimumLevel, int MaximumLevel, int MaximumCount)
{
    internal long Levels => (long)MaximumLevel - MinimumLevel + 1;
    internal int Size => (int)(Levels * (MaximumCount + 1L));

    internal LevelPlanState StateAt(int index) =>
        new(MinimumLevel + index % (int)Levels, index / (int)Levels);

    internal int IndexOf(LevelPlanState state) =>
        state.Level < MinimumLevel || state.Level > MaximumLevel || state.Count < 0 || state.Count > MaximumCount
            ? -1
            : state.Count * (int)Levels + (state.Level - MinimumLevel);
}

public readonly record struct LevelPlanCandidate(int Move, LevelPlanState Successor, BigInteger Reward);

// State slots cover every layer, including terminal. They bound the stored choices and ordered output.
public readonly record struct LevelPlanBudget(int MaximumLevels, long MaximumCandidates, long MaximumStates, int MaximumIntegerBits);

public enum LevelPlanStatus { Optimal, Infeasible, WorkLimit }

public sealed record LevelPlanResult(LevelPlanStatus Status, ImmutableArray<int> Moves, BigInteger? Score);

public static class LevelPlanSearch
{
    private readonly record struct Choice(int Move, int Successor);
    private static readonly LevelPlanResult Infeasible = new(LevelPlanStatus.Infeasible, [], null);
    private static readonly LevelPlanResult WorkLimit = new(LevelPlanStatus.WorkLimit, [], null);

    // There is one deterministic candidate per move from a state. Each candidate advances one layer.
    // The caller declares a bound on all visits, including candidates whose successors are infeasible.
    public static LevelPlanResult Search(ImmutableArray<LevelPlanBounds> layers, LevelPlanState opening,
        Func<int, LevelPlanState, IEnumerable<LevelPlanCandidate>> candidates, Func<int, int> restMove,
        Func<LevelPlanState, bool> feasibleTerminal, long candidateUpperBound, LevelPlanBudget budget,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (layers.IsDefaultOrEmpty)
            throw new ArgumentException("Supply at least the terminal layer.", nameof(layers));
        ArgumentOutOfRangeException.ThrowIfNegative(candidateUpperBound);
        if (budget.MaximumLevels <= 0 || budget.MaximumCandidates < 0 || budget.MaximumStates <= 0 || budget.MaximumIntegerBits <= 0)
            throw new ArgumentOutOfRangeException(nameof(budget));
        if (candidateUpperBound > budget.MaximumCandidates || layers.Length > budget.MaximumStates)
            return WorkLimit;

        long stateSlots = 0;
        var largestLayer = 0;
        foreach (var bounds in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bounds.MinimumLevel > bounds.MaximumLevel || bounds.MaximumCount < 0)
                throw new ArgumentException("Each layer requires nonempty level and count ranges.", nameof(layers));
            if (bounds.Levels > budget.MaximumLevels)
                return WorkLimit;
            // The level budget bounds Levels to Int32, and count values to Int32.MaxValue + 1.
            var slots = bounds.Levels * (bounds.MaximumCount + 1L);
            if (slots > Array.MaxLength || slots > budget.MaximumStates - stateSlots)
                return WorkLimit;
            stateSlots += slots;
            largestLayer = Math.Max(largestLayer, (int)slots);
        }
        var origin = layers[0].IndexOf(opening);
        if (origin < 0)
            throw new ArgumentOutOfRangeException(nameof(opening));

        var days = layers.Length - 1;
        var choices = new Choice[days][];
        var after = new BigInteger?[largestLayer];
        var current = days > 0 ? new BigInteger?[largestLayer] : [];
        var terminal = layers[^1];
        for (var index = 0; index < terminal.Size; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (feasibleTerminal(terminal.StateAt(index)))
                after[index] = BigInteger.Zero;
        }

        long visited = 0;
        for (var day = days - 1; day >= 0; day--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var from = layers[day];
            var to = layers[day + 1];
            var rest = restMove(day);
            var chosen = choices[day] = new Choice[from.Size];
            for (var index = 0; index < chosen.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BigInteger? best = null;
                Choice choice = default;
                foreach (var candidate in candidates(day, from.StateAt(index)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (visited == candidateUpperBound || TooWide(candidate.Reward, budget))
                        return WorkLimit;
                    visited++;

                    var successor = to.IndexOf(candidate.Successor);
                    if (successor < 0 || after[successor] is not { } suffix)
                        continue;
                    var score = candidate.Reward + suffix;
                    if (TooWide(score, budget))
                        return WorkLimit;
                    if (best is { } previous)
                    {
                        var order = score.CompareTo(previous);
                        if (order < 0 || (order == 0 && !Prefer(candidate.Move, choice.Move, rest)))
                            continue;
                    }
                    best = score;
                    choice = new(candidate.Move, successor);
                }
                current[index] = best;
                chosen[index] = choice;
            }
            (after, current) = (current, after);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (after[origin] is not { } objective)
            return Infeasible;
        var moves = ImmutableArray.CreateBuilder<int>(days);
        var at = origin;
        foreach (var layer in choices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var choice = layer[at];
            moves.Add(choice.Move);
            at = choice.Successor;
        }
        return new(LevelPlanStatus.Optimal, moves.MoveToImmutable(), objective);
    }

    private static bool TooWide(BigInteger value, LevelPlanBudget budget) =>
        BigInteger.Abs(value).GetBitLength() > budget.MaximumIntegerBits;

    private static bool Prefer(int move, int previous, int rest)
    {
        var distance = Math.Abs((long)move - rest);
        var previousDistance = Math.Abs((long)previous - rest);
        return distance < previousDistance || (distance == previousDistance && move < previous);
    }
}
