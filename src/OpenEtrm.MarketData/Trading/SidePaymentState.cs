using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// Exact cash conservation is shared with financial-swap settlement. Pricing
// remains rational; recording a payment does not round its remaining balance.
internal static class CashAllocationBalance
{
    internal static void Require(ExactMoney amount, ExactMoney applied)
    {
        var zero=ExactMoney.Parse("0",amount.Currency);
        if(applied.Currency!=amount.Currency || (applied.CompareTo(zero)>0?amount.CompareTo(applied)<0:
            applied.CompareTo(zero)<0&&amount.CompareTo(applied)>0))
            throw new MarketDataException("settlement_conservation","Cash allocations must retain contribution direction and cannot exceed its amount.");
    }
}

public sealed record FinalSidePayment
{
    public Guid Id { get; }
    public Guid TradeId { get; }
    public Guid BindingId { get; }
    public int MembershipOrdinal { get; }
    public int PaymentOrdinal { get; }
    public DateTimeOffset DeterminedAt { get; }
    public DateTimeOffset KnownAt { get; }
    public SidePaymentCash Evidence { get; }
    public string ContractIdentity { get; }

    private FinalSidePayment(Guid id,Trade trade,Guid binding,int membership,int payment,SideValuationContext context)
    {
        if(id==Guid.Empty || context?.Snapshot.Knowledge is not {} knowledge)
            throw Error("Supply a determination identity and an explicit resolved knowledge cutoff.");
        var terms=PaymentSideState.Terms(trade,binding,membership);
        var rows=((SidePaymentSchedule)terms.Member.Side.GenerateSchedule()).Payments;
        if(payment<0||payment>=rows.Length)throw Error("Select an exact retained payment occurrence.");
        var row=rows[payment];var prices=row.Prices.Select(context.Resolve).ToImmutableArray();
        if(prices.Any(p=>p.State==SchedulePriceState.Projected))throw Error("A projected price cannot determine final cash.");
        Id=id;TradeId=trade.Id;BindingId=binding;MembershipOrdinal=membership;PaymentOrdinal=payment;
        DeterminedAt=context.Snapshot.ValuationAt;KnownAt=knowledge.KnownAt;
        Evidence=new(row,prices);ContractIdentity=PaymentSideState.Identity(terms.Member.Side,terms.Quantity,terms.Member);
    }
    public static FinalSidePayment Determine(Guid id,Trade trade,Guid binding,int membership,int payment,SideValuationContext context)=>new(id,trade,binding,membership,payment,context);
    private static MarketDataException Error(string message)=>new("payment_determination",message);
}

public sealed record SideCashAllocation(Guid Id,Guid DeterminationId,DateTimeOffset PaidAt,DateTimeOffset KnownAt,ExactMoney Amount);
public enum CashPaymentState { Projected=1,FixedUnpaid,Overdue,PartPaid,PartPaidOverdue,Paid,NoAmountDue }
public sealed record CashPaymentBalance(Guid? DeterminationId,ExactMoney Paid,ExactMoney Outstanding,CashPaymentState State)
{
    internal CashPaymentBalance Scale(ExactDecimal scale)=>this with{Paid=Paid.Scale(scale),Outstanding=Outstanding.Scale(scale)};
}

// Immutable, operation-owned facts for one cash membership of a held binding.
// No write, database lookup, settlement-date inference or risk formula lives here.
public sealed class PaymentSideState
{
    public const string Policy="explicit_paid_cash_nominal_due_v1";
    public Guid TradeId { get; }
    public Guid BindingId { get; }
    public Quantity Quantity { get; }
    public Guid SideId { get; }
    public int MembershipOrdinal { get; }
    public ImmutableArray<FinalSidePayment> Determinations { get; }
    public ImmutableArray<SideCashAllocation> Allocations { get; }
    private readonly string identity;
    private readonly ExactRatio toUnit;
    private readonly ExactDecimal direction;
    private readonly SideMembership membershipTerms;

    public PaymentSideState(Trade trade,Guid binding,int membership,ImmutableArray<FinalSidePayment> determinations,
        ImmutableArray<SideCashAllocation> allocations,string policy)
    {
        var terms=Terms(trade,binding,membership);var member=terms.Member;
        if(policy!=Policy || determinations.IsDefault || allocations.IsDefault ||
            member.Coefficient!=ExactDecimal.Parse("1")&&member.Coefficient!=ExactDecimal.Parse("-1") || terms.Quantity.Amount.CompareTo(default)==0)
            throw Error("Select complete cash inputs, unit receive/pay direction and the explicit paid-cash/due policy.");
        TradeId=trade.Id;BindingId=binding;Quantity=terms.Quantity;MembershipOrdinal=membership;SideId=member.Side.Id;
        identity=Identity(member.Side,Quantity,member);
        membershipTerms=member;
        var amount=Quantity.Amount.ScaledValue;direction=member.Coefficient;
        if(amount.Sign<0){amount=-amount;direction*=ExactDecimal.Parse("-1");}
        toUnit=new ExactRatio(amount,ExactDecimal.WorkingScale).Times(member.QuantityRatio??ExactRatio.One).Inverse();
        if(determinations.Any(d=>d is null||d.TradeId!=TradeId||d.BindingId!=BindingId||d.MembershipOrdinal!=membership||d.ContractIdentity!=identity) ||
            determinations.Select(d=>d.Id).Distinct().Count()!=determinations.Length || determinations.Select(d=>d.PaymentOrdinal).Distinct().Count()!=determinations.Length ||
            allocations.Any(a=>a is null||a.Id==Guid.Empty||a.Amount is null||a.Amount.Currency!=terms.Member.Side.GenerateSchedule().Currency||
                !Finite(a.PaidAt)||!Finite(a.KnownAt)||!determinations.Any(d=>d.Id==a.DeterminationId)) || allocations.Select(a=>a.Id).Distinct().Count()!=allocations.Length)
            throw Error("Retain distinct exact owned payment determinations and explicitly linked cash allocations.");
        Determinations=determinations;Allocations=allocations;
    }
    internal void Require(Side side,SideMembership member,Quantity quantity)
    {
        if(ReferenceEquals(side,membershipTerms.Side)&&ReferenceEquals(member,membershipTerms)&&quantity==Quantity)return;
        if(side.Id!=SideId||quantity!=Quantity||identity!=Identity(side,quantity,member))throw Error("Cash state requires its exact retained side terms, quantity and membership.");
    }
    internal void Require(Side side)=>Require(side,membershipTerms,Quantity);
    internal void Require(SidePaymentSchedule schedule)
    {
        if(schedule.SideId!=SideId || Determinations.Any(d=>d.PaymentOrdinal<0||d.PaymentOrdinal>=schedule.Payments.Length||
            d.Evidence.Plan.Key!=schedule.Payments[d.PaymentOrdinal].Key||d.Evidence.Plan.DeliveryFrom!=schedule.Payments[d.PaymentOrdinal].DeliveryFrom||
            d.Evidence.Plan.DeliveryTo!=schedule.Payments[d.PaymentOrdinal].DeliveryTo||d.Evidence.Plan.AllocatedQuantity!=schedule.Payments[d.PaymentOrdinal].AllocatedQuantity))
            throw Error("Final cash must retain its exact payment plan and complete occurrence coverage.");
    }
    internal FinalSidePayment? FinalAt(int ordinal,MarketSnapshot snapshot)=>Determinations.SingleOrDefault(d=>d.PaymentOrdinal==ordinal&&d.DeterminedAt<=snapshot.ValuationAt&&d.KnownAt<=snapshot.Knowledge!.KnownAt);
    public ImmutableArray<CurveRequest> DependenciesAt(Side side,MarketSnapshot snapshot)
    {
        Require(side);
        if(snapshot.Knowledge is null)throw Error("Cash dependencies require an explicit resolved knowledge cutoff.");
        var schedule=(SidePaymentSchedule)side.GenerateSchedule();Require(schedule);
        return SidePaymentSchedule.PriceDependencies(schedule.Payments.Where(p=>FinalAt(p.Ordinal,snapshot) is null).SelectMany(p=>p.Prices),snapshot);
    }
    internal ExactMoney PaidAt(FinalSidePayment? final,MarketSnapshot snapshot,ExactMoney amount)
    {
        var zero=ExactMoney.Parse("0",amount.Currency);var total=zero;
        foreach(var allocation in Allocations.Where(a=>a.PaidAt<=snapshot.ValuationAt&&a.KnownAt<=snapshot.Knowledge!.KnownAt))
        {
            var receipt=Determinations.Single(d=>d.Id==allocation.DeterminationId);
            if(receipt.DeterminedAt>snapshot.ValuationAt||receipt.KnownAt>snapshot.Knowledge!.KnownAt||allocation.PaidAt<receipt.DeterminedAt||allocation.KnownAt<receipt.KnownAt)
                throw Error("A visible cash allocation requires its visible preceding determination.");
            if(receipt.Id!=final?.Id)continue;
            var normalized=allocation.Amount.Scale(toUnit).Scale(direction);
            if(normalized==zero)throw Error("A cash allocation must be nonzero.");
            CashAllocationBalance.Require(amount,normalized);total=total.Add(normalized);
        }
        CashAllocationBalance.Require(amount,total);return total;
    }
    internal static (SideMembership Member,Quantity Quantity) Terms(Trade trade,Guid binding,int membership)
    {
        var matches=trade?.Instruments.Where(i=>i.Id==binding).ToArray();
        if(matches is not [var held]||held.Instrument is not SidedInstrument instrument||membership<0||membership>=instrument.Sides.Length||
            instrument.Sides[membership].Side.GenerateSchedule() is not SidePaymentSchedule)
            throw Error("Select a held instrument's exact dated cash membership.");
        return(instrument.Sides[membership],held.Quantity);
    }
    internal static string Identity(Side side,Quantity quantity,SideMembership member)=>DefinitionIdentity.CanonicalTerm(new object?[]{
        "side_payment_state_v1",DefinitionIdentity.For(side).Canonical,side.PricingBindingIdentity?.Canonical,quantity,member.Coefficient,member.QuantityRatio});
    private static bool Finite(DateTimeOffset at)=>at!=default&&TradingInstant.IsSupported(at);
    private static MarketDataException Error(string message)=>new("payment_state",message);
}

public sealed class CashflowBindingValuation:IBindingValuationState
{
    public Guid TradeId { get; }
    public Guid BindingId { get; }
    public Quantity Quantity { get; }
    public ImmutableArray<PaymentSideState> Sides { get; }
    public CashflowBindingValuation(Guid trade,Guid binding,Quantity quantity,ImmutableArray<PaymentSideState> sides)
    {
        if(trade==Guid.Empty||binding==Guid.Empty||quantity is null||sides.IsDefaultOrEmpty||sides.Any(s=>s is null||s.TradeId!=trade||s.BindingId!=binding||s.Quantity!=quantity)||
            sides.Select(s=>s.MembershipOrdinal).Distinct().Count()!=sides.Length||sides.Select(s=>s.SideId).Distinct().Count()!=sides.Length)
            throw new MarketDataException("payment_state","Select distinct complete cash memberships for this exact held occurrence and quantity.");
        TradeId=trade;BindingId=binding;Quantity=quantity;Sides=sides;
    }
    internal PaymentSideState For(SidePaymentSchedule schedule)=>Sides.SingleOrDefault(s=>s.SideId==schedule.SideId)??
        throw new MarketDataException("payment_state","Every valued cash side requires its explicit cash state.");
    internal void Require(Side side)
    {
        var selected=Sides.SingleOrDefault(s=>s.SideId==side.Id)??throw new MarketDataException("payment_state","Every valued cash side requires its explicit cash state.");
        selected.Require(side);
    }
}

public sealed partial class SideValuationContext
{
    internal IBindingValuationState? BindingState { get; private set; }
    internal SideValuationContext WithBindingState(IBindingValuationState? state)
    {var copy=(SideValuationContext)MemberwiseClone();copy.BindingState=state;return copy;}
    internal static SideValuation EvaluatePaymentSide(SideValuationContext? context,Side side,SideSchedule schedule)
    {
        if(context is null)throw new MarketDataException("side_context","A side valuation context is required.");
        if(context.BindingState is CashflowBindingValuation state)state.Require(side);
        return schedule.Evaluate(context);
    }
}
