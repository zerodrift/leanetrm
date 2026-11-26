using System.Collections.Immutable;
using OpenEtrm.MarketData.Options;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

public sealed record SwingDayValue(SwingDay Day, string State, SwingElection? Election, ExactQuantity? SelectedQuantity,
    ImmutableArray<DeliveryQuantityEvidence> QuantityEvidence)
{
    public string? Forward { get; init; }
}
public sealed record SwingVolatilityValue(CurveKey Curve, DateOnly Month, Guid RevisionId, string Volatility);
public sealed record SwingRightValue(int Right, int CommittedLots, int RemainingMinimumLots, int RemainingMaximumLots,
    ExactQuantity Increment, ExactMoney Counted, ExactMoney Intrinsic, ImmutableArray<SwingDayValue> Days,
    ImmutableArray<int> IntrinsicLots, SwingPricingBinding Pricing, SwingChoiceInputs? Inputs, SwingWorkCounts Work,
    SwingRight Terms, ImmutableArray<SwingVolatilityValue> Marks);

// Commercial commitments use absolute elected lots. Selected operational quantities affect ordinary Sides only.
internal sealed class RetainedSwingValuation
{
    internal const string Undecided = "undecided";
    private readonly SwingRight right;
    private readonly SwingBudget budget;
    private readonly ExactQuantity increment;
    private readonly ImmutableArray<SwingDayValue> days;
    private readonly ImmutableArray<EntryDayQuantity> ordinary, hypothetical;
    internal ImmutableArray<SwingDay> Pending { get; }
    internal bool RequiresMarks { get; }

    internal RetainedSwingValuation(SwingRight right, PhysicalCashState held, DateTimeOffset at)
    {
        this.right = right;
        if (held.NextSwingElectionEffectiveAt <= at)
            throw new MarketDataException("valuation_time_coverage", "Refresh swing election facts at the selected valuation instant.");
        var owner = held.OwnerDays.Where(d => d.Slot == right.PhysicalSlot).ToDictionary(d => d.Day);
        if (owner.Count != right.Days.Length || right.Days.Any(day => !owner.ContainsKey(day.Day)))
            throw PhysicalDeliveryQuantities.Invalid("Swing requires its complete retained physical owner-day quantities.");
        var reference = owner[right.Days[0].Day].Contract;
        increment = reference.Scale(right.IncrementRatio);
        budget = SwingFeasibility.Resolve(right, reference, held.SwingElections, at);
        Pending = [..budget.Days.Where(d => d.Election is null).Select(d => d.Day)];
        if (Pending.Any(d => d.ExerciseAt <= at))
            throw new MarketDataException("swing_determination", "An elapsed swing day requires an explicit commercial election.");
        // With identical contiguous daily bounds there are multiple schedules unless all days are
        // forced to an endpoint, or a single remaining day has a fixed total.
        RequiresMarks = Pending.Length > 0 && budget.RemainingMaximumLots > (long)Pending.Length * right.DailyMinimumLots &&
            budget.RemainingMinimumLots < (long)Pending.Length * right.DailyMaximumLots &&
            (Pending.Length > 1 || budget.RemainingMinimumLots != budget.RemainingMaximumLots);

        var selected = ImmutableArray.CreateBuilder<SwingDayValue>();
        var counted = ImmutableArray.CreateBuilder<EntryDayQuantity>();
        var perLot = ImmutableArray.CreateBuilder<EntryDayQuantity>();
        foreach (var decision in budget.Days)
        {
            var day = owner[decision.Day.Day];
            var fallback = day.Source == PhysicalLifecycle.ContractQuantity;
            var election = decision.Election;
            if (election is null && (!fallback || day.HasOperationalRecord || day.HasContractDeliveryRecord))
                throw new MarketDataException("swing_conflict", "A swing day with operational records requires its commercial election.");
            if (fallback && day.HasContractDeliveryRecord)
                throw PhysicalDeliveryQuantities.Invalid("Supply the selected operational quantity of the swing day.");
            var quantity = fallback ? election?.Quantity : day.Selected;
            var source = fallback ? election is null ? Undecided : PhysicalLifecycle.ElectedQuantity : day.Source;
            ImmutableArray<DeliveryQuantityEvidence> evidence = election is null ? [] :
                [new(PhysicalLifecycle.ElectedQuantity, election.RevisionId, election.Evidence), ..day.Evidence];
            selected.Add(new(decision.Day, election is null ? Undecided : PhysicalLifecycle.ElectedQuantity, election, quantity, evidence));
            var amount = quantity is null ? SignedRatio.Zero : Ratio(quantity);
            counted.Add(new(day.Day, Ratio(day.Contract), amount, amount, source, evidence));
            var lot = election is null ? Ratio(increment) : SignedRatio.Zero;
            perLot.Add(new(day.Day, Ratio(day.Contract), lot, lot, "swing_increment", []));
        }
        days = selected.ToImmutable();
        ordinary = counted.ToImmutable();
        hypothetical = perLot.ToImmutable();
    }

    internal void RequireCompatiblePayments<TMember>(ImmutableArray<TMember> members, PhysicalCashState held, MarketSnapshot snapshot)
        where TMember : IRetainedProgramMember
    {
        foreach (var payment in held.VisiblePayments(snapshot).Where(p => right.Slots.Contains(p.Slot)))
        {
            var row = members[payment.Slot].Program.Document.Instructions.Single(i =>
                i.Output.Operation == "output_guard" && i.Output.PaymentOrdinal == payment.Occurrence).Output;
            if (!days.Any(d => d.Election is not null && d.Day.Day >= row.DeliveryFrom && d.Day.Day < row.DeliveryTo))
                throw new MarketDataException("swing_conflict", "A covered payment requires a commercial election on its retained cash row.");
        }
    }

    internal CurveKey PointCurve(SidedInstrument instrument) =>
        instrument.Sides[right.PhysicalSlot].Side.Dependencies.Select(d => d.Curve).Distinct().Single();

    private static SignedRatio Ratio(ExactQuantity q) => new(q.NumeratorValue, q.DenominatorValue);

    internal ImmutableArray<ImmutableArray<EntryDayQuantity>> Entries(ImmutableArray<ImmutableArray<EntryDayQuantity>> other,
        bool counterfactual)
    {
        var result = other.ToBuilder();
        foreach (var slot in right.Slots) result[slot] = counterfactual ? hypothetical : ordinary;
        return result.ToImmutable();
    }

    internal SwingRightValue Evaluate(SideValuationContext context, ReferenceKey currency, SwingPricingBinding pricing,
        ExerciseMarks? marks, CurveKey curve, Func<int, RetainedProgramValuation> perLot)
    {
        var sign = ((PhysicalCashState)context.BindingState!).Quantity.Amount.ScaledValue.Sign;
        var holder = right.Holder == ExerciseHolder.Us ? 1 : -1;
        var (uncertain, certain, forwards) = Amounts(context, Pending, right.Slots, right.PhysicalSlot,
            new(holder * sign, 1), perLot);
        var monthly = new Dictionary<DateOnly, SwingVolatilityValue>();
        LatticeDecimal? Volatility(DateOnly day)
        {
            if (!RequiresMarks) return null;
            var month = new DateOnly(day.Year, day.Month, 1);
            if (!monthly.TryGetValue(month, out var mark))
            {
                var input = (marks ?? throw ExerciseMarks.Missing()).Volatility(curve, month);
                mark = new(curve, month, input.Id, context.ExerciseVolatility(input).Format(30, MidpointRounding.ToEven));
                monthly.Add(month, mark);
            }
            return LatticeDecimal.Parse(mark.Volatility);
        }
        var inputs = new SwingChoiceInputs([..Pending.Select(d => new SwingPayoffDay(d, uncertain[d.Day], certain[d.Day], Volatility(d.Day)))],
            right.DailyMinimumLots, right.DailyMaximumLots, right.MinimumLots, right.MaximumLots, budget.CommittedLots,
            context.UnitEvaluation.CalculationAt, pricing.NumericalPolicy);
        var choice = SwingChoice.Value(inputs, context.UnitEvaluation.Cancellation);
        ExactMoney Held(SignedRatio value) => ExactMoney.FromFraction(value.Numerator * holder * sign, value.Denominator, currency);
        return new(right.Ordinal, budget.CommittedLots, budget.RemainingMinimumLots, budget.RemainingMaximumLots, increment,
            Held(choice.Value), Held(choice.IntrinsicValue), [..days.Select(d => d with { Forward = forwards.GetValueOrDefault(d.Day.Day) })],
            choice.IntrinsicLots, pricing, Pending.IsEmpty ? null : inputs, choice.Work, right, [..monthly.Values.OrderBy(m => m.Month)]);
    }

    internal static (Dictionary<DateOnly, SignedRatio> Physical, Dictionary<DateOnly, SignedRatio> Fixed,
        Dictionary<DateOnly, string> Forwards) Amounts(SideValuationContext context, ImmutableArray<SwingDay> pending,
        ImmutableArray<int> slots, int physicalSlot, SignedRatio orientation, Func<int, RetainedProgramValuation> occurrence)
    {
        var uncertain = pending.ToDictionary(d => d.Day, _ => SignedRatio.Zero);
        var certain = pending.ToDictionary(d => d.Day, _ => SignedRatio.Zero);
        var forwards = new Dictionary<DateOnly, string>();
        if (pending.IsEmpty) return (uncertain, certain, forwards);
        foreach (var slot in slots)
        {
            var physical = slot == physicalSlot;
            var amounts = physical ? uncertain : certain;
            var factor = context.RetainedPrograms!.HeldFactors[slot] * orientation;
            var side = occurrence(slot);
            var rows = side.Lifecycle!.Rows;
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                var covered = Enumerable.Range(0, row.DeliveryTo.DayNumber - row.DeliveryFrom.DayNumber)
                    .Select(row.DeliveryFrom.AddDays).Where(amounts.ContainsKey).ToArray();
                if (covered.Length == 0) continue;
                // Each pending day in the occurrence contributes one equal hypothetical parcel.
                // The concrete Side has applied its own payment terms and quantity denominator.
                var gross = (physical ? row.Unrealized : row.Expected)!.Ratio;
                if (physical)
                {
                    if (gross.Numerator.Sign <= 0)
                        throw new MarketDataException("swing_model_scope", "The one-factor swing model requires positive physical forwards.");
                    forwards.Add(row.DeliveryFrom, PhysicalForward(side, index));
                }
                var share = gross * factor * new SignedRatio(1, covered.Length);
                foreach (var day in covered) amounts[day] += share;
            }
        }
        return (uncertain, certain, forwards);
    }

    private static string PhysicalForward(RetainedProgramValuation side, int contribution)
    {
        var document = ((RetainedProgramSchedule)side.Schedule).Program.Document;
        var guard = document.Instructions[side.Execution.Contributions[contribution].Instruction];
        var amount = SideScheduleInspection.Operand(document, guard, "amount");
        var price = side.Execution.Values[SideScheduleInspection.Operand(document, amount, "price").Output.Ordinal];
        var (column, cell) = SideScheduleInspection.Column(price, 0);
        var point = column.Values[cell];
        return ExactNumberText.Format(new(point.Numerator, point.Denominator));
    }
}
