namespace OpenEtrm.MarketData.Trading;

// Stable native allocation identity and full held owner; never a generated report-row identity.
public readonly record struct AccountingPositionKey(Guid TradeId,Guid BindingId,Guid BookId);
public enum AccountingFlowKind { EconomicCash=1,NonEarningTransfer }
public sealed record AccountingFlow(Guid AllocationId,int Revision,AccountingPositionKey Position,DateTimeOffset EffectiveAt,DateTimeOffset KnownAt,ExactMoney Amount,AccountingFlowKind Kind);
