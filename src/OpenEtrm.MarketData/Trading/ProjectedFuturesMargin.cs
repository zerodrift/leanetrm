using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// A path-local economic basis, never an accepted native determination. Native
// planners and writers accept FuturesMarginState/FuturesSettlementFact only.
public sealed record ProjectedFuturesMarginState(Guid TradeId,Guid BindingId,Guid? BookId,Quantity Quantity,ReferenceKey Series,
    ExactUnitPrice Basis,DateTimeOffset BasisAt,bool Final,Guid SourceContextId,DateTimeOffset KnownThrough):IBindingValuationState;

public sealed partial record FuturesContract
{
    private void RequireProjected(ProjectedFuturesMarginState state)
    {
        if(state.TradeId==Guid.Empty||state.BindingId==Guid.Empty||state.BookId is null||state.BookId==Guid.Empty||state.SourceContextId==Guid.Empty||
            state.Series!=SeriesKey||state.Quantity is null||state.Quantity.Specification!=UnitQuantity.Specification||state.Quantity.Amount==default||
            !state.Quantity.Amount.IsMultipleOf(LotSize.Amount)||state.Basis is null||state.Basis.Unit.Currency!=Currency||
            state.Basis.Unit.Quantity!=UnitQuantity.Specification||state.Basis.Unit.Multiplier!=Series.Multiplier||
            !TradingInstant.IsSupported(state.BasisAt)||!TradingInstant.IsSupported(state.KnownThrough)||
            state.Final&&(Series.SettlementMethod!="cash"||state.BasisAt<Series.LastTradingAt))
            throw new MarketDataException("projection_futures_state","Supply the exact path-local held future, quotation, basis and source pins.");
    }
    private void RequireProjectedContext(ProjectedFuturesMarginState state,UnitValuationContext context)
    {
        RequireProjected(state);
        if(!context.IsLifecycleProjection||context.Snapshot.Knowledge is not {} knowledge||state.SourceContextId!=knowledge.ContextId||
            state.KnownThrough!=knowledge.KnownAt||state.BasisAt>context.CalculationAt)
            throw new MarketDataException("projection_futures_state","A hypothetical basis requires its exact projection path and certified opening inputs.");
    }
}

public sealed class FuturesLifecycleProjection:ILifecycleProjection
{
    private readonly Trade trade;
    private readonly TradeInstrument holding;
    private readonly FuturesMarketBinding market;
    private readonly FuturesMarginPolicy policy;
    private readonly ValuationSetup setup;
    private readonly MarketSnapshot source;
    private readonly CurveModel model;
    private readonly ImmutableDictionary<Guid,FuturesMarginOccurrence> occurrences;
    private readonly ImmutableDictionary<Guid,KnownFuturesMargin> actualEvents;
    private readonly ImmutableDictionary<Guid,(CashLifecycleProjection Projection,LifecycleProjectionEvent Event)> cashEvents;
    private readonly ImmutableDictionary<Guid,Guid> cashOccurrences;
    public CurveRequest SettlementProjection{get;}
    public string Implementation=>"futures_margin_economic_projection_v1";
    public AccountingPositionKey Owner=>new(trade.Id,holding.Id,trade.BookId);
    public ReferenceKey Currency=>market.Instrument.Currency;
    public MarketSnapshot SourceSnapshot=>source;
    public ImmutableArray<CurveRequest> Dependencies{get;}
    public LifecycleProjectionState Opening{get;}
    public ImmutableArray<LifecycleInputFinal> InputFinals{get;}
    public ImmutableArray<LifecycleProjectionEvent> Events{get;}
    public LifecycleProjectionEvent? Activation{get;}
    private readonly Guid? pricingExpiryId;
    public FuturesLifecycleProjection(Trade trade,Guid bindingId,FuturesMarginState state,FuturesMarginHistory history,
        ValuationSetup setup,CurveRequest settlementProjection,CurveModel model,MarketSnapshot source,FuturesKnownHistory? known=null)
        :this(trade,bindingId,state,history,setup,settlementProjection,model,source,known,null){}
    public FuturesLifecycleProjection(KnownFuturesOptionEvent causal,FuturesKnownHistory known,ValuationSetup setup,
        CurveRequest settlementProjection,CurveModel model,MarketSnapshot source)
        :this(causal.Future?.Trade??throw Error("Retain the accepted option's exact linked future."),causal.Future.BindingId,null,null,
            setup,settlementProjection,model,source,known,causal){}
    private FuturesLifecycleProjection(Trade trade,Guid bindingId,FuturesMarginState? state,FuturesMarginHistory? history,
        ValuationSetup setup,CurveRequest settlementProjection,CurveModel model,MarketSnapshot source,FuturesKnownHistory? known,KnownFuturesOptionEvent? causal)
    {
        this.trade=trade;holding=trade.Instruments.SingleOrDefault(b=>b.Id==bindingId)??throw Error("Select the exact held future.");
        market=FuturesExecution.Require(holding).MarketBinding;policy=(history?.Terms??known?.Terms??throw Error("Retain the exact native execution policy.")).Policy;this.source=source;this.model=model;
        var terms=state is null?market.Instrument.RetainKnownMarginTerms(trade,bindingId,policy,model,source):
            market.Instrument.ProposeMarginTerms(trade,bindingId,policy,model,source);
        if(state is not null)
        {
        if(history is null||history.Terms!=terms||history.SourceContextId!=source.Knowledge?.ContextId||history.KnownThrough!=source.Knowledge.KnownAt||
            state.TradeId!=trade.Id||state.BindingId!=bindingId||state.BookId!=trade.BookId||state.Quantity!=holding.Quantity||
            state.SourceContextId!=source.Knowledge.ContextId||state.KnownThrough!=source.Knowledge.KnownAt||setup.Measure!=ValuationMeasure.PresentValue||
            !setup.BindingStates.TryGetValue(new(trade.Id,bindingId),out var supplied)||!ReferenceEquals(supplied,state))
            throw Error("Projection must retain the exact adopted native history, held quantity, accepted state and opening setup.");
        var execution=FuturesExecution.Require(holding);
        var past=policy.Occurrences.Where(o=>(o.At>execution.ExecutedAt||o.At==execution.ExecutedAt&&policy.Opening==FuturesMarginOpening.IncludeOpeningInstant)&&
            o.At<=source.ValuationAt).OrderBy(o=>o.At).ThenBy(o=>o.Phase).ToArray();
        if(history.Determinations.IsDefault||history.Determinations.Length!=past.Length||
            !past.Select(o=>o.Id).ToHashSet().SetEquals(history.Determinations.Select(d=>d.OccurrenceId)))
            throw Error("Every required opening margin occurrence needs its accepted native source history before economic projection.");
        if(past.Length>0)
        {
            var accepted=market.Instrument.PlanMarginTrajectory(trade,bindingId,policy,source,past[^1].Id,
                history.Determinations.ToImmutableDictionary(d=>d.OccurrenceId,d=>d.Evidence),history,ImmutableDictionary<Guid,DateTimeOffset>.Empty);
            if(accepted.Steps.Any(s=>!s.NoChange)||accepted.Basis.Price!=state.SettledBasis||accepted.Basis.At!=state.SettledAt||accepted.Basis.Status!=state.Status)
                throw Error("The complete native prefix and accepted opening basis must reconcile before projection.");
        }
        else if(state.SettledBasis!=ExactUnitPrice.FromPrice(execution.ExecutionPrice)||state.SettledAt!=execution.ExecutedAt||
            state.Status!=FuturesMarginStatus.Open||state.LatestSettlement is not null)
            throw Error("An inception must retain its exact original execution basis without invented accepted margin.");
        }
        else if(causal?.Future is not {} linked||known is null||causal.EventId==Guid.Empty||causal.EffectiveAt!=terms.ExecutedAt||
            causal.KnownAt>source.Knowledge!.KnownAt||linked.AdoptionEffectiveAt!=terms.ExecutedAt||linked.AdoptionKnownAt!=causal.KnownAt||
            linked.AdoptionPublicationOrder!=causal.PublicationOrder||known.Terms.Policy!=linked.MarginPolicy||setup.Measure!=ValuationMeasure.PresentValue||setup.BindingStates.ContainsKey(new(trade.Id,bindingId)))
            throw Error("A source-known future execution requires its exact causal native event without fabricated opening state.");
        setup=setup.ForPosition(Owner,market.Instrument.Id);
        this.setup=setup=setup with{FuturesBindings=setup.FuturesBindings.SetItem(market.Instrument.Id,market)};
        if(settlementProjection.Contract!=policy.Contract||model.AssetDefinition(settlementProjection.Curve).Rule!=CurveRule.Direct||
            PriceUnit.FromQuote(model.QuoteFor(settlementProjection))!=PriceUnit.FromQuote(market.Quote))
            throw Error("Select the exact future contract and native quotation for its explicit scenario-event market mapping.");
        SettlementProjection=settlementProjection;
        InputFinals=state?.Status==FuturesMarginStatus.FinalMarginDetermined?[]:
            [..new[]{market.Market,settlementProjection}.Distinct().Select(m=>new LifecycleInputFinal(m,
                policy.Occurrences.Single(o=>o.Phase==FuturesMarginPhase.Final).At,null))];
        Dependencies=state?.Status==FuturesMarginStatus.FinalMarginDetermined?[]:[..market.Dependencies.Append(settlementProjection)
            .Concat(ProjectedFuturesDelivery.Dependencies(policy)).Distinct()];
        if(known is not null)RequireKnown(known,history,terms,model);
        Opening=new(state is null?[]:[ValuationPortfolio.FromTrades([new(trade.Id,trade.BookId,[holding],trade.Booking)],setup)],[]){
            KnownCashSources=known is null?[]:[..known.Cash.Select(c=>c.State)],
            KnownPayments=known is null?[]:[..known.Cash.SelectMany(c=>c.State.KnownFlows(source)).Where(p=>p.EffectiveAt>source.ValuationAt)]};
        Activation=causal is null?null:new(LifecycleProjectionEvent.ScopedId(Owner,causal.EventId),terms.ExecutedAt,[],"known_future_execution");
        var future=state?.Status==FuturesMarginStatus.FinalMarginDetermined?[]:policy.Occurrences.Where(o=>o.At>terms.ExecutedAt||
            o.At==terms.ExecutedAt&&policy.Opening==FuturesMarginOpening.IncludeOpeningInstant).Where(o=>o.At>source.ValuationAt).OrderBy(o=>o.At).ThenBy(o=>o.Phase).ToArray();
        occurrences=future.ToImmutableDictionary(o=>LifecycleProjectionEvent.ScopedId(Owner,o.Id));
        actualEvents=known is null?ImmutableDictionary<Guid,KnownFuturesMargin>.Empty:known.Determinations.Where(d=>d.Occurrence.At>source.ValuationAt)
            .ToImmutableDictionary(d=>LifecycleProjectionEvent.ScopedId(Owner,d.Occurrence.Id));
        var events=future.Select((o,n)=>new LifecycleProjectionEvent(LifecycleProjectionEvent.ScopedId(Owner,o.Id),o.At,
            n==0?(Activation is {} activation?[activation.Id]:[]):[LifecycleProjectionEvent.ScopedId(Owner,future[n-1].Id)],o.Phase==FuturesMarginPhase.Final?"final_margin":"daily_margin")).ToList();
        var cashEvents=ImmutableDictionary.CreateBuilder<Guid,(CashLifecycleProjection,LifecycleProjectionEvent)>();
        foreach(var cash in known?.Cash??[])
        {
            var projection=new CashLifecycleProjection(cash.State.Position,(CashPaymentInstrument)cash.Holding.Instrument,cash.State,
                setup.InstrumentDiscounting.GetValueOrDefault(market.Instrument.Id)??setup.Discounting,source);
            foreach(var due in projection.Events)
            {
                cashEvents.Add(due.Id,(projection,due));
                events.Add(due with{Requires=[LifecycleProjectionEvent.ScopedId(Owner,cash.Occurrence.Id)]});
            }
        }
        cashOccurrences=known is null?ImmutableDictionary<Guid,Guid>.Empty:known.Cash.ToImmutableDictionary(c=>c.Holding.Id,c=>c.Occurrence.Id);
        if(Activation is {} start)events.Add(start);
        pricingExpiryId=state?.Status!=FuturesMarginStatus.FinalMarginDetermined&&market.Contract.Expiry>source.ValuationAt&&
            policy.Occurrences.Single(o=>o.Phase==FuturesMarginPhase.Final).At>market.Contract.Expiry?
            LifecycleProjectionEvent.ScopedId(Owner,ContractIdentity.FromCanonical($"futures_native_mark_expiry_v1:{market.Contract.Key.Id:D}:{market.Contract.Expiry:O}")):null;
        if(pricingExpiryId is {} expiryId)
        {
            events.Add(new(expiryId,market.Contract.Expiry,[..future.Where(o=>o.At<=market.Contract.Expiry).Select(o=>LifecycleProjectionEvent.ScopedId(Owner,o.Id)),
                ..Activation is {} activated?new[]{activated.Id}:[]],"future_mark_expiry"));
            for(var n=0;n<events.Count;n++)if(events[n].Kind=="final_margin")events[n]=events[n] with{Requires=events[n].Requires.Add(expiryId)};
        }
        this.cashEvents=cashEvents.ToImmutable();Events=[..events.OrderBy(e=>e.At).ThenBy(e=>e.Id)];
    }
    public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
    {
        if(occurrence==Activation)
        {
            var execution=FuturesExecution.Require(holding);var executionBasis=ExactUnitPrice.FromPrice(execution.ExecutionPrice);
            var projected=new ProjectedFuturesMarginState(trade.Id,holding.Id,trade.BookId,holding.Quantity,market.Series.Key,executionBasis,
                execution.ExecutedAt,false,source.Knowledge!.ContextId,source.Knowledge.KnownAt);
            return state with{Residuals=state.Residuals.Add(ValuationPortfolio.FromTrades([trade],setup with{
                BindingStates=setup.BindingStates.SetItem(new(trade.Id,holding.Id),projected)}))};
        }
        if(occurrence.Id==pricingExpiryId)
        {
            var active=state.Residuals.Single(p=>p.Positions.Any(p=>p.Owner==Owner.Owner&&p.BindingId==holding.Id));
            var current=active.Setup.BindingStates[new(trade.Id,holding.Id)];
            var(carriedBasis,carriedBasisAt)=current switch{FuturesMarginState native=>(native.SettledBasis,native.SettledAt),
                ProjectedFuturesMarginState projected=>(projected.Basis,projected.BasisAt),_=>throw Error("Retain the exact future basis at its projected mark-expiry boundary.")};
            var carried=new ProjectedFuturesMarginState(trade.Id,holding.Id,trade.BookId,holding.Quantity,market.Series.Key,carriedBasis,carriedBasisAt,false,
                source.Knowledge!.ContextId,source.Knowledge.KnownAt);
            var retained=ValuationPortfolio.FromTrades([new(trade.Id,trade.BookId,[holding],trade.Booking)],active.Setup with{
                BindingStates=active.Setup.BindingStates.SetItem(new(trade.Id,holding.Id),carried)});
            return state with{Residuals=[..state.Residuals.Where(p=>!ReferenceEquals(p,active)),retained]};
        }
        if(cashEvents.TryGetValue(occurrence.Id,out var cashEvent))
        {
            var active=state.Residuals.Single(p=>p.Positions.Any(p=>p.Owner==cashEvent.Projection.Owner.Owner&&p.BindingId==cashEvent.Projection.Owner.BindingId));
            var discharged=cashEvent.Projection.Apply(cashEvent.Event,new([active],[]),context);
            return state with{Residuals=[..state.Residuals.Where(p=>!ReferenceEquals(p,active)),..discharged.Residuals],Cash=state.Cash.AddRange(discharged.Cash)};
        }
        var selected=occurrences[occurrence.Id];
        var prior=state.Residuals.Single(p=>p.Positions.Any(p=>p.Owner==Owner.Owner&&p.BindingId==holding.Id));
        var basis=prior.Setup.BindingStates[new(trade.Id,holding.Id)] switch{
            FuturesMarginState accepted=>accepted.SettledBasis,ProjectedFuturesMarginState projected=>projected.Basis,
            _=>throw Error("The future has no qualified actual or path-local settlement basis.")};
        var actual=actualEvents.GetValueOrDefault(occurrence.Id);
        if(actual is not null&&actual.PreviousBasis!=basis)throw Error("The accepted future occurrence must activate from its exact retained causal basis.");
        var price=actual is null?context.Fix(new(SettlementProjection,
            selected.Phase==FuturesMarginPhase.Final?ObservationRole.FinalContractSettlement:ObservationRole.DailySettlement,selected.At)).Price:
            ExactUnitPrice.FromPrice(new(actual.Observation.Value,market.Quote));
        var(_,cash,_)=FuturesMargin.Difference(basis,price,holding.Quantity,policy.Rounding,policy.RoundingScope);
        ValuationPortfolio residual;
        if(selected.Phase==FuturesMarginPhase.Final&&market.SettlementMethod=="physical")
            residual=ProjectedFuturesDelivery.Residual(Owner,market.Instrument,policy,holding.Quantity,price,setup);
        else
        {
            var projectedState=new ProjectedFuturesMarginState(trade.Id,holding.Id,trade.BookId,holding.Quantity,market.Series.Key,price,selected.At,
                selected.Phase==FuturesMarginPhase.Final,source.Knowledge!.ContextId,source.Knowledge.KnownAt);
            var projectedSetup=prior.Setup with{BindingStates=prior.Setup.BindingStates.SetItem(new(trade.Id,holding.Id),projectedState)};
            if(projectedSetup.FuturesValueBindings.TryGetValue(new(trade.Id,holding.Id),out var funding))
                projectedSetup=projectedSetup with{FuturesValueBindings=projectedSetup.FuturesValueBindings.SetItem(new(trade.Id,holding.Id),funding.Advance(selected.Id))};
            residual=ValuationPortfolio.FromTrades([new(trade.Id,trade.BookId,[holding],trade.Booking)],projectedSetup);
        }
        var residuals=state.Residuals.Where(p=>!ReferenceEquals(p,prior)).Append(residual);
        if(actual is not null)residuals=residuals.Concat(cashEvents.Values.Select(p=>p.Projection).DistinctBy(p=>p.Owner)
            .Where(p=>p.Owner.BindingId!=holding.Id&&Opening.KnownCashSources.Any(c=>c.Position==p.Owner&&
                cashOccurrences[c.BindingId]==selected.Id)).SelectMany(p=>p.Opening.Residuals));
        return state with{Residuals=[..residuals],Cash=actual is not null||cash.Amount==default?state.Cash:
            state.Cash.Add(new(occurrence.Id,occurrence.At,selected.DueAt,ExactMoney.FromMoney(cash)))};
    }
    public ImmutableArray<LifecycleMarketUse> MarketUsesFor(DateTimeOffset horizon)
    {
        var final=policy.Occurrences.Single(o=>o.Phase==FuturesMarginPhase.Final).At;var through=final<horizon?final:horizon;
        var delivery=ProjectedFuturesDelivery.Dependencies(policy);
        return [..Dependencies.Select(m=>new LifecycleMarketUse(m,delivery.Contains(m)?horizon:through))];
    }
    public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)
    {
        if(market.SettlementMethod!="cash"&&Events.Any(e=>e.At<=horizon&&e.Kind=="final_margin"))
            ProjectedFuturesDelivery.Require(market.Instrument,policy,model,setup,source,actualEvents.Values.Any(e=>e.Occurrence.Phase==FuturesMarginPhase.Final&&e.Occurrence.At<=horizon));
        return [..Events.Where(e=>e.At<=horizon)];
    }
    private void RequireKnown(FuturesKnownHistory known,FuturesMarginHistory? opening,FuturesMarginTerms terms,CurveModel model)
    {
        if(known.Owner!=Owner||known.Terms!=terms||known.SourceContextId!=source.Knowledge!.ContextId||known.KnownThrough!=source.Knowledge.KnownAt||
            known.Determinations.IsDefault||known.Cash.IsDefault||known.Determinations.Select(d=>d.Occurrence.Id).Distinct().Count()!=known.Determinations.Length||
            known.Cash.Select(c=>c.Holding.Id).Distinct().Count()!=known.Cash.Length)
            throw Error("Retain complete distinct knowledge-only native determinations and cash under this exact source and execution.");
        var required=policy.Occurrences.Where(o=>o.At>terms.ExecutedAt||o.At==terms.ExecutedAt&&policy.Opening==FuturesMarginOpening.IncludeOpeningInstant)
            .OrderBy(o=>o.At).ThenBy(o=>o.Phase).ToArray();
        var accepted=known.Determinations.OrderBy(d=>d.Occurrence.At).ThenBy(d=>d.Occurrence.Phase).ToArray();
        if(!accepted.Select(d=>d.Occurrence).SequenceEqual(required.Take(accepted.Length))||accepted.Length>required.Length||
            !accepted.Where(d=>d.Occurrence.At<=source.ValuationAt).Select(d=>d.DeterminationId).ToHashSet().SetEquals((opening?.Determinations??[]).Select(d=>d.DeterminationId)))
            throw Error("Knowledge-only accepted events must preserve the complete opening prefix and every native successor.");
        var official=market.Instrument.SettlementSource(market,policy.SettlementCurve,model);
        var basis=ExactUnitPrice.FromPrice(terms.ExecutionPrice);Guid? previous=null;var publication=0;var knownAt=DateTimeOffset.MinValue;
        foreach(var acceptedEvent in accepted)
        {
            var observation=acceptedEvent.Observation;var role=acceptedEvent.Occurrence.Phase==FuturesMarginPhase.Final?ObservationRole.FinalContractSettlement:ObservationRole.DailySettlement;
            if(acceptedEvent.DeterminationId==Guid.Empty||acceptedEvent.RunRevision<1||acceptedEvent.PublicationOrder<publication||
                acceptedEvent.PublicationOrder<1||!TradingInstant.IsSupported(acceptedEvent.KnownAt)||acceptedEvent.KnownAt>known.KnownThrough||
                acceptedEvent.KnownAt<knownAt||acceptedEvent.KnownAt<acceptedEvent.ObservationKnownAt||!TradingInstant.IsSupported(acceptedEvent.ObservationKnownAt)||
                acceptedEvent.PublicationOrder==publication&&acceptedEvent.KnownAt!=knownAt||acceptedEvent.BasisDeterminationId!=previous||
                acceptedEvent.PreviousBasis!=basis||observation.Series!=official.ObservationSeries||observation.Source!=official.Source||
                observation.Role!=role||observation.State!=PublicationState.Final||observation.ObservedAt!=acceptedEvent.Occurrence.At||
                observation.Value.CompareTo(market.Quote.Minimum)<0||observation.Value.CompareTo(market.Quote.Maximum)>0||
                !observation.Value.IsMultipleOf(market.Quote.ClearingIncrement))
                throw Error("Accepted future events require their exact ordered native source, basis, amount and certified publication.");
            var price=ExactUnitPrice.FromPrice(new(observation.Value,market.Quote));
            var(exact,total,_)=FuturesMargin.Difference(basis,price,holding.Quantity,policy.Rounding,policy.RoundingScope);
            if(exact!=acceptedEvent.ExactVariation||total!=acceptedEvent.RoundedTotal)
                throw Error("Accepted future economics must reconcile the original quantity and selected rounding.");
            basis=price;previous=acceptedEvent.DeterminationId;publication=acceptedEvent.PublicationOrder;knownAt=acceptedEvent.KnownAt;
        }
        foreach(var cash in known.Cash)
            if(cash.Occurrence.At<=source.ValuationAt||!accepted.Any(d=>d.Occurrence==cash.Occurrence)||
                cash.State.Position!=new AccountingPositionKey(trade.Id,cash.Holding.Id,trade.BookId)||cash.State.Quantity!=cash.Holding.Quantity||
                cash.State.InstrumentId!=cash.Holding.Instrument.Id||cash.Holding.Instrument is not CashPaymentInstrument payment||
                cash.Holding.Quantity.Specification!=Currency||cash.Holding.Quantity.Amount==default||cash.DeterminationId==Guid.Empty||
                cash.PublicationOrder<1||!TradingInstant.IsSupported(cash.KnownAt)||
                accepted.Single(d=>d.Occurrence==cash.Occurrence).PublicationOrder<cash.PublicationOrder||
                accepted.Single(d=>d.Occurrence==cash.Occurrence).KnownAt<cash.KnownAt||
                accepted.Single(d=>d.Occurrence==cash.Occurrence).PublicationOrder==cash.PublicationOrder&&
                    accepted.Single(d=>d.Occurrence==cash.Occurrence).KnownAt!=cash.KnownAt||
                payment.Currency!=Currency||payment.PaymentAt<cash.Occurrence.At||cash.KnownAt>known.KnownThrough||
                cash.State.SourceContextId!=known.SourceContextId||cash.State.KnownThrough!=known.KnownThrough||
                cash.State.KnownFlows(source).Any(p=>p.EffectiveAt<cash.Occurrence.At||p.KnownAt<cash.KnownAt))
                throw Error("An accepted future cash membership requires exact native owner, due terms and immutable payment proof.");
        foreach(var futureEvent in accepted.Where(d=>d.Occurrence.At>source.ValuationAt))
            if(known.Cash.Where(c=>c.Occurrence==futureEvent.Occurrence).Select(c=>ExactMoney.FromMoney(new(c.Holding.Quantity.Amount,Currency)))
                .Aggregate(ExactMoney.Parse("0",Currency),(a,b)=>a.Add(b))!=ExactMoney.FromMoney(futureEvent.RoundedTotal))
                throw Error("All original and adjustment cash must reconcile the accepted occurrence total exactly once.");
    }
    private static MarketDataException Error(string message)=>new("projection_futures_state",message);
}
