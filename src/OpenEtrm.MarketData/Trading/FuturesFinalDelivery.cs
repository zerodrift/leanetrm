namespace OpenEtrm.MarketData.Trading;

// Product-owned immutable terms create the ordinary residual instrument. This
// contract establishes neither accepted final margin nor physical performance.
public interface IFuturesFinalDeliveryTerms
{
    Guid Id{get;}
    string Implementation{get;}
    ReferenceKey Series{get;}
    ReferenceKey Quantity{get;}
    ReferenceKey Currency{get;}
    DateTimeOffset From{get;}
    DateTimeOffset To{get;}
    Instrument CreateObligation(ExactUnitPrice finalPrice);
}

public sealed record FuturesFinalDeliveryConfiguration(Guid ProfileId,int ProfileRevision,IFuturesFinalDeliveryTerms Terms);
public sealed record AcceptedFuturesFinalDelivery(Guid PositionId,Guid ProfileId,int ProfileRevision,FuturesFinalDeliveryPlan Plan,
    DateTimeOffset KnownAt,int PublicationOrder);

public sealed record FuturesFinalDeliveryPlan
{
    public IFuturesFinalDeliveryTerms Terms{get;}
    public Instrument Obligation{get;}
    public Quantity Quantity{get;}
    public ExactUnitPrice FinalPrice{get;}
    private FuturesFinalDeliveryPlan(IFuturesFinalDeliveryTerms terms,Instrument obligation,Quantity quantity,ExactUnitPrice finalPrice)
    {Terms=terms;Obligation=obligation;Quantity=quantity;FinalPrice=finalPrice;}
    public static FuturesFinalDeliveryPlan Create(FuturesContract future,Quantity quantity,IFuturesFinalDeliveryTerms terms,ExactUnitPrice finalPrice)
    {
        Validate(future,quantity,terms,finalPrice);
        var obligation=terms.CreateObligation(finalPrice);
        if(obligation is null||!obligation.SupportsQuantityScaling||obligation.Currency!=future.Currency||obligation.UnitQuantity!=future.UnitQuantity)
            throw Invalid();
        return new(terms,obligation,quantity,finalPrice);
    }
    public void Require(FuturesContract future,Quantity quantity,ExactUnitPrice finalPrice)
    {
        Validate(future,quantity,Terms,finalPrice);
        if(quantity!=Quantity||finalPrice!=FinalPrice)throw Invalid();
    }
    private static void Validate(FuturesContract future,Quantity quantity,IFuturesFinalDeliveryTerms terms,ExactUnitPrice finalPrice)
    {
        if(future is null||future.Series.SettlementMethod!="physical"||quantity is null||terms is null||terms.Id==Guid.Empty||
            string.IsNullOrWhiteSpace(terms.Implementation)||terms.Series!=future.SeriesKey||terms.Quantity!=future.UnitQuantity.Specification||
            terms.Currency!=future.Currency||quantity.Specification!=future.UnitQuantity.Specification||quantity.Amount==default||
            !quantity.Amount.IsMultipleOf(future.LotSize.Amount)||!TradingInstant.IsSupported(terms.From)||!TradingInstant.IsSupported(terms.To)||
            terms.From<future.Series.LastTradingAt||terms.From>=terms.To||finalPrice is null||
            finalPrice.Unit!=new PriceUnit(future.Currency,future.UnitQuantity.Specification,future.Series.Multiplier))throw Invalid();
    }
    private static MarketDataException Invalid()=>new("futures_delivery_transition","Select exact product-owned delivery terms, held quantity and terminal quotation.");
}
