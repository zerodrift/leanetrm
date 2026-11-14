using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

// Saved adapter economics. Actual acceptance separately proves the exact native
// future, official final, title evidence and supplied stock under current authority.
[EconomicDefinition("gas_physical_delivery_terms_v1")]
public sealed record GasPhysicalDeliveryTerms:IFuturesFinalDeliveryTerms
{
    public const string Version="gas_future_physical_delivery_v1";
    [IdentityField]public Guid Id{get;}
    [IdentityField]public ReferenceKey Series{get;}
    [IdentityField]public ReferenceKey Quantity{get;}
    [IdentityField]public ReferenceKey Currency{get;}
    [IdentityField]public ReferenceKey Location{get;}
    [IdentityField]public DateTimeOffset From{get;}
    [IdentityField]public DateTimeOffset To{get;}
    [IdentityField]public DateTimeOffset CashDueAt{get;}
    [IdentityField]public CurveRequest Mark{get;}
    [IdentityField]public GasHoldingCarrying Carrying{get;}
    [IdentityField]public string Implementation{get;}
    public GasPhysicalDeliveryTerms(Guid id,ReferenceKey series,ReferenceKey quantity,ReferenceKey currency,ReferenceKey location,
        DateTimeOffset from,DateTimeOffset to,DateTimeOffset cashDueAt,CurveRequest mark,GasHoldingCarrying carrying,string implementation)
    {
        foreach(var key in new[]{series,quantity,currency,location})Money.RequireKey(key);
        if(id==Guid.Empty||implementation!=Version||!TradingInstant.IsSupported(from)||!TradingInstant.IsSupported(to)||from>=to||
            !TradingInstant.IsSupported(cashDueAt)||cashDueAt<from||!Enum.IsDefined(carrying)||
            series.Revision!=quantity.Revision||quantity.Revision!=currency.Revision||currency.Revision!=location.Revision||
            mark.Curve.Revision.CatalogId==Guid.Empty||mark.Curve.Revision.RevisionId==Guid.Empty||string.IsNullOrWhiteSpace(mark.Curve.Code)||
            mark.Contract.Id==Guid.Empty||mark.Contract.Revision!=mark.Curve.Revision)
            throw new MarketDataException("gas_delivery_terms","Select qualified exact physical delivery, quantity, location, cash timing and marking terms.");
        Id=id;Series=series;Quantity=quantity;Currency=currency;Location=location;From=from;To=to;CashDueAt=cashDueAt;
        Mark=mark;Carrying=carrying;Implementation=implementation;
    }
    public Instrument CreateObligation(ExactUnitPrice finalPrice)=>new GasPhysicalDeliveryObligationInstrument(
        ContractIdentity.FromCanonical(DefinitionIdentity.CanonicalTerm(new object?[]{"gas_physical_delivery_obligation_v1",this,finalPrice})),this,finalPrice);
}
public sealed record GasPhysicalTitlePlan(Quantity Remaining,Quantity StockChange,ExactMoney Consideration,DateTimeOffset CashDueAt);

public static class GasPhysicalFinalHandoff
{
    // Pure conservation shared by actual planning and projection. This returns
    // no accepted fact and cannot establish title, procurement or a paid receipt.
    public static GasPhysicalTitlePlan Title(GasPhysicalDeliveryTerms terms,Quantity remaining,Quantity performed,
        ExactUnitPrice finalPrice,DateTimeOffset at,ExactDecimal availableSupply)
    {
        if(terms is null||remaining is null||performed is null||finalPrice is null||remaining.Specification!=terms.Quantity||
            performed.Specification!=terms.Quantity||performed.Amount.CompareTo(default)<=0||remaining.Amount==default||
            performed.Amount.CompareTo(remaining.Amount.CompareTo(default)<0?-remaining.Amount:remaining.Amount)>0||
            finalPrice.Unit.Currency!=terms.Currency||finalPrice.Unit.Quantity!=terms.Quantity||!TradingInstant.IsSupported(at)||at<terms.From||at>=terms.To)
            throw new MarketDataException("gas_delivery_performance","Select an exact outstanding delivery quantity, native final quotation and performance instant.");
        var shortSide=remaining.Amount.CompareTo(default)<0;
        if(shortSide&&availableSupply.CompareTo(performed.Amount)<0)
            throw new MarketDataException("gas_delivery_supply","Physical delivery requires explicitly identified available owned supply; no purchase or negative lot is inferred.");
        var signed=shortSide?-performed.Amount:performed.Amount;
        return new(new(remaining.Amount-signed,terms.Quantity),new(signed,terms.Quantity),
            finalPrice.ExactTimes(new(-signed,terms.Quantity)),terms.CashDueAt);
    }
}

// Final VM closes the futures mark exposure, but physical performance remains
// an ordinary acquisition/dispatch obligation until title is actually exchanged.
[EconomicDefinition("gas_physical_delivery_obligation_v1")]
public sealed record GasPhysicalDeliveryObligationInstrument:Instrument,IGasQuantityInstrument
{
    [IdentityField]public GasPhysicalDeliveryTerms Terms{get;}
    [IdentityField]public ExactUnitPrice FinalPrice{get;}
    public override ReferenceKey Currency=>Terms.Currency;
    public override Quantity UnitQuantity=>new(ExactDecimal.Parse("1"),Terms.Quantity);
    public override bool SupportsQuantityScaling=>true;
    CurveRequest IGasQuantityInstrument.Mark=>Terms.Mark;
    ReferenceKey IGasQuantityInstrument.QuantitySpecification=>Terms.Quantity;
    DateTimeOffset? IGasQuantityInstrument.ReturnAt=>null;
    GasHoldingCarrying IGasQuantityInstrument.Carrying=>Terms.Carrying;
    Instrument IGasQuantityInstrument.ValuationInstrument=>this;
    public GasPhysicalDeliveryObligationInstrument(Guid id,GasPhysicalDeliveryTerms terms,ExactUnitPrice finalPrice):base(id)
    {
        if(terms is null||finalPrice is null||finalPrice.Unit.Currency!=terms.Currency||finalPrice.Unit.Quantity!=terms.Quantity)
            throw new MarketDataException("gas_delivery_terms","A physical obligation requires its exact accepted or path-local final quotation.");
        Terms=terms;FinalPrice=finalPrice;
    }
    public System.Collections.Immutable.ImmutableArray<SidePaymentKey> RequiredDiscounts(DateTimeOffset calculationAt)=>
        GasDeliveryExchange.RequiredDiscounts(Id,Terms,calculationAt);
    public override UnitPricingContract Declare(UnitValuationContext context)
    {
        var discounted=RequiredDiscounts(context.CalculationAt);
        return new(Id,"gas_physical_delivery_obligation_pv_v1",[Terms.Mark],Terms.Quantity,Currency){
            Time=discounted.IsEmpty?ValuationTimeDependency.Independent:ValuationTimeDependency.Between(context.CalculationAt,discounted.Min(p=>p.PaymentAt)),
            DiscountInputs=[..discounted.Select(p=>new DiscountInput(p,context.PaymentDiscounting(Id)))]};
    }
    public override UnitValuation PV(UnitValuationContext context)
    {
        if(context.Measure!=ValuationMeasure.PresentValue)throw new MarketDataException("valuation_measure","A physical delivery obligation implements remaining PV.");
        ValidateValuationState(context.BindingState);
        return new(context,GasDeliveryExchange.Value(Id,Terms,UnitQuantity,context.Inputs.ExactPrice(Terms.Mark),FinalPrice,context),Terms.Quantity);
    }
}
