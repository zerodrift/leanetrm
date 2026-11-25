using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

[EconomicDefinition("gas_swing_day_v1")]
public sealed record SwingDay(
    [property: IdentityField] DateOnly Day,
    [property: IdentityField] DateTimeOffset ExerciseAt);

[EconomicDefinition("gas_swing_right_v1")]
public sealed record SwingRight(
    [property: IdentityField] int Ordinal,
    [property: IdentityField] int PhysicalSlot,
    [property: IdentityField] ImmutableArray<int> Slots,
    [property: IdentityField] ExerciseHolder Holder,
    [property: IdentityField] ExactRatio IncrementRatio,
    [property: IdentityField] int DailyMinimumLots,
    [property: IdentityField] int DailyMaximumLots,
    [property: IdentityField] int MinimumLots,
    [property: IdentityField] int MaximumLots,
    [property: IdentityField] string QuantityPolicy,
    [property: IdentityField] ImmutableArray<SwingDay> Days)
{
    public const string ElectedDeliveryQuantity = "swing_elected_delivery_quantity_v1";
}

[EconomicDefinition("gas_swing_pricing_v1")]
public sealed record SwingPricingBinding(
    [property: IdentityField] int Right,
    [property: IdentityField] string Model,
    [property: IdentityField] string DayCount,
    [property: IdentityField] string NumericalPolicy,
    [property: IdentityField] string MarksConvention)
{
    public const string SupportedModel = "gas_swing_one_factor_lognormal_v1";
    public const string SupportedNumericalPolicy = "swing_trinomial_6h_half_even_18_v1";
}

public sealed record SwingElection(int Right, DateOnly Day, ExactQuantity Quantity, ExerciseHolder ExercisingParty,
    DateTimeOffset SubmittedAt, DateTimeOffset EffectiveAt, Guid RevisionId, string Evidence);

public sealed record SwingDayDecision(SwingDay Day, SwingElection? Election, int? Lots);

public sealed record SwingBudget(ImmutableArray<SwingDayDecision> Days, int CommittedLots, int RemainingMinimumLots,
    int RemainingMaximumLots);

public static partial class SwingFeasibility
{
    // The caller selects one fact for each day at the resolved cutoff. This check
    // only counts facts already effective at the valuation instant.
    public static SwingBudget Resolve(SwingRight right, ExactQuantity referenceDailyQuantity,
        ImmutableArray<SwingElection> elections, DateTimeOffset valuationAt)
    {
        RequireRight(right);
        var facts = SelectElections(right.Ordinal, right.Holder, right.Days, referenceDailyQuantity,
            elections, valuationAt, quantity => ElectionLots(right, referenceDailyQuantity, quantity));
        ImmutableArray<SwingDayDecision> decisions = [..right.Days.Select(day => facts.TryGetValue(day.Day, out var fact)
            ? new SwingDayDecision(day, fact.Election, fact.Quantity) : new(day, null, null))];
        var committed = decisions.Sum(day => (long)(day.Lots ?? 0));
        var (minimum, maximum) = RequireCompletion(right, committed, decisions.Count(day => day.Lots is null));
        // A feasible completion bounds the sum by the retained Int32 term maximum.
        return new(decisions, (int)committed, minimum, maximum);
    }

    private static Dictionary<DateOnly, (SwingElection Election, T Quantity)> SelectElections<T>(int ordinal,
        ExerciseHolder holder, ImmutableArray<SwingDay> days, ExactQuantity referenceDailyQuantity,
        ImmutableArray<SwingElection> elections, DateTimeOffset valuationAt, Func<ExactQuantity, T> readQuantity)
    {
        if (referenceDailyQuantity is null || referenceDailyQuantity.NumeratorValue.Sign <= 0)
            throw InvalidRight("Swing needs a positive reference daily quantity.");
        if (elections.IsDefault || valuationAt == default || !TradingInstant.IsSupported(valuationAt))
            throw InvalidElection("Supply the selected election facts and a supported valuation instant.");

        var calendar = days.ToDictionary(day => day.Day);
        var selected = new Dictionary<DateOnly, (SwingElection, T)>();
        var seen = new HashSet<DateOnly>();
        var revisions = new HashSet<Guid>();
        foreach (var election in elections)
        {
            if (election is null || election.Right != ordinal || election.ExercisingParty != holder ||
                !calendar.TryGetValue(election.Day, out var day) || election.SubmittedAt == default || election.EffectiveAt == default ||
                !TradingInstant.IsSupported(election.SubmittedAt) || !TradingInstant.IsSupported(election.EffectiveAt) ||
                election.SubmittedAt > election.EffectiveAt || election.EffectiveAt > day.ExerciseAt ||
                election.RevisionId == Guid.Empty || string.IsNullOrWhiteSpace(election.Evidence))
                throw InvalidElection("Each election requires its retained day, holder, timely submission and effectiveness, revision and evidence.");

            var quantity = readQuantity(election.Quantity);
            if (!revisions.Add(election.RevisionId) || !seen.Add(election.Day))
                throw InvalidElection("Supply one distinct selected election revision for each elected swing day.");
            if (election.EffectiveAt <= valuationAt)
                selected.Add(election.Day, (election, quantity));
        }
        return selected;
    }

    internal static void RequireRight(SwingRight right)
    {
        if (right is null || right.Ordinal < 0 || right.PhysicalSlot < 0 ||
            right.Slots.IsDefaultOrEmpty || right.Slots.Any(slot => slot < 0) || !right.Slots.Contains(right.PhysicalSlot) ||
            right.Slots.Distinct().Count() != right.Slots.Length || !Enum.IsDefined(right.Holder) ||
            right.IncrementRatio is null || right.IncrementRatio.Numerator != BigInteger.One ||
            right.IncrementRatio.Denominator < right.DailyMinimumLots || right.IncrementRatio.Denominator > right.DailyMaximumLots ||
            right.DailyMinimumLots < 0 || right.DailyMinimumLots > right.DailyMaximumLots ||
            right.MinimumLots < 0 || right.MinimumLots > right.MaximumLots ||
            right.QuantityPolicy != SwingRight.ElectedDeliveryQuantity || right.Days.IsDefaultOrEmpty || right.Days.Length > 31)
            throw InvalidRight("Select explicit swing terms with a positive on-grid reference quantity, ordered volume limits and one to 31 gas days.");

        RequireCalendar(right.Days);
        RequireCompletion(right, 0, right.Days.Length);
    }

    private static void RequireCalendar(ImmutableArray<SwingDay> days)
    {
        SwingDay? previous = null;
        foreach (var day in days)
        {
            if (day is null || day.Day == default || day.ExerciseAt == default || !TradingInstant.IsSupported(day.ExerciseAt) ||
                previous is not null && (day.Day.DayNumber != previous.Day.DayNumber + 1 || day.ExerciseAt <= previous.ExerciseAt))
                throw InvalidRight("Swing days and their deadlines must be ordered across consecutive gas days.");
            previous = day;
        }
    }

    private static int ElectionLots(SwingRight right, ExactQuantity reference, ExactQuantity election)
    {
        if (election is null || election.Specification != reference.Specification || election.NumeratorValue.Sign < 0)
            throw InvalidElection("A swing election requires a nonnegative quantity in its retained native unit.");

        // RequireRight establishes an increment of exactly 1/n of the daily reference.
        var lots = BigInteger.DivRem(election.NumeratorValue * reference.DenominatorValue * right.IncrementRatio.Denominator,
            election.DenominatorValue * reference.NumeratorValue, out var remainder);
        if (!remainder.IsZero || lots < right.DailyMinimumLots || lots > right.DailyMaximumLots)
            throw InvalidElection("A swing election must be exactly on the retained quantity grid and within the daily bounds.");
        return (int)lots;
    }

    private static (int Minimum, int Maximum) RequireCompletion(SwingRight right, long committed, int undecided)
    {
        var minimum = Math.Max(right.MinimumLots - committed, (long)undecided * right.DailyMinimumLots);
        var maximum = Math.Min(right.MaximumLots - committed, (long)undecided * right.DailyMaximumLots);
        if (minimum > maximum)
            throw new MarketDataException("swing_feasibility", "The retained elections and remaining days cannot meet the term volume limits.");
        return ((int)minimum, (int)maximum);
    }

    internal static MarketDataException InvalidRight(string message) => new("swing_right", message);
    private static MarketDataException InvalidElection(string message) => new("swing_election", message);
}
