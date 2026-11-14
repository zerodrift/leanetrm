using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed partial class SwapStore
{
    internal async Task<SwapSettlementInputs> ProjectionSettlement(NpgsqlConnection connection,ResolvedSwapHolding holding,
        string duePolicy,string recognitionPolicy,CancellationToken ct)
    {
        if(holding.Settlement is {} accepted)return accepted;
        var zero=ExactMoney.Parse("0",holding.Instrument.Currency);
        return new(holding.Row.Id("trade_id"),holding.Row.Id("binding_id"),holding.Quantity,[],zero,zero,
            await SettlementCompletions(connection,holding.Row,holding.Instrument,ct),duePolicy,recognitionPolicy);
    }
}
