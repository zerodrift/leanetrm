using System.Collections.Immutable;
using OpenEtrm.MarketData.Physical;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// One recorded allocation of payment Id to a retained cash row (payment/{Occurrence}) of member Slot,
// in the held binding's signed cash terms. A payment and an occurrence identify one allocation.
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
            payments.Select(p=>(p.Id,p.Occurrence)).Distinct().Count()!=payments.Length||
            payments.GroupBy(p=>p.Id).Any(g=>g.Select(p=>(p.Slot,p.PaidAt,p.KnownAt)).Distinct().Count()!=1))
            throw new MarketDataException("payment_state","Select the whole-day accrual policy, an exact held binding and quantity, and distinct identified payment allocations.");
        TradeId=tradeId;BindingId=bindingId;Quantity=quantity;Payments=payments;
    }
    // Paid is only ever recorded against cash that has started to accrue: there is no prepayment.
    // Returns the visible paid total and the latest visible payment instant.
    internal (ExactMoney Total,DateTimeOffset? Last) Paid(int slot,int occurrence,MarketSnapshot snapshot,ExactMoney accrued,bool accruing)
    {
        var total=ExactMoney.Parse("0",accrued.Currency);DateTimeOffset? last=null;
        foreach(var payment in Payments.Where(p=>p.Slot==slot&&p.Occurrence==occurrence&&p.PaidAt<=snapshot.ValuationAt&&p.KnownAt<=snapshot.Knowledge!.KnownAt))
        {
            if(!accruing)throw new MarketDataException("payment_state","A payment requires at least one ended gas day of its cash occurrence.");
            CashAllocationBalance.RequireDirection(accrued,payment.Amount);total=total.Add(payment.Amount);
            if(last is null||payment.PaidAt>last)last=payment.PaidAt;
        }
        return (total,last);
    }
    private static bool Supported(DateTimeOffset at)=>at!=default&&TradingInstant.IsSupported(at);
}

public enum DeliveryState { NotStarted=1,InProgress,Ended }
public enum SettlementState { Remaining=1,AccruedUnpaid,PartPaid,Paid,Overpaid,NoAmountDue }
public enum LifecycleMeasure { Unrealized=1,RealizedUnsettled }
// Settlement is null on physical rows: delivery is not cash and is never settled by a date.
// DueAt, Expected (undiscounted cash still to be paid: amount less visible payments) and PaidAt are cash-only.
public sealed record PhysicalLifecycleRow(int Slot,int Occurrence,DateOnly DeliveryFrom,DateOnly DeliveryTo,DeliveryState Delivery,SettlementState? Settlement,
    ExactMoney Accrued,ExactMoney Paid,ExactMoney Outstanding,ExactMoney Unrealized,ExactMoney RealizedUnsettled,string QuantityBasis,
    DateTimeOffset? DueAt=null,ExactMoney? Expected=null,DateTimeOffset? PaidAt=null)
{
    // The P&L explain measures the row contributes to: a day not yet ended is unrealized (remaining delivery MTM); cash
    // accrued on ended, fixed days and not yet paid is realized unsettled. A fully paid, ended day is history only and contributes to neither.
    public ImmutableArray<LifecycleMeasure> Measures=>[
        ..Delivery!=DeliveryState.Ended?[LifecycleMeasure.Unrealized]:Array.Empty<LifecycleMeasure>(),
        ..Settlement is SettlementState.AccruedUnpaid or SettlementState.PartPaid or SettlementState.Overpaid?[LifecycleMeasure.RealizedUnsettled]:Array.Empty<LifecycleMeasure>()];
}
public sealed record ExpectedCashFlow(DateTimeOffset DueAt,ExactMoney Amount);
public sealed record PhysicalLifecycleValue(ExactMoney Unrealized,ExactMoney RealizedUnsettled,ExactMoney Total,ImmutableArray<PhysicalLifecycleRow> Rows)
{
    // Undiscounted cash still expected, by due date. It is shown beside the measures and never added to them.
    public ImmutableArray<ExpectedCashFlow> ExpectedCash=>[..Rows.Where(r=>r.DueAt is not null).GroupBy(r=>r.DueAt!.Value).OrderBy(g=>g.Key)
        .Select(g=>new ExpectedCashFlow(g.Key,g.Select(r=>r.Expected!).Aggregate((a,b)=>a.Add(b))))
        .Where(f=>f.Amount.CompareTo(ExactMoney.Parse("0",f.Amount.Currency))!=0)];
}

// A member's evaluation and the map from its unscaled amounts to signed held amounts.
public sealed record LifecycleMember(BookedSideProgram Program,BookedSideEvaluation Evaluation,bool Physical,Func<ExactMoney,ExactMoney> Held);

// Started delivery, whole gas days: ended days leave the unrealized measure; their cash accrues on contract quantity
// and stays realized unsettled until paid, discounted to its payment date until due.
public static class PhysicalLifecycle
{
    public const string EstimatedQuantity="contract_estimate_allocation_pending_v1",ContractQuantity="contractual_quantity_v1";
    // The named value components a started-delivery position reports to risk; they sum to its value.
    public const string UnrealizedComponent="unrealized",RealizedUnsettledComponent="realizedUnsettled";
    // Refusals that belong to one held trade's terms or recorded cash, not to the shared market: a reader reports them on
    // that trade's row and keeps valuing the others.
    public static bool TradeRefusal(string code)=>code is "physical_lifecycle_unsupported" or "physical_lifecycle_quantity" or
        "side_due_leg_unresolved" or "payment_state" or "settlement_conservation";

    // Missing lifecycle wiring is a system defect, not a trade's refusal: it fails the read (TradeRefusal excludes it).
    public const string ContextRefusal="physical_lifecycle_context";

    // Owner decision (D360): a whole gas-day physical program prices each gas day not yet ended at its location curve's own mark.
    // A published fixing locks only the payment price, so it neither prices nor backs up the physical leg.
    public static bool ReadsDeliveryMarks(BookedSideProgram program)=>program.Document.Family=="gas"&&program.Document.Instructions.Any(i=>
        i.Output.Operation=="output_guard"&&i.Output.Parameters.GetValueOrDefault("lifecycle")==ScheduleLifecycles.WholeGasDayDelivery);
    // The curve inputs a set of retained programs demands at the instant from its dated lookups. A lookup that only a whole
    // gas-day physical program reads is declared by its delivery mark, for each gas day not yet ended; every other lookup
    // keeps its ordinary fixing-or-projection selection.
    public static ImmutableArray<CurveRequest> PriceDependencies(IEnumerable<BookedSideProgram> programs,IReadOnlyCollection<IndexPriceLookup> lookups,MarketSnapshot snapshot)
    {
        var held=programs.ToArray();
        if(!held.Any(ReadsDeliveryMarks))return [..lookups.Select(lookup=>PeriodPriceSelector.ProjectionAt(lookup,snapshot)).OfType<CurveRequest>().Distinct()];
        // Each price row matches only the lookups of its own market and observation period.
        var index=lookups.ToLookup(l=>(l.Market,l.Contract.DeliveryFrom,l.Contract.DeliveryTo));
        IEnumerable<IndexPriceLookup> Read(BookedSideProgram program)=>program.Document.Instructions
            .Where(i=>i.Output.Grid is null&&CurvePriceScheduleSource.IsLookup(i.Output.Operation))
            .SelectMany(i=>ScheduleMarketSources.DatedCandidates(i,i.Bindings.Where(b=>b.Port=="market").Select(b=>b.Market).FirstOrDefault() is {} market&&
                i.Output.ObservationFrom is {} from&&i.Output.ObservationTo is {} to?index[(market,from,to)]:[],program.Document.Instructions.Any(a=>a.Output.Operation=="price_application"&&a.Output.PaymentOrdinal==i.Output.PaymentOrdinal&&a.Output.Parameters.GetValueOrDefault("mapping")==FloatingSide.DeliveryIndexImplementation)));
        var marked=held.Where(ReadsDeliveryMarks).SelectMany(Read).ToHashSet();
        var ordinary=held.Where(p=>!ReadsDeliveryMarks(p)).SelectMany(Read).ToHashSet();
        return [..lookups.Where(lookup=>!marked.Contains(lookup)||ordinary.Contains(lookup)).Select(lookup=>PeriodPriceSelector.ProjectionAt(lookup,snapshot)).OfType<CurveRequest>()
            .Concat(held.Where(ReadsDeliveryMarks).SelectMany(program=>
            {
                var open=PhysicalDeliverySchedule.ReadLiteralQuantities(program,ExactRatio.One).Where(day=>day.End>snapshot.ValuationAt).Select(day=>day.OperatingDate).ToHashSet();
                return Read(program).Where(lookup=>open.Any(day=>lookup.ForecastDelivery.From<=day&&day<lookup.ForecastDelivery.To))
                    .Select(lookup=>PeriodPriceSelector.DeliveryMarkAt(lookup,snapshot)).OfType<CurveRequest>();
            })).Distinct()];
    }

    // Valued without its lifecycle state, a started-delivery program would drop its ended gas days and due cash.
    // It values so only before its first gas day starts and while its cash is not yet due.
    public static void RequireUnstarted(IEnumerable<BookedSideProgram> programs,DateTimeOffset at)
    {
        foreach(var document in programs.Select(p=>p.Document))
            foreach(var row in document.Instructions.Select(i=>i.Output).Where(o=>o.Operation=="output_guard"))
                if(row.Parameters.GetValueOrDefault("lifecycle") switch
                    {ScheduleLifecycles.WholeGasDayDelivery=>document.Grids[row.Grid!.Value].Interval(0).Start<=at,ScheduleLifecycles.AccruedPayment=>row.ValueAt<=at,_=>false})
                    throw new MarketDataException("gas_delivery_lifecycle","A started gas delivery is valued only with its held lifecycle state.");
    }

    public static PhysicalLifecycleValue Evaluate(ImmutableArray<LifecycleMember> members,PhysicalCashState state,MarketSnapshot snapshot,ReferenceKey currency)
    {
        if(snapshot.Knowledge is null)throw new MarketDataException(ContextRefusal,"Payment visibility requires an explicit resolved knowledge cutoff.");
        var at=snapshot.ValuationAt;var zero=ExactMoney.Parse("0",currency);
        DeliveryState Delivery(DateTimeOffset start,DateTimeOffset end)=>end<=at?DeliveryState.Ended:start<=at?DeliveryState.InProgress:DeliveryState.NotStarted;
        // Each operating day's state across the physical members. Members on different gas-day clocks may deliver the
        // same operating day; a cash day covering it is defined only while every member agrees on that day's state.
        var days=new Dictionary<DateOnly,DeliveryState>();
        var quantities=new Dictionary<DateOnly,ExactQuantity>();
        foreach(var member in members.Where(m=>m.Physical))
        {
            var seen=new HashSet<DateOnly>();
            foreach(var day in PhysicalDeliverySchedule.ReadLiteralQuantities(member.Program,ExactRatio.One))
            {
                var delivered=Delivery(day.Start,day.End);
                if(days.TryGetValue(day.OperatingDate,out var known)&&known!=delivered||!seen.Add(day.OperatingDate))
                    throw Unsupported("Every physical member must agree whether each operating day has started or ended at the valuation instant.");
                days[day.OperatingDate]=delivered;
                quantities[day.OperatingDate]=quantities.TryGetValue(day.OperatingDate,out var sum)?sum.Add(day.Quantity):day.Quantity;
            }
        }
        ExactMoney Held(LifecycleMember member,ScheduleValueColumn column)=>column.Presence[0]==ScheduleCellPresence.Resolved?
            member.Held(ExactMoney.FromFraction(column.Values[0].Numerator,column.Values[0].Denominator,currency)):
            throw Unsupported("A lifecycle amount requires its resolved evaluated value.");
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
                    rows.Add(new(slot,occurrence,interval.OperatingDate,interval.OperatingDate.AddDays(1),delivery,null,
                        zero,zero,zero,delivery==DeliveryState.Ended?zero:Held(member,contribution.PresentValue),zero,
                        delivery==DeliveryState.Ended?EstimatedQuantity:ContractQuantity));
                    continue;
                }
                if(row.Grid is not null||row.DeliveryFrom is not {} from||row.DeliveryTo is not {} to||row.ValueAt is not {} valueAt||from>=to)
                    throw Unsupported("Cash lifecycle rows require their retained dated delivery term.");
                var covered=Enumerable.Range(0,to.DayNumber-from.DayNumber).Select(from.AddDays).ToArray();
                if(covered.Any(day=>!days.ContainsKey(day)))throw Unsupported("Every cash day requires its retained physical gas day.");
                // Pro rata by day is exact only when the total physical quantity of every covered day is equal.
                if(covered.Select(day=>quantities[day]).Distinct().Count()!=1)
                    throw new MarketDataException("physical_lifecycle_quantity","Whole-day accrual of a multi-day payment requires equal daily contract quantities.");
                var ended=covered.Count(day=>days[day]==DeliveryState.Ended);var started=covered.Any(day=>days[day]!=DeliveryState.NotStarted);
                var due=valueAt<=at;
                var prices=contribution.Amount.States[0].Prices;
                if(ended>0&&(prices==SchedulePriceFacts.None||(prices&~(SchedulePriceFacts.Fixed|SchedulePriceFacts.Actual|SchedulePriceFacts.PublishedFixing))!=0))
                    throw new MarketDataException("side_due_leg_unresolved","Accrued cash requires its fixed, actual or published price.");
                if(due&&ended<covered.Length)throw Unsupported("A due payment requires every gas day it covers to have ended.");
                var amount=Held(member,contribution.Amount);
                var accrued=amount.Scale(new SignedRatio(ended,covered.Length));
                var(paid,paidAt)=state.Paid(slot,occurrence,snapshot,accrued,ended>0);var outstanding=accrued.Subtract(paid);
                if(due)RequireOutside(contribution);
                // Not yet due: the row's own discount factor, PV over amount from this one evaluation.
                var value=due?zero:Held(member,contribution.PresentValue);
                var outstandingPv=due?outstanding:amount.CompareTo(zero)==0?zero:outstanding.Scale(value.Over(amount));
                rows.Add(new(slot,occurrence,from,to,ended==covered.Length?DeliveryState.Ended:started?DeliveryState.InProgress:DeliveryState.NotStarted,
                    Settle(ended>0,accrued,paid),accrued,paid,outstanding,value.Scale(new SignedRatio(covered.Length-ended,covered.Length)),outstandingPv,
                    ended>0?EstimatedQuantity:ContractQuantity,valueAt,amount.Subtract(paid),paidAt));
            }
        }
        var result=rows.ToImmutable();
        var unrealized=result.Aggregate(zero,(sum,r)=>sum.Add(r.Unrealized));var unsettled=result.Aggregate(zero,(sum,r)=>sum.Add(r.RealizedUnsettled));
        return new(unrealized,unsettled,unrealized.Add(unsettled),result);
    }
    // A row with no ended day still has value to come; otherwise visible payments against accrued.
    // Paid only when visible payments cover a nonzero accrual.
    private static SettlementState Settle(bool accruing,ExactMoney accrued,ExactMoney paid)
    {
        if(!accruing)return SettlementState.Remaining;
        var zero=ExactMoney.Parse("0",accrued.Currency);var sign=accrued.CompareTo(zero);
        if(paid.CompareTo(zero)==0)return sign==0?SettlementState.NoAmountDue:SettlementState.AccruedUnpaid;
        return (paid.CompareTo(accrued)*sign) switch{<0=>SettlementState.PartPaid,0=>SettlementState.Paid,_=>SettlementState.Overpaid};
    }
    private static MarketDataException Unsupported(string message)=>new("physical_lifecycle_unsupported",message);
}
