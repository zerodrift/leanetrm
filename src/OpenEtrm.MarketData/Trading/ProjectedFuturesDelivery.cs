using System.Collections.Immutable;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.MarketData.Trading;

// This adapter assembles the ordinary residual supplied by the product's shared
// final-delivery plan. It establishes neither native ownership nor performance.
internal static class ProjectedFuturesDelivery
{
    internal static ImmutableArray<CurveRequest> Dependencies(FuturesMarginPolicy policy)=>
        policy.Delivery?.Terms is GasPhysicalDeliveryTerms gas&&gas.Mark.Curve.Revision==policy.Contract.Revision?[gas.Mark]:[];

    internal static void Require(FuturesContract future,FuturesMarginPolicy policy,CurveModel model,ValuationSetup setup,MarketSnapshot source,bool knownFinal)
    {
        if(policy.Delivery?.Terms is not GasPhysicalDeliveryTerms gas)
            throw Error("Select the exact qualified physical delivery profile before projecting its final branch.");
        if(gas.Mark.Curve.Revision!=policy.Contract.Revision)
            throw new MarketDataException("projection_delivery_source","The physical-final projection requires the delivery mark and future in one certified market definition; a connected cross-definition handoff is not yet qualified.");
        if(PriceUnit.FromQuote(model.QuoteFor(gas.Mark))!=new PriceUnit(future.Currency,future.UnitQuantity.Specification,future.Series.Multiplier))
            throw Error("The delivery residual requires its exact native mark quotation and explicit units.");
        if(gas.Carrying==GasHoldingCarrying.CurrencyCurve)
        {
            if((setup.Discounting??setup.InstrumentDiscounting.GetValueOrDefault(future.Id)) is not IProjectionCurrencyCurve curve||
                curve.TimingPolicy!=RateDiscountTiming.Version||curve.Currency!=gas.Currency||!curve.SourceSnapshot.HasSameInputs(source))
                throw new MarketDataException("projection_delivery_discount","Prepare this currency's complete captured exact-instant term structure under the exact source certificate before sampling.");
        }
        if(knownFinal)
            throw Error("A source-known physical final requires its exact native obligation and complete performance descendants before sampling.");
    }

    internal static ValuationPortfolio Residual(AccountingPositionKey owner,FuturesContract future,FuturesMarginPolicy policy,
        Quantity quantity,ExactUnitPrice price,ValuationSetup setup)
    {
        var plan=FuturesFinalDeliveryPlan.Create(future,quantity,policy.Delivery?.Terms??throw Error("Retain the selected physical delivery terms."),price);
        var provider=setup.Discounting??setup.InstrumentDiscounting.GetValueOrDefault(future.Id);
        var residualSetup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,provider);
        return new([plan.Obligation],[new(owner.Owner,owner.BindingId,owner.BookId,plan.Obligation.Id,plan.Quantity,
            new(new Money(default,future.Currency),quantity.Specification),new(default,future.Currency))],residualSetup);
    }
    private static MarketDataException Error(string message)=>new("futures_delivery_transition",message);
}
