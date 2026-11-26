using System.Collections.Immutable;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// Each quantity is a reduced fraction of one reference-day parcel on PhysicalSlot.
[EconomicDefinition("gas_swing_quantity_right_v1")]
public sealed record SwingQuantityRight(
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
    public const string ElectedDeliveryQuantity = "swing_elected_delivery_quantity_v1";
    public const string Term = "term", Month = "delivery_month", PerDay = "per_day_v1", PerPeriod = "per_period_v1";
    public const string DiffersFromBase = "differs_from_base_v1", Elected = "swing_elected_quantity_v1";
    public const string ForwardPlan = "best_plan_on_forwards_v1", Deadline = "lead_before_first_gas_day_v1";
    internal static readonly SignedRatio Base = new(1, 1);
    internal bool HasTotal => Lower.HasValue || Upper.HasValue;

    internal SignedRatio IndexOf(SignedRatio aboveMinimum) => aboveMinimum * new SignedRatio(Step.Denominator, Step.Numerator);
}

public sealed record SwingQuantityDay(SwingDay Day, SwingElection? CommercialRevision, SignedRatio? QuantityRatio)
{
    public SwingElection? Election => CommercialRevision is { Quantity: not null } ? CommercialRevision : null;
}

public sealed record SwingPeriodBudget(SwingQuantityRight Right, ImmutableArray<SwingQuantityDay> Days,
    DateTimeOffset AssessmentAt, SignedRatio Reference, SignedRatio? AppliedLower, SignedRatio? AppliedUpper, SignedRatio Elected,
    SignedRatio RemainingMinimum, SignedRatio RemainingMaximum, int ChangedDays, int? AvailableChanges)
{
    internal bool MissingCoupledPeriod => (Right.HasTotal || Right.ChangeCount.HasValue) &&
        Days.Any(d => d.Election is null && d.Day.ExerciseAt <= AssessmentAt);

    public IEnumerable<(SwingQuantityDay Decision, string State)> AssessedDays()
    {
        var blocked = MissingCoupledPeriod;
        foreach (var day in Days)
        {
            var state = day.Election is not null ? PhysicalLifecycle.ElectedQuantity :
                day.Day.ExerciseAt <= AssessmentAt ? RetainedSwingQuantityValuation.Missing :
                blocked ? RetainedSwingQuantityValuation.Blocked : RetainedSwingQuantityValuation.Undecided;
            yield return (day, state);
        }
    }
}

public sealed class SwingTermFieldException(string code,string field,string message) : MarketDataException(code,message)
{
    public string Field { get; } = field;
}

public static partial class SwingFeasibility
{
    public static ImmutableArray<SwingPeriodBudget> Resolve(SwingQuantityRight right, ExactQuantity reference,
        ImmutableArray<SwingElection> elections, DateTimeOffset assessmentAt)
    {
        RequireQuantityTerms(right);
        var facts = SelectElections(right.Ordinal, right.Holder, right.Days, reference, elections, assessmentAt, quantity =>
        {
            if (quantity.Specification != reference.Specification)
                throw InvalidElection("Supply the swing election in its retained native unit.");
            var ratio = new SignedRatio(quantity.NumeratorValue, quantity.DenominatorValue) *
                new SignedRatio(reference.DenominatorValue, reference.NumeratorValue);
            if (ratio.CompareTo(right.Minimum) < 0 || ratio.CompareTo(right.Maximum) > 0)
                throw InvalidElection("The swing election lies outside the retained daily range.");
            return ratio;
        });
        var periods = right.Days.GroupBy(d => right.Period == SwingQuantityRight.Term ? 0 : d.Day.Year * 12 + d.Day.Month);
        return [..periods.Select(period => ResolvePeriod(right, [..period.Select(day => facts.TryGetValue(day.Day, out var fact)
            ? new SwingQuantityDay(day, fact.Revision, fact.Quantity) : new(day, null, null))], assessmentAt, new(reference.NumeratorValue, reference.DenominatorValue)))];
    }

    private static SwingPeriodBudget ResolvePeriod(SwingQuantityRight right, ImmutableArray<SwingQuantityDay> days, DateTimeOffset at, SignedRatio reference)
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
            throw new SwingTermFieldException("swing_feasibility","changeCount","Elections exceed the changed-day count in this period.");
        var k = new SignedRatio(Math.Min(open, available ?? open), 1);
        var atBase = new SignedRatio(open, 1);
        var minimum = atBase - k * (SwingQuantityRight.Base - right.Minimum);
        var maximum = atBase + k * (right.Maximum - SwingQuantityRight.Base);
        if (lower is {} low && (low-elected).CompareTo(maximum)>0)
            throw new SwingTermFieldException("swing_feasibility","totalMinimum","The remaining days cannot reach the applied minimum.");
        if (upper is {} high && (high-elected).CompareTo(minimum)<0)
            throw new SwingTermFieldException("swing_feasibility","totalMaximum","The remaining days exceed the applied maximum.");
        if (lower is {} l && (l-elected).CompareTo(minimum)>0) minimum=l-elected;
        if (upper is {} u && (u-elected).CompareTo(maximum)<0) maximum=u-elected;
        return new(right, days, at, reference, lower, upper, elected, minimum, maximum, changed, available);
    }

    private static void RequireQuantityTerms(SwingQuantityRight r)
    {
        static bool Nonnegative(SignedRatio value) => value.Denominator.Sign > 0 && value.Numerator.Sign >= 0;
        if (r is null || r.Ordinal < 0 || r.PhysicalSlot < 0 || r.Slots.IsDefaultOrEmpty || !r.Slots.Contains(r.PhysicalSlot) ||
            r.Slots.Any(slot => slot < 0) || r.Slots.Distinct().Count() != r.Slots.Length || !Enum.IsDefined(r.Holder))
            throw InvalidRight("Select one physical source, its covered slots and the contractual holder.");
        static void Check(bool valid,string field,string message)
        {
            if (!valid) throw new SwingTermFieldException("swing_right",field,message);
        }
        Check(r.Period is SwingQuantityRight.Term or SwingQuantityRight.Month,"limitPeriod","Select term or delivery-month limits.");
        Check(r.HasTotal ? r.TotalBasis is SwingQuantityRight.PerDay or SwingQuantityRight.PerPeriod : r.TotalBasis is null,
            "totalBasis","Supply the total basis exactly when specifying a total.");
        Check(r.ChangeCount is null or >=0,"changeCount","Supply a nonnegative changed-day count.");
        Check(r.ChangeCount.HasValue ? r.CountRule==SwingQuantityRight.DiffersFromBase : r.CountRule is null,
            "countRule","Select the reference-day count rule exactly when specifying a count.");
        Check(r.QuantityPolicy is SwingQuantityRight.Elected or SwingQuantityRight.ElectedDeliveryQuantity,
            "quantityPolicy","Select the quantity governed by the physical source.");
        Check(r.PlanRule==SwingQuantityRight.ForwardPlan,"planRule","Select the installed forward-planning rule.");
        Check(r.DeadlineRule==SwingQuantityRight.Deadline,"deadlineRule","Select the installed deadline rule.");
        Check(r.LeadHours>=0,"leadHours","Supply a nonnegative deadline lead.");
        Check(!r.Days.IsDefaultOrEmpty && r.Days.Length<=GasQuantityTerms.MaximumOperatingDays,"side","Select a supported gas-day calendar.");
        Check(Nonnegative(r.Step) && !r.Step.Numerator.IsZero,"planningStep","Supply a positive planning step.");
        Check(Nonnegative(r.Minimum) && r.Minimum.CompareTo(SwingQuantityRight.Base)<=0,"dailyMinimum","The daily minimum must lie between zero and the reference quantity.");
        Check(Nonnegative(r.Maximum) && r.Maximum.CompareTo(SwingQuantityRight.Base)>=0,"dailyMaximum","The daily maximum must include the reference quantity.");
        Check(r.IndexOf(SwingQuantityRight.Base-r.Minimum).Denominator.IsOne && r.IndexOf(r.Maximum-r.Minimum).Denominator.IsOne,
            "planningStep","The planning grid must contain the reference and both daily endpoints.");
        Check(r.Lower is null || Nonnegative(r.Lower.Value),"totalMinimum","Supply a nonnegative minimum total.");
        Check(r.Upper is null || Nonnegative(r.Upper.Value),"totalMaximum","Supply a nonnegative maximum total.");
        Check(r.Upper-r.Lower is not {} width || width.CompareTo(r.Step)>=0,"totalMaximum","Paired stated limits must span at least one planning step.");
        RequireCalendar(r.Days);
    }
}
