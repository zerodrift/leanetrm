using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

// Every timing and reporting choice is retained with this bounded fee profile.
// The cutoff is a fee/valuation plan; it never discharges overdue principal.
[EconomicDefinition("gas_park_loan_balance_fee_terms_v1")]
public sealed record GasParkLoanBalanceFeeTerms
{
    public const string Version="physical_gas_park_loan_balance_time_v1";
    public const string AcceptedOutstanding="accepted_outstanding_principal_v1";
    public const string ElapsedHour="elapsed_utc_hour_v1";
    public const string AcceptedAdvance="accepted_advance_v1";
    public const string ContractualCutoff="contractual_accrual_cutoff_v1";
    public const string AccrueOverdue="accrue_outstanding_until_cutoff_v1";
    public const string ReturnAtCutoff="remaining_return_at_fee_cutoff_v1";
    public const string UnadvancedAtEvaluation="unadvanced_plan_from_later_of_earliest_or_evaluation_v1";
    public const string CumulativeDifference="rounded_cumulative_difference_v1";

    [IdentityField]public string Implementation{get;}
    [IdentityField]public ReferenceKey Quantity{get;}
    [IdentityField]public ReferenceKey Currency=>Rate.Currency;
    public Money Rate{get;}
    [IdentityField]public ExactDecimal RateAmount=>Rate.Amount;
    [IdentityField]public ExactDecimal Direction{get;}
    [IdentityField]public DateTimeOffset CutoffAt{get;}
    [IdentityField]public DateTimeOffset DueAt{get;}
    [IdentityField]public string Basis{get;}
    [IdentityField]public string TimeBasis{get;}
    [IdentityField]public string StartPolicy{get;}
    [IdentityField]public string EndPolicy{get;}
    [IdentityField]public string OverduePolicy{get;}
    [IdentityField]public string ProjectionPolicy{get;}
    [IdentityField]public string UnadvancedPolicy{get;}
    [IdentityField]public string DeterminationPolicy{get;}
    public CashRoundingConvention Rounding{get;}
    [IdentityField]public ExactDecimal RoundingIncrement=>Rounding.Increment;
    [IdentityField]public CashTieRule RoundingTieRule=>Rounding.TieRule;
    [IdentityField]public CashRoundingStage RoundingStage=>Rounding.Stage;

    public GasParkLoanBalanceFeeTerms(string implementation,ReferenceKey quantity,Money rate,ExactDecimal direction,
        DateTimeOffset cutoffAt,DateTimeOffset dueAt,string basis,string timeBasis,string startPolicy,string endPolicy,
        string overduePolicy,string projectionPolicy,string unadvancedPolicy,string determinationPolicy,CashRoundingConvention rounding)
    {
        Money.RequireKey(quantity);
        if(implementation!=Version||rate is null||rate.Currency.Revision!=quantity.Revision||rate.Amount.CompareTo(default)<0||
            direction!=ExactDecimal.Parse("1")&&direction!=ExactDecimal.Parse("-1")||
            !TradingInstant.IsSupported(cutoffAt)||!TradingInstant.IsSupported(dueAt)||dueAt<cutoffAt||
            basis!=AcceptedOutstanding||timeBasis!=ElapsedHour||startPolicy!=AcceptedAdvance||endPolicy!=ContractualCutoff||
            overduePolicy!=AccrueOverdue||projectionPolicy!=ReturnAtCutoff||unadvancedPolicy!=UnadvancedAtEvaluation||
            determinationPolicy!=CumulativeDifference||rounding is null)
            throw new MarketDataException("gas_balance_fee_terms","Select the complete installed outstanding-principal elapsed-hour fee profile, independent direction, bounded cutoff, due time and cumulative cash rounding.");
        rounding.Require(rate.Currency,CashRoundingStage.PaymentTotal);
        Implementation=implementation;Quantity=quantity;Rate=rate;Direction=direction;CutoffAt=cutoffAt;DueAt=dueAt;
        Basis=basis;TimeBasis=timeBasis;StartPolicy=startPolicy;EndPolicy=endPolicy;OverduePolicy=overduePolicy;
        ProjectionPolicy=projectionPolicy;UnadvancedPolicy=unadvancedPolicy;DeterminationPolicy=determinationPolicy;Rounding=rounding;
    }
}
