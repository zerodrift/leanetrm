using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class LifecycleCurrencyChecks
{
    internal static int Run()
    {
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string code)=>new(reference,code);
        var midnight=new DateTimeOffset(2031,1,1,0,0,0,TimeSpan.Zero);var at=midnight.AddHours(6);var horizon=at.AddDays(1);var due=midnight.AddDays(2).AddHours(12);
        var series=Enumerable.Range(0,4).Select(_=>new SeriesKey(revision,Guid.NewGuid())).ToArray();
        var observations=series.Select(s=>new MarketObservation(new(revision.CatalogId,Guid.NewGuid()),s,R("source"),ObservationRole.Parameter,
            PublicationState.Final,midnight,ExactDecimal.Parse("0.36"))).ToArray();
        var source=new MarketSnapshot(Guid.NewGuid(),revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [..observations.Select(o=>new SelectedObservation(new(o.Series,InputUse.Variable),o))]).WithKnowledge(Guid.NewGuid(),at);
        var definition=new RateCurveDefinition(new(revision,"rates"),R("usd"),R("source"),
            RateConventionSet.Parse("actual_360","simple","linear_zero","none","decimal_per_year"),
            [..new[]{1,2,4,8}.Select((n,i)=>new RateTenor(n+"D",series[i],new DateOnly(2031,1,1).AddDays(n)))],Guid.NewGuid(),1);
        var mapping=new CurrencyDiscountCurve(Guid.NewGuid(),R("usd"),definition);
        var payment=new SidePaymentKey(Guid.NewGuid(),0,due,R("usd"));
        ISideDiscounting provider=new LifecycleCurrencyDiscounting(mapping,source,RateDiscountTiming.Version,ClockChange.FrozenInputs);
        if(provider.ResolveForValuation([payment],source,source.ValuationAt,null).Single().DiscountFactor.ToString()!="0.997755672276703731")
            throw new Exception("Ordinary opening PV must use the selected exact-instant curve without requiring a nonexistent clock roll.");
        Reject("discount_context",()=>provider.ResolveForValuation([payment],source,horizon,null));
        Reject("discount_context",()=>provider.ResolveForValuation([payment],source,source.ValuationAt,"unsupported_clock"));
        var conditional=provider.ResolveForValuation([payment],source,horizon,ClockChange.FrozenInputs).Single();
        // Independent source-day simple discount factors are rounded to18 places,
        // linearly interpolated at each instant, then divided once, half-even.
        if(conditional.DiscountFactor.ToString()!="0.9987531789458179"||conditional.RateDefinition?.Timing?.CalculationAt!=horizon||
            !conditional.Snapshot.HasSameInputs(source)||conditional.RateDefinition.Pillars.Length!=3)
            throw new Exception("Lifecycle currency cash lost its exact conditional factor, actual source pins or required pillar geometry.");
        var component=new CashProjection(new(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),Guid.NewGuid()),R("usd"),at.AddHours(1),due,source);
        var model=new CurveModel(revision,reference,[],[],[],[],[],[new(R("rate"),NumericKind.Dimensionless,null,null,ExactDecimal.Parse("-0.1"),ExactDecimal.Parse("1"))],
            series.Select(s=>new MarketSeries(s,null,null,R("rate"))));
        var policy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Linear,LifecycleProjectionPolicy.PayAtDue,
            LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry);
        var end=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_close",1),R("calendar"),[new(new(2031,1,2),horizon,0)]);
        string Value(ISideDiscounting selected)
        {
            var prepared=LifecycleProjection.Prepare(model,source,end,policy,[component],[],ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(R("usd"),selected));
            return prepared.Evaluate("cash",[]).Values.Single().ResidualValue.ToExactString();
        }
        if(Value(provider)!="99.87531789458179")throw new Exception("Ordinary cash PV did not consume the conditional horizon factor.");
        Reject("discount_curve",()=>Value(new Changed((IProjectionCurrencyCurve)provider,f=>f with{DiscountFactor=ExactDecimal.Parse("1")})));
        Reject("discount_timing",()=>Value(new Changed((IProjectionCurrencyCurve)provider,f=>f with{RateDefinition=f.RateDefinition! with{
            Timing=f.RateDefinition.Timing! with{CalculationAt=horizon.AddHours(1)}}})));
        Reject("discount_curve",()=>Value(new Changed((IProjectionCurrencyCurve)provider,f=>f with{RateDefinition=f.RateDefinition! with{
            Pillars=f.RateDefinition.Pillars.RemoveAt(1)}})));
        Reject("discount_context",()=>provider.Resolve([payment],source.WithKnowledge(Guid.NewGuid(),source.Knowledge!.KnownAt)));
        Reject("valuation_time_event",()=>provider.ResolveForValuation([payment],source,due,ClockChange.FrozenInputs));
        var second=new SidePaymentKey(Guid.NewGuid(),1,due,R("usd"));
        if(provider.Resolve([second],source).Single().DiscountFactor.ToString()!="0.997755672276703731")
            throw new Exception("A deterministic new cash membership could not reuse its captured currency term structure.");
        Console.WriteLine("PASS lifecycle currency curve: exact intraday conditional cash and retained three-pillar source evidence");
        return 1;
    }
    private sealed record CashProjection(AccountingPositionKey Owner,ReferenceKey Currency,DateTimeOffset At,DateTimeOffset Due,MarketSnapshot SourceSnapshot):ILifecycleProjection
    {
        public string Implementation=>"synthetic_currency_cash_projection_v1";
        public ImmutableArray<CurveRequest> Dependencies=>[];
        public LifecycleProjectionState Opening=>new([],[]);
        public ImmutableArray<LifecycleProjectionEvent> Events=>[new(LifecycleProjectionEvent.ScopedId(Owner,Guid.Parse("22000000-0000-0000-0000-000000000001")),At,[],"cash")];
        public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)=>[..Events.Where(e=>e.At<=horizon)];
        public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)=>
            state with{Cash=state.Cash.Add(new(occurrence.Id,At,Due,ExactMoney.Parse("100",Currency)))};
    }
    private sealed class Changed(IProjectionCurrencyCurve source,Func<SidePaymentFactor,SidePaymentFactor> change):IProjectionCurrencyCurve
    {
        public ReferenceKey Currency=>source.Currency;
        public MarketSnapshot SourceSnapshot=>source.SourceSnapshot;
        public ProjectionCurrencyCurveKey CurveIdentity=>source.CurveIdentity;
        public string TimingPolicy=>source.TimingPolicy;
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> keys,MarketSnapshot snapshot)=>[..source.Resolve(keys,snapshot).Select(change)];
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> keys,MarketSnapshot snapshot,DateTimeOffset at,string? policy)=>
            [..source.ResolveForValuation(keys,snapshot,at,policy).Select(change)];
    }
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
