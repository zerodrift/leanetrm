namespace OpenEtrm.MarketData.Trading;

public sealed record SwapContractBridge(SwapEconomicBridge Total,ExactMoney NewTradeEconomics,ExactMoney ContractualAmendment,ExactMoney ContinuingEconomics)
{
    public const string Policy="closing_context_contract_changes_v1";
    public static SwapContractBridge Calculate(SwapLifecycleValue? opening,SwapLifecycleValue closing,SwapLifecycleValue? priorTermsAtClosing,ExactMoney periodCash)
    {
        if(closing is null||periodCash is null||opening is null&&priorTermsAtClosing is not null)
            throw new MarketDataException("settlement_bridge","Supply complete actual lifecycle values and an authorized closing-context prior contract when amended.");
        var zero=ExactMoney.Parse("0",closing.NetCash.Currency);
        static ExactMoney Economics(SwapLifecycleValue value)=>value.RemainingValue.Add(value.NetCash);
        var initial=opening??new(zero,zero,zero,zero,zero,zero,zero,zero,zero,zero,[]);
        var total=SwapEconomicBridge.Calculate(initial,closing,periodCash);
        var added=opening is null?Economics(closing):zero;
        var amendment=priorTermsAtClosing is null?zero:Economics(closing).Subtract(Economics(priorTermsAtClosing));
        var continuing=opening is null?zero:Economics(priorTermsAtClosing??closing).Subtract(Economics(opening));
        if(added.Add(amendment).Add(continuing)!=total.EconomicPnl)
            throw new MarketDataException("settlement_bridge","Independently valued contractual changes and continuing economics must reconcile.");
        return new(total,added,amendment,continuing);
    }
}
