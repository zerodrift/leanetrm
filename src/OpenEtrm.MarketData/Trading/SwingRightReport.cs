using System.Collections.Immutable;
using OpenEtrm.MarketData.Options;

namespace OpenEtrm.MarketData.Trading;

public sealed record SwingForwardInput(CurveRequest Market, DateOnly From, DateOnly To, string Price, ImmutableArray<ObservationKey> Observations);

// Amounts are signed held currency. A missing subtotal has no assessable open decision.
public sealed record SwingPlanComparison(string Model, string Scope, int PlannedDays, int UnassessedDays, ExactMoney Amount);
public sealed record SwingQuantityDayValue(SwingDay Day, string State, SwingElection? CommercialRevision,
    ExactQuantity? SelectedQuantity, ImmutableArray<DeliveryQuantityEvidence> Evidence, ExactQuantity? PlannedQuantity,
    string? Forward, SwingUnitPayoffDay? UnitPayoff, ImmutableArray<HeldDeliveryRecord> OperationalRecords)
{
    public SwingElection? Election => CommercialRevision is { Quantity: not null } ? CommercialRevision : null;
}
public sealed record SwingQuantityRightValue(SwingQuantityRight Terms, ExactQuantity Reference, SwingPricingBinding Pricing,
    ImmutableArray<SwingPeriodBudget> Periods, ImmutableArray<SwingQuantityDayValue> Days,
    ImmutableArray<SwingVolatilityValue> Marks, DateTimeOffset? NextDeadline, string Scope, int ModeledDays, ExactMoney? Counted,
    SwingPlanComparison? Comparison, SwingUnitEstimate Estimate, SwingUnitWork Work, ImmutableArray<SwingForwardInput> ForwardInputs, DateTimeOffset KnownAt)
{
    internal int UnassessedDays => Days.Count(d => d.State is RetainedSwingQuantityValuation.Missing or RetainedSwingQuantityValuation.Blocked);
}

public sealed record SwingPeriodReport(DateOnly From,DateOnly Through,int Days,string LimitPeriod,string? TotalBasis,int? ChangeCount,
    string? CountRule,string? AppliedLower,string? AppliedUpper,string? RemainingLower,string? RemainingUpper,string Elected,
    int ChangesUsed,int? AvailableChanges,string RemainingMinimum,string RemainingMaximum,int MissingDays,int BlockedDays,
    DateTimeOffset? NextDeadline,string? Planned,int? PlannedChanges)
{
    public static SwingPeriodReport FromBudget(SwingPeriodBudget period,IReadOnlyDictionary<DateOnly,ExactQuantity>? plan=null)
    {
        string? Amount(OpenEtrm.Shared.Numerics.SignedRatio? ratio) =>
            ratio is {} value ? ExactNumberText.Format(value*period.Reference) : null;
        var pending=period.Days.Where(d=>d.Election is null && d.Day.ExerciseAt>period.AssessmentAt).ToArray();
        var planned=plan is null || period.MissingCoupledPeriod?null:pending.Where(d=>plan.ContainsKey(d.Day.Day)).Select(d=>plan[d.Day.Day]).ToArray();
        return new(period.Days[0].Day.Day,period.Days[^1].Day.Day,period.Days.Length,period.Right.Period,
            period.Right.TotalBasis,period.Right.ChangeCount,period.Right.CountRule,Amount(period.AppliedLower),Amount(period.AppliedUpper),
            Amount(period.AppliedLower-period.Elected),Amount(period.AppliedUpper-period.Elected),Amount(period.Elected)!,
            period.ChangedDays,period.AvailableChanges,Amount(period.RemainingMinimum)!,Amount(period.RemainingMaximum)!,
            period.Days.Count(d=>d.Election is null&&d.Day.ExerciseAt<=period.AssessmentAt),period.MissingCoupledPeriod?pending.Length:0,
            pending.FirstOrDefault()?.Day.ExerciseAt,planned is null?null:ExactNumberText.Format(OpenEtrm.Shared.Numerics.SignedRatio.Sum(
                planned.Select(q=>new OpenEtrm.Shared.Numerics.SignedRatio(q.NumeratorValue,q.DenominatorValue)))),
            planned?.Count(q=>q.NumeratorValue*period.Reference.Denominator!=q.DenominatorValue*period.Reference.Numerator));
    }
}
public sealed record SwingDayReport(DateOnly Day,DateTimeOffset ExerciseAt,string State,string? ElectedQuantity,string? SelectedQuantity,
    Guid? RevisionId,string? CommercialEvidence,ImmutableArray<DeliveryQuantityEvidence> QuantityEvidence,ImmutableArray<SwingOperatingRecord> OperationalRecords,
    string? Forward,string? PhysicalPerUnit,string? FixedPerUnit,string? PlannedQuantity);
public sealed record SwingOperatingRecord(string Kind,string Status,string Quantity,DeliveryQuantityEvidence Evidence);
public sealed record SwingPlanReport(string Model,string Scope,int PlannedDays,int UnassessedDays,string Amount);
public sealed record SwingRightReport(int Right,int PhysicalSlot,ImmutableArray<int> Slots,string Holder,string QuantityPolicy,
    string ReferenceQuantity,ReferenceKey QuantityUnit,string DailyMinimum,string DailyMaximum,string PlanningStep,
    ImmutableArray<SwingPeriodReport> Periods,DateTimeOffset? NextDeadline,string Scope,int ModeledDays,int UnassessedDays,string? Counted,
    SwingPlanReport? Comparison,SwingPricingBinding Pricing,ImmutableArray<SwingVolatilityValue> Marks,ImmutableArray<SwingDayReport> Days,
    SwingUnitEstimate Estimate,SwingUnitWork Work,ImmutableArray<SwingForwardInput> ForwardInputs,DateTimeOffset KnownAt)
{
    public static SwingRightReport FromValue(SwingQuantityRightValue value)
    {
        var right=value.Terms;
        string Amount(OpenEtrm.Shared.Numerics.SignedRatio ratio)=>value.Reference.ScaleFraction(ratio.Numerator,ratio.Denominator).ToExactString();
        var plan=value.Days.Where(d=>d.PlannedQuantity is not null).ToDictionary(d=>d.Day.Day,d=>d.PlannedQuantity!);
        return new(right.Ordinal,right.PhysicalSlot,right.Slots,right.Holder==ExerciseHolder.Us?"us":"counterparty",right.QuantityPolicy,
            value.Reference.ToExactString(),value.Reference.Specification,Amount(right.Minimum),Amount(right.Maximum),Amount(right.Step),
            [..value.Periods.Select(p=>SwingPeriodReport.FromBudget(p,value.Comparison is null?null:plan))],value.NextDeadline,value.Scope,
            value.ModeledDays,value.UnassessedDays,value.Counted?.ToExactString(),value.Comparison is {} c?
                new(c.Model,c.Scope,c.PlannedDays,c.UnassessedDays,c.Amount.ToExactString()):null,value.Pricing,value.Marks,
            [..value.Days.Select(d=>new SwingDayReport(d.Day.Day,d.Day.ExerciseAt,d.State,d.CommercialRevision?.Quantity?.ToExactString(),d.SelectedQuantity?.ToExactString(),
                d.CommercialRevision?.RevisionId,d.CommercialRevision?.Evidence,d.Evidence,[..d.OperationalRecords.Select(o=>new SwingOperatingRecord(o.Kind,o.Status,o.Quantity.ToExactString(),o.Evidence))],
                d.Forward,d.UnitPayoff is {} u?ExactNumberText.Format(u.PhysicalToUs):null,d.UnitPayoff is {} f?ExactNumberText.Format(f.FixedToUs):null,
                d.PlannedQuantity?.ToExactString()))],value.Estimate,value.Work,value.ForwardInputs,value.KnownAt);
    }
}
