using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Rates;

// The service supplies this exact version after resolving the configured currency
// default under its authorized effective/knowledge context. This type does not
// select a current mapping or confer authority to read a curve.
public sealed record CurrencyDiscountCurve(Guid Id, ReferenceKey Currency,
    ZeroRateDefinition Definition, string InterpolationPolicy);

public sealed record PaymentDiscount
{
    public bool Enabled { get; }
    public ExactDecimal Factor { get; }
    public Guid? MappingId { get; }
    public CurveKey? Curve { get; }
    public ReferenceKey Currency { get; }
    public ZeroRateDefinition? Definition { get; }
    public DateOnly PaymentDate { get; }
    public MarketSnapshot Snapshot { get; }
    public ImmutableArray<ZeroRateValue> Pillars { get; }
    public ImmutableArray<ObservationKey> Observations => Pillars.Select(p => p.Observation).ToImmutableArray();
    public string? RateNumericalPolicy { get; }
    public string? InterpolationPolicy { get; }

    private PaymentDiscount(bool enabled, ExactDecimal factor, ReferenceKey currency, CurrencyDiscountCurve? mapping,
        DateOnly paymentDate, MarketSnapshot snapshot, ImmutableArray<ZeroRateValue> pillars)
    {
        Enabled = enabled; Factor = factor; MappingId = mapping?.Id; Curve = mapping?.Definition.Key;
        Currency = currency; Definition = mapping?.Definition;
        PaymentDate = paymentDate; Snapshot = snapshot; Pillars = pillars;
        RateNumericalPolicy = enabled ? ZeroRateCurve.Policy : null;
        InterpolationPolicy = mapping?.InterpolationPolicy;
    }

    public static PaymentDiscount Resolve(bool discounting, ReferenceKey currency, DateOnly paymentDate,
        MarketSnapshot snapshot, CurrencyDiscountCurve? currencyDefault) =>
        new Resolver(discounting, snapshot, currencyDefault).Resolve(currency, paymentDate);

    // One valuation owns this resolver. Full curve validation is retained, while
    // every payment uses the same exact immutable snapshot and mapping.
    internal sealed class Resolver(bool discounting, MarketSnapshot snapshot, CurrencyDiscountCurve? currencyDefault)
    {
        private ZeroRateBuild? rates;
        private ImmutableArray<DateOnly> maturities;
        private readonly Dictionary<DateOnly, PaymentDiscount> resolved = [];

        internal PaymentDiscount Resolve(ReferenceKey currency, DateOnly paymentDate)
        {
            if (snapshot is null || currency.Revision.CatalogId == Guid.Empty || currency.Revision.RevisionId == Guid.Empty ||
                string.IsNullOrWhiteSpace(currency.Code) || snapshot.Id == Guid.Empty ||
                snapshot.Definitions.CatalogId == Guid.Empty || snapshot.Definitions.RevisionId == Guid.Empty ||
                snapshot.ValuationAt.Offset != TimeSpan.Zero || snapshot.MarketCutoff.Offset != TimeSpan.Zero ||
                snapshot.MarketCutoff > snapshot.ValuationAt || snapshot.Selections.IsDefault ||
                snapshot.SelectionPolicy != InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering ||
                snapshot.ValuationAt.Year is < 1901 or > 2199 || paymentDate.Year is < 1901 or > 2199 ||
                paymentDate <= DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime))
                throw new MarketDataException("discount_context", "An exact supported currency/context and future UTC payment date are required.");
            if (!discounting) return new(false, ExactDecimal.Parse("1"), currency, null, paymentDate, snapshot, []);
            if (currencyDefault is null)
                throw new MarketDataException("discount_curve_missing", "The selected currency has no configured default discount curve in this context.");
            var mapping = currencyDefault;
            if (mapping.Id == Guid.Empty || mapping.Definition is null || mapping.Currency != currency ||
                mapping.Definition.Currency != currency || mapping.InterpolationPolicy != DiscountInterpolation.Policy)
                throw new MarketDataException("discount_curve", "The exact configured currency, curve definition and interpolation policy must agree.");
            if (resolved.TryGetValue(paymentDate, out var cached)) return cached;
            if (rates is null)
            {
                rates = new ZeroRateCurve(mapping.Definition).Build(snapshot);
                maturities = rates.Values.Select(v => v.Maturity).ToImmutableArray();
            }
            var index = maturities.BinarySearch(paymentDate);
            PaymentDiscount value;
            if (index >= 0)
            {
                var exact = rates.Values[index];
                value = new(true, ExactDecimal.Parse(exact.DiscountFactor), currency, mapping, paymentDate, snapshot, [exact]);
            }
            else
            {
                var rightIndex = ~index;
                if (rightIndex == 0 || rightIndex == rates.Values.Length)
                    throw new MarketDataException("discount_coverage", "Payment must be at a stored pillar or bracketed by two pillars; extrapolation is unsupported.");
                var left = rates.Values[rightIndex - 1]; var right = rates.Values[rightIndex];
                var factor = DiscountInterpolation.Factor(left.Maturity, ExactDecimal.Parse(left.DiscountFactor),
                    right.Maturity, ExactDecimal.Parse(right.DiscountFactor), paymentDate, mapping.InterpolationPolicy);
                value = new(true, factor, currency, mapping, paymentDate, snapshot, [left, right]);
            }
            resolved.Add(paymentDate, value);
            return value;
        }
    }
}


// Immutable, run-local inputs prepared from each side's own payment schedule.
// A side requests only its factors; no instrument net payment is manufactured.
public sealed class CurrencyDefaultDiscounting : ISideDiscounting
{
    public MarketSnapshot Snapshot { get; }
    public ImmutableDictionary<SidePaymentKey, PaymentDiscount> Inputs { get; }
    public CurrencyDefaultDiscounting(IEnumerable<SidePaymentSchedule> schedules, MarketSnapshot snapshot,
        bool discounting, CurrencyDiscountCurve? currencyDefault)
    {
        if (schedules is null || snapshot is null)
            throw new MarketDataException("discount_context", "Explicit side schedules and a selected market snapshot are required.");
        var plans = schedules.ToImmutableArray();
        if (plans.IsDefaultOrEmpty || plans.Any(p => p is null))
            throw new MarketDataException("discount_context", "At least one valid side schedule is required.");
        Snapshot = snapshot;
        var selected = ImmutableDictionary.CreateBuilder<SidePaymentKey, PaymentDiscount>();
        var resolver = new PaymentDiscount.Resolver(discounting, snapshot, currencyDefault);
        foreach (var schedule in plans)
        foreach (var payment in schedule.Payments)
        {
            if (!selected.TryAdd(payment.Key, resolver.Resolve(payment.Currency, payment.PaymentDate)))
                throw new MarketDataException("side_payment_factors", "Side payment identities must be distinct in this discount context.");
        }
        Inputs = selected.ToImmutable();
    }
    public ImmutableArray<SidePaymentFactor> Resolve(SidePaymentSchedule schedule, MarketSnapshot snapshot)
    {
        if (!Snapshot.HasSameInputs(snapshot))
            throw new MarketDataException("discount_context", "Discount inputs must retain the complete selected side valuation context.");
        return Inputs.Where(p => p.Key.SideId == schedule.SideId).Select(p => new SidePaymentFactor(p.Key, p.Value.Factor,
            Snapshot, p.Value.Enabled ? DiscountFactorSource.CurrencyDefault : DiscountFactorSource.Undiscounted)).ToImmutableArray();
    }
}

public sealed record DiscountedSidePayment
{
    public SidePaymentValue Cashflow { get; }
    public PaymentDiscount Discount { get; }
    internal DiscountedSidePayment(SidePaymentValue cashflow, PaymentDiscount discount, MarketSnapshot snapshot)
    {
        if (cashflow.Discount.DiscountFactor != discount.Factor || !snapshot.HasSameInputs(discount.Snapshot) ||
            discount.Currency != cashflow.Plan.Currency ||
            discount.PaymentDate != cashflow.Plan.PaymentDate ||
            cashflow.Discount.Source != (discount.Enabled ? DiscountFactorSource.CurrencyDefault : DiscountFactorSource.Undiscounted))
            throw new MarketDataException("schedule_discount", "Discount evidence must match the side payment's factor, date, currency and full context.");
        Cashflow = cashflow; Discount = discount;
    }
}
public sealed record DiscountedSideValuation
{
    public SideContribution Contribution { get; }
    public ImmutableArray<DiscountedSidePayment> Payments { get; }
    internal DiscountedSideValuation(SideContribution contribution, ImmutableArray<DiscountedSidePayment> payments)
    { Contribution = contribution; Payments = payments; }
}
public sealed record FinancialSwapValue
{
    public const string Policy = "financial_swap_trade_scale_exact_18_v1";
    public string NumericalPolicy => Policy;
    public InstrumentValuation Instrument { get; }
    public Quantity Quantity { get; }
    public ImmutableArray<DiscountedSideValuation> Sides { get; }
    public Money PresentValue { get; }
    internal FinancialSwapValue(InstrumentValuation instrument, Quantity quantity, ImmutableArray<DiscountedSideValuation> sides)
    {
        if (quantity is null || quantity.Specification != instrument.UnitQuantity.Specification || quantity.Amount.CompareTo(default) <= 0)
            throw new MarketDataException("swap_quantity", "Supply a positive trade quantity in the exact financial swap unit.");
        Instrument = instrument; Quantity = quantity; Sides = sides;
        PresentValue = instrument.PresentValue.Scale(quantity.Amount).ToMoney();
    }
}

// Immutable per-instrument valuation. Trade sizes and books never enter this
// object; only final Scale converts exact side money into working Money.
public sealed record FinancialSwapEvaluation
{
    public InstrumentValuation Instrument { get; }
    public ImmutableArray<DiscountedSideValuation> Sides { get; }
    internal FinancialSwapEvaluation(InstrumentValuation instrument,ImmutableArray<DiscountedSideValuation> sides){Instrument=instrument;Sides=sides;}
    public FinancialSwapValue Scale(Quantity quantity)=>new(Instrument,quantity,Sides);
}

public static class FinancialSwapValuation
{
    public static FinancialSwapEvaluation Evaluate(FinancialSwapInstrument instrument,CurveBuild market,bool discounting,CurrencyDiscountCurve? currencyDefault) =>
        Evaluate(instrument,market,discounting,currencyDefault,instrument?.Discounting);
    public static FinancialSwapEvaluation Evaluate(MonthlyFinancialSwap instrument,CurveBuild market,bool discounting,CurrencyDiscountCurve? currencyDefault) =>
        Evaluate(instrument,market,discounting,currencyDefault,instrument?.Discounting);
    private static FinancialSwapEvaluation Evaluate(SidedInstrument? instrument,CurveBuild market,bool discounting,
        CurrencyDiscountCurve? currencyDefault,bool? declaredDiscounting)
    {
        if(instrument is null||market is null)throw new MarketDataException("swap_context","An explicit instrument and built market scenario are required.");
        if(discounting!=declaredDiscounting)throw new MarketDataException("swap_valuation","Discounting must match the immutable instrument definition.");
        var inputs=new CurrencyDefaultDiscounting(instrument.Sides.Select(m=>m.Side.GenerateSchedule()),market.Snapshot,discounting,currencyDefault);
        var value=instrument.PV(new SideValuationContext(market,inputs));
        return new(value,value.Sides.Select(s=>new DiscountedSideValuation(s,s.Value.Payments.Select(p=>new DiscountedSidePayment(p,inputs.Inputs[p.Plan.Key],value.Snapshot)).ToImmutableArray())).ToImmutableArray());
    }
    public static FinancialSwapValue Price(FinancialSwapInstrument instrument,CurveBuild market,Quantity quantity,bool discounting,CurrencyDiscountCurve? currencyDefault) =>
        Price(instrument,market,quantity,discounting,currencyDefault,instrument?.Discounting);
    public static FinancialSwapValue Price(MonthlyFinancialSwap instrument,CurveBuild market,Quantity quantity,bool discounting,CurrencyDiscountCurve? currencyDefault) =>
        Price(instrument,market,quantity,discounting,currencyDefault,instrument?.Discounting);
    private static FinancialSwapValue Price(SidedInstrument? instrument,CurveBuild market,Quantity quantity,bool discounting,
        CurrencyDiscountCurve? currencyDefault,bool? declaredDiscounting)
    {
        if(instrument is null||quantity is null||(instrument.UnitQuantity is not {} basis||quantity.Specification!=basis.Specification)||quantity.Amount.CompareTo(default)<=0)
            throw new MarketDataException("swap_quantity","Supply a positive trade quantity in the exact financial swap unit.");
        return Evaluate(instrument,market,discounting,currencyDefault,declaredDiscounting).Scale(quantity);
    }
}
