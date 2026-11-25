using System.Collections.Immutable;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Physical;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// One recorded allocation of payment Id to a retained cash row (payment/{Occurrence}) of member Slot,
// in the held binding's signed cash terms. A payment and an occurrence identify one allocation.
public sealed record CashOccurrencePayment(Guid Id,int Slot,int Occurrence,DateTimeOffset PaidAt,DateTimeOffset KnownAt,ExactMoney Amount);

// Operation-owned lifecycle facts for one held physical binding. Payment visibility uses
// paid and knowledge time. Supplied elections are already selected at the knowledge cutoff.
// These facts are independent of the instrument's economic definition.
public sealed class PhysicalCashState:IBindingValuationState
{
    public const string Policy="physical_whole_day_accrual_v1";
    public Guid TradeId { get; }
    public Guid BindingId { get; }
    public Guid? BookId { get; }
    public Quantity Quantity { get; }
    public ImmutableArray<CashOccurrencePayment> Payments { get; }
    public ImmutableArray<HeldDayQuantity> OwnerDays { get; }
    public ImmutableArray<ExerciseElection> Elections { get; }
    public ImmutableArray<SwingElection> SwingElections { get; }
    public DateTimeOffset? NextSwingElectionEffectiveAt { get; }
    public ImmutableArray<SlotBalances> Balances { get; }

    // Earliest known correction or withdrawal after the business instant used to select Elections.
    public DateTimeOffset? NextElectionEffectiveAt { get; }

    public PhysicalCashState(Guid tradeId,Guid bindingId,Quantity quantity,Guid? bookId,ImmutableArray<CashOccurrencePayment> payments,string policy,
        ImmutableArray<HeldDayQuantity> ownerDays=default,ImmutableArray<ExerciseElection> elections=default,DateTimeOffset? nextElectionEffectiveAt=null,
        ImmutableArray<SlotBalances> balances=default, ImmutableArray<SwingElection> swingElections=default, DateTimeOffset? nextSwingElectionEffectiveAt=null)
    {
        if(bookId==Guid.Empty||policy!=Policy||tradeId==Guid.Empty||bindingId==Guid.Empty||quantity is null||quantity.Amount==default||payments.IsDefault||
            payments.Any(p=>p is null||p.Id==Guid.Empty||p.Slot<0||p.Occurrence<0||p.Amount is null||!Supported(p.PaidAt)||!Supported(p.KnownAt))||
            payments.Select(p=>(p.Id,p.Occurrence)).Distinct().Count()!=payments.Length||
            payments.GroupBy(p=>p.Id).Any(g=>g.Select(p=>(p.Slot,p.PaidAt,p.KnownAt)).Distinct().Count()!=1))
            throw new MarketDataException("payment_state","Select the whole-day accrual policy, an exact held binding and quantity, and distinct identified payment allocations.");
        TradeId=tradeId;BindingId=bindingId;BookId=bookId;Quantity=quantity;Payments=payments;
        OwnerDays = ownerDays.IsDefault ? [] : ownerDays;
        PhysicalDeliveryQuantities.RequireFacts(OwnerDays, quantity.Specification);
        Elections = elections.IsDefault ? [] : elections;
        ExerciseElection.RequireFacts(Elections);
        if (nextElectionEffectiveAt is {} next && !Supported(next))
            throw new MarketDataException("exercise_election","Select a supported UTC election transition instant.");
        NextElectionEffectiveAt = nextElectionEffectiveAt;
        SwingElections = swingElections.IsDefault ? [] : swingElections;
        if (nextSwingElectionEffectiveAt is {} swingTransition && !Supported(swingTransition))
            throw new MarketDataException("swing_election", "Supply a supported UTC swing election transition.");
        NextSwingElectionEffectiveAt = nextSwingElectionEffectiveAt;
        Balances = balances.IsDefault ? [] : balances;
    }
    // Paid is only ever recorded against cash that has started to accrue: there is no prepayment.
    // Returns the visible paid total and the latest visible payment instant.
    internal (ExactMoney Total,DateTimeOffset? Last) Paid(int slot,int occurrence,MarketSnapshot snapshot,ExactMoney accrued,bool accruing,ExactMoney? contractAmount=null,bool selectedQuantity=false,ExactMoney? recordedDirection=null)
    {
        var total=ExactMoney.Parse("0",accrued.Currency);DateTimeOffset? last=null;
        foreach(var payment in VisiblePayments(snapshot).Where(p=>p.Slot==slot&&p.Occurrence==occurrence))
        {
            if(!accruing)throw new MarketDataException("payment_state","A payment requires at least one ended gas day of its cash occurrence.");
            // A selected quantity can correct accrued cash to zero. Its retained price and
            // contractual amount still establish payment direction; cash is never rescaled.
            var basis=recordedDirection??(selectedQuantity&&accrued.CompareTo(ExactMoney.Parse("0",accrued.Currency))==0?contractAmount??accrued:accrued);
            CashAllocationBalance.RequireDirection(basis,payment.Amount);total=total.Add(payment.Amount);
            if(last is null||payment.PaidAt>last)last=payment.PaidAt;
        }
        return (total,last);
    }
    internal IEnumerable<CashOccurrencePayment> KnownPayments(MarketSnapshot snapshot)
    {
        if (Payments.IsEmpty) return [];
        var knownAt = PhysicalLifecycle.RequirePaymentKnowledge(snapshot).KnownAt;
        return Payments.Where(payment => payment.KnownAt <= knownAt);
    }

    internal IEnumerable<CashOccurrencePayment> VisiblePayments(MarketSnapshot snapshot) =>
        KnownPayments(snapshot).Where(payment => payment.PaidAt <= snapshot.ValuationAt);

    private static bool Supported(DateTimeOffset at)=>at!=default&&TradingInstant.IsSupported(at);
}

public enum DeliveryState { NotStarted=1,InProgress,Ended }
public enum SettlementState { Remaining=1,AccruedUnpaid,PartPaid,Paid,Overpaid,NoAmountDue,Projected,Unassessed,UnassessedPaid }
public enum LifecycleMeasure { Unrealized=1,RealizedUnsettled }
// Settlement is null on physical rows: delivery is not cash and is never settled by a date.
// DueAt, Expected (undiscounted cash still to be paid: amount less visible payments) and PaidAt are cash-only.
public sealed record PhysicalLifecycleRow(int Slot,int Occurrence,DateOnly DeliveryFrom,DateOnly DeliveryTo,DeliveryState Delivery,SettlementState? Settlement,
    ExactMoney? Accrued,ExactMoney Paid,ExactMoney? Outstanding,ExactMoney? Unrealized,ExactMoney? RealizedUnsettled,string QuantityBasis,
    DateTimeOffset? DueAt=null,ExactMoney? Expected=null,DateTimeOffset? PaidAt=null)
{
    public ReportedDayQuantity? ReportedQuantity { get; init; }
    // The P&L explain measures the row contributes to: a day not yet ended is unrealized (remaining delivery MTM); cash
    // accrued on ended, fixed days and not yet paid is realized unsettled. A fully paid, ended day is history only and contributes to neither.
    public ImmutableArray<LifecycleMeasure> Measures=>[
        ..Unrealized is not null&&(Delivery!=DeliveryState.Ended||Settlement==SettlementState.Projected)?[LifecycleMeasure.Unrealized]:Array.Empty<LifecycleMeasure>(),
        ..Settlement is SettlementState.AccruedUnpaid or SettlementState.PartPaid or SettlementState.Overpaid?[LifecycleMeasure.RealizedUnsettled]:Array.Empty<LifecycleMeasure>()];
}
public sealed record ExpectedCashFlow(DateTimeOffset DueAt,ExactMoney Amount);
public sealed record PhysicalLifecycleValue(ExactMoney Unrealized,ExactMoney RealizedUnsettled,ExactMoney Total,ImmutableArray<PhysicalLifecycleRow> Rows)
{
    public int UnassessedDays => Unassessed(Rows).Count();
    public bool Incomplete => UnassessedDays != 0;
    internal static IEnumerable<(int SourceSlot, DateOnly Day)> Unassessed(IEnumerable<PhysicalLifecycleRow> rows) => rows
        .Where(row => row.ReportedQuantity?.State == ReportedDayQuantity.Unassessed)
        .Select(row => (row.ReportedQuantity!.SourceSlot, row.ReportedQuantity.Day)).Distinct();
    // Undiscounted cash still expected, by due date. It is shown beside the measures and never added to them.
    public ImmutableArray<ExpectedCashFlow> ExpectedCash=>[..Rows.Where(r=>r.DueAt is not null&&r.Expected is not null).GroupBy(r=>r.DueAt!.Value).OrderBy(g=>g.Key)
        .Select(g=>new ExpectedCashFlow(g.Key,g.Select(r=>r.Expected!).Aggregate((a,b)=>a.Add(b))))
        .Where(f=>f.Amount.CompareTo(ExactMoney.Parse("0",f.Amount.Currency))!=0)];
}

// A member's evaluation and the map from its unscaled amounts to signed held amounts.
public sealed record LifecycleMember(BookedSideProgram Program,BookedSideEvaluation Evaluation,bool Physical,Func<ExactMoney,ExactMoney> Held);

// Instrument-owned connections carry day states and physical quantities.
// Owned cash days are scoped to their occurrence slot.
internal sealed record PhysicalLifecycleGeometry(ImmutableDictionary<DateOnly,DeliveryState> Days,
    ImmutableDictionary<DateOnly,ExactQuantity> Quantities,
    ImmutableDictionary<int,ImmutableDictionary<DateOnly,DeliveryState>> OwnedDays);

// Started delivery, whole gas days: ended days leave the unrealized measure; their cash accrues on contract quantity
// and stays realized unsettled until paid, discounted to its payment date until due.
public static class PhysicalLifecycle
{
    public const string EstimatedQuantity="contract_estimate_allocation_pending_v1",ContractQuantity="contractual_quantity_v1";
    public const string ElectedQuantity = "elected";
    public const string SelectedQuantityPolicy="selected_delivery_quantity_v1";
    public const string AllocatedFinalQuantity="allocated_final_v1",AllocatedProvisionalQuantity="allocated_provisional_v1",ScheduledQuantity="scheduled_v1";
    // The named value components a started-delivery position reports to risk; they sum to its value.
    public const string UnrealizedComponent="unrealized",RealizedUnsettledComponent="realizedUnsettled";
    // Refusals that belong to one held trade's terms or recorded cash, not to the shared market: a reader reports them on
    // that trade's row and keeps valuing the others.
    public static bool TradeRefusal(string code)=>code is "physical_lifecycle_unsupported" or "physical_lifecycle_quantity" or
        "side_due_leg_unresolved" or "payment_state" or "settlement_conservation" or "gas_quantity_selection" or "gas_imbalance_transfer";

    // Missing lifecycle wiring is a system defect, not a trade's refusal: it fails the read (TradeRefusal excludes it).
    public const string ContextRefusal="physical_lifecycle_context";

    internal static MarketKnowledge RequirePaymentKnowledge(MarketSnapshot snapshot) => snapshot.Knowledge ??
        throw new MarketDataException(ContextRefusal, "Payment visibility requires an explicit resolved knowledge cutoff.");

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
        IEnumerable<IndexPriceLookup> Read(BookedSideProgram program)=>DatedInputs(program,index).Select(input => input.Lookup);
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

    public static ImmutableArray<SchedulePriceTerm> ActivePrices(IEnumerable<BookedSideProgram> programs,IReadOnlyCollection<IndexPriceLookup> lookups)
    {
        var index=lookups.ToLookup(l=>(l.Market,l.Contract.DeliveryFrom,l.Contract.DeliveryTo));
        return [..programs.SelectMany(program=>DatedInputs(program,index)
            .Select(input=>SchedulePriceTerm.FromLookup(input.Lookup) with{UsesDeliveryMark=ReadsDeliveryMarks(program)})).Distinct()];
    }
    internal static IEnumerable<(BookedSideInstruction Instruction, IndexPriceLookup Lookup)> DatedInputs(BookedSideProgram program,
        ILookup<(CurveRequest,DateOnly,DateOnly),IndexPriceLookup> index)
    {
        var availabilityPayments=program.Document.Instructions.Where(i=>i.Output.Operation=="price_application"&&
            i.Output.Parameters.GetValueOrDefault("mapping")==FloatingSide.DeliveryIndexImplementation).Select(i=>i.Output.PaymentOrdinal).ToHashSet();
        return program.Document.Instructions
            .Where(i=>i.Output.Grid is null&&CurvePriceScheduleSource.IsLookup(i.Output.Operation))
            .SelectMany(i=>ScheduleMarketSources.DatedCandidates(i,i.Bindings.Where(b=>b.Port=="market").Select(b=>b.Market).FirstOrDefault() is {} market&&
                i.Output.ObservationFrom is {} from&&i.Output.ObservationTo is {} to?index[(market,from,to)]:[],availabilityPayments.Contains(i.Output.PaymentOrdinal)).Select(lookup => (i, lookup)));
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

    // Project already-valued Side rows to the exact held quantity. No program
    // or price is evaluated here; the Instrument's schedule owns that work.
    public static PhysicalLifecycleValue FromValuation(InstrumentValuation value)
    {
        if(value?.Context.BindingState is not PhysicalCashState state || value.Sides.IsDefaultOrEmpty ||
            value.Sides.Any(s => s.Value is not RetainedProgramValuation { Lifecycle: not null }))
            throw new MarketDataException(ContextRefusal, "Project a complete held Instrument valuation with its Side-owned lifecycle rows.");
        var quantity = new SignedRatio(state.Quantity.Amount.ScaledValue, ExactDecimal.WorkingScale);
        var zero = ExactMoney.Parse("0", value.Currency);
        var parts = value.Sides.Select(side => Scale(((RetainedProgramValuation)side.Value).Lifecycle!, quantity *
            new SignedRatio(side.Coefficient.ScaledValue, ExactDecimal.WorkingScale) *
            new SignedRatio(side.QuantityRatio.Numerator, side.QuantityRatio.Denominator))).ToArray();
        var choice = value.UndecidedUnitValue.Scale(quantity);
        return new(parts.Aggregate(choice, (sum, p) => sum.Add(p.Unrealized)), parts.Aggregate(zero, (sum, p) => sum.Add(p.RealizedUnsettled)),
            parts.Aggregate(choice, (sum, p) => sum.Add(p.Total)), [..parts.SelectMany(p => p.Rows)]);
    }
    internal static PhysicalLifecycleGeometry PrepareGeometry(IEnumerable<BookedSideProgram> programs,
        IEnumerable<(int Slot, BookedSideProgram Program, OwnedQuantity Quantity)> owningEntries, MarketSnapshot snapshot)
    {
        var at=snapshot.ValuationAt;
        var days=new Dictionary<DateOnly,DeliveryState>();
        var quantities=new Dictionary<DateOnly,ExactQuantity>();
        foreach(var program in programs)
        {
            var seen=new HashSet<DateOnly>();
            foreach(var day in PhysicalDeliverySchedule.ReadLiteralQuantities(program,ExactRatio.One))
            {
                var delivered=DeliveryAt(day.Start,day.End,at);
                if(days.TryGetValue(day.OperatingDate,out var known)&&known!=delivered||!seen.Add(day.OperatingDate))
                    throw Unsupported("Every physical member must agree whether each operating day has started or ended at the valuation instant.");
                days[day.OperatingDate]=delivered;
                quantities[day.OperatingDate]=quantities.TryGetValue(day.OperatingDate,out var sum)?sum.Add(day.Quantity):day.Quantity;
            }
        }
        var ownedDays = ImmutableDictionary.CreateBuilder<int,ImmutableDictionary<DateOnly,DeliveryState>>();
        foreach (var (slot, program, owned) in owningEntries)
        {
            var entryDays = ImmutableDictionary.CreateBuilder<DateOnly,DeliveryState>();
            foreach (var instruction in program.Document.Instructions.Where(i => i.Output.Operation == "output_guard"))
            {
                var (from, to, _) = CashTerm(instruction.Output);
                var start = owned.Clock.Boundary(from);
                for (var day = from; day < to; day = day.AddDays(1))
                {
                    var end = owned.Clock.Boundary(day.AddDays(1));
                    entryDays[day] = DeliveryAt(start, end, at);
                    start = end;
                }
            }
            ownedDays.Add(slot, entryDays.ToImmutable());
        }
        return new(days.ToImmutableDictionary(),quantities.ToImmutableDictionary(),ownedDays.ToImmutable());
    }
    private static DeliveryState DeliveryAt(DateTimeOffset start, DateTimeOffset end, DateTimeOffset at) =>
        end <= at ? DeliveryState.Ended : start <= at ? DeliveryState.InProgress : DeliveryState.NotStarted;

    private static (DateOnly From, DateOnly To, DateTimeOffset ValueAt) CashTerm(ScheduleDefinitionRow row) =>
        row.Grid is null && row.DeliveryFrom is {} from && row.DeliveryTo is {} to && row.ValueAt is {} valueAt && from < to
            ? (from, to, valueAt)
            : throw Unsupported("Cash lifecycle rows require their retained dated delivery term.");
    internal static PhysicalLifecycleValue EvaluateSide(BookedSideProgram program,BookedSideEvaluation evaluation,bool physical,int slot,
        SignedRatio heldFactor,PhysicalLifecycleGeometry geometry,PhysicalCashState state,MarketSnapshot snapshot,ReferenceKey currency,ImmutableArray<EntryDayQuantity> entryDays,bool cashAutomatic=false,
        ImmutableArray<ReportedDayQuantity> reportedDays=default)
    {
        if(heldFactor.Numerator.IsZero)throw new MarketDataException(ContextRefusal,"A held Side requires nonzero exact quantity normalization.");
        var held=EvaluateConnected([new(program,evaluation,physical,value=>value.Scale(heldFactor))],state,snapshot,currency,geometry,slot,entryDays,cashAutomatic,reportedDays,heldFactor);
        return Scale(held,new SignedRatio(heldFactor.Numerator.Sign*heldFactor.Denominator,System.Numerics.BigInteger.Abs(heldFactor.Numerator)));
    }
    internal static PhysicalLifecycleValue Scale(PhysicalLifecycleValue value,SignedRatio factor)=>new(
        value.Unrealized.Scale(factor),value.RealizedUnsettled.Scale(factor),value.Total.Scale(factor),
        [..value.Rows.Select(row=>row with{Accrued=row.Accrued?.Scale(factor),Paid=row.Paid.Scale(factor),Outstanding=row.Outstanding?.Scale(factor),
            Unrealized=row.Unrealized?.Scale(factor),RealizedUnsettled=row.RealizedUnsettled?.Scale(factor),Expected=row.Expected?.Scale(factor)})]);
    private static PhysicalLifecycleValue EvaluateConnected(ImmutableArray<LifecycleMember> members,PhysicalCashState state,MarketSnapshot snapshot,
        ReferenceKey currency,PhysicalLifecycleGeometry geometry,int firstSlot,ImmutableArray<EntryDayQuantity> entryDays,bool cashAutomatic,
        ImmutableArray<ReportedDayQuantity> reportedDays,SignedRatio heldFactor)
    {
        _=RequirePaymentKnowledge(snapshot);
        var at=snapshot.ValuationAt;var zero=ExactMoney.Parse("0",currency);
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
            var ownsDays=geometry.OwnedDays.TryGetValue(slot,out var ownedDays);
            var days=ownedDays??geometry.Days;
            foreach(var contribution in member.Evaluation.Contributions)
            {
                var row=document.Instructions[contribution.Instruction].Output;
                if(row.PaymentOrdinal is not {} occurrence||contribution.Amount.Values.Length!=1)
                    throw Unsupported("Lifecycle rows require one retained occurrence per contribution.");
                if(member.Physical)
                {
                    if(row.Grid is not {} grid||document.Grids[grid].CellCount!=1)throw Unsupported("Physical lifecycle rows require whole gas days.");
                    var interval=document.Grids[grid].Interval(0);var delivery=DeliveryAt(interval.Start,interval.End,at);
                    var selected=entryDays.IsDefault?null:entryDays.Single(d=>d.Day==interval.OperatingDate);
                    var fraction=selected is null?new SignedRatio(1,1):DeliveryQuantityFractions.For([selected],_=>false).Total;
                    if(delivery==DeliveryState.Ended)RequireOutside(contribution);
                    rows.Add(new(slot,occurrence,interval.OperatingDate,interval.OperatingDate.AddDays(1),delivery,null,
                        zero,zero,zero,delivery==DeliveryState.Ended?zero:Held(member,contribution.PresentValue).Scale(fraction),zero,
                        selected?.Source??(delivery==DeliveryState.Ended?EstimatedQuantity:ContractQuantity)));
                    continue;
                }
                var (from,to,valueAt)=CashTerm(row);
                var covered=Enumerable.Range(0,to.DayNumber-from.DayNumber).Select(from.AddDays).ToArray();
                if(!ownsDays&&covered.Any(day=>!days.ContainsKey(day)))throw Unsupported("Every cash day requires its retained physical gas day.");
                var reported=reportedDays.IsDefault?null:reportedDays.SingleOrDefault(day=>day.Day==from)
                    ??throw PhysicalDeliveryQuantities.Invalid("A reported quantity must cover every payment day.");
                if(reported is not null&&(covered.Length!=1||member.Physical||ownsDays||!entryDays.IsDefault||
                    reported.Quantity is {} native&&native.Specification!=state.Quantity.Specification))
                    throw PhysicalDeliveryQuantities.Invalid("An absolute reported quantity belongs to one independent daily payment.");
                if(reported is {State:not ReportedDayQuantity.Assessed})
                {
                    var visible=state.VisiblePayments(snapshot).Where(p=>p.Slot==slot&&p.Occurrence==occurrence).ToArray();
                    if(reported.State==ReportedDayQuantity.NotApplicable&&visible.Length!=0)
                        throw new MarketDataException("payment_state","A day that has not ended cannot carry a cash-out payment.");
                    var direction=ExactMoney.FromFraction(heldFactor.Numerator.Sign,1,currency);
                    foreach(var payment in visible)CashAllocationBalance.RequireDirection(direction,payment.Amount);
                    var paidUnknown=visible.Aggregate(zero,(sum,p)=>sum.Add(p.Amount));
                    rows.Add(new(slot,occurrence,from,to,days[from],reported.State==ReportedDayQuantity.Unassessed
                            ? visible.Length==0?SettlementState.Unassessed:SettlementState.UnassessedPaid : null,
                        null,paidUnknown,null,null,null,reported.State,valueAt,null,visible.Length==0?null:visible.Max(p=>p.PaidAt))
                        {ReportedQuantity=reported});
                    continue;
                }
                // A charge owns one constant daily quantity. Pooled physical quantities must agree before using the same fraction.
                var follows=!ownsDays&&!entryDays.IsDefault;
                if(!ownsDays&&!follows&&covered.Select(day=>geometry.Quantities[day]).Distinct().Count()!=1)
                    throw new MarketDataException("physical_lifecycle_quantity","Whole-day accrual of a multi-day payment requires equal daily contract quantities.");
                var ended=covered.Count(day=>days[day]==DeliveryState.Ended);var started=covered.Any(day=>days[day]!=DeliveryState.NotStarted);
                var fractions=reported is not null?ReportedFractions(reported,geometry.Quantities[from],heldFactor,ended):
                    follows?DeliveryQuantityFractions.For(entryDays.Where(d=>d.Day>=from&&d.Day<to),day=>days[day]==DeliveryState.Ended):
                    DeliveryQuantityFractions.EqualDays(ended,covered.Length);
                var quantityBasis=reported?.State??(follows?SelectedQuantityPolicy:!ownsDays&&ended>0?EstimatedQuantity:ContractQuantity);
                var due=PaymentDatePolicy.IsOverdue(valueAt,at);
                var prices=contribution.Amount.States[0].Prices;
                const SchedulePriceFacts finalPrices=SchedulePriceFacts.Fixed|SchedulePriceFacts.Actual|SchedulePriceFacts.PublishedFixing;
                var fixedPrice=prices!=SchedulePriceFacts.None&&(prices&~finalPrices)==0;
                var projectedPrice=(prices&SchedulePriceFacts.Projected)!=0&&(prices&~(finalPrices|SchedulePriceFacts.Projected))==0;
                if(ended>0&&!fixedPrice&&((due&&!cashAutomatic)||!projectedPrice))
                    throw new MarketDataException("side_due_leg_unresolved","Accrued cash requires its fixed, actual or published price.");
                if(due&&ended<covered.Length)throw Unsupported("A due payment requires every gas day it covers to have ended.");
                var contractAmount=Held(member,contribution.Amount);var amount=contractAmount.Scale(fractions.Total);
                if(ended>0&&!fixedPrice)
                {
                    // Delivery completion does not turn a forecast into earned
                    // cash. Until the required fixing, its projection keeps its market risk.
                    _=state.Paid(slot,occurrence,snapshot,zero,false);
                    rows.Add(new(slot,occurrence,from,to,ended==covered.Length?DeliveryState.Ended:DeliveryState.InProgress,
                        SettlementState.Projected,zero,zero,amount,due?amount:Held(member,contribution.PresentValue).Scale(fractions.Total),zero,
                        quantityBasis,valueAt,amount){ReportedQuantity=reported});
                    continue;
                }
                var accrued=contractAmount.Scale(fractions.Ended);
                // A later fixing can cross zero. The recorded cash fact keeps its
                // booked membership direction, independently of the corrected price.
                var recordedDirection=reported is null?null:ExactMoney.FromFraction(heldFactor.Numerator.Sign,1,currency);
                var(paid,paidAt)=state.Paid(slot,occurrence,snapshot,accrued,ended>0,contractAmount,follows||reported is not null,recordedDirection);var outstanding=accrued.Subtract(paid);
                if(due)RequireOutside(contribution);
                // Not yet due: the row's own discount factor, PV over amount from this one evaluation.
                var value=due?zero:Held(member,contribution.PresentValue);
                var outstandingPv=due?outstanding:contractAmount.CompareTo(zero)==0?zero:outstanding.Scale(value.Over(contractAmount));
                if(!due&&reported is not null&&contractAmount.CompareTo(zero)==0&&outstanding.CompareTo(zero)!=0)
                    outstandingPv=outstanding.Scale(DiscountFactor(member,contribution));
                rows.Add(new(slot,occurrence,from,to,ended==covered.Length?DeliveryState.Ended:started?DeliveryState.InProgress:DeliveryState.NotStarted,
                    Settle(ended>0,accrued,paid),accrued,paid,outstanding,value.Scale(fractions.Total-fractions.Ended),outstandingPv,
                    quantityBasis,valueAt,amount.Subtract(paid),paidAt){ReportedQuantity=reported});
            }
        }
        var result=rows.ToImmutable();
        var unrealized=result.Where(r=>r.Unrealized is not null).Aggregate(zero,(sum,r)=>sum.Add(r.Unrealized!));
        var unsettled=result.Where(r=>r.RealizedUnsettled is not null).Aggregate(zero,(sum,r)=>sum.Add(r.RealizedUnsettled!));
        return new(unrealized,unsettled,unrealized.Add(unsettled),result);
    }
    private static DeliveryQuantityFractions ReportedFractions(ReportedDayQuantity day,ExactQuantity unitQuantity,SignedRatio heldFactor,int ended)
    {
        if(ended!=1||day.Quantity is not {} quantity||quantity.Specification!=unitQuantity.Specification)
            throw PhysicalDeliveryQuantities.Invalid("Only an ended day can have an assessed absolute quantity in the payment's native unit.");
        var contract=new SignedRatio(unitQuantity.NumeratorValue,unitQuantity.DenominatorValue)*
            new SignedRatio(System.Numerics.BigInteger.Abs(heldFactor.Numerator),heldFactor.Denominator);
        var fraction=DeliveryQuantityFractions.Ratio(new(quantity.NumeratorValue,quantity.DenominatorValue),contract);
        return new(fraction,fraction);
    }
    // A real zero price cannot supply PV/amount. The Side has already evaluated
    // the exact discount factor, which still discounts an overpaid cash balance.
    private static SignedRatio DiscountFactor(LifecycleMember member,ScheduleContribution contribution)
    {
        var rows=member.Program.Document.Instructions;
        var guard=rows[contribution.Instruction];
        var discounted=rows[guard.Inputs["discounted"].Single()];
        var factor=member.Evaluation.Values[discounted.Inputs["discount"].Single()].Scenario;
        if(factor.Values.Length!=1||factor.Presence[0]!=ScheduleCellPresence.Resolved)
            throw Unsupported("A recorded cash balance requires the Side's resolved discount factor.");
        return new(factor.Values[0].Numerator,factor.Values[0].Denominator);
    }
    // A row with no ended day still has value to come; otherwise visible payments against accrued.
    // Paid only when visible payments cover a nonzero accrual.
    private static SettlementState Settle(bool accruing,ExactMoney accrued,ExactMoney paid)
    {
        if(!accruing)return SettlementState.Remaining;
        var zero=ExactMoney.Parse("0",accrued.Currency);var sign=accrued.CompareTo(zero);
        if(paid.CompareTo(zero)==0)return sign==0?SettlementState.NoAmountDue:SettlementState.AccruedUnpaid;
        if(sign==0||paid.CompareTo(zero)!=sign)return SettlementState.Overpaid;
        return (paid.CompareTo(accrued)*sign) switch{<0=>SettlementState.PartPaid,0=>SettlementState.Paid,_=>SettlementState.Overpaid};
    }
    private static MarketDataException Unsupported(string message)=>new("physical_lifecycle_unsupported",message);
}
