using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class CashAccumulationSelectionChecks
{
    internal static void Run()
    {
        var f=new Fixture();var source=f.Snapshot().WithKnowledge(Guid.NewGuid(),f.Snapshot().ValuationAt);
        var at=source.ValuationAt;var currency=f.R("currency");
        var sourceC=source with{Definitions=new(source.Definitions.CatalogId,Guid.NewGuid()),Id=Guid.NewGuid(),Selections=[]};
        var residual=new Curve(source,currency,"0.8","0.75","0.6");
        var funding=new Curve(sourceC,currency,"0.95","0.8","0.7");
        var owner=new AccountingPositionKey(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        var component=new Cash(owner,currency,source,at.AddHours(4),ExactMoney.Parse("-101/40",currency));
        var horizon=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(new(2040,12,18),at.AddHours(8),0)]);
        var policy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,
            LifecycleProjectionPolicy.PayAtDue,LifecycleProjectionPolicy.Accumulated,LifecycleProjectionPolicy.Numerics,
            LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
        var local=ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(currency,residual);
        var selected=new LifecycleCashAccumulation(LifecycleCashAccumulation.Version,
            ImmutableDictionary<ReferenceKey,IProjectionCurrencyCurve>.Empty.Add(currency,funding));
        PreparedLifecycleProjection Prepare(LifecycleCashAccumulation? choice,ILifecycleProjection? cash=null,LifecycleProjectionPolicy? p=null)=>
            LifecycleProjection.Prepare(f.Model(),source,horizon,p??policy,[cash??component],[],local,cashAccumulation:choice);
        var value=Prepare(selected).Evaluate("independent_funding",[]).Values.Single();
        // Settled cash uses C's 19/16 growth, while unpaid 10 uses A's 4/5.
        Equal(value.PeriodCash,"-1919/640");Equal(value.ResidualValue,"8");
        if(!ReferenceEquals(funding.Seen,sourceC)||!ReferenceEquals(residual.Seen,source))
            throw new Exception("Cash funding or residual carrying relabelled its original source.");
        Equal(Prepare(null).Evaluate("legacy",[]).Values.Single().PeriodCash,"-202/75");
        Equal(Prepare(selected,component with{Amount=ExactMoney.Parse("101/40",currency)}).Evaluate("signed",[]).Values.Single().PeriodCash,"1919/640");
        var nominal=policy with{CashAccumulation=LifecycleProjectionPolicy.Nominal};
        Equal(Prepare(selected with{Curves=selected.Curves.Clear()},p:nominal).Evaluate("nominal",[]).Values.Single().PeriodCash,"-101/40");
        Reject(()=>Prepare(selected with{Curves=selected.Curves.Clear()}));
        Reject(()=>Prepare(selected with{Implementation="synthetic_unsupported"}));
        Reject(()=>Prepare(selected,p:nominal));
        Reject(()=>Prepare(selected with{Curves=selected.Curves.Add(f.R("another_currency"),funding)}));
        foreach(var bad in new[]{sourceC.WithKnowledge(Guid.NewGuid(),at),sourceC with{ValuationAt=at.AddHours(-1)},sourceC with{MarketCutoff=at.AddHours(-1)}})
            Reject(()=>Prepare(selected with{Curves=selected.Curves.SetItem(currency,new Curve(bad,currency,"0.95","0.8","0.7"))}));
        var otherRevision=new ReferenceKey(new(currency.Revision.CatalogId,Guid.NewGuid()),currency.Code);
        Reject(()=>Prepare(selected with{Curves=selected.Curves.Clear().Add(otherRevision,funding)}));
        var zero=component with{Events=[]};
        Reject(()=>Prepare(selected with{Curves=selected.Curves.Clear()},zero));
        Equal(Prepare(selected,zero).Evaluate("zero_cash_path",[]).Values.Single().PeriodCash,"0");
        var other=component with{Owner=new(Guid.NewGuid(),Guid.NewGuid(),owner.BookId)};
        var deterministic=new LifecycleEconomicRiskPolicy(LifecycleEconomicRiskPolicy.Deterministic,"0.99",HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail);
        Equal(LifecycleEconomicRisk.Deterministic([Prepare(selected),Prepare(selected,other)],deterministic).NoShockTimeCarry[1].PeriodCash,"-1919/640");
        foreach(var pair in new[]{new[]{Prepare(null),Prepare(selected,other)},
            new[]{Prepare(selected),Prepare(selected with{Curves=selected.Curves.SetItem(currency,new Curve(sourceC with{Id=Guid.NewGuid()},currency,"0.95","0.8","0.7"){CurveIdentity=funding.CurveIdentity})},other)}})
        {
            try{LifecycleEconomicRisk.Deterministic([..pair],deterministic);throw new Exception("A portfolio mixed cash accumulation profiles or source receipts.");}
            catch(MarketDataException e)when(e.Code=="risk_economic_scope"){}
        }
        var rateObservation=new MarketObservation(new(sourceC.Definitions.CatalogId,Guid.NewGuid()),new(sourceC.Definitions,Guid.NewGuid()),
            f.R("source"),ObservationRole.Parameter,PublicationState.Final,at,default);
        var nativeSource=sourceC with{Selections=[new(new(rateObservation.Series,InputUse.Variable),rateObservation)]};
        var native=new NativeCurve(nativeSource,currency,rateObservation);
        LifecycleCashAccumulation Native(NativeCurve curve)=>selected with{Curves=selected.Curves.SetItem(currency,curve)};
        Equal(Prepare(Native(native)).Evaluate("native_funding",[]).Values.Single().PeriodCash,"-101/40");
        foreach(var changed in new[]{"mapping","curve","definition","version","timing"})
            Reject(()=>Prepare(Native(native with{Changed=changed})).Evaluate("substituted_native_curve",[]));
        Console.WriteLine("PASS explicit cash accumulation: independent retained source, signed rational funding, residual carrying, exact currency coverage and legacy profile");
    }
    private static void Equal(ExactMoney actual,string expected)
    {if(actual.CompareTo(ExactMoney.Parse(expected,actual.Currency))!=0)throw new Exception($"Expected {expected}; got {actual.ToExactString()}.");}
    private static void Reject(Action action)
    {try{action();}catch(MarketDataException e)when(e.Code=="projection_cash_policy"){return;}throw new Exception("Expected projection_cash_policy.");}
    private sealed record Cash(AccountingPositionKey Owner,ReferenceKey Currency,MarketSnapshot SourceSnapshot,DateTimeOffset At,ExactMoney Amount):ILifecycleProjection
    {
        public string Implementation=>"synthetic_explicit_cash_v1";
        public ImmutableArray<CurveRequest> Dependencies=>[];
        public LifecycleProjectionState Opening=>new([],[]);
        public ImmutableArray<LifecycleProjectionEvent> Events{get;init;}=[new(Guid.NewGuid(),At,[],"cash")];
        public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)=>[..Events.Where(e=>e.At<=horizon)];
        public LifecycleProjectionState Apply(LifecycleProjectionEvent evt,LifecycleProjectionState state,LifecycleProjectionContext context)=>state with{
            Cash=[new(evt.Id,evt.At,evt.At,Amount),new(LifecycleProjectionEvent.ScopedId(Owner,evt.Id),evt.At,evt.At.AddHours(8),ExactMoney.Parse("10",Currency))]};
    }
    private sealed class Curve(MarketSnapshot source,ReferenceKey currency,string at4,string at8,string at12):IProjectionCurrencyCurve
    {
        public ReferenceKey Currency=>currency;
        public MarketSnapshot SourceSnapshot=>source;
        public MarketSnapshot? Seen{get;private set;}
        public ProjectionCurrencyCurveKey CurveIdentity{get;init;}=new(new(source.Definitions,"synthetic_rates"),Guid.NewGuid(),Guid.NewGuid(),1);
        public string TimingPolicy=>RateDiscountTiming.Version;
        private ExactDecimal Factor(DateTimeOffset at)=>ExactDecimal.Parse(at==source.ValuationAt.AddHours(4)?at4:at==source.ValuationAt.AddHours(8)?at8:at12);
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)
        {Seen=snapshot;return [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt),snapshot,DiscountFactorSource.Explicit))];}
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot,DateTimeOffset at,string? policy)
        {Seen=snapshot;return [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt).DivideExact(Factor(at)),snapshot,DiscountFactorSource.Explicit))];}
    }
    private sealed record NativeCurve(MarketSnapshot SourceSnapshot,ReferenceKey Currency,MarketObservation Observation):IProjectionCurrencyCurve
    {
        public string? Changed{get;init;}
        public string TimingPolicy=>RateDiscountTiming.Version;
        public ProjectionCurrencyCurveKey CurveIdentity{get;}=new(new(SourceSnapshot.Definitions,"synthetic_rates"),Guid.NewGuid(),Guid.NewGuid(),1);
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)=>[..payments.Select(payment=>
        {
            var day=DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime);var maturity=day.AddDays(5);
            var definition=new RateDiscountDefinition(Changed=="mapping"?Guid.NewGuid():CurveIdentity.MappingId,
                Changed=="curve"?new(SourceSnapshot.Definitions,"another_curve"):CurveIdentity.Curve,Currency,Observation.Source,
                day,DateOnly.FromDateTime(payment.PaymentAt.UtcDateTime),RateConventionSet.Parse("actual_365_fixed","simple","linear_zero","flat_zero","decimal_per_year"),
                RateCurveMath.Policy,[new(maturity,Observation)]){Timing=Changed=="timing"?null:new(snapshot.ValuationAt,payment.PaymentAt,RateDiscountTiming.Version),
                CurveTerms=Changed=="timing"?null:new(Changed=="definition"?Guid.NewGuid():CurveIdentity.DefinitionId,
                    Changed=="version"?2:CurveIdentity.DefinitionVersion,[new(Observation.Series,maturity)])};
            return new SidePaymentFactor(payment,ExactDecimal.Parse("1"),snapshot,DiscountFactorSource.CurrencyDefault){RateDefinition=definition};
        })];
    }
}
