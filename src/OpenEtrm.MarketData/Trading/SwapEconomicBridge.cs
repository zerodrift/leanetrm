namespace OpenEtrm.MarketData.Trading;

public sealed record SwapEconomicBridge(ExactMoney EconomicPnl,ExactMoney RemainingValueMovement,ExactMoney NetCashMovement,
    ExactMoney RealizedTransfer,ExactMoney UnrealizedMovement,ExactMoney ContractualCashMovement,ExactMoney DiscountEffectMovement,
    ExactMoney CorrectionsMovement,ExactMoney RoundingEffectMovement,ExactMoney PeriodCashReceived,ExactMoney PriorCashKnowledgeAdjustment)
{
    public static SwapEconomicBridge Calculate(SwapLifecycleValue opening,SwapLifecycleValue closing,ExactMoney periodCashReceived)
    {
        if(opening is null||closing is null||periodCashReceived is null)throw new MarketDataException("settlement_bridge","Supply both explicit lifecycle values and effective-dated period cash.");
        var remaining=closing.RemainingValue.Subtract(opening.RemainingValue);
        var cash=closing.NetCash.Subtract(opening.NetCash);
        var economic=remaining.Add(cash);
        var realized=closing.Realized.Subtract(opening.Realized);
        var unrealized=closing.Unrealized.Subtract(opening.Unrealized);
        var zero=ExactMoney.FromMoney(new(default,economic.Currency));
        var contractual=closing.Payments.Select(p=>p.ContractualAmount).Aggregate(zero,(a,b)=>a.Add(b))
            .Subtract(opening.Payments.Select(p=>p.ContractualAmount).Aggregate(zero,(a,b)=>a.Add(b)));
        if(realized.Add(unrealized)!=economic)throw new MarketDataException("settlement_bridge","Realized/unrealized transfers must reconcile to economic P&L.");
        return new(economic,remaining,cash,realized,unrealized,contractual,economic.Subtract(contractual),
            closing.Corrections.Subtract(opening.Corrections),closing.RoundingEffect.Subtract(opening.RoundingEffect),periodCashReceived,cash.Subtract(periodCashReceived));
    }
}
