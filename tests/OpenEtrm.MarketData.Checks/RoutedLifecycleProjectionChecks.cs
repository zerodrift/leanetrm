using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class RoutedLifecycleProjectionChecks
{
    internal static void Run()
    {
        var f=new Fixture();var a=f.Snapshot().WithKnowledge(Guid.NewGuid(),Fixture.Utc("2040-12-18T00:00:00Z"));
        var revision=new DefinitionRevision(f.Revision.CatalogId,Guid.NewGuid());var contract=f.Contracts[1] with{Key=new(revision,f.K(2).Id)};
        var request=new CurveRequest(new(revision,"inventory"),contract.Key);var series=new SeriesKey(revision,Guid.NewGuid());
        var curve=f.Direct("inventory",contract.Key,f.R("gas_out"),SettlementKind.Index) with{Key=request.Curve};
        var model=new CurveModel(revision,f.Reference,f.Quantities,[f.Quotes[0]],[contract],[curve],[],[],[new(series,contract.Key,f.R("gas_out"),null)]);
        var observation=new MarketObservation(new(revision.CatalogId,Guid.NewGuid()),series,f.R("source"),ObservationRole.TraderMark,PublicationState.Final,a.ValuationAt,ExactDecimal.Parse("5"));
        var b=new MarketSnapshot(Guid.NewGuid(),revision,a.ValuationAt,a.MarketCutoff,a.SelectionPolicy,[new(new(series,InputUse.Variable),observation)]).WithKnowledge(a.Knowledge!.ContextId,a.Knowledge.KnownAt);
        var at=a.ValuationAt.AddHours(4);var end=a.ValuationAt.AddHours(8);
        var horizon=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(new(2040,12,18),end,0)]);
        var policy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Linear,LifecycleProjectionPolicy.Outstanding,
            LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
        var owner=new AccountingPositionKey(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        var component=new Transfer(owner,f.R("currency"),a,b,request,at,end.AddHours(4),new Curve(b,f.R("currency"),end,end.AddHours(4)));
        PreparedLifecycleProjection Prepare(Transfer selected,ImmutableArray<LifecycleProjectionMarket>? routes=null)=>LifecycleProjection.Prepare(f.Model(),a,horizon,policy,[selected],[],
            additionalMarkets:routes??[new(model.Build(b,[request]),ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(component.Currency,component.Provider),[])]);
        var prepared=Prepare(component);var factor=prepared.Factors.Single();
        var path=prepared.Evaluate("native_b",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]).Values.Single();
        // No opening position. The B stock is 7 at H; B's explicit future
        // cash carrying is .6/.8, giving 9.25, with no A-source substitution.
        Equal(path.OpeningValue,"0");Equal(path.ResidualValue,"9.25");Equal(path.EconomicPnl,"9.25");
        // Once admitted, horizon PV consumes the immutable route receipt; it
        // does not ask a selector to reinterpret earlier cash or stock.
        Equal(Prepare(component with{LateSelection=true}).Evaluate("retained_routes",[]).Values.Single().ResidualValue,"7.25");
        var absent=new DefinitionRevision(f.Revision.CatalogId,Guid.NewGuid());
        var absentMarket=new CurveRequest(new(absent,"absent"),new(absent,Guid.NewGuid()));
        Reject("projection_input_expiry",()=>Prepare(component with{InputFinals=[new(absentMarket,end,null)]}));
        var aFinal=new LifecycleInputFinal(new(f.C("future"),f.K(1)),f.Contracts[0].Expiry,null);
        Reject("projection_input_expiry",()=>Prepare(component,[new(model.Build(b,[request]),ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(component.Currency,component.Provider),[aFinal])]));
        Reject("projection_source",()=>Prepare(component,[]));
        Reject("projection_source",()=>Prepare(component with{DeclaredB=false}));
        Reject("projection_source",()=>Prepare(component with{CashInB=false}).Evaluate("wrong_cash_source",[]));
        Reject("projection_source",()=>Prepare(component with{ResidualInB=false}).Evaluate("wrong_residual_source",[]));
        Reject("projection_source",()=>Prepare(component with{CashChangesSource=true}).Evaluate("cash_source_rewrite",[]));
        Console.WriteLine("PASS routed lifecycle projection: complete foreign factor, ordinary B residual and exact B cash carrying, missing/wrong/rewritten routes refused");
    }
    private static void Equal(ExactMoney actual,string expected){if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected routed wealth {expected}, got {actual.ToExactString()}.");}
    private static void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected routed projection refusal "+code);}
    private sealed record Transfer(AccountingPositionKey Owner,ReferenceKey Currency,MarketSnapshot SourceSnapshot,MarketSnapshot B,CurveRequest Mark,DateTimeOffset At,DateTimeOffset Due,ISideDiscounting Provider):ILifecycleProjection
    {
        public bool DeclaredB{get;init;}=true;public bool CashInB{get;init;}=true;public bool ResidualInB{get;init;}=true;public bool CashChangesSource{get;init;}public bool LateSelection{get;init;}
        private Guid First=>LifecycleProjectionEvent.ScopedId(Owner,Guid.Parse("50000000-0000-0000-0000-000000000001"));
        private Guid Second=>LifecycleProjectionEvent.ScopedId(Owner,Guid.Parse("50000000-0000-0000-0000-000000000002"));
        public string Implementation=>"synthetic_native_delivery_v1";
        public ImmutableArray<MarketSnapshot> MarketSources=>DeclaredB?[SourceSnapshot,B]:[SourceSnapshot];
        public MarketSnapshot DependencySource(CurveRequest market)=>B;
        public MarketSnapshot ValuationSource(AccountingPositionKey owner,Instrument instrument)=>ResidualInB&&(!LateSelection||++residualReads<=2)?B:SourceSnapshot;
        public MarketSnapshot CashSource(ProjectedCashContribution cash)=>CashInB&&(!CashChangesSource||!changed)&&(!LateSelection||++cashReads<=2)?B:SourceSnapshot;
        private bool changed;private int cashReads,residualReads;
        public ImmutableArray<LifecycleInputFinal> InputFinals{get;init;}=[];
        public ImmutableArray<CurveRequest> Dependencies=>[Mark];
        public LifecycleProjectionState Opening{get;}=new([],[]);
        public ImmutableArray<LifecycleProjectionEvent> Events=>[new(First,At,[],"receive"),new(Second,At.AddHours(1),[First],"retain")];
        public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)=>Events;
        public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
        {
            if(occurrence.Id==Second){changed=true;return state;}
            changed=false;cashReads=0;residualReads=0;_ =context.Price(Mark);
            var instrument=new GasCommodityHoldingInstrument(First,Mark,context.Price(Mark).Unit.Quantity,Currency,null,GasHoldingCarrying.Nominal);
            var quantity=instrument.UnitQuantity;
            var portfolio=new ValuationPortfolio([instrument],[new(Owner.Owner,Owner.BindingId,Owner.BookId,instrument.Id,quantity,new(new Money(default,Currency),quantity.Specification),new(default,Currency))],
                new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
            return new([portfolio],[new(First,At,Due,ExactMoney.Parse("3",Currency))]);
        }
    }
    private sealed class Curve(MarketSnapshot source,ReferenceKey currency,DateTimeOffset horizon,DateTimeOffset due):IProjectionCurrencyCurve
    {
        public ReferenceKey Currency=>currency;public MarketSnapshot SourceSnapshot=>source;
        public ProjectionCurrencyCurveKey CurveIdentity{get;}=new(new(source.Definitions,"synthetic_rates"),Guid.NewGuid(),Guid.NewGuid(),1);
        public string TimingPolicy=>RateDiscountTiming.Version;
        private ExactDecimal Factor(DateTimeOffset at)=>ExactDecimal.Parse(at==due?"0.6":at==horizon?"0.8":"1");
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot selected)=>[..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt),selected,DiscountFactorSource.Explicit))];
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> payments,MarketSnapshot selected,DateTimeOffset at,string? clockPolicy)=>
            [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt).DivideExact(Factor(at)),selected,DiscountFactorSource.Explicit))];
    }
}
