using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class LifecycleCompositeChecks
{
    internal static void Run()
    {
        var f=new Fixture();var source=f.Snapshot().WithKnowledge(Guid.NewGuid(),Fixture.Utc("2040-12-18T00:00:00Z"));
        var currency=f.R("currency");var book=Guid.NewGuid();var at=source.ValuationAt;
        var a=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),book);
        var b=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),book);
        LifecycleProjectionState Member(AccountingPositionKey owner,string amount)
        {
            var instrument=new TimedCashPayment(currency,at);var quantity=new Quantity(ExactDecimal.Parse(amount),currency);
            var actual=new RecordedCashValuation(owner,instrument,quantity,[],RecordedCashValuation.Policy,source.Knowledge!.ContextId,source.Knowledge.KnownAt);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null){
                BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(owner.Owner,owner.BindingId),actual)};
            var portfolio=new ValuationPortfolio([instrument],[new(owner.Owner,owner.BindingId,book,instrument.Id,quantity,
                new(new Money(default,currency),currency),new(default,currency))],setup);
            return new([portfolio],[]);
        }
        var component=new Composite(a,b,currency,source,LifecycleProjectionState.Compose(ImmutableDictionary<AccountingPositionKey,LifecycleProjectionState>.Empty
            .Add(a,Member(a,"100")).Add(b,Member(b,"50"))));
        var horizon=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(new(2040,12,18),at.AddHours(8),0)]);
        var policy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,
            LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice);
        var prepared=LifecycleProjection.Prepare(f.Model(),source,horizon,policy,[component],[]);
        var result=prepared.Evaluate("atomic_members",[]);
        void Equal(ExactMoney actual,string expected){if(actual.CompareTo(ExactMoney.Parse(expected,currency))!=0)throw new Exception($"Expected {expected}; got {actual.ToExactString()}.");}
        if(result.Values.Length!=2||result.Values.Select(v=>v.Owner).ToHashSet().SetEquals([a,b]) is false)throw new Exception("A composite erased its independent native economic owners.");
        Equal(result.Values.Single(v=>v.Owner==a).OpeningValue,"100");Equal(result.Values.Single(v=>v.Owner==a).EconomicPnl,"-100");
        Equal(result.Values.Single(v=>v.Owner==b).OpeningValue,"50");Equal(result.Values.Single(v=>v.Owner==b).EconomicPnl,"100");
        Equal(result.Values.Select(v=>v.EconomicPnl).Aggregate((x,y)=>x.Add(y)),"0");
        var risk=LifecycleEconomicRisk.Deterministic([prepared],new(LifecycleEconomicRiskPolicy.Deterministic,"0.975",HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail));
        if(risk.Economic.Single().ValueAtRisk!="0"||risk.Economic.Single().Trades.Single(t=>t.Owner==a.Owner).ValueAtRisk!="100"||
            risk.Economic.Single().Trades.Single(t=>t.Owner==b.Owner).ValueAtRisk!="-100")throw new Exception("Point-mass risk reassigned member losses or double-counted nested portfolios.");
        try
        {
            _=LifecycleProjection.Prepare(f.Model(),source,horizon,policy,[component with{Opening=component.Opening with{Residuals=[]}}],[]);
            throw new Exception("The composite top-level value omitted member residuals.");
        }
        catch(MarketDataException e)when(e.Code=="projection_composition"){}
        Console.WriteLine("PASS lifecycle composite: atomic member transition, exact separate owner P&L/tail contributions, no nested double PV and complete aggregate proof");
    }
    private sealed record Composite(AccountingPositionKey Owner,AccountingPositionKey Other,ReferenceKey Currency,MarketSnapshot SourceSnapshot,
        LifecycleProjectionState Opening):ILifecycleProjection
    {
        public string Implementation=>"synthetic_atomic_projection_v1";
        public ImmutableArray<AccountingPositionKey> EconomicOwners=>[Owner,Other];
        public ImmutableArray<CurveRequest> Dependencies=>[];
        public ImmutableArray<LifecycleProjectionEvent> Events=>[new(LifecycleProjectionEvent.ScopedId(Owner,Guid.Parse("ee000000-0000-0000-0000-000000000001")),SourceSnapshot.ValuationAt.AddHours(4),[],"synthetic_transfer")];
        public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)=>[..Events.Where(e=>e.At<=horizon)];
        public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
        {
            var members=state.Members.SetItem(Owner,state.Members[Owner] with{Residuals=[]}).SetItem(Other,state.Members[Other] with{
                Cash=[new(occurrence.Id,occurrence.At,occurrence.At,ExactMoney.Parse("100",Currency))]});
            return LifecycleProjectionState.Compose(members) with{CompletedEvents=state.CompletedEvents};
        }
    }
}
