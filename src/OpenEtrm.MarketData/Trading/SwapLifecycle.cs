using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

public sealed record SettlementContribution(Guid Id, int MembershipOrdinal, DateOnly DeliveryFrom, DateOnly DeliveryTo,
    int Ordinal, DateTimeOffset DueAt, ExactMoney Amount, ExactMoney Applied)
{
    public ExactMoney Outstanding => Amount.Subtract(Applied);
    public SidePaymentKey DiscountKey => new(Id, 0, DueAt, Amount.Currency);
}

public sealed record SwapLifecyclePayment(int MembershipOrdinal, SidePaymentPlan Plan, string State,
    ExactMoney ContractualAmount, ExactMoney RemainingValue, bool Final, bool Realized,DateTimeOffset CompletionAt);
public sealed record SwapPeriodCompletion(int MembershipOrdinal,DateOnly DeliveryFrom,DateOnly DeliveryTo,DateTimeOffset At,string Source)
{
    public const string Policy="financial_swap_period_completion_v1";
}
public sealed record SwapLifecycleValue(ExactMoney RemainingValue, ExactMoney DeterminedOutstanding,
    ExactMoney DeterminedPv, ExactMoney DueBalance, ExactMoney Realized, ExactMoney Unrealized,
    ExactMoney NetCash, ExactMoney Corrections, ExactMoney EstimatedRemainingPv, ExactMoney RoundingEffect,
    ImmutableArray<SwapLifecyclePayment> Payments);
public sealed record SwapBindingValuation(SwapSettlementInputs Settlement, ISideDiscounting Discounting);

// Immutable operation-owned trade binding state, outside reusable instrument identity.
public sealed class SwapSettlementInputs
{
    public Guid TradeId { get; }
    public Guid BindingId { get; }
    public Quantity Quantity { get; }
    public ImmutableArray<SettlementContribution> Contributions { get; }
    public ExactMoney NetCash { get; }
    public ExactMoney RoundingEffect { get; }
    public ImmutableArray<SwapPeriodCompletion> Completions { get; }
    public SwapSettlementInputs(Guid tradeId, Guid bindingId, Quantity quantity,
        ImmutableArray<SettlementContribution> contributions, ExactMoney netCash, ExactMoney roundingEffect,
        ImmutableArray<SwapPeriodCompletion> completions,string duePolicy, string recognitionPolicy)
    {
        if (tradeId == Guid.Empty || bindingId == Guid.Empty || quantity is null || quantity.Amount.CompareTo(default) <= 0 ||
            contributions.IsDefault || contributions.Any(c => c is null || c.Id == Guid.Empty || c.MembershipOrdinal is < 0 or > 1 ||
                c.DeliveryFrom >= c.DeliveryTo || c.Ordinal < 1 || !TradingInstant.IsSupported(c.DueAt) || c.DueAt == default ||
                c.Amount is null || c.Applied is null || c.Amount.Currency != netCash?.Currency || c.Applied.Currency != c.Amount.Currency) ||
            contributions.Select(c => c.Id).Distinct().Count() != contributions.Length || roundingEffect is null || netCash is null ||
            roundingEffect.Currency != netCash.Currency || completions.IsDefaultOrEmpty ||
            completions.Any(c=>c is null||c.MembershipOrdinal is <0 or >1||c.DeliveryFrom>=c.DeliveryTo||!TradingInstant.IsSupported(c.At)||c.At==default||
                c.Source=="date_only_utc_v1"&&c.At!=new DateTimeOffset(c.DeliveryTo.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc))||
                c.Source is not("date_only_utc_v1" or "retained_quantity_interval_end_v1"))||
            completions.Select(c=>(c.MembershipOrdinal,c.DeliveryFrom,c.DeliveryTo)).Distinct().Count()!=completions.Length||
            duePolicy != "swap_due_nominal_v1" || recognitionPolicy != "swap_completed_period_trading_pnl_v1")
            throw Error("settlement_inputs", "Supply complete immutable contributions, quantities, currency and explicit supported lifecycle policies.");
        foreach (var group in contributions.GroupBy(c => (c.MembershipOrdinal, c.DeliveryFrom, c.DeliveryTo)))
            if (!group.Select(c => c.Ordinal).Order().SequenceEqual(Enumerable.Range(1, group.Count())))
                throw Error("settlement_lineage", "Retain the complete ordered original and adjustment chain.");
        foreach(var contribution in contributions)
        {
            var original=contribution.Amount.ToMoney().Amount;var applied=contribution.Applied.ToMoney().Amount;
            if(applied.CompareTo(default)>0?original.CompareTo(applied)<0:applied.CompareTo(default)<0&&original.CompareTo(applied)>0)
                throw Error("settlement_conservation","Cash allocations must retain contribution direction and cannot exceed its amount.");
        }
        if (contributions.Select(c => c.Applied).Aggregate(ExactMoney.FromMoney(new(default,netCash.Currency)),(a,b) => a.Add(b)) != netCash)
            throw Error("settlement_conservation", "Recorded cash must equal its explicit allocations.");
        TradeId = tradeId; BindingId = bindingId; Quantity = quantity; Contributions = contributions;
        NetCash = netCash; RoundingEffect = roundingEffect;
        Completions=completions;
    }

    private IEnumerable<(int Membership, SidePaymentPlan Plan)> Plans(SidedInstrument instrument)
    {
        if (instrument is not (FinancialSwapInstrument or MonthlyFinancialSwap or ScheduledFinancialSwap or CrossCommodityScheduledSwap) ||
            instrument.Currency != NetCash.Currency || instrument.UnitQuantity.Specification != Quantity.Specification)
            throw Error("settlement_capability", "Lifecycle valuation requires the exact enrolled financial swap currency and quantity basis.");
        var plans = instrument.Sides.SelectMany((m,n) => ((SidePaymentSchedule)m.Side.GenerateSchedule()).Payments.Select(p => (Membership:n, Plan:p))).ToArray();
        if(!plans.Select(p=>(p.Membership,p.Plan.DeliveryFrom,p.Plan.DeliveryTo)).ToHashSet()
            .SetEquals(Completions.Select(c=>(c.MembershipOrdinal,c.DeliveryFrom,c.DeliveryTo))))
            throw Error("settlement_completion","Every contractual period requires its explicit retained completion boundary.");
        if (Contributions.Any(c => !plans.Any(p => Covers(c,p.Membership,p.Plan))))
            throw Error("settlement_continuity", "Every determined contribution must retain exact current occurrence coverage.");
        return plans;
    }
    private static bool Covers(SettlementContribution c,int membership,SidePaymentPlan plan) =>
        c.MembershipOrdinal == membership && c.DeliveryFrom == plan.DeliveryFrom && c.DeliveryTo == plan.DeliveryTo;
    public ImmutableArray<SidePaymentKey> DiscountKeys(SidedInstrument instrument, MarketSnapshot snapshot) =>
        Plans(instrument).Where(p => !Contributions.Any(c => Covers(c,p.Membership,p.Plan)) && p.Plan.PaymentAt > snapshot.ValuationAt)
            .Select(p => p.Plan.Key).Concat(Contributions.Where(c => c.DueAt > snapshot.ValuationAt && c.Outstanding.Numerator != "0").Select(c => c.DiscountKey))
            .Distinct().ToImmutableArray();

    public SwapLifecycleValue Evaluate(SidedInstrument instrument, SideValuationContext context)
    {
        if (context is null) throw Error("settlement_context", "Supply the selected immutable valuation context.");
        var plans = Plans(instrument).ToArray(); var keys = DiscountKeys(instrument,context.Snapshot);
        var factors = context.Discounting.Resolve(keys,context.Snapshot);
        var enabled = instrument switch { FinancialSwapInstrument s => s.Discounting, MonthlyFinancialSwap s => s.Discounting,
            ScheduledFinancialSwap s => s.Discounting,CrossCommodityScheduledSwap s=>s.Discounting, _ => throw Error("settlement_capability","Unsupported financial swap.") };
        if (factors.IsDefault || factors.Length != keys.Length || factors.Any(f => f is null ||
                f.DiscountFactor.CompareTo(default) <= 0 || !Enum.IsDefined(f.Source) ||
                f.Source == DiscountFactorSource.Undiscounted && f.DiscountFactor != ExactDecimal.Parse("1") ||
                enabled == (f.Source == DiscountFactorSource.Undiscounted) || !context.Snapshot.HasSameInputs(f.Snapshot)) ||
            !factors.Select(f => f.Payment).ToHashSet().SetEquals(keys) || factors.Select(f => f.Payment).Distinct().Count() != factors.Length)
            throw Error("side_payment_factors", "Retain exactly one positive factor for each future lifecycle value key.");
        var discounts = factors.ToDictionary(f => f.Payment, f => f.DiscountFactor);
        var zero = ExactMoney.FromMoney(new(default,instrument.Currency));
        var determined = zero; var determinedPv = zero; var due = zero; var corrections = zero;
        foreach (var contribution in Contributions)
        {
            var balance = contribution.Outstanding;
            determined = determined.Add(balance);
            if (contribution.Ordinal > 1) corrections = corrections.Add(contribution.Amount);
            if (contribution.DueAt <= context.Snapshot.ValuationAt) { due = due.Add(balance); determinedPv = determinedPv.Add(balance); }
            else if (balance.Numerator != "0") determinedPv = determinedPv.Add(balance.Scale(discounts[contribution.DiscountKey]));
        }
        var rows = ImmutableArray.CreateBuilder<SwapLifecyclePayment>(); var remaining = zero; var realized = zero; var estimated = zero;
        foreach (var (membership,plan) in plans)
        {
            var covered = Contributions.Where(c => Covers(c,membership,plan)).ToArray();
            ExactMoney amount; ExactMoney pv; bool final;
            if (covered.Length != 0)
            {
                amount = covered.Select(c => c.Amount).Aggregate((a,b) => a.Add(b)); final = true;
                pv = covered.Select(c => c.DueAt <= context.Snapshot.ValuationAt ? c.Outstanding : c.Outstanding.Numerator == "0" ? zero :
                    c.Outstanding.Scale(discounts[c.DiscountKey])).Aggregate((a,b) => a.Add(b));
            }
            else
            {
                if (plan.FirstFixingAt is {} boundary && context.Snapshot.ValuationAt >= boundary)
                    throw Error("swap_fixing_required", "Legacy projection-only payments require qualified final fixing definitions.");
                var prices = plan.Prices.Select(context.Resolve).ToImmutableArray();
                final = prices.All(p => p.State is SchedulePriceState.Fixed or SchedulePriceState.Actual);
                if (plan.PaymentAt <= context.Snapshot.ValuationAt && !final)
                    throw Error("settlement_not_final", "Undetermined due payments require established final prices.");
                var m = instrument.Sides[membership];
                amount = new SidePaymentCash(plan,prices).CashAmount.Scale(m.QuantityRatio ?? OpenEtrm.Shared.Numerics.ExactRatio.One)
                    .Scale(m.Coefficient).Scale(Quantity.Amount);
                pv = plan.PaymentAt <= context.Snapshot.ValuationAt ? amount : amount.Scale(discounts[plan.Key]);
                if (!final) estimated = estimated.Add(pv);
            }
            var completion=Completions.Single(c=>c.MembershipOrdinal==membership&&c.DeliveryFrom==plan.DeliveryFrom&&c.DeliveryTo==plan.DeliveryTo);
            var completed = context.Snapshot.ValuationAt>=completion.At;
            var recognized = completed && final;
            remaining = remaining.Add(pv); if (recognized) realized = realized.Add(amount);
            rows.Add(new(membership,plan,covered.Length == 0 ? "Undetermined" : covered.All(c => c.Outstanding.Numerator == "0") ? "Paid" :
                covered.Any(c => c.DueAt <= context.Snapshot.ValuationAt && c.Outstanding.Numerator != "0") ? "Due" : "Determined",amount,pv,final,recognized,completion.At));
        }
        var qualified = rows.Select(p => p with { Realized = p.Realized && rows.Where(other =>
            other.Plan.DeliveryFrom == p.Plan.DeliveryFrom && other.Plan.DeliveryTo == p.Plan.DeliveryTo).All(other => other.Realized) }).ToImmutableArray();
        realized = qualified.Where(p => p.Realized).Select(p => p.ContractualAmount).Aggregate(zero,(a,b) => a.Add(b));
        return new(remaining,determined,determinedPv,due,realized,remaining.Add(NetCash).Subtract(realized),NetCash,corrections,estimated,RoundingEffect,qualified);
    }
    internal ExactMoney UnitValue(SidedInstrument instrument,SideValuationContext context) => Evaluate(instrument,context).RemainingValue.DivideBy(Quantity.Amount);
    private static MarketDataException Error(string code,string message) => new(code,message);
}
