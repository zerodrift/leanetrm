using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Trading;

public readonly record struct SidePaymentKey(Guid SideId, int Ordinal, DateTimeOffset PaymentAt, ReferenceKey Currency);
public sealed record IndexPriceLookup(ReferenceKey Index, QuoteSpecification Quote, CurveRequest Market,
    MarketContract Contract, Quantity? AveragingQuantity, IndexFixing? Fixing = null);
public enum SchedulePriceState { Fixed = 1, Projected, Actual }
public enum DiscountFactorSource { Explicit = 1, CurrencyDefault, Undiscounted }
public sealed record SidePaymentFactor(SidePaymentKey Payment, ExactDecimal DiscountFactor,
    MarketSnapshot Snapshot, DiscountFactorSource Source);

public interface ISideDiscounting
{
    ImmutableArray<SidePaymentFactor> Resolve(SidePaymentSchedule schedule, MarketSnapshot snapshot);
}

// Explicit scalar discounting is an arithmetic input, not currency-default
// resolution. The latter is supplied by the rates operation under its context.
public sealed class ExplicitDiscounting : ISideDiscounting
{
    private readonly ExactDecimal factor;
    private readonly DiscountFactorSource source;
    public ExplicitDiscounting(ExactDecimal factor, DiscountFactorSource source)
    {
        if (factor.CompareTo(default) <= 0 || source is not (DiscountFactorSource.Explicit or DiscountFactorSource.Undiscounted) ||
            source == DiscountFactorSource.Undiscounted && factor != ExactDecimal.Parse("1"))
            throw new MarketDataException("side_payment_factors", "Supply a positive explicit factor; explicitly undiscounted payments require one.");
        this.factor = factor; this.source = source;
    }
    public ImmutableArray<SidePaymentFactor> Resolve(SidePaymentSchedule schedule, MarketSnapshot snapshot) =>
        schedule.Payments.Select(p => new SidePaymentFactor(p.Key, factor, snapshot, source)).ToImmutableArray();
}

public sealed record SchedulePriceTerm
{
    public ExactDecimal Weight { get; }
    public UnitPrice? FixedPrice { get; }
    public IndexPriceLookup? Lookup { get; }
    internal SchedulePriceTerm(ExactDecimal weight, UnitPrice? fixedPrice, IndexPriceLookup? lookup)
    {
        if (weight.CompareTo(default) <= 0 || (fixedPrice is null) == (lookup is null))
            throw new MarketDataException("schedule_price", "A price term requires a positive weight and exactly one fixed price or exact index lookup.");
        Weight = weight; FixedPrice = fixedPrice; Lookup = lookup;
    }
}

public sealed record SidePaymentPlan
{
    public SidePaymentKey Key { get; }
    public Guid SideId => Key.SideId;
    public DateOnly DeliveryFrom { get; }
    public DateOnly DeliveryTo { get; }
    public DateTimeOffset PaymentAt => Key.PaymentAt;
    public DateOnly PaymentDate => DateOnly.FromDateTime(PaymentAt.UtcDateTime);
    public GeneratedPaymentDate? GeneratedDate { get; }
    public PaymentRule? PaymentConvention { get; }
    public ReferenceKey Currency => Key.Currency;
    public Quantity UnitQuantity { get; }
    // Allocation per external strip unit; fractions remain exact through cash/PV.
    public ExactQuantity AllocatedQuantity { get; }
    public DateTimeOffset? FirstFixingAt { get; }
    public FloatingAveraging? Averaging { get; }
    public SwapObservationCalendar? Calendar { get; }
    public ImmutableArray<SchedulePriceTerm> Prices { get; }
    public string Implementation { get; }
    internal SidePaymentPlan(Guid sideId, DateOnly from, DateOnly to, DateTimeOffset paymentAt,
        ReferenceKey currency, Quantity unitQuantity, DateTimeOffset? firstFixingAt,
        FloatingAveraging? averaging, SwapObservationCalendar? calendar,
        ImmutableArray<SchedulePriceTerm> prices, string implementation,GeneratedPaymentDate? generatedDate=null,PaymentRule? paymentConvention=null)
    {
        if((generatedDate is null)!=(paymentConvention is null))throw new MarketDataException("payment_rule","Generated dates retain their originating payment convention.");
        PaymentConvention=paymentConvention; GeneratedDate=generatedDate; Key = new(sideId, 0, paymentAt, currency); DeliveryFrom = from; DeliveryTo = to;
        UnitQuantity = unitQuantity; AllocatedQuantity = new(1, 1, unitQuantity.Specification); FirstFixingAt = firstFixingAt;
        Averaging = averaging; Calendar = calendar; Prices = prices; Implementation = implementation;
    }
    internal SidePaymentPlan(SidePaymentPlan period, Guid sideId, int ordinal, ExactQuantity allocation)
        : this(sideId, period.DeliveryFrom, period.DeliveryTo, period.PaymentAt, period.Currency,
            period.UnitQuantity, period.FirstFixingAt, period.Averaging, period.Calendar, period.Prices,
            period.Implementation, period.GeneratedDate, period.PaymentConvention)
    {
        if (allocation.Specification != UnitQuantity.Specification || allocation.NumeratorValue.Sign <= 0 || ordinal < 0)
            throw new MarketDataException("monthly_quantity_basis", "Each monthly payment requires a positive exact allocation in the leg quantity unit.");
        Key = new(sideId, ordinal, period.PaymentAt, period.Currency); AllocatedQuantity = allocation;
    }
}

public sealed record ResolvedSchedulePrice
{
    public SchedulePriceTerm Term { get; }
    public UnitPrice Price { get; }
    public ImmutableHashSet<ObservationKey> Lineage { get; }
    public SchedulePriceState State { get; }
    internal ResolvedSchedulePrice(SchedulePriceTerm term, UnitPrice price, ImmutableHashSet<ObservationKey> lineage,
        SchedulePriceState? state = null)
    { Term = term; Price = price; Lineage = lineage; State = state ?? (term.FixedPrice is null ? SchedulePriceState.Projected : SchedulePriceState.Fixed); }
}

// No instrument, direction or trade quantity is available to a side valuation.
// It consumes only its own schedule, declared prices and explicit discount inputs.
public sealed class SideValuationContext
{
    private readonly CurveBuild? market;
    private readonly IReadOnlyDictionary<CurveRequest, BuiltValue> prices;
    private readonly ImmutableHashSet<CurveRequest>? permitted;
    private readonly ISideDiscounting? discounting;
    private readonly Func<CurveRequest,ExactUnitPrice>? exactPrices;
    private readonly string? connectedNumericalPolicy;
    private readonly string? connectedScenarioId;
    public MarketSnapshot Snapshot { get; }
    public ISideDiscounting Discounting => discounting??throw new MarketDataException("side_context","Supply this instrument's explicit payment discount inputs.");
    public string CurveNumericalPolicy => connectedNumericalPolicy??CurveModel.NumericalPolicy;
    public string? ScenarioId=>market?.Scenario.Id??connectedScenarioId;
    public SideValuationContext(MarketSnapshot snapshot, ISideDiscounting discounting)
    {
        Validate(snapshot,discounting);
        Snapshot = snapshot; this.discounting = discounting; prices = ImmutableDictionary<CurveRequest, BuiltValue>.Empty;
    }
    internal static void Validate(MarketSnapshot snapshot,ISideDiscounting discounting)
    {
        if (snapshot is null || snapshot.Id == Guid.Empty || snapshot.Definitions.CatalogId == Guid.Empty ||
            snapshot.Definitions.RevisionId == Guid.Empty || !TradingInstant.IsSupported(snapshot.ValuationAt) ||
            !TradingInstant.IsSupported(snapshot.MarketCutoff) || snapshot.MarketCutoff > snapshot.ValuationAt ||
            snapshot.SelectionPolicy != InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering || !snapshot.HasCoherentSelections() || discounting is null)
            throw new MarketDataException("side_context", "An exact supported snapshot and explicit side discounting operation are required.");
    }
    public SideValuationContext(CurveBuild market, ISideDiscounting discounting)
        : this(market?.Snapshot ?? throw new MarketDataException("swap_context", "A built market scenario is required."), discounting)
    {
        if (market.NumericalPolicy != CurveModel.NumericalPolicy)
            throw new MarketDataException("side_context", "Use the exact supported curve numerical policy.");
        this.market = market; prices = market.Values.ToDictionary(v => v.Request);
    }
    // The portfolio owns this already validated scenario and shared lookup.
    // A unit receives only its declared access fence, without rebuilding or
    // rescanning the entire scenario for every instrument.
    internal SideValuationContext(CurveBuild market,ISideDiscounting? discounting,
        IReadOnlyDictionary<CurveRequest,BuiltValue> prices,ImmutableArray<CurveRequest> dependencies)
    {
        this.market=market;Snapshot=market.Snapshot;this.discounting=discounting;
        this.prices=prices;permitted=dependencies.ToImmutableHashSet();
    }
    // Connected calculations retain the same pricer contract and dependency
    // fence. Rational inputs are resolved on demand under their active scenario.
    internal SideValuationContext(MarketSnapshot snapshot,ISideDiscounting? discounting,
        ImmutableArray<CurveRequest> dependencies,Func<CurveRequest,ExactUnitPrice> exactPrices,string numericalPolicy,string scenarioId)
    {
        Snapshot=snapshot;this.discounting=discounting;this.exactPrices=exactPrices;
        prices=ImmutableDictionary<CurveRequest,BuiltValue>.Empty;permitted=dependencies.ToImmutableHashSet();
        connectedNumericalPolicy=numericalPolicy;connectedScenarioId=scenarioId;
    }
    public UnitPrice Price(CurveRequest request)
    {
        if(permitted is not null&&!permitted.Contains(request))
            throw new MarketDataException("risk_dependency","An instrument requested an undeclared market input.");
        if(exactPrices is not null)return exactPrices(request).ToUnitPriceExact();
        return prices.TryGetValue(request,out var value)?value.Price:
            throw new MarketDataException("risk_dependency","An instrument's declared market input is unavailable.");
    }
    public ExactUnitPrice ExactPrice(CurveRequest request)
    {
        if(permitted is not null&&!permitted.Contains(request))
            throw new MarketDataException("risk_dependency","An instrument requested an undeclared market input.");
        return exactPrices is not null?exactPrices(request):ExactUnitPrice.FromPrice(Price(request));
    }
    internal ResolvedSchedulePrice Resolve(SchedulePriceTerm term)
    {
        if (term.FixedPrice is { } fixedPrice) return new(term, fixedPrice, []);
        if (market is null) throw new MarketDataException("side_market_inputs", "This side requires its declared built commodity market inputs.");
        var lookup = term.Lookup!;
        market.Model.RequireIndexProjection(lookup.Market, lookup.Contract, lookup.Index, lookup.Quote);
        if (lookup.Fixing is { } fixing)
        {
            market.Model.RequireIndexFixing(lookup, fixing);
            if (Snapshot.ValuationAt >= fixing.FixingAt)
            {
                if (!market.SelectedInputs.TryGetValue(new(fixing.Series, InputUse.FrozenFact), out var selected))
                    throw new MarketDataException("index_fixing_missing", "The exact published index fixing is required; a forecast cannot replace it.");
                var observation = selected.Observation;
                if (observation.Series != fixing.Series || observation.Source != fixing.Source ||
                    observation.Role != ObservationRole.IndexFixing || observation.State != PublicationState.Final ||
                    !TradingInstant.IsSupported(observation.ObservedAt) || observation.ObservedAt < fixing.FixingAt || observation.ObservedAt > Snapshot.MarketCutoff)
                    throw new MarketDataException("index_fixing_selection", "The pinned actual must match the qualified series, source, final role and publication interval.");
                return new(term, new(observation.Value, lookup.Quote), ImmutableHashSet.Create(observation.Key), SchedulePriceState.Actual);
            }
        }
        if (permitted is not null && !permitted.Contains(lookup.Market))
            throw new MarketDataException("risk_dependency", "A side requested an undeclared market input.");
        if (!prices.TryGetValue(lookup.Market, out var value) || value.Quote != lookup.Quote.Key)
            throw new MarketDataException("missing_observation", "Every exact scheduled index projection is required in the selected market scenario.");
        return new(term, value.Price, value.Lineage);
    }
}

public sealed record SideResetValue
{
    public ResolvedSchedulePrice Price { get; }
    public ExactMoney CashContribution { get; }
    internal SideResetValue(ResolvedSchedulePrice price,ExactMoney cashContribution)
    {Price=price;CashContribution=cashContribution;}
}

public sealed record SidePaymentValue
{
    public SidePaymentPlan Plan { get; }
    public ImmutableArray<ResolvedSchedulePrice> Prices { get; }
    public ExactMoney CashAmount { get; }
    public ImmutableArray<SideResetValue> Resets { get; }
    public SidePaymentFactor Discount { get; }
    public ExactMoney PresentValue { get; }
    internal SidePaymentValue(SidePaymentPlan plan, ImmutableArray<ResolvedSchedulePrice> prices, SidePaymentFactor discount)
    {
        Plan = plan; Prices = prices; Discount = discount;
        CashAmount = PriceAmounts.Sum(plan.Currency, plan.UnitQuantity,
            [new(new(plan.SideId, prices.Select(p => new WeightedPrice(p.Price, p.Term.Weight)).ToImmutableArray(), plan.UnitQuantity),
                ExactDecimal.Parse("1"))], [ExactDecimal.Parse("1")]).Scale(plan.AllocatedQuantity, plan.UnitQuantity.Specification);
        Resets=ExactMoney.WeightedTerms(plan.Currency,prices.Select(p=>(p.Price.Value,p.Term.Weight)),
            [prices[0].Price.Unit.Multiplier,plan.UnitQuantity.Amount]).Select((amount,n)=>new SideResetValue(prices[n],
                amount.Scale(plan.AllocatedQuantity, plan.UnitQuantity.Specification))).ToImmutableArray();
        PresentValue = CashAmount.Scale(discount.DiscountFactor);
    }
}

public sealed record SidePaymentSchedule
{
    public Guid SideId { get; }
    public ReferenceKey Currency { get; }
    public Quantity UnitQuantity { get; }
    public string Implementation { get; }
    public ImmutableArray<SidePaymentPlan> Payments { get; }
    public ImmutableArray<CurveRequest> Dependencies => Payments.SelectMany(p => p.Prices)
        .Where(p => p.Lookup is not null).Select(p => p.Lookup!.Market).Distinct().ToImmutableArray();
    public ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot)
    {
        if (snapshot is null || !TradingInstant.IsSupported(snapshot.ValuationAt))
            throw new MarketDataException("side_context", "An explicit supported valuation instant is required.");
        return Payments.SelectMany(p => p.Prices).Where(p => p.Lookup is { } lookup &&
                (lookup.Fixing is null || snapshot.ValuationAt < lookup.Fixing.FixingAt))
            .Select(p => p.Lookup!.Market).Distinct().ToImmutableArray();
    }
    internal SidePaymentSchedule(SidePaymentPlan payment)
        : this(payment.SideId, payment.Currency, payment.UnitQuantity, payment.Implementation, [payment]) { }
    internal SidePaymentSchedule(Guid sideId, ReferenceKey currency, Quantity unitQuantity, string implementation,
        ImmutableArray<SidePaymentPlan> payments)
    {
        if (payments.IsDefaultOrEmpty || payments.Where((p, n) => p is null || p.Key.SideId != sideId ||
                p.Key.Ordinal != n || p.Currency != currency || p.UnitQuantity != unitQuantity).Any())
            throw new MarketDataException("side_payment_schedule", "Every payment must retain its leg, ordinal, currency and normalized quantity unit.");
        SideId = sideId; Currency = currency; UnitQuantity = unitQuantity;
        Implementation = implementation; Payments = payments;
    }
    // A due payment needs explicit lifecycle state: it is never priced or discounted.
    public void RequireUndue(MarketSnapshot snapshot)
    {
        if (snapshot is null) throw new MarketDataException("side_context", "An explicit valuation snapshot is required.");
        foreach (var payment in Payments) RequireUndue(payment, snapshot);
    }
    private static void RequireUndue(SidePaymentPlan payment, MarketSnapshot snapshot)
    {
        if (snapshot.ValuationAt >= payment.PaymentAt)
            throw new MarketDataException("side_payment_due", "A due side payment requires explicit lifecycle state before valuation.");
    }
    internal SideValuation Evaluate(SideValuationContext context)
    {
        if (context is null) throw new MarketDataException("side_context", "A side valuation context is required.");
        var resolved = Payments.Select(payment =>
        {
            if (payment.FirstFixingAt is { } fixing && context.Snapshot.ValuationAt >= fixing)
                throw new MarketDataException("swap_fixing_required", "This projection-only side requires retained actual fixings from its first fixing onward.");
            RequireUndue(payment, context.Snapshot);
            return payment.Prices.Select(context.Resolve).ToImmutableArray();
        }).ToImmutableArray();
        var factors = context.Discounting.Resolve(this, context.Snapshot);
        if (factors.IsDefault || factors.Length != Payments.Length || factors.Any(f => f is null ||
            f.DiscountFactor.CompareTo(default) <= 0 || !Enum.IsDefined(f.Source) ||
            f.Source == DiscountFactorSource.Undiscounted && f.DiscountFactor != ExactDecimal.Parse("1") ||
            !context.Snapshot.HasSameInputs(f.Snapshot)) || factors.Select(f => f.Payment).Distinct().Count() != factors.Length ||
            !factors.Select(f => f.Payment).ToHashSet().SetEquals(Payments.Select(p => p.Key)))
            throw new MarketDataException("side_payment_factors", "Supply one positive factor for each exact side payment and complete context, without missing or extra rows.");
        var selected = factors.ToDictionary(f => f.Payment);
        return new(this, context, Payments.Select((p, n) => new SidePaymentValue(p, resolved[n], selected[p.Key])).ToImmutableArray());
    }
}

public sealed record SideValuation
{
    public const string Policy = "independent_side_rational_pv_v1";
    public string NumericalPolicy => Policy;
    public Guid SideId => Schedule.SideId;
    public ReferenceKey Currency => Schedule.Currency;
    public Quantity UnitQuantity => Schedule.UnitQuantity;
    public SidePaymentSchedule Schedule { get; }
    internal SideValuationContext Context { get; }
    public MarketSnapshot Snapshot => Context.Snapshot;
    public ImmutableArray<SidePaymentValue> Payments { get; }
    public ExactMoney PresentValue { get; }
    internal SideValuation(SidePaymentSchedule schedule, SideValuationContext context, ImmutableArray<SidePaymentValue> payments)
    {
        Schedule = schedule; Context = context; Payments = payments;
        PresentValue = payments.Select(p => p.PresentValue).Aggregate((a, b) => a.Add(b));
    }
}

public sealed record SideContribution
{
    public Guid SideId => Value.SideId;
    public ExactDecimal Coefficient { get; }
    public SideValuation Value { get; }
    public ExactMoney Contribution { get; }
    internal SideContribution(ExactDecimal coefficient, SideValuation value)
    { Coefficient = coefficient; Value = value; Contribution = value.PresentValue.Scale(coefficient); }
}

public sealed record InstrumentValuation
{
    public const string Policy = "signed_exact_side_pv_sum_v1";
    public Guid InstrumentId { get; }
    public ReferenceKey Currency { get; }
    public Quantity UnitQuantity { get; }
    public MarketSnapshot Snapshot { get; }
    internal SideValuationContext Context { get; }
    public ImmutableArray<SideContribution> Sides { get; }
    public ExactMoney PresentValue { get; }
    public string NumericalPolicy { get; }
    public InstrumentValuation(Instrument instrument,SideValuationContext context,ExactMoney presentValue,string numericalPolicy)
        :this(instrument,context,numericalPolicy)
    {
        if(presentValue is null||presentValue.Currency!=instrument.Currency)
            throw new MarketDataException("instrument_contribution","A unit PV must retain its instrument's exact currency.");
        PresentValue=presentValue;
    }
    private InstrumentValuation(Instrument instrument,SideValuationContext context,string numericalPolicy)
    {
        if(instrument is null||context is null||string.IsNullOrWhiteSpace(numericalPolicy))
            throw new MarketDataException("instrument_context","A unit PV requires an exact instrument, valuation context and numerical policy.");
        InstrumentId=instrument.Id;Currency=instrument.Currency;UnitQuantity=instrument.UnitQuantity;Context=context;Snapshot=context.Snapshot;
        NumericalPolicy=numericalPolicy;Sides=[];PresentValue=null!;
    }
    internal InstrumentValuation(SidedInstrument instrument, SideValuationContext context, ImmutableArray<SideValuation> values)
        :this(instrument,context,Policy)
    {
        if (values.IsDefault || values.Length != instrument.Sides.Length || values.Where((v, n) => v is null ||
            v.SideId != instrument.Sides[n].Side.Id || v.Currency != instrument.Currency || v.UnitQuantity != instrument.UnitQuantity).Any())
            throw new MarketDataException("instrument_contribution", "Every membership requires its own exact side valuation in the instrument currency and unit basis.");
        if (values.Any(v => !ReferenceEquals(context, v.Context)))
            throw new MarketDataException("instrument_context", "All side PVs must belong to this valuation operation, including its exact scenario and discount inputs.");
        Sides = values.Select((v, n) => new SideContribution(instrument.Sides[n].Coefficient, v)).ToImmutableArray();
        PresentValue = Sides.Select(s => s.Contribution).Aggregate((a, b) => a.Add(b));
    }
}
