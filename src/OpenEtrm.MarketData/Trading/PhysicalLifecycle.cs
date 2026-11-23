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
    public Guid? BookId { get; }
    public Quantity Quantity { get; }
    public ImmutableArray<CashOccurrencePayment> Payments { get; }
    readonly PhysicalDeliveryQuantities? delivery;
    public string? QuantityPolicy { get; }
    public ImmutableArray<HeldDayQuantity> Quantities=>delivery?.Days??[];
    public ImmutableArray<HeldQuantityMembership> QuantityMemberships=>delivery?.Memberships??[];
    public bool Follows(int slot)=>delivery?.Follows(slot)??false;
    public HeldDayQuantity Day(int slot,DateOnly day)=>delivery?.Day(slot,day)??
        throw PhysicalDeliveryQuantities.Invalid("This binding has no selected delivery quantity policy.");
    public void ValidateQuantitySchedules(IReadOnlyList<(BookedSideProgram Program,bool Physical,ExactRatio Scale)> programs)=>delivery?.ValidateSchedules(programs);

    public PhysicalCashState(Guid tradeId,Guid bindingId,Quantity quantity,Guid? bookId,ImmutableArray<CashOccurrencePayment> payments,string policy,
        ImmutableArray<HeldDayQuantity> quantities=default,string? quantityPolicy=null,ImmutableArray<HeldQuantityMembership> quantityMemberships=default)
    {
        if(bookId==Guid.Empty||policy!=Policy||tradeId==Guid.Empty||bindingId==Guid.Empty||quantity is null||quantity.Amount==default||payments.IsDefault||
            payments.Any(p=>p is null||p.Id==Guid.Empty||p.Slot<0||p.Occurrence<0||p.Amount is null||!Supported(p.PaidAt)||!Supported(p.KnownAt))||
            payments.Select(p=>(p.Id,p.Occurrence)).Distinct().Count()!=payments.Length||
            payments.GroupBy(p=>p.Id).Any(g=>g.Select(p=>(p.Slot,p.PaidAt,p.KnownAt)).Distinct().Count()!=1))
            throw new MarketDataException("payment_state","Select the whole-day accrual policy, an exact held binding and quantity, and distinct identified payment allocations.");
        TradeId=tradeId;BindingId=bindingId;BookId=bookId;Quantity=quantity;Payments=payments;
        if(quantityPolicy is null)
        {
            if(!quantities.IsDefaultOrEmpty||!quantityMemberships.IsDefaultOrEmpty)
                throw PhysicalDeliveryQuantities.Invalid("Selected quantities require their explicit saved membership policy.");
        }
        else delivery=new(quantities,quantityMemberships,quantity.Specification,quantityPolicy);
        QuantityPolicy=quantityPolicy;
    }
    // Paid is only ever recorded against cash that has started to accrue: there is no prepayment.
    // Returns the visible paid total and the latest visible payment instant.
    internal (ExactMoney Total,DateTimeOffset? Last) Paid(int slot,int occurrence,MarketSnapshot snapshot,ExactMoney accrued,bool accruing,ExactMoney? contractAmount=null)
    {
        var total=ExactMoney.Parse("0",accrued.Currency);DateTimeOffset? last=null;
        foreach(var payment in Payments.Where(p=>p.Slot==slot&&p.Occurrence==occurrence&&p.PaidAt<=snapshot.ValuationAt&&p.KnownAt<=snapshot.Knowledge!.KnownAt))
        {
            if(!accruing)throw new MarketDataException("payment_state","A payment requires at least one ended gas day of its cash occurrence.");
            // A selected quantity can correct accrued cash to zero. Its retained price and
            // contractual amount still establish payment direction; cash is never rescaled.
            var basis=Follows(slot)&&accrued.CompareTo(ExactMoney.Parse("0",accrued.Currency))==0?contractAmount??accrued:accrued;
            CashAllocationBalance.RequireDirection(basis,payment.Amount);total=total.Add(payment.Amount);
            if(last is null||payment.PaidAt>last)last=payment.PaidAt;
        }
        return (total,last);
    }
    private static bool Supported(DateTimeOffset at)=>at!=default&&TradingInstant.IsSupported(at);
}

public enum DeliveryState { NotStarted=1,InProgress,Ended }
public enum SettlementState { Remaining=1,AccruedUnpaid,PartPaid,Paid,Overpaid,NoAmountDue,Projected }
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
        ..Delivery!=DeliveryState.Ended||Settlement==SettlementState.Projected?[LifecycleMeasure.Unrealized]:Array.Empty<LifecycleMeasure>(),
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

// Instrument-owned connections carry only exact physical day states and
// quantities. They contain no price, cash value, Side or Instrument callback.
internal sealed record PhysicalLifecycleGeometry(ImmutableDictionary<DateOnly,DeliveryState> Days,
    ImmutableDictionary<DateOnly,ExactQuantity> Quantities);

// Started delivery, whole gas days: ended days leave the unrealized measure; their cash accrues on contract quantity
// and stays realized unsettled until paid, discounted to its payment date until due.
public static class PhysicalLifecycle
{
    public const string EstimatedQuantity="contract_estimate_allocation_pending_v1",ContractQuantity="contractual_quantity_v1";
    public const string SelectedQuantityPolicy="selected_delivery_quantity_v1";
    public const string AllocatedFinalQuantity="allocated_final_v1",AllocatedProvisionalQuantity="allocated_provisional_v1",ScheduledQuantity="scheduled_v1";
    // The named value components a started-delivery position reports to risk; they sum to its value.
    public const string UnrealizedComponent="unrealized",RealizedUnsettledComponent="realizedUnsettled";
    // Refusals that belong to one held trade's terms or recorded cash, not to the shared market: a reader reports them on
    // that trade's row and keeps valuing the others.
    public static bool TradeRefusal(string code)=>code is "physical_lifecycle_unsupported" or "physical_lifecycle_quantity" or
        "side_due_leg_unresolved" or "payment_state" or "settlement_conservation" or "gas_quantity_selection";

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
        IEnumerable<IndexPriceLookup> Read(BookedSideProgram program)
        {
            var availabilityPayments=program.Document.Instructions.Where(i=>i.Output.Operation=="price_application"&&
                i.Output.Parameters.GetValueOrDefault("mapping")==FloatingSide.DeliveryIndexImplementation).Select(i=>i.Output.PaymentOrdinal).ToHashSet();
            return program.Document.Instructions
                .Where(i=>i.Output.Grid is null&&CurvePriceScheduleSource.IsLookup(i.Output.Operation))
                .SelectMany(i=>ScheduleMarketSources.DatedCandidates(i,i.Bindings.Where(b=>b.Port=="market").Select(b=>b.Market).FirstOrDefault() is {} market&&
                    i.Output.ObservationFrom is {} from&&i.Output.ObservationTo is {} to?index[(market,from,to)]:[],availabilityPayments.Contains(i.Output.PaymentOrdinal)));
        }
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
                    {ScheduleLifecycles.WholeGasDayDelivery=>document.Grids[row.Grid!.Value].Interval(0).Start<=at,ScheduleLifecycles.AccruedPayment=>PaymentDatePolicy.IsOverdue(row.ValueAt!.Value,at),_=>false})
                    throw new MarketDataException("gas_delivery_lifecycle","A started gas delivery is valued only with its held lifecycle state.");
    }

    public static PhysicalLifecycleValue Evaluate(ImmutableArray<LifecycleMember> members,PhysicalCashState state,MarketSnapshot snapshot,ReferenceKey currency)
    {
        var geometry=PrepareGeometry(members.Where(m=>m.Physical).Select(m=>m.Program),snapshot);
        return EvaluateConnected(members,state,snapshot,currency,geometry,0);
    }
    internal static PhysicalLifecycleGeometry PrepareGeometry(IEnumerable<BookedSideProgram> programs,MarketSnapshot snapshot)
    {
        var at=snapshot.ValuationAt;
        DeliveryState Delivery(DateTimeOffset start,DateTimeOffset end)=>end<=at?DeliveryState.Ended:start<=at?DeliveryState.InProgress:DeliveryState.NotStarted;
        var days=new Dictionary<DateOnly,DeliveryState>();
        var quantities=new Dictionary<DateOnly,ExactQuantity>();
        foreach(var program in programs)
        {
            var seen=new HashSet<DateOnly>();
            foreach(var day in PhysicalDeliverySchedule.ReadLiteralQuantities(program,ExactRatio.One))
            {
                var delivered=Delivery(day.Start,day.End);
                if(days.TryGetValue(day.OperatingDate,out var known)&&known!=delivered||!seen.Add(day.OperatingDate))
                    throw Unsupported("Every physical member must agree whether each operating day has started or ended at the valuation instant.");
                days[day.OperatingDate]=delivered;
                quantities[day.OperatingDate]=quantities.TryGetValue(day.OperatingDate,out var sum)?sum.Add(day.Quantity):day.Quantity;
            }
        }
        return new(days.ToImmutableDictionary(),quantities.ToImmutableDictionary());
    }
    internal static PhysicalLifecycleValue EvaluateSide(BookedSideProgram program,BookedSideEvaluation evaluation,bool physical,int slot,
        SignedRatio heldFactor,PhysicalLifecycleGeometry geometry,PhysicalCashState state,MarketSnapshot snapshot,ReferenceKey currency)
    {
        if(heldFactor.Numerator.IsZero)throw new MarketDataException(ContextRefusal,"A held Side requires nonzero exact quantity normalization.");
        var held=EvaluateConnected([new(program,evaluation,physical,value=>value.Scale(heldFactor))],state,snapshot,currency,geometry,slot);
        return Scale(held,new SignedRatio(heldFactor.Numerator.Sign*heldFactor.Denominator,System.Numerics.BigInteger.Abs(heldFactor.Numerator)));
    }
    internal static PhysicalLifecycleValue Scale(PhysicalLifecycleValue value,SignedRatio factor)=>new(
        value.Unrealized.Scale(factor),value.RealizedUnsettled.Scale(factor),value.Total.Scale(factor),
        [..value.Rows.Select(row=>row with{Accrued=row.Accrued.Scale(factor),Paid=row.Paid.Scale(factor),Outstanding=row.Outstanding.Scale(factor),
            Unrealized=row.Unrealized.Scale(factor),RealizedUnsettled=row.RealizedUnsettled.Scale(factor),Expected=row.Expected?.Scale(factor)})]);
    private static PhysicalLifecycleValue EvaluateConnected(ImmutableArray<LifecycleMember> members,PhysicalCashState state,MarketSnapshot snapshot,
        ReferenceKey currency,PhysicalLifecycleGeometry geometry,int firstSlot)
    {
        if(snapshot.Knowledge is null)throw new MarketDataException(ContextRefusal,"Payment visibility requires an explicit resolved knowledge cutoff.");
        var at=snapshot.ValuationAt;var zero=ExactMoney.Parse("0",currency);
        var days=geometry.Days;var quantities=geometry.Quantities;
        DeliveryState Delivery(DateTimeOffset start,DateTimeOffset end)=>end<=at?DeliveryState.Ended:start<=at?DeliveryState.InProgress:DeliveryState.NotStarted;
        ExactMoney Held(LifecycleMember member,ScheduleValueColumn column)=>column.Presence[0]==ScheduleCellPresence.Resolved?
            member.Held(ExactMoney.FromFraction(column.Values[0].Numerator,column.Values[0].Denominator,currency)):
            throw Unsupported("A lifecycle amount requires its resolved evaluated value.");
        static void RequireOutside(ScheduleContribution contribution)
        {
            if(contribution.PresentValue.Presence[0]!=ScheduleCellPresence.OutsideMeasure)
                throw Unsupported("An ended day or due payment must already be outside the evaluated measure.");
        }
        var rows=ImmutableArray.CreateBuilder<PhysicalLifecycleRow>();
        for(var index=0;index<members.Length;index++)
        {
            var slot=index+firstSlot;var member=members[index];var document=member.Program.Document;
            foreach(var contribution in member.Evaluation.Contributions)
            {
                var row=document.Instructions[contribution.Instruction].Output;
                if(row.PaymentOrdinal is not {} occurrence||contribution.Amount.Values.Length!=1)
                    throw Unsupported("Lifecycle rows require one retained occurrence per contribution.");
                if(member.Physical)
                {
                    if(row.Grid is not {} grid||document.Grids[grid].CellCount!=1)throw Unsupported("Physical lifecycle rows require whole gas days.");
                    var interval=document.Grids[grid].Interval(0);var delivery=Delivery(interval.Start,interval.End);
                    var selected=state.Follows(slot)?state.Day(slot,interval.OperatingDate):null;
                    var fraction=selected is null?new SignedRatio(1,1):DeliveryQuantityFractions.For([selected],_=>false).Total;
                    if(delivery==DeliveryState.Ended)RequireOutside(contribution);
                    rows.Add(new(slot,occurrence,interval.OperatingDate,interval.OperatingDate.AddDays(1),delivery,null,
                        zero,zero,zero,delivery==DeliveryState.Ended?zero:Held(member,contribution.PresentValue).Scale(fraction),zero,
                        selected?.Source??(delivery==DeliveryState.Ended?EstimatedQuantity:ContractQuantity)));
                    continue;
                }
                if(row.Grid is not null||row.DeliveryFrom is not {} from||row.DeliveryTo is not {} to||row.ValueAt is not {} valueAt||from>=to)
                    throw Unsupported("Cash lifecycle rows require their retained dated delivery term.");
                var covered=Enumerable.Range(0,to.DayNumber-from.DayNumber).Select(from.AddDays).ToArray();
                if(covered.Any(day=>!days.ContainsKey(day)))throw Unsupported("Every cash day requires its retained physical gas day.");
                // Pro rata by day is exact only when the total physical quantity of every covered day is equal.
                var follows=state.Follows(slot);
                if(!follows&&covered.Select(day=>quantities[day]).Distinct().Count()!=1)
                    throw new MarketDataException("physical_lifecycle_quantity","Whole-day accrual of a multi-day payment requires equal daily contract quantities.");
                var ended=covered.Count(day=>days[day]==DeliveryState.Ended);var started=covered.Any(day=>days[day]!=DeliveryState.NotStarted);
                var fractions=follows?DeliveryQuantityFractions.For(covered.Select(day=>state.Day(slot,day)),day=>days[day]==DeliveryState.Ended):
                    new DeliveryQuantityFractions(new(1,1),new(ended,covered.Length));
                var due=PaymentDatePolicy.IsOverdue(valueAt,at);
                var prices=contribution.Amount.States[0].Prices;
                const SchedulePriceFacts finalPrices=SchedulePriceFacts.Fixed|SchedulePriceFacts.Actual|SchedulePriceFacts.PublishedFixing;
                var fixedPrice=prices!=SchedulePriceFacts.None&&(prices&~finalPrices)==0;
                var projectedPrice=(prices&SchedulePriceFacts.Projected)!=0&&(prices&~(finalPrices|SchedulePriceFacts.Projected))==0;
                if(ended>0&&!fixedPrice&&(due||!projectedPrice))
                    throw new MarketDataException("side_due_leg_unresolved","Accrued cash requires its fixed, actual or published price.");
                if(due&&ended<covered.Length)throw Unsupported("A due payment requires every gas day it covers to have ended.");
                var contractAmount=Held(member,contribution.Amount);var amount=contractAmount.Scale(fractions.Total);
                if(ended>0&&!fixedPrice)
                {
                    // Delivery completion does not turn a forecast into earned
                    // cash. Until the required fixing, its projection keeps its market risk.
                    _=state.Paid(slot,occurrence,snapshot,zero,false);
                    rows.Add(new(slot,occurrence,from,to,ended==covered.Length?DeliveryState.Ended:DeliveryState.InProgress,
                        SettlementState.Projected,zero,zero,amount,Held(member,contribution.PresentValue).Scale(fractions.Total),zero,
                        follows?SelectedQuantityPolicy:EstimatedQuantity,valueAt,amount));
                    continue;
                }
                var accrued=contractAmount.Scale(fractions.Ended);
                var(paid,paidAt)=state.Paid(slot,occurrence,snapshot,accrued,ended>0,contractAmount);var outstanding=accrued.Subtract(paid);
                if(due)RequireOutside(contribution);
                // Not yet due: the row's own discount factor, PV over amount from this one evaluation.
                var value=due?zero:Held(member,contribution.PresentValue);
                var outstandingPv=due?outstanding:contractAmount.CompareTo(zero)==0?zero:outstanding.Scale(value.Over(contractAmount));
                rows.Add(new(slot,occurrence,from,to,ended==covered.Length?DeliveryState.Ended:started?DeliveryState.InProgress:DeliveryState.NotStarted,
                    Settle(ended>0,accrued,paid),accrued,paid,outstanding,value.Scale(fractions.Total-fractions.Ended),outstandingPv,
                    follows?SelectedQuantityPolicy:ended>0?EstimatedQuantity:ContractQuantity,valueAt,amount.Subtract(paid),paidAt));
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
        if(sign==0)return SettlementState.Overpaid;
        return (paid.CompareTo(accrued)*sign) switch{<0=>SettlementState.PartPaid,0=>SettlementState.Paid,_=>SettlementState.Overpaid};
    }
    private static MarketDataException Unsupported(string message)=>new("physical_lifecycle_unsupported",message);
}
