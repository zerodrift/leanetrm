using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

[EconomicDefinition("gas_swing_day_v1")]
public sealed record SwingDay(
    [property: IdentityField] DateOnly Day,
    [property: IdentityField] DateTimeOffset ExerciseAt);

[EconomicDefinition("gas_swing_pricing_v1")]
public sealed record SwingPricingBinding(
    [property: IdentityField] int Right,
    [property: IdentityField] string Model,
    [property: IdentityField] string DayCount,
    [property: IdentityField] string NumericalPolicy,
    [property: IdentityField] string MarksConvention)
{
    public const string SupportedModel = "gas_swing_one_factor_lognormal_v1";
}

public sealed record SwingElection(int Right, DateOnly Day, ExactQuantity? Quantity, ExerciseHolder ExercisingParty,
    DateTimeOffset SubmittedAt, DateTimeOffset EffectiveAt, Guid RevisionId, string Evidence);

public static partial class SwingFeasibility
{
    private static Dictionary<DateOnly, (SwingElection Revision, SignedRatio? Quantity)> SelectElections(int ordinal,
        ExerciseHolder holder, ImmutableArray<SwingDay> days, ExactQuantity referenceDailyQuantity,
        ImmutableArray<SwingElection> elections, DateTimeOffset valuationAt, Func<ExactQuantity, SignedRatio> readQuantity)
    {
        if (referenceDailyQuantity is null || referenceDailyQuantity.NumeratorValue.Sign <= 0)
            throw InvalidRight("Swing needs a positive reference daily quantity.");
        if (elections.IsDefault || valuationAt == default || !TradingInstant.IsSupported(valuationAt))
            throw InvalidElection("Supply the selected election facts and a supported valuation instant.");

        var calendar = days.ToDictionary(day => day.Day);
        var selected = new Dictionary<DateOnly, (SwingElection, SignedRatio?)>();
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

            SignedRatio? quantity = election.Quantity is {} amount ? readQuantity(amount) : null;
            if (!revisions.Add(election.RevisionId) || !seen.Add(election.Day))
                throw InvalidElection("Supply one distinct selected election revision for each elected swing day.");
            if (election.EffectiveAt <= valuationAt)
                selected.Add(election.Day, (election, quantity));
        }
        return selected;
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

    internal static MarketDataException InvalidRight(string message) => new("swing_right", message);
    private static MarketDataException InvalidElection(string message) => new("swing_election", message);
}
