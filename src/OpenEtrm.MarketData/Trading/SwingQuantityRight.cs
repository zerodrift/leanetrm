using System.Collections.Immutable;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// Each quantity is a reduced fraction of one reference-day parcel on PhysicalSlot.
[EconomicDefinition("gas_swing_quantity_right_v1")]
internal sealed record SwingQuantityRight(
    [property: IdentityField] int Ordinal,
    [property: IdentityField] int PhysicalSlot,
    [property: IdentityField] ImmutableArray<int> Slots,
    [property: IdentityField] ExerciseHolder Holder,
    [property: IdentityField] SignedRatio Minimum,
    [property: IdentityField] SignedRatio Maximum,
    [property: IdentityField] SignedRatio Step,
    [property: IdentityField] string Period,
    [property: IdentityField] SignedRatio? Lower,
    [property: IdentityField] SignedRatio? Upper,
    [property: IdentityField] string? TotalBasis,
    [property: IdentityField] int? ChangeCount,
    [property: IdentityField] string? CountRule,
    [property: IdentityField] string QuantityPolicy,
    [property: IdentityField] string PlanRule,
    [property: IdentityField] string DeadlineRule,
    [property: IdentityField] int LeadHours,
    [property: IdentityField] ImmutableArray<SwingDay> Days)
{
    internal const string Term = "term", Month = "delivery_month", PerDay = "per_day_v1", PerPeriod = "per_period_v1";
    internal const string DiffersFromBase = "differs_from_base_v1", Elected = "swing_elected_quantity_v1";
    internal const string ForwardPlan = "best_plan_on_forwards_v1", Deadline = "lead_before_first_gas_day_v1";
    internal static readonly SignedRatio Base = new(1, 1);
    internal bool HasTotal => Lower.HasValue || Upper.HasValue;

    internal SignedRatio IndexOf(SignedRatio aboveMinimum) => aboveMinimum * new SignedRatio(Step.Denominator, Step.Numerator);
}

internal sealed record SwingQuantityDay(SwingDay Day, SwingElection? Election, SignedRatio? QuantityRatio);

internal sealed record SwingPeriodBudget(SwingQuantityRight Right, ImmutableArray<SwingQuantityDay> Days,
    DateTimeOffset AssessmentAt, SignedRatio? AppliedLower, SignedRatio? AppliedUpper, SignedRatio Elected,
    SignedRatio RemainingMinimum, SignedRatio RemainingMaximum, int ChangedDays, int? AvailableChanges);

public static partial class SwingFeasibility
{
    internal static ImmutableArray<SwingPeriodBudget> Resolve(SwingQuantityRight right, ExactQuantity reference,
        ImmutableArray<SwingElection> elections, DateTimeOffset assessmentAt)
    {
        RequireQuantityTerms(right);
        var facts = SelectElections(right.Ordinal, right.Holder, right.Days, reference, elections, assessmentAt, quantity =>
        {
            if (quantity is null || quantity.Specification != reference.Specification)
                throw InvalidElection("Supply the swing election in its retained native unit.");
            var ratio = new SignedRatio(quantity.NumeratorValue, quantity.DenominatorValue) *
                new SignedRatio(reference.DenominatorValue, reference.NumeratorValue);
            if (ratio.CompareTo(right.Minimum) < 0 || ratio.CompareTo(right.Maximum) > 0)
                throw InvalidElection("The swing election lies outside the retained daily range.");
            return ratio;
        });
        var periods = right.Days.GroupBy(d => right.Period == SwingQuantityRight.Term ? 0 : d.Day.Year * 12 + d.Day.Month);
        return [..periods.Select(period => ResolvePeriod(right, [..period.Select(day => facts.TryGetValue(day.Day, out var fact)
            ? new SwingQuantityDay(day, fact.Election, fact.Quantity) : new(day, null, null))], assessmentAt))];
    }

    private static SwingPeriodBudget ResolvePeriod(SwingQuantityRight right, ImmutableArray<SwingQuantityDay> days, DateTimeOffset at)
    {
        var multiplier = new SignedRatio(right.TotalBasis == SwingQuantityRight.PerDay ? days.Length : 1, 1);
        var lower = right.Lower * multiplier;
        var upper = right.Upper * multiplier;
        var elected = SignedRatio.Zero;
        var open = 0;
        var changed = 0;
        foreach (var day in days)
        {
            if (day.QuantityRatio is not {} quantity) { open++; continue; }
            elected += quantity;
            if (quantity != SwingQuantityRight.Base) changed++;
        }
        var available = right.ChangeCount - changed;
        if (available < 0)
            throw new MarketDataException("swing_feasibility", "Elections exceed the changed-day count in this period.");
        var k = new SignedRatio(Math.Min(open, available ?? open), 1);
        var atBase = new SignedRatio(open, 1);
        var minimum = atBase - k * (SwingQuantityRight.Base - right.Minimum);
        var maximum = atBase + k * (right.Maximum - SwingQuantityRight.Base);
        if (lower is {} l && (l - elected).CompareTo(minimum) > 0) minimum = l - elected;
        if (upper is {} u && (u - elected).CompareTo(maximum) < 0) maximum = u - elected;
        if (minimum.CompareTo(maximum) > 0)
            throw new MarketDataException("swing_feasibility", "The elected days leave no continuous completion in this limit period.");
        return new(right, days, at, lower, upper, elected, minimum, maximum, changed, available);
    }

    private static void RequireQuantityTerms(SwingQuantityRight r)
    {
        static bool Nonnegative(SignedRatio value) => value.Denominator.Sign > 0 && value.Numerator.Sign >= 0;
        if (r is null || r.Ordinal < 0 || r.PhysicalSlot < 0 || r.Slots.IsDefaultOrEmpty || !r.Slots.Contains(r.PhysicalSlot) ||
            r.Slots.Any(slot => slot < 0) || r.Slots.Distinct().Count() != r.Slots.Length || !Enum.IsDefined(r.Holder) ||
            r.Period is not (SwingQuantityRight.Term or SwingQuantityRight.Month) ||
            (r.HasTotal ? r.TotalBasis is not (SwingQuantityRight.PerDay or SwingQuantityRight.PerPeriod) : r.TotalBasis != null) ||
            (r.ChangeCount.HasValue ? r.CountRule != SwingQuantityRight.DiffersFromBase : r.CountRule != null) || r.ChangeCount < 0 ||
            r.QuantityPolicy is not (SwingQuantityRight.Elected or SwingRight.ElectedDeliveryQuantity) ||
            r.PlanRule != SwingQuantityRight.ForwardPlan || r.DeadlineRule != SwingQuantityRight.Deadline || r.LeadHours < 0 ||
            r.Days.IsDefaultOrEmpty || r.Days.Length > GasQuantityTerms.MaximumOperatingDays)
            throw InvalidRight("Select one retained limit period, matching total basis and count rule, source choice, plan and deadline policy.");
        if (!Nonnegative(r.Minimum) || !Nonnegative(r.Maximum) || !Nonnegative(r.Step) || r.Step.Numerator.IsZero ||
            r.Minimum.CompareTo(SwingQuantityRight.Base) > 0 || r.Maximum.CompareTo(SwingQuantityRight.Base) < 0 ||
            !r.IndexOf(SwingQuantityRight.Base - r.Minimum).Denominator.IsOne || !r.IndexOf(r.Maximum - r.Minimum).Denominator.IsOne ||
            r.Lower is {} l && !Nonnegative(l) || r.Upper is {} u && !Nonnegative(u) ||
            r.Upper - r.Lower is {} width && width.CompareTo(r.Step) < 0)
            throw InvalidRight("Select nonnegative quantities, a relative grid containing the base and both daily endpoints, and paired stated totals at least one step apart.");
        RequireCalendar(r.Days);
    }
}
