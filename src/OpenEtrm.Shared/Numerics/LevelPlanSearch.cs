using System.Collections.Immutable;
using System.Numerics;

namespace OpenEtrm.Shared.Numerics;

public readonly record struct LevelPlanState(int Level, int Count);

public readonly struct LevelPlanLayer
{
    private readonly int minimumLevel;
    private readonly long rangeLength;
    private readonly ImmutableArray<LevelPlanState> coordinates;

    private LevelPlanLayer(int minimumLevel, long rangeLength, ImmutableArray<LevelPlanState> coordinates)
    {
        this.minimumLevel = minimumLevel;
        this.rangeLength = rangeLength;
        this.coordinates = coordinates;
    }

    // An inclusive range with no count dimension, indexed directly for storage.
    public static LevelPlanLayer Range(int minimumLevel, int maximumLevel)
    {
        if (minimumLevel > maximumLevel)
            throw new ArgumentOutOfRangeException(nameof(maximumLevel));
        return new(minimumLevel, (long)maximumLevel - minimumLevel + 1, default);
    }

    // Supply unique coordinates sorted by level, then remaining count. No range between them is implied.
    public static LevelPlanLayer Sparse(ImmutableArray<LevelPlanState> coordinates)
    {
        if (coordinates.IsDefault)
            throw new ArgumentException("Supply the reached coordinates.", nameof(coordinates));
        return new(0, 0, coordinates);
    }

    internal bool IsDefault => coordinates.IsDefault && rangeLength == 0;
    internal long Slots => coordinates.IsDefault ? rangeLength : coordinates.Length;
    internal int Size => (int)Slots;

    internal long CountLevels(CancellationToken cancellationToken)
    {
        if (coordinates.IsDefault)
            return rangeLength;

        var count = 0;
        for (var index = 0; index < coordinates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = coordinates[index];
            if (state.Count < 0 || (index > 0 && Compare(coordinates[index - 1], state) >= 0))
                throw new ArgumentException("Sparse coordinates must have nonnegative counts and be strictly ordered by level and count.");
            if (index == 0 || coordinates[index - 1].Level != state.Level)
                count++;
        }
        return count;
    }

    internal LevelPlanState StateAt(int index) => coordinates.IsDefault
        ? new(minimumLevel + index, 0)
        : coordinates[index];

    internal int IndexOf(LevelPlanState state)
    {
        if (coordinates.IsDefault)
        {
            var offset = (long)state.Level - minimumLevel;
            return state.Count == 0 && offset >= 0 && offset < rangeLength ? (int)offset : -1;
        }

        var low = 0;
        var high = coordinates.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var order = Compare(coordinates[middle], state);
            if (order == 0) return middle;
            if (order < 0) low = middle + 1;
            else high = middle - 1;
        }
        return -1;
    }

    private static int Compare(LevelPlanState left, LevelPlanState right)
    {
        var levels = left.Level.CompareTo(right.Level);
        return levels == 0 ? left.Count.CompareTo(right.Count) : levels;
    }
}

public readonly record struct LevelPlanCandidate(int Move, LevelPlanState Successor, BigInteger Reward);

// The level limit counts distinct levels per layer. State slots cover every layer, including terminal.
// The state budget also bounds the number of layers.
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
    public static LevelPlanResult Search(ImmutableArray<LevelPlanLayer> layers, LevelPlanState opening,
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
        var hasEmptyLayer = false;
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer.IsDefault)
                throw new ArgumentException("Supply the coordinates for each layer.", nameof(layers));
            var slots = layer.Slots;
            if (slots > Array.MaxLength || slots > budget.MaximumStates - stateSlots ||
                layer.CountLevels(cancellationToken) > budget.MaximumLevels)
                return WorkLimit;
            stateSlots += slots;
            largestLayer = Math.Max(largestLayer, (int)slots);
            hasEmptyLayer |= slots == 0;
        }
        var origin = layers[0].IndexOf(opening);
        if (origin < 0)
            throw new ArgumentOutOfRangeException(nameof(opening));
        cancellationToken.ThrowIfCancellationRequested();
        if (hasEmptyLayer)
            return Infeasible;

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
                    if (AdditionExceeds(candidate.Reward, suffix, budget.MaximumIntegerBits))
                        return WorkLimit;
                    var score = candidate.Reward + suffix;
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

    // Both operands have already passed the integer limit. Only same-sign
    // additions with at least one full-width operand can exceed it.
    private static bool AdditionExceeds(BigInteger left, BigInteger right, int bits)
    {
        if (left.Sign != right.Sign || left.IsZero) return false;
        var a = BigInteger.Abs(left);
        var b = BigInteger.Abs(right);
        if (a.GetBitLength() < bits && b.GetBitLength() < bits) return false;
        var highBit = BigInteger.One << (bits - 1);
        return a > highBit + (highBit - 1) - b;
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
