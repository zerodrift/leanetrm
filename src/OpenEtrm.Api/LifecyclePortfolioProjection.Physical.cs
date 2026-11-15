using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static partial class LifecyclePortfolioProjection
{
    private static KnownFuturesPhysicalFinal PhysicalFinal(GasPhysicalKnownAttachment native,FuturesKnownHistory history,MarketSnapshot source,FuturesContract future)
    {
        var terms=history.Terms;var policy=terms.Policy;
        if(history.Owner!=native.FutureOwner||terms.Owner!=new MeasureBinding(native.FutureOwner.Owner,native.FutureOwner.BindingId)||
            terms.BookId!=native.FutureOwner.BookId||history.SourceContextId!=source.Knowledge?.ContextId||history.KnownThrough!=source.Knowledge.KnownAt||
            native.SourceSnapshot.Knowledge!=source.Knowledge||native.SourceSnapshot.ValuationAt!=source.ValuationAt||
            native.SourceSnapshot.MarketCutoff!=source.MarketCutoff||policy.Id!=native.PolicyId||policy.Revision!=native.PolicyRevision||
            policy.Delivery!=native.Configuration||!policy.Occurrences.Contains(native.FinalOccurrence))
            throw Error("projection_native_delivery","Retain the exact native future, adopted physical policy and accepted final source.");
        var determination=history.Determinations.SingleOrDefault(d=>d.Occurrence.Id==native.FinalOccurrence.Id)??
            throw Error("projection_native_delivery","Retain this delivery owner's complete accepted final history.");
        var plan=FuturesFinalDeliveryPlan.Create(future,terms.Quantity,native.Terms,native.CurrentFinal.Price);
        if(plan.Quantity!=native.OriginalQuantity)throw Error("projection_native_delivery","Reconcile the native obligation quantity with the exact parent future lots and multiplier.");
        return KnownFuturesPhysicalFinal.Create(native,determination);
    }
    private static void RetainPhysicalCarrying(ImmutableArray<LifecycleSourceComponents>.Builder sources,PortfolioValuation.Prepared opening)
    {
        var futures=opening.Sources.SelectMany(s=>s.Portfolio.Setup.FuturesValueBindings.Values).Select(b=>b.Physical).Where(p=>p is not null)
            .Select(p=>(Source:p!.Asset.Snapshot,Provider:p.Carrying));
        var options=opening.Sources.SelectMany(s=>s.Portfolio.Setup.PhysicalOptionValueBindings.Values).Select(p=>(Source:p.Asset.Snapshot,Provider:p.Carrying));
        foreach(var (snapshot,provider) in futures.Concat(options).Where(p=>p.Provider is not null))RetainCarrying(sources,snapshot,provider!);
    }
    private static void RetainCarrying(ImmutableArray<LifecycleSourceComponents>.Builder sources,MarketSnapshot snapshot,ISideDiscounting provider)
    {
        var index=SourceIndex(sources,snapshot);var part=sources[index];
        if(provider is not IProjectionCurrencyCurve carrying||!carrying.SourceSnapshot.HasSameInputs(snapshot))
            throw Error("projection_delivery_source","Retain physical carrying under its exact selected asset market source.");
        if(part.Curves.TryGetValue(carrying.Currency,out var existing))
        {
            if(existing is not IProjectionCurrencyCurve expected||expected.CurveIdentity!=carrying.CurveIdentity||
                !expected.SourceSnapshot.HasSameInputs(carrying.SourceSnapshot))
                throw Error("projection_cash_policy","One exact source and currency cannot have conflicting selected carrying curves.");
        }
        else sources[index]=part with{Curves=part.Curves.Add(carrying.Currency,provider)};
    }
    private static void ComposePhysical(ImmutableArray<LifecycleSourceComponents>.Builder sources,PortfolioValuation.Prepared opening,
        GasPhysicalKnownRead? physical,ImmutableDictionary<AccountingPositionKey,KnownFuturesPhysicalFinal> finals,
        Dictionary<AccountingPositionKey,FuturesLifecycleProjection> children,GasOwnedProjectionPrepared gas,
        ImmutableArray<LifecycleEconomicOwnerDescription>.Builder additional)
    {
        var components=gas.Components.ToBuilder();var absorbed=new HashSet<AccountingPositionKey>();
        if(physical is not null)
        foreach(var plan in gas.QuantityPlans)
        {
            var quantities=plan.Build();if(!quantities.RequiresPhysicalComposition)continue;
            var physicalOwners=plan.OpeningHoldings.Where(h=>h.Kind==GasProjectionHoldingKind.PhysicalDelivery).Select(h=>h.Position)
                .Concat(plan.KnownTransitions.SelectMany(t=>(t.PhysicalOrigins??[]).Select(o=>o.Holding.Position))).ToHashSet();
            var parents=physicalOwners.Select(owner=>
            {
                var native=physical.Attachments.SingleOrDefault(a=>a.DeliveryOwner==owner)??
                    throw Error("projection_native_delivery","Compose every exact native physical parent in this shared resource plan.");
                if(!finals.TryGetValue(native.FutureOwner,out var accepted))
                    throw Error("projection_native_delivery","Retain each physical parent's accepted native future final proof.");
                ISideDiscounting? provider=null;
                if(native.Terms.Carrying==GasHoldingCarrying.Nominal)provider=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
                else provider=plan.OpeningHoldings.Concat(plan.KnownTransitions.SelectMany(t=>(t.PhysicalOrigins??[]).Select(o=>o.Holding)))
                    .Single(h=>h.Position==owner).Discounting;
                children.TryGetValue(native.FutureOwner,out var child);
                if(child is not null)absorbed.Add(child.Owner);
                foreach(var cash in native.Cash)absorbed.Add(cash.Owner);
                return new GasPhysicalProjectionParent(child,native,accepted,provider);
            }).ToImmutableArray();
            components.Add(new GasPhysicalLifecycleProjection(parents,quantities));
        }
        for(var index=0;index<sources.Count;index++)
        {
            var part=sources[index];sources[index]=part with{Components=part.Components.Where(c=>!absorbed.Contains(c.Owner)).ToImmutableArray()
                .AddRange(components.Where(c=>c.SourceSnapshot.HasSameInputs(part.Source.Snapshot)))};
        }
        if(components.Any(c=>!sources.Any(s=>s.Source.Snapshot.HasSameInputs(c.SourceSnapshot))))
            throw Error("projection_source","Every connected resource requires its explicit original source partition.");
        var original=opening.Positions.Select(p=>new AccountingPositionKey(p.Owner,p.BindingId,p.BookId)).ToHashSet();
        var owners=sources.SelectMany(s=>s.Components).SelectMany(c=>c.EconomicOwners).ToHashSet();
        var descriptions=gas.AdditionalOwners.ToDictionary(d=>d.Owner);
        if(physical is not null)
        foreach(var native in physical.Attachments)
        foreach(var cash in native.Cash.Where(c=>owners.Contains(c.Owner)&&!original.Contains(c.Owner)))
        {
            var step=native.PrincipalRuns.SelectMany(r=>r.Steps).Single(s=>s.Id==cash.DeterminationId);
            var performance=native.Performances.Single(p=>p.Id==step.PerformanceId);
            descriptions.TryAdd(cash.Owner,new(cash.Owner,performance.BusinessReference,"Physical delivery principal"));
        }
        additional.AddRange(descriptions.Values.Where(d=>owners.Contains(d.Owner)&&!original.Contains(d.Owner)));
    }
    private static LifecycleMarketSource OriginalMarketSource(PortfolioValuation.Prepared opening,MarketSnapshot snapshot)
    {
        if(opening.InputMarkets.SingleOrDefault(m=>m.Snapshot.HasSameInputs(snapshot)) is {} input)return new(input.Data,input.Frame,input.Snapshot);
        if(opening.Markets.SingleOrDefault(m=>m.Snapshot.HasSameInputs(snapshot)) is {} held)return new(held.Data,held.Frame,held.Snapshot);
        throw Error("projection_delivery_source","Retain the exact independently admitted native source for every physical branch.");
    }
    private static CustomCurves.PreparedMarket OriginalMarket(PortfolioValuation.Prepared opening,MarketSnapshot snapshot,ImmutableArray<CurveRequest> required)
    {
        var market=opening.InputMarkets.SingleOrDefault(m=>m.Snapshot.HasSameInputs(snapshot))?.Market??
            opening.Markets.SingleOrDefault(m=>m.Snapshot.HasSameInputs(snapshot))?.InputMarket??
            throw Error("projection_delivery_source","Retain this foreign branch's original independently prepared market graph.");
        if(!market.Valuation.Snapshot.HasSameInputs(snapshot)||!market.Authority.Snapshot.HasSameInputs(snapshot)||required.Any(d=>!market.Bindings.ContainsKey(d)))
            throw Error("projection_delivery_source","The original native market graph must declare every selected branch input under its exact source certificate.");
        return market;
    }
    private static int SourceIndex(ImmutableArray<LifecycleSourceComponents>.Builder sources,MarketSnapshot snapshot)
    {
        var matching=Enumerable.Range(0,sources.Count).Where(i=>sources[i].Source.Snapshot.HasSameInputs(snapshot)).ToArray();
        if(matching.Length!=1)throw Error("projection_delivery_source","Retain one exact independently admitted source for each physical branch.");
        return matching[0];
    }
}
