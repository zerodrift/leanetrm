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
[EconomicDefinition("swap_projection_bindings_v1")]
public sealed record SwapProjectionBindings([property: IdentityField] ImmutableArray<FloatingProjectionBinding> Sides);
public sealed record SwapObservationCalendar(ReferenceKey Key, DateSet BusinessDays);

[EconomicDefinition("FixedSide_v4")]
public sealed partial record FixedSide : Side
{
    public const string Implementation = "fixed_total_payment_v1";
    [IdentityField] public FixedPriceTerm Price { get; }
    [IdentityField] public ReferenceKey? DateQuoteKey { get; }
    public ReferenceKey QuoteKey=>DateQuoteKey??throw new MarketDataException("side_schedule_terms","A native interval fixed price has no dated observation quote binding.");
    public Quantity UnitQuantity { get; }
    [IdentityField] public ReferenceKey QuantityBasis => UnitQuantity.Specification;
    [IdentityField] public ImmutableArray<FixedSidePeriod> Periods { get; } = [];
    public ReferenceKey Currency => Price.Unit.Currency;
    public DateOnly DeliveryFrom => IntervalDefinition is {} native?native.Delivery.From:Periods[0].DeliveryFrom;
    public DateOnly DeliveryTo => IntervalDefinition is {} native?native.Delivery.To:Periods[^1].DeliveryTo;
    public DateTimeOffset PaymentAt => SinglePeriod.PaymentAt;
    public PaymentRule? PaymentSchedule => SinglePeriod.PaymentSchedule;
    public ImmutableArray<OpenEtrm.Shared.Numerics.ExactRatio> Quantities => [..Periods.Select(p=>p.Allocation)];
    private FixedSidePeriod SinglePeriod => Periods.Length == 1 ? Periods[0] :
        throw new MarketDataException("side_payment_scope", "Select a payment period from this whole-term side.");
    public override ImmutableArray<CurveRequest> Dependencies => IntervalDefinition is null?[]:throw PpaErrors.Error("ppa_context","dependencies","Prepare the exact interval inputs before declaring dependencies.");

    public FixedSide(Guid id, FixedPriceTerm price, QuoteSpecification quote, Quantity unitQuantity,
        ImmutableArray<FixedSidePeriod> periods) : base(id)
    {
        if(price is null || quote is null || unitQuantity is null || unitQuantity.Amount != ExactDecimal.Parse("1") ||
            price.Unit != PriceUnit.FromQuote(quote) || price.Unit.Quantity != unitQuantity.Specification)
            throw new MarketDataException("swap_definition", "A fixed side requires explicit price terms, quote and one exact external quantity unit.");
        Money.RequireKey(quote.Key);
        if(quote.Key.Revision != price.Unit.Currency.Revision)
            throw new MarketDataException("swap_definition", "The exact quote and price references must share a revision.");
        if(periods.IsDefaultOrEmpty||periods.Any(p=>p is null))throw new MarketDataException("swap_side_periods","Supply explicit nonempty whole-term period parameters.");
        SwapSidePeriods.Validate(periods.Select(p=>(p.DeliveryFrom,p.DeliveryTo,p.PaymentAt,p.Allocation,p.PaymentSchedule)).ToImmutableArray(), price.Unit.Currency);
        foreach(var period in periods)RequireConstantPrice(price,period.DeliveryFrom,period.DeliveryTo);
        Price=price;DateQuoteKey=quote.Key;UnitQuantity=unitQuantity;Periods=periods;
    }
    private static void RequireConstantPrice(FixedPriceTerm price,DateOnly from,DateOnly to)
    {
        var first=price.At(from);var last=price.At(to.AddDays(-1));
        var interior=price switch
        {
            FlatPrice=>Enumerable.Empty<DateOnly>(),
            SteppedPrice stepped=>stepped.Steps.Select(s=>s.From).Where(date=>date>from&&date<to),
            EscalatedPrice=>Enumerable.Empty<DateOnly>(),
            _=>throw new MarketDataException("side_price_cadence","Select a supported explicit fixed price term.")
        };
        if(first!=last||interior.Any(date=>price.At(date)!=first))
            throw new MarketDataException("side_price_cadence","A dated row requires one constant contractual price; select finer delivery periods at every price change.");
    }
    public FixedSide(Guid id, UnitPrice price, Quantity unitQuantity, DateOnly deliveryFrom,
        DateOnly deliveryTo, DateTimeOffset paymentAt, string implementation, QuoteSpecification quote)
        : this(id, RequirePrice(price,implementation), quote, unitQuantity,
            [new(deliveryFrom,deliveryTo,paymentAt,OpenEtrm.Shared.Numerics.ExactRatio.One,null)]) { }
    public FixedSide(Guid id, UnitPrice price, Quantity unitQuantity, DateOnly deliveryFrom,
        DateOnly deliveryTo, PaymentRule paymentSchedule, string implementation, QuoteSpecification quote)
        : this(id, RequirePrice(price,implementation),quote,unitQuantity,
            [new(deliveryFrom,deliveryTo,PaymentBoundary(paymentSchedule,deliveryFrom,deliveryTo),OpenEtrm.Shared.Numerics.ExactRatio.One,paymentSchedule)]) { }
    private static FixedPriceTerm RequirePrice(UnitPrice? price,string implementation) =>
        RequirePrice(price is null?null:new FlatPrice(price),implementation);
    private static FixedPriceTerm RequirePrice(FixedPriceTerm? price,string implementation) =>
        price is not null && implementation==Implementation?price:
        throw new MarketDataException("swap_definition","Select explicit fixed price terms and their supported implementation.");
    internal static DateTimeOffset PaymentBoundary(PaymentRule? rule,DateOnly from,DateOnly to)=>
        PaymentBoundary((rule??throw new MarketDataException("payment_rule","An explicit payment rule is required.")).Generate(from,to).Date);
    internal static DateTimeOffset PaymentBoundary(DateOnly date)=>new(date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
    public override SideSchedule GenerateSchedule()=>IntervalDefinition is null?GenerateDatedSchedule():new IntervalSideSchedule(this);
    public SidePaymentSchedule GenerateDatedSchedule()
    {
        if(RetainedPlan is {} retained)return retained.DatedSchedule();
        var payments=Periods.Select((period,n)=>
        {
            var primitive=new ScheduleRow(Id,period.DeliveryFrom,period.DeliveryTo,period.PaymentAt,Currency,UnitQuantity,null,null,null,
                [new(ExactDecimal.Parse("1"),Price,period.DeliveryFrom)],Implementation,
                [new(period.DeliveryFrom,period.DeliveryTo,0,ExactDecimal.Parse("1"),Implementation)],Implementation,
                period.PaymentSchedule?.Generate(period.DeliveryFrom,period.DeliveryTo),period.PaymentSchedule);
            return new ScheduleRow(primitive,Id,n,new(period.Allocation.Numerator,period.Allocation.Denominator,UnitQuantity.Specification));
        }).ToImmutableArray();
        return new(Id,Currency,UnitQuantity,Implementation,payments);
    }
    public override SideValuation PV(SideValuationContext context) => GenerateSchedule().Evaluate(context);
}

public sealed record CustomCurveProjectionBinding(Guid CatalogId, Guid CurveId, int Version,
    string OutputNode, int? Position, string Selection, string Implementation)
{
    public const string SupportedSelection = "single_output_position_v1";
    public const string DeliverySelection = "exact_delivery_interval_v1";
    public const string SupportedImplementation = "published_custom_curve_projection_v1";
    internal void Validate()
    {
        if (CatalogId == Guid.Empty || CurveId == Guid.Empty || Version is < 1 or > 64 || Position < 0 ||
            string.IsNullOrEmpty(OutputNode) || OutputNode.Length > 32 || OutputNode[0] is < 'a' or > 'z' ||
            OutputNode.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) ||
            (Selection switch{SupportedSelection=>Position is null or <0,DeliverySelection=>Position is not null,_=>true}) || Implementation != SupportedImplementation)
            throw new MarketDataException("custom_projection", "Supply an exact published curve version, output coordinate and supported projection policies.");
    }
    // An in-memory input alias, never a database name lookup. The contractual
    // curve remains separate for index and actual-fixing validation.
    public CurveRequest Request(CurveRequest original)
    {
        Validate();
        if (original.Curve.Revision.CatalogId != CatalogId || original.Contract.Revision != original.Curve.Revision)
            throw new MarketDataException("custom_projection", "This projection contract requires the same exact market catalog and contract revision.");
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new object?[]
            { "custom_projection_request_v1", CatalogId, CurveId, Version, OutputNode, Position, Selection, Implementation, original.Curve.Code });
        return new(new(original.Curve.Revision, "custom_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))), original.Contract);
    }
}

[EconomicDefinition("FloatingSide_v8")]
public sealed partial record FloatingSide : Side
{
    public const string Implementation = "prefixing_index_projection_v1";
    public const string IndexedImplementation = "index_observation_fixings_v1";
    public const int MaximumDeliveryDays = 366;
    [IdentityField] public PriceSettlementSelection? DateSettlement { get; }
    public PriceSettlementSelection Settlement=>DateSettlement??throw new MarketDataException("side_schedule_terms","Select the dated settlement contract of this floating side.");
    public ReferenceKey Index=>Settlement.SelectedIndex;
    public override ImmutableArray<SettlementProjection> SettlementProjections
    {
        get
        {
            var selection=DateSettlement??IntervalPriceDefinition?.Settlement;
            if(selection is null)return [];
            var curves=IntervalDefinition is null?Periods.SelectMany(p=>p.Projections).Select(p=>p.Market.Curve).Distinct():
                [new CurveKey(Price.Curve.Revision,Price.Curve.Code)];
            return [..curves.Select(curve=>new SettlementProjection(Id,curve,selection))];
        }
    }
    [IdentityField] public QuoteSpecification? DateQuote { get; }
    public QuoteSpecification Quote=>DateQuote??throw new MarketDataException("side_schedule_terms","A native interval floating side has an explicit interval price unit.");
    public Quantity UnitQuantity { get; }
    [IdentityField] public ReferenceKey QuantityBasis => UnitQuantity.Specification;
    [IdentityField] public ImmutableArray<FloatingSidePeriod> Periods { get; } = [];
    private readonly ImmutableArray<FloatingObservationPlan> observations=[];
    public ReferenceKey Currency=>IntervalDefinition is {} native?native.Currency:Quote.Currency;
    public DateOnly DeliveryFrom=>IntervalDefinition is {} native?native.Delivery.From:Periods[0].DeliveryFrom;
    public DateOnly DeliveryTo=>IntervalDefinition is {} native?native.Delivery.To:Periods[^1].DeliveryTo;
    public DateTimeOffset FirstFixingAt=>Periods.Min(p=>p.FirstFixingAt);
    public DateTimeOffset PaymentAt=>SinglePeriod.PaymentAt;
    public PaymentRule? PaymentSchedule=>SinglePeriod.PaymentSchedule;
    public FloatingAveraging Averaging=>SinglePeriod.Averaging;
    public SwapObservationCalendar? Calendar=>SinglePeriod.Calendar;
    public UnitPrice? Spread=>SinglePeriod.Spread;
    public CustomCurveProjectionBinding? CustomProjection=>SinglePeriod.CustomProjection;
    public ImmutableArray<IndexFixing> Fixings=>[..Periods.SelectMany(p=>p.Fixings)];
    public ImmutableArray<SwapProjection> Projections=>[..Periods.SelectMany(p=>p.Projections)];
    public CurveKey ProjectionCurve=>Projections[0].Market.Curve;
    public ImmutableArray<FloatingObservation> ObservationSchedule=>[..Projections.Select(p=>new FloatingObservation(p.Contract.DeliveryFrom,p.Contract.DeliveryTo,p.AveragingQuantity))];
    public FloatingProjectionBinding PricingBinding=>new(Projections);
    public override DefinitionIdentity PricingBindingIdentity=>IntervalDefinition is null?DefinitionIdentity.For(new MonthlyProjectionBindings([..Periods.Select(p=>new FloatingProjectionBinding(p.Projections))])):DefinitionIdentity.For(Price);
    public ImmutableArray<OpenEtrm.Shared.Numerics.ExactRatio> Quantities=>[..Periods.Select(p=>p.Allocation)];
    private FloatingSidePeriod SinglePeriod=>Periods.Length==1?Periods[0]:
        throw new MarketDataException("side_payment_scope","Select an observation/payment period from this whole-term side.");
    public override ImmutableArray<CurveRequest> Dependencies=>GenerateSchedule().Dependencies;
    public override ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot)=>GenerateSchedule().DependenciesAt(snapshot);

    public FloatingSide(Guid id,PriceSettlementSelection settlement,QuoteSpecification quote,Quantity unitQuantity,ImmutableArray<FloatingSidePeriod> periods):base(id)
    {
        if(settlement is null)throw new MarketDataException("price_settlement_selection","A floating period side requires its explicit settlement selection.");
        var index=settlement.SelectedIndex;
        if(quote is null || unitQuantity is null || index.Revision!=quote.Key.Revision || quote.Kind!=QuoteKind.Outright ||
            unitQuantity.Amount!=ExactDecimal.Parse("1") || unitQuantity.Specification!=quote.Quantity || periods.IsDefaultOrEmpty || periods.Any(p=>p is null))
            throw new MarketDataException("swap_definition","A floating side requires exact index/quote/unit references and explicit whole-term periods.");
        _=PriceUnit.FromQuote(quote);
        SwapSidePeriods.Validate(periods.Select(p=>(p.DeliveryFrom,p.DeliveryTo,p.PaymentAt,p.Allocation,p.PaymentSchedule)).ToImmutableArray(),quote.Currency);
        var plans=ImmutableArray.CreateBuilder<FloatingObservationPlan>();
        foreach(var period in periods)
        {
            if(period.DeliveryTo.DayNumber-period.DeliveryFrom.DayNumber>MaximumDeliveryDays || !TradingInstant.IsSupported(period.FirstFixingAt) ||
                period.FirstFixingAt>=period.PaymentAt || period.Projections.IsDefaultOrEmpty || period.Projections.Length>MaximumDeliveryDays ||
                period.Projections.Any(p=>p is null || p.Contract is null) || period.Projections.Select(p=>p.Market).Distinct().Count()!=period.Projections.Length ||
                period.Projections.Any(p=>p.Contract.Kind!=ContractKind.Forward || p.Contract.FuturesSeries is not null || p.Market.Contract!=p.Contract.Key ||
                    p.Market.Curve.Revision!=p.Contract.Key.Revision || p.Contract.DeliveryFrom>=p.Contract.DeliveryTo) ||
                period.Projections.Select(p=>p.Market.Curve).Distinct().Count()!=1)
                throw new MarketDataException("swap_definition","Every period requires coherent fixing, payment and exact projection contracts.");
            if(period.Spread is not null && period.Spread.Unit!=PriceUnit.FromQuote(quote))throw new MarketDataException("swap_spread","A floating spread requires the side's exact price unit.");
            if(period.Averaging!=FloatingAveraging.QuantityWeighted && period.Projections.Any(p=>p.AveragingQuantity is not null))
                throw new MarketDataException("swap_averaging","Only quantity-weighted observations accept explicit averaging quantities.");
            var plan=FloatingObservationPlan.Generate(period.DeliveryFrom,period.DeliveryTo,period.Averaging,quote,period.Calendar,
                period.Averaging==FloatingAveraging.QuantityWeighted?[..period.Projections.Select(p=>new FloatingObservation(p.Contract.DeliveryFrom,p.Contract.DeliveryTo,p.AveragingQuantity))]:[]);
            if(!period.Projections.Select(p=>new FloatingObservation(p.Contract.DeliveryFrom,p.Contract.DeliveryTo,p.AveragingQuantity)).SequenceEqual(plan.Observations))
                throw new MarketDataException("swap_averaging","Resolved projections must match the complete ordered observation plan.");
            if(period.ObservationImplementation==IndexedImplementation)
            {
                if(period.Fixings.IsDefaultOrEmpty || period.Fixings.Length!=period.Projections.Length || period.Fixings.Any(f=>f is null) ||
                    period.Fixings.Select(f=>f.Series).Distinct().Count()!=period.Fixings.Length || period.Fixings.Where((f,n)=>f.Index!=index || f.Quote!=quote.Key ||
                        f.From!=period.Projections[n].Contract.DeliveryFrom || f.To!=period.Projections[n].Contract.DeliveryTo ||
                        !TradingInstant.IsSupported(f.FixingAt) || f.FixingAt>=period.PaymentAt || f.Series.Revision!=period.Projections[n].Market.Curve.Revision ||
                        f.Series.Id==Guid.Empty || f.Source.Revision!=index.Revision || string.IsNullOrWhiteSpace(f.Source.Code) || f.Implementation!=IndexFixing.SupportedImplementation).Any() ||
                    period.FirstFixingAt!=period.Fixings.Min(f=>f.FixingAt))
                    throw new MarketDataException("index_fixing_definition","Every exact observation requires its qualified index, quote, series, source and publication instant.");
            }
            else if(period.ObservationImplementation!=Implementation || !period.Fixings.IsEmpty)
                throw new MarketDataException("index_fixing_definition","Select an explicit projection-only or published-fixing observation contract.");
            if(settlement.Mode==PriceSettlementMode.Override && (period.Fixings.IsEmpty || period.CustomProjection is not null))
                throw new MarketDataException("price_settlement_selection","An override requires every qualified fixing rule and forward_curve_no_basis_v1 without a custom projection.");
            if(period.CustomProjection is {} binding)ValidateCustom(period,binding);
            plans.Add(plan);
        }
        DateSettlement=settlement;DateQuote=quote;UnitQuantity=unitQuantity;Periods=periods;observations=plans.ToImmutable();
    }
    public FloatingSide(Guid id,PriceSettlementSelection settlement,QuoteSpecification quote,Quantity unitQuantity,DateOnly from,DateOnly to,
        ImmutableArray<IndexFixing> fixings,DateTimeOffset paymentAt,FloatingAveraging averaging,ImmutableArray<SwapProjection> projections,
        string implementation,SwapObservationCalendar? calendar=null,UnitPrice? spread=null)
        :this(id,settlement,quote,unitQuantity,[new(from,to,FirstFixing(fixings),paymentAt,averaging,projections,fixings,calendar,spread,null,null,OpenEtrm.Shared.Numerics.ExactRatio.One,implementation)]) { }
    public FloatingSide(Guid id,PriceSettlementSelection settlement,QuoteSpecification quote,Quantity unitQuantity,DateOnly from,DateOnly to,
        ImmutableArray<IndexFixing> fixings,PaymentRule paymentSchedule,FloatingAveraging averaging,ImmutableArray<SwapProjection> projections,
        string implementation,SwapObservationCalendar? calendar=null,UnitPrice? spread=null)
        :this(id,settlement,quote,unitQuantity,[new(from,to,FirstFixing(fixings),FixedSide.PaymentBoundary(paymentSchedule,from,to),averaging,projections,fixings,calendar,spread,paymentSchedule,null,OpenEtrm.Shared.Numerics.ExactRatio.One,implementation)]) { }
    public FloatingSide(Guid id,PriceSettlementSelection settlement,QuoteSpecification quote,Quantity unitQuantity,DateOnly from,DateOnly to,
        DateTimeOffset firstFixingAt,DateTimeOffset paymentAt,FloatingAveraging averaging,ImmutableArray<SwapProjection> projections,
        string implementation,SwapObservationCalendar? calendar=null,UnitPrice? spread=null)
        :this(id,settlement,quote,unitQuantity,[new(from,to,firstFixingAt,paymentAt,averaging,projections,[],calendar,spread,null,null,OpenEtrm.Shared.Numerics.ExactRatio.One,implementation)]) { }
    public FloatingSide(Guid id,PriceSettlementSelection settlement,QuoteSpecification quote,Quantity unitQuantity,DateOnly from,DateOnly to,
        DateTimeOffset firstFixingAt,PaymentRule paymentSchedule,FloatingAveraging averaging,ImmutableArray<SwapProjection> projections,
        string implementation,SwapObservationCalendar? calendar=null,UnitPrice? spread=null)
        :this(id,settlement,quote,unitQuantity,[new(from,to,firstFixingAt,FixedSide.PaymentBoundary(paymentSchedule,from,to),averaging,projections,[],calendar,spread,paymentSchedule,null,OpenEtrm.Shared.Numerics.ExactRatio.One,implementation)]) { }
    private static DateTimeOffset FirstFixing(ImmutableArray<IndexFixing> fixings)=>!fixings.IsDefaultOrEmpty && fixings.All(f=>f is not null)?fixings.Min(f=>f.FixingAt):
        throw new MarketDataException("index_fixing_definition","Supply the complete explicit dated fixing schedule.");
    private static void ValidateCustom(FloatingSidePeriod period,CustomCurveProjectionBinding binding)
    {
        if(period.Fixings.IsDefaultOrEmpty || (period.Averaging switch
            {FloatingAveraging.MonthlyIndexPrice=>period.Projections.Length!=1 || binding.Selection!=CustomCurveProjectionBinding.SupportedSelection,
             FloatingAveraging.CalendarDayArithmetic=>binding.Selection!=CustomCurveProjectionBinding.DeliverySelection,_=>true}))
            throw new MarketDataException("custom_projection","Select one exact output for a monthly reset or exact delivery-interval mapping for daily indexed resets.");
        foreach(var projection in period.Projections)_=binding.Request(projection.Market);
    }
    public FloatingSide WithCustomProjection(CustomCurveProjectionBinding binding)
    {
        if(binding is null)throw new MarketDataException("custom_projection","Supply the exact published projection binding.");
        foreach(var period in Periods)ValidateCustom(period,binding);
        return new(Id,Settlement,Quote,UnitQuantity,[..Periods.Select(p=>p with{CustomProjection=binding})]);
    }
    public override SideSchedule GenerateSchedule()=>IntervalDefinition is null?GenerateDatedSchedule():new IntervalSideSchedule(this);
    public SidePaymentSchedule GenerateDatedSchedule()
    {
        if(RetainedPlan is {} retained)return retained.DatedSchedule();
        var payments=Periods.Select((period,n)=>
        {
            var plan=observations[n];
            var primitive=new ScheduleRow(Id,period.DeliveryFrom,period.DeliveryTo,period.PaymentAt,Currency,UnitQuantity,
                period.Fixings.IsEmpty?period.FirstFixingAt:null,period.Averaging,period.Calendar,
                [..period.Projections.Select((p,i)=>new SchedulePriceTerm(plan.Weights[i],null,
                    new(Settlement,Quote,p.Market,p.Contract,p.AveragingQuantity,period.Fixings.IsEmpty?null:period.Fixings[i],period.CustomProjection),period.Spread))],
                period.ObservationImplementation,plan.Applications,plan.MappingRule,period.PaymentSchedule?.Generate(period.DeliveryFrom,period.DeliveryTo),period.PaymentSchedule);
            return new ScheduleRow(primitive,Id,n,new(period.Allocation.Numerator,period.Allocation.Denominator,UnitQuantity.Specification));
        }).ToImmutableArray();
        return new(Id,Currency,UnitQuantity,Implementation,payments);
    }
    public override SideValuation PV(SideValuationContext context)=>GenerateSchedule().Evaluate(context);
}

[EconomicDefinition("FinancialSwapInstrument_v2")]
public sealed record FinancialSwapInstrument : SidedInstrument
{
    public const string Implementation = "independent_side_financial_swap_v2";
    public const string Discounted = "common_payment_factor_v1";
    public const string Undiscounted = "explicitly_undiscounted_v1";
    [IdentityField]
    public bool Discounting { get; }
    public override DefinitionIdentity PricingBindingIdentity => First is FixedSide?Floating.PricingBindingIdentity:DefinitionIdentity.For(new SwapProjectionBindings(Sides.Select(s=>s.Side).OfType<FloatingSide>().Select(s=>s.PricingBinding).ToImmutableArray()));
    public Side First => Sides[0].Side;
    public FixedSide Fixed => First as FixedSide??throw new MarketDataException("swap_par_rate","A fixed rate requires a fixed/floating swap.");
    public FloatingSide Floating => (FloatingSide)Sides[1].Side;
    public override ReferenceKey Currency => Floating.Quote.Currency;
    public override Quantity UnitQuantity => Floating.UnitQuantity;

    public FinancialSwapInstrument(Guid id, ImmutableArray<SideMembership> sides, string implementation, bool discounting) : base(id, sides)
    {
        Discounting = discounting;
        var one = ExactDecimal.Parse("1"); var minusOne = ExactDecimal.Parse("-1");
        if (implementation != Implementation || sides.Length != 2 || sides[0].Side is not (FixedSide or FloatingSide) ||
            sides[1].Side is not FloatingSide b ||
            !(sides[0].Coefficient == one && sides[1].Coefficient == minusOne ||
                sides[0].Coefficient == minusOne && sides[1].Coefficient == one))
            throw new MarketDataException("swap_instrument", "A financial swap requires two independent compatible sides, at least one floating, with opposite explicit unit coefficients.");
        var (currency,quantity)=sides[0].Side switch
        {
            FixedSide fixedSide=>(fixedSide.Price.Unit.Currency,fixedSide.UnitQuantity),
            FloatingSide floatingSide=>(floatingSide.Quote.Currency,floatingSide.UnitQuantity),
            _=>throw new MarketDataException("swap_instrument","The selected first side is unsupported.")
        };
        if(currency!=b.Quote.Currency||quantity!=b.UnitQuantity)
            throw new MarketDataException("swap_instrument","Both sides must be normalized to the same exact currency and quantity basis.");
    }

    public override InstrumentValuation PV(SideValuationContext context)
    {
        return RequireDiscounting(base.PV(context), Discounting);
    }
    internal static InstrumentValuation RequireDiscounting(InstrumentValuation value, bool discounting)
    {
        if (value.Sides.SelectMany(s => ((PaymentSideValuation)s.Value).Payments).Any(p =>
            discounting == (p.PaymentDiscount.Source == DiscountFactorSource.Undiscounted)))
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
        if (valuationPolicy == Discounted && Sides.SelectMany(m=>m.Side.GenerateSchedule().Rows).Select(row=>row.PaymentAt).Distinct().Skip(1).Any())
            throw new MarketDataException("swap_valuation", "Independent payment dates require per-side payment discount factors.");
        var context = new SideValuationContext(market, new ExplicitDiscounting(paymentFactor,
            valuationPolicy == Undiscounted ? DiscountFactorSource.Undiscounted : DiscountFactorSource.Explicit));
        return PV(context).PresentValue.Scale(quantity.Amount).ToMoney();
    }
}
