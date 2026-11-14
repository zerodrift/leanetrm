namespace OpenEtrm.MarketData.Trading;

// Stable native allocation identity and full held owner; never a generated report-row identity.
public enum AccountingOwnerKind { Trade=1, NativeHolding=2 }

// Native storage retains the exact foreign keys. This key carries the admitted
// ownership boundary through valuation without manufacturing a booked trade.
public readonly record struct AccountingOwnerKey(AccountingOwnerKind Kind,Guid Id)
{
    [System.Text.Json.Serialization.JsonIgnore] public bool IsValid=>Enum.IsDefined(Kind)&&Id!=Guid.Empty;
    [System.Text.Json.Serialization.JsonIgnore] public Guid TradeId=>Kind==AccountingOwnerKind.Trade?Id:Guid.Empty;
    public bool Matches(Guid tradeId)=>IsValid&&TradeId==tradeId;
}

[method:System.Text.Json.Serialization.JsonConstructor]
public readonly record struct AccountingPositionKey(Guid TradeId,Guid BindingId,Guid BookId)
{
    public AccountingOwnerKey Owner{get;init;}=new(AccountingOwnerKind.Trade,TradeId);
    public AccountingPositionKey(AccountingOwnerKey owner,Guid bindingId,Guid bookId):this(owner.TradeId,bindingId,bookId){Owner=owner;}
    [System.Text.Json.Serialization.JsonIgnore] public bool IsValid=>Owner.Matches(TradeId)&&BindingId!=Guid.Empty&&BookId!=Guid.Empty;
}
public enum AccountingFlowKind { EconomicCash=1,NonEarningTransfer }
public sealed record AccountingFlow(Guid AllocationId,int Revision,AccountingPositionKey Position,DateTimeOffset EffectiveAt,DateTimeOffset KnownAt,ExactMoney Amount,AccountingFlowKind Kind);
