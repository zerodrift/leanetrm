using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Calculation;

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
        private RatesCurve? rates;
        private readonly Dictionary<DateOnly, PaymentDiscount> resolved = [];

        internal PaymentDiscount Resolve(ReferenceKey currency, DateOnly paymentDate)
            =>Resolve(currency,paymentDate,false);
        internal PaymentDiscount Resolve(SidePaymentKey payment)
        {
            payment.RequireUndue(snapshot);
            return Resolve(payment.Currency,DateOnly.FromDateTime(payment.PaymentAt.UtcDateTime),!discounting);
        }
        private PaymentDiscount Resolve(ReferenceKey currency, DateOnly paymentDate,bool allowSameDay)
        {
            if (snapshot is null || currency.Revision.CatalogId == Guid.Empty || currency.Revision.RevisionId == Guid.Empty ||
                string.IsNullOrWhiteSpace(currency.Code) || snapshot.Id == Guid.Empty ||
                snapshot.Definitions.CatalogId == Guid.Empty || snapshot.Definitions.RevisionId == Guid.Empty ||
                snapshot.ValuationAt.Offset != TimeSpan.Zero || snapshot.MarketCutoff.Offset != TimeSpan.Zero ||
                snapshot.MarketCutoff > snapshot.ValuationAt || snapshot.Selections.IsDefault ||
                snapshot.SelectionPolicy != InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering ||
                snapshot.ValuationAt.Year is < 1901 or > 2199 || paymentDate.Year is < 1901 or > 2199 ||
                paymentDate < DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime) ||
                !allowSameDay && paymentDate == DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime))
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
                rates = new(mapping.Definition,snapshot,mapping.InterpolationPolicy);
            }
            var factor=rates.Evaluate(paymentDate);
            var value=new PaymentDiscount(true,factor.Value,currency,mapping,paymentDate,snapshot,factor.Pillars);
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
    private readonly ImmutableDictionary<Guid, ImmutableArray<SidePaymentFactor>> factorsBySide;
    public CurrencyDefaultDiscounting(IEnumerable<SideSchedule> schedules,MarketSnapshot snapshot,
        bool discounting,CurrencyDiscountCurve? currencyDefault)
        : this(PaymentKeys(schedules,snapshot),snapshot,discounting,currencyDefault) { }

    // A physical value can use the same exact date/currency rate lookup without manufacturing a cash plan.
    // Empty keys explicitly request no rate factors, as for an all-held or all-overdue PPA scope.
    public CurrencyDefaultDiscounting(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot,
        bool discounting,CurrencyDiscountCurve? currencyDefault)
    {
        if(payments.IsDefault || snapshot is null)
            throw new MarketDataException("discount_context","Explicit side value keys and a selected market snapshot are required.");
        if(payments.Distinct().Count()!=payments.Length)
            throw new MarketDataException("side_payment_factors","Side value identities must be distinct in this discount context.");
        Snapshot=snapshot;
        foreach(var payment in payments)payment.RequireUndue(snapshot);
        var resolver=new PaymentDiscount.Resolver(discounting,snapshot,currencyDefault);
        Inputs=payments.ToImmutableDictionary(p=>p,p=>resolver.Resolve(p));
        factorsBySide=Inputs.GroupBy(p=>p.Key.SideId).ToImmutableDictionary(g=>g.Key,
            g=>g.Select(p=>Factor(p.Key,p.Value)).ToImmutableArray());
    }
    private static ImmutableArray<SidePaymentKey> PaymentKeys(IEnumerable<SideSchedule> schedules,MarketSnapshot snapshot)
    {
        if(schedules is null || snapshot is null)
            throw new MarketDataException("discount_context","Explicit side schedules and a selected market snapshot are required.");
        var plans=schedules.ToImmutableArray();
        if(plans.IsDefaultOrEmpty || plans.Any(p=>p is not SidePaymentSchedule))
            throw new MarketDataException("discount_context","Legacy cash discounting requires at least one exact payment schedule.");
        foreach(var schedule in plans.Cast<SidePaymentSchedule>())schedule.RequireUndue(snapshot);
        return plans.Cast<SidePaymentSchedule>().SelectMany(p=>p.Payments).Select(p=>p.Key).ToImmutableArray();
    }
    private SidePaymentFactor Factor(SidePaymentKey key,PaymentDiscount input)
    {
        var factor=new SidePaymentFactor(key,input.Factor,Snapshot,input.Enabled?DiscountFactorSource.CurrencyDefault:DiscountFactorSource.Undiscounted);
        if(!input.Enabled)return factor;
        var definition=input.Definition!;
        return factor with { RateDefinition=new(input.MappingId!.Value,definition.Key,definition.Specification,definition.Currency,definition.Source,
            DateOnly.FromDateTime(Snapshot.ValuationAt.UtcDateTime),input.PaymentDate,definition.DayCount,definition.Compounding,definition.Implementation,
            definition.DateBasis,input.RateNumericalPolicy!,input.InterpolationPolicy!,
            [..input.Pillars.Select(p=>new RateDiscountPillar(p.Maturity,Snapshot.Selections.Single(s=>s.Input==new InputKey(p.Series,InputUse.Variable) && s.Observation.Key==p.Observation).Observation))]) };
    }
    public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)
    {
        if(!Snapshot.HasSameInputs(snapshot))
            throw new MarketDataException("discount_context","Discount inputs must retain the complete selected side valuation context.");
        if(payments.IsDefault || payments.Distinct().Count()!=payments.Length || payments.Any(p=>!Inputs.ContainsKey(p)))
            throw new MarketDataException("side_payment_factors","Request distinct exact value keys prepared in this discount context.");
        return payments.Select(p=>Factor(p,Inputs[p])).ToImmutableArray();
    }
    public ImmutableArray<SidePaymentFactor> Resolve(SidePaymentSchedule schedule, MarketSnapshot snapshot)
    {
        if (!Snapshot.HasSameInputs(snapshot))
            throw new MarketDataException("discount_context", "Discount inputs must retain the complete selected side valuation context.");
        return factorsBySide.TryGetValue(schedule.SideId, out var factors) ? factors : [];
    }
}

public sealed record DiscountedSidePayment
{
    public ScheduleRowValue Cashflow { get; }
    public PaymentDiscount Discount { get; }
    internal DiscountedSidePayment(ScheduleRowValue cashflow, PaymentDiscount discount, MarketSnapshot snapshot)
    {
        if (cashflow.PaymentDiscount.DiscountFactor != discount.Factor || !snapshot.HasSameInputs(discount.Snapshot) ||
            discount.Currency != cashflow.Plan.Currency ||
            discount.PaymentDate != cashflow.Plan.PaymentDate ||
            cashflow.PaymentDiscount.Source != (discount.Enabled ? DiscountFactorSource.CurrencyDefault : DiscountFactorSource.Undiscounted))
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
    public const string ExactPolicy = "financial_swap_trade_scale_exact_rational_v1";
    public string NumericalPolicy => ExactPolicy;
    public InstrumentValuation Instrument { get; }
    public Quantity Quantity { get; }
    public ImmutableArray<DiscountedSideValuation> Sides { get; }
    public ExactMoney ExactPresentValue { get; }
    public Money PresentValue => ExactPresentValue.ToMoney();
    internal FinancialSwapValue(InstrumentValuation instrument, Quantity quantity, ImmutableArray<DiscountedSideValuation> sides)
    {
        if (quantity is null || quantity.Specification != instrument.UnitQuantity.Specification || quantity.Amount.CompareTo(default) <= 0)
            throw new MarketDataException("swap_quantity", "Supply a positive trade quantity in the exact financial swap unit.");
        Instrument = instrument; Quantity = quantity; Sides = sides;
        ExactPresentValue = instrument.PresentValue.Scale(quantity.Amount);
    }
}

// Immutable per-instrument valuation. Trade sizes and books never enter this
// object. Scale retains exact money; working-decimal projection is explicit.
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
        return SolveParRate(fixedSide,Evaluate(instrument,market,instrument.Discounting,currencyDefault),rounding);
    }
    public static ParSwapRate ParRate(FinancialSwapInstrument instrument,ConnectedValuation valuation,ImmutableDictionary<CurveRequest,CurvePointKey> bindings,CurveBuild authority,CurrencyDiscountCurve? currencyDefault,AmountFormat rounding,CancellationToken cancellation=default)
    {
        if(instrument?.First is not FixedSide fixedSide||rounding is null||rounding.TieRule!=CashTieRule.ToEven)
            throw new MarketDataException("swap_par_rate","Par fixed pricing requires a fixed/floating swap and explicit decimal precision with ties to even.");
        return SolveParRate(fixedSide,Evaluate(instrument,valuation,bindings,authority,currencyDefault,cancellation),rounding);
    }
    public static ParSwapRate ParRate(ScheduledFinancialSwap instrument,ImmutableArray<DiscountedSideValuation> sides,AmountFormat rounding)
    {
        if(instrument?.First is not FixedSide||sides.IsDefault||sides.Length!=2||rounding is null||rounding.TieRule!=CashTieRule.ToEven||
            sides.Where((s,n)=>s.Contribution.SideId!=instrument.Sides[n].Side.Id).Any())
            throw new MarketDataException("swap_par_rate","Scheduled par pricing requires the exact fixed/floating evaluation and explicit precision with ties to even.");
        var unit=((FixedSide)instrument.First).Price.Unit;
        return SolveScheduledPar(sides,unit,rounding);
    }
    public static ParSwapRate ParRate(FinancialSwapValue value,AmountFormat rounding)
    {
        if(value is null||rounding is null)throw new MarketDataException("swap_par_rate","An exact fixed/floating valuation and explicit precision are required.");
        var sides=value.Sides;
        if(sides.Length!=2||sides[0].Payments.IsEmpty||sides[0].Payments.SelectMany(p=>p.Cashflow.Prices).Any(p=>p.Term.FixedPrice is null)||rounding.TieRule!=CashTieRule.ToEven)
            throw new MarketDataException("swap_par_rate","A common par price requires an exact fixed/floating payment valuation and explicit precision.");
        var unit=sides[0].Payments[0].Cashflow.Prices[0].ExactPrice.Unit;
        return SolveScheduledPar(sides,unit,rounding);
    }
    private static ParSwapRate SolveScheduledPar(ImmutableArray<DiscountedSideValuation> sides,PriceUnit unit,AmountFormat rounding)
    {
        var annuity=sides[0].Payments.Select(p=>p.Cashflow.Plan.AllocatedQuantity.Times(new UnitPrice(ExactDecimal.Parse("1"),unit)).Scale(p.Discount.Factor)).Aggregate((a,b)=>a.Add(b)).Scale(sides[0].Contribution.QuantityRatio);
        return QuotientRate(sides[1].Contribution.Value.PresentValue.Scale(sides[1].Contribution.QuantityRatio),annuity,unit,rounding);
    }
    private static ParSwapRate QuotientRate(ExactMoney value,ExactMoney coefficient,PriceUnit unit,AmountFormat rounding)
    {
        var numerator=System.Numerics.BigInteger.Parse(value.Numerator,System.Globalization.CultureInfo.InvariantCulture)*System.Numerics.BigInteger.Parse(coefficient.Denominator,System.Globalization.CultureInfo.InvariantCulture);
        var denominator=System.Numerics.BigInteger.Parse(value.Denominator,System.Globalization.CultureInfo.InvariantCulture)*System.Numerics.BigInteger.Parse(coefficient.Numerator,System.Globalization.CultureInfo.InvariantCulture);
        var gcd=System.Numerics.BigInteger.GreatestCommonDivisor(System.Numerics.BigInteger.Abs(numerator),denominator);numerator/=gcd;denominator/=gcd;
        var price=new UnitPrice(ExactDecimal.Parse(new AmountFormat(rounding.DecimalPlaces,rounding.TieRule,false,true).FormatFraction(numerator,denominator)),unit);
        return new(price,numerator.ToString(System.Globalization.CultureInfo.InvariantCulture),denominator.ToString(System.Globalization.CultureInfo.InvariantCulture),ParRatePolicy,rounding.DecimalPlaces,rounding.TieRule);
    }
    private static ParSwapRate SolveParRate(FixedSide fixedSide,FinancialSwapEvaluation value,AmountFormat rounding)
    {
        var coefficient=ExactMoney.FromMoney(new UnitPrice(ExactDecimal.Parse("1"),fixedSide.Price.Unit).Times(fixedSide.UnitQuantity))
            .Scale(value.Sides[0].Payments.Single().Discount.Factor);
        var floating=value.Sides[1].Contribution.Value.PresentValue;
        return QuotientRate(floating,coefficient,fixedSide.Price.Unit,rounding);
    }

    public static CurrencyDefaultDiscounting CreateDiscounting(FinancialSwapInstrument instrument,MarketSnapshot snapshot,CurrencyDiscountCurve? currencyDefault)=>
        CreateDiscounting(instrument,instrument?.Discounting,snapshot,currencyDefault);
    private static CurrencyDefaultDiscounting CreateDiscounting(SidedInstrument? instrument,bool? enabled,MarketSnapshot snapshot,CurrencyDiscountCurve? currencyDefault)
    {
        if(instrument is null||enabled is null)throw new MarketDataException("swap_context","An explicit instrument is required.");
        return new(Schedules(instrument),snapshot,enabled.Value,currencyDefault);
    }
    public static FinancialSwapEvaluation Evaluate(FinancialSwapInstrument instrument,CurveBuild market,bool discounting,CurrencyDiscountCurve? currencyDefault) =>
        Evaluate(instrument,market,discounting,currencyDefault,instrument?.Discounting);
    public static FinancialSwapEvaluation Evaluate(MonthlyFinancialSwap instrument,CurveBuild market,bool discounting,CurrencyDiscountCurve? currencyDefault) =>
        Evaluate(instrument,market,discounting,currencyDefault,instrument?.Discounting);
    public static CurrencyDefaultDiscounting CreateDiscounting(CrossCommodityScheduledSwap instrument,MarketSnapshot snapshot,CurrencyDiscountCurve? currencyDefault)=>CreateDiscounting(instrument,instrument?.Discounting,snapshot,currencyDefault);
    public static FinancialSwapEvaluation Evaluate(CrossCommodityScheduledSwap instrument,ConnectedValuation valuation,
        ImmutableDictionary<CurveRequest,CurvePointKey> bindings,CurveBuild authority,CurrencyDiscountCurve? currencyDefault,CancellationToken cancellation=default)=>
        EvaluateConnected(instrument,instrument?.Discounting,valuation,bindings,authority,currencyDefault,cancellation);
    public static CurrencyDefaultDiscounting CreateDiscounting(ScheduledFinancialSwap instrument,MarketSnapshot snapshot,CurrencyDiscountCurve? currencyDefault)=>CreateDiscounting(instrument,instrument?.Discounting,snapshot,currencyDefault);
    public static FinancialSwapEvaluation Evaluate(ScheduledFinancialSwap instrument,CurveBuild market,bool discounting,CurrencyDiscountCurve? currencyDefault)=>Evaluate(instrument,market,discounting,currencyDefault,instrument?.Discounting);
    public static FinancialSwapEvaluation Evaluate(ScheduledFinancialSwap instrument,ConnectedValuation valuation,
        ImmutableDictionary<CurveRequest,CurvePointKey> bindings,CurveBuild authority,CurrencyDiscountCurve? currencyDefault,CancellationToken cancellation=default)=>
        EvaluateConnected(instrument,instrument?.Discounting,valuation,bindings,authority,currencyDefault,cancellation);
    public static FinancialSwapEvaluation Evaluate(FinancialSwapInstrument instrument,ConnectedValuation valuation,
        ImmutableDictionary<CurveRequest,CurvePointKey> bindings,CurveBuild authority,CurrencyDiscountCurve? currencyDefault,CancellationToken cancellation=default)=>
        EvaluateConnected(instrument,instrument?.Discounting,valuation,bindings,authority,currencyDefault,cancellation);
    private static FinancialSwapEvaluation EvaluateConnected(SidedInstrument? instrument,bool? enabled,ConnectedValuation valuation,
        ImmutableDictionary<CurveRequest,CurvePointKey> bindings,CurveBuild authority,CurrencyDiscountCurve? currencyDefault,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(instrument is null||valuation is null||authority is null)
            throw new MarketDataException("swap_context","An exact instrument, prepared connected valuation and qualified lifecycle authority are required.");
        var inputs=CreateDiscounting(instrument,enabled,valuation.Snapshot,currencyDefault);
        return Result(valuation.PV(instrument,bindings,authority,inputs,cancellation),inputs);
    }
    private static FinancialSwapEvaluation Evaluate(SidedInstrument? instrument,CurveBuild market,bool discounting,
        CurrencyDiscountCurve? currencyDefault,bool? declaredDiscounting)
    {
        if(instrument is null||market is null)throw new MarketDataException("swap_context","An explicit instrument and built market scenario are required.");
        if(discounting!=declaredDiscounting)throw new MarketDataException("swap_valuation","Discounting must match the immutable instrument definition.");
        var inputs=new CurrencyDefaultDiscounting(Schedules(instrument),market.Snapshot,discounting,currencyDefault);
        var value=instrument.PV(new SideValuationContext(market,inputs));
        return Result(value,inputs);
    }
    private static IEnumerable<SideSchedule> Schedules(SidedInstrument instrument)
    {
        var sides=instrument.Sides.Select(m=>m.Side).ToArray();
        if(sides.GroupBy(s=>s.Id).Any(g=>g.Select(DefinitionIdentity.For).Distinct().Count()!=1||g.Select(s=>s.PricingBindingIdentity).Distinct().Count()!=1))
            throw new MarketDataException("side_definition","One side identity cannot resolve to conflicting economic terms or pricing bindings.");
        return sides.DistinctBy(s=>s.Id).Select(s=>s.GenerateSchedule());
    }
    private static FinancialSwapEvaluation Result(InstrumentValuation value,CurrencyDefaultDiscounting inputs)=>
        new(value,value.Sides.Select(s=>new DiscountedSideValuation(s,((PaymentSideValuation)s.Value).Payments.Select(p=>new DiscountedSidePayment(p,inputs.Inputs[p.Plan.Key],value.Snapshot)).ToImmutableArray())).ToImmutableArray());
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
