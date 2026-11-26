using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Options;

// One interval for changed choices and the separate base choice. Ranks are
// local to a state, ordered by distance to the exact base index, then lower
// index. This lets the integer search use rest rank zero even on enormous grids.
internal readonly record struct SwingPlanMoves(BigInteger First, BigInteger Last, BigInteger Rest, bool RestAllowed, int Count)
{
    internal BigInteger IndexAt(int rank)
    {
        if ((uint)rank >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(rank));
        if (RestAllowed)
        {
            if (rank == 0) return Rest;
            rank--;
        }
        if (Last <= Rest) return BigInteger.Min(Last, Rest - 1) - rank;
        if (First >= Rest) return BigInteger.Max(First, Rest + 1) + rank;
        var pairs = BigInteger.Min(Rest - First, Last - Rest);
        if (rank / 2 < pairs)
        {
            var distance = 1 + rank / 2;
            return rank % 2 == 0 ? Rest - distance : Rest + distance;
        }
        var tailDistance = rank - pairs + 1;
        return Rest - First > Last - Rest ? Rest - tailDistance : Rest + tailDistance;
    }
}

internal sealed class SwingPlanGrid
{
    internal SwingPeriodBudget Period { get; }
    internal ImmutableArray<SwingDay> Days { get; }
    internal LevelPlanBudget Budget { get; }
    internal LevelPlanState Opening { get; }
    internal BigInteger TerminalMinimum { get; }
    internal BigInteger TerminalMaximum { get; }
    internal ImmutableArray<ImmutableArray<LevelPlanState>> Coordinates { get; }
    internal int MaximumChoices { get; }
    internal ImmutableArray<long> ActionCounts { get; }
    private readonly ImmutableArray<Dictionary<LevelPlanState,SwingPlanMoves>> choices;
    private readonly ImmutableArray<LevelCoordinates> levels;
    private readonly BigInteger last, rest;
    private readonly bool hasTotal;
    private readonly bool hasCount;
    private readonly SwingPlanArithmetic arithmetic;

    // Only reached levels have an address. The encoded Int32 level has no
    // commercial magnitude and is meaningful only in this particular layer.
    private sealed class LevelCoordinates
    {
        private readonly List<BigInteger> values = [];
        private readonly Dictionary<BigInteger,int> indexes = [];
        internal BigInteger Value(int index) => values[index];
        internal int Index(BigInteger value) => indexes[value];
        internal bool TryIndex(BigInteger value, out int index) => indexes.TryGetValue(value, out index);
        internal int Intern(BigInteger value, int limit)
        {
            if (indexes.TryGetValue(value, out var index)) return index;
            if (values.Count == limit) throw Scope("distinct levels", limit);
            index = values.Count;
            indexes.Add(value, index);
            values.Add(value);
            return index;
        }
    }

    internal static SwingPlanGrid Build(SwingPeriodBudget period, LevelPlanBudget budget, CancellationToken cancellation = default) =>
        new(period, budget, cancellation);

    private SwingPlanGrid(SwingPeriodBudget period, LevelPlanBudget budget, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (budget.MaximumLevels < 1 || budget.MaximumCandidates < 0 || budget.MaximumStates < 1 || budget.MaximumIntegerBits < 1)
            throw new ArgumentOutOfRangeException(nameof(budget));
        Period = period;
        Budget = budget;
        arithmetic = new(budget.MaximumIntegerBits);
        var right = period.Right;
        hasTotal = right.HasTotal;
        hasCount = right.ChangeCount.HasValue;
        int eligible = 0;
        foreach (var day in period.Days)
        {
            cancellation.ThrowIfCancellationRequested();
            if (day.Election != null) continue;
            if (day.Day.ExerciseAt > period.AssessmentAt) eligible++;
            else if (hasTotal || hasCount)
                throw new MarketDataException("swing_determination", "A missed election leaves the coupled limit period unassessed.");
        }
        if (eligible + 1L > budget.MaximumStates) throw Scope("state slots", budget.MaximumStates);
        Days = [..period.Days.Where(d => d.Election is null && d.Day.ExerciseAt > period.AssessmentAt).Select(d => d.Day)];
        var bounds = SwingIndexBounds.From(period, eligible, arithmetic);
        (last, rest, TerminalMinimum, TerminalMaximum) = bounds;
        Opening = new(0, Math.Min(Days.Length, period.AvailableChanges ?? 0));

        var layers = ImmutableArray.CreateBuilder<ImmutableArray<LevelPlanState>>(Days.Length + 1);
        var levelMaps = ImmutableArray.CreateBuilder<LevelCoordinates>(Days.Length + 1);
        var origin = new LevelCoordinates();
        origin.Intern(0, budget.MaximumLevels);
        levelMaps.Add(origin);
        layers.Add([Opening]);
        var moves = ImmutableArray.CreateBuilder<Dictionary<LevelPlanState,SwingPlanMoves>>(Days.Length);
        var actionCounts = ImmutableArray.CreateBuilder<long>(Days.Length);
        long stateCount = 1, visits = 0;
        for (var day = 0; day < Days.Length; day++)
        {
            var from = new Dictionary<LevelPlanState,SwingPlanMoves>();
            var next = new SortedSet<LevelPlanState>(StateOrder);
            var nextLevels = new LevelCoordinates();
            long actions = 0;
            foreach (var state in layers[day])
            {
                cancellation.ThrowIfCancellationRequested();
                var level = levelMaps[day].Value(state.Level);
                var feasible = FeasibleMoves(level, state.Count, Days.Length - day - 1, budget.MaximumCandidates - visits);
                MaximumChoices = Math.Max(MaximumChoices,feasible.Count);
                visits += feasible.Count;
                actions += feasible.Count;
                from.Add(state, feasible);
                for (var rank = 0; rank < feasible.Count; rank++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var index = feasible.IndexAt(rank);
                    var nextLevel = hasTotal ? arithmetic.Add(level, index) : BigInteger.Zero;
                    var count = RemainingCount(state.Count, index);
                    if (nextLevels.TryIndex(nextLevel, out var encoded) && next.Contains(new(encoded, count))) continue;
                    if (stateCount == budget.MaximumStates || next.Count == Array.MaxLength)
                        throw Scope("state slots", Math.Min(budget.MaximumStates, Array.MaxLength));
                    stateCount++;
                    next.Add(new(nextLevels.Intern(nextLevel, budget.MaximumLevels), count));
                }
            }
            var coordinates = ImmutableArray.CreateBuilder<LevelPlanState>(next.Count);
            foreach (var coordinate in next)
            {
                cancellation.ThrowIfCancellationRequested();
                coordinates.Add(coordinate);
            }
            layers.Add(coordinates.MoveToImmutable());
            levelMaps.Add(nextLevels);
            moves.Add(from);
            actionCounts.Add(actions);
        }
        Coordinates = layers.MoveToImmutable();
        levels = levelMaps.MoveToImmutable();
        choices = moves.MoveToImmutable();
        ActionCounts = actionCounts.MoveToImmutable();
    }

    private static readonly IComparer<LevelPlanState> StateOrder = Comparer<LevelPlanState>.Create((a,b) =>
        a.Level == b.Level ? a.Count.CompareTo(b.Count) : a.Level.CompareTo(b.Level));

    internal SwingPlanMoves Moves(int day, LevelPlanState state) => choices[day][state];
    internal BigInteger ExactLevel(int day, LevelPlanState state) => levels[day].Value(state.Level);

    internal LevelPlanState Successor(int day, LevelPlanState state, BigInteger index)
    {
        var exactLevel = hasTotal ? arithmetic.Add(ExactLevel(day, state), index) : BigInteger.Zero;
        return new(levels[day + 1].Index(exactLevel), RemainingCount(state.Count, index));
    }

    private int RemainingCount(int count, BigInteger index) => hasCount && index != rest ? count - 1 : count;

    private SwingPlanMoves FeasibleMoves(BigInteger level, int count, int remaining, long visitsLeft)
    {
        // A suffix with k changes reaches every index from its all-base level
        // minus k daily decrements to that level plus k daily increments.
        (BigInteger Low, BigInteger High) Range(int allowance)
        {
            if (hasCount && allowance < 0) return (1,0);
            if (!hasTotal) return (0,last);
            var k = hasCount ? Math.Min(remaining, allowance) : remaining;
            var fixedBase = arithmetic.Product(remaining - k, rest);
            var suffixMaximum = arithmetic.Add(fixedBase, arithmetic.Product(k, last));
            var low = BigInteger.Max(0, arithmetic.Add(arithmetic.Add(TerminalMinimum, -level), -suffixMaximum));
            var high = BigInteger.Min(last, arithmetic.Add(arithmetic.Add(TerminalMaximum, -level), -fixedBase));
            return low > high ? (1,0) : (low, high);
        }
        var same = Range(count);
        var changed = hasCount ? Range(count - 1) : same;
        var restAllowed = same.Low <= rest && rest <= same.High;
        var span = changed.High - changed.Low;
        if (span > visitsLeft) throw Scope("candidate visits", Budget.MaximumCandidates);
        long size = span.Sign < 0 ? 0 : (long)span;
        // When the base lies inside, the changed interval already has exactly
        // span remaining ranks after its removal.
        if (span.Sign >= 0 && !(changed.Low <= rest && rest <= changed.High))
        {
            if (size == visitsLeft) throw Scope("candidate visits", Budget.MaximumCandidates);
            size++;
        }
        if (restAllowed)
        {
            if (size == visitsLeft) throw Scope("candidate visits", Budget.MaximumCandidates);
            size++;
        }
        if (size == 0) throw NoCompletion();
        if (size > visitsLeft) throw Scope("candidate visits", Budget.MaximumCandidates);
        if (size > int.MaxValue) throw Scope("candidate ranks per state", int.MaxValue);
        return new(changed.Low, changed.High, rest, restAllowed, (int)size);
    }

    internal static MarketDataException NoCompletion() => new("swing_plan_infeasible", "The admitted swing period has no grid completion.");
    internal static MarketDataException Scope(string dimension, long limit) =>
        new("swing_numerical_scope", $"Swing planning exceeds the {dimension} limit of {limit}.");
}
