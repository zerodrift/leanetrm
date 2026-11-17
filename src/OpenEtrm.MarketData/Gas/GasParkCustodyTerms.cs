using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

[EconomicDefinition("gas_park_custody_terms_v1")]
public sealed record GasParkCustodyTerms
{
    public const string Version="physical_gas_park_custody_v1";
    public const string RetainTitle="customer_title_retained_v1";
    public const string SameBasis="same_point_same_basis_partial_custody_returns_v1";
    public const string ReturnsAfterAcceptance="accepted_custody_then_returns_v1";
    public const string NondecreasingReturns="nondecreasing_accepted_custody_return_instants_v1";
    public const string NoUse="no_use_or_sale_of_custody_v1";
    public const string LossUnqualified="unqualified_loss_requires_reconciliation_v1";
    public const string SeparatePhysical="custody_only_no_inferred_flow_v1";
    public const string UnacceptedPlan="unaccepted_custody_plan_from_later_of_earliest_or_evaluation_v1";
    public const string FeeAtAcceptance="accepted_custody_v1";

    [IdentityField]public string Implementation{get;}
    [IdentityField]public string Service{get;}
    [IdentityField]public string Perspective{get;}
    [IdentityField]public string TitlePolicy{get;}
    [IdentityField]public string ReturnPolicy{get;}
    [IdentityField]public string ReturnTiming{get;}
    [IdentityField]public string ReturnOrder{get;}
    [IdentityField]public string UsePolicy{get;}
    [IdentityField]public string LossPolicy{get;}
    [IdentityField]public string PhysicalPolicy{get;}
    [IdentityField]public string PlanPolicy{get;}
    [IdentityField]public ReferenceKey Point{get;}
    [IdentityField]public ReferenceKey Quantity{get;}
    [IdentityField]public ReferenceKey Currency{get;}
    [IdentityField]public DateTimeOffset ParkAt{get;}
    [IdentityField]public DateTimeOffset ReturnAt{get;}
    public Money FixedFee{get;}
    [IdentityField]public ExactDecimal FixedFeeAmount=>FixedFee.Amount;
    [IdentityField]public ExactDecimal FeeDirection{get;}
    [IdentityField]public DateTimeOffset? FeeDueAt{get;}
    [IdentityField]public string FeeRecognition{get;}
    public Money SignedFixedFee=>FixedFee.Scale(FeeDirection);

    public GasParkCustodyTerms(string implementation,string service,string perspective,string titlePolicy,string returnPolicy,
        string returnTiming,string returnOrder,string usePolicy,string lossPolicy,string physicalPolicy,string planPolicy,ReferenceKey point,
        ReferenceKey quantity,ReferenceKey currency,DateTimeOffset parkAt,DateTimeOffset returnAt,Money fixedFee,
        ExactDecimal feeDirection,DateTimeOffset? feeDueAt,string feeRecognition)
    {
        foreach(var key in new[]{point,quantity,currency})Money.RequireKey(key);
        if(implementation!=Version||service!="park"||perspective is not("customer" or "provider")||titlePolicy!=RetainTitle||
            returnPolicy!=SameBasis||returnTiming!=ReturnsAfterAcceptance||returnOrder!=NondecreasingReturns||usePolicy!=NoUse||lossPolicy!=LossUnqualified||
            physicalPolicy!=SeparatePhysical||planPolicy!=UnacceptedPlan||feeRecognition!=FeeAtAcceptance||
            point.Revision!=quantity.Revision||quantity.Revision!=currency.Revision||!TradingInstant.IsSupported(parkAt)||
            !TradingInstant.IsSupported(returnAt)||returnAt<=parkAt||fixedFee is null||fixedFee.Currency!=currency||fixedFee.Amount.CompareTo(default)<0||
            feeDirection!=ExactDecimal.Parse("1")&&feeDirection!=ExactDecimal.Parse("-1")||
            (fixedFee.Amount==default)!=(feeDueAt is null)||feeDueAt is {} due&&(!TradingInstant.IsSupported(due)||due<parkAt))
            throw new MarketDataException("gas_custody_terms","Select the complete retained-title Park custody profile, exact reference bundle, independent fixed fee and explicit operating, loss-capability, timing and valuation policies.");
        if(fixedFee.Amount.CompareTo(ExactDecimal.Parse("100000000000000000000"))>=0||feeDueAt is {} cashAt&&(cashAt.Year<1901||cashAt.Year>2199))
            throw new MarketDataException("gas_custody_cash_capacity","The selected ordinary cash membership requires an absolute fee below 10^20 and a due instant in 1901 through 2199.");
        Implementation=implementation;Service=service;Perspective=perspective;TitlePolicy=titlePolicy;ReturnPolicy=returnPolicy;
        ReturnTiming=returnTiming;ReturnOrder=returnOrder;UsePolicy=usePolicy;LossPolicy=lossPolicy;PhysicalPolicy=physicalPolicy;PlanPolicy=planPolicy;
        Point=point;Quantity=quantity;Currency=currency;ParkAt=parkAt;ReturnAt=returnAt;FixedFee=fixedFee;
        FeeDirection=feeDirection;FeeDueAt=feeDueAt;FeeRecognition=feeRecognition;
    }
}
