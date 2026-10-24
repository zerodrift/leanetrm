using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// The generated payments own the resolved quantities and observation applications.
// Each side is normalized independently; relative leg size belongs to membership.
[EconomicDefinition("ScheduledSwapSide_v1")]
public sealed record ScheduledSwapSide : Side
{
    public const string Implementation="explicit_monthly_quantity_schedule_v1";
    [IdentityField] public ImmutableArray<Side> Periods { get; }
    [IdentityField] public ImmutableArray<ExactRatio> Quantities { get; }
    public ReferenceKey Currency=>schedule.Currency;
    public Quantity UnitQuantity=>schedule.UnitQuantity;
    public DateOnly DeliveryFrom=>schedule.Payments[0].DeliveryFrom;
    public DateOnly DeliveryTo=>schedule.Payments[^1].DeliveryTo;
    public bool IsFixed=>Periods[0] is FixedSide;
    public override DefinitionIdentity PricingBindingIdentity=>DefinitionIdentity.For(new MonthlyProjectionBindings(
        Periods.OfType<FloatingSide>().Select(p=>p.PricingBinding).ToImmutableArray()));
    internal override ImmutableArray<Side> IdentityComponents=>[this,..Periods];
    private readonly SidePaymentSchedule schedule;
    public override ImmutableArray<CurveRequest> Dependencies=>schedule.Dependencies;
    public override ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot)=>schedule.DependenciesAt(snapshot);
    public ScheduledSwapSide(Guid id,ImmutableArray<Side> periods,ImmutableArray<ExactRatio> quantities,string implementation):base(id)
    {
        // Every version shares month/quote/identity qualification, while owning
        // its explicit allocation records.
        if(implementation!=Implementation)throw new MarketDataException("scheduled_swap_periods","Select the explicit scheduled-side version.");
        ImmutableArray<SidePaymentPlan> payments;
        try{payments=MonthlySidePeriods.Validate(id,periods);}
        catch(MarketDataException e)when(e.Code=="monthly_swap_periods"){throw new MarketDataException("scheduled_swap_periods",e.Message);}
        if(quantities.IsDefault||quantities.Length!=periods.Length||quantities.Any(q=>q is null)||quantities.Aggregate((a,b)=>a.Add(b))!=ExactRatio.One)
            throw new MarketDataException("scheduled_quantity","Every monthly period requires a positive exact share; independently normalized side shares must sum to one.");
        Periods=periods;Quantities=quantities;
        var first=payments[0];
        schedule=new(id,first.Currency,first.UnitQuantity,Implementation,payments.Select((p,n)=>
            new SidePaymentPlan(p,id,n,new(quantities[n].Numerator,quantities[n].Denominator,first.UnitQuantity.Specification))).ToImmutableArray());
    }
    public override SidePaymentSchedule GenerateSchedule()=>schedule;
    public override PaymentSideValuation PV(SideValuationContext context)=>schedule.Evaluate(context);
}

[EconomicDefinition("ScheduledFinancialSwap_v1")]
public sealed record ScheduledFinancialSwap:SidedInstrument
{
    public const string Implementation="independent_scheduled_financial_swap_v1";
    [IdentityField] public bool Discounting { get; }
    public ScheduledSwapSide First=>(ScheduledSwapSide)Sides[0].Side;
    public ScheduledSwapSide Second=>(ScheduledSwapSide)Sides[1].Side;
    public override ReferenceKey Currency=>First.Currency;
    public override Quantity UnitQuantity=>First.UnitQuantity;
    public override DefinitionIdentity PricingBindingIdentity=>DefinitionIdentity.For(new ScheduledProjectionBindings(Sides.Select(m=>m.Side.PricingBindingIdentity!.Canonical).ToImmutableArray()));
    public ScheduledFinancialSwap(Guid id,ImmutableArray<SideMembership> sides,bool discounting,string implementation):base(id,sides)
    {
        var one=ExactDecimal.Parse("1");var minus=ExactDecimal.Parse("-1");
        if(implementation!=Implementation||sides.Length!=2||sides[0].Side is not ScheduledSwapSide a||sides[1].Side is not ScheduledSwapSide b||b.IsFixed||
            a.Currency!=b.Currency||a.UnitQuantity!=b.UnitQuantity||sides.Any(m=>m.QuantityRatio is null)||sides[0].QuantityRatio!=ExactRatio.One||
            !(sides[0].Coefficient==one&&sides[1].Coefficient==minus||sides[0].Coefficient==minus&&sides[1].Coefficient==one))
            throw new MarketDataException("scheduled_swap_instrument","Select independent scheduled sides with exact common currency/unit, explicit relative quantities and opposite directions.");
        Discounting=discounting;
    }
    public override InstrumentValuation PV(SideValuationContext context)=>FinancialSwapInstrument.RequireDiscounting(base.PV(context),Discounting);
}
[EconomicDefinition("scheduled_projection_bindings_v1")]
internal sealed record ScheduledProjectionBindings([property:IdentityField]ImmutableArray<string> Sides);
