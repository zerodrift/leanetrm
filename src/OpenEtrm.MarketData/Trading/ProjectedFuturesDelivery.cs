using System.Collections.Immutable;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.MarketData.Trading;

// A selected physical branch retains its own exact native mark source and
// captured carrying provider. It is never inferred from a quote or currency.
public sealed record FuturesDeliveryProjectionMarket(GasPhysicalDeliveryTerms Terms,CurveModel Model,MarketSnapshot Source,ISideDiscounting? Discounting);

// This adapter assembles the ordinary residual supplied by the product's shared
// final-delivery plan. It establishes neither native ownership nor performance.
internal static class ProjectedFuturesDelivery
{
    internal static ImmutableArray<CurveRequest> Dependencies(FuturesMarginPolicy policy,FuturesDeliveryProjectionMarket? selected=null)=>
        policy.Delivery?.Terms is GasPhysicalDeliveryTerms gas&&(gas.Mark.Curve.Revision==policy.Contract.Revision||selected?.Terms==gas)?[gas.Mark]:[];

    internal static void Require(FuturesContract future,FuturesMarginPolicy policy,CurveModel model,ValuationSetup setup,MarketSnapshot source,KnownFuturesMargin? knownFinal,FuturesDeliveryProjectionMarket? selected=null,KnownFuturesPhysicalFinal? native=null)
    {
        if(policy.Delivery?.Terms is not GasPhysicalDeliveryTerms gas)
            throw Error("Select the exact qualified physical delivery profile before projecting its final branch.");
        if(selected is not null&&(selected.Terms!=gas||selected.Model is null||selected.Source is null))
            throw new MarketDataException("projection_delivery_source","The prepared delivery source differs from its exact selected native terms.");
        var foreign=gas.Mark.Curve.Revision!=policy.Contract.Revision;
        if(foreign&&(selected is null||selected.Terms!=gas||selected.Source.Definitions!=gas.Mark.Curve.Revision||
            selected.Source.Knowledge!=source.Knowledge||selected.Source.ValuationAt!=source.ValuationAt||selected.Source.MarketCutoff!=source.MarketCutoff))
            throw new MarketDataException("projection_delivery_source","Prepare the selected physical branch's exact native mark source under the common original context and opening clock.");
        var funding=setup.FuturesValueBindings.Values.SingleOrDefault();var physical=funding?.Physical;
        if(physical is null||funding!.Implementation!=FuturesMarginValueBinding.FlatReference||funding.Terms.Policy!=policy||
            physical.Implementation!=FuturesPhysicalBasisValueBinding.Version||physical.ProfileId!=policy.Delivery.ProfileId||physical.ProfileRevision!=policy.Delivery.ProfileRevision)
            throw new MarketDataException("futures_physical_value","Physical economic projection requires the selected funded-margin and declared-delivery-basis valuation under the exact adopted profile.");
        var marking=selected?.Model??model;var markingSource=selected?.Source??source;
        if(PriceUnit.FromQuote(marking.QuoteFor(gas.Mark))!=new PriceUnit(future.Currency,future.UnitQuantity.Specification,future.Series.Multiplier))
            throw Error("The delivery residual requires its exact native mark quotation and explicit units.");
        if(gas.Carrying==GasHoldingCarrying.CurrencyCurve)
        {
            if(physical.Carrying is not IProjectionCurrencyCurve curve||selected is not null&&!ReferenceEquals(selected.Discounting,curve)||
                curve.TimingPolicy!=RateDiscountTiming.Version||curve.Currency!=gas.Currency||!curve.SourceSnapshot.HasSameInputs(markingSource))
                throw new MarketDataException("projection_delivery_discount","Prepare this currency's complete captured exact-instant term structure under the exact source certificate before sampling.");
        }
        if(knownFinal is not null&&native?.Determination!=knownFinal)
            throw Error("A source-known physical final requires its exact native obligation and complete performance descendants before sampling.");
    }

    internal static ValuationPortfolio Residual(AccountingPositionKey owner,FuturesContract future,FuturesMarginPolicy policy,
        Quantity quantity,ExactUnitPrice price,ValuationSetup setup,KnownFuturesPhysicalFinal? native=null)
    {
        var plan=FuturesFinalDeliveryPlan.Create(future,quantity,policy.Delivery?.Terms??throw Error("Retain the selected physical delivery terms."),price);
        var provider=setup.FuturesValueBindings[new(owner.Owner,owner.BindingId)].Physical!.Carrying;
        var residualSetup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,provider);
        var selected=native?.DeliveryOwner??owner;
        return new([plan.Obligation],[new(selected.Owner,selected.BindingId,selected.BookId,plan.Obligation.Id,plan.Quantity,
            new(new Money(default,future.Currency),quantity.Specification),new(default,future.Currency))],residualSetup);
    }
    private static MarketDataException Error(string message)=>new("futures_delivery_transition",message);
}
