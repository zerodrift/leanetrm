using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Risk;

public sealed class PreparedLifecycleProjection
{
    public MarketSnapshot SourceSnapshot=>valuation.Snapshot;
    public DateTimeOffset Horizon{get;}
    public LifecycleProjectionPolicy Policy{get;}
    public ImmutableArray<QuoteFactor> Factors{get;}
    public ImmutableArray<ILifecycleProjection> Components{get;}
    public RiskHistoryMarket Market=>new(SourceSnapshot.Definitions,SourceSnapshot.Id,valuation.Scenario.Plan.ManifestId);
    public ImmutableDictionary<ReferenceKey,ProjectionCurrencyCurveKey> CashCurveIdentities{get;}
    private readonly PreparedValuationEnvironment basis;
    private readonly ConnectedValuation valuation;
    private readonly ImmutableDictionary<CurveRequest,CurvePointKey> bindings;
    private readonly CurveBuild authority;
    private readonly ImmutableDictionary<InputKey,CurvePointKey> primitives;
    private readonly ImmutableArray<(int Component,LifecycleProjectionEvent Event)> events;
    private readonly ImmutableDictionary<ProjectedFixingKey,ExactUnitPrice> known;
    private readonly ImmutableDictionary<ReferenceKey,ISideDiscounting> cashCurves;
    private readonly ImmutableDictionary<AccountingPositionKey,ExactMoney> openings;
    private readonly ImmutableDictionary<(AccountingOwnerKey Owner,Guid Binding),AccountingPositionKey> economicRoots;
    internal PreparedLifecycleProjection(CurveBuild basis,DateTimeOffset horizon,LifecycleProjectionPolicy policy,
        ImmutableArray<ILifecycleProjection> components,ImmutableDictionary<ProjectedFixingKey,ExactUnitPrice> known,
        ImmutableDictionary<ReferenceKey,ISideDiscounting> cashCurves,CancellationToken ct)
        :this(basis.Connected,basis.PricingBindings,basis,horizon,policy,components,known,cashCurves,ct){}
    internal PreparedLifecycleProjection(ConnectedValuation valuation,ImmutableDictionary<CurveRequest,CurvePointKey> bindings,CurveBuild authority,
        DateTimeOffset horizon,LifecycleProjectionPolicy policy,ImmutableArray<ILifecycleProjection> components,
        ImmutableDictionary<ProjectedFixingKey,ExactUnitPrice> known,ImmutableDictionary<ReferenceKey,ISideDiscounting> cashCurves,CancellationToken ct)
    {
        this.valuation=valuation;this.bindings=bindings;this.authority=authority;Horizon=horizon;Policy=policy;Components=components;this.known=known;this.cashCurves=cashCurves;
        if(cashCurves.Any(p=>p.Value is not IProjectionCurrencyCurve curve||curve.Currency!=p.Key||
            !curve.SourceSnapshot.HasSameInputs(SourceSnapshot)||curve.TimingPolicy!=RateDiscountTiming.Version))
            throw LifecycleProjection.Error("projection_cash_policy","Select captured native currency curves with explicit exact-instant timing; scalar factors cannot define a path cash policy.");
        CashCurveIdentities=cashCurves.ToImmutableDictionary(p=>p.Key,p=>((IProjectionCurrencyCurve)p.Value).CurveIdentity);
        if(valuation.Scenario.CalculationAt!=SourceSnapshot.ValuationAt||!authority.Snapshot.HasSameInputs(SourceSnapshot))
            throw LifecycleProjection.Error("projection_source","Projection requires the original certified source clock and compatible retained market authority.");
        basis=valuation.Market(bindings,authority,ct,authority.BindConnected(valuation,bindings,ct));
        primitives=valuation.Scenario.Plan.Points.Values.Where(p=>p.Expression is ConnectedSource {Input.Use:InputUse.Variable})
            .GroupBy(p=>((ConnectedSource)p.Expression).Input).ToImmutableDictionary(g=>g.Key,g=>g.First().Key);
        if(components.Any(c=>c.Opening is null||!c.Opening.Cash.IsEmpty))
            throw LifecycleProjection.Error("projection_cash","Opening obligations belong to ordinary held portfolios; period cash is created only by events after opening.");
        if(policy.CashAccumulation==LifecycleProjectionPolicy.Accumulated&&components.Any(c=>!cashCurves.TryGetValue(c.Currency,out var curve)||curve is null))
            throw LifecycleProjection.Error("projection_cash_policy","Select exact currency curves for accumulated cash, or an explicit empty selection for nominal cash.");
        var declarations=components.SelectMany(c=>c.Dependencies).ToImmutableHashSet();
        Factors=basis.Factors([..declarations],ct);
        events=Order(components,SourceSnapshot.ValuationAt,Horizon);
        foreach(var component in components)RequireState(component.Opening,component,SourceSnapshot.ValuationAt);
        RequireUniqueResiduals([..components.Select(c=>c.Opening)]);
        var roots=new Dictionary<(AccountingOwnerKey,Guid),AccountingPositionKey>();
        foreach(var owner in components.SelectMany(c=>c.EconomicOwners))
            if(!roots.TryAdd((owner.Owner,owner.BindingId),owner))
                throw LifecycleProjection.Error("projection_scope","One original native owner/binding requires one economic root and book.");
        RequireEconomicRoots([..components.Select(c=>c.Opening)],roots);economicRoots=roots.ToImmutableDictionary();
        openings=components.SelectMany(c=>Leaves(c.Opening,c).Select(p=>new KeyValuePair<AccountingPositionKey,ExactMoney>(p.Key,
            Value(p.Value,c,p.Key,SourceSnapshot.ValuationAt,basis,ct)))).ToImmutableDictionary();
        foreach(var component in components)foreach(var member in Leaves(component.Opening,component))
        {
            var states=member.Value.Residuals.SelectMany(p=>p.Positions.Select(position=>p.Setup.BindingStates.GetValueOrDefault(new(position.Owner,position.BindingId))))
                .Where(s=>s is not null).ToArray();
            if(states.OfType<RecordedCashValuation>().Any(s=>s.SourceContextId!=SourceSnapshot.Knowledge!.ContextId||s.KnownThrough!=SourceSnapshot.Knowledge.KnownAt))
                throw LifecycleProjection.Error("projection_cash","Native opening cash must retain this exact certified source context.");
            var captured=states
                .SelectMany(s=>s switch{RecordedCashValuation cash=>cash.KnownFlows(SourceSnapshot),
                    ProjectedSwapValuation swap=>swap.KnownPayments(SourceSnapshot),_=>[]})
                .Concat(member.Value.KnownCashSources.SelectMany(s=>s.KnownFlows(SourceSnapshot)))
                .Where(p=>p.EffectiveAt>SourceSnapshot.ValuationAt).ToHashSet();
            if(!captured.SetEquals(member.Value.KnownPayments))
                throw LifecycleProjection.Error("projection_cash","Retain every source-known future payment exactly once with its native owner-specific cash evidence.");
        }
    }
    public LifecycleProjectionResult Evaluate(string id,ImmutableArray<QuoteShift> terminalChanges,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(id)||id.Length>128||terminalChanges.IsDefault||terminalChanges.Any(s=>s is null||
            s.ConventionId!=QuoteShift.Convention||!Factors.Any(f=>f.Input==s.Input&&f.Quote==s.Quote&&f.Unit==s.Unit))||
            terminalChanges.Select(s=>s.Input).Distinct().Count()!=terminalChanges.Length)
            throw LifecycleProjection.Error("projection_scenario","Supply a distinct complete native primitive quote scenario under the prepared factor closure.");
        var markets=new Dictionary<DateTimeOffset,PreparedValuationEnvironment>();
        var finalInstants=new Dictionary<InputKey,DateTimeOffset>();
        PreparedValuationEnvironment At(DateTimeOffset at)
        {
            if(at<SourceSnapshot.ValuationAt||at>Horizon)throw LifecycleProjection.Error("projection_clock","The path read is outside its common horizon.");
            if(markets.TryGetValue(at,out var cached))return cached;
            var shifts=terminalChanges.Select(s=>s with{Size=Policy.PricePath==LifecycleProjectionPolicy.Hold?s.Size:
                Scale(s.Size,(finalInstants.TryGetValue(s.Input,out var final)&&final<at?final:at).Ticks-SourceSnapshot.ValuationAt.Ticks,Horizon.Ticks-SourceSnapshot.ValuationAt.Ticks)})
                .Where(s=>s.Size!=default).ToImmutableArray();
            var changes=shifts.Select(s=>
            {
                if(!primitives.TryGetValue(s.Input,out var point))throw LifecycleProjection.Error("projection_scenario","The declared factor has no admitted native primitive node.");
                return new CurveShift(CurveShockCoordinate.MarketQuote,new PointCurveTarget(point.Curve,[point.PointId]),s.Unit,s.Size,CurveShift.ParentAdditive);
            }).ToImmutableArray();
            var child=changes.IsEmpty?valuation:valuation.WithScenario(valuation.Scenario.WithShifts(changes,ct));
            var market=changes.IsEmpty?basis:child.Market(bindings,authority,ct,authority.BindConnected(child,bindings,ct));
            markets.Add(at,market);return market;
        }
        var facts=new Dictionary<ProjectedFixingKey,ProjectedFixing>();
        void FreezeFinal(ProjectedFixing fact)
        {
            if(!bindings.TryGetValue(fact.Key.Market,out var point)||valuation.Scenario.Plan.Points[point].Expression is not ConnectedSource {Input.Use:InputUse.Variable} primitive)
                throw LifecycleProjection.Error("projection_fixing","A projected final contract settlement requires its explicitly selected native primitive market.");
            if(finalInstants.TryGetValue(primitive.Input,out var existing)&&existing!=fact.Key.At)
                throw LifecycleProjection.Error("projection_fixing","One native contract cannot have conflicting final-settlement instants in the same economic path.");
            finalInstants[primitive.Input]=fact.Key.At;
            foreach(var time in markets.Keys.Where(time=>time>fact.Key.At).ToArray())markets.Remove(time);
        }
        var states=Components.Select(c=>c.Opening).ToArray();
        var roots=economicRoots.ToDictionary(p=>p.Key,p=>p.Value);
        var contexts=Components.Select(c=>new LifecycleProjectionContext(SourceSnapshot,Horizon,Policy,At,c.Dependencies.ToImmutableHashSet(),known,facts,FreezeFinal)).ToArray();
        foreach(var (index,occurrence) in events)
        {
            ct.ThrowIfCancellationRequested();var context=contexts[index];context.CalculationAt=occurrence.At;
            var next=Components[index].Apply(occurrence,states[index],context)??throw LifecycleProjection.Error("projection_state","A transition returned no state.");
            RequireState(next,Components[index],occurrence.At);
            var previous=Leaves(states[index],Components[index]).ToDictionary(p=>p.Key,p=>p.Value);
            foreach(var member in Leaves(next,Components[index]))
            {
                var prior=previous[member.Key];var cash=prior.Cash.ToDictionary(c=>c.Id);
                if(cash.Any(p=>!member.Value.Cash.Contains(p.Value)))throw LifecycleProjection.Error("projection_cash","A transition removed, reassigned or rewrote an earlier owned cash contribution.");
                if(!member.Value.KnownPayments.ToHashSet().SetEquals(prior.KnownPayments))
                    throw LifecycleProjection.Error("projection_cash","A projected transition cannot add, remove, reassign or correct source-known actual payments.");
                if(!member.Value.KnownCashSources.SequenceEqual(prior.KnownCashSources))
                    throw LifecycleProjection.Error("projection_cash","A projected transition cannot replace its certified native known-cash source evidence.");
                if(member.Value.Cash.Any(c=>!cash.ContainsKey(c.Id)&&c.EffectiveAt!=occurrence.At))
                    throw LifecycleProjection.Error("projection_cash","New cash must retain the event that created its economic obligation.");
            }
            states[index]=next with{CompletedEvents=states[index].CompletedEvents.Add(occurrence.Id)};
            RequireUniqueResiduals(states);
            RequireEconomicRoots(states,roots);
        }
        var values=ImmutableArray.CreateBuilder<LifecycleProjectionValue>(openings.Count);
        for(var i=0;i<Components.Length;i++)
        {
            ct.ThrowIfCancellationRequested();var component=Components[i];var state=states[i];
            foreach(var member in Leaves(state,component))
            {
                var residual=Value(member.Value,component,member.Key,Horizon,At(Horizon),ct);
                var period=Zero(component.Currency);
                foreach(var cash in member.Value.Cash.Where(c=>c.DueAt<=Horizon&&Policy.CashDisposition==LifecycleProjectionPolicy.PayAtDue))
                    period=period.Add(Accumulate(cash,component.Currency));
                foreach(var payment in member.Value.KnownPayments.Where(p=>p.EffectiveAt<=Horizon))
                    period=period.Add(Accumulate(new(payment.AllocationId,payment.EffectiveAt,payment.EffectiveAt,payment.Amount),component.Currency));
                values.Add(new(member.Key,component.Currency,openings[member.Key],residual,period,residual.Add(period).Subtract(openings[member.Key])));
            }
            states[i]=state with{ProjectedFacts=facts.ToImmutableDictionary()};
        }
        return new(id,Horizon,values.ToImmutable(),[..facts.Values.OrderBy(f=>f.Key.At).ThenBy(f=>f.Key.Market.Contract.Id)],states.ToImmutableArray());
    }
    private ExactMoney Value(LifecycleProjectionState state,ILifecycleProjection component,AccountingPositionKey owner,DateTimeOffset at,PreparedValuationEnvironment market,CancellationToken ct)
    {
        RequireFlatState(state,owner,component.Currency,at);var total=Zero(component.Currency);
        var environment=new ProjectionMarket(market,at);
        foreach(var portfolio in state.Residuals)
        {
            if(portfolio.Setup.Measure!=ValuationMeasure.PresentValue)throw LifecycleProjection.Error("projection_measure","Economic residuals require ordinary present value.");
            var declared=portfolio.Declare(SourceSnapshot,at);
            if(declared.Any(d=>d.Currency!=component.Currency||d.Dependencies.Any(r=>!component.Dependencies.Contains(r))))
                throw LifecycleProjection.Error("projection_dependency","Every residual branch must retain its prepared currency and complete declared price closure.");
            environment.RequireInputs(declared);
            foreach(var value in portfolio.PV(environment,declared,ct:ct).Totals)total=total.Add(value.Value);
        }
        foreach(var cash in state.Cash.Where(c=>Policy.CashDisposition==LifecycleProjectionPolicy.Outstanding||c.DueAt>at))
        {
            if(cash.EffectiveAt>at)throw LifecycleProjection.Error("projection_cash","A residual contribution cannot precede its occurrence.");
            cashCurves.TryGetValue(component.Currency,out var provider);
            if(cash.DueAt>at&&cash.Amount.Numerator!="0"&&provider is null)throw LifecycleProjection.Error("projection_cash_discount","An unpaid future contribution requires its explicitly selected currency discount curve.");
            var instrument=new TimedCashPayment(component.Currency,cash.DueAt);
            var quantity=new Quantity(ExactDecimal.Parse("1"),component.Currency);
            var binding=LifecycleProjectionEvent.ScopedId(owner,cash.Id);
            var position=new AccountingPositionKey(owner.Owner,binding,owner.BookId);
            var projected=new ProjectedCashValuation(position,quantity,instrument.Id,cash.Amount,
                SourceSnapshot.Knowledge!.ContextId,SourceSnapshot.Knowledge.KnownAt);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,provider){
                BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(owner.Owner,binding),projected)};
            var portfolio=new ValuationPortfolio([instrument],[new(owner.Owner,binding,owner.BookId,instrument.Id,quantity,
                new UnitMoney(new Money(default,component.Currency),component.Currency),new(default,component.Currency))],setup);
            var declared=portfolio.Declare(SourceSnapshot,at);
            environment.RequireInputs(declared);
            total=total.Add(portfolio.PV(environment,declared,ct:ct).Totals.Single().Value);
        }
        return total;
    }
    private ExactMoney Accumulate(ProjectedCashContribution cash,ReferenceKey currency)
    {
        if(Policy.CashAccumulation==LifecycleProjectionPolicy.Nominal||cash.DueAt==Horizon)return cash.Amount;
        if(!cashCurves.TryGetValue(currency,out var provider))throw LifecycleProjection.Error("projection_cash_policy","Select this currency's cash accumulation curve.");
        var start=new SidePaymentKey(cash.Id,0,cash.DueAt,currency);var end=new SidePaymentKey(cash.Id,1,Horizon,currency);
        var factors=provider.Resolve([start,end],SourceSnapshot);
        if(factors.Length!=2||factors[0].Payment!=start||factors[1].Payment!=end||factors.Any(f=>f.DiscountFactor.CompareTo(default)<=0||!f.Snapshot.HasSameInputs(SourceSnapshot)))
            throw LifecycleProjection.Error("projection_cash_policy","Cash accumulation requires both exact retained currency curve factors.");
        return cash.Amount.Scale(factors[0].DiscountFactor).DivideBy(factors[1].DiscountFactor);
    }
    private void RequireState(LifecycleProjectionState state,ILifecycleProjection component,DateTimeOffset at)
    {
        if(state is null||state.Members is null)throw LifecycleProjection.Error("projection_composition","Retain the exact immutable member-state map.");
        RequireFlatState(state,component.Owner,component.Currency,at);
        if(state.Members.IsEmpty)
        {
            if(component.EconomicOwners.Length!=1||component.EconomicOwners[0]!=component.Owner)
                throw LifecycleProjection.Error("projection_composition","Multiple economic owners require complete explicit member states.");
            return;
        }
        if(!state.Members.Keys.ToHashSet().SetEquals(component.EconomicOwners)||state.Members.Any(p=>p.Value is null||p.Value.Members is null||!p.Value.Members.IsEmpty))
            throw LifecycleProjection.Error("projection_composition","Preserve every declared economic owner in one flat immutable member map.");
        foreach(var member in state.Members)RequireFlatState(member.Value,member.Key,component.Currency,at);
        var expected=LifecycleProjectionState.Compose(state.Members);
        if(!state.Residuals.SequenceEqual(expected.Residuals)||!state.Cash.SequenceEqual(expected.Cash)||
            !state.KnownPayments.SequenceEqual(expected.KnownPayments)||!state.KnownCashSources.SequenceEqual(expected.KnownCashSources))
            throw LifecycleProjection.Error("projection_composition","The sole valued residual, cash and actual-source aggregates must exactly equal the declared member states.");
    }
    private static KeyValuePair<AccountingPositionKey,LifecycleProjectionState>[] Leaves(LifecycleProjectionState state,ILifecycleProjection component)=>
        state.Members.IsEmpty?[new(component.Owner,state)]:LifecycleProjectionState.Order(state.Members);
    private void RequireFlatState(LifecycleProjectionState state,AccountingPositionKey owner,ReferenceKey currency,DateTimeOffset at)
    {
        if(state is null||state.Residuals.IsDefault||state.Residuals.Any(p=>p is null)||state.Cash.IsDefault||state.Cash.Any(c=>c is null||c.Id==Guid.Empty||
            c.Amount is null||c.Amount.Currency!=currency||!TradingInstant.IsSupported(c.EffectiveAt)||!TradingInstant.IsSupported(c.DueAt)||
            c.DueAt<c.EffectiveAt||c.EffectiveAt>at)||state.Cash.Select(c=>c.Id).Distinct().Count()!=state.Cash.Length||state.KnownPayments.IsDefault||
            state.Residuals.SelectMany(p=>p.Positions).Any(p=>p.BookId!=owner.BookId)||
            state.KnownPayments.Any(p=>p is null||p.Kind!=AccountingFlowKind.EconomicCash||p.Position.BookId!=owner.BookId||p.Amount is null||p.Amount.Currency!=currency||
                p.EffectiveAt<=SourceSnapshot.ValuationAt||!TradingInstant.IsSupported(p.EffectiveAt)||!TradingInstant.IsSupported(p.KnownAt)||p.KnownAt>SourceSnapshot.Knowledge!.KnownAt)||
            state.KnownPayments.Select(p=>(p.Position.Owner,p.AllocationId,p.Revision)).Distinct().Count()!=state.KnownPayments.Length||
            state.KnownCashSources.IsDefault||state.KnownCashSources.Any(s=>s is null||s.BookId!=owner.BookId||
                s.Quantity.Specification!=currency||s.SourceContextId!=SourceSnapshot.Knowledge!.ContextId||s.KnownThrough!=SourceSnapshot.Knowledge.KnownAt)||
            state.KnownCashSources.Select(s=>(s.Owner,s.BindingId)).Distinct().Count()!=state.KnownCashSources.Length)
            throw LifecycleProjection.Error("projection_state","Supply complete residual portfolios and distinct immutable owned cash contributions.");
    }
    private static void RequireUniqueResiduals(IEnumerable<LifecycleProjectionState> states)
    {
        var retained=states.ToArray();var positions=retained.SelectMany(s=>s.Residuals).SelectMany(p=>p.Positions).ToArray();
        if(positions.Select(p=>(p.Owner,p.BindingId)).Distinct().Count()!=positions.Length)
            throw LifecycleProjection.Error("projection_scope","One residual owner/binding cannot appear in multiple economic components.");
        var payments=retained.SelectMany(s=>s.KnownPayments).ToArray();
        if(payments.Select(p=>(p.Position.Owner,p.AllocationId,p.Revision)).Distinct().Count()!=payments.Length)
            throw LifecycleProjection.Error("projection_scope","One native actual payment cannot be counted by multiple economic components.");
    }
    private void RequireEconomicRoots(LifecycleProjectionState[] states,Dictionary<(AccountingOwnerKey,Guid),AccountingPositionKey> roots)
    {
        void Retain(AccountingOwnerKey native,Guid binding,AccountingPositionKey economic)
        {
            if(roots.TryGetValue((native,binding),out var existing)&&existing!=economic)
                throw LifecycleProjection.Error("projection_scope","An existing native owner/binding cannot be reassigned to another economic member.");
            roots[(native,binding)]=economic;
        }
        for(var i=0;i<states.Length;i++)foreach(var member in Leaves(states[i],Components[i]))
        {
            foreach(var position in member.Value.Residuals.SelectMany(p=>p.Positions))Retain(position.Owner,position.BindingId,member.Key);
            foreach(var source in member.Value.KnownCashSources)Retain(source.Owner,source.BindingId,member.Key);
        }
    }
    private static ImmutableArray<(int,LifecycleProjectionEvent)> Order(ImmutableArray<ILifecycleProjection> components,DateTimeOffset opening,DateTimeOffset horizon)
    {
        var schedules=components.Select(c=>c.EventsFor(horizon)).ToArray();
        if(schedules.Any(s=>s.IsDefault))throw LifecycleProjection.Error("projection_events","Every handler must admit its exact horizon event schedule before sampling.");
        var all=schedules.SelectMany((schedule,i)=>schedule.Select(e=>(Component:i,Event:e))).ToArray();
        if(all.Any(e=>e.Event is null||e.Event.Id==Guid.Empty||!TradingInstant.IsSupported(e.Event.At)||e.Event.Requires.IsDefault||
            e.Event.Requires.Distinct().Count()!=e.Event.Requires.Length||string.IsNullOrWhiteSpace(e.Event.Kind))||
            all.Select(e=>e.Event.Id).Distinct().Count()!=all.Length)
            throw LifecycleProjection.Error("projection_events","Declare distinct exact events and their causal prerequisites.");
        var byId=all.ToDictionary(e=>e.Event.Id);
        var completed=components.SelectMany(c=>c.Opening.CompletedEvents).ToHashSet();
        if(all.Any(e=>e.Event.Requires.Any(id=>byId.TryGetValue(id,out var prior)?prior.Event.At>e.Event.At:!completed.Contains(id))))
            throw LifecycleProjection.Error("projection_events","Every prerequisite requires an exact declared event no later than its dependent.");
        var selected=all.Where(e=>e.Event.At>opening&&e.Event.At<=horizon).ToDictionary(e=>e.Event.Id);
        var done=completed;
        if(done.Contains(Guid.Empty)||selected.Keys.Any(done.Contains))
            throw LifecycleProjection.Error("projection_events","A completed opening event cannot be an empty or future scheduled transition.");
        var result=ImmutableArray.CreateBuilder<(int,LifecycleProjectionEvent)>();
        while(selected.Count>0)
        {
            var at=selected.Values.Min(e=>e.Event.At);
            var ready=selected.Values.Where(e=>e.Event.At==at&&e.Event.Requires.All(done.Contains)).OrderBy(e=>e.Event.Id).ToArray();
            if(ready.Length==0)throw LifecycleProjection.Error("projection_events","Same-instant event prerequisites contain a cycle or unavailable predecessor.");
            foreach(var e in ready){result.Add((e.Component,e.Event));selected.Remove(e.Event.Id);done.Add(e.Event.Id);}
        }
        return result.ToImmutable();
    }
    private static ExactDecimal Scale(ExactDecimal value,long elapsed,long horizon)=>ExactDecimal.FromScaled(
        IntegerRounding.RoundQuotient(value.ScaledValue*elapsed,new BigInteger(horizon),MidpointRounding.ToEven));
    private static ExactMoney Zero(ReferenceKey currency)=>ExactMoney.Parse("0",currency);

    private sealed class ProjectionMarket(PreparedValuationEnvironment source,DateTimeOffset at):PreparedValuationEnvironment
    {
        public override MarketSnapshot Snapshot=>source.Snapshot;
        public override string ScenarioId=>$"{source.ScenarioId}/lifecycle/{at.Ticks}";
        public override DateTimeOffset CalculationAt=>at;
        internal override DateTimeOffset LifecycleAt=>at;
        internal override bool IsLifecycleProjection=>true;
        public override string? ClockPolicy=>ClockChange.FrozenInputs;
        public override string NumericalPolicy=>source.NumericalPolicy;
        internal override object Identity=>this;
        internal override CurveBuild? Authority=>source.Authority;
        internal override CurveBuild? NativeIntervalScenario=>source.NativeIntervalScenario;
        internal override CurveBuild? NativeProgramBasis=>source.NativeProgramBasis;
        internal override ExactDecimal VolatilityOffset(Guid input)=>source.VolatilityOffset(input);
        internal override ImmutableHashSet<Guid> ShiftedVolatilityInputs=>source.ShiftedVolatilityInputs;
        internal override ExactUnitPrice Price(CurveRequest request,CancellationToken cancellation)=>source.Price(request,cancellation);
        internal override ImmutableHashSet<ObservationKey> Lineage(CurveRequest request,CancellationToken cancellation)=>source.Lineage(request,cancellation);
        internal override ImmutableArray<QuoteFactor> Factors(ImmutableArray<CurveRequest> requests,CancellationToken cancellation)=>source.Factors(requests,cancellation);
        internal override bool Compatible(PreparedValuationEnvironment prior)=>ReferenceEquals(this,prior);
        internal override bool Unchanged(PreparedValuationEnvironment prior,ImmutableArray<CurveRequest> requests,CancellationToken cancellation)=>ReferenceEquals(this,prior);
        internal override SideValuationContext Context(ISideDiscounting? discounting,ImmutableArray<CurveRequest> dependencies,CancellationToken cancellation,PricingInputs? inputs=null)
            =>source.Context(discounting,dependencies,cancellation,inputs);
    }
}
