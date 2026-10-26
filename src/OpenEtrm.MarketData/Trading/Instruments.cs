using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

internal static class TradingInstant
{
    public static bool IsSupported(DateTimeOffset at) => at.Offset == TimeSpan.Zero && at.Ticks % 10 == 0 &&
        at < new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);
}

public abstract record Side
{
    public Guid Id { get; }
    public virtual DefinitionIdentity? PricingBindingIdentity => null;
    internal virtual ImmutableArray<Side> IdentityComponents => [this];
    public abstract ImmutableArray<CurveRequest> Dependencies { get; }
    public virtual ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot) => Dependencies;
    public abstract SideSchedule GenerateSchedule();
    public abstract SideValuation PV(SideValuationContext context);

    protected Side(Guid id)
    {
        if (id == Guid.Empty)
            throw new MarketDataException("side_definition", "A side requires an exact identity.");
        Id = id;
    }
}

public sealed record SideMembership(Side Side, ExactDecimal Coefficient, OpenEtrm.Shared.Numerics.ExactRatio? QuantityRatio = null);
// Keep quote scaling and quantity multiplication deferred until the final sum.
public sealed record WeightedPrice(UnitPrice Price, ExactDecimal Weight);
public sealed record InstrumentSideValue
{
    public Guid SideId { get; }
    public ImmutableArray<WeightedPrice> Prices { get; }
    public Quantity UnitQuantity { get; }
    public InstrumentSideValue(Guid sideId, UnitPrice price, Quantity unitQuantity)
        : this(sideId, [new(price, ExactDecimal.Parse("1"))], unitQuantity) { }
    public InstrumentSideValue(Guid sideId, ImmutableArray<WeightedPrice> prices, Quantity unitQuantity)
    { SideId = sideId; Prices = prices; UnitQuantity = unitQuantity; }
}
public sealed record SignedSideValue(InstrumentSideValue Value, ExactDecimal Coefficient);

internal static class PriceAmounts
{
    internal static ExactMoney Sum(ReferenceKey currency, Quantity unitQuantity,
        ImmutableArray<SignedSideValue> contributions, ImmutableArray<ExactDecimal> quantities)
    {
        if (contributions.IsDefaultOrEmpty || contributions.Any(c => c is null || c.Value is null ||
            c.Value.UnitQuantity != unitQuantity || c.Value.Prices.IsDefaultOrEmpty ||
            c.Value.Prices.Any(p => p is null || p.Price is null || p.Weight.CompareTo(default) <= 0 ||
                p.Price.Unit.Quantity != unitQuantity.Specification || p.Price.Unit.Currency != currency) ||
            c.Value.Prices.Select(p => p.Price.Unit).Distinct().Count() != 1))
            throw new MarketDataException("instrument_contribution", "Every contribution requires the exact currency, per-unit quantity basis and positive price weights.");
        return ExactMoney.WeightedSum(currency, contributions.Select(c =>
            (c.Value.Prices.Select(p => (p.Price.Value, p.Weight)), new[]
                { c.Value.Prices[0].Price.Unit.Multiplier, c.Value.UnitQuantity.Amount, c.Coefficient }.Concat(quantities).ToArray())));
    }
}

public abstract record Instrument
{
    public Guid Id { get; }
    public DefinitionIdentity EconomicIdentity => DefinitionIdentity.For(this);
    public virtual DefinitionIdentity? PricingBindingIdentity => null;
    public abstract ReferenceKey Currency { get; }
    public abstract Quantity UnitQuantity { get; }
    public virtual bool SupportsQuantityScaling=>false;
    public virtual InstrumentValuation PV(SideValuationContext context)=>
        throw new MarketDataException("valuation_measure","This instrument requires its declared unit valuation context.");
    public abstract UnitPricingContract Declare(UnitValuationContext context);
    // Common black-box entry point. The denominator remains explicit until the
    // trade's typed quantity is applied. Value preserves existing pricer adapters.
    public virtual UnitValuation PV(UnitValuationContext context)=>new(context,Value(context),UnitQuantity.Specification);
    public virtual void ValidateBinding(Quantity quantity,DeliveryCoverage? coverage)
    {
        if(!SupportsQuantityScaling)
            throw new MarketDataException("risk_quantity_scaling","This instrument has not declared linear scaling in booked quantity.");
        if(coverage is not null)
            throw new MarketDataException("ppa_segment_coverage","This instrument does not accept delivery coverage.");
    }
    internal virtual bool SupportsSettlement=>false;
    internal virtual UnitPricingContract DeclareScope(UnitValuationContext context,bool lifecycle)=>Declare(context);
    public virtual long WorkRows(UnitValuationContext context)=>0;
    internal virtual void RequireSameDefinition(Instrument other)
    {
        if(EconomicIdentity!=other.EconomicIdentity||PricingBindingIdentity!=other.PricingBindingIdentity)
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
        var contract=Declare(context);
        return lifecycle&&contract is not null?contract with{ModelVersion="swap_binding_dispatch_v1/"+contract.ModelVersion}:contract!;
    }
    public override UnitValuation PV(UnitValuationContext context)
    {
        if(context.Settlement is not {} lifecycle)return base.PV(context);
        if(context.Measure!=ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure","Settlement valuation requires the explicit present-value measure.");
        return new(context,lifecycle.Settlement.UnitValue(this,context.SideContext(lifecycle.Discounting)),UnitQuantity.Specification);
    }
    [IdentityField]
    public ImmutableArray<SideMembership> Sides { get; }
    public ImmutableArray<CurveRequest> Dependencies =>
        Sides.SelectMany(m => m.Side.Dependencies).Distinct().ToImmutableArray();
    public ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot) =>
        Sides.SelectMany(m => m.Side.DependenciesAt(snapshot)).Distinct().ToImmutableArray();

    protected SidedInstrument(Guid id, ImmutableArray<SideMembership> sides) : base(id)
    {
        if (id == Guid.Empty || sides.IsDefaultOrEmpty || sides.Any(m => m is null || m.Side is null))
            throw new MarketDataException("instrument_definition", "An instrument requires an exact identity and at least one valid side membership.");
        if(sides.Any(m=>m.QuantityRatio is not null)&&this is not (ScheduledFinancialSwap or CrossCommodityScheduledSwap))
            throw new MarketDataException("instrument_definition","Quantity-ratio memberships require the scheduled swap contract version.");
        Sides = sides;
    }

    public override UnitPricingContract Declare(UnitValuationContext context)=>
        new(Id,"independent_side_unit_pv_v1",DependenciesAt(context.Snapshot),UnitQuantity.Specification,Currency);

    // The instrument delegates valuation; it never interprets a side's cashflows.
    public override InstrumentValuation PV(SideValuationContext context)
    {
        if (context is null) throw new MarketDataException("side_context", "An explicit valuation context is required.");
        return new(this, context, Sides.Select(m => context.Evaluate(m.Side)).ToImmutableArray());
    }

    // Legacy execution-P&L arithmetic; full PV delegates to the sides above.
    // Contributions are ordered by membership, with direction applied once.
    public Money SumSideValues(ImmutableArray<InstrumentSideValue> values)
        => Value(values, ExactDecimal.Parse("1"));

    public virtual Money Value(ImmutableArray<InstrumentSideValue> values, ExactDecimal quantity) => Value(values, [quantity]);

    protected Money Value(ImmutableArray<InstrumentSideValue> values, ImmutableArray<ExactDecimal> quantities)
    {
        if (Sides.Any(s=>s.QuantityRatio is not null))
            throw new MarketDataException("instrument_contribution","Rational quantity memberships require schedule-based PV valuation.");
        if (values.IsDefault || values.Length != Sides.Length ||
            values.Where((v, i) => v is null || v.SideId != Sides[i].Side.Id).Any())
            throw new MarketDataException("instrument_contribution", "Every membership requires a value in the exact instrument currency and per-unit quantity basis.");
        return PriceAmounts.Sum(Currency, UnitQuantity,
            values.Select((v, i) => new SignedSideValue(v, Sides[i].Coefficient)).ToImmutableArray(), quantities).ToMoney();
    }
}
