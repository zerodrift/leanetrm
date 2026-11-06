namespace OpenEtrm.MarketData.Trading;

// Operation-owned facts belong to a held occurrence, not to a reusable economic
// definition. The instrument validates and interprets its supported state.
public interface IBindingValuationState
{
    Guid TradeId { get; }
    Guid BindingId { get; }
    Quantity Quantity { get; }
}
