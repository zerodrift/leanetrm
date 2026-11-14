using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public sealed partial class GasPhysicalLifecycleProjection:ILifecycleProjection
{
    public string Implementation=>"accepted_gas_physical_continuation_v1";
    public AccountingPositionKey Owner=>child.Owner;
    public ImmutableArray<AccountingPositionKey> EconomicOwners{get;}
    public ReferenceKey Currency=>child.Currency;
    public MarketSnapshot SourceSnapshot=>child.SourceSnapshot;
    public ImmutableArray<MarketSnapshot> MarketSources=>[..child.MarketSources.Concat(quantities is null?[]:((ILifecycleProjection)quantities).MarketSources).DistinctBy(s=>s.Definitions)];
    public ImmutableArray<CurveRequest> Dependencies{get;}
    public ImmutableArray<LifecycleInputFinal> InputFinals=>child.InputFinals;
    public ImmutableArray<LifecycleProjectionEvent> Events{get;}
    public LifecycleProjectionState Opening{get;}
    private readonly FuturesLifecycleProjection child;
    private readonly GasPhysicalKnownAttachment native;
    private readonly GasQuantityLifecycleProjection? quantities;
    public GasPhysicalLifecycleProjection(FuturesLifecycleProjection child,GasPhysicalKnownAttachment native,ISideDiscounting? principalDiscounting=null)
        :this(child,native,null,principalDiscounting){}
    public GasPhysicalLifecycleProjection(FuturesLifecycleProjection child,GasPhysicalKnownAttachment native,
        GasQuantityLifecycleProjection? quantities,ISideDiscounting? principalDiscounting)
    {
        if(child is null||native is null||child.NativeDelivery is not {} selected||
            KnownFuturesPhysicalFinal.Create(native,selected.Determination)!=selected||native.FutureOwner!=child.Owner||
            native.EffectiveAt<=child.SourceSnapshot.ValuationAt||!child.Opening.Members.IsEmpty||
            !child.Opening.KnownResidualOwners.SequenceEqual([native.DeliveryOwner]))throw Error();
        this.child=child;this.native=native;
        this.quantities=quantities;
        if(quantities is not null&&(quantities.Currency!=Currency||quantities.SourceSnapshot.Knowledge!=SourceSnapshot.Knowledge||
            quantities.SourceSnapshot.ValuationAt!=SourceSnapshot.ValuationAt||quantities.SourceSnapshot.MarketCutoff!=SourceSnapshot.MarketCutoff||
            quantities.EconomicOwners.Contains(Owner)||!quantities.EconomicOwners.Contains(native.DeliveryOwner)||quantities.Opening.Members.IsEmpty||
            !quantities.Opening.Members[native.DeliveryOwner].Residuals.IsEmpty||
            !quantities.Events.Any(e=>e.Id==LifecycleProjectionEvent.ScopedId(Owner,native.FinalOccurrence.Id)&&e.At==native.EffectiveAt)))throw Error();
        if(child.MarketSources.Concat(quantities is null?[]:quantities.MarketSources).GroupBy(s=>s.Definitions).Any(g=>g.Any(s=>!s.HasSameInputs(g.First())))||
            quantities is not null&&quantities.Dependencies.Intersect(child.Dependencies).Any(d=>!quantities.DependencySource(d).HasSameInputs(child.DependencySource(d))))throw Error();
        quantities?.RequirePhysicalContinuation(native,selected);
        var members=ImmutableDictionary<AccountingPositionKey,LifecycleProjectionState>.Empty
            .Add(Owner,child.Opening with{KnownResidualOwners=[]});
        members=quantities is null?members.Add(native.DeliveryOwner,new([],[]){KnownResidualOwners=[native.DeliveryOwner]}):members.AddRange(quantities.Opening.Members);
        PrepareNativeContinuation(ref members,principalDiscounting);
        Opening=LifecycleProjectionState.Compose(members);
        EconomicOwners=[..LifecycleProjectionState.Order(members).Select(p=>p.Key)];
        Dependencies=[..child.Dependencies.Concat(quantities?.Dependencies??[]).Distinct()];
        var events=child.Events.Concat(quantities?.Events??[]).Concat(nativeEvents.Values).GroupBy(e=>e.Id).ToArray();
        var finalId=LifecycleProjectionEvent.ScopedId(Owner,native.FinalOccurrence.Id);
        if(events.Any(g=>g.Select(e=>e.At).Distinct().Count()!=1||g.Count()>1&&(g.Count()!=2||g.Key!=finalId||g.First().At!=native.EffectiveAt)))throw Error();
        Events=[..events.Select(g=>g.Count()==1?g.First():g.First() with{Requires=[..g.SelectMany(e=>e.Requires).Distinct()]}).OrderBy(e=>e.At).ThenBy(e=>e.Id)];
    }
    public MarketSnapshot DependencySource(CurveRequest market)=>quantities?.Dependencies.Contains(market)==true?quantities.DependencySource(market):child.DependencySource(market);
    public MarketSnapshot ValuationSource(AccountingPositionKey position,Instrument instrument)=>
        cashOwners.Contains(position)?PrincipalSource:quantities?.EconomicOwners.Contains(position)==true?((ILifecycleProjection)quantities).ValuationSource(position,instrument):child.ValuationSource(position,instrument);
    public MarketSnapshot CashSource(ProjectedCashContribution cash)=>nativeCashIds.Contains(cash.Id)?PrincipalSource:((ILifecycleProjection)child).CashSource(cash);
    public MarketSnapshot PaymentSource(AccountingFlow payment)=>cashOwners.Contains(payment.Position)?PrincipalSource:quantities?.EconomicOwners.Contains(payment.Position)==true?
        ((ILifecycleProjection)quantities).PaymentSource(payment):((ILifecycleProjection)child).PaymentSource(payment);
    public ImmutableArray<LifecycleMarketUse> MarketUsesFor(DateTimeOffset horizon)=>[..child.MarketUsesFor(horizon).Concat(quantities?.MarketUsesFor(horizon)??[])
        .GroupBy(u=>u.Market).Select(g=>new LifecycleMarketUse(g.Key,g.Max(u=>u.Through)))];
    public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)
    {_=child.EventsFor(horizon);_=quantities?.EventsForConnected(horizon);return [..Events.Where(e=>e.At<=horizon)];}
    public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
    {
        if(nativeEvents.ContainsKey(occurrence.Id))return ApplyNative(occurrence,state,context);
        if(!Events.Contains(occurrence)||state.Members.Count!=EconomicOwners.Length||!state.Members.TryGetValue(Owner,out var original)||
            !state.Members.TryGetValue(native.DeliveryOwner,out var delivery))throw Error();
        var childEvent=child.Events.SingleOrDefault(e=>e.Id==occurrence.Id);
        var next=childEvent is null?original:child.Apply(childEvent,original with{CompletedEvents=state.CompletedEvents},context);
        var activated=next.Residuals.Where(p=>p.Positions.Any(r=>new AccountingPositionKey(r.Owner,r.BindingId,r.BookId)==native.DeliveryOwner)).ToImmutableArray();
        if(!activated.IsEmpty)
        {
            if(!delivery.Residuals.IsEmpty||occurrence.At!=native.EffectiveAt||activated.Any(p=>p.Positions.Length!=1||
                p.Instruments.Single() is not GasPhysicalDeliveryObligationInstrument gas||gas.Terms!=native.Terms||gas.FinalPrice!=native.CurrentFinal.Price))throw Error();
            next=next with{Residuals=next.Residuals.RemoveRange(activated)};
            if(quantities is null)delivery=delivery with{Residuals=activated};
        }
        var members=state.Members.SetItem(Owner,next).SetItem(native.DeliveryOwner,delivery);
        if(quantities is not null&&quantities.Events.SingleOrDefault(e=>e.Id==occurrence.Id) is {} quantityEvent)
        {
            var quantityState=LifecycleProjectionState.Compose(members.Where(p=>quantities.EconomicOwners.Contains(p.Key)).ToImmutableDictionary()) with{CompletedEvents=state.CompletedEvents};
            var changed=quantities.ApplyConnected(quantityEvent,quantityState,quantities.SourceSnapshot,context.CalculationAt);
            members=members.SetItems(changed.Members);
        }
        return LifecycleProjectionState.Compose(members) with{CompletedEvents=state.CompletedEvents};
    }
    private static MarketDataException Error()=>new("gas_physical_projection","Retain the exact accepted physical final and distinct original-future and native-delivery economic members.");
}
