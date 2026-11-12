using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// A unit cash instrument: one unit of an exact currency paid on a date. A trade holds it at a signed quantity in that
// currency, negative for cash it pays. Its identity is its terms, so equal cash on the same date is one instrument.
// Its value excludes recorded paid cash. Before the payment date, outstanding cash uses its selected
// discounting; due but unpaid cash remains at nominal value under the explicit paid-cash policy.
// It reads no market price, so a price shock leaves it unchanged. An option premium paid up front is one.
[EconomicDefinition("CashPayment_v1")]
public sealed record CashPayment : Instrument
{
    public const string ModelVersion = "cash_payment_explicit_paid_state_v2";
    [IdentityField]
    public ReferenceKey PaymentCurrency { get; }
    [IdentityField]
    public DateOnly PaymentDate { get; }
    public override ReferenceKey Currency => PaymentCurrency;
    // The quantity unit is the currency itself.
    public override Quantity UnitQuantity => new(ExactDecimal.Parse("1"), PaymentCurrency);
    public override bool SupportsQuantityScaling => true;
    // The cash is due from the start of its UTC payment date. Payment remains a separate recorded fact.
    public SidePaymentKey Payment => new(Id, 0, new(PaymentDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), Currency);
    public bool Due(DateTimeOffset at) => at >= Payment.PaymentAt;

    public CashPayment(ReferenceKey currency, DateOnly paymentDate) : base(ContractIdentity.CashPayment(currency, paymentDate))
    {
        if (paymentDate.Year is < 1901 or > 2199)
            throw new MarketDataException("cash_payment_definition", "A unit cash instrument needs a supported payment date.");
        PaymentCurrency = currency; PaymentDate = paymentDate;
    }

    public override void ValidateBinding(Quantity quantity, DeliveryCoverage? coverage)
    {
        base.ValidateBinding(quantity, coverage);
        if (quantity.Specification != PaymentCurrency)
            throw new MarketDataException("cash_payment_quantity", "Hold cash as a quantity of its own currency.");
    }

    public override void ValidateValuationState(IBindingValuationState? state)
    {
        if(state is not RecordedCashValuation cash||cash.InstrumentId!=Id||cash.Quantity.Specification!=Currency)
            throw new MarketDataException("payment_state","A cash instrument requires its exact recorded paid-state projection.");
    }

    public override UnitPricingContract Declare(UnitValuationContext context)
    {
        ValidateValuationState(context.BindingState);
        var cash=(RecordedCashValuation)context.BindingState!;
        var needsDiscount=cash.UnitOutstanding(context.Snapshot).Numerator!="0"&&!Due(context.Snapshot.ValuationAt);
        var events=cash.KnownFlows(context.Snapshot).Where(f=>f.EffectiveAt>context.Snapshot.ValuationAt).Select(f=>f.EffectiveAt).ToList();
        if(needsDiscount)events.Add(Payment.PaymentAt);
        return new(Id,ModelVersion,[],PaymentCurrency,Currency){
            Time=events.Count>0?ValuationTimeDependency.Between(context.Snapshot.ValuationAt,events.Min()):ValuationTimeDependency.Independent,
            DiscountInputs=needsDiscount?[new(Payment,context.PaymentDiscounting(Id))]:[]};
    }

    public override UnitValuation PV(UnitValuationContext context)
    {
        if (context.Measure != ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure", "A unit cash instrument implements present value only.");
        ValidateValuationState(context.BindingState);
        var remaining=((RecordedCashValuation)context.BindingState!).UnitOutstanding(context.Snapshot);
        if(remaining.Numerator=="0"||Due(context.Snapshot.ValuationAt))return new(context,remaining,PaymentCurrency);
        var factors = context.Inputs.Discounts([Payment]);
        if (factors.IsDefault || factors.Length != 1 || factors[0] is null || factors[0].Payment != Payment)
            throw new MarketDataException("cash_payment_discount", "The selected discounting supplies no factor for this payment.");
        return new(context, remaining.Scale(factors[0].DiscountFactor), PaymentCurrency);
    }
}
