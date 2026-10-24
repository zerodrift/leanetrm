namespace OpenEtrm.MarketData.Trading;

public sealed record SettlementDetermination
{
    public Guid Id { get; }
    public Guid? PreviousId { get; }
    public SwapPaymentCalculation Calculation { get; }
    public Money Total => Calculation.Amount;
    public Money Contribution { get; }
    public DateTimeOffset DueAt { get; }
    private SettlementDetermination(Guid id, SwapPaymentCalculation calculation, SettlementDetermination? previous, DateTimeOffset dueAt)
    {
        Id = id; Calculation = calculation; PreviousId = previous?.Id; DueAt = dueAt;
        Contribution = RestatedContribution(calculation.Amount,previous?.Total);
    }
    public static Money RestatedContribution(Money roundedTotal,Money? previousRoundedTotal)
    {
        if(roundedTotal is null)throw new MarketDataException("settlement_determination","Supply the rounded contractual total.");
        return previousRoundedTotal is null?roundedTotal:ExactMoney.FromMoney(roundedTotal).Subtract(ExactMoney.FromMoney(previousRoundedTotal)).ToMoney();
    }

    public static SettlementDetermination Create(Guid id, SwapPaymentCalculation calculation,
        SettlementDetermination? previous, DateTimeOffset? correctionDueAt)
    {
        if (id == Guid.Empty || calculation is null)
            throw new MarketDataException("settlement_determination", "Supply an immutable determination identity and complete final calculation.");
        if (previous is null)
        {
            if (correctionDueAt is not null)
                throw new MarketDataException("settlement_correction_due", "An original determination retains its contractual payment date.");
            return new(id, calculation, null, calculation.Evidence.Plan.PaymentAt);
        }
        if (previous.Id == id || previous.Calculation.Occurrence != calculation.Occurrence ||
            previous.Calculation.Rounding != calculation.Rounding || previous.Calculation.ContractHash != calculation.ContractHash)
            throw new MarketDataException("settlement_continuity", "A correction must retain the original occurrence, currency precision and rounding policy.");
        if (correctionDueAt is not {} due || !TradingInstant.IsSupported(due) || due == default)
            throw new MarketDataException("settlement_correction_due", "Supply and retain an explicit UTC correction due instant.");
        if (previous.Calculation.InputHash == calculation.InputHash)
            throw new MarketDataException("settlement_no_change", "These final inputs already determine the occurrence.");
        return new(id, calculation, previous, due);
    }
}
