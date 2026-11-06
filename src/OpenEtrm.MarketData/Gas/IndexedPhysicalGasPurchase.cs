using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

// Indexed consideration for a physical purchase. This explicit cash measure
// does not add the commodity's market value or assert delivery fulfillment.
[EconomicDefinition("indexed_physical_gas_purchase_v1")]
public sealed record IndexedPhysicalGasPurchase:SidedInstrument
{
    public PhysicalGasSide Delivery=>(PhysicalGasSide)Sides[0].Side;
    public FloatingSide Consideration=>(FloatingSide)Sides[1].Side;
    public override ReferenceKey Currency=>Consideration.Currency;
    public override Quantity UnitQuantity=>Consideration.UnitQuantity;
    public override DefinitionIdentity PricingBindingIdentity=>DefinitionIdentity.For(new GasInstrumentPricingBindings(
        [..Sides.Select(m=>m.Side.PricingBindingIdentity?.Canonical)]));
    internal override bool SupportsSettlement=>false;

    public IndexedPhysicalGasPurchase(Guid id,PhysicalGasSide delivery,FloatingSide consideration)
        :base(id,[new(delivery,ExactDecimal.Parse("1")),new(consideration,ExactDecimal.Parse("-1"))])
    {
        if(delivery is null||consideration is null||delivery.UnitQuantity!=consideration.UnitQuantity||delivery.Currency!=consideration.Currency)
            throw Invalid("Select compatible independent physical delivery and indexed consideration sides.");
        var physical=delivery.GenerateSchedule().Quantities.Rows;
        var cash=consideration.GenerateDatedSchedule().Payments;
        if(cash.Length!=physical.Length||cash.Where((p,n)=>p.DeliveryFrom!=physical[n].OperatingDay||p.DeliveryTo!=physical[n].OperatingDay.AddDays(1)||
            p.AllocatedQuantity!=physical[n].Delivery||p.PaymentAt<physical[n].End||p.Prices.Length!=1||p.Prices[0].Lookup?.Fixing is null).Any())
            throw Invalid("Each physical gas day requires its exact indexed cash quantity, daily fixing and explicit later payment date.");
    }
    public override void ValidateValuationState(IBindingValuationState? state)
    {
        if(state is not CashflowBindingValuation input||input.Sides.Length!=1||input.Sides[0].MembershipOrdinal!=1)
            throw Invalid("Select explicit payment state for the purchase's consideration membership.");
        input.Sides[0].Require(Consideration,Sides[1],input.Quantity);
    }
    public override UnitPricingContract Declare(UnitValuationContext context)
    {
        ValidateValuationState(context.BindingState);
        if(context.Measure!=ValuationMeasure.UnpaidCashPresentValue)
            throw new MarketDataException("valuation_measure","Select unpaid contractual cash PV for this indexed purchase profile.");
        var state=((CashflowBindingValuation)context.BindingState!).Sides[0];
        return new(Id,"indexed_physical_purchase_cash_pv_v1",state.DependenciesAt(Consideration,context.Snapshot),UnitQuantity.Specification,Currency);
    }
    internal override UnitPricingContract DeclareScope(UnitValuationContext context,bool lifecycle)=>Declare(context);
    public override ExactMoney Value(UnitValuationContext context)
    {
        _=Declare(context);
        // The cash Side owns price lookup, frozen amounts, payment balances and
        // discounting. Ordinary instrument membership supplies the pay sign.
        return context.SideContext().Evaluate(Consideration).PresentValue.Scale(Sides[1].Coefficient);
    }
    public override InstrumentValuation PV(SideValuationContext context)=>
        throw new MarketDataException("valuation_measure","Use the normal unit PV context with the selected cash measure and held payment state.");
    private static MarketDataException Invalid(string message)=>new("indexed_physical_gas",message);
}
