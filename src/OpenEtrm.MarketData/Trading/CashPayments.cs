using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// A unit cash instrument: one unit of an exact currency paid on a date. A trade holds it at a signed quantity in that
// currency, negative for cash it pays. Its identity is its terms, so equal cash on the same date is one instrument.
// Its value excludes settled cash: before the payment date a unit is worth the discount factor its own selected
// discounting supplies; from the payment date on the cash is settled and the instrument contributes nothing further.
// It reads no market price, so a price shock leaves it unchanged. An option premium paid up front is one.
[EconomicDefinition("CashPayment_v1")]
public sealed record CashPayment : Instrument
{
    public const string ModelVersion = "cash_payment_excluding_settled_v1";
    [IdentityField]
    public ReferenceKey PaymentCurrency { get; }
    [IdentityField]
    public DateOnly PaymentDate { get; }
    public override ReferenceKey Currency => PaymentCurrency;
    // The quantity unit is the currency itself.
    public override Quantity UnitQuantity => new(ExactDecimal.Parse("1"), PaymentCurrency);
    public override bool SupportsQuantityScaling => true;
    // The cash is due, and from then settled, from the start of its UTC payment date.
    public SidePaymentKey Payment => new(Id, 0, new(PaymentDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), Currency);
    public bool Settled(DateTimeOffset at) => at >= Payment.PaymentAt;

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

    public override UnitPricingContract Declare(UnitValuationContext context) => new(Id, ModelVersion, [], PaymentCurrency, Currency);

    public override UnitValuation PV(UnitValuationContext context)
    {
        if (context.Measure != ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure", "A unit cash instrument implements present value only.");
        var zero = ExactMoney.FromMoney(new Money(default, Currency));
        if (Settled(context.Snapshot.ValuationAt)) return new(context, zero, PaymentCurrency);
        var factors = context.DiscountProvider.Resolve([Payment], context.Snapshot);
        if (factors.IsDefault || factors.Length != 1 || factors[0] is null || factors[0].Payment != Payment)
            throw new MarketDataException("cash_payment_discount", "The selected discounting supplies no factor for this payment.");
        return new(context, ExactMoney.FromMoney(new Money(ExactDecimal.Parse("1"), Currency)).Scale(factors[0].DiscountFactor), PaymentCurrency);
    }
}
