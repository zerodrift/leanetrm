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
    private readonly ExerciseDecisions decisions;
    private readonly Dictionary<DateOnly, Amounts> byDay = [];
    private readonly Amounts[] groups;

    private sealed class Amounts(string outcome)
    {
        internal string Outcome { get; } = outcome;
        internal bool HasUncertainPrice;
        internal SignedRatio Forward = SignedRatio.Zero, Certain = SignedRatio.Zero;
        internal SignedRatio Receives = SignedRatio.Zero, Pays = SignedRatio.Zero;
        internal CurveKey? ReceiveCurve, PayCurve;
    }

    internal RetainedExerciseValuation(ExerciseRight right, ExerciseDecisions decisions)
    {
        this.right = right;
        this.decisions = decisions;
        groups = right.Groups.Select(group =>
        {
            var amounts = new Amounts(decisions.Outcome(right, group));
            for (var day = group.From; day < group.To; day = day.AddDays(1)) byDay.Add(day, amounts);
            return amounts;
        }).ToArray();
    }

    internal void Add(RetainedProgramValuation side, ImmutableArray<EntryDayQuantity> days, SignedRatio membership)
    {
        var program = ((RetainedProgramSchedule)side.Schedule).Program;
        var byOccurrence = side.Lifecycle!.Rows.ToDictionary(row => row.Occurrence);
        ILookup<int, int>? sources = null;
        var quantities = days.ToDictionary(day => day.Day);
        var holderReceives = (membership.Numerator.Sign > 0) == (right.Holder == ExerciseHolder.Us);
        foreach (var contribution in side.Execution.Contributions)
        {
            var output = program.Document.Instructions[contribution.Instruction].Output;
            var row = byOccurrence[output.PaymentOrdinal!.Value];
            var amount = (row.Expected?.Add(row.Paid) ?? row.Unrealized ??
                throw new MarketDataException("exercise_right", "An exercise right requires an assessed row amount.")).Ratio;
            var certain = IsCertain(contribution.Amount.States[0].Prices);
            var shares = new Dictionary<Amounts, SignedRatio>();
            var total = SignedRatio.Zero;
            for (var day = row.DeliveryFrom; day < row.DeliveryTo; day = day.AddDays(1))
            {
                var group = byDay[day];
                group.HasUncertainPrice |= !certain;
                var taken = quantities[day].Taken;
                if (taken.Numerator.IsZero) continue;
                shares[group] = shares.GetValueOrDefault(group, SignedRatio.Zero) + taken;
                total += taken;
            }
            if (shares.Count == 0) continue;
            var modeled = shares.Keys.Any(group => group.Outcome == ExerciseDecisions.Undecided);
            if (modeled && amount.Numerator.Sign < 0) throw OutsideModel();
            CurveKey? curve = null;
            if (modeled && !certain)
            {
                sources ??= program.Document.Instructions.SelectMany((source, i) =>
                    source.Consumers.Select(output => (output, source: i))).ToLookup(pair => pair.output, pair => pair.source);
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
            foreach (var (group, share) in shares)
            {
                var forward = amount * membership * share * new SignedRatio(total.Denominator, total.Numerator);
                group.Forward += forward;
                if (group.Outcome != ExerciseDecisions.Undecided) continue;
                var holder = right.Holder == ExerciseHolder.Us ? forward : SignedRatio.Zero - forward;
                if (certain) group.Certain += holder;
                else if (holderReceives)
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

    internal void DecideAutomatic()
    {
        if (right.Settlement != ExerciseSettlement.CashAutomatic) return;
        for (int i = 0; i < groups.Length; i++)
            if (!groups[i].HasUncertainPrice)
                decisions.DecideAutomatic(right, right.Groups[i], groups[i].Certain.Numerator.Sign > 0);
    }

    internal IEnumerable<ExerciseGroupValue> Values(SideValuationContext context, ExerciseMarks? marks, ReferenceKey currency, int sign)
    {
        for (var i = 0; i < groups.Length; i++)
        {
            var group = right.Groups[i];
            var amounts = groups[i];
            var forward = ExactMoney.FromFraction(sign * amounts.Forward.Numerator, amounts.Forward.Denominator, currency);
            var outcome = decisions.Outcome(right, group);
            if (outcome != ExerciseDecisions.Undecided)
            {
                // Group reports show selected gross economics. The Side owns paid and outstanding cash.
                var decided = outcome == ExerciseElection.Exercised ? forward : ExactMoney.Parse("0", currency);
                yield return new(right.Ordinal, group.From, group.To, outcome, forward, decided, null);
                continue;
            }
            var month = new DateOnly(group.From.Year, group.From.Month, 1);
            var a = HalfEven18.Round(amounts.Receives);
            var b = HalfEven18.Round(amounts.Pays);
            if (amounts.ReceiveCurve is {} first && first == amounts.PayCurve) throw OutsideModel();
            var ticks = (group.ExerciseAt - context.UnitEvaluation.CalculationAt).Ticks;
            if (ticks <= 0) outcome = ExerciseDecisions.UndecidedForecast;
            var years = ticks <= 0 ? default : LatticeDecimal.Parse(ticks.ToString(CultureInfo.InvariantCulture)) /
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
            yield return new(right.Ordinal, group.From, group.To, outcome, forward,
                ExactMoney.FromMoney(new(counted, currency)), inputs);
        }
    }

    internal sealed record MarkScope(ImmutableArray<(ExerciseGroupKey Group, CurveKey Curve, DateOnly Month)> Inputs,
        bool HasModel, DateTimeOffset? NextEvent);

    internal static MarkScope Scope(RetainedGasInstrument instrument, MarketSnapshot snapshot, DateTimeOffset at, PhysicalCashState held)
    {
        var rights = instrument.Rights;
        var decisions = ExerciseDecisions.Resolve(rights, instrument.Members, held, snapshot, at);
        DateTimeOffset? firstEvent = null;
        void Boundary(DateTimeOffset boundary)
        {
            if (boundary > snapshot.ValuationAt && (firstEvent is null || boundary < firstEvent)) firstEvent = boundary;
        }
        var modeled = rights.Where(right => right.Settlement == ExerciseSettlement.Physical)
            .SelectMany(right => right.Groups.Where(group => decisions.Outcome(right, group) == ExerciseDecisions.Undecided)).ToHashSet();
        if (held.NextElectionEffectiveAt is {} next)
        {
            if (next <= snapshot.ValuationAt)
                throw new MarketDataException("valuation_time_coverage", "Refresh election facts at the selected valuation instant.");
            Boundary(next);
        }
        foreach (var election in held.Elections) Boundary(election.EffectiveAt);
        var entryDays = EntryQuantities.ForMembers(instrument.Members, held, rights: instrument.Rights);
        var used = new HashSet<(ExerciseGroupKey Group, CurveKey Curve, DateOnly Month)>();
        var lookupIndex = instrument.PriceLookups.ToLookup(l => (l.Market, l.Contract.DeliveryFrom, l.Contract.DeliveryTo));
        for (var slot = 0; slot < instrument.Members.Length; slot++)
        {
            var program = instrument.Members[slot].Program;
            var right = rights.SingleOrDefault(r => r.Slots.Contains(slot));
            var selectedDays = entryDays[slot].IsDefault ? null : entryDays[slot]
                .Select(d => (d.Day, d.Taken, Group: decisions.UndecidedGroup(slot, d.Day))).Where(d => d.Group is not null)
                .ToDictionary(d => d.Day);
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
                if (!physical && PeriodPriceSelector.ProjectionAt(lookup, snapshot) is null) continue;
                Boundary(lookup.Contract.Expiry);
                if (!physical && lookup.Fixing is {} fixing) Boundary(fixing.FixingAt);
                if (selectedDays is null) continue;
                foreach (var output in instruction.Consumers)
                {
                    var guard = instructions[output].Output;
                    var from = guard.Grid is {} g ? program.Document.Grids[g].Interval(0).OperatingDate : guard.DeliveryFrom!.Value;
                    var to = guard.DeliveryTo ?? from.AddDays(1);
                    for (var day = from; day < to; day = day.AddDays(1))
                        if (selectedDays.TryGetValue(day, out var selected))
                        {
                            var group = selected.Group!;
                            if (selected.Taken.Numerator.Sign > 0)
                                used.Add((new(right!.Ordinal, group.From, group.To), lookup.Market.Curve, new(day.Year, day.Month, 1)));
                            modeled.Add(group);
                        }
                }
            }
        }
        foreach (var group in modeled) Boundary(group.ExerciseAt);
        foreach (var payment in held.KnownPayments(snapshot)) Boundary(payment.PaidAt);
        if (firstEvent <= at)
            throw new MarketDataException("valuation_time_coverage", "The calculation clock crosses a retained valuation event.");
        return new([..used], modeled.Count > 0, firstEvent);
    }

    internal static UnitPricingContract Declare(UnitPricingContract contract, RetainedGasInstrument instrument, UnitValuationContext context)
    {
        if (context.BindingState is not PhysicalCashState held)
            throw PhysicalDeliveryQuantities.Invalid("An exercise right requires its complete held day quantities.");
        var scope = Scope(instrument, context.Snapshot, context.CalculationAt, held);
        var used = scope.Inputs.Select(i => (i.Curve, i.Month)).ToHashSet();
        return contract with
        {
            ModelVersion = !scope.HasModel ? contract.ModelVersion : contract.ModelVersion + "/" + ModelPolicy,
            Time = scope.NextEvent is {} boundary ? ValuationTimeDependency.Between(context.CalculationAt, boundary) : contract.Time,
            Volatility = instrument.Marks is {} marks ? [..marks.OrderedVolatilities.Where(v => used.Contains((v.Curve, v.Month))).Select(v => v.Volatility)] : []
        };
    }
}
