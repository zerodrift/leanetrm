using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

public interface ICashPaymentValuation:IBindingValuationState
{
    Guid InstrumentId{get;}
    ExactMoney UnitOutstanding(MarketSnapshot source);
    ExactMoney UnitOutstanding(MarketSnapshot source,DateTimeOffset calculationAt);
    ImmutableArray<DateTimeOffset> FutureChanges(MarketSnapshot source);
}

// Shared paid-state and valuation rules for dated premiums and exact-instant
// margin/settlement cash. Ownership and payment history remain binding state.
public abstract record CashPaymentInstrument:Instrument
{
    public const string ModelVersion="cash_payment_explicit_paid_state_v2";
    public abstract ReferenceKey PaymentCurrency{get;}
    public abstract DateTimeOffset PaymentAt{get;}
    public override ReferenceKey Currency=>PaymentCurrency;
    public override Quantity UnitQuantity=>new(ExactDecimal.Parse("1"),PaymentCurrency);
    public override bool SupportsQuantityScaling=>true;
    public SidePaymentKey Payment=>new(Id,0,PaymentAt,Currency);
    public bool Due(DateTimeOffset at)=>at>=PaymentAt;
    protected CashPaymentInstrument(Guid id):base(id){}

    public override void ValidateBinding(Quantity quantity, DeliveryCoverage? coverage)
    {
        base.ValidateBinding(quantity, coverage);
        if (quantity.Specification != PaymentCurrency)
            throw new MarketDataException("cash_payment_quantity", "Hold cash as a quantity of its own currency.");
    }

    public override void ValidateValuationState(IBindingValuationState? state)
    {
        if(state is not ICashPaymentValuation cash||cash.InstrumentId!=Id||cash.Quantity.Specification!=Currency)
            throw new MarketDataException("payment_state","A cash instrument requires its exact recorded paid-state projection.");
    }

    public override UnitPricingContract Declare(UnitValuationContext context)
    {
        ValidateValuationState(context.BindingState);
        var needsDiscount=Outstanding(context).Numerator!="0"&&!Due(context.CalculationAt);
        var events=((ICashPaymentValuation)context.BindingState!).FutureChanges(context.Snapshot).Where(at=>at>context.CalculationAt).ToList();
        if(needsDiscount)events.Add(Payment.PaymentAt);
        return new(Id,ModelVersion,[],PaymentCurrency,Currency){
            Time=events.Count>0?ValuationTimeDependency.Between(context.CalculationAt,events.Min()):ValuationTimeDependency.Independent,
            DiscountInputs=needsDiscount?[new(Payment,context.PaymentDiscounting(Id))]:[]};
    }

    public override UnitValuation PV(UnitValuationContext context)
    {
        if (context.Measure != ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure", "A unit cash instrument implements present value only.");
        ValidateValuationState(context.BindingState);
        var remaining=Outstanding(context);
        if(remaining.Numerator=="0"||Due(context.CalculationAt))return new(context,remaining,PaymentCurrency);
        var factors = context.Inputs.Discounts([Payment]);
        if (factors.IsDefault || factors.Length != 1 || factors[0] is null || factors[0].Payment != Payment)
            throw new MarketDataException("cash_payment_discount", "The selected discounting supplies no factor for this payment.");
        return new(context, remaining.Scale(factors[0].DiscountFactor), PaymentCurrency);
    }
    private ExactMoney Outstanding(UnitValuationContext context)
    {
        if(context.BindingState is ProjectedCashValuation&&!context.IsLifecycleProjection)
            throw new MarketDataException("projection_cash_state","A forecast cash obligation cannot be valued as an actual holding.");
        return ((ICashPaymentValuation)context.BindingState!).UnitOutstanding(context.Snapshot,context.CalculationAt);
    }
}

// Date-only cash retains its original identity and midnight-UTC convention.
[EconomicDefinition("CashPayment_v1")]
public sealed record CashPayment:CashPaymentInstrument
{
    [IdentityField] public override ReferenceKey PaymentCurrency{get;}
    [IdentityField] public DateOnly PaymentDate{get;}
    public override DateTimeOffset PaymentAt=>new(PaymentDate.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero);
    public CashPayment(ReferenceKey currency,DateOnly paymentDate):base(ContractIdentity.CashPayment(currency,paymentDate))
    {
        if(paymentDate.Year is <1901 or >2199)
            throw new MarketDataException("cash_payment_definition","A unit cash instrument needs a supported payment date.");
        PaymentCurrency=currency;PaymentDate=paymentDate;
    }
}

[EconomicDefinition("TimedCashPayment_v1")]
public sealed record TimedCashPayment:CashPaymentInstrument
{
    [IdentityField] public override ReferenceKey PaymentCurrency{get;}
    [IdentityField] public override DateTimeOffset PaymentAt{get;}
    public TimedCashPayment(ReferenceKey currency,DateTimeOffset paymentAt):base(Identity(currency,paymentAt))
    {
        PaymentCurrency=currency;PaymentAt=paymentAt;
    }
    static Guid Identity(ReferenceKey currency,DateTimeOffset at)
    {
        Money.RequireKey(currency);
        if(!TradingInstant.IsSupported(at)||at.Year is <1901 or >2199)
            throw new MarketDataException("cash_payment_definition","Supply an exact supported UTC cash due instant.");
        return ContractIdentity.FromCanonical($"timed_cash_payment_v1:{currency.Revision.CatalogId:D}:{currency.Revision.RevisionId:D}:{currency.Code}:{at:O}");
    }
}
