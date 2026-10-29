using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

public enum MonthlyQuantityBasis { PerMonth=1,TotalEqualMonths,TotalCalendarDays }

[EconomicDefinition("whole_term_projection_bindings_v1")]
internal sealed record MonthlyProjectionBindings([property:IdentityField]ImmutableArray<FloatingProjectionBinding> Periods);

[EconomicDefinition("MonthlyFinancialSwap_v2")]
public sealed record MonthlyFinancialSwap:SidedInstrument
{
    public const string Implementation="whole_term_monthly_financial_swap_v1";
    [IdentityField]public bool Discounting { get; }
    public FixedSide Fixed=>(FixedSide)Sides[0].Side;
    public FloatingSide Floating=>(FloatingSide)Sides[1].Side;
    public override ReferenceKey Currency=>Fixed.Currency;
    public override Quantity UnitQuantity=>Fixed.UnitQuantity;
    public override DefinitionIdentity PricingBindingIdentity=>Floating.PricingBindingIdentity;
    public MonthlyFinancialSwap(Guid id,ImmutableArray<SideMembership> sides,string implementation,bool discounting):base(id,sides)
    {
        var one=ExactDecimal.Parse("1");var minus=ExactDecimal.Parse("-1");
        if(implementation!=Implementation || sides.Length!=2 || sides[0].Side is not FixedSide a || sides[1].Side is not FloatingSide b || a.Periods.IsDefaultOrEmpty || b.Periods.IsDefaultOrEmpty || a.Id==b.Id ||
            !(sides[0].Coefficient==one && sides[1].Coefficient==minus || sides[0].Coefficient==minus && sides[1].Coefficient==one) ||
            a.Currency!=b.Currency || a.UnitQuantity!=b.UnitQuantity || a.DeliveryFrom!=b.DeliveryFrom || a.DeliveryTo!=b.DeliveryTo ||
            !a.Quantities.SequenceEqual(b.Quantities))
            throw new MarketDataException("monthly_swap_instrument","Pair whole-term fixed and floating sides with the same monthly delivery, exact allocations, currency/unit and opposite directions.");
        SwapSidePeriods.Monthly(a);SwapSidePeriods.Monthly(b);Discounting=discounting;
    }
    public override InstrumentValuation PV(SideValuationContext context)=>FinancialSwapInstrument.RequireDiscounting(base.PV(context),Discounting);
}
