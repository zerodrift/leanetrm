using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public readonly record struct SidePaymentKey(Guid SideId, int Ordinal, DateTimeOffset PaymentAt, ReferenceKey Currency)
{
    public void RequireUndue(MarketSnapshot snapshot)
    {
        if(snapshot is null)throw new MarketDataException("side_context","An explicit valuation snapshot is required.");
        if(SideId==Guid.Empty || Ordinal<0 || !TradingInstant.IsSupported(PaymentAt) || Currency.Revision.CatalogId==Guid.Empty ||
            Currency.Revision.RevisionId==Guid.Empty || string.IsNullOrWhiteSpace(Currency.Code))
            throw new MarketDataException("side_payment_factors","Supply an exact side, ordinal, UTC value instant and currency reference.");
        if(snapshot.ValuationAt>=PaymentAt)throw new MarketDataException("side_payment_due","A due side value requires explicit lifecycle treatment, not a discount factor.");
    }
}
public sealed record IndexPriceLookup(PriceSettlementSelection Settlement, QuoteSpecification Quote, CurveRequest Market,
    MarketContract Contract, Quantity? AveragingQuantity, IndexFixing? Fixing = null,
    CustomCurveProjectionBinding? CustomProjection = null)
{
    public ReferenceKey Index => Settlement.SelectedIndex;
    public CurveRequest ProjectionRequest => CustomProjection?.Request(Market) ?? Market;
}
public enum SchedulePriceState { Fixed = 1, Projected, Actual }
public enum DiscountFactorSource { Explicit = 1, CurrencyDefault, Undiscounted }
public sealed record SidePaymentFactor(SidePaymentKey Payment, ExactDecimal DiscountFactor,
    MarketSnapshot Snapshot, DiscountFactorSource Source)
{
    public RateDiscountDefinition? RateDefinition { get; init; }
}

public interface ISideDiscounting
{
    ImmutableArray<SidePaymentFactor> Resolve(SidePaymentSchedule schedule, MarketSnapshot snapshot) =>
        Resolve(schedule.Payments.Select(p=>p.Key).ToImmutableArray(),snapshot);
    ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot);
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
        Resolve(schedule.Payments.Select(p=>p.Key).ToImmutableArray(),snapshot);
    public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)
    {
        SideValuationContext.Validate(snapshot,this);
        if(payments.IsDefault || payments.Distinct().Count()!=payments.Length)
            throw new MarketDataException("side_payment_factors","Supply distinct exact side value keys.");
        foreach(var payment in payments)payment.RequireUndue(snapshot);
        return payments.Select(p=>new SidePaymentFactor(p,factor,snapshot,source)).ToImmutableArray();
    }
}

public sealed record SchedulePriceTerm
{
    public ExactDecimal Weight { get; }
    public FixedPriceTerm? FixedPrice { get; }
    public DateOnly? FixedPriceDate { get; }
    public IndexPriceLookup? Lookup { get; }
    public ContractObservationLookup? ContractObservation { get; }
    public UnitPrice? Spread { get; }
    internal SchedulePriceTerm(ExactDecimal weight, UnitPrice? fixedPrice, IndexPriceLookup? lookup,UnitPrice? spread=null)
    {
        if (weight.CompareTo(default) <= 0 || (fixedPrice is null) == (lookup is null))
            throw new MarketDataException("schedule_price", "A price term requires a positive weight and exactly one fixed price or exact index lookup.");
        if(spread is not null&&(lookup is null||spread.Unit!=PriceUnit.FromQuote(lookup.Quote)))throw new MarketDataException("swap_spread","A floating spread requires its exact index price unit.");
        Weight = weight; FixedPrice = fixedPrice is null?null:new FlatPrice(fixedPrice); Lookup = lookup; Spread=spread;
    }
    internal SchedulePriceTerm(ExactDecimal weight,ContractObservationLookup lookup,UnitPrice? spread=null)
    {
        lookup.Validate();
        if(weight.CompareTo(default)<=0||spread is not null&&spread.Unit!=PriceUnit.FromQuote(lookup.Quote))
            throw new MarketDataException("schedule_price","An observation requires positive weight and an exact quote spread.");
        Weight=weight;ContractObservation=lookup;Spread=spread;
    }
    internal SchedulePriceTerm(ExactDecimal weight,FixedPriceTerm fixedPrice,DateOnly operatingDate)
    {
        if(weight.CompareTo(default)<=0||fixedPrice is null)
            throw new MarketDataException("schedule_price","A fixed price reference requires an exact term, operating date and positive weight.");
        Weight=weight;FixedPrice=fixedPrice;FixedPriceDate=operatingDate;
    }
}

// Each original target interval remains linked to the exact term priced once.
// Several applications may deliberately reference the same observation term.
public sealed record SchedulePriceApplication
{
    public SidePaymentKey Payment { get; }
    public DateOnly TargetFrom { get; }
    public DateOnly TargetTo { get; }
    public int PriceOrdinal { get; }
    public SchedulePriceTerm Price { get; }
    public ExactDecimal Weight { get; }
    public string MappingRule { get; }
    internal SchedulePriceApplication(SidePaymentKey payment,FloatingObservationApplication application,SchedulePriceTerm price)
    {
        Payment=payment;TargetFrom=application.TargetFrom;TargetTo=application.TargetTo;
        PriceOrdinal=application.ObservationOrdinal;Price=price;Weight=application.Weight;MappingRule=application.MappingRule;
    }
    internal SchedulePriceApplication Rebind(SidePaymentKey payment)=>new(payment,
        new(TargetFrom,TargetTo,PriceOrdinal,Weight,MappingRule),Price);
}

internal sealed record DatedScheduleRow
{
    internal RetainedScheduleRowReference? RetainedReference { get; init; }
    public SidePaymentKey Key { get; }
    public Guid SideId => Key.SideId;
    public int Ordinal { get; }
    public ScheduleDeliveryWindow DeliveryWindow { get; }
    public ScheduleQuantityReference QuantityReference { get; }
    public DateOnly DeliveryFrom { get; }
    public DateOnly DeliveryTo { get; }
    public DateTimeOffset PaymentAt => Key.PaymentAt;
    public DateOnly PaymentDate => DateOnly.FromDateTime(PaymentAt.UtcDateTime);
    public GeneratedPaymentDate? GeneratedDate { get; }
    public PaymentRule? PaymentConvention { get; }
    public ReferenceKey Currency => Key.Currency;
    public Quantity UnitQuantity { get; }
    // Allocation per external strip unit; fractions remain exact through cash/PV.
    private readonly ExactQuantity? allocation;
    public ExactQuantity AllocatedQuantity => allocation??throw new MarketDataException("schedule_quantity_reference","An interval row's quantity is resolved from its declared source in the valuation graph.");
    public DateTimeOffset? FirstFixingAt { get; }
    public FloatingAveraging? Averaging { get; }
    public SwapObservationCalendar? Calendar { get; }
    public ImmutableArray<SchedulePriceTerm> Prices { get; }
    public ImmutableArray<SchedulePriceApplication> Applications { get; }
    public string Implementation { get; }
    internal DatedScheduleRow(Guid sideId, DateOnly from, DateOnly to, DateTimeOffset paymentAt,
        ReferenceKey currency, Quantity unitQuantity, DateTimeOffset? firstFixingAt,
        FloatingAveraging? averaging, SwapObservationCalendar? calendar,
        ImmutableArray<SchedulePriceTerm> prices, string implementation,ImmutableArray<FloatingObservationApplication> applications,
        string mappingRule,GeneratedPaymentDate? generatedDate=null,PaymentRule? paymentConvention=null)
    {
        if((generatedDate is null)!=(paymentConvention is null))throw new MarketDataException("payment_rule","Generated dates retain their originating payment convention.");
        PaymentConvention=paymentConvention; GeneratedDate=generatedDate; Key = new(sideId, 0, paymentAt, currency); DeliveryFrom = from; DeliveryTo = to;DeliveryWindow=new DatedScheduleWindow(from,to);
        UnitQuantity = unitQuantity; allocation = new(1, 1, unitQuantity.Specification); QuantityReference=new ContractualScheduleQuantity(allocation); FirstFixingAt = firstFixingAt;
        Averaging = averaging; Calendar = calendar; Prices = prices; Implementation = implementation;
        if(prices.IsDefaultOrEmpty||applications.IsDefaultOrEmpty||string.IsNullOrWhiteSpace(mappingRule)||
            applications.Any(a=>a is null||a.ObservationOrdinal<0||a.ObservationOrdinal>=prices.Length||
                a.TargetFrom<from||a.TargetTo>to||a.TargetFrom>=a.TargetTo||a.Weight.CompareTo(default)<=0||a.MappingRule!=mappingRule)||
            prices.Where((p,n)=>p is null||p.Weight.ScaledValue!=applications.Where(a=>a.ObservationOrdinal==n)
                .Aggregate(System.Numerics.BigInteger.Zero,(sum,a)=>sum+a.Weight.ScaledValue)).Any())
            throw new MarketDataException("side_price_applications","Every exact price term requires its complete positive target applications and mapping rule.");
        Applications=applications.Select(a=>new SchedulePriceApplication(Key,a,prices[a.ObservationOrdinal])).ToImmutableArray();
    }
    internal DatedScheduleRow(DatedScheduleRow period, Guid sideId, int ordinal, ExactQuantity allocation)
        : this(sideId, period.DeliveryFrom, period.DeliveryTo, period.PaymentAt, period.Currency,
            period.UnitQuantity, period.FirstFixingAt, period.Averaging, period.Calendar, period.Prices,
            period.Implementation,period.Applications.Select(a=>new FloatingObservationApplication(a.TargetFrom,a.TargetTo,a.PriceOrdinal,a.Weight,a.MappingRule)).ToImmutableArray(),
            period.Applications[0].MappingRule,period.GeneratedDate, period.PaymentConvention)
    {
        if (allocation.Specification != UnitQuantity.Specification || allocation.NumeratorValue.Sign <= 0 || ordinal < 0)
            throw new MarketDataException("monthly_quantity_basis", "Each monthly payment requires a positive exact allocation in the leg quantity unit.");
        Key = new(sideId, ordinal, period.PaymentAt, period.Currency); Ordinal=ordinal;this.allocation = allocation;QuantityReference=new ContractualScheduleQuantity(allocation);
        Applications=Applications.Select(a=>a.Rebind(Key)).ToImmutableArray();
    }
}

public sealed record ResolvedSchedulePrice
{
    public SchedulePriceTerm Term { get; }
    public ExactUnitPrice ExactPrice { get; }
    public UnitPrice Price => ExactPrice.ToUnitPriceExact();
    public ImmutableHashSet<ObservationKey> Lineage { get; }
    public SchedulePriceState State { get; }
    public PriceSettlementSelection? Settlement => Term.Lookup?.Settlement;
    internal ResolvedSchedulePrice(SchedulePriceTerm term, UnitPrice price, ImmutableHashSet<ObservationKey> lineage,
        SchedulePriceState? state = null)
        : this(term, ExactUnitPrice.FromPrice(price), lineage, state) { }
    internal ResolvedSchedulePrice(SchedulePriceTerm term, ExactUnitPrice price, ImmutableHashSet<ObservationKey> lineage,
        SchedulePriceState? state = null)
    { Term = term; ExactPrice = price; Lineage = lineage; State = state ?? (term.FixedPrice is null ? SchedulePriceState.Projected : SchedulePriceState.Fixed); }
}

// No instrument, direction or trade quantity is available to a side valuation.
// It consumes only its own schedule, declared prices and explicit discount inputs.
public sealed partial class SideValuationContext
{
    private readonly Dictionary<Side,SideValuation> sideValues=new(ReferenceEqualityComparer.Instance);
    internal int EvaluatedSides=>sideValues.Count;
    internal SideValuation Evaluate(Side side)
    {
        if(!sideValues.TryGetValue(side,out var value)){value=side.PV(this);sideValues.Add(side,value);}
        return value;
    }
    private readonly CurveBuild? market;
    private readonly ImmutableHashSet<CurveRequest>? permitted;
    private readonly ISideDiscounting? discounting;
    private readonly Func<CurveRequest,ExactUnitPrice>? exactPrices;
    private readonly Func<CurveRequest,ImmutableHashSet<ObservationKey>>? exactLineage;
    private readonly string? connectedNumericalPolicy;
    private readonly string? connectedScenarioId;
    public MarketSnapshot Snapshot { get; }
    public ISideDiscounting Discounting => discounting??throw new MarketDataException("side_context","Supply this instrument's explicit payment discount inputs.");
    public string CurveNumericalPolicy => connectedNumericalPolicy??ConnectedCurves.NumericalPolicy;
    public string? ScenarioId=>observationScenario?.Id??connectedScenarioId;
    public SideValuationContext(MarketSnapshot snapshot, ISideDiscounting discounting)
    {
        Validate(snapshot,discounting);
        Snapshot = snapshot; this.discounting = discounting;
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
        : this(market?.Snapshot ?? throw new MarketDataException("swap_context", "A compiled market scenario is required."), discounting)
    {
        this.market = market;
        var pricing = market.Pricing;
        permitted = market.Requests.ToImmutableHashSet();
        exactPrices = request => pricing.Price(request, default);
        exactLineage = request => pricing.Lineage(request, default);
        connectedNumericalPolicy = pricing.NumericalPolicy;
        connectedScenarioId = pricing.ScenarioId;
    }
    // Connected calculations retain the same pricer contract and dependency
    // fence. Rational inputs are resolved on demand under their active scenario.
    internal SideValuationContext(MarketSnapshot snapshot,ISideDiscounting? discounting,
        ImmutableArray<CurveRequest> dependencies,Func<CurveRequest,ExactUnitPrice> exactPrices,string numericalPolicy,string scenarioId)
    {
        Snapshot=snapshot;this.discounting=discounting;this.exactPrices=exactPrices;
        permitted=dependencies.ToImmutableHashSet();
        connectedNumericalPolicy=numericalPolicy;connectedScenarioId=scenarioId;
    }
    internal SideValuationContext(CurveBuild authority, ISideDiscounting? discounting,
        IEnumerable<CurveRequest> dependencies, Func<CurveRequest,ExactUnitPrice> exactPrices,
        Func<CurveRequest,ImmutableHashSet<ObservationKey>> lineage, string numericalPolicy, string scenarioId)
        : this(authority.Snapshot, discounting, dependencies.ToImmutableArray(), exactPrices, numericalPolicy, scenarioId)
    {
        market = authority; exactLineage = lineage;
    }
    public UnitPrice Price(CurveRequest request) => ExactPrice(request).ToUnitPriceExact();
    public ExactUnitPrice ExactPrice(CurveRequest request)
    {
        if(permitted is not null&&!permitted.Contains(request))
            throw new MarketDataException("risk_dependency","An instrument requested an undeclared market input.");
        return exactPrices is not null?exactPrices(request):
            throw new MarketDataException("risk_dependency","An instrument's declared market input is unavailable.");
    }
    private ContractObservationPricing? observationPricing;
    private ConnectedScenario? observationScenario;
    public SideValuationContext WithContractObservations(ContractObservationPricing pricing,ConnectedScenario scenario)
    {
        if(pricing is null||scenario is null||pricing.Plan.Context.ValuationAt!=Snapshot.ValuationAt||pricing.Plan.Context.MarketAsOf!=Snapshot.MarketCutoff)
            throw ContractObservationLookup.Invalid();
        var copy=(SideValuationContext)MemberwiseClone();copy.observationPricing=pricing;copy.observationScenario=scenario;return copy;
    }
    internal ResolvedSchedulePrice Resolve(SchedulePriceTerm term)
    {
        if (term.FixedPrice is { } fixedPrice) return new(term, fixedPrice.At(term.FixedPriceDate??
            (fixedPrice is FlatPrice?DateOnly.MinValue:throw new MarketDataException("schedule_price","A dated fixed price term requires its operating date."))), []);
        if(term.ContractObservation is {} observation)
        {
            var sampled=(observationPricing??throw ContractObservationLookup.Invalid()).Resolve(observation,observationScenario!);
            return new(term,term.Spread is {} adder?sampled.Price.Add(ExactUnitPrice.FromPrice(adder)):sampled.Price,sampled.Lineage,sampled.State);
        }
        if (market is null) throw new MarketDataException("side_market_inputs", "This side requires its declared built commodity market inputs.");
        var lookup = term.Lookup!;
        var selected = PeriodPriceSelector.Resolve(market, lookup, ExactPrice, request =>
            exactLineage is not null ? exactLineage(request) : throw new MarketDataException("risk_dependency", "The selected projection lineage is unavailable."));
        return new(term,term.Spread is {} spread?selected.Price.Add(ExactUnitPrice.FromPrice(spread)):selected.Price,selected.Lineage,selected.State);
    }
}

public sealed record SideResetValue
{
    public ResolvedSchedulePrice Price { get; }
    public ExactMoney CashContribution { get; }
    internal SideResetValue(ResolvedSchedulePrice price,ExactMoney cashContribution)
    {Price=price;CashContribution=cashContribution;}
}

public sealed record SideApplicationValue
{
    public SchedulePriceApplication Application { get; }
    public ResolvedSchedulePrice Price { get; }
    public ExactMoney CashContribution { get; }
    internal SideApplicationValue(SchedulePriceApplication application,ResolvedSchedulePrice price,ExactMoney cash)
    {Application=application;Price=price;CashContribution=cash;}
}

public record SidePaymentCash
{
    public ScheduleRow Plan { get; }
    public ImmutableArray<ResolvedSchedulePrice> Prices { get; }
    public ExactMoney CashAmount { get; }
    public ImmutableArray<SideResetValue> Resets { get; }
    public ImmutableArray<SideApplicationValue> Applications { get; }
    // The sum may exceed the entered-decimal range while its mean remains valid.
    public string ObservationWeightTotal { get; }
    internal SidePaymentCash(ScheduleRow plan, ImmutableArray<ResolvedSchedulePrice> prices)
    {
        Plan = plan; Prices = prices;
        var weight = prices.Aggregate(System.Numerics.BigInteger.Zero, (sum, price) => sum + price.Term.Weight.ScaledValue);
        ObservationWeightTotal=new AmountFormat(18,CashTieRule.ToEven,false,true)
            .FormatFraction(weight,ExactDecimal.WorkingScale);
        if (prices.Any(p => p.ExactPrice.Unit.Currency != plan.Currency || p.ExactPrice.Unit.Quantity != plan.UnitQuantity.Specification))
            throw new MarketDataException("unit_mismatch", "Scheduled cash requires the exact payment currency and quantity basis.");
        Applications=plan.Applications.Select(application=>
        {
            var price=prices[application.PriceOrdinal];
            var cash=ExactMoney.FromFraction(price.ExactPrice.NumeratorValue*application.Weight.ScaledValue,
                price.ExactPrice.DenominatorValue*weight,plan.Currency)
                .Scale(price.ExactPrice.Unit.Multiplier).Scale(plan.UnitQuantity.Amount)
                .Scale(plan.AllocatedQuantity,plan.UnitQuantity.Specification);
            return new SideApplicationValue(application,price,cash);
        }).ToImmutableArray();
        Resets=prices.Select((price,n)=>new SideResetValue(price,Applications.Where(a=>a.Application.PriceOrdinal==n)
            .Select(a=>a.CashContribution).Aggregate((a,b)=>a.Add(b)))).ToImmutableArray();
        CashAmount=Applications.Select(a=>a.CashContribution).Aggregate((a,b)=>a.Add(b));
    }
    private SidePaymentCash(SidePaymentCash value,ExactDecimal factor)
    {
        Plan=value.Plan;Prices=value.Prices;CashAmount=value.CashAmount.Scale(factor);ObservationWeightTotal=value.ObservationWeightTotal;
        Resets=[..value.Resets.Select(r=>new SideResetValue(r.Price,r.CashContribution.Scale(factor)))];
        Applications=[..value.Applications.Select(a=>new SideApplicationValue(a.Application,a.Price,a.CashContribution.Scale(factor)))];
    }
    internal SidePaymentCash Scale(ExactDecimal factor)=>new(this,factor);
}

public abstract record SideSchedule
{
    public Guid SideId { get; }
    public ReferenceKey Currency { get; }
    public Quantity UnitQuantity { get; }
    public string Implementation { get; }
    public abstract ImmutableArray<CurveRequest> Dependencies { get; }
    public abstract IReadOnlyList<ScheduleRow> Rows { get; }
    public abstract SideValuation Evaluate(SideValuationContext context);
    public virtual ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot)=>Dependencies;
    protected SideSchedule(Guid sideId,ReferenceKey currency,Quantity unitQuantity,string implementation)
    {SideId=sideId;Currency=currency;UnitQuantity=unitQuantity;Implementation=implementation;}
}

public sealed record SidePaymentSchedule : SideSchedule
{
    public ImmutableArray<ScheduleRow> Payments { get; }
    public override IReadOnlyList<ScheduleRow> Rows=>Payments;
    public override ImmutableArray<CurveRequest> Dependencies => Payments.SelectMany(p => p.Prices)
        .Where(p => p.Lookup is not null||p.ContractObservation is not null).Select(p => p.ContractObservation?.Market??p.Lookup!.ProjectionRequest).Distinct().ToImmutableArray();
    public override ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot)
    {
        if (snapshot is null || !TradingInstant.IsSupported(snapshot.ValuationAt))
            throw new MarketDataException("side_context", "An explicit supported valuation instant is required.");
        return Payments.SelectMany(p => p.Prices).Where(p => p.Lookup is not null||p.ContractObservation is not null)
            .Select(p => p.ContractObservation is {} observation?observation.ProjectionAt(snapshot):PeriodPriceSelector.ProjectionAt(p.Lookup!, snapshot)).OfType<CurveRequest>().Distinct().ToImmutableArray();
    }
    internal SidePaymentSchedule(ScheduleRow payment)
        : this(payment.SideId, payment.Currency, payment.UnitQuantity, payment.Implementation, [payment]) { }
    internal SidePaymentSchedule(Guid sideId, ReferenceKey currency, Quantity unitQuantity, string implementation,
        ImmutableArray<ScheduleRow> payments) : base(sideId,currency,unitQuantity,implementation)
    {
        if (payments.IsDefaultOrEmpty || payments.Where((p, n) => !p.IsValid || p.Key.SideId != sideId ||
                p.Key.Ordinal != n || p.Currency != currency || p.UnitQuantity != unitQuantity).Any())
            throw new MarketDataException("side_payment_schedule", "Every payment must retain its leg, ordinal, currency and normalized quantity unit.");
        Payments = payments;
    }
    // A due payment needs explicit lifecycle state: it is never priced or discounted.
    public void RequireUndue(MarketSnapshot snapshot)
    {
        if (snapshot is null) throw new MarketDataException("side_context", "An explicit valuation snapshot is required.");
        foreach (var payment in Payments) RequireUndue(payment, snapshot);
    }
    private static void RequireUndue(ScheduleRow payment, MarketSnapshot snapshot)
    {
        if (snapshot.ValuationAt >= payment.PaymentAt)
            throw new MarketDataException("side_payment_due", "A due side payment requires explicit lifecycle state before valuation.");
    }
    public override PaymentSideValuation Evaluate(SideValuationContext context)
    {
        if (context is null) throw new MarketDataException("side_context", "A side valuation context is required.");
        var resolved = Payments.Select(payment =>
        {
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
        return new(this, context, Payments.Select((p, n) => new ScheduleRowValue(p, resolved[n], selected[p.Key])).ToImmutableArray());
    }
}

public abstract record SideValuation
{
    public const string Policy = "independent_side_rational_pv_v1";
    public virtual string NumericalPolicy => Policy;
    public Guid SideId => Schedule.SideId;
    public ReferenceKey Currency => Schedule.Currency;
    public Quantity UnitQuantity => Schedule.UnitQuantity;
    public SideSchedule Schedule { get; }
    internal SideValuationContext Context { get; }
    public MarketSnapshot Snapshot => Context.Snapshot;
    public ExactMoney PresentValue { get; }
    protected SideValuation(SideSchedule schedule,SideValuationContext context,ExactMoney presentValue)
    {Schedule=schedule;Context=context;PresentValue=presentValue;}
}

public sealed record PaymentSideValuation : SideValuation
{
    public new SidePaymentSchedule Schedule => (SidePaymentSchedule)base.Schedule;
    public ImmutableArray<ScheduleRowValue> Payments { get; }
    internal PaymentSideValuation(SidePaymentSchedule schedule,SideValuationContext context,ImmutableArray<ScheduleRowValue> payments)
        : base(schedule,context,payments.Select(p=>p.RequirePresentValue()).Aggregate((a,b)=>a.Add(b)))
    {Payments=payments;}
}

public sealed record SideContribution
{
    public Guid SideId => Value.SideId;
    public ExactDecimal Coefficient { get; }
    public SideValuation Value { get; }
    public ExactMoney Contribution { get; }
    public OpenEtrm.Shared.Numerics.ExactRatio QuantityRatio { get; }
    internal SideContribution(SideMembership membership, SideValuation value)
    { Coefficient = membership.Coefficient; QuantityRatio=membership.QuantityRatio??OpenEtrm.Shared.Numerics.ExactRatio.One; Value = value; Contribution = value.PresentValue.Scale(QuantityRatio).Scale(Coefficient); }
}

public sealed record InstrumentValuation
{
    public const string Policy = "signed_exact_side_pv_sum_v1";
    public Guid InstrumentId { get; }
    public ReferenceKey Currency { get; }
    public Quantity UnitQuantity { get; }
    public MarketSnapshot Snapshot { get; }
    internal SideValuationContext Context { get; }
    public string? ScenarioId => Context.ScenarioId;
    public string CurveNumericalPolicy => Context.CurveNumericalPolicy;
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
            v.SideId != instrument.Sides[n].Side.Id || v.Currency != instrument.Currency ||
            v.UnitQuantity != (instrument is CrossCommodityScheduledSwap ? ((FloatingSide)instrument.Sides[n].Side).UnitQuantity : instrument.UnitQuantity)).Any())
            throw new MarketDataException("instrument_contribution", "Every membership requires its own exact side valuation in the instrument currency and unit basis.");
        if (values.Any(v => !ReferenceEquals(context, v.Context)))
            throw new MarketDataException("instrument_context", "All side PVs must belong to this valuation operation, including its exact scenario and discount inputs.");
        Sides = values.Select((v, n) => new SideContribution(instrument.Sides[n], v)).ToImmutableArray();
        PresentValue = Sides.Select(s => s.Contribution).Aggregate((a, b) => a.Add(b));
    }
}
