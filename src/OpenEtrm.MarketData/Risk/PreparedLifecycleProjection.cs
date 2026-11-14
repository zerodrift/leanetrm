using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Risk;

public sealed class PreparedLifecycleProjection
{
    public MarketSnapshot SourceSnapshot{get;}
    public DateTimeOffset Horizon{get;}
    public LifecycleProjectionPolicy Policy{get;}
    public ImmutableArray<QuoteFactor> Factors{get;}
    public ImmutableArray<ILifecycleProjection> Components{get;}
    public RiskHistoryMarket Market=>markets[0].Market;
    public ImmutableArray<RiskHistoryMarket> Markets=>[..markets.Select(m=>m.Market)];
    public ImmutableArray<KeyValuePair<ReferenceKey,ProjectionCurrencyCurveKey>> CashCurveIdentities{get;}
    private readonly ImmutableArray<PreparedLifecycleMarket> markets;
    private readonly ImmutableArray<ImmutableDictionary<CurveRequest,DateTimeOffset>> marketUses;
    private readonly ImmutableDictionary<AccountingPositionKey,ImmutableDictionary<CurveRequest,PreparedLifecycleMarket>> dependencyMarkets;
    private readonly ImmutableDictionary<InputKey,DateTimeOffset> inputFinals;
    private readonly ImmutableDictionary<InputKey,(DateTimeOffset At,ExactDecimal Shift)> actualFinals;
    private readonly ImmutableArray<(int Component,LifecycleProjectionEvent Event)> events;
    private readonly ImmutableDictionary<ProjectedFixingKey,ExactUnitPrice> known;
    private readonly ImmutableDictionary<AccountingPositionKey,ExactMoney> openings;
    private readonly ImmutableDictionary<(AccountingOwnerKey Owner,Guid Binding),AccountingPositionKey> economicRoots;
    internal PreparedLifecycleProjection(CurveBuild basis,DateTimeOffset horizon,LifecycleProjectionPolicy policy,
        ImmutableArray<ILifecycleProjection> components,ImmutableDictionary<ProjectedFixingKey,ExactUnitPrice> known,
        ImmutableDictionary<ReferenceKey,ISideDiscounting> cashCurves,CancellationToken ct,ImmutableArray<LifecycleInputFinal> additionalFinals=default,ImmutableArray<LifecycleProjectionMarket> additionalMarkets=default)
        :this(basis.Connected,basis.PricingBindings,basis,horizon,policy,components,known,cashCurves,ct,additionalFinals,additionalMarkets){}
    internal PreparedLifecycleProjection(ConnectedValuation valuation,ImmutableDictionary<CurveRequest,CurvePointKey> bindings,CurveBuild authority,
        DateTimeOffset horizon,LifecycleProjectionPolicy policy,ImmutableArray<ILifecycleProjection> components,
        ImmutableDictionary<ProjectedFixingKey,ExactUnitPrice> known,ImmutableDictionary<ReferenceKey,ISideDiscounting> cashCurves,CancellationToken ct,ImmutableArray<LifecycleInputFinal> additionalFinals=default,ImmutableArray<LifecycleProjectionMarket> additionalMarkets=default)
    {
        SourceSnapshot=valuation.Snapshot;Horizon=horizon;Policy=policy;Components=components;
        var selectedMarkets=ImmutableArray.Create(new LifecycleProjectionMarket(valuation,bindings,authority,cashCurves,
            additionalFinals.IsDefault?[]:additionalFinals)).AddRange(additionalMarkets.IsDefault?[]:additionalMarkets);
        if(selectedMarkets.Any(m=>m is null||m.Valuation is null||m.Bindings is null||m.Authority is null||m.CashCurves is null||m.InputFinals.IsDefault||
            m.Valuation.Snapshot.Knowledge!=SourceSnapshot.Knowledge||m.Valuation.Snapshot.ValuationAt!=SourceSnapshot.ValuationAt||
            m.Valuation.Snapshot.MarketCutoff!=SourceSnapshot.MarketCutoff||!m.Valuation.Snapshot.HasCoherentSelections())||
            selectedMarkets.Select(m=>m.Valuation.Snapshot.Definitions).Distinct().Count()!=selectedMarkets.Length||
            components.Any(c=>c.MarketSources.IsDefaultOrEmpty||c.MarketSources.Any(s=>s is null||!selectedMarkets.Any(m=>m.Valuation.Snapshot.HasSameInputs(s)))||
                c.MarketSources.Select(s=>s.Definitions).Distinct().Count()!=c.MarketSources.Length||!c.MarketSources.Any(s=>s.HasSameInputs(c.SourceSnapshot))))
            throw LifecycleProjection.Error("projection_source","Every selected route must retain its exact complete native graph under the common source certificate and opening clock.");
        if(components.Any(c=>c.Opening is null||!c.Opening.Cash.IsEmpty))
            throw LifecycleProjection.Error("projection_cash","Opening obligations belong to ordinary held portfolios; period cash is created only by events after opening.");
        if(selectedMarkets.Any(m=>m.InputFinals.Any(f=>f is null||f.Market.Curve.Revision!=m.Valuation.Snapshot.Definitions)))
            throw LifecycleProjection.Error("projection_input_expiry","A native market's final declarations must retain that exact graph and source provenance.");
        if(components.Any(c=>c.InputFinals.IsDefault)||components.SelectMany(c=>c.InputFinals).Concat(selectedMarkets.SelectMany(m=>m.InputFinals)).Any(f=>
            f is null||!selectedMarkets.Any(m=>m.Valuation.Snapshot.Definitions==f.Market.Curve.Revision)))
            throw LifecycleProjection.Error("projection_input_expiry","Retain complete input-final declarations under their explicitly prepared native definitions.");
        var componentUses=components.Select(c=>c.MarketUsesFor(Horizon)).ToArray();var uses=componentUses.Where(u=>!u.IsDefault).SelectMany(u=>u).ToArray();
        if(componentUses.Where((u,i)=>u.IsDefault||u.Any(u=>u is null||!components[i].Dependencies.Contains(u.Market))||
                !u.Select(u=>u.Market).ToHashSet().SetEquals(components[i].Dependencies)).Any()||
            uses.Any(u=>!TradingInstant.IsSupported(u.Through)||u.Through<SourceSnapshot.ValuationAt||u.Through>Horizon))
            throw LifecycleProjection.Error("projection_dependency","Every prepared market dependency requires its explicit last calculation-use instant within this horizon.");
        marketUses=[..componentUses.Select(u=>u.GroupBy(u=>u.Market).ToImmutableDictionary(g=>g.Key,g=>g.Max(u=>u.Through)))];
        foreach(var c in components)foreach(var dependency in c.Dependencies)
            RequireDeclaredSource(c,c.DependencySource(dependency),selectedMarkets.Select(m=>m.Valuation.Snapshot));
        markets=[..selectedMarkets.Select(m=>new PreparedLifecycleMarket(m,Horizon,policy,
            components.SelectMany((c,i)=>componentUses[i].Where(u=>c.DependencySource(u.Market).HasSameInputs(m.Valuation.Snapshot)))
                .GroupBy(u=>u.Market).ToImmutableDictionary(g=>g.Key,g=>g.Max(u=>u.Through)),
            [..components.SelectMany(c=>c.InputFinals).Concat(m.InputFinals).Where(f=>f.Market.Curve.Revision==m.Valuation.Snapshot.Definitions)],known,ct))];
        dependencyMarkets=components.ToImmutableDictionary(c=>c.Owner,c=>c.Dependencies.ToImmutableDictionary(d=>d,d=>Select(c,c.DependencySource(d))));
        var caps=markets.SelectMany(m=>m.FinalInstants).GroupBy(p=>p.Key).ToArray();
        var actuals=markets.SelectMany(m=>m.ActualFinals).GroupBy(p=>p.Key).ToArray();
        if(caps.Any(g=>g.Select(p=>p.Value).Distinct().Count()!=1)||actuals.Any(g=>g.Select(p=>p.Value).Distinct().Count()!=1))
            throw LifecycleProjection.Error("projection_input_expiry","One native input cannot have inconsistent final transitions across prepared market graphs.");
        inputFinals=caps.ToImmutableDictionary(g=>g.Key,g=>g.First().Value);actualFinals=actuals.ToImmutableDictionary(g=>g.Key,g=>g.First().Value);
        try{Factors=QuoteFactors.Union(markets.SelectMany(m=>m.Factors));}
        catch(MarketDataException e)when(e.Code=="risk_factor_conflict")
        {throw new MarketDataException("projection_source","One native scenario input cannot have inconsistent observations or units across prepared routes.",e);}
        CashCurveIdentities=[..markets.SelectMany(m=>m.CashCurves.Select(p=>new KeyValuePair<ReferenceKey,ProjectionCurrencyCurveKey>(p.Key,((IProjectionCurrencyCurve)p.Value).CurveIdentity)))];
        if(policy.CashAccumulation==LifecycleProjectionPolicy.Accumulated&&(components.Any(c=>!Select(c,c.SourceSnapshot).CashCurves.ContainsKey(c.Currency))||
            CashCurveIdentities.GroupBy(p=>p.Key).Any(g=>g.Select(p=>p.Value).Distinct().Count()!=1)))
            throw LifecycleProjection.Error("projection_cash_policy","Accumulated cash requires one explicitly selected retained curve identity per currency across every market route.");
        var finalFixings=markets.SelectMany(m=>m.KnownFixings).GroupBy(p=>p.Key).ToArray();
        if(finalFixings.Any(g=>g.Select(p=>p.Value).Distinct().Count()!=1))throw LifecycleProjection.Error("projection_input_expiry","Conflicting native final fixings across prepared routes.");
        this.known=finalFixings.ToImmutableDictionary(g=>g.Key,g=>g.First().Value);
        events=Order(components,SourceSnapshot.ValuationAt,Horizon);
        foreach(var component in components)RequireState(component.Opening,component,SourceSnapshot.ValuationAt);
        RequireUniqueResiduals([..components.Select(c=>c.Opening)]);
        var roots=new Dictionary<(AccountingOwnerKey,Guid),AccountingPositionKey>();
        foreach(var owner in components.SelectMany(c=>c.EconomicOwners))
            if(!roots.TryAdd((owner.Owner,owner.BindingId),owner))
                throw LifecycleProjection.Error("projection_scope","One original native owner/binding requires one economic root and book.");
        RequireEconomicRoots([..components.Select(c=>c.Opening)],roots);economicRoots=roots.ToImmutableDictionary();
        openings=components.SelectMany(c=>Leaves(c.Opening,c).Select(p=>new KeyValuePair<AccountingPositionKey,ExactMoney>(p.Key,
            Value(p.Value,c,p.Key,SourceSnapshot.ValuationAt,m=>m.Basis,ct)))).ToImmutableDictionary();
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
        var paths=markets.ToDictionary(m=>m,m=>m.OpenPath(terminalChanges,inputFinals,actualFinals,ct));
        var facts=new Dictionary<ProjectedFixingKey,ProjectedFixing>();
        var states=Components.Select(c=>c.Opening).ToArray();
        var roots=economicRoots.ToDictionary(p=>p.Key,p=>p.Value);
        var contexts=Components.Select((c,i)=>new LifecycleProjectionContext(SourceSnapshot,Horizon,Policy,(request,at)=>paths[dependencyMarkets[c.Owner][request]].At(at),marketUses[i],known,facts,
            fact=>paths[dependencyMarkets[c.Owner][fact.Key.Market]].Freeze(fact))).ToArray();
        var retainedRoutes=new Dictionary<(AccountingOwnerKey Owner,Guid Binding,Guid Instrument),PreparedLifecycleMarket>();
        var cashRoutes=new Dictionary<(AccountingPositionKey Owner,Guid Cash),PreparedLifecycleMarket>();
        var paymentRoutes=new Dictionary<(AccountingOwnerKey Owner,Guid Allocation,int Revision),PreparedLifecycleMarket>();
        void RetainRoutes()
        {
            for(var i=0;i<Components.Length;i++)foreach(var member in Leaves(states[i],Components[i]))
            {
                var component=Components[i];
                foreach(var portfolio in member.Value.Residuals)foreach(var position in portfolio.Positions)
                {
                    var instrument=portfolio.Instruments.Single(n=>n.Id==position.ValuationId);var owner=new AccountingPositionKey(position.Owner,position.BindingId,position.BookId);
                    Retain(retainedRoutes,(position.Owner,position.BindingId,position.ValuationId),Select(component,component.ValuationSource(owner,instrument)));
                }
                foreach(var cash in member.Value.Cash)Retain(cashRoutes,(member.Key,cash.Id),Select(component,component.CashSource(cash)));
                foreach(var payment in member.Value.KnownPayments)Retain(paymentRoutes,(payment.Position.Owner,payment.AllocationId,payment.Revision),Select(component,component.PaymentSource(payment)));
            }
        }
        RetainRoutes();
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
                if(!member.Value.KnownResidualOwners.SequenceEqual(prior.KnownResidualOwners))
                    throw LifecycleProjection.Error("projection_source","A transition cannot replace its source-known native residual ownership declarations.");
                if(member.Value.Cash.Any(c=>!cash.ContainsKey(c.Id)&&c.EffectiveAt!=occurrence.At))
                    throw LifecycleProjection.Error("projection_cash","New cash must retain the event that created its economic obligation.");
            }
            states[index]=next with{CompletedEvents=states[index].CompletedEvents.Add(occurrence.Id)};
            RequireUniqueResiduals(states);
            RequireEconomicRoots(states,roots);RetainRoutes();
        }
        var values=ImmutableArray.CreateBuilder<LifecycleProjectionValue>(openings.Count);
        for(var i=0;i<Components.Length;i++)
        {
            ct.ThrowIfCancellationRequested();var component=Components[i];var state=states[i];
            foreach(var member in Leaves(state,component))
            {
                var residual=Value(member.Value,component,member.Key,Horizon,m=>paths[m].At(Horizon),ct,
                    (position,instrument)=>retainedRoutes[(position.Owner,position.BindingId,instrument.Id)],cash=>cashRoutes[(member.Key,cash.Id)]);
                var period=Zero(component.Currency);
                foreach(var cash in member.Value.Cash.Where(c=>c.DueAt<=Horizon&&Policy.CashDisposition==LifecycleProjectionPolicy.PayAtDue))
                    period=period.Add(Accumulate(cash,component.Currency,cashRoutes[(member.Key,cash.Id)]));
                foreach(var payment in member.Value.KnownPayments.Where(p=>p.EffectiveAt<=Horizon))
                    period=period.Add(Accumulate(new(payment.AllocationId,payment.EffectiveAt,payment.EffectiveAt,payment.Amount),component.Currency,paymentRoutes[(payment.Position.Owner,payment.AllocationId,payment.Revision)]));
                values.Add(new(member.Key,component.Currency,openings[member.Key],residual,period,residual.Add(period).Subtract(openings[member.Key])));
            }
            states[i]=state with{ProjectedFacts=facts.ToImmutableDictionary()};
        }
        return new(id,Horizon,values.ToImmutable(),[..facts.Values.OrderBy(f=>f.Key.At).ThenBy(f=>f.Key.Market.Contract.Id)],states.ToImmutableArray());
    }
    private ExactMoney Value(LifecycleProjectionState state,ILifecycleProjection component,AccountingPositionKey owner,DateTimeOffset at,Func<PreparedLifecycleMarket,PreparedValuationEnvironment> market,CancellationToken ct,
        Func<RiskPosition,Instrument,PreparedLifecycleMarket>? residualSource=null,Func<ProjectedCashContribution,PreparedLifecycleMarket>? cashSource=null)
    {
        RequireFlatState(state,owner,component.Currency,at);var total=Zero(component.Currency);
        foreach(var portfolio in state.Residuals)
        {
            if(portfolio.Setup.Measure!=ValuationMeasure.PresentValue)throw LifecycleProjection.Error("projection_measure","Economic residuals require ordinary present value.");
            var routed=portfolio.Positions.GroupBy(position=>
            {
                var instrument=portfolio.Instruments.Single(n=>n.Id==position.ValuationId);
                return residualSource is null?Select(component,component.ValuationSource(new(position.Owner,position.BindingId,position.BookId),instrument)):residualSource(position,instrument);
            });
            foreach(var group in routed)
            {
                var selected=group.Key;var positions=group.ToImmutableArray();var instruments=portfolio.Instruments.Where(i=>positions.Any(p=>p.ValuationId==i.Id)).ToImmutableArray();
                var held=positions.Length==portfolio.Positions.Length?portfolio:new ValuationPortfolio(instruments,positions,portfolio.Setup.ForPositions(positions));
                var declared=held.Declare(selected.SourceSnapshot,at);
                if(declared.Any(d=>d.Currency!=component.Currency||d.Dependencies.Any(r=>!component.Dependencies.Contains(r)||!dependencyMarkets[component.Owner][r].SourceSnapshot.HasSameInputs(selected.SourceSnapshot))))
                    throw LifecycleProjection.Error("projection_source","An ordinary residual requires its exact declared native market route and complete prepared dependency closure.");
                var environment=new ProjectionMarket(market(selected),at);environment.RequireInputs(declared);
                foreach(var value in held.PV(environment,declared,ct:ct).Totals)total=total.Add(value.Value);
            }
        }
        foreach(var cash in state.Cash.Where(c=>Policy.CashDisposition==LifecycleProjectionPolicy.Outstanding||c.DueAt>at))
        {
            if(cash.EffectiveAt>at)throw LifecycleProjection.Error("projection_cash","A residual contribution cannot precede its occurrence.");
            var selected=cashSource is null?Select(component,component.CashSource(cash)):cashSource(cash);var environment=new ProjectionMarket(market(selected),at);
            selected.CashCurves.TryGetValue(component.Currency,out var provider);
            if(cash.DueAt>at&&cash.Amount.Numerator!="0"&&provider is null)throw LifecycleProjection.Error("projection_source","An unpaid future contribution requires its explicitly selected native source and currency discount curve.");
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
            var declared=portfolio.Declare(selected.SourceSnapshot,at);
            environment.RequireInputs(declared);
            total=total.Add(portfolio.PV(environment,declared,ct:ct).Totals.Single().Value);
        }
        return total;
    }
    private ExactMoney Accumulate(ProjectedCashContribution cash,ReferenceKey currency,PreparedLifecycleMarket selected)
    {
        if(Policy.CashAccumulation==LifecycleProjectionPolicy.Nominal||cash.DueAt==Horizon)return cash.Amount;
        if(!selected.CashCurves.TryGetValue(currency,out var provider))throw LifecycleProjection.Error("projection_cash_policy","Select this currency's cash accumulation curve.");
        var start=new SidePaymentKey(cash.Id,0,cash.DueAt,currency);var end=new SidePaymentKey(cash.Id,1,Horizon,currency);
        var factors=provider.Resolve([start,end],selected.SourceSnapshot);
        if(factors.Length!=2||factors[0].Payment!=start||factors[1].Payment!=end||factors.Any(f=>f.DiscountFactor.CompareTo(default)<=0||!f.Snapshot.HasSameInputs(selected.SourceSnapshot)))
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
            !state.KnownPayments.SequenceEqual(expected.KnownPayments)||!state.KnownCashSources.SequenceEqual(expected.KnownCashSources)||
            !state.KnownResidualOwners.SequenceEqual(expected.KnownResidualOwners))
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
            state.KnownCashSources.Select(s=>(s.Owner,s.BindingId)).Distinct().Count()!=state.KnownCashSources.Length||
            state.KnownResidualOwners.IsDefault||state.KnownResidualOwners.Any(p=>!p.IsValid||p.BookId!=owner.BookId)||
            state.KnownResidualOwners.Select(p=>(p.Owner,p.BindingId)).Distinct().Count()!=state.KnownResidualOwners.Length)
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
            foreach(var source in member.Value.KnownResidualOwners)Retain(source.Owner,source.BindingId,member.Key);
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
    private PreparedLifecycleMarket Select(ILifecycleProjection component,MarketSnapshot source)
    {
        RequireDeclaredSource(component,source,markets.Select(m=>m.SourceSnapshot));
        return markets.Single(m=>m.SourceSnapshot.HasSameInputs(source));
    }
    private static void RequireDeclaredSource(ILifecycleProjection component,MarketSnapshot source,IEnumerable<MarketSnapshot> selected)
    {
        if(source is null||!component.MarketSources.Any(s=>s.HasSameInputs(source))||!selected.Any(s=>s.HasSameInputs(source)))
            throw LifecycleProjection.Error("projection_source","Select this exact native source explicitly before preparing its lifecycle branch.");
    }
    private static void Retain<TKey>(Dictionary<TKey,PreparedLifecycleMarket> retained,TKey key,PreparedLifecycleMarket source) where TKey:notnull
    {
        if(retained.TryGetValue(key,out var prior)&&!ReferenceEquals(prior,source))
            throw LifecycleProjection.Error("projection_source","An existing owned residual, cash contribution or actual payment cannot change its retained native market route.");
        retained[key]=source;
    }
    private static ExactMoney Zero(ReferenceKey currency)=>ExactMoney.Parse("0",currency);

    private sealed class ProjectionMarket(PreparedValuationEnvironment source,DateTimeOffset at):PreparedValuationEnvironment
    {
        public override MarketSnapshot Snapshot=>source.Snapshot;
        internal override void RequireDiscountSource(MarketSnapshot selected)=>source.RequireDiscountSource(selected);
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
