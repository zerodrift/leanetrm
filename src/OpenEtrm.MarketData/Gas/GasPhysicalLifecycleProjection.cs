using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasPhysicalProjectionParent(FuturesLifecycleProjection? Future,GasPhysicalKnownAttachment Native,
    KnownFuturesPhysicalFinal Accepted,ISideDiscounting? PrincipalDiscounting);

// Accepted delivery parents share one resource transition. Each future,
// obligation, stock lot and principal membership keeps its real native owner.
public sealed partial class GasPhysicalLifecycleProjection:ILifecycleProjection
{
    public string Implementation=>"accepted_gas_physical_continuation_v1";
    public AccountingPositionKey Owner{get;}
    public ImmutableArray<AccountingPositionKey> EconomicOwners{get;}
    public ReferenceKey Currency{get;}
    public MarketSnapshot SourceSnapshot{get;}
    public ImmutableArray<MarketSnapshot> MarketSources{get;}
    public ImmutableArray<CurveRequest> Dependencies{get;}
    public ImmutableArray<LifecycleInputFinal> InputFinals{get;}
    public ImmutableArray<LifecycleProjectionEvent> Events{get;}
    public LifecycleProjectionState Opening{get;}
    private readonly ImmutableArray<GasPhysicalProjectionParent> parents;
    private readonly GasQuantityLifecycleProjection? quantities;
    private readonly ImmutableDictionary<CurveRequest,MarketSnapshot> dependencySources;
    private readonly ImmutableDictionary<AccountingPositionKey,MarketSnapshot> deliverySources;
    private readonly ImmutableDictionary<Guid,FuturesLifecycleProjection> childEvents;
    private readonly ImmutableDictionary<AccountingOwnerKey,FuturesLifecycleProjection> futureOwners;
    private readonly ImmutableDictionary<Guid,NativeContinuation> nativeEvents;
    private readonly ImmutableDictionary<AccountingPositionKey,NativeContinuation> cashOwners;
    private readonly ImmutableDictionary<Guid,NativeContinuation> nativeCash;

    public GasPhysicalLifecycleProjection(ImmutableArray<GasPhysicalProjectionParent> parents,GasQuantityLifecycleProjection quantities)
        :this(parents,quantities??throw Error(),true){}
    public GasPhysicalLifecycleProjection(FuturesLifecycleProjection child,GasPhysicalKnownAttachment native,ISideDiscounting? principalDiscounting=null)
        :this(child,native,null,principalDiscounting){}
    public GasPhysicalLifecycleProjection(FuturesLifecycleProjection child,GasPhysicalKnownAttachment native,
        GasQuantityLifecycleProjection? quantities,ISideDiscounting? principalDiscounting)
        :this([new(child,native,child?.NativeDelivery??throw Error(),principalDiscounting)],quantities,false){}
    private GasPhysicalLifecycleProjection(ImmutableArray<GasPhysicalProjectionParent> parents,GasQuantityLifecycleProjection? quantities,bool shared)
    {
        if(shared&&quantities is null||parents.IsDefaultOrEmpty||parents.Any(p=>p?.Native?.Terms is null||p.Native.SourceSnapshot is null||p.Accepted is null)||
            parents.Select(p=>p.Native.DeliveryOwner).Distinct().Count()!=parents.Length||
            parents.Select(p=>p.Native.FutureOwner).Distinct().Count()!=parents.Length)throw Error();
        parents=[..parents.OrderBy(p=>p.Native.DeliveryOwner.Owner.Id).ThenBy(p=>p.Native.DeliveryOwner.BindingId)];
        this.parents=parents;this.quantities=quantities;
        var children=parents.Where(p=>p.Future is not null).Select(p=>p.Future!).ToImmutableArray();
        if(children.Select(c=>c.Owner.Owner).Distinct().Count()!=children.Length)throw Error();
        SourceSnapshot=quantities?.SourceSnapshot??children.FirstOrDefault()?.SourceSnapshot??throw Error();
        Currency=parents[0].Native.Terms.Currency;Owner=shared?quantities!.Owner:children[0].Owner;
        foreach(var parent in parents)
        {
            var native=parent.Native;var child=parent.Future;
            if(KnownFuturesPhysicalFinal.Create(native,parent.Accepted.Determination)!=parent.Accepted||native.Terms.Currency!=Currency||
                native.DeliveryOwner.BookId!=Owner.BookId||native.SourceSnapshot.Knowledge!=SourceSnapshot.Knowledge||
                native.SourceSnapshot.ValuationAt!=SourceSnapshot.ValuationAt||native.SourceSnapshot.MarketCutoff!=SourceSnapshot.MarketCutoff||
                native.SourceSnapshot.Definitions.CatalogId!=SourceSnapshot.Definitions.CatalogId)throw Error();
            if(child is null)
            {if(native.EffectiveAt>SourceSnapshot.ValuationAt||quantities is null)throw Error();}
            else if(child.NativeDelivery!=parent.Accepted||native.FutureOwner!=child.Owner||native.EffectiveAt<=SourceSnapshot.ValuationAt||
                !child.Opening.Members.IsEmpty||!child.Opening.KnownResidualOwners.SequenceEqual([native.DeliveryOwner]))throw Error();
        }
        if(quantities is not null&&(quantities.Currency!=Currency||quantities.SourceSnapshot.Knowledge!=SourceSnapshot.Knowledge||
            quantities.SourceSnapshot.ValuationAt!=SourceSnapshot.ValuationAt||quantities.SourceSnapshot.MarketCutoff!=SourceSnapshot.MarketCutoff||
            quantities.Opening.Members.IsEmpty||children.Any(c=>quantities.EconomicOwners.Contains(c.Owner))))throw Error();
        if(quantities is not null&&!quantities.PhysicalOwners.ToHashSet().SetEquals(parents.Select(p=>p.Native.DeliveryOwner)))
            throw new MarketDataException("gas_projection_scope","Compose every exact physical parent before sharing its connected native quantity resources.");
        foreach(var parent in parents)quantities?.RequirePhysicalContinuation(parent.Native,parent.Accepted);
        var uses=children.SelectMany(c=>c.Dependencies.Select(d=>(Request:d,Source:c.DependencySource(d))))
            .Concat(quantities is null?[]:quantities.Dependencies.Select(d=>(Request:d,Source:quantities.DependencySource(d)))).ToArray();
        if(uses.GroupBy(p=>p.Request).Any(g=>g.Any(p=>!p.Source.HasSameInputs(g.First().Source))))throw Error();
        dependencySources=uses.GroupBy(p=>p.Request).ToImmutableDictionary(g=>g.Key,g=>g.First().Source);
        deliverySources=parents.ToImmutableDictionary(p=>p.Native.DeliveryOwner,p=>DependencySource(p.Native.Terms.Mark));
        var sources=children.SelectMany(c=>c.MarketSources).Concat(quantities?.MarketSources??[]).Prepend(SourceSnapshot).ToArray();
        if(sources.GroupBy(s=>s.Definitions).Any(g=>g.Any(s=>!s.HasSameInputs(g.First()))))throw Error();
        MarketSources=[..sources.DistinctBy(s=>s.Definitions)];Dependencies=[..dependencySources.Keys];
        InputFinals=[..children.SelectMany(c=>c.InputFinals).Distinct()];
        var members=quantities?.Opening.Members??parents.ToImmutableDictionary(p=>p.Native.DeliveryOwner,
            p=>new LifecycleProjectionState([],[]){KnownResidualOwners=[p.Native.DeliveryOwner]});
        foreach(var child in children)members=members.Add(child.Owner,child.Opening with{KnownResidualOwners=[]});
        var continuations=parents.Select(p=>new NativeContinuation(p,SourceSnapshot,quantities)).ToArray();
        foreach(var continuation in continuations)continuation.Prepare(ref members);
        Opening=LifecycleProjectionState.Compose(members);EconomicOwners=[..LifecycleProjectionState.Order(members).Select(p=>p.Key)];
        var finalIds=parents.Where(p=>p.Future is not null).ToDictionary(p=>LifecycleProjectionEvent.ScopedId(p.Native.FutureOwner,p.Native.FinalOccurrence.Id),p=>p.Native.EffectiveAt);
        var events=children.SelectMany(c=>c.Events).Concat(quantities?.Events??[]).Concat(continuations.SelectMany(c=>c.Events.Values)).GroupBy(e=>e.Id).ToArray();
        if(events.Any(g=>g.Select(e=>e.At).Distinct().Count()!=1||g.Count()>1&&
            (g.Count()!=2||!finalIds.TryGetValue(g.Key,out var at)||g.First().At!=at)))throw Error();
        Events=[..events.Select(g=>g.Count()==1?g.First():g.First() with{Requires=[..g.SelectMany(e=>e.Requires).Distinct()]}).OrderBy(e=>e.At).ThenBy(e=>e.Id)];
        childEvents=children.SelectMany(c=>c.Events.Select(e=>(e.Id,Child:c))).ToImmutableDictionary(p=>p.Id,p=>p.Child);
        futureOwners=children.ToImmutableDictionary(c=>c.Owner.Owner);
        nativeEvents=continuations.SelectMany(c=>c.Events.Keys.Select(id=>(Id:id,Part:c))).ToImmutableDictionary(p=>p.Id,p=>p.Part);
        cashOwners=continuations.SelectMany(c=>c.CashOwners.Select(owner=>(Owner:owner,Part:c))).ToImmutableDictionary(p=>p.Owner,p=>p.Part);
        nativeCash=continuations.SelectMany(c=>c.CashIds.Select(id=>(Id:id,Part:c))).ToImmutableDictionary(p=>p.Id,p=>p.Part);
    }
    public MarketSnapshot DependencySource(CurveRequest market)=>dependencySources.GetValueOrDefault(market)??throw Error();
    public MarketSnapshot ValuationSource(AccountingPositionKey position,Instrument instrument)=>cashOwners.TryGetValue(position,out var cash)?cash.Source:
        quantities?.EconomicOwners.Contains(position)==true?quantities.ValuationSource(position,instrument):
            deliverySources.TryGetValue(position,out var source)?source:Future(position).ValuationSource(position,instrument);
    public MarketSnapshot CashSource(ProjectedCashContribution cash)=>nativeCash.TryGetValue(cash.Id,out var native)?native.Source:
        childEvents.TryGetValue(cash.Id,out var child)?((ILifecycleProjection)child).CashSource(cash):throw Error();
    public MarketSnapshot PaymentSource(AccountingFlow payment)=>cashOwners.TryGetValue(payment.Position,out var cash)?cash.Source:
        quantities?.EconomicOwners.Contains(payment.Position)==true?((ILifecycleProjection)quantities).PaymentSource(payment):
            ((ILifecycleProjection)Future(payment.Position)).PaymentSource(payment);
    private FuturesLifecycleProjection Future(AccountingPositionKey position)=>futureOwners.GetValueOrDefault(position.Owner)??throw Error();
    public ImmutableArray<LifecycleMarketUse> MarketUsesFor(DateTimeOffset horizon)=>[..parents.Where(p=>p.Future is not null).SelectMany(p=>p.Future!.MarketUsesFor(horizon))
        .Concat(quantities?.MarketUsesFor(horizon)??[]).GroupBy(u=>u.Market).Select(g=>new LifecycleMarketUse(g.Key,g.Max(u=>u.Through)))];
    public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)
    {foreach(var parent in parents)_=parent.Future?.EventsFor(horizon);_=quantities?.EventsForConnected(horizon);return [..Events.Where(e=>e.At<=horizon)];}
    public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
    {
        if(!Events.Contains(occurrence)||state.Members.Count!=EconomicOwners.Length)throw Error();
        if(nativeEvents.TryGetValue(occurrence.Id,out var native))return native.Apply(occurrence,state,context);
        var members=state.Members;
        if(childEvents.TryGetValue(occurrence.Id,out var child))
        {
            var parent=parents.Single(p=>ReferenceEquals(p.Future,child));var deliveryOwner=parent.Native.DeliveryOwner;
            if(!members.TryGetValue(child.Owner,out var original)||!members.TryGetValue(deliveryOwner,out var delivery))throw Error();
            var next=child.Apply(child.Events.Single(e=>e.Id==occurrence.Id),original with{CompletedEvents=state.CompletedEvents},context);
            var activated=next.Residuals.Where(p=>p.Positions.Any(r=>new AccountingPositionKey(r.Owner,r.BindingId,r.BookId)==deliveryOwner)).ToImmutableArray();
            if(!activated.IsEmpty)
            {
                if(!delivery.Residuals.IsEmpty||occurrence.At!=parent.Native.EffectiveAt||activated.Any(p=>p.Positions.Length!=1||
                    p.Instruments.Single() is not GasPhysicalDeliveryObligationInstrument gas||gas.Terms!=parent.Native.Terms||gas.FinalPrice!=parent.Native.CurrentFinal.Price))throw Error();
                next=next with{Residuals=next.Residuals.RemoveRange(activated)};
                if(quantities is null)delivery=delivery with{Residuals=activated};
            }
            members=members.SetItem(child.Owner,next).SetItem(deliveryOwner,delivery);
        }
        if(quantities?.Events.SingleOrDefault(e=>e.Id==occurrence.Id) is {} quantityEvent)
        {
            var quantityState=LifecycleProjectionState.Compose(members.Where(p=>quantities.EconomicOwners.Contains(p.Key)).ToImmutableDictionary()) with{CompletedEvents=state.CompletedEvents};
            members=members.SetItems(quantities.ApplyConnected(quantityEvent,quantityState,quantities.SourceSnapshot,context.CalculationAt).Members);
        }
        return LifecycleProjectionState.Compose(members) with{CompletedEvents=state.CompletedEvents};
    }
    private static MarketDataException Error()=>new("gas_physical_projection","Retain the complete exact accepted physical parents and distinct native future, delivery, stock and principal economic members.");
}
