using System.Collections.Immutable;
using OpenEtrm.Shared.Temporal;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public static class TradingInstant
{
    public static DateTimeOffset Parse(string? value,string errorCode)
    {
        if(value is null||!(value.EndsWith('Z')||value.EndsWith("+00:00",StringComparison.Ordinal))||
            !UtcInstant.TryParse(value,out var instant)||!IsSupported(instant))
            throw new MarketDataException(errorCode,"Supply an explicit UTC instant with exact microsecond precision within the supported trading horizon.");
        return instant;
    }
    public static bool IsSupported(DateTimeOffset at) => at.Offset == TimeSpan.Zero && at.Ticks % 10 == 0 &&
        at < new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);
}

public abstract record Side
{
    public Guid Id { get; }
    private readonly SideCalendarPolicy? calendarPolicy;
    [IdentityField] public SideCalendarPolicy CalendarPolicy => calendarPolicy ??
        throw new MarketDataException("retained_side_calendar", "This Side executes the calendar policies sealed in its retained program; it cannot author a replacement calendar.");
    [System.Text.Json.Serialization.JsonIgnore]
    public ScheduleDefinition? RetainedSchedule { get; internal init; }
    internal RetainedSideSchedule? RetainedPlan { get; init; }
    public virtual PpaSideTerms Terms=>throw new MarketDataException("side_schedule_terms","This side uses explicit dated period parameters rather than native interval terms.");
    public virtual DefinitionIdentity? PricingBindingIdentity => null;
    public virtual ImmutableArray<SettlementProjection> SettlementProjections=>[];
    public abstract ImmutableArray<CurveRequest> Dependencies { get; }
    public virtual ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot) => Dependencies;
    public abstract SideSchedule GenerateSchedule();
    public abstract SideValuation PV(SideValuationContext context);

    protected Side(Guid id,SideCalendarPolicy calendarPolicy)
    {
        if (id == Guid.Empty)
            throw new MarketDataException("side_definition", "A side requires an exact identity.");
        Id = id;
        this.calendarPolicy=calendarPolicy??throw new MarketDataException("side_calendar_policy","Supply the Side's explicit versioned calendar bindings.");
    }
    // A retained program already owns its exact calendar rules and resolved grid.
    protected Side(Guid id)
    {
        if(id==Guid.Empty)throw new MarketDataException("side_definition","A side requires an exact identity.");
        Id=id;
    }
}

public sealed record SideMembership(Side Side, ExactDecimal Coefficient, OpenEtrm.Shared.Numerics.ExactRatio? QuantityRatio = null);
public abstract record Instrument
{
    public Guid Id { get; }
    // Derived once per instance. A `with` copy is a new instance and derives its
    // own, so a retained identity never describes different terms.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Instrument, DefinitionIdentity> identities = new();
    public DefinitionIdentity EconomicIdentity => identities.GetValue(this, static instrument => DefinitionIdentity.For(instrument));
    public virtual DefinitionIdentity? PricingBindingIdentity => null;
    public ImmutableArray<SettlementProjection> SettlementProjections=>this is SidedInstrument sided?
        [..sided.Sides.SelectMany(m=>m.Side.SettlementProjections).Distinct()]:[];
    public virtual string? RetainedScheduleIdentity=>this is SidedInstrument sided?
        "retained_side_schedules_v1/"+string.Join('/',sided.Sides.Select(m=>m.Side.RetainedSchedule?.Manifest??"transient")):null;
    public abstract ReferenceKey Currency { get; }
    public abstract Quantity UnitQuantity { get; }
    public virtual bool SupportsQuantityScaling=>false;
    public virtual bool SupportsWholePosition=>false;
    public virtual bool SupportsFixedUnitBinding=>false;
    internal virtual bool RequiresNativeProgramMarket=>false;
    public virtual InstrumentValuation PV(SideValuationContext context)=>
        throw new MarketDataException("valuation_measure","This instrument requires its declared unit valuation context.");
    public abstract UnitPricingContract Declare(UnitValuationContext context);
    // Model resolution can depend on an admitted calculation clock. It may
    // refine numerical metadata only; the original financial input fence stays.
    public virtual ImmutableArray<DeclaredPriceStep> ResolvePriceSteps(UnitValuationContext context,UnitPricingContract declaration)=>declaration.PriceSteps;
    // Common black-box entry point. The denominator remains explicit until the
    // trade's typed quantity is applied. Value preserves existing pricer adapters.
    public virtual UnitValuation PV(UnitValuationContext context)
    {
        ValidateValuationState(context.BindingState);
        return new(context,Value(context),UnitQuantity.Specification);
    }
    public virtual void ValidateValuationState(IBindingValuationState? state)
    {
        if(state is not null)
            throw new MarketDataException("settlement_scope","This instrument does not support the supplied binding valuation state.");
    }
    public virtual void ValidateBinding(Quantity quantity,DeliveryCoverage? coverage)
    {
        RequireQuantity(quantity,coverage);
        if(!SupportsQuantityScaling&&!SupportsWholePosition&&quantity!=UnitQuantity)
            throw new MarketDataException("risk_quantity_scaling","This instrument has not declared linear scaling in booked quantity.");
        if(coverage is not null)
            throw new MarketDataException("ppa_segment_coverage","This instrument does not accept delivery coverage.");
    }
    internal void RequireHolding(Quantity quantity,DeliveryCoverage? coverage,TradeExecution? execution=null)
    {
        RequireQuantity(quantity,coverage);
        if(SupportsWholePosition&&execution is not null)
            throw new MarketDataException("risk_whole_position","A whole position has no execution basis or quantity scaling.");
        ValidateBinding(quantity,coverage);
    }
    private void RequireQuantity(Quantity quantity,DeliveryCoverage? coverage)
    {
        if(!SupportsQuantityScaling&&!SupportsWholePosition&&!SupportsFixedUnitBinding)
            throw new MarketDataException("risk_quantity_scaling","This instrument has not declared linear scaling in booked quantity.");
        if(SupportsWholePosition&&(SupportsQuantityScaling||quantity is null||quantity.Amount!=ExactDecimal.Parse("1")||
            quantity.Specification!=UnitQuantity.Specification||UnitQuantity.Amount!=ExactDecimal.Parse("1")||coverage is not null))
            throw new MarketDataException("risk_whole_position","Hold one exact normalized whole position without coverage, fractional size, sign reversal or multiplication.");
    }
    internal virtual bool SupportsSettlement=>false;
    internal virtual UnitPricingContract DeclareScope(UnitValuationContext context,bool lifecycle)=>Declare(context);
    public virtual long WorkRows(UnitValuationContext context)=>0;
    internal virtual void RequireSameDefinition(Instrument other)
    {
        if(EconomicIdentity!=other.EconomicIdentity||PricingBindingIdentity!=other.PricingBindingIdentity||RetainedScheduleIdentity!=other.RetainedScheduleIdentity)
            throw new MarketDataException("instrument_identity","An instrument identity cannot reinterpret economic terms or pricing bindings.");
    }
    // Implementers own the declaration. Curve-consuming instruments can use
    // this common resolver; other supported models may override the method.
    public virtual ImmutableArray<QuoteFactor> MarketFactors(UnitValuationContext context)=>
        context.ResolveFactors(Declare(context).Dependencies);
    public virtual ExactMoney Value(UnitValuationContext context)
    {
        if(context.Measure!=ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure","This instrument implements present value; another measure requires its explicit valuation implementation.");
        var inputs=context.SideContext();var value=PV(inputs);
        if(value is null||value.InstrumentId!=Id||value.Currency!=Currency||value.UnitQuantity!=UnitQuantity||!ReferenceEquals(value.Context,inputs))
            throw new MarketDataException("instrument_context","A unit PV must belong to this exact instrument and valuation scenario.");
        return value.PresentValue;
    }
    protected Instrument(Guid id)
    {
        if (id == Guid.Empty) throw new MarketDataException("instrument_definition", "An instrument requires an exact identity.");
        Id = id;
    }
}

public abstract record SidedInstrument : Instrument
{
    public override bool SupportsQuantityScaling=>true;
    internal override bool SupportsSettlement=>true;
    internal override UnitPricingContract DeclareScope(UnitValuationContext context,bool lifecycle)
    {
        if(context.BindingState is ProjectedSwapValuation projected)return projected.Declare(this,context);
        var contract=Declare(context);
        return lifecycle&&contract is not null?contract with{ModelVersion="swap_binding_dispatch_v1/"+contract.ModelVersion}:contract!;
    }
    public override UnitValuation PV(UnitValuationContext context)
    {
        ValidateValuationState(context.BindingState);
        if(context.BindingState is ProjectedSwapValuation projected)
            return new(context,projected.UnitValue(this,context),UnitQuantity.Specification);
        if(context.BindingState is not SwapBindingValuation lifecycle)return base.PV(context);
        if(context.Measure!=ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure","Settlement valuation requires the explicit present-value measure.");
        return new(context,lifecycle.Settlement.UnitValue(this,context.SideContext(lifecycle.Discounting)),UnitQuantity.Specification);
    }
    public override void ValidateValuationState(IBindingValuationState? state)
    {
        if(state is null)return;
        if(state is ProjectedSwapValuation)return;
        if(state is not SwapBindingValuation { Settlement: not null, Discounting: not null } || !SupportsSettlement)
            throw new MarketDataException("settlement_scope","This instrument does not support the supplied binding valuation state.");
    }
    private ImmutableArray<SideMembership> sides;
    private readonly Lazy<ImmutableArray<SideMembership>>? composedSides;
    [IdentityField]
    public ImmutableArray<SideMembership> Sides
    {
        get
        {
            return sides.IsDefaultOrEmpty?composedSides!.Value:sides;
        }
        internal init
        {
            if(composedSides is not null)
                throw new MarketDataException("instrument_definition","Retained Side memberships derive from sealed members and cannot be replaced independently.");
            sides=RequireSides(value);
        }
    }
    public ImmutableArray<CurveRequest> Dependencies =>
        Sides.SelectMany(m => m.Side.Dependencies).Distinct().ToImmutableArray();
    public ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot) =>
        Sides.SelectMany(m => m.Side.DependenciesAt(snapshot)).Distinct().ToImmutableArray();

    protected SidedInstrument(Guid id, ImmutableArray<SideMembership> sides) : base(id)
    {
        this.sides=RequireSides(sides);
    }
    // Derived retained projections preserve their existing economic identity
    // fields and assemble the corresponding concrete Sides after validating them.
    protected SidedInstrument(Guid id):base(id)
    {
        sides=[];
        composedSides=new(()=>RequireSides(ComposeSides()),LazyThreadSafetyMode.ExecutionAndPublication);
    }
    protected virtual ImmutableArray<SideMembership> ComposeSides()=>
        throw new MarketDataException("instrument_definition","This instrument requires explicit concrete Side memberships.");
    private ImmutableArray<SideMembership> RequireSides(ImmutableArray<SideMembership> sides)
    {
        if (Id == Guid.Empty || sides.IsDefaultOrEmpty || sides.Any(m => m is null || m.Side is null))
            throw new MarketDataException("instrument_definition", "An instrument requires an exact identity and at least one valid side membership.");
        if(sides.Any(m=>m.QuantityRatio is not null)&&this is not (ScheduledFinancialSwap or CrossCommodityScheduledSwap or OpenEtrm.MarketData.Gas.GasCompositionInstrument or IRetainedProgramComposition))
            throw new MarketDataException("instrument_definition","Quantity-ratio memberships require the scheduled swap contract version.");
        return sides;
    }

    public override UnitPricingContract Declare(UnitValuationContext context)=>
        new(Id,"independent_side_unit_pv_v1",DependenciesAt(context.Snapshot),UnitQuantity.Specification,Currency) { Time = ValuationTimeDependency.Pinned };

    // The instrument delegates valuation; it never interprets a side's cashflows.
    public override InstrumentValuation PV(SideValuationContext context)
    {
        if (context is null) throw new MarketDataException("side_context", "An explicit valuation context is required.");
        return new(this, context, Sides.Select(m => context.Evaluate(m.Side)).ToImmutableArray());
    }

}
