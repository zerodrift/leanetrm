using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public enum GasProjectionHoldingKind { OwnedStock=1,OperatorClaim,LoanReturn }
public sealed record GasProjectionAccount(Guid Id,Guid BookId,PartyKey LegalOwner,ReferenceKey Location);
public sealed record GasProjectionHolding(AccountingPositionKey Position,GasProjectionAccount Account,GasProjectionHoldingKind Kind,
    GasCommodityHoldingInstrument Instrument,Quantity Quantity,DateTimeOffset? AvailableAt,
    ImmutableDictionary<Guid,ExactDecimal> Reservations,ISideDiscounting? Discounting);
public sealed record GasProjectionSupply(AccountingPositionKey EconomicOwner,Guid BindingId,Guid? ReservationId,Quantity Quantity);
public sealed record GasProjectedInventoryTerms(GasProjectionAccount Account,GasCommodityHoldingInstrument Instrument,DateTimeOffset AvailableAt);
public sealed record GasQuantityPerformance(Guid Id,AccountingPositionKey Obligation,DateTimeOffset At,Quantity Quantity,
    ImmutableArray<GasProjectionSupply> Supply,GasProjectedInventoryTerms? Receipt,ImmutableArray<Guid> Requires);
public sealed record GasQuantityProjectionPolicy(string Implementation,string Disposition)
{
    public const string Version="gas_quantity_lifecycle_projection_v1",Retain="retain_quantity_obligations_v1",AtDue="perform_full_quantity_at_contractual_due_v1";
    // Version 3 preserves accepted source-known movements before hypothetical
    // events at the same instant; stated projected allocations are never scaled.
    public const string KnownVersion="gas_accepted_quantity_lifecycle_projection_v3";
}

// Projection-only physical resource state. This cannot establish accepted title,
// release an actual reservation or be passed to an ordinary actual valuation.
public sealed partial record ProjectedGasStockValuation(AccountingPositionKey Position,Quantity Quantity,DateTimeOffset AvailableAt,
    ImmutableDictionary<Guid,ExactDecimal> Reservations):IBindingValuationState
{
    public Guid TradeId=>Position.TradeId;
    public AccountingOwnerKey Owner=>Position.Owner;
    public Guid BindingId=>Position.BindingId;
    public Guid? BookId=>Position.BookId;
    public ProjectedGasStockValuation Consume(Quantity quantity,DateTimeOffset at,Guid? reservation)
    {
        if(KnownSchedule is {} known&&!known.Operations.IsDefaultOrEmpty&&known.Operations[0].At<at)
            throw Error("gas_projection_supply","Apply earlier accepted stock movements before allocating hypothetical supply.");
        var next=ConsumeQuantity(quantity,at,reservation);next.RequireKnownSupply();return next;
    }
    private ProjectedGasStockValuation ConsumeQuantity(Quantity quantity,DateTimeOffset at,Guid? reservation)
    {
        if(quantity.Specification!=Quantity.Specification||quantity.Amount.CompareTo(default)<=0||at<AvailableAt)
            throw Error("gas_projection_supply","Select compatible owned stock that is available at the performance instant.");
        var available=reservation is {} id?Reservations.GetValueOrDefault(id):Quantity.Amount-Reservations.Values.Aggregate(default(ExactDecimal),(a,b)=>a+b);
        if(available.CompareTo(quantity.Amount)<0||Quantity.Amount.CompareTo(quantity.Amount)<0)
            throw Error("gas_projection_supply","The selected owned stock or reservation cannot cover this quantity; no procurement is inferred.");
        var reservations=reservation is {} selected?Reservations.SetItem(selected,available-quantity.Amount):Reservations;
        return this with{Quantity=new(Quantity.Amount-quantity.Amount,Quantity.Specification),Reservations=reservations};
    }
    internal void Validate()
    {
        if(!Position.IsValid||Quantity is null||Quantity.Amount.CompareTo(default)<0||!TradingInstant.IsSupported(AvailableAt)||Reservations is null||
            Reservations.Any(p=>p.Key==Guid.Empty||p.Value.CompareTo(default)<0)||Reservations.Values.Aggregate(default(ExactDecimal),(a,b)=>a+b).CompareTo(Quantity.Amount)>0)
            throw Error("gas_projection_stock","Retain exact nonnegative projected stock, availability and reserved quantities.");
        RequireKnownSupply();
    }
    private static MarketDataException Error(string code,string message)=>new(code,message);
}

// One connected component owns each original economic member exactly once.
// Performance transfers quantities between those members without adding cash or
// an internal-transfer gain. Prices remain ordinary black-box unit valuations.
public sealed partial class GasQuantityLifecycleProjection:ILifecycleProjection
{
    public string Implementation=>policies.Values.Any(p=>p.Implementation==GasQuantityProjectionPolicy.KnownVersion)?GasQuantityProjectionPolicy.KnownVersion:GasQuantityProjectionPolicy.Version;
    public AccountingPositionKey Owner{get;}
    public ImmutableArray<AccountingPositionKey> EconomicOwners{get;}
    public ReferenceKey Currency{get;}
    public MarketSnapshot SourceSnapshot{get;}
    public ImmutableArray<CurveRequest> Dependencies{get;}
    public LifecycleProjectionState Opening{get;}
    public ImmutableArray<LifecycleProjectionEvent> Events{get;}
    private readonly ImmutableDictionary<Guid,GasQuantityProjectionPolicy> policies;
    private readonly ImmutableDictionary<AccountingPositionKey,GasProjectionHolding> holdings;
    private readonly ImmutableDictionary<Guid,GasQuantityPerformance> performances;
    public GasQuantityLifecycleProjection(GasQuantityProjectionPolicy policy,MarketSnapshot source,
        ImmutableArray<GasProjectionHolding> holdings,ImmutableArray<GasQuantityPerformance> performances)
        :this(holdings.IsDefault?ImmutableDictionary<Guid,GasQuantityProjectionPolicy>.Empty:
            holdings.Where(p=>p?.Account is not null).Select(p=>p.Account.Id).Distinct().ToImmutableDictionary(a=>a,_=>policy),source,holdings,performances){}
    public GasQuantityLifecycleProjection(ImmutableDictionary<Guid,GasQuantityProjectionPolicy> policies,MarketSnapshot source,
        ImmutableArray<GasProjectionHolding> holdings,ImmutableArray<GasQuantityPerformance> performances)
        :this(policies,source,holdings,performances,[]){}
    public GasQuantityLifecycleProjection(ImmutableDictionary<Guid,GasQuantityProjectionPolicy> policies,MarketSnapshot source,
        ImmutableArray<GasProjectionHolding> holdings,ImmutableArray<GasQuantityPerformance> performances,ImmutableArray<GasKnownQuantityTransition> known)
    {
        if(known.IsDefault||known.Any(p=>p is null))throw Error("gas_projection_source","Retain the explicit complete accepted gas transition schedule.");
        var opening=holdings;
        if(known.Any(p=>p.Receipts.IsDefault||p.Receipts.Any(r=>r is null)||p.Discharges.IsDefault||p.Discharges.Any(d=>d is null||d.Quantity is null)))
            throw Error("gas_projection_source","Retain every exact native receipt and quantity discharge in its accepted event.");
        holdings=holdings.IsDefault?holdings:holdings.AddRange(known.SelectMany(p=>p.Receipts.Select(r=>r.Stock)));
        if(policies is null||policies.Any(p=>p.Key==Guid.Empty||p.Value is null||p.Value.Implementation is not(GasQuantityProjectionPolicy.Version or GasQuantityProjectionPolicy.KnownVersion)||
            p.Value.Disposition is not(GasQuantityProjectionPolicy.Retain or GasQuantityProjectionPolicy.AtDue))||source is null||source.Knowledge is null||
            holdings.IsDefaultOrEmpty||performances.IsDefault||holdings.Any(p=>p is null)||performances.Any(p=>p is null)||
            holdings.Select(p=>p.Position).Distinct().Count()!=holdings.Length||performances.Select(p=>p.Id).Distinct().Count()!=performances.Length)
            throw Error("gas_projection_policy","Select an explicit quantity-performance policy, certified source and complete distinct gas holdings.");
        this.policies=policies;SourceSnapshot=source;this.holdings=holdings.ToImmutableDictionary(p=>p.Position);
        foreach(var holding in holdings)RequireHolding(holding);
        if(!holdings.Select(p=>p.Account.Id).All(policies.ContainsKey))
            throw Error("gas_projection_policy","Select one exact saved quantity-performance policy for every account in this connected component.");
        Owner=holdings.OrderBy(p=>p.Position.Owner.Id).ThenBy(p=>p.Position.BindingId).First().Position;
        EconomicOwners=[..holdings.Select(p=>p.Position)];Currency=holdings[0].Instrument.Currency;
        if(holdings.Any(p=>p.Position.BookId!=Owner.BookId||p.Instrument.Currency!=Currency))
            throw Error("gas_projection_scope","The qualified connected gas component requires one book, currency and retained source graph.");
        var instructions=ImmutableDictionary.CreateBuilder<Guid,GasQuantityPerformance>();
        foreach(var instruction in performances)
        {
            if(instruction.Id==Guid.Empty||!this.holdings.TryGetValue(instruction.Obligation,out var held)||held.Kind==GasProjectionHoldingKind.OwnedStock||
                !TradingInstant.IsSupported(instruction.At)||instruction.At<=source.ValuationAt||instruction.At!=held.Instrument.ReturnAt||instruction.Quantity is null||
                instruction.Quantity.Specification!=held.Quantity.Specification||instruction.Supply.IsDefault||
                instruction.Requires.IsDefault||instruction.Requires.Distinct().Count()!=instruction.Requires.Length||policies[held.Account.Id].Disposition!=GasQuantityProjectionPolicy.AtDue)
                throw Error("gas_projection_performance","A projected quantity performance requires its exact outstanding owner, full quantity and contractual due instant.");
            var quantity=Abs(held.Quantity.Amount);
            if(policies[held.Account.Id].Implementation==GasQuantityProjectionPolicy.KnownVersion)
            {
                var accepted=known.Where(k=>k.Discharges.Any(d=>d.Obligation==held.Position)).ToArray();
                if(accepted.Any(k=>k.At>instruction.At))
                    throw Error("gas_projection_known_conflict","A full performance at due cannot precede a later accepted discharge of the same obligation.");
                quantity-=accepted.SelectMany(k=>k.Discharges).Where(d=>d.Obligation==held.Position)
                    .Aggregate(default(ExactDecimal),(a,b)=>a+b.Quantity.Amount);
            }
            if(quantity.CompareTo(default)<=0||instruction.Quantity.Amount!=quantity)
                throw Error("gas_projection_performance","State the exact full remaining quantity after accepted discharges; saved projected allocations are never resized.");
            var positive=held.Quantity.Amount.CompareTo(default)>0;
            if(positive)
            {
                if(instruction.Receipt is not {} receipt||!instruction.Supply.IsEmpty||!Compatible(held.Account,receipt.Account)||receipt.Instrument is null||
                    receipt.Instrument.Carrying!=GasHoldingCarrying.Nominal||receipt.Instrument.ReturnAt is not null||
                    receipt.Instrument.QuantitySpecification!=held.Quantity.Specification||receipt.Instrument.Currency!=Currency||
                    !TradingInstant.IsSupported(receipt.AvailableAt)||receipt.AvailableAt<instruction.At)
                    throw Error("gas_projection_title","Choose explicit compatible inventory marking and availability after projected title transfer.");
            }
            else if(instruction.Receipt is not null||instruction.Supply.IsDefaultOrEmpty||instruction.Supply.Any(p=>p is null||p.BindingId==Guid.Empty||
                p.ReservationId==Guid.Empty||p.Quantity is null||p.Quantity.Specification!=held.Quantity.Specification||p.Quantity.Amount.CompareTo(default)<=0)||
                instruction.Supply.Select(p=>(p.EconomicOwner,p.BindingId,p.ReservationId)).Distinct().Count()!=instruction.Supply.Length||
                instruction.Supply.Aggregate(default(ExactDecimal),(a,b)=>a+b.Quantity.Amount)!=instruction.Quantity.Amount)
                throw Error("gas_projection_supply","Select exact existing or explicitly linked incoming owned supply for the full return quantity.");
            var id=LifecycleProjectionEvent.ScopedId(instruction.Obligation,instruction.Id);instructions.Add(id,instruction);
        }
        if(performances.Select(p=>p.Obligation).Distinct().Count()!=performances.Length)
            throw Error("gas_projection_performance","The full-quantity projection admits one performance instruction per outstanding obligation.");
        this.performances=instructions.ToImmutable();
        PrepareKnownTransitions(known,opening);
        if(!policies.Keys.ToHashSet().SetEquals(holdings.Select(p=>p.Account.Id).Concat(performances.Where(p=>p.Receipt is not null).Select(p=>p.Receipt!.Account.Id))))
            throw Error("gas_projection_policy","Select exactly the original accounts and explicitly linked projected inventory destinations.");
        foreach(var instruction in performances)foreach(var supply in instruction.Supply)
        {
            _=StockAccount(supply);
            var incoming=performances.SingleOrDefault(p=>p.Obligation==supply.EconomicOwner&&p.Receipt is not null&&ProjectedStockBinding(p.Obligation,p.Id)==supply.BindingId);
            if(incoming is not null&&(incoming.At>instruction.At||!instruction.Requires.Contains(LifecycleProjectionEvent.ScopedId(incoming.Obligation,incoming.Id))))
                throw Error("gas_projection_causality","A supplied incoming title requires its explicit causal receipt event before the return.");
        }
        Events=[..instructions.Select(p=>new LifecycleProjectionEvent(p.Key,p.Value.At,known.Any(k=>k.At==p.Value.At)?
                [..p.Value.Requires.Concat(known.Where(k=>k.At==p.Value.At).Select(k=>k.Id)).Distinct()]:p.Value.Requires,"gas_quantity_performance_v1")),
            ..known.Select(p=>new LifecycleProjectionEvent(p.Id,p.At,p.Requires,"gas_accepted_quantity_transition_v1"))];
        Dependencies=[..holdings.Select(p=>p.Instrument.Mark).Concat(performances.Where(p=>p.Receipt is not null).Select(p=>p.Receipt!.Instrument.Mark)).Distinct()];
        Opening=LifecycleProjectionState.Compose(holdings.ToImmutableDictionary(p=>p.Position,p=>opening.Any(o=>o.Position==p.Position)?
            new LifecycleProjectionState([Portfolio(p)],[]):new LifecycleProjectionState([],[]){KnownResidualOwners=[p.Position]}));
    }
    public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)
    {
        _=StateAt(horizon);return Events;
    }
    public ImmutableArray<LifecycleMarketUse> MarketUsesFor(DateTimeOffset horizon)
    {
        var remaining=StateAt(horizon).Residuals.SelectMany(p=>p.Instruments).OfType<GasCommodityHoldingInstrument>().Select(i=>i.Mark).ToHashSet();
        // Quantity/title events read no prices. A discharged claim or fully
        // consumed lot uses its mark at opening only; surviving inventory must
        // retain its selected mark through H and its declared expiry policy.
        return [..Dependencies.Select(m=>new LifecycleMarketUse(m,remaining.Contains(m)?horizon:SourceSnapshot.ValuationAt))];
    }
    private LifecycleProjectionState StateAt(DateTimeOffset horizon)
    {
        if(!TradingInstant.IsSupported(horizon)||horizon<=SourceSnapshot.ValuationAt)
            throw Error("gas_projection_horizon","Select a supported common close after the gas opening.");
        // The same immutable quantity transition qualifies coverage before any
        // paths are sampled. Scenario prices cannot manufacture physical stock.
        var state=Opening;var remaining=Events.Where(e=>e.At<=horizon).OrderBy(e=>e.At).ThenBy(e=>e.Id).ToList();
        var completed=Opening.CompletedEvents;
        while(remaining.Count>0)
        {
            var at=remaining[0].At;var ready=remaining.Where(e=>e.At==at&&e.Requires.All(completed.Contains)).ToArray();
            if(ready.Length==0)throw Error("gas_projection_causality","Linked incoming title must have an explicit causal predecessor no later than the supplied return.");
            foreach(var occurrence in ready){state=Perform(occurrence,state);completed=completed.Add(occurrence.Id);state=state with{CompletedEvents=completed};remaining.Remove(occurrence);}
        }
        if(holdings.Values.Any(p=>policies[p.Account.Id].Disposition==GasQuantityProjectionPolicy.AtDue&&p.Kind!=GasProjectionHoldingKind.OwnedStock&&
            p.Instrument.ReturnAt>SourceSnapshot.ValuationAt&&p.Instrument.ReturnAt<=horizon&&
            state.Members[p.Position].Residuals.SelectMany(r=>r.Positions).Any(r=>r.BindingId==p.Position.BindingId&&r.Owner==p.Position.Owner&&r.Quantity.Amount!=default)))
            throw Error("gas_projection_supply","Every unperformed quantity obligation crossing this horizon requires an explicit receipt or supplied-return instruction.");
        return state;
    }
    public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
    {
        if(!SourceSnapshot.HasSameInputs(context.SourceSnapshot)||context.CalculationAt!=occurrence.At)
            throw Error("gas_projection_source","Gas transitions require the same exact source and event clock as their prepared component.");
        return Perform(occurrence,state);
    }
    private LifecycleProjectionState Perform(LifecycleProjectionEvent occurrence,LifecycleProjectionState state)
    {
        if(knownTransitions.ContainsKey(occurrence.Id))return PerformKnown(occurrence,state);
        if(!performances.TryGetValue(occurrence.Id,out var instruction)||!Events.Contains(occurrence)||state.Members.Count!=holdings.Count)
            throw Error("gas_projection_performance","Apply only an admitted gas performance to its complete connected owner state.");
        var held=holdings[instruction.Obligation];var members=state.Members;var obligation=members[instruction.Obligation];
        var original=obligation.Residuals.SelectMany(p=>p.Positions).SingleOrDefault(p=>p.Owner==held.Position.Owner&&p.BindingId==held.Position.BindingId);
        if(original is null||Abs(original.Quantity.Amount)!=instruction.Quantity.Amount||original.Quantity.Amount.CompareTo(default)!=held.Quantity.Amount.CompareTo(default))
            throw Error("gas_projection_performance","The exact remaining obligation must equal its stated projected performance after accepted discharges.");
        obligation=obligation with{Residuals=[..obligation.Residuals.Where(p=>!p.Positions.Any(r=>r.Owner==held.Position.Owner&&r.BindingId==held.Position.BindingId))]};
        members=members.SetItem(held.Position,obligation);
        if(held.Quantity.Amount.CompareTo(default)>0)
        {
            var receipt=instruction.Receipt!;var binding=ProjectedStockBinding(held.Position,instruction.Id);
            var position=new AccountingPositionKey(held.Position.Owner,binding,held.Position.BookId);
            var stock=new GasProjectionHolding(position,receipt.Account,GasProjectionHoldingKind.OwnedStock,receipt.Instrument,instruction.Quantity,
                receipt.AvailableAt,ImmutableDictionary<Guid,ExactDecimal>.Empty,null);
            members=members.SetItem(held.Position,obligation with{Residuals=obligation.Residuals.Add(Portfolio(stock))});
        }
        else foreach(var supply in instruction.Supply)
        {
            if(!Compatible(held.Account,StockAccount(supply)))
                throw Error("gas_projection_supply","The selected supply must be compatible existing or explicitly linked incoming owned stock.");
            members=ChangeStock(members,supply.EconomicOwner,supply.BindingId,resource=>resource.Consume(supply.Quantity,instruction.At,supply.ReservationId));
        }
        return LifecycleProjectionState.Compose(members) with{CompletedEvents=state.CompletedEvents};
    }
    public static Guid ProjectedStockBinding(AccountingPositionKey owner,Guid performance)=>ContractIdentity.FromCanonical($"projected_gas_stock_v1:{LifecycleProjectionEvent.ScopedId(owner,performance):D}");
    public static Guid PerformanceIdentity(Guid profile,int revision,AccountingPositionKey owner)
    {
        if(profile==Guid.Empty||revision<1||!owner.IsValid)throw Error("gas_projection_policy","Select the exact saved profile revision and obligation owner.");
        return ContractIdentity.FromCanonical($"gas_quantity_performance_v1:{profile:D}:{revision}:{(int)owner.Owner.Kind}:{owner.Owner.Id:D}:{owner.BindingId:D}:{owner.BookId:D}");
    }
    private GasProjectionAccount StockAccount(GasProjectionSupply supply)
    {
        if(holdings.TryGetValue(supply.EconomicOwner,out var original)&&original.Kind==GasProjectionHoldingKind.OwnedStock&&original.Position.BindingId==supply.BindingId)return original.Account;
        var incoming=performances.Values.SingleOrDefault(p=>p.Obligation==supply.EconomicOwner&&p.Receipt is not null&&ProjectedStockBinding(p.Obligation,p.Id)==supply.BindingId);
        return incoming?.Receipt?.Account??throw Error("gas_projection_supply","No explicit accepted stock or linked incoming receipt supplies this binding.");
    }
    private ValuationPortfolio Portfolio(GasProjectionHolding holding)
    {
        var position=holding.Position;var states=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty;
        if(holding.Kind==GasProjectionHoldingKind.OwnedStock)
        {
            var stock=new ProjectedGasStockValuation(position,holding.Quantity,holding.AvailableAt!.Value,holding.Reservations){
                KnownSchedule=knownStockSchedules.GetValueOrDefault(position)};
            stock.Validate();states=states.Add(new(position.Owner,position.BindingId),stock);
        }
        return new([holding.Instrument],[new(position.Owner,position.BindingId,position.BookId,holding.Instrument.Id,holding.Quantity,
            new(new Money(default,Currency),holding.Quantity.Specification),new(default,Currency))],
            new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,holding.Discounting){BindingStates=states});
    }
    private void RequireHolding(GasProjectionHolding p)
    {
        if(!p.Position.IsValid||p.Account is null||p.Account.Id==Guid.Empty||p.Account.BookId!=p.Position.BookId||p.Account.LegalOwner.Id==Guid.Empty||
            p.Account.LegalOwner.CatalogId==Guid.Empty||p.Account.LegalOwner.Revision<1||p.Instrument is null||p.Quantity is null||p.Quantity.Amount==default||
            p.Quantity.Specification!=p.Instrument.QuantitySpecification||!Enum.IsDefined(p.Kind)||p.Reservations is null||
            (p.Kind==GasProjectionHoldingKind.OwnedStock?(p.Quantity.Amount.CompareTo(default)<0||p.AvailableAt is null||!TradingInstant.IsSupported(p.AvailableAt.Value)||
                p.Instrument.Carrying!=GasHoldingCarrying.Nominal||p.Instrument.ReturnAt is not null):
                (p.AvailableAt is not null||!p.Reservations.IsEmpty||p.Instrument.ReturnAt is null||p.Kind==GasProjectionHoldingKind.LoanReturn&&p.Quantity.Amount.CompareTo(default)>0)))
            throw Error("gas_projection_holding","Supply exact nonzero stock or signed quantity obligations with explicit native title, units, owner and timing.");
        Money.RequireKey(p.Account.Location);
    }
    private static bool Compatible(GasProjectionAccount a,GasProjectionAccount b)=>a is not null&&b is not null&&a.BookId==b.BookId&&a.LegalOwner==b.LegalOwner&&a.Location==b.Location;
    private static ExactDecimal Abs(ExactDecimal n)=>n.CompareTo(default)<0?-n:n;
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
