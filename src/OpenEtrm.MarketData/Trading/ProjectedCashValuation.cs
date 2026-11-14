using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

// A forecast obligation has no native payment allocations or acceptance receipt.
// It is usable only by the pure lifecycle projection valuation environment.
public sealed record ProjectedCashValuation(Guid TradeId,Guid BindingId,Guid? BookId,Quantity Quantity,Guid InstrumentId,ExactMoney UnitAmount,
    Guid SourceContextId,DateTimeOffset KnownThrough):ICashPaymentValuation
{
    public AccountingOwnerKey Owner{get;init;}=new(AccountingOwnerKind.Trade,TradeId);
    public ProjectedCashValuation(AccountingPositionKey position,Quantity quantity,Guid instrumentId,ExactMoney unitAmount,Guid sourceContextId,DateTimeOffset knownThrough)
        :this(position.TradeId,position.BindingId,position.BookId,quantity,instrumentId,unitAmount,sourceContextId,knownThrough){Owner=position.Owner;}
    public ExactMoney UnitOutstanding(MarketSnapshot source)=>UnitOutstanding(source,source.ValuationAt);
    public ExactMoney UnitOutstanding(MarketSnapshot source,DateTimeOffset calculationAt)
    {
        if(!Owner.Matches(TradeId)||BindingId==Guid.Empty||BookId is null||BookId==Guid.Empty||InstrumentId==Guid.Empty||SourceContextId==Guid.Empty||
            source.Knowledge is not {} knowledge||knowledge.ContextId!=SourceContextId||knowledge.KnownAt!=KnownThrough||
            !TradingInstant.IsSupported(calculationAt)||calculationAt<source.ValuationAt)
            throw new MarketDataException("projection_cash_state","A forecast cash obligation requires its exact owner and immutable opening source context.");
        if(UnitAmount is null||UnitAmount.Currency!=Quantity.Specification)throw new MarketDataException("projection_cash_state","A projected obligation requires its exact per-unit outstanding balance.");
        return UnitAmount;
    }
    public ImmutableArray<DateTimeOffset> FutureChanges(MarketSnapshot source)=>[];
}
