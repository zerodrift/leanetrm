using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasKnownStockMovement(AccountingPositionKey Stock,GasKnownStockOperation Operation,AccountingPositionKey? Obligation);
public sealed record GasKnownQuantityDischarge(AccountingPositionKey Obligation,Quantity Quantity);
public sealed record GasKnownQuantityReceipt(AccountingPositionKey Obligation,GasProjectionHolding Stock);
public sealed record GasKnownPhysicalOrigin(KnownFuturesPhysicalFinal Accepted,GasProjectionHolding Holding);
public sealed record GasKnownQuantityTransition(Guid Id,DateTimeOffset At,DateTimeOffset KnownAt,ImmutableArray<Guid> Requires,
    ImmutableArray<GasKnownStockMovement> Stock,ImmutableArray<GasKnownQuantityDischarge> Discharges,ImmutableArray<GasKnownQuantityReceipt> Receipts)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<GasKnownPhysicalOrigin>? PhysicalOrigins{get;init;}
}

public sealed partial class GasQuantityLifecycleProjection
{
    public static Guid AcceptedEventIdentity(Guid catalog,int publication,DateTimeOffset at)
    {
        if(catalog==Guid.Empty||publication<1||!TradingInstant.IsSupported(at))
            throw Error("gas_projection_source","An accepted gas event requires its exact native catalog, publication and supported UTC effective instant.");
        return ContractIdentity.FromCanonical(FormattableString.Invariant($"gas_accepted_quantity_event_v1:{catalog:D}:{publication}:{at:yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'}"));
    }
    public static Guid AcceptedReleaseIdentity(Guid reservation,int publication)
    {
        if(reservation==Guid.Empty||publication<1)throw Error("gas_projection_source","An accepted reservation release requires its exact native reservation and publication.");
        return ContractIdentity.FromCanonical(FormattableString.Invariant($"gas_accepted_reservation_release_v1:{reservation:D}:{publication}"));
    }
    private ImmutableDictionary<Guid,GasKnownQuantityTransition> knownTransitions=ImmutableDictionary<Guid,GasKnownQuantityTransition>.Empty;
    internal void RequirePhysicalContinuation(GasPhysicalKnownAttachment native,KnownFuturesPhysicalFinal accepted)
    {
        if(!holdings.TryGetValue(native.DeliveryOwner,out var held)||held.Kind!=GasProjectionHoldingKind.PhysicalDelivery||
            held.Instrument.ValuationInstrument is not GasPhysicalDeliveryObligationInstrument instrument||instrument.Terms!=native.Terms||
            instrument.FinalPrice!=native.CurrentFinal.Price)throw Error("gas_projection_source","Retain the exact native obligation and accepted final price.");
        var origins=knownTransitions.Values.SelectMany(t=>(t.PhysicalOrigins??[]).Select(o=>(Transition:t,Origin:o)))
            .Where(o=>o.Origin.Holding.Position==native.DeliveryOwner).ToArray();
        var future=native.EffectiveAt>SourceSnapshot.ValuationAt;
        if(future?(origins.Length!=1||origins[0].Origin.Accepted!=accepted):origins.Length!=0)
            throw Error("gas_projection_source","Retain this exact physical origin before composing its accepted quantity continuation.");
        var past=native.Performances.Where(p=>!p.Retracted&&p.PerformedAt<=SourceSnapshot.ValuationAt)
            .Aggregate(default(ExactDecimal),(sum,p)=>sum+Abs(p.SignedQuantity.Amount));
        var remaining=Abs(native.OriginalQuantity.Amount)-past;
        var expected=native.OriginalQuantity.Amount.CompareTo(default)>0?remaining:-remaining;
        if(remaining.CompareTo(default)<=0||held.Quantity.Specification!=native.OriginalQuantity.Specification||held.Quantity.Amount!=(future?native.OriginalQuantity.Amount:expected))
            throw Error("gas_projection_source","Opening physical quantity must reconcile its complete accepted past performance prefix.");
        var active=native.Performances.Where(p=>!p.Retracted&&p.PerformedAt>SourceSnapshot.ValuationAt).ToArray();
        var discharges=knownTransitions.Values.SelectMany(t=>t.Discharges.Where(d=>d.Obligation==native.DeliveryOwner).Select(d=>(Transition:t,Discharge:d))).ToArray();
        if(discharges.Length!=active.Length)throw Error("gas_projection_source","Retain every selected accepted physical performance exactly once.");
        foreach(var performance in active)
        {
            var id=LifecycleProjectionEvent.ScopedId(native.DeliveryOwner,performance.Id);
            var selected=discharges.SingleOrDefault(d=>d.Transition.Id==id&&d.Transition.At==performance.PerformedAt&&
                d.Discharge.Quantity.Amount==Abs(performance.SignedQuantity.Amount)&&d.Discharge.Quantity.Specification==performance.SignedQuantity.Specification&&
                (performance.SignedQuantity.Amount.CompareTo(default)>0?
                    d.Transition.Receipts.Any(r=>r.Obligation==native.DeliveryOwner&&r.Stock.Position==performance.ReceiptStockOwner):
                    d.Transition.Stock.Where(s=>s.Obligation==native.DeliveryOwner).Select(s=>s.Operation.Id).ToHashSet().SetEquals(performance.Supply.Where(s=>!s.Retracted).Select(s=>s.ConsumptionId))));
            if(selected.Transition is null)throw Error("gas_projection_source","Physical title requires its exact native receipt or independently owned supply counterfacts.");
            if(performance.SignedQuantity.Amount.CompareTo(default)>0)
            {
                var receipt=selected.Transition.Receipts.Single(r=>r.Obligation==native.DeliveryOwner).Stock;
                var stock=native.Stocks.SingleOrDefault(s=>s.Owner==performance.ReceiptStockOwner);
                if(stock is null||stock.Retracted||stock.Lot.Id!=stock.Owner.Owner.Id||stock.Lot.AccountId!=stock.AccountId||
                    stock.Lot.EvidenceId!=performance.TitleEvidenceId||stock.Lot.TitleAt!=performance.PerformedAt||
                    receipt.Account.Id!=stock.AccountId||receipt.Account.LegalOwner!=stock.LegalOwner||receipt.Account.Location!=native.Terms.Location||
                    receipt.Quantity!=stock.Lot.Quantity||receipt.AvailableAt!=stock.Lot.AvailableAt||
                    receipt.Instrument.ValuationInstrument is not GasCommodityHoldingInstrument commodity||commodity!=stock.OriginalInstrument)
                    throw Error("gas_projection_source","An accepted receipt must retain its exact native stock instrument, title, account and availability.");
            }
            else foreach(var supply in performance.Supply.Where(s=>!s.Retracted))
            {
                var movement=selected.Transition.Stock.SingleOrDefault(s=>s.Obligation==native.DeliveryOwner&&s.Stock==supply.StockOwner&&s.Operation.Id==supply.ConsumptionId);
                if(movement is null||movement.Operation.Quantity!=supply.Quantity||movement.Operation.ReservationId!=supply.ReservationId)
                    throw Error("gas_projection_source","Every supplied native stock movement must retain its exact owner, quantity and reservation.");
            }
            var owners=performance.Supply.Where(s=>!s.Retracted).Select(s=>s.StockOwner).Concat(performance.ReceiptStockOwner is {} owner?[owner]:[]).ToHashSet();
            var requiredKnown=performance.Supply.Where(s=>!s.Retracted).Select(s=>s.Receipt.KnownAt)
                .Concat(native.Stocks.Where(s=>owners.Contains(s.Owner)).Select(s=>s.Receipt.KnownAt)).Append(performance.Receipt.KnownAt).Max();
            if(selected.Transition.KnownAt<requiredKnown||selected.Transition.At==native.EffectiveAt&&
                !selected.Transition.Requires.Contains(LifecycleProjectionEvent.ScopedId(native.FutureOwner,native.FinalOccurrence.Id)))
                throw Error("gas_projection_source","A physical performance requires every selected financial receipt and its same-instant native final prerequisite.");
        }
    }
    internal ImmutableArray<AccountingPositionKey> PhysicalOwners=>[..holdings.Values.Where(h=>h.Kind==GasProjectionHoldingKind.PhysicalDelivery).Select(h=>h.Position)];
    internal ISideDiscounting? PhysicalCarrying(AccountingPositionKey owner)=>holdings.TryGetValue(owner,out var holding)&&
        holding.Kind==GasProjectionHoldingKind.PhysicalDelivery?holding.Discounting:
        throw Error("gas_projection_source","Select the exact native physical obligation before reading its carrying policy.");
    private ImmutableDictionary<AccountingPositionKey,GasKnownStockSchedule> knownStockSchedules=ImmutableDictionary<AccountingPositionKey,GasKnownStockSchedule>.Empty;
    private void PrepareKnownTransitions(ImmutableArray<GasKnownQuantityTransition> known,ImmutableArray<GasProjectionHolding> opening)
    {
        if(known.IsEmpty)return;
        if(known.Any(p=>p.Id==Guid.Empty||!TradingInstant.IsSupported(p.At)||p.At<=SourceSnapshot.ValuationAt||
            !TradingInstant.IsSupported(p.KnownAt)||p.KnownAt>SourceSnapshot.Knowledge!.KnownAt||p.Requires.IsDefault||
            p.Requires.Distinct().Count()!=p.Requires.Length||p.Requires.Contains(p.Id)||p.Stock.IsDefault||p.Discharges.IsDefault||
            p.Stock.IsEmpty&&p.Discharges.IsEmpty&&p.Receipts.IsEmpty&&(p.PhysicalOrigins is null||p.PhysicalOrigins.Value.IsEmpty)||p.Stock.Any(s=>s is null||s.Operation is null)||
            p.Discharges.Any(d=>d is null))||known.Select(p=>p.Id).Distinct().Count()!=known.Length||
            known.Zip(known.Skip(1)).Any(p=>p.First.At>p.Second.At))
            throw Error("gas_projection_source","Retain the complete distinct accepted quantity events in native effective order under the original knowledge cutoff.");
        knownTransitions=known.ToImmutableDictionary(p=>p.Id);
        if(known.Any(p=>p.Requires.Any(id=>!knownTransitions.TryGetValue(id,out var prior)||prior.At>p.At)))
            throw Error("gas_projection_causality","Accepted quantity events may depend only on admitted native predecessors.");
        var received=known.SelectMany(p=>p.Receipts.Select(r=>(Event:p,Receipt:r))).ToArray();
        var nativeStock=known.SelectMany(p=>p.Stock.Select(s=>(Event:p,Movement:s))).ToArray();
        if(nativeStock.Select(p=>p.Movement.Operation.Id).Distinct().Count()!=nativeStock.Length)
            throw Error("gas_projection_source","Each accepted stock movement belongs to one exact native event and stock owner.");
        foreach(var transition in known)
        {
            var owners=transition.Stock.Select(s=>s.Stock).Concat(transition.Discharges.Select(d=>d.Obligation)).Concat(transition.Receipts.Select(r=>r.Stock.Position))
                .Concat((transition.PhysicalOrigins??[]).Select(o=>o.Holding.Position));
            if(owners.Any(o=>!holdings.TryGetValue(o,out var h)||policies[h.Account.Id].Implementation!=GasQuantityProjectionPolicy.KnownVersion))
                throw Error("gas_projection_policy","Select the explicit accepted-quantity projection version for every affected native gas account.");
            if(transition.PhysicalOrigins is {} origins)
            {
                if(origins.IsDefault||origins.Any(o=>o?.Accepted is null||o.Holding is null))
                    throw Error("gas_projection_source","Retain exact accepted native physical origins.");
                foreach(var origin in origins)
                {
                    var accepted=origin.Accepted;var held=origin.Holding;
                    if(held.Kind!=GasProjectionHoldingKind.PhysicalDelivery||held.Position!=accepted.DeliveryOwner||held.Quantity!=accepted.OriginalQuantity||
                        held.Instrument.ValuationInstrument is not GasPhysicalDeliveryObligationInstrument instrument||!instrument.Terms.Equals(accepted.Configuration.Terms)||
                        instrument.FinalPrice!=accepted.FinalPrice||transition.At!=accepted.Determination.Occurrence.At||transition.KnownAt<accepted.Determination.KnownAt||
                        accepted.CatalogId!=SourceSnapshot.Definitions.CatalogId||accepted.SourceContextId!=SourceSnapshot.Knowledge!.ContextId||
                        accepted.SourceAt!=SourceSnapshot.ValuationAt||accepted.KnownThrough!=SourceSnapshot.Knowledge.KnownAt||
                        transition.Id!=LifecycleProjectionEvent.ScopedId(accepted.FutureOwner,accepted.Determination.Occurrence.Id)||opening.Any(p=>p.Position==held.Position))
                        throw Error("gas_projection_source","A physical quantity origin must preserve its exact accepted future final, profile, native owner, quantity and ordinary locked-principal valuation.");
                }
            }
            foreach(var movement in transition.Stock)movement.Operation.Require(holdings[movement.Stock].Quantity.Specification);
            if(transition.Discharges.Select(d=>d.Obligation).Distinct().Count()!=transition.Discharges.Length)
                throw Error("gas_projection_performance","An accepted event reduces each exact quantity obligation once.");
            foreach(var discharge in transition.Discharges)
            {
                var held=holdings[discharge.Obligation];
                if(held.Kind==GasProjectionHoldingKind.OwnedStock||discharge.Quantity is null||discharge.Quantity.Specification!=held.Quantity.Specification||
                    discharge.Quantity.Amount.CompareTo(default)<=0||discharge.Quantity.Amount.CompareTo(Abs(held.Quantity.Amount))>0)
                    throw Error("gas_projection_performance","An accepted performance must reduce an exact outstanding quantity without reversing its sign.");
                if(held.Quantity.Amount.CompareTo(default)>0)
                {
                    var receipts=transition.Receipts.Where(r=>r.Obligation==held.Position).ToArray();
                    if(receipts.Length!=1||receipts[0].Stock.Kind!=GasProjectionHoldingKind.OwnedStock||
                        receipts[0].Stock.Quantity!=discharge.Quantity||!Compatible(held.Account,receipts[0].Stock.Account)||
                        receipts[0].Stock.AvailableAt<transition.At||opening.Any(o=>o.Position==receipts[0].Stock.Position))
                        throw Error("gas_projection_title","An accepted positive quantity performance requires its exact distinct native receipt stock, title quantity and availability.");
                }
                else
                {
                    var used=transition.Stock.Where(s=>s.Obligation==held.Position).ToArray();
                    if(used.Length==0||used.Any(s=>s.Operation.Use!=GasKnownStockUse.Consume||!Compatible(held.Account,holdings[s.Stock].Account))||
                        used.Aggregate(default(ExactDecimal),(a,b)=>a+b.Operation.Quantity!.Amount)!=discharge.Quantity.Amount)
                        throw Error("gas_projection_performance","An accepted negative quantity performance requires its complete exact identified stock consumption.");
                }
            }
            if(transition.Receipts.Any(r=>!transition.Discharges.Any(d=>d.Obligation==r.Obligation&&holdings[d.Obligation].Quantity.Amount.CompareTo(default)>0)))
                throw Error("gas_projection_title","Native receipt stock cannot activate without its accepted positive claim counterfact.");
            foreach(var movement in transition.Stock)
            {
                if(holdings[movement.Stock].Kind!=GasProjectionHoldingKind.OwnedStock||movement.Operation.At!=transition.At||
                    movement.Obligation is {} obligation&&!transition.Discharges.Any(d=>d.Obligation==obligation&&holdings[obligation].Quantity.Amount.CompareTo(default)<0))
                    throw Error("gas_projection_performance","Retain exact native stock movements and their corresponding negative quantity counterfacts.");
                var incoming=received.SingleOrDefault(r=>r.Receipt.Stock.Position==movement.Stock);
                if(incoming.Receipt is not null&&
                    (incoming.Event.At>transition.At||incoming.Event.At==transition.At&&!transition.Requires.Contains(incoming.Event.Id)))
                    throw Error("gas_projection_causality","A stock movement requires its accepted receipt before native consumption or reservation.");
            }
        }
        if(known.SelectMany(p=>p.Discharges).GroupBy(p=>p.Obligation).Any(g=>
            g.Aggregate(default(ExactDecimal),(a,b)=>a+b.Quantity.Amount).CompareTo(Abs(holdings[g.Key].Quantity.Amount))>0))
            throw Error("gas_projection_performance","The complete source-known performance schedule cannot exceed its exact opening quantity obligation.");
        foreach(var stock in nativeStock.GroupBy(p=>p.Movement.Stock))
        {
            var ordered=stock.ToArray();
            for(var i=1;i<ordered.Length;i++)
                if(ordered[i-1].Event.Id!=ordered[i].Event.Id&&ordered[i-1].Event.At==ordered[i].Event.At&&
                    !ordered[i].Event.Requires.Contains(ordered[i-1].Event.Id))
                    throw Error("gas_projection_causality","Same-instant native stock events require their explicit accepted causal order.");
            knownStockSchedules=knownStockSchedules.Add(stock.Key,new([..ordered.Select(p=>p.Movement.Operation)]));
        }
    }
    private LifecycleProjectionState PerformKnown(LifecycleProjectionEvent occurrence,LifecycleProjectionState state)
    {
        if(!Events.Contains(occurrence)||state.CompletedEvents.Contains(occurrence.Id)||state.Members.Count!=holdings.Count)
            throw Error("gas_projection_performance","Apply an admitted accepted quantity event once to its complete native owner state.");
        var transition=knownTransitions[occurrence.Id];var members=state.Members;
        foreach(var origin in transition.PhysicalOrigins??[])
        {
            var member=members[origin.Holding.Position];
            if(!member.Residuals.IsEmpty)throw Error("gas_projection_source","An accepted physical origin cannot activate native ownership twice.");
            members=members.SetItem(origin.Holding.Position,member with{Residuals=[Portfolio(origin.Holding)]});
        }
        foreach(var movement in transition.Stock)
            members=ChangeStock(members,movement.Stock,movement.Stock.BindingId,stock=>stock.AcceptKnown(movement.Operation));
        foreach(var discharge in transition.Discharges)
        {
            var member=members[discharge.Obligation];var held=holdings[discharge.Obligation];
            var portfolio=member.Residuals.SingleOrDefault(p=>p.Positions.Any(r=>r.Owner==held.Position.Owner&&r.BindingId==held.Position.BindingId));
            var original=portfolio?.Positions.Single(r=>r.Owner==held.Position.Owner&&r.BindingId==held.Position.BindingId);
            if(original is null||Abs(original.Quantity.Amount).CompareTo(discharge.Quantity.Amount)<0)
                throw Error("gas_projection_performance","An accepted quantity performance cannot exceed its remaining native obligation.");
            var quantity=original.Quantity.Amount.CompareTo(default)>0?original.Quantity.Amount-discharge.Quantity.Amount:original.Quantity.Amount+discharge.Quantity.Amount;
            var residuals=member.Residuals.Remove(portfolio!);
            if(quantity!=default)residuals=residuals.Add(new(portfolio!.Instruments,[original with{Quantity=new(quantity,original.Quantity.Specification)}],portfolio.Setup));
            members=members.SetItem(discharge.Obligation,member with{Residuals=residuals});
        }
        foreach(var receipt in transition.Receipts)
        {
            var member=members[receipt.Stock.Position];
            if(!member.Residuals.IsEmpty)throw Error("gas_projection_title","An accepted native receipt cannot create title twice.");
            members=members.SetItem(receipt.Stock.Position,member with{Residuals=[Portfolio(receipt.Stock)]});
        }
        return LifecycleProjectionState.Compose(members) with{CompletedEvents=state.CompletedEvents};
    }
    private ImmutableDictionary<AccountingPositionKey,LifecycleProjectionState> ChangeStock(
        ImmutableDictionary<AccountingPositionKey,LifecycleProjectionState> members,AccountingPositionKey owner,Guid binding,
        Func<ProjectedGasStockValuation,ProjectedGasStockValuation> change)
    {
        if(!members.TryGetValue(owner,out var member))throw Error("gas_projection_supply","The identified stock is outside the connected economic component.");
        var portfolio=member.Residuals.SingleOrDefault(p=>p.Positions.Any(r=>r.BindingId==binding&&r.Owner==owner.Owner));
        var position=portfolio?.Positions.Single(r=>r.BindingId==binding&&r.Owner==owner.Owner);
        if(portfolio is null||position is null||portfolio.Setup.BindingStates.GetValueOrDefault(new(position.Owner,binding)) is not ProjectedGasStockValuation resource)
            throw Error("gas_projection_supply","The exact supplied stock is absent or has not received native title.");
        var next=change(resource);var residuals=member.Residuals.Remove(portfolio);
        if(next.Quantity.Amount!=default||next.KnownSchedule is not null)
        {
            var instrument=next.Quantity.Amount==default?(Instrument)new ExhaustedGasStockResource(owner,next.Quantity.Specification,Currency,SourceSnapshot):portfolio.Instruments.Single();
            residuals=residuals.Add(new([instrument],[position with{Quantity=next.Quantity,ValuationId=instrument.Id}],portfolio.Setup with{
                BindingStates=portfolio.Setup.BindingStates.SetItem(new(position.Owner,binding),next)}));
        }
        return members.SetItem(owner,member with{Residuals=residuals});
    }
}
