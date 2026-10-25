using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// Original trade membership and period identify the obligation. Current side,
// payment date, instrument revision and input revisions are calculation evidence.
public sealed record PaymentOccurrenceKey
{
    public Guid TradeId { get; }
    public Guid OriginBindingId { get; }
    public int OriginMembershipOrdinal { get; }
    public DateOnly DeliveryFrom { get; }
    public DateOnly DeliveryTo { get; }
    public PaymentOccurrenceKey(Guid tradeId, Guid originBindingId, int originMembershipOrdinal,
        DateOnly deliveryFrom, DateOnly deliveryTo)
    {
        if (tradeId == Guid.Empty || originBindingId == Guid.Empty || originMembershipOrdinal < 0 || deliveryFrom >= deliveryTo)
            throw new MarketDataException("settlement_occurrence", "Supply the originating trade binding, membership and complete contractual period.");
        TradeId = tradeId; OriginBindingId = originBindingId; OriginMembershipOrdinal = originMembershipOrdinal;
        DeliveryFrom = deliveryFrom; DeliveryTo = deliveryTo;
    }
}

public sealed record SwapSettlementRounding
{
    public const string Implementation = "currency_minor_units_to_even_v1";
    public ReferenceKey Currency { get; }
    public int DecimalPlaces { get; }
    public CashTieRule TieRule { get; }
    public string Version => Implementation;
    internal AmountFormat Format => new(DecimalPlaces, TieRule, false, false);
    public SwapSettlementRounding(ReferenceKey currency, int decimalPlaces, CashTieRule tieRule, string implementation)
    {
        Money.RequireKey(currency);
        if (implementation != Implementation || decimalPlaces is < 0 or > 12 || tieRule != CashTieRule.ToEven)
            throw new MarketDataException("settlement_rounding", "Select an explicit supported gross-payment precision, tie rule and implementation.");
        Currency = currency; DecimalPlaces = decimalPlaces; TieRule = tieRule;
    }
}

// Nonbinding evidence only. Durable acceptance must bind certified knowledge,
// authorized trade head, expected settlement version and financial idempotency.
public sealed record SwapPaymentCalculation
{
    public const string Implementation = "final_swap_gross_payment_v1";
    public PaymentOccurrenceKey Occurrence { get; }
    public Guid InstrumentId { get; }
    public SidePaymentCash Evidence { get; }
    public MarketSnapshot Snapshot { get; }
    public string NumericalPolicy => SideValuation.Policy;
    public Quantity BookedQuantity { get; }
    public ExactQuantity ResolvedQuantity { get; }
    public ExactDecimal Coefficient { get; }
    public ExactRatio QuantityRatio { get; }
    public SwapSettlementRounding Rounding { get; }
    public ExactMoney ExactAmount { get; }
    public Money Amount { get; }
    public ExactMoney RoundingEffect => ExactMoney.FromMoney(Amount).Subtract(ExactAmount);
    public const string EvidenceIdentityVersion = "swap_payment_evidence_identity_v1";
    public string ContractHash { get; }
    public string InputHash { get; }
    internal SwapPaymentCalculation(PaymentOccurrenceKey occurrence, SidedInstrument instrument, SideMembership membership,
        SidePaymentCash evidence, Quantity quantity, MarketSnapshot snapshot, SwapSettlementRounding rounding)
    {
        Occurrence = occurrence; InstrumentId = instrument.Id; Evidence = evidence; BookedQuantity = quantity;
        Snapshot = snapshot; Rounding = rounding; Coefficient = membership.Coefficient;
        QuantityRatio = membership.QuantityRatio ?? ExactRatio.One;
        ResolvedQuantity = evidence.Plan.AllocatedQuantity.Scale(QuantityRatio).Scale(quantity.Amount);
        ExactAmount = evidence.CashAmount.Scale(QuantityRatio).Scale(Coefficient).Scale(quantity.Amount);
        Amount = ExactAmount.ToMoney(rounding.Format);
        ContractHash = ContractIdentity(instrument,membership,evidence.Plan,quantity);
        InputHash = Hash(new
        {
            version = EvidenceIdentityVersion,implementation=Implementation,NumericalPolicy, ExactAmount.Numerator, ExactAmount.Denominator, rounding.Currency, rounding.DecimalPlaces, roundingPolicyVersion=rounding.Version,
            prices = evidence.Prices.Select(p => new { p.ExactPrice.Numerator,p.ExactPrice.Denominator,unit = Unit(p.ExactPrice.Unit),
                lineage = p.Lineage.OrderBy(k => k.CatalogId).ThenBy(k => k.Id) })
        });
    }
    public static string ContractIdentity(SidedInstrument instrument,SideMembership membership,SidePaymentPlan plan,Quantity quantity)
    {
        if(instrument is null||membership is null||plan is null||quantity is null||quantity.Specification!=instrument.UnitQuantity.Specification)
            throw new MarketDataException("settlement_continuity","Supply exact supported contractual payment terms and booked quantity.");
        static object? Price(UnitPrice? price) => price is null ? null : new { value = price.Value.ToString(), unit = Unit(price.Unit) };
        return Hash(new
        {
            version = EvidenceIdentityVersion, plan.DeliveryFrom, plan.DeliveryTo, plan.PaymentAt,
            plan.PaymentConvention, plan.Currency, quantity = plan.AllocatedQuantity.Scale(membership.QuantityRatio ?? ExactRatio.One).Scale(quantity.Amount),
            coefficient = membership.Coefficient.ToString(),
            prices = plan.Prices.Select(p => new { fixedPrice = Price(p.FixedPrice), spread = Price(p.Spread), weight = p.Weight.ToString(),
                index = p.Lookup?.Index, quote = p.Lookup?.Quote.Key, fixing = p.Lookup?.Fixing,
                unit = p.Lookup is {} lookup ? Unit(PriceUnit.FromQuote(lookup.Quote)) : null }),
            applications = plan.Applications.Select(a => new { a.TargetFrom,a.TargetTo,a.PriceOrdinal,a.MappingRule,weight = a.Weight.ToString() })
        });
    }
    private static object Unit(PriceUnit unit) => new { unit.Currency, unit.Quantity, multiplier = unit.Multiplier.ToString() };
    private static string Hash(object value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}

public static class SwapSettlement
{
    public static SwapPaymentCalculation Calculate(SidedInstrument instrument, int membershipOrdinal, int paymentOrdinal,
        Quantity bookedQuantity, PaymentOccurrenceKey occurrence, CurveBuild market, SwapSettlementRounding rounding)
    {
        if (market is null) throw new MarketDataException("settlement_context", "Supply the exact final-fixing market authority.");
        var context = new SideValuationContext(market, null, [],
            _ => throw new MarketDataException("settlement_not_final", "A projection cannot determine cash."),
            _ => [], market.Pricing.NumericalPolicy, market.Pricing.ScenarioId);
        return Calculate(instrument, membershipOrdinal, paymentOrdinal, bookedQuantity, occurrence, context, rounding);
    }

    public static SwapPaymentCalculation Calculate(SidedInstrument instrument, int membershipOrdinal, int paymentOrdinal,
        Quantity bookedQuantity, PaymentOccurrenceKey occurrence, SideValuationContext context, SwapSettlementRounding rounding)
    {
        if (instrument is not (FinancialSwapInstrument or MonthlyFinancialSwap or ScheduledFinancialSwap))
            throw new MarketDataException("settlement_capability", "Only qualified financial swap contracts support determination.");
        if (occurrence is null || context is null || rounding is null || membershipOrdinal < 0 || membershipOrdinal >= instrument.Sides.Length ||
            occurrence.OriginMembershipOrdinal != membershipOrdinal)
            throw new MarketDataException("settlement_occurrence", "Select one explicit original payment membership and context.");
        if (bookedQuantity is null) throw new MarketDataException("settlement_quantity", "Supply the explicit booked contractual quantity.");
        if (bookedQuantity.Specification != instrument.UnitQuantity.Specification)
            throw new MarketDataException("quantity_mismatch", "The booked quantity must retain the instrument's exact unit.");
        if (bookedQuantity.Amount.CompareTo(default) <= 0)
            throw new MarketDataException("settlement_quantity", "Supply the positive contractual swap quantity; membership owns direction.");
        var membership = instrument.Sides[membershipOrdinal];
        if (membership.Side.GenerateSchedule() is not SidePaymentSchedule schedule || paymentOrdinal < 0 || paymentOrdinal >= schedule.Payments.Length)
            throw new MarketDataException("settlement_occurrence", "Select an exact generated contractual payment.");
        var plan = schedule.Payments[paymentOrdinal];
        if (rounding.Currency != plan.Currency)
            throw new MarketDataException("settlement_rounding", "Rounding must pin this payment's exact currency revision and precision.");
        if (plan.DeliveryFrom != occurrence.DeliveryFrom || plan.DeliveryTo != occurrence.DeliveryTo)
            throw new MarketDataException("settlement_continuity", "Changed contractual periods require an explicit qualified continuity mapping.");
        if (plan.Prices.Any(p => p.FixedPrice is null && (p.Lookup?.Fixing is not {} fixing || context.Snapshot.ValuationAt < fixing.FixingAt)))
            throw new MarketDataException("settlement_not_final", "All required contractual fixings must be eligible; projections cannot determine cash.");
        var prices = plan.Prices.Select(context.Resolve).ToImmutableArray();
        if (prices.Any(p => p.State is not (SchedulePriceState.Fixed or SchedulePriceState.Actual)))
            throw new MarketDataException("settlement_not_final", "Every contractual price must be fixed or an eligible final fixing; projections cannot determine cash.");
        return new(occurrence, instrument, membership, new(plan, prices), bookedQuantity, context.Snapshot, rounding);
    }
}
