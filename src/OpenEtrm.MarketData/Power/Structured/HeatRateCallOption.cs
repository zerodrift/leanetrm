using System.Collections.Immutable;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Commands;

namespace OpenEtrm.MarketData.Power.Structured;

// One delivery day of the strip. The holder decides at ExerciseAt whether that
// day's energy is worth power − heat rate × fuel − strike; cash follows at PaymentAt.
// The bucket names the volatility and correlation marks the day is valued with.
[EconomicDefinition("heat_rate_call_exercise_v1")]
public sealed record HeatRateCallExercise(
    [property: IdentityField] DateOnly DeliveryDay, [property: IdentityField] DateTimeOffset ExerciseAt,
    [property: IdentityField] DateTimeOffset PaymentAt, [property: IdentityField] ExactDecimal EnergyPerUnit,
    [property: IdentityField] CurveRequest Power, [property: IdentityField] CurveRequest Fuel,
    [property: IdentityField] Guid MarkBucket);

// A cash-settled toll written as a strip of daily calls on the spark spread.
// Capacity is the external quantity; each exercise states its own energy per unit of it.
[EconomicDefinition("heat_rate_call_terms_v1")]
public sealed record HeatRateCallTerms(
    [property: IdentityField] ReferenceKey Capacity, [property: IdentityField] QuoteSpecification PowerQuote,
    [property: IdentityField] QuoteSpecification FuelQuote, [property: IdentityField] QuantityFactor HeatRate,
    [property: IdentityField] UnitPrice Strike, [property: IdentityField] string ExerciseStyle,
    [property: IdentityField] string Model, [property: IdentityField] string DayCount,
    [property: IdentityField] ImmutableArray<HeatRateCallExercise> Exercises)
{
    public const string DailyAutomaticCash = "daily_automatic_cash_exercise_v1";
    public const string ActualOver365 = OptionNumericalPolicies.ActualOver365FixedInstant;
    public const int MaximumExercises = 3_660;

    internal HeatRateCallTerms Require()
    {
        var one = ExactDecimal.Parse("1");
        if (PowerQuote is null || FuelQuote is null || HeatRate is null || Strike is null || Exercises.IsDefaultOrEmpty ||
            Exercises.Length > MaximumExercises || string.IsNullOrWhiteSpace(Capacity.Code) ||
            PowerQuote.Kind != QuoteKind.Outright || FuelQuote.Kind != QuoteKind.Outright ||
            PowerQuote.Currency != FuelQuote.Currency || PowerQuote.Multiplier != one || FuelQuote.Multiplier != one ||
            HeatRate.NumeratorQuantity != FuelQuote.Quantity || HeatRate.DenominatorQuantity != PowerQuote.Quantity ||
            Strike.Unit != PriceUnit.FromQuote(PowerQuote) || Strike.Value.CompareTo(default) < 0 ||
            ExerciseStyle != DailyAutomaticCash || Model != KirkSpreadCall.Implementation || DayCount != ActualOver365 ||
            Exercises.Any(e => e is null || e.EnergyPerUnit.CompareTo(default) <= 0 || e.MarkBucket == Guid.Empty ||
                !TradingInstant.IsSupported(e.ExerciseAt) || !TradingInstant.IsSupported(e.PaymentAt) || e.PaymentAt < e.ExerciseAt) ||
            Exercises.Zip(Exercises.Skip(1)).Any(p => p.Second.ExerciseAt < p.First.ExerciseAt || p.Second.DeliveryDay <= p.First.DeliveryDay))
            throw new MarketDataException("heat_rate_call_terms",
                "A heat-rate call strip needs outright power and fuel quotes in one currency, a heat rate from fuel to power, " +
                "a nonnegative strike in the power price unit, the installed exercise, model and day-count laws, and delivery days " +
                "in order, each with positive energy, a mark bucket and payment at or after exercise.");
        return this;
    }
}

// Annualized log-return volatilities of the two forwards and their correlation for
// one bucket of delivery days. They are trader marks held through price scenarios.
public sealed record SpreadCallMark(Guid Bucket, VolatilityInput PowerVolatility, VolatilityInput FuelVolatility, string Correlation);

[EconomicDefinition("heat_rate_call_marks_v1")]
public sealed record HeatRateCallMarks(ImmutableArray<SpreadCallMark> Buckets, [property: IdentityField] string Convention)
{
    public const string HeldLogReturn = "spread_marks_log_return_held_v1";
    [IdentityField] public ImmutableArray<string> Canonical => [..Buckets.Select(b =>
        $"{b.Bucket:D}|{b.PowerVolatility.Id:D}|{b.PowerVolatility.Value}|{b.FuelVolatility.Id:D}|{b.FuelVolatility.Value}|{b.Correlation}")];

    internal ImmutableDictionary<Guid, SpreadCallMark> Require(HeatRateCallTerms terms)
    {
        var used = terms.Exercises.Select(e => e.MarkBucket).ToHashSet();
        if (Convention != HeldLogReturn || Buckets.IsDefaultOrEmpty ||
            Buckets.Any(b => b is null || b.PowerVolatility is null || b.FuelVolatility is null || !Valid(b.Correlation)) ||
            !Buckets.Select(b => b.Bucket).ToHashSet().SetEquals(used) || Buckets.Length != used.Count ||
            Buckets.SelectMany(b => new[] { b.PowerVolatility.Id, b.FuelVolatility.Id }).Distinct().Count() != 2 * Buckets.Length)
            throw new MarketDataException("heat_rate_call_marks",
                "Supply one mark for exactly each bucket the strip uses: distinct exact power and fuel volatilities and a " +
                "correlation from -1 to 1 under the installed held log-return convention.");
        return Buckets.ToImmutableDictionary(b => b.Bucket);
    }
    private static bool Valid(string? correlation)
    {
        if (correlation is null) return false;
        try
        {
            var value = LatticeDecimal.Parse(correlation);
            return value.CompareTo(LatticeDecimal.Parse("1")) <= 0 && value.CompareTo(LatticeDecimal.Parse("-1")) >= 0;
        }
        catch (MarketDataException) { return false; }
    }
}

[EconomicDefinition("heat_rate_call_side_v1")]
public sealed record HeatRateCallSide : ValueSide
{
    public const string Role = "heat_rate_call_strip";
    private static readonly LatticeDecimal One = LatticeDecimal.Parse("1"),
        TicksPerYear = LatticeDecimal.Parse((365 * TimeSpan.TicksPerDay).ToString(System.Globalization.CultureInfo.InvariantCulture));
    [IdentityField] public HeatRateCallTerms CallTerms { get; }
    public HeatRateCallMarks? Marks { get; }
    private readonly ImmutableDictionary<Guid, SpreadCallMark>? marks;
    public override DefinitionIdentity? PricingBindingIdentity => Marks is null ? null : DefinitionIdentity.For(Marks);
    public override ImmutableArray<CurveRequest> Dependencies =>
        [..CallTerms.Exercises.SelectMany(e => new[] { e.Power, e.Fuel }).Distinct()];

    internal HeatRateCallSide(Guid instrument, HeatRateCallTerms terms, HeatRateCallMarks? marks)
        : base(OperationIdentity.For("heat_rate_call_side_v1", instrument),
            (terms ?? throw new MarketDataException("heat_rate_call_terms", "Supply the strip's terms.")).Require().PowerQuote.Currency,
            new(ExactDecimal.Parse("1"), terms.Capacity), HeatRateCallInstrument.ModelVersion,
            [..terms.Exercises.Select((e, n) => new ValueSideTerm(n, ComponentKind.Holding, null, null, e.ExerciseAt, null, e.Power))])
    {
        CallTerms = terms; Marks = marks; this.marks = marks?.Require(terms);
    }

    private SidePaymentKey Payment(int ordinal) =>
        new(Id, ordinal, CallTerms.Exercises[ordinal].PaymentAt, Currency) { Timing = ValueTimingKind.Instant };

    // The first installed profile values days still ahead of the clock. A day at or
    // past its exercise instant is a determined cash amount, which this profile does not own.
    private ImmutableDictionary<Guid, SpreadCallMark> RequireLive(DateTimeOffset at, bool lifecycle)
    {
        if (marks is null)
            throw new MarketDataException("heat_rate_call_marks", "The strip is unpriced until its volatility and correlation marks are selected.");
        if (lifecycle)
            throw new MarketDataException("heat_rate_call_exercise", "The strip has no projected lifecycle valuation.");
        if (CallTerms.Exercises[0].ExerciseAt <= at)
            throw new MarketDataException("heat_rate_call_exercise",
                "A delivery day at or past its exercise needs its recorded exercise determination; the date alone supplies no value.");
        return marks;
    }

    internal UnitPricingContract Declare(UnitValuationContext context, Guid instrument)
    {
        var selected = RequireLive(context.CalculationAt, context.IsLifecycleProjection);
        var provider = context.PaymentDiscounting(instrument);
        return new(instrument, HeatRateCallInstrument.ModelVersion, Dependencies, CallTerms.Capacity, Currency)
        {
            Time = ValuationTimeDependency.Between(context.CalculationAt, CallTerms.Exercises[0].ExerciseAt),
            DiscountInputs = [..CallTerms.Exercises.Select((_, n) => new DiscountInput(Payment(n), provider))],
            Volatility = [..Marks!.Buckets.SelectMany(b => new[] { b.PowerVolatility, b.FuelVolatility })]
        };
    }

    protected override ImmutableArray<ScheduleRowValue> Evaluate(SideValuationContext context, IReadOnlyList<ScheduleRow> rows)
    {
        var scope = context.UnitEvaluation; var selected = RequireLive(scope.CalculationAt, scope.IsLifecycleProjection);
        if (context.BindingState is not null)
            throw new MarketDataException("settlement_scope", "The option strip has no external settlement-state interpretation.");
        var inputs = context.Inputs;
        var factors = inputs.Discounts([..CallTerms.Exercises.Select((_, n) => Payment(n))]);
        var heatRate = LatticeDecimal.From(CallTerms.HeatRate.Value); var strike = LatticeDecimal.From(CallTerms.Strike.Value);
        var values = ImmutableArray.CreateBuilder<ScheduleRowValue>(rows.Count);
        for (var n = 0; n < rows.Count; n++)
        {
            scope.Cancellation.ThrowIfCancellationRequested();
            var exercise = CallTerms.Exercises[n]; var mark = selected[exercise.MarkBucket];
            var years = LatticeDecimal.Parse((exercise.ExerciseAt - scope.CalculationAt).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)) / TicksPerYear;
            // The forward premium is rounded once, half-even, to the working scale before energy is applied.
            var premium = KirkSpreadCall.UnitPrice(new(Price(inputs, exercise.Power, CallTerms.PowerQuote),
                Price(inputs, exercise.Fuel, CallTerms.FuelQuote) * heatRate, strike, Volatility(inputs, mark.PowerVolatility),
                Volatility(inputs, mark.FuelVolatility), LatticeDecimal.Parse(mark.Correlation), years, One)).ToExact();
            var amount = ExactMoney.FromMoney(new Money(premium, Currency)).Scale(exercise.EnergyPerUnit);
            if (factors[n] is null || factors[n].Payment != Payment(n))
                throw new MarketDataException("side_payment_factors", "Every exercise needs its exact declared payment factor.");
            values.Add(Value(rows[n], amount, amount.Scale(factors[n].DiscountFactor), exercise.PaymentAt, discount: factors[n]));
        }
        return values.MoveToImmutable();
    }

    private static LatticeDecimal Price(PricingInputs inputs, CurveRequest request, QuoteSpecification quote)
    {
        var price = inputs.ExactPrice(request).ToUnitPriceExact();
        if (price.Unit != PriceUnit.FromQuote(quote))
            throw new MarketDataException("heat_rate_call_terms", "A forward price must be in its contractual quote unit.");
        return LatticeDecimal.From(price.Value);
    }
    private static LatticeDecimal Volatility(PricingInputs inputs, VolatilityInput input) =>
        LatticeDecimal.Parse(inputs.Volatility(input.Id).Format(30, MidpointRounding.ToEven));
}

[EconomicDefinition("heat_rate_call_instrument_v1")]
public sealed record HeatRateCallInstrument : SidedInstrument
{
    public const string ModelVersion = "heat_rate_call_daily_strip_kirk_side_pv_v1";
    public HeatRateCallSide CallSide => (HeatRateCallSide)Sides[0].Side;
    public HeatRateCallTerms Terms => CallSide.CallTerms;
    public override ReferenceKey Currency => CallSide.Currency;
    public override Quantity UnitQuantity => CallSide.UnitQuantity;
    public override DefinitionIdentity? PricingBindingIdentity => CallSide.PricingBindingIdentity;
    internal override bool SupportsSettlement => false;

    public HeatRateCallInstrument(Guid id, HeatRateCallTerms terms, HeatRateCallMarks? marks)
        : base(id, [new(new HeatRateCallSide(id, terms, marks), ExactDecimal.Parse("1"))]) { }

    public override UnitPricingContract Declare(UnitValuationContext context)
    {
        if (context is null || context.Measure != ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure", "A heat-rate call strip implements present value only.");
        return CallSide.Declare(context, Id);
    }
    public override void ValidateValuationState(IBindingValuationState? state)
    {
        if (state is not null)
            throw new MarketDataException("settlement_scope", "The option strip has no external settlement-state interpretation.");
    }
    internal override UnitPricingContract DeclareScope(UnitValuationContext context, bool lifecycle) => Declare(context);
}
