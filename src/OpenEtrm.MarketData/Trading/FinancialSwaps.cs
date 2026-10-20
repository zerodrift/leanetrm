using System.Collections.Immutable;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.Trading;

public enum FloatingAveraging
{
    MonthlyIndexPrice = 1, CalendarDayArithmetic, ObservationArithmetic,
    CalendarDaysPrevious, CalendarDaysNext, QuantityWeighted
}
public sealed record SwapProjection(CurveRequest Market, MarketContract Contract, Quantity? AveragingQuantity = null);
public sealed record FloatingObservation(DateOnly From, DateOnly To, Quantity? AveragingQuantity);
[EconomicDefinition("floating_projection_binding_v1")]
public sealed record FloatingProjectionBinding([property: IdentityField] ImmutableArray<SwapProjection> Projections);
public sealed record SwapObservationCalendar(ReferenceKey Key, DateSet BusinessDays);

[EconomicDefinition("FixedSide_v3")]
public sealed record FixedSide : Side
{
    public const string Implementation = "fixed_total_payment_v1";
    [IdentityField]
    public UnitPrice Price { get; }
    [IdentityField]
    public ReferenceKey QuoteKey { get; }
    public Quantity UnitQuantity { get; }
    [IdentityField]
    public ReferenceKey QuantityBasis => UnitQuantity.Specification;
    [IdentityField]
    public DateOnly DeliveryFrom { get; }
    [IdentityField]
    public DateOnly DeliveryTo { get; }
    [IdentityField]
    public DateTimeOffset PaymentAt { get; }
    [IdentityField]
    public PaymentRule? PaymentSchedule { get; }
    public override ImmutableArray<CurveRequest> Dependencies => [];

    public FixedSide(Guid id, UnitPrice price, Quantity unitQuantity, DateOnly deliveryFrom,
        DateOnly deliveryTo, DateTimeOffset paymentAt, string implementation, QuoteSpecification quote)
        : this(id,price,unitQuantity,deliveryFrom,deliveryTo,paymentAt,implementation,false,quote) { }
    public FixedSide(Guid id,UnitPrice price,Quantity unitQuantity,DateOnly deliveryFrom,DateOnly deliveryTo,
        PaymentRule paymentSchedule,string implementation,QuoteSpecification quote)
        : this(id,price,unitQuantity,deliveryFrom,deliveryTo,PaymentBoundary(paymentSchedule,deliveryFrom,deliveryTo),implementation,true,quote)
    {
        if(paymentSchedule.Calendar is {} calendar&&calendar.Key.Revision!=price.Unit.Currency.Revision)
            throw new MarketDataException("payment_calendar","The payment calendar must use the side's exact reference revision.");
        PaymentSchedule=paymentSchedule;
    }
    internal static DateTimeOffset PaymentBoundary(PaymentRule? rule,DateOnly from,DateOnly to)=>
        PaymentBoundary((rule??throw new MarketDataException("payment_rule","An explicit payment rule is required.")).Generate(from,to).Date);
    internal static DateTimeOffset PaymentBoundary(DateOnly date)=>new(date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
    private FixedSide(Guid id,UnitPrice price,Quantity unitQuantity,DateOnly deliveryFrom,DateOnly deliveryTo,
        DateTimeOffset paymentAt,string implementation,bool scheduled,QuoteSpecification quote) : base(id)
    {
        if (price is null || unitQuantity is null || implementation != Implementation ||
            unitQuantity.Amount != ExactDecimal.Parse("1") || price.Unit.Quantity != unitQuantity.Specification ||
            deliveryFrom >= deliveryTo || !TradingInstant.IsSupported(paymentAt) ||
            !scheduled && DateOnly.FromDateTime(paymentAt.UtcDateTime) < deliveryTo)
            throw new MarketDataException("swap_definition", "A fixed side requires explicit price, one exact quantity unit, delivery interval and payment after delivery.");
        if(quote is null || price.Unit != PriceUnit.FromQuote(quote)) throw new MarketDataException("swap_definition","The selected exact quote must match the fixed price unit.");
        Money.RequireKey(quote.Key);
        if(quote.Key.Revision != price.Unit.Currency.Revision) throw new MarketDataException("swap_definition","The exact quote and price references must share a revision.");
        QuoteKey = quote.Key;
        Price = price; UnitQuantity = unitQuantity; DeliveryFrom = deliveryFrom;
        DeliveryTo = deliveryTo; PaymentAt = paymentAt;
    }
    public override SidePaymentSchedule GenerateSchedule() => new(new(Id, DeliveryFrom, DeliveryTo, PaymentAt,
        Price.Unit.Currency, UnitQuantity, null, null, null,
        [new(ExactDecimal.Parse("1"), Price, null)], Implementation,PaymentSchedule?.Generate(DeliveryFrom,DeliveryTo),PaymentSchedule));
    public override SideValuation PV(SideValuationContext context) => GenerateSchedule().Evaluate(context);
}

[EconomicDefinition("FloatingSide_v3")]
public sealed record FloatingSide : Side
{
    public const string Implementation = "prefixing_index_projection_v1";
    public const string IndexedImplementation = "index_observation_fixings_v1";
    public const int MaximumDeliveryDays = 366;
    [IdentityField]
    public ReferenceKey Index { get; }
    [IdentityField]
    public QuoteSpecification Quote { get; }
    public Quantity UnitQuantity { get; }
    [IdentityField]
    public ReferenceKey QuantityBasis => UnitQuantity.Specification;
    [IdentityField]
    public DateOnly DeliveryFrom { get; }
    [IdentityField]
    public DateOnly DeliveryTo { get; }
    [IdentityField]
    public DateTimeOffset FirstFixingAt { get; }
    public ImmutableArray<IndexFixing> Fixings { get; } = [];
    [IdentityField]
    public DateTimeOffset PaymentAt { get; }
    [IdentityField]
    public PaymentRule? PaymentSchedule { get; }
    [IdentityField]
    public FloatingAveraging Averaging { get; }
    [IdentityField]
    public CurveKey ProjectionCurve => Projections[0].Market.Curve;
    [IdentityField]
    public ImmutableArray<FloatingObservation> ObservationSchedule => Projections.Select(p =>
        new FloatingObservation(p.Contract.DeliveryFrom,p.Contract.DeliveryTo,p.AveragingQuantity)).ToImmutableArray();
    public FloatingProjectionBinding PricingBinding { get; }
    public ImmutableArray<SwapProjection> Projections => PricingBinding.Projections;
    [IdentityField]
    public SwapObservationCalendar? Calendar { get; }
    private readonly ImmutableArray<ExactDecimal> weights;
    public override ImmutableArray<CurveRequest> Dependencies => Projections.Select(p => p.Market).ToImmutableArray();
    public override ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot) =>
        GenerateSchedule().DependenciesAt(snapshot);

    public FloatingSide(Guid id, ReferenceKey index, QuoteSpecification quote, Quantity unitQuantity,
        DateOnly deliveryFrom, DateOnly deliveryTo, ImmutableArray<IndexFixing> fixings,
        DateTimeOffset paymentAt, FloatingAveraging averaging, ImmutableArray<SwapProjection> projections,
        string implementation, SwapObservationCalendar? calendar = null)
        : this(id, index, quote, unitQuantity, deliveryFrom, deliveryTo, fixings, paymentAt,
            averaging, projections, implementation, calendar, false) { }

    private FloatingSide(Guid id, ReferenceKey index, QuoteSpecification quote, Quantity unitQuantity,
        DateOnly deliveryFrom, DateOnly deliveryTo, ImmutableArray<IndexFixing> fixings,
        DateTimeOffset paymentAt, FloatingAveraging averaging, ImmutableArray<SwapProjection> projections,
        string implementation, SwapObservationCalendar? calendar, bool scheduled)
        : this(id, index, quote, unitQuantity, deliveryFrom, deliveryTo,
            FirstFixing(fixings), paymentAt, averaging, projections, Implementation, calendar, scheduled)
    {
        if (implementation != IndexedImplementation)
            throw new MarketDataException("index_fixing_definition", "Select the explicit indexed fixing implementation.");
        if (fixings.Length != projections.Length || fixings.Select(f => f.Series).Distinct().Count() != fixings.Length ||
            fixings.Where((f, n) => f.Index != index || f.Quote != quote.Key || f.From != projections[n].Contract.DeliveryFrom ||
                f.To != projections[n].Contract.DeliveryTo || !TradingInstant.IsSupported(f.FixingAt) || f.FixingAt >= paymentAt ||
                f.Series.Revision != projections[n].Market.Curve.Revision || f.Series.Id == Guid.Empty ||
                f.Source.Revision != index.Revision || string.IsNullOrWhiteSpace(f.Source.Code) ||
                f.Implementation != IndexFixing.SupportedImplementation).Any())
            throw new MarketDataException("index_fixing_definition", "Every exact observation period requires its qualified index, quote, series, source and UTC publication instant.");
        Fixings = fixings;
    }

    public FloatingSide(Guid id, ReferenceKey index, QuoteSpecification quote, Quantity unitQuantity,
        DateOnly deliveryFrom, DateOnly deliveryTo, ImmutableArray<IndexFixing> fixings,
        PaymentRule paymentSchedule, FloatingAveraging averaging, ImmutableArray<SwapProjection> projections,
        string implementation, SwapObservationCalendar? calendar = null)
        : this(id, index, quote, unitQuantity, deliveryFrom, deliveryTo, fixings,
            FixedSide.PaymentBoundary(paymentSchedule, deliveryFrom, deliveryTo), averaging, projections, implementation, calendar, true)
    {
        if (paymentSchedule.Calendar is { } dates && dates.Key.Revision != quote.Currency.Revision)
            throw new MarketDataException("payment_calendar", "The payment calendar must use the side's exact reference revision.");
        PaymentSchedule = paymentSchedule;
    }

    private static DateTimeOffset FirstFixing(ImmutableArray<IndexFixing> fixings) =>
        !fixings.IsDefaultOrEmpty && fixings.All(f => f is not null)
            ? fixings.Min(f => f.FixingAt)
            : throw new MarketDataException("index_fixing_definition", "Supply the complete explicit dated index fixing schedule.");

    public FloatingSide(Guid id, ReferenceKey index, QuoteSpecification quote, Quantity unitQuantity,
        DateOnly deliveryFrom, DateOnly deliveryTo, DateTimeOffset firstFixingAt, DateTimeOffset paymentAt,
        FloatingAveraging averaging, ImmutableArray<SwapProjection> projections, string implementation,
        SwapObservationCalendar? calendar = null)
        : this(id,index,quote,unitQuantity,deliveryFrom,deliveryTo,firstFixingAt,paymentAt,averaging,projections,implementation,calendar,false) { }
    public FloatingSide(Guid id,ReferenceKey index,QuoteSpecification quote,Quantity unitQuantity,
        DateOnly deliveryFrom,DateOnly deliveryTo,DateTimeOffset firstFixingAt,PaymentRule paymentSchedule,
        FloatingAveraging averaging,ImmutableArray<SwapProjection> projections,string implementation,SwapObservationCalendar? calendar=null)
        : this(id,index,quote,unitQuantity,deliveryFrom,deliveryTo,firstFixingAt,FixedSide.PaymentBoundary(paymentSchedule,deliveryFrom,deliveryTo),averaging,projections,implementation,calendar,true)
    {
        if(paymentSchedule.Calendar is {} dates&&dates.Key.Revision!=quote.Currency.Revision)
            throw new MarketDataException("payment_calendar","The payment calendar must use the side's exact reference revision.");
        PaymentSchedule=paymentSchedule;
    }
    private FloatingSide(Guid id,ReferenceKey index,QuoteSpecification quote,Quantity unitQuantity,
        DateOnly deliveryFrom,DateOnly deliveryTo,DateTimeOffset firstFixingAt,DateTimeOffset paymentAt,
        FloatingAveraging averaging,ImmutableArray<SwapProjection> projections,string implementation,SwapObservationCalendar? calendar,bool scheduled) : base(id)
    {
        Money.RequireKey(index);
        if (quote is null || unitQuantity is null || implementation != Implementation ||
            index.Revision != quote.Key.Revision || quote.Kind != QuoteKind.Outright ||
            unitQuantity.Amount != ExactDecimal.Parse("1") || unitQuantity.Specification != quote.Quantity ||
            deliveryFrom >= deliveryTo || deliveryTo.DayNumber - deliveryFrom.DayNumber > MaximumDeliveryDays ||
            !TradingInstant.IsSupported(firstFixingAt) || !TradingInstant.IsSupported(paymentAt) ||
            firstFixingAt >= paymentAt || !scheduled && DateOnly.FromDateTime(paymentAt.UtcDateTime) < deliveryTo ||
            projections.IsDefaultOrEmpty || projections.Length > MaximumDeliveryDays || projections.Any(p => p is null || p.Contract is null) ||
            projections.Select(p => p.Market).Distinct().Count() != projections.Length ||
            projections.Any(p => p.Contract.Kind != ContractKind.Forward || p.Contract.FuturesSeries is not null ||
                p.Market.Contract != p.Contract.Key || p.Market.Curve.Revision != p.Contract.Key.Revision ||
                p.Contract.DeliveryFrom >= p.Contract.DeliveryTo) ||
            projections.Select(p => p.Market.Curve).Distinct().Count() != 1)
            throw new MarketDataException("swap_definition", "A floating side requires explicit coherent index, quote, unit, delivery, fixing, payment and exact projection contracts.");
        _ = PriceUnit.FromQuote(quote);
        weights = ResolveWeights(averaging, projections, calendar, quote, deliveryFrom, deliveryTo);
        Index = index; Quote = quote; UnitQuantity = unitQuantity; DeliveryFrom = deliveryFrom;
        DeliveryTo = deliveryTo; FirstFixingAt = firstFixingAt; PaymentAt = paymentAt;
        Averaging = averaging; PricingBinding = new(projections); Calendar = calendar;
    }

    private static ImmutableArray<ExactDecimal> ResolveWeights(FloatingAveraging averaging,
        ImmutableArray<SwapProjection> projections, SwapObservationCalendar? calendar,
        QuoteSpecification quote, DateOnly deliveryFrom, DateOnly deliveryTo)
    {
        static MarketDataException Invalid() => new("swap_averaging", "Select complete ordered observations, an explicit covered calendar where required, and compatible weights for the chosen averaging rule.");
        var one = ExactDecimal.Parse("1");
        var calendarRequired = averaging is FloatingAveraging.ObservationArithmetic or
            FloatingAveraging.CalendarDaysPrevious or FloatingAveraging.CalendarDaysNext;
        if (!Enum.IsDefined(averaging) || calendarRequired != (calendar is not null) ||
            averaging != FloatingAveraging.QuantityWeighted && projections.Any(p => p.AveragingQuantity is not null))
            throw Invalid();
        if (averaging == FloatingAveraging.MonthlyIndexPrice)
        {
            if (deliveryFrom.Day != 1 || deliveryFrom.Year == 9999 || deliveryFrom.AddMonths(1) != deliveryTo || projections.Length != 1 ||
                projections[0].Contract.DeliveryFrom != deliveryFrom || projections[0].Contract.DeliveryTo != deliveryTo)
                throw Invalid();
            return [one];
        }
        if (projections.Any(p => p.Contract.DeliveryFrom.Year == 9999 || p.Contract.DeliveryFrom.AddDays(1) != p.Contract.DeliveryTo) ||
            !projections.Select(p => p.Contract.DeliveryFrom).SequenceEqual(projections.Select(p => p.Contract.DeliveryFrom).Distinct().Order()))
            throw Invalid();
        var days = Enumerable.Range(deliveryFrom.DayNumber, deliveryTo.DayNumber - deliveryFrom.DayNumber).Select(DateOnly.FromDayNumber).ToArray();
        if (!calendarRequired)
        {
            if (!projections.Select(p => p.Contract.DeliveryFrom).SequenceEqual(days)) throw Invalid();
            if (averaging == FloatingAveraging.CalendarDayArithmetic) return projections.Select(_ => one).ToImmutableArray();
            if (averaging != FloatingAveraging.QuantityWeighted || projections.Any(p => p.AveragingQuantity is null ||
                p.AveragingQuantity.Specification != quote.Quantity || p.AveragingQuantity.Amount.CompareTo(default) <= 0)) throw Invalid();
            return projections.Select(p => p.AveragingQuantity!.Amount).ToImmutableArray();
        }
        if (calendar!.BusinessDays is null || calendar.Key.Revision != quote.Key.Revision || string.IsNullOrWhiteSpace(calendar.Key.Code) ||
            calendar.BusinessDays.From > deliveryFrom || calendar.BusinessDays.To < deliveryTo) throw Invalid();
        var observed = calendar.BusinessDays;
        int LowerBound(DateOnly date)
        {
            var low = 0; var high = observed.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (observed[middle] < date) low = middle + 1;
                else high = middle;
            }
            return low;
        }
        var counts = new Dictionary<DateOnly, int>();
        if (averaging == FloatingAveraging.ObservationArithmetic)
        {
            for (var n = LowerBound(deliveryFrom); n < observed.Count && observed[n] < deliveryTo; n++)
                counts.Add(observed[n], 1);
        }
        else foreach (var day in days)
        {
            var n = LowerBound(day);
            if (averaging == FloatingAveraging.CalendarDaysPrevious && (n == observed.Count || observed[n] != day)) n--;
            if (n < 0 || n >= observed.Count) throw Invalid();
            var date = observed[n];
            counts[date] = counts.GetValueOrDefault(date) + 1;
        }
        if (counts.Count == 0 || !projections.Select(p => p.Contract.DeliveryFrom).SequenceEqual(counts.Keys.Order())) throw Invalid();
        return projections.Select(p => ExactDecimal.Parse(counts[p.Contract.DeliveryFrom].ToString(System.Globalization.CultureInfo.InvariantCulture))).ToImmutableArray();
    }

    public override SidePaymentSchedule GenerateSchedule() => new(new(Id, DeliveryFrom, DeliveryTo, PaymentAt,
        Quote.Currency, UnitQuantity, Fixings.IsEmpty ? FirstFixingAt : null, Averaging, Calendar,
        Projections.Select((p, n) => new SchedulePriceTerm(weights[n], null,
            new(Index, Quote, p.Market, p.Contract, p.AveragingQuantity, Fixings.IsEmpty ? null : Fixings[n]))).ToImmutableArray(),
        Fixings.IsEmpty ? Implementation : IndexedImplementation,PaymentSchedule?.Generate(DeliveryFrom,DeliveryTo),PaymentSchedule));
    public override SideValuation PV(SideValuationContext context) => GenerateSchedule().Evaluate(context);

}

[EconomicDefinition("FinancialSwapInstrument_v2")]
public sealed record FinancialSwapInstrument : SidedInstrument
{
    public const string Implementation = "independent_side_financial_swap_v2";
    public const string Discounted = "common_payment_factor_v1";
    public const string Undiscounted = "explicitly_undiscounted_v1";
    [IdentityField]
    public bool Discounting { get; }
    public DefinitionIdentity PricingBindingIdentity => DefinitionIdentity.For(Floating.PricingBinding);
    public FixedSide Fixed => (FixedSide)Sides[0].Side;
    public FloatingSide Floating => (FloatingSide)Sides[1].Side;
    public override ReferenceKey Currency => Fixed.Price.Unit.Currency;
    public override Quantity UnitQuantity => Fixed.UnitQuantity;

    public FinancialSwapInstrument(Guid id, ImmutableArray<SideMembership> sides, string implementation, bool discounting) : base(id, sides)
    {
        Discounting = discounting;
        var one = ExactDecimal.Parse("1"); var minusOne = ExactDecimal.Parse("-1");
        if (implementation != Implementation || sides.Length != 2 || sides[0].Side is not FixedSide a ||
            sides[1].Side is not FloatingSide b || a.Id == b.Id ||
            !(sides[0].Coefficient == one && sides[1].Coefficient == minusOne ||
                sides[0].Coefficient == minusOne && sides[1].Coefficient == one) ||
            a.Price.Unit.Currency != b.Quote.Currency || a.UnitQuantity != b.UnitQuantity || a.Price.Unit.Quantity != b.Quote.Quantity)
            throw new MarketDataException("swap_instrument", "A financial swap requires fixed and floating sides normalized to the same currency and quantity basis, with opposite explicit unit coefficients.");
    }

    public override InstrumentValuation PV(SideValuationContext context)
    {
        var value = base.PV(context);
        if (value.Sides.SelectMany(s => s.Value.Payments).Any(p =>
            Discounting == (p.Discount.Source == DiscountFactorSource.Undiscounted)))
            throw new MarketDataException("swap_valuation", "Payment discounting must match the immutable instrument definition.");
        return value;
    }

    // The caller resolves the authorized currency/curve mapping and retains its
    // evidence. This pure arithmetic entry point never selects a discount curve.
    public Money Price(CurveBuild market, Quantity quantity, ExactDecimal paymentFactor, string valuationPolicy)
    {
        if (quantity is null || quantity.Specification != UnitQuantity.Specification || quantity.Amount.CompareTo(default) <= 0)
            throw new MarketDataException("swap_quantity", "Supply a positive total quantity in the exact instrument unit; membership directions carry the signs.");
        if (paymentFactor.CompareTo(default) <= 0 ||
            valuationPolicy != Discounted && valuationPolicy != Undiscounted ||
            Discounting != (valuationPolicy == Discounted) ||
            valuationPolicy == Undiscounted && paymentFactor != ExactDecimal.Parse("1"))
            throw new MarketDataException("swap_valuation", "Choose explicit discounted or undiscounted valuation with a positive payment factor; undiscounted requires one.");
        if (valuationPolicy == Discounted && Fixed.PaymentAt != Floating.PaymentAt)
            throw new MarketDataException("swap_valuation", "Independent payment dates require per-side payment discount factors.");
        var context = new SideValuationContext(market, new ExplicitDiscounting(paymentFactor,
            valuationPolicy == Undiscounted ? DiscountFactorSource.Undiscounted : DiscountFactorSource.Explicit));
        return PV(context).PresentValue.Scale(quantity.Amount).ToMoney();
    }
}
