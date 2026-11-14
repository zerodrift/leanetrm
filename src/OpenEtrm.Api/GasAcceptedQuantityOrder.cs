using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record GasAcceptedQuantityEvent(GasKnownQuantityTransition Transition,int OriginalPublication);

internal static class GasAcceptedQuantityOrder
{
    internal static ImmutableArray<GasKnownQuantityTransition> Merge(ImmutableArray<GasAcceptedQuantityEvent> events)
    {
        if(events.IsDefault||events.Any(e=>e?.Transition is null||e.Transition.Id==Guid.Empty||e.OriginalPublication<1||
            !TradingInstant.IsSupported(e.Transition.At)||e.Transition.Requires.IsDefault||
            e.Transition.Requires.Distinct().Count()!=e.Transition.Requires.Length||e.Transition.Stock.IsDefault||e.Transition.Receipts.IsDefault)||
            events.Select(e=>e.Transition.Id).Distinct().Count()!=events.Length||
            events.Select(e=>(e.Transition.At,e.OriginalPublication)).Distinct().Count()!=events.Length)
            throw Error();
        var byId=events.ToDictionary(e=>e.Transition.Id);
        if(events.Any(e=>e.Transition.Requires.Any(id=>id==e.Transition.Id||!byId.TryGetValue(id,out var prior)||prior.Transition.At>e.Transition.At)))throw Error();
        var receipts=events.SelectMany(e=>e.Transition.Receipts.Select(r=>(r.Stock.Position,Event:e.Transition))).ToArray();
        if(receipts.Select(r=>r.Position).Distinct().Count()!=receipts.Length)throw Error();
        var received=receipts.ToDictionary(r=>r.Position,r=>r.Event);
        var reservations=events.SelectMany(e=>e.Transition.Stock.Where(s=>s.Operation.Use==GasKnownStockUse.Reserve)
            .Select(s=>(Key:(s.Stock,s.Operation.ReservationId),Event:e.Transition))).ToArray();
        if(reservations.Select(r=>r.Key).Distinct().Count()!=reservations.Length)throw Error();
        var reserved=reservations.ToDictionary(r=>r.Key,r=>r.Event);
        var prerequisites=events.ToDictionary(e=>e.Transition.Id,e=>e.Transition.Requires.ToHashSet());
        foreach(var item in events)
        foreach(var movement in item.Transition.Stock)
        {
            RequirePrior(item.Transition,received.GetValueOrDefault(movement.Stock));
            if(movement.Operation.Use!=GasKnownStockUse.Reserve&&movement.Operation.ReservationId is {} reservation)
                RequirePrior(item.Transition,reserved.GetValueOrDefault((movement.Stock,reservation)));
        }
        // Exact title and reservation foreign keys establish causality before
        // publication orders independent ready events. A financial revision can
        // refer to a same-instant resource published after the original use.
        var ordered=new List<GasKnownQuantityTransition>();
        foreach(var group in events.GroupBy(e=>e.Transition.At).OrderBy(g=>g.Key))
        {
            var pending=group.ToDictionary(e=>e.Transition.Id);var completed=ordered.Select(e=>e.Id).ToHashSet();
            while(pending.Count>0)
            {
                var ready=pending.Values.Where(e=>prerequisites[e.Transition.Id].All(completed.Contains))
                    .OrderBy(e=>e.OriginalPublication).FirstOrDefault()??throw Error();
                var current=ready.Transition with{Requires=[..prerequisites[ready.Transition.Id].Order()]};
                ordered.Add(current);completed.Add(current.Id);pending.Remove(current.Id);
            }
        }
        var last=new Dictionary<AccountingPositionKey,GasKnownQuantityTransition>();
        var result=ImmutableArray.CreateBuilder<GasKnownQuantityTransition>();
        foreach(var current in ordered)
        {
            var requires=current.Requires.ToImmutableHashSet().ToBuilder();
            foreach(var movement in current.Stock)
            {
                if(last.TryGetValue(movement.Stock,out var previous)&&previous.At==current.At&&previous.Id!=current.Id)requires.Add(previous.Id);
                last[movement.Stock]=current;
            }
            result.Add(current with{Requires=[..requires.Order()]});
        }
        return result.ToImmutable();
        void RequirePrior(GasKnownQuantityTransition current,GasKnownQuantityTransition? prior)
        {
            if(prior is null)return; // Opening title/reservations are not future events.
            if(prior.At>current.At||prior.Id==current.Id)throw Error();
            if(prior.At==current.At)prerequisites[current.Id].Add(prior.Id);
        }
    }
    private static MarketDataException Error()=>new("gas_projection_source","Retain distinct accepted native event identities, original publications and complete stock/receipt metadata; no source order is inferred.");
}
