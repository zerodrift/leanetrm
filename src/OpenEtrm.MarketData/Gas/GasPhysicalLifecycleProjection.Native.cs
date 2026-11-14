using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public sealed partial class GasPhysicalLifecycleProjection
{
    private sealed class NativeContinuation(GasPhysicalProjectionParent parent,MarketSnapshot opening,GasQuantityLifecycleProjection? quantities)
    {
        private readonly GasPhysicalKnownAttachment native=parent.Native;
        private MarketSnapshot SourceSnapshot=>opening;
        private ReferenceKey Currency=>native.Terms.Currency;
        private AccountingPositionKey Owner=>native.FutureOwner;
        internal MarketSnapshot Source=>quantities?.DependencySource(native.Terms.Mark)??parent.Future?.DependencySource(native.Terms.Mark)??throw Error();
        internal ImmutableDictionary<Guid,LifecycleProjectionEvent> Events{get;private set;}=ImmutableDictionary<Guid,LifecycleProjectionEvent>.Empty;
        internal ImmutableHashSet<AccountingPositionKey> CashOwners{get;private set;}=ImmutableHashSet<AccountingPositionKey>.Empty;
        internal ImmutableHashSet<Guid> CashIds{get;private set;}=ImmutableHashSet<Guid>.Empty;
        private ImmutableDictionary<Guid,(GasPhysicalKnownCash Cash,CashLifecycleProjection Adapter,bool Activate)> cashEvents=
            ImmutableDictionary<Guid,(GasPhysicalKnownCash,CashLifecycleProjection,bool)>.Empty;
        internal void Prepare(ref ImmutableDictionary<AccountingPositionKey,LifecycleProjectionState> members)
        {
            var principalDiscounting=parent.PrincipalDiscounting;
            RequirePrincipalPrefix();
            var needsPrincipal=!native.Cash.IsEmpty&&native.Terms.CashDueAt>SourceSnapshot.ValuationAt;
            if((!native.Performances.IsEmpty||!native.Stocks.IsEmpty)&&quantities is null||native.Cash.IsDefault||native.Cash.Any(c=>c is null)||
                needsPrincipal&&principalDiscounting is null||native.Cash.Select(c=>c.Owner).Distinct().Count()!=native.Cash.Length)throw Error();
            if(needsPrincipal)
            {
                if(native.Terms.Carrying==GasHoldingCarrying.CurrencyCurve)
                {
                    if(quantities?.PhysicalCarrying(native.DeliveryOwner) is not IProjectionCurrencyCurve expected||principalDiscounting is not IProjectionCurrencyCurve selected||
                        selected.Currency!=Currency||expected.Currency!=Currency||selected.CurveIdentity!=expected.CurveIdentity||!selected.SourceSnapshot.HasSameInputs(expected.SourceSnapshot)||
                        !selected.SourceSnapshot.HasSameInputs(Source)||selected.TimingPolicy!=RateDiscountTiming.Version)throw Error();
                }
                else
                {
                    var factors=principalDiscounting!.Resolve([new(native.Terms.Id,0,native.Terms.CashDueAt,Currency)],Source);
                    if(factors.Length!=1||factors[0].Source!=DiscountFactorSource.Undiscounted||factors[0].DiscountFactor!=ExactDecimal.Parse("1")||
                        !factors[0].Snapshot.HasSameInputs(Source))throw Error();
                }
            }
            var steps=native.PrincipalRuns.SelectMany(r=>r.Steps.Select(s=>(Run:r,Step:s))).ToArray();
            if(steps.Select(s=>s.Step.Id).Distinct().Count()!=steps.Length||steps.Any(s=>
                native.Cash.Count(c=>c.DeterminationId==s.Step.Id)!=(s.Step.Adjustment.Amount==default?0:1)))throw Error();
            foreach(var cash in native.Cash)
            {
                var selected=steps.SingleOrDefault(s=>s.Step.Id==cash.DeterminationId);var step=selected.Step;
                if(cash.Owner.Owner!=native.DeliveryOwner.Owner||cash.Owner.BookId!=Owner.BookId||cash.Owner.BindingId!=cash.Id||
                    cash.State.Position!=cash.Owner||cash.Quantity!=cash.State.Quantity||
                    cash.Quantity.Specification!=Currency||cash.Receipt.KnownAt>SourceSnapshot.Knowledge!.KnownAt||
                    step is null||cash.Quantity.Amount!=step.Adjustment.Amount||step.Adjustment.Currency!=Currency||
                    selected.Run.Receipt!=cash.Receipt||cash.Instrument.PaymentAt!=native.Terms.CashDueAt||
                    native.Performances.SingleOrDefault(p=>p.Id==step.PerformanceId)?.PerformedAt!=cash.EffectiveAt||
                    cash.State.KnownFlows(Source).Any(p=>p.EffectiveAt<cash.EffectiveAt))throw Error();
                var adapter=new CashLifecycleProjection(cash.Owner,cash.Instrument,cash.State,principalDiscounting,Source);
                if(members.ContainsKey(cash.Owner))throw Error();
                var future=cash.EffectiveAt>SourceSnapshot.ValuationAt;
                var member=future?new LifecycleProjectionState([],[]){KnownResidualOwners=[cash.Owner]}:adapter.Opening;
                members=members.Add(cash.Owner,member with{KnownCashSources=[cash.State],
                    KnownPayments=[..cash.State.KnownFlows(Source).Where(p=>p.EffectiveAt>SourceSnapshot.ValuationAt)]});
                var id=LifecycleProjectionEvent.ScopedId(cash.Owner,cash.Id);
                var performanceId=LifecycleProjectionEvent.ScopedId(native.DeliveryOwner,step.PerformanceId);
                var performance=native.Performances.Single(p=>p.Id==step.PerformanceId);
                if(future&&(performance.Retracted?quantities?.Events.Any(e=>e.Id==performanceId)==true:
                    quantities?.Events.Any(e=>e.Id==performanceId&&e.At==cash.EffectiveAt)!=true))throw Error();
                // A retired title has no quantity effect. Its certified principal
                // and offsetting claims keep their original membership instants.
                IEnumerable<Guid> requires=performance.Retracted?[]:[performanceId];
                if(cash.EffectiveAt==native.EffectiveAt)requires=requires.Append(LifecycleProjectionEvent.ScopedId(Owner,native.FinalOccurrence.Id));
                var birth=new LifecycleProjectionEvent(id,cash.EffectiveAt,[..requires.Distinct()],"known_physical_principal");
                if(future){Events=Events.Add(id,birth);cashEvents=cashEvents.Add(id,(cash,adapter,true));}
                foreach(var due in adapter.Events)
                {
                    if(due.At<cash.EffectiveAt)continue; // Already-due actual claims activate unpaid at their real origin.
                    Events=Events.Add(due.Id,due with{Requires=future&&due.At==cash.EffectiveAt?[id]:due.Requires});
                    cashEvents=cashEvents.Add(due.Id,(cash,adapter,false));CashIds=CashIds.Add(due.Id);
                }
                CashOwners=CashOwners.Add(cash.Owner);
            }
        }
        private void RequirePrincipalPrefix()
        {
            if(native.Performances.IsDefault||native.PrincipalRuns.IsDefault||native.Cash.IsDefault||native.Performances.Any(p=>p is null)||
                native.PrincipalRuns.Any(r=>r is null)||native.Performances.Select(p=>p.Id).Distinct().Count()!=native.Performances.Length)throw Error();
            if(native.Performances.IsEmpty)
            {if(!native.PrincipalRuns.IsEmpty||!native.Cash.IsEmpty)throw Error();return;}
            var runs=native.PrincipalRuns.OrderBy(r=>r.Revision).ToArray();
            if(runs.Length==0||!runs.Select(r=>r.Revision).SequenceEqual(Enumerable.Range(1,runs.Length)))throw Error();
            var performances=native.Performances.ToDictionary(p=>p.Id);
            foreach(var run in runs)
            {
                var final=native.Finals.SingleOrDefault(f=>f.DeterminationId==run.FinalDeterminationId);
                if(final is null||run.SourceContextId==Guid.Empty||!TradingInstant.IsSupported(run.AsOf)||run.Steps.IsDefaultOrEmpty||
                    run.Receipt.PublicationOrder<final.Receipt.PublicationOrder||run.Receipt.KnownAt<final.Receipt.KnownAt||run.Receipt.KnownAt>SourceSnapshot.Knowledge!.KnownAt||
                    run.Steps.Any(s=>s is null||s.Id==Guid.Empty||!performances.TryGetValue(s.PerformanceId,out var p)||s.PerformanceRevision<1||
                        s.PerformanceRevision>p.FinancialRevision||p.PerformedAt>run.AsOf)||run.Steps.Select(s=>s.PerformanceId).Distinct().Count()!=run.Steps.Length)throw Error();
            }
            for(var index=1;index<runs.Length;index++)
                if(runs[index].Receipt.PublicationOrder<runs[index-1].Receipt.PublicationOrder||runs[index].Receipt.KnownAt<runs[index-1].Receipt.KnownAt||
                    runs[index].Receipt.PublicationOrder==runs[index-1].Receipt.PublicationOrder&&runs[index].Receipt.KnownAt!=runs[index-1].Receipt.KnownAt)throw Error();
            var latest=runs[^1];
            if(latest.FinalDeterminationId!=native.CurrentFinal.DeterminationId||!latest.Steps.Select(s=>s.PerformanceId).ToHashSet().SetEquals(performances.Keys)||
                latest.Steps.Any(s=>s.PerformanceRevision!=performances[s.PerformanceId].FinancialRevision||latest.Receipt.KnownAt<performances[s.PerformanceId].Receipt.KnownAt))throw Error();
            var previous=runs.Take(runs.Length-1).SelectMany(r=>r.Steps.Select(s=>(r.Revision,Step:s))).GroupBy(s=>s.Step.PerformanceId)
                .ToDictionary(g=>g.Key,g=>g.OrderByDescending(s=>s.Revision).First().Step);
            var expected=GasDeliveryPrincipal.PlanRetained(native.OriginalQuantity,native.CurrentFinal.Price,native.PrincipalRounding,native.PrincipalScope,
                [..native.Performances.Select(p=>new GasDeliveryPrincipalPerformance(p.Id,p.BusinessReference,p.PerformedAt,p.SignedQuantity))],
                [..native.Performances.Where(p=>p.Retracted).Select(p=>p.Id)],[..previous.Select(p=>new GasDeliveryPrincipalPrevious(p.Key,p.Value.Contribution))]);
            var steps=latest.Steps.ToDictionary(s=>s.PerformanceId);Guid? basis=null;
            foreach(var planned in expected)
            {
                var retained=steps[planned.Performance.Id];
                if(retained.PreviousDeterminationId!=previous.GetValueOrDefault(planned.Performance.Id)?.Id||retained.BasisDeterminationId!=basis||
                    retained.CumulativeQuantity!=planned.CumulativeQuantity||retained.ExactCumulative!=planned.ExactCumulative||
                    retained.RoundedCumulative!=planned.RoundedCumulative||retained.Contribution!=planned.Contribution||retained.Adjustment!=planned.Adjustment)throw Error();
                basis=retained.Id;
            }
        }
        internal LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
        {
            if(!Events.TryGetValue(occurrence.Id,out var declared)||declared!=occurrence||!cashEvents.TryGetValue(occurrence.Id,out var selected)||
                !state.Members.TryGetValue(selected.Cash.Owner,out var member))throw Error();
            if(selected.Activate)
            {
                if(!member.Residuals.IsEmpty)throw Error();
                member=member with{Residuals=selected.Adapter.Opening.Residuals};
            }
            else member=selected.Adapter.Apply(selected.Adapter.Events.Single(e=>e.Id==occurrence.Id),member,context);
            return LifecycleProjectionState.Compose(state.Members.SetItem(selected.Cash.Owner,member)) with{CompletedEvents=state.CompletedEvents};
        }
    }
}
