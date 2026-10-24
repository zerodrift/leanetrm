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
        MarketSnapshot snapshot, CurrencyDiscountCurve? currencyDefault)
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
        var rates = new ZeroRateCurve(mapping.Definition).Build(snapshot);
        var exact = rates.Values.SingleOrDefault(v => v.Maturity == paymentDate);
        if (exact is not null)
            return new(true, ExactDecimal.Parse(exact.DiscountFactor), currency, mapping, paymentDate, snapshot, [exact]);
        var left = rates.Values.LastOrDefault(v => v.Maturity < paymentDate);
        var right = rates.Values.FirstOrDefault(v => v.Maturity > paymentDate);
        if (left is null || right is null)
            throw new MarketDataException("discount_coverage", "Payment must be at a stored pillar or bracketed by two pillars; extrapolation is unsupported.");
        var factor = DiscountInterpolation.Factor(left.Maturity, ExactDecimal.Parse(left.DiscountFactor),
            right.Maturity, ExactDecimal.Parse(right.DiscountFactor), paymentDate, mapping.InterpolationPolicy);
        return new(true, factor, currency, mapping, paymentDate, snapshot, [left, right]);
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
        foreach (var schedule in plans)
        foreach (var payment in schedule.Payments)
        {
            if (!selected.TryAdd(payment.Key, PaymentDiscount.Resolve(discounting, payment.Currency,
                payment.PaymentDate, snapshot, currencyDefault)))
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
    public const string ParRatePolicy="par_fixed_price_to_even_v1";
    public sealed record ParSwapRate(UnitPrice Price,string Numerator,string Denominator,string Policy,int DecimalPlaces,CashTieRule TieRule);
    public static ParSwapRate ParRate(FinancialSwapInstrument instrument,CurveBuild market,CurrencyDiscountCurve? currencyDefault,AmountFormat rounding)
    {
        if(instrument?.First is not FixedSide fixedSide||rounding is null||rounding.TieRule!=CashTieRule.ToEven)
            throw new MarketDataException("swap_par_rate","Par fixed pricing requires a fixed/floating swap and explicit decimal precision with ties to even.");
        var value=Evaluate(instrument,market,instrument.Discounting,currencyDefault);
        var coefficient=ExactMoney.FromMoney(new UnitPrice(ExactDecimal.Parse("1"),fixedSide.Price.Unit).Times(fixedSide.UnitQuantity))
            .Scale(value.Sides[0].Payments.Single().Discount.Factor);
        var floating=value.Sides[1].Contribution.Value.PresentValue;
        var numerator=System.Numerics.BigInteger.Parse(floating.Numerator,System.Globalization.CultureInfo.InvariantCulture)*System.Numerics.BigInteger.Parse(coefficient.Denominator,System.Globalization.CultureInfo.InvariantCulture);
        var denominator=System.Numerics.BigInteger.Parse(floating.Denominator,System.Globalization.CultureInfo.InvariantCulture)*System.Numerics.BigInteger.Parse(coefficient.Numerator,System.Globalization.CultureInfo.InvariantCulture);
        var gcd=System.Numerics.BigInteger.GreatestCommonDivisor(System.Numerics.BigInteger.Abs(numerator),denominator);numerator/=gcd;denominator/=gcd;
        var price=new UnitPrice(ExactDecimal.Parse(new AmountFormat(rounding.DecimalPlaces,rounding.TieRule,false,true).FormatFraction(numerator,denominator)),fixedSide.Price.Unit);
        return new(price,numerator.ToString(System.Globalization.CultureInfo.InvariantCulture),denominator.ToString(System.Globalization.CultureInfo.InvariantCulture),ParRatePolicy,rounding.DecimalPlaces,rounding.TieRule);
    }
    public static FinancialSwapEvaluation Evaluate(FinancialSwapInstrument instrument,CurveBuild market,bool discounting,CurrencyDiscountCurve? currencyDefault)
    {
        if(instrument is null||market is null)throw new MarketDataException("swap_context","An explicit instrument and built market scenario are required.");
        if(discounting!=instrument.Discounting)throw new MarketDataException("swap_valuation","Discounting must match the immutable instrument definition.");
        var sides=instrument.Sides.Select(m=>m.Side).ToArray();
        if(sides.GroupBy(s=>s.Id).Any(group=>group.Select(DefinitionIdentity.For).Distinct().Count()!=1||
            group.OfType<FloatingSide>().Select(s=>DefinitionIdentity.For(s.PricingBinding)).Distinct().Count()>1))
            throw new MarketDataException("side_definition","One side identity cannot resolve to conflicting economic terms or pricing bindings.");
        var inputs=new CurrencyDefaultDiscounting(sides.DistinctBy(s=>s.Id).Select(s=>s.GenerateSchedule()),market.Snapshot,discounting,currencyDefault);
        var value=instrument.PV(new SideValuationContext(market,inputs));
        return new(value,value.Sides.Select(s=>new DiscountedSideValuation(s,s.Value.Payments.Select(p=>new DiscountedSidePayment(p,inputs.Inputs[p.Plan.Key],value.Snapshot)).ToImmutableArray())).ToImmutableArray());
    }
    public static FinancialSwapValue Price(FinancialSwapInstrument instrument,CurveBuild market,Quantity quantity,bool discounting,CurrencyDiscountCurve? currencyDefault)
    {
        if(instrument is null||quantity is null||(instrument.UnitQuantity is not {} basis||quantity.Specification!=basis.Specification)||quantity.Amount.CompareTo(default)<=0)
            throw new MarketDataException("swap_quantity","Supply a positive trade quantity in the exact financial swap unit.");
        return Evaluate(instrument,market,discounting,currencyDefault).Scale(quantity);
    }
}
