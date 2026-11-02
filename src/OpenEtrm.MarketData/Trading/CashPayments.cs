using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// A fixed cash amount per unit of an explicit quantity, paid (negative) or received (positive) on one date. Its value is
// the amount discounted from that date by the instrument's own selected discount inputs; it reads no market price, so a
// price shock leaves it unchanged. An option premium paid up front is one, per option contract.
[EconomicDefinition("FixedCashPayment_v1")]
public sealed record FixedCashPayment : Instrument
{
    public const string ModelVersion = "fixed_cash_payment_discounted_v1";
    [IdentityField]
    public ReferenceKey PaymentCurrency { get; }
    [IdentityField]
    public DateOnly PaymentDate { get; }
    [IdentityField]
    public ExactDecimal UnitAmount { get; }
    [IdentityField]
    public ReferenceKey Quantity { get; }
    public override ReferenceKey Currency => PaymentCurrency;
    public override Quantity UnitQuantity => new(ExactDecimal.Parse("1"), Quantity);
    public override bool SupportsQuantityScaling => true;
    // The payment is due from the start of its UTC date.
    public SidePaymentKey Payment => new(Id, 0, new(PaymentDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), Currency);

    public FixedCashPayment(Guid id, ReferenceKey currency, DateOnly paymentDate, ExactDecimal unitAmount, ReferenceKey quantity) : base(id)
    {
        if (paymentDate.Year is < 1901 or > 2199 || new[] { currency, quantity }.Any(k => k.Revision.CatalogId == Guid.Empty ||
                k.Revision.RevisionId == Guid.Empty || string.IsNullOrWhiteSpace(k.Code)))
            throw new MarketDataException("cash_payment_definition", "A fixed cash payment needs an exact currency, quantity and supported payment date.");
        PaymentCurrency = currency; PaymentDate = paymentDate; UnitAmount = unitAmount; Quantity = quantity;
    }

    public override void ValidateBinding(Quantity quantity, DeliveryCoverage? coverage)
    {
        base.ValidateBinding(quantity, coverage);
        if (quantity.Specification != Quantity || quantity.Amount.CompareTo(default) <= 0)
            throw new MarketDataException("cash_payment_quantity", "Hold a positive quantity in the payment's own unit.");
    }

    public override UnitPricingContract Declare(UnitValuationContext context) => new(Id, ModelVersion, [], Quantity, Currency);

    public override UnitValuation PV(UnitValuationContext context)
    {
        if (context.Measure != ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure", "A fixed cash payment implements present value only.");
        var factors = context.DiscountProvider.Resolve([Payment], context.Snapshot);
        if (factors.IsDefault || factors.Length != 1 || factors[0] is null || factors[0].Payment != Payment)
            throw new MarketDataException("cash_payment_discount", "The selected discounting supplies no factor for this payment.");
        return new(context, ExactMoney.FromMoney(new Money(UnitAmount, Currency)).Scale(factors[0].DiscountFactor), Quantity);
    }
}
