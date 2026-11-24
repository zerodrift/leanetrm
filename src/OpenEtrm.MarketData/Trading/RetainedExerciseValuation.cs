using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// Consumes already evaluated Side occurrences in their held quantity basis.
internal sealed class RetainedExerciseValuation
{
    internal const string ModelPolicy = KirkSpreadCall.Implementation + "/" + HalfEven18.Policy;
    private const SchedulePriceFacts CertainFacts = SchedulePriceFacts.Fixed | SchedulePriceFacts.PublishedFixing;
    private readonly ExerciseRight right;
    private readonly Dictionary<DateOnly, Amounts> byDay = [];
    private readonly Amounts[] groups;

    private sealed class Amounts
    {
        internal SignedRatio Forward = SignedRatio.Zero, Certain = SignedRatio.Zero;
        internal SignedRatio Receives = SignedRatio.Zero, Pays = SignedRatio.Zero;
        internal CurveKey? ReceiveCurve, PayCurve;
    }

    internal RetainedExerciseValuation(ExerciseRight right)
    {
        this.right = right;
        groups = right.Groups.Select(group =>
        {
            var amounts = new Amounts();
            for (var day = group.From; day < group.To; day = day.AddDays(1)) byDay.Add(day, amounts);
            return amounts;
        }).ToArray();
    }

    internal static void RequireUndecided(ImmutableArray<ExerciseRight> rights, DateTimeOffset at)
    {
        if (rights.Any(right => right.Groups.Any(g => g.ExerciseAt <= at)))
            throw new MarketDataException("exercise_determination", "An elapsed exercise group requires its determination.");
    }

    internal void Add(RetainedProgramValuation side, ImmutableArray<EntryDayQuantity> days, SignedRatio membership)
    {
        var program = ((RetainedProgramSchedule)side.Schedule).Program;
        var byOccurrence = side.Lifecycle!.Rows.ToDictionary(row => row.Occurrence);
        var sources = program.Document.Instructions.SelectMany((source, i) =>
            source.Consumers.Select(output => (output, source: i))).ToLookup(pair => pair.output, pair => pair.source);
        var quantities = days.ToDictionary(day => day.Day, day => day.Taken);
        foreach (var contribution in side.Execution.Contributions)
        {
            var output = program.Document.Instructions[contribution.Instruction].Output;
            var row = byOccurrence[output.PaymentOrdinal!.Value];
            var amount = (row.Expected?.Add(row.Paid) ?? row.Unrealized).Ratio;
            if (amount.Numerator.IsZero) continue;
            if (amount.Numerator.Sign < 0) throw OutsideModel();
            var certain = IsCertain(contribution.Amount.States[0].Prices);
            CurveKey? curve = null;
            if (!certain)
            {
                foreach (var address in sources[contribution.Instruction])
                {
                    var source = program.Document.Instructions[address];
                    if (!CurvePriceScheduleSource.IsLookup(source.Output.Operation) ||
                        IsCertain(side.Execution.Values[address].Scenario.States[0].Prices)) continue;
                    var market = source.Bindings.Single(b => b.Port == "market").Market!.Value;
                    SetCurve(ref curve, market.Curve);
                }
                if (curve is null) throw OutsideModel();
            }
            var shares = new Dictionary<Amounts, SignedRatio>();
            var total = SignedRatio.Zero;
            for (var day = row.DeliveryFrom; day < row.DeliveryTo; day = day.AddDays(1))
            {
                var taken = quantities[day];
                if (taken.Numerator.IsZero) continue;
                var group = byDay[day];
                shares[group] = shares.GetValueOrDefault(group, SignedRatio.Zero) + taken;
                total += taken;
            }
            foreach (var (group, share) in shares)
            {
                var forward = amount * membership * share * new SignedRatio(total.Denominator, total.Numerator);
                group.Forward += forward;
                var holder = right.Holder == ExerciseHolder.Us ? forward : SignedRatio.Zero - forward;
                if (certain) group.Certain += holder;
                else if (holder.Numerator.Sign > 0)
                {
                    SetCurve(ref group.ReceiveCurve, curve!.Value);
                    group.Receives += holder;
                }
                else
                {
                    SetCurve(ref group.PayCurve, curve!.Value);
                    group.Pays -= holder;
                }
            }
        }
    }

    private static bool IsCertain(SchedulePriceFacts prices) => prices != SchedulePriceFacts.None && (prices & ~CertainFacts) == 0;
    private static void SetCurve(ref CurveKey? selected, CurveKey curve)
    {
        if (selected is {} previous && previous != curve) throw OutsideModel();
        selected = curve;
    }
    private static MarketDataException OutsideModel() => new("exercise_model_scope",
        "A holder's choice requires nonnegative prices, one curve per uncertain side and distinct curves across sides.");

    internal IEnumerable<ExerciseGroupValue> Values(SideValuationContext context, ExerciseMarks? marks, ReferenceKey currency, int sign)
    {
        for (var i = 0; i < groups.Length; i++)
        {
            var group = right.Groups[i];
            var amounts = groups[i];
            var month = new DateOnly(group.From.Year, group.From.Month, 1);
            var a = HalfEven18.Round(amounts.Receives);
            var b = HalfEven18.Round(amounts.Pays);
            if (amounts.ReceiveCurve is {} first && first == amounts.PayCurve) throw OutsideModel();
            var ticks = (group.ExerciseAt - context.UnitEvaluation.CalculationAt).Ticks;
            var years = LatticeDecimal.Parse(ticks.ToString(CultureInfo.InvariantCulture)) /
                LatticeDecimal.Parse((365 * TimeSpan.TicksPerDay).ToString(CultureInfo.InvariantCulture));
            LatticeDecimal? Volatility(CurveKey? curve, SignedRatio amount) => amount.Numerator.IsZero ? null :
                LatticeDecimal.Parse(context.ExerciseVolatility((marks ?? throw ExerciseMarks.Missing()).Volatility(curve!.Value, month))
                    .Format(30, MidpointRounding.ToEven));
            var inputs = new HolderChoiceInputs(a, b, HalfEven18.Round(amounts.Certain),
                Volatility(amounts.ReceiveCurve, a), Volatility(amounts.PayCurve, b),
                !a.Numerator.IsZero && !b.Numerator.IsZero ? marks!.Correlation(amounts.ReceiveCurve!.Value, amounts.PayCurve!.Value, month) : null, years);
            var choice = HolderChoice.Value(inputs);
            var receiveChoice = (right.Holder == ExerciseHolder.Us) == (sign > 0);
            var counted = receiveChoice ? choice : default(ExactDecimal) - choice;
            yield return new(right.Ordinal, group.From, group.To, "undecided",
                ExactMoney.FromFraction(sign * amounts.Forward.Numerator, amounts.Forward.Denominator, currency),
                ExactMoney.FromMoney(new(counted, currency)), inputs);
        }
    }

    internal static UnitPricingContract Declare(UnitPricingContract contract, RetainedGasInstrument instrument, UnitValuationContext context)
    {
        var rights = instrument.Rights;
        RequireUndecided(rights, context.CalculationAt);
        var firstEvent = rights.SelectMany(r => r.Groups).Min(g => g.ExerciseAt);
        void Boundary(DateTimeOffset at)
        {
            if (at > context.Snapshot.ValuationAt && at < firstEvent) firstEvent = at;
        }
        if (context.BindingState is not PhysicalCashState held)
            throw PhysicalDeliveryQuantities.Invalid("An exercise right requires its complete held day quantities.");
        instrument.ValidateValuationState(held);
        var entryDays = EntryQuantities.ForMembers(instrument.Members, held);
        var used = new HashSet<(CurveKey Curve, DateOnly Month)>();
        var lookupIndex = instrument.PriceLookups.ToLookup(l => (l.Market, l.Contract.DeliveryFrom, l.Contract.DeliveryTo));
        var selectedSlots = rights.SelectMany(right => right.Slots).ToHashSet();
        for (var slot = 0; slot < instrument.Members.Length; slot++)
        {
            var program = instrument.Members[slot].Program;
            var selectedDays = selectedSlots.Contains(slot) ? entryDays[slot].Where(d => d.Taken.Numerator.Sign > 0)
                .Select(d => d.Day).ToHashSet() : null;
            var physical = PhysicalLifecycle.ReadsDeliveryMarks(program);
            var instructions = program.Document.Instructions;
            foreach (var row in instrument.Sides[slot].Side.GenerateSchedule().Rows)
            {
                if (row.DeliveryWindow is IntervalScheduleWindow { Interval: var interval })
                {
                    Boundary(interval.Start);
                    Boundary(interval.End);
                }
                else
                {
                    Boundary(PaymentDatePolicy.Start(row.PaymentDate));
                    Boundary(PaymentDatePolicy.OverdueBoundary(row.PaymentAt));
                }
            }
            foreach (var (instruction, lookup) in PhysicalLifecycle.DatedInputs(program, lookupIndex))
            {
                if (!physical && PeriodPriceSelector.ProjectionAt(lookup, context.Snapshot) is null) continue;
                Boundary(lookup.Contract.Expiry);
                if (!physical && lookup.Fixing is {} fixing) Boundary(fixing.FixingAt);
                if (selectedDays is null) continue;
                foreach (var output in instruction.Consumers)
                {
                    var guard = instructions[output].Output;
                    var from = guard.Grid is {} g ? program.Document.Grids[g].Interval(0).OperatingDate : guard.DeliveryFrom!.Value;
                    var to = guard.DeliveryTo ?? from.AddDays(1);
                    for (var day = from; day < to; day = day.AddDays(1))
                        if (selectedDays.Contains(day)) used.Add((lookup.Market.Curve, new(day.Year, day.Month, 1)));
                }
            }
        }
        if (context.Snapshot.Knowledge is {} knowledge)
            foreach (var payment in held.Payments.Where(p => p.KnownAt <= knowledge.KnownAt)) Boundary(payment.PaidAt);
        if (firstEvent <= context.CalculationAt)
            throw new MarketDataException("valuation_time_coverage", "The calculation clock crosses a retained valuation event.");
        return contract with { ModelVersion = contract.ModelVersion + "/" + ModelPolicy, Time = ValuationTimeDependency.Between(context.CalculationAt, firstEvent),
            Volatility = instrument.Marks is {} marks ? [..marks.OrderedVolatilities.Where(v => used.Contains((v.Curve, v.Month))).Select(v => v.Volatility)] : [] };
    }
}
