using System.Collections.Immutable;
using OpenEtrm.MarketData.Options;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

public enum ExercisePeriod { DeliveryDay = 1, DeliveryMonth }
public enum ExerciseHolder { Us = 1, Counterparty }
public enum ExerciseSettlement { Physical = 1, CashAutomatic }

[EconomicDefinition("exercise_group_v1")]
public sealed record ExerciseGroup(
    [property: IdentityField] DateOnly From,
    [property: IdentityField] DateOnly To,
    [property: IdentityField] DateTimeOffset ExerciseAt);

[EconomicDefinition("exercise_right_v1")]
public sealed record ExerciseRight(
    [property: IdentityField] int Ordinal,
    [property: IdentityField] ImmutableArray<int> Slots,
    [property: IdentityField] ExercisePeriod Period,
    [property: IdentityField] ExerciseHolder Holder,
    [property: IdentityField] ExerciseSettlement Settlement,
    [property: IdentityField] string Model,
    [property: IdentityField] string DayCount,
    [property: IdentityField] ImmutableArray<ExerciseGroup> Groups);

public sealed record ExerciseElection(int Right, DateOnly From, string Outcome, DateTimeOffset EffectiveAt, Guid RevisionId, string Evidence)
{
    internal const string Exercised = "exercised", Abandoned = "abandoned";

    internal static void RequireFacts(ImmutableArray<ExerciseElection> elections)
    {
        if (elections.Any(e => e is null || e.Right < 0 || e.From == default || e.Outcome is not (Exercised or Abandoned) ||
            e.EffectiveAt == default || !TradingInstant.IsSupported(e.EffectiveAt) || e.RevisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(e.Evidence)) || elections.DistinctBy(e => (e.Right, e.From)).Count() != elections.Length)
            throw Invalid();
    }

    internal static void RequireRetainedGroups(ImmutableArray<ExerciseRight> rights, ImmutableArray<ExerciseElection> elections)
    {
        foreach (var election in elections)
            if (election.Right >= rights.Length || rights[election.Right].Settlement != ExerciseSettlement.Physical ||
                !rights[election.Right].Groups.Any(group => group.From == election.From && election.EffectiveAt <= group.ExerciseAt))
                throw Invalid();
    }

    private static MarketDataException Invalid() => new("exercise_election",
        "Each election requires a distinct retained physical exercise group, an outcome, an effective instant and identified evidence.");
}

internal sealed class ExerciseDecisions
{
    internal const string Undecided = "undecided", UndecidedForecast = "undecided_forecast";
    private readonly Dictionary<(int Right, DateOnly From), string> outcomes = [];
    private readonly Dictionary<int, ExerciseRight> memberRights = [];
    private readonly Dictionary<(int Right, DateOnly Day), ExerciseGroup> groups = [];

    internal static ExerciseDecisions Resolve<TMember>(ImmutableArray<ExerciseRight> rights, ImmutableArray<TMember> members,
        PhysicalCashState state, MarketSnapshot snapshot, DateTimeOffset at) where TMember : IRetainedProgramMember
    {
        var decisions = new ExerciseDecisions(rights, state.Elections, at);
        // Owner-day facts belong to physical memberships. A cash right may use an outside owner's schedule.
        if (state.OwnerDays.Any(day => (day.HasContractDeliveryRecord || !day.Evidence.IsEmpty) && !decisions.Counted(day.Slot, day.Day)))
            throw new MarketDataException("exercise_conflict", "Delivery records require an exercised day on the covered physical member.");
        decisions.RequireCompatiblePayments(members, state, snapshot, ExerciseSettlement.Physical);
        decisions.RequireDetermined(rights, at);
        return decisions;
    }

    private ExerciseDecisions(ImmutableArray<ExerciseRight> rights, ImmutableArray<ExerciseElection> elections, DateTimeOffset at)
    {
        foreach (var election in elections)
            if (election.EffectiveAt <= at) outcomes.Add((election.Right, election.From), election.Outcome);
        foreach (var right in rights)
        {
            foreach (var slot in right.Slots) memberRights.Add(slot, right);
            foreach (var group in right.Groups)
                for (var day = group.From; day < group.To; day = day.AddDays(1)) groups.Add((right.Ordinal, day), group);
        }
    }

    internal string Outcome(ExerciseRight right, ExerciseGroup group) => outcomes.GetValueOrDefault((right.Ordinal, group.From), Undecided);
    internal void DecideAutomatic(ExerciseRight right, ExerciseGroup group, bool exercised) =>
        outcomes.Add((right.Ordinal, group.From), exercised ? ExerciseElection.Exercised : ExerciseElection.Abandoned);
    internal bool Counted(int slot, DateOnly day) => DayOutcome(slot, day) is null or ExerciseElection.Exercised;
    internal ExerciseGroup? UndecidedGroup(int slot, DateOnly day) =>
        DayOutcome(slot, day) == Undecided ? groups[(memberRights[slot].Ordinal, day)] : null;
    private string? DayOutcome(int slot, DateOnly day) =>
        memberRights.TryGetValue(slot, out var right) && groups.TryGetValue((right.Ordinal, day), out var group) ? Outcome(right, group) : null;

    private void RequireDetermined(ImmutableArray<ExerciseRight> rights, DateTimeOffset at)
    {
        if (rights.Any(right => right.Settlement == ExerciseSettlement.Physical &&
            right.Groups.Any(group => group.ExerciseAt <= at && Outcome(right, group) == Undecided)))
            throw new MarketDataException("exercise_determination", "An elapsed physical exercise group requires its determination.");
    }

    internal void RequireCompatiblePayments<TMember>(ImmutableArray<TMember> members, PhysicalCashState state, MarketSnapshot snapshot,
        ExerciseSettlement settlement) where TMember : IRetainedProgramMember
    {
        foreach (var payment in state.VisiblePayments(snapshot))
        {
            if (!memberRights.TryGetValue(payment.Slot, out var right) || right.Settlement != settlement) continue;
            var row = members[payment.Slot].Program.Document.Instructions.Single(i =>
                i.Output.Operation == "output_guard" && i.Output.PaymentOrdinal == payment.Occurrence).Output;
            var from = row.DeliveryFrom!.Value;
            var to = row.DeliveryTo!.Value;
            if (!Enumerable.Range(0, to.DayNumber - from.DayNumber).Select(from.AddDays).Any(day => DayOutcome(payment.Slot, day) == ExerciseElection.Exercised))
                throw new MarketDataException("exercise_conflict", "A covered cash row requires an exercised day before it can carry a recorded payment.");
        }
    }
}

[EconomicDefinition("exercise_curve_volatility_v1")]
public sealed record CurveVolatilityMark([property: IdentityField] CurveKey Curve, [property: IdentityField] DateOnly Month,
    VolatilityInput Volatility)
{
    [IdentityField] public Guid Id => Volatility.Id;
    [IdentityField] public string Value => Volatility.Value;
    [IdentityField] public string Convention => Volatility.Convention;
}

[EconomicDefinition("exercise_curve_correlation_v1")]
public sealed record CurveCorrelationMark([property: IdentityField] CurveKey First, [property: IdentityField] CurveKey Second,
    [property: IdentityField] DateOnly Month, [property: IdentityField] string Correlation, [property: IdentityField] Guid RevisionId);

[EconomicDefinition("exercise_marks_v1")]
public sealed record ExerciseMarks(ImmutableArray<CurveVolatilityMark> Volatilities, ImmutableArray<CurveCorrelationMark> Correlations,
    [property: IdentityField] string Convention)
{
    public const string HeldLogReturn = "curve_marks_log_return_held_v1";
    [IdentityField] public ImmutableArray<CurveVolatilityMark> OrderedVolatilities => [..Volatilities
        .OrderBy(v => DefinitionIdentity.CanonicalTerm(v.Curve), StringComparer.Ordinal).ThenBy(v => v.Month)];
    [IdentityField] public ImmutableArray<CurveCorrelationMark> OrderedCorrelations => [..Correlations
        .Select(c => string.CompareOrdinal(c.First.Code, c.Second.Code) switch
        {
            < 0 => c,
            0 when string.CompareOrdinal(DefinitionIdentity.CanonicalTerm(c.First), DefinitionIdentity.CanonicalTerm(c.Second)) <= 0 => c,
            _ => c with { First = c.Second, Second = c.First }
        })
        .OrderBy(c => DefinitionIdentity.CanonicalTerm(c.First), StringComparer.Ordinal)
        .ThenBy(c => DefinitionIdentity.CanonicalTerm(c.Second), StringComparer.Ordinal).ThenBy(c => c.Month)];

    internal void Validate()
    {
        static bool ValidCurve(CurveKey c) => c.Revision.CatalogId != Guid.Empty && c.Revision.RevisionId != Guid.Empty &&
            !string.IsNullOrWhiteSpace(c.Code);
        if (Convention != HeldLogReturn || Volatilities.IsDefault || Correlations.IsDefault ||
            Volatilities.Any(v => v is null || !ValidCurve(v.Curve) || v.Volatility is null || v.Month == default || v.Month.Day != 1) ||
            Volatilities.DistinctBy(v => (v.Curve, v.Month)).Count() != Volatilities.Length ||
            Volatilities.DistinctBy(v => v.Volatility.Id).Count() != Volatilities.Length ||
            Correlations.Any(c => c is null || c.RevisionId == Guid.Empty || !ValidCurve(c.First) || !ValidCurve(c.Second) || c.First == c.Second ||
                c.Month == default || c.Month.Day != 1 || !ValidCorrelation(c.Correlation)) ||
            Correlations.DistinctBy(c => c.RevisionId).Count() != Correlations.Length ||
            Correlations.Any(c => Correlations.Count(other => Matches(other, c.First, c.Second, c.Month)) != 1))
            throw Missing();
    }
    private static bool ValidCorrelation(string? text)
    {
        if (text is null) return false;
        try
        {
            var value = LatticeDecimal.Parse(text);
            return value.CompareTo(LatticeDecimal.Parse("-1")) >= 0 && value.CompareTo(LatticeDecimal.Parse("1")) <= 0;
        }
        catch (MarketDataException) { return false; }
    }
    internal VolatilityInput Volatility(CurveKey curve, DateOnly month) =>
        Volatilities.SingleOrDefault(v => v.Month == month && v.Curve == curve)?.Volatility ?? throw Missing();
    internal LatticeDecimal Correlation(CurveKey a, CurveKey b, DateOnly month) =>
        LatticeDecimal.Parse(Correlations.SingleOrDefault(c => Matches(c, a, b, month))?.Correlation ?? throw Missing());
    private static bool Matches(CurveCorrelationMark mark, CurveKey a, CurveKey b, DateOnly month) =>
        mark.Month == month && (mark.First == a && mark.Second == b || mark.First == b && mark.Second == a);
    internal static MarketDataException Missing() =>
        new("exercise_marks", "Supply distinct curve and delivery-month marks under the held log-return convention.");
}

public sealed record ExerciseGroupValue(int Right, DateOnly From, DateOnly To, string State,
    ExactMoney ForwardSum, ExactMoney Counted, HolderChoiceInputs? Inputs);

public readonly record struct ExerciseGroupKey(int Right, DateOnly From, DateOnly To);
public sealed record ExerciseGroupContribution(ExerciseGroupKey Group, ExactMoney Value);
