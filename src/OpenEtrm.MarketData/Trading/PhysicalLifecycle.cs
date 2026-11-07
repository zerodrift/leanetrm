using System.Collections.Immutable;
using OpenEtrm.MarketData.Physical;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// One recorded allocation of a payment to a retained cash row (payment/{Occurrence}) of member Slot,
// in the held binding's signed cash terms.
public sealed record CashOccurrencePayment(Guid Id,int Slot,int Occurrence,DateTimeOffset PaidAt,DateTimeOffset KnownAt,ExactMoney Amount);

// Operation-owned payment facts for one held physical binding. Visibility is resolved per
// valuation from paid and knowledge time; nothing here is part of economic identity.
public sealed class PhysicalCashState:IBindingValuationState
{
    public const string Policy="physical_whole_day_accrual_v1";
    public Guid TradeId { get; }
    public Guid BindingId { get; }
    public Quantity Quantity { get; }
    public ImmutableArray<CashOccurrencePayment> Payments { get; }

    public PhysicalCashState(Guid tradeId,Guid bindingId,Quantity quantity,ImmutableArray<CashOccurrencePayment> payments,string policy)
    {
        if(policy!=Policy||tradeId==Guid.Empty||bindingId==Guid.Empty||quantity is null||quantity.Amount==default||payments.IsDefault||
            payments.Any(p=>p is null||p.Id==Guid.Empty||p.Slot<0||p.Occurrence<0||p.Amount is null||!Supported(p.PaidAt)||!Supported(p.KnownAt))||
            payments.Select(p=>p.Id).Distinct().Count()!=payments.Length)
            throw new MarketDataException("payment_state","Select the whole-day accrual policy, an exact held binding and quantity, and distinct identified payment allocations.");
        TradeId=tradeId;BindingId=bindingId;Quantity=quantity;Payments=payments;
    }
    // Paid is only ever recorded against cash that has started to accrue: there is no prepayment.
    internal ExactMoney Paid(int slot,int occurrence,MarketSnapshot snapshot,ExactMoney accrued,bool accruing)
    {
        var total=ExactMoney.Parse("0",accrued.Currency);
        foreach(var payment in Payments.Where(p=>p.Slot==slot&&p.Occurrence==occurrence&&p.PaidAt<=snapshot.ValuationAt&&p.KnownAt<=snapshot.Knowledge!.KnownAt))
        {
            if(!accruing)throw new MarketDataException("payment_state","A payment requires at least one ended gas day of its cash occurrence.");
            CashAllocationBalance.RequireDirection(accrued,payment.Amount);total=total.Add(payment.Amount);
        }
        return total;
    }
    private static bool Supported(DateTimeOffset at)=>at!=default&&TradingInstant.IsSupported(at);
}

public enum DeliveryState { NotStarted=1,InProgress,Ended }
public enum SettlementState { Remaining=1,AccruedUnpaid,PartPaid,Paid,Overpaid }
public enum LifecycleMeasure { RemainingDeliveryMtm=1,OutstandingSettlement }
public sealed record PhysicalLifecycleRow(int Slot,int Occurrence,DateOnly DeliveryFrom,DateOnly DeliveryTo,DeliveryState Delivery,SettlementState Settlement,
    ExactMoney Accrued,ExactMoney Paid,ExactMoney Outstanding,ExactMoney RemainingPv,ExactMoney OutstandingPv,string QuantityBasis);
public sealed record PhysicalLifecycleValue(ExactMoney RemainingDeliveryMtm,ExactMoney OutstandingSettlement,ExactMoney Total,ImmutableArray<PhysicalLifecycleRow> Rows);

// A member's evaluation and the map from its unscaled amounts to signed held amounts.
internal sealed record LifecycleMember(BookedSideProgram Program,BookedSideEvaluation Evaluation,bool Physical,Func<ExactMoney,ExactMoney> Held);

// Started delivery, whole gas days: ended days leave the delivery measure; their cash accrues
// on contract quantity and stays outstanding until paid, discounted to its payment date until due.
public static class PhysicalLifecycle
{
    public const string EstimatedQuantity="contract_estimate_allocation_pending_v1",ContractQuantity="contractual_quantity_v1";

    internal static PhysicalLifecycleValue Evaluate(ImmutableArray<LifecycleMember> members,PhysicalCashState state,MarketSnapshot snapshot,ReferenceKey currency)
    {
        if(snapshot.Knowledge is null)throw new MarketDataException("payment_state","Payment visibility requires an explicit resolved knowledge cutoff.");
        var at=snapshot.ValuationAt;var zero=ExactMoney.Parse("0",currency);
        var days=new Dictionary<DateOnly,(DateTimeOffset Start,DateTimeOffset End)>();
        var quantities=new List<Dictionary<DateOnly,ExactQuantity>>();
        foreach(var member in members.Where(m=>m.Physical))
        {
            var daily=new Dictionary<DateOnly,ExactQuantity>();
            foreach(var day in PhysicalDeliverySchedule.ReadLiteralQuantities(member.Program,ExactRatio.One))
            {
                if(days.TryGetValue(day.OperatingDate,out var known)&&known!=(day.Start,day.End)||!daily.TryAdd(day.OperatingDate,day.Quantity))
                    throw Unsupported("Every physical member must share one whole gas-day boundary per operating day.");
                days[day.OperatingDate]=(day.Start,day.End);
            }
            quantities.Add(daily);
        }
        DeliveryState Delivery(DateTimeOffset start,DateTimeOffset end)=>end<=at?DeliveryState.Ended:start<=at?DeliveryState.InProgress:DeliveryState.NotStarted;
        ExactMoney Held(LifecycleMember member,ScheduleValueColumn column)=>member.Held(ExactMoney.FromFraction(column.Values[0].Numerator,column.Values[0].Denominator,currency));
        static void RequireOutside(ScheduleContribution contribution)
        {
            if(contribution.PresentValue.Presence[0]!=ScheduleCellPresence.OutsideMeasure)
                throw Unsupported("An ended day or due payment must already be outside the evaluated measure.");
        }
        var rows=ImmutableArray.CreateBuilder<PhysicalLifecycleRow>();
        for(var slot=0;slot<members.Length;slot++)
        {
            var member=members[slot];var document=member.Program.Document;
            foreach(var contribution in member.Evaluation.Contributions)
            {
                var row=document.Instructions[contribution.Instruction].Output;
                if(row.PaymentOrdinal is not {} occurrence||contribution.Amount.Values.Length!=1)
                    throw Unsupported("Lifecycle rows require one retained occurrence per contribution.");
                if(member.Physical)
                {
                    if(row.Grid is not {} grid||document.Grids[grid].CellCount!=1)throw Unsupported("Physical lifecycle rows require whole gas days.");
                    var interval=document.Grids[grid].Interval(0);var delivery=Delivery(interval.Start,interval.End);
                    if(delivery==DeliveryState.Ended)RequireOutside(contribution);
                    rows.Add(new(slot,occurrence,interval.OperatingDate,interval.OperatingDate.AddDays(1),delivery,Settle(delivery==DeliveryState.Ended,zero,zero),
                        zero,zero,zero,delivery==DeliveryState.Ended?zero:Held(member,contribution.PresentValue),zero,
                        delivery==DeliveryState.Ended?EstimatedQuantity:ContractQuantity));
                    continue;
                }
                if(row.Grid is not null||row.DeliveryFrom is not {} from||row.DeliveryTo is not {} to||row.ValueAt is not {} valueAt||from>=to)
                    throw Unsupported("Cash lifecycle rows require their retained dated delivery term.");
                var covered=Enumerable.Range(0,to.DayNumber-from.DayNumber).Select(from.AddDays).ToArray();
                if(covered.Any(day=>!days.ContainsKey(day)))throw Unsupported("Every cash day requires its retained physical gas day.");
                if(covered.Length>1&&quantities.Any(daily=>covered.Any(daily.ContainsKey)&&covered.Select(day=>daily.GetValueOrDefault(day)).Distinct().Count()!=1))
                    throw new MarketDataException("physical_lifecycle_quantity","Whole-day accrual of a multi-day payment requires equal daily contract quantities.");
                var ended=covered.Count(day=>days[day].End<=at);var started=covered.Any(day=>days[day].Start<=at);
                var due=valueAt<=at;
                if(ended>0&&(contribution.Amount.States[0].Prices&~(SchedulePriceFacts.Fixed|SchedulePriceFacts.Actual|SchedulePriceFacts.PublishedFixing))!=0)
                    throw new MarketDataException("side_due_leg_unresolved","Accrued cash requires its fixed, actual or published price.");
                if(due&&ended<covered.Length)throw Unsupported("A due payment requires every gas day it covers to have ended.");
                var amount=Held(member,contribution.Amount);
                var accrued=amount.Scale(new SignedRatio(ended,covered.Length));
                var paid=state.Paid(slot,occurrence,snapshot,accrued,ended>0);var outstanding=accrued.Subtract(paid);
                if(due)RequireOutside(contribution);
                // Not yet due: the row's own discount factor, PV over amount from this one evaluation.
                var value=due?zero:Held(member,contribution.PresentValue);
                var outstandingPv=due?outstanding:amount.CompareTo(zero)==0?zero:outstanding.Scale(value.Over(amount));
                rows.Add(new(slot,occurrence,from,to,ended==covered.Length?DeliveryState.Ended:started?DeliveryState.InProgress:DeliveryState.NotStarted,
                    Settle(ended>0,accrued,paid),accrued,paid,outstanding,value.Scale(new SignedRatio(covered.Length-ended,covered.Length)),outstandingPv,
                    ended>0?EstimatedQuantity:ContractQuantity));
            }
        }
        var result=rows.ToImmutable();
        var remaining=result.Aggregate(zero,(sum,r)=>sum.Add(r.RemainingPv));var settlement=result.Aggregate(zero,(sum,r)=>sum.Add(r.OutstandingPv));
        return new(remaining,settlement,remaining.Add(settlement),result);
    }
    // A row with nothing accrued or ended still has value to come; otherwise paid against accrued.
    private static SettlementState Settle(bool accruing,ExactMoney accrued,ExactMoney paid)
    {
        if(!accruing)return SettlementState.Remaining;
        var zero=ExactMoney.Parse("0",accrued.Currency);var sign=accrued.CompareTo(zero);
        if(paid.CompareTo(zero)==0)return sign==0?SettlementState.Paid:SettlementState.AccruedUnpaid;
        return (paid.CompareTo(accrued)*sign) switch{<0=>SettlementState.PartPaid,0=>SettlementState.Paid,_=>SettlementState.Overpaid};
    }
    private static MarketDataException Unsupported(string message)=>new("physical_lifecycle_unsupported",message);
}
