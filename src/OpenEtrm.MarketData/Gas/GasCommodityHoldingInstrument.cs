using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public enum GasHoldingCarrying { Nominal=1,CurrencyCurve }

// A reusable unit claim on gas, not its owner's title or performance history.
// Native owned holdings apply signed quantities outside this ordinary pricer.
[EconomicDefinition("gas_commodity_holding_v1")]
public sealed record GasCommodityHoldingInstrument:Instrument,IGasQuantityInstrument
{
    [IdentityField]public CurveRequest Mark{get;}
    [IdentityField]public ReferenceKey QuantitySpecification{get;}
    [IdentityField]public ReferenceKey PaymentCurrency{get;}
    [IdentityField]public DateTimeOffset? ReturnAt{get;}
    [IdentityField]public GasHoldingCarrying Carrying{get;}
    public override Quantity UnitQuantity=>new(ExactDecimal.Parse("1"),QuantitySpecification);
    public override ReferenceKey Currency=>PaymentCurrency;
    public override bool SupportsQuantityScaling=>true;
    Instrument IGasQuantityInstrument.ValuationInstrument=>this;
    public static Guid SelectedNativeMarkIdentity(Guid position,CurveRequest mark,int revision)
    {
        if(position==Guid.Empty||mark.Curve.Revision.CatalogId==Guid.Empty||mark.Curve.Revision.RevisionId==Guid.Empty||
            string.IsNullOrWhiteSpace(mark.Curve.Code)||mark.Contract.Id==Guid.Empty||mark.Contract.Revision!=mark.Curve.Revision||revision<0)
            throw new MarketDataException("gas_owned_mark","Select the exact owned holding and retained mark revision.");
        return ContractIdentity.FromCanonical($"gas_owned_selected_mark_v1:{position:D}:{mark.Curve.Revision.CatalogId:D}:{mark.Curve.Revision.RevisionId:D}:{mark.Curve.Code}:{mark.Contract.Id:D}:{revision}");
    }
    public GasCommodityHoldingInstrument(Guid id,CurveRequest mark,ReferenceKey quantity,ReferenceKey currency,
        DateTimeOffset? returnAt,GasHoldingCarrying carrying):base(id)
    {
        Money.RequireKey(quantity);Money.RequireKey(currency);
        if(mark.Curve.Revision.CatalogId==Guid.Empty||mark.Curve.Revision.RevisionId==Guid.Empty||
            string.IsNullOrWhiteSpace(mark.Curve.Code)||mark.Contract.Id==Guid.Empty||mark.Contract.Revision!=mark.Curve.Revision||
            quantity.Revision!=currency.Revision||!Enum.IsDefined(carrying)||
            returnAt is {} at&&!TradingInstant.IsSupported(at)||carrying==GasHoldingCarrying.CurrencyCurve&&returnAt is null)
            throw new MarketDataException("gas_holding_terms","Select exact native mark, quantity/currency and explicit holding carrying terms.");
        Mark=mark;QuantitySpecification=quantity;PaymentCurrency=currency;ReturnAt=returnAt;Carrying=carrying;
    }
    private SidePaymentKey Return=>new(Id,0,ReturnAt!.Value,Currency);
    public override void ValidateValuationState(IBindingValuationState? state)
    {
        if(state is null)return;
        if(state is not ProjectedGasStockValuation stock||Carrying!=GasHoldingCarrying.Nominal||ReturnAt is not null||stock.Quantity.Specification!=QuantitySpecification)
            throw new MarketDataException("gas_projection_stock","Only an exact projected owned-stock resource state may accompany this gas holding.");
        stock.Validate();
    }
    private void RequireContext(UnitValuationContext context)
    {
        ValidateValuationState(context.BindingState);
        if(context.BindingState is ProjectedGasStockValuation&&!context.IsLifecycleProjection)
            throw new MarketDataException("gas_projection_actual","Hypothetical gas title and supply cannot enter an actual held valuation.");
    }
    public override UnitPricingContract Declare(UnitValuationContext context)
    {
        RequireContext(context);
        return new(Id,"gas_commodity_holding_pv_v1",[Mark],QuantitySpecification,Currency){
            Time=Carrying==GasHoldingCarrying.CurrencyCurve&&ReturnAt>context.CalculationAt?
                ValuationTimeDependency.Between(context.CalculationAt,ReturnAt.Value):ValuationTimeDependency.Independent,
            DiscountInputs=Carrying==GasHoldingCarrying.CurrencyCurve&&ReturnAt>context.CalculationAt?[new(Return,context.PaymentDiscounting(Id))]:[]};
    }
    public override UnitValuation PV(UnitValuationContext context)
    {
        if(context.Measure!=ValuationMeasure.PresentValue)throw new MarketDataException("valuation_measure","A gas holding implements its explicit remaining present value.");
        RequireContext(context);
        var price=context.Inputs.ExactPrice(Mark);
        if(price.Unit.Quantity!=QuantitySpecification||price.Unit.Currency!=Currency)
            throw new MarketDataException("gas_holding_unit","The gas holding mark must retain its exact quantity specification and currency.");
        var value=price.ExactTimes(UnitQuantity);
        if(Carrying==GasHoldingCarrying.CurrencyCurve&&ReturnAt>context.CalculationAt)
        {
            var discounts=context.Inputs.Discounts([Return]);
            if(discounts.Length!=1||discounts[0].Payment!=Return)throw new MarketDataException("gas_holding_discount","The return claim requires its exact declared currency discount input.");
            value=value.Scale(discounts[0].DiscountFactor);
        }
        return new(context,value,QuantitySpecification);
    }
}
