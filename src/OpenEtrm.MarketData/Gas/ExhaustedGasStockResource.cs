using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

// A depleted lot can still have a source-known reservation release to process.
// Its exact resource state survives without a commodity valuation dependency.
internal sealed record ExhaustedGasStockResource(AccountingPositionKey Position,ReferenceKey QuantitySpecification,
    ReferenceKey PaymentCurrency,MarketSnapshot Source):Instrument(ContractIdentity.FromCanonical(
        $"gas_exhausted_stock_resource_v1:{(int)Position.Owner.Kind}:{Position.Owner.Id:D}:{Position.BindingId:D}:{Position.BookId:D}:{Source.Id:D}"))
{
    public override ReferenceKey Currency=>PaymentCurrency;
    public override Quantity UnitQuantity=>new(ExactDecimal.Parse("1"),QuantitySpecification);
    public override bool SupportsQuantityScaling=>true;
    public override void ValidateBinding(Quantity quantity,DeliveryCoverage? coverage)
    {
        base.ValidateBinding(quantity,coverage);
        if(quantity.Specification!=QuantitySpecification||quantity.Amount!=default)
            throw Error();
    }
    public override void ValidateValuationState(IBindingValuationState? state)
    {
        if(state is not ProjectedGasStockValuation stock||stock.Position!=Position||stock.Quantity.Specification!=QuantitySpecification||
            stock.Quantity.Amount!=default||stock.KnownSchedule is not {} schedule||schedule.Operations.IsDefaultOrEmpty||schedule.Operations.Any(p=>p is null||p.Use!=GasKnownStockUse.Release))
            throw Error();
        stock.Validate();
    }
    private void RequireContext(UnitValuationContext context)
    {
        if(!context.IsLifecycleProjection||!Source.HasSameInputs(context.Snapshot))throw Error();
        ValidateValuationState(context.BindingState);
    }
    public override UnitPricingContract Declare(UnitValuationContext context)
    {
        RequireContext(context);
        return new(Id,"gas_exhausted_stock_resource_v1",[],QuantitySpecification,Currency){Time=ValuationTimeDependency.Independent};
    }
    public override UnitValuation PV(UnitValuationContext context)
    {RequireContext(context);return new(context,ExactMoney.Parse("0",Currency),QuantitySpecification);}
    private static MarketDataException Error()=>new("gas_projection_stock","A zero-stock resource requires its exact depleted native binding, accepted release schedule and projection-only source context.");
}
