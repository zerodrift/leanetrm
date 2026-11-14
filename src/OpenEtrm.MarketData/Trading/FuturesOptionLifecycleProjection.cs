using System.Collections.Immutable;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// The expiry decision is contractual. It is independent of the pricing tree's
// optimal early-exercise decisions and retains the exact reference rule version.
public sealed record FuturesOptionAutomaticRule(ReferenceKey Key,int Version,string Evaluation,ExactDecimal Threshold,
    string AtTheMoneyCall,string AtTheMoneyPut)
{
    public string Decide(OptionRight right,ExactDecimal strike,ExactDecimal settlement)
    {
        Money.RequireKey(Key);
        if(Version<1||Evaluation!="settlement_moneyness_v1"||Threshold.CompareTo(default)<0||strike.CompareTo(default)<=0||
            !Enum.IsDefined(right)||AtTheMoneyCall is not("exercise" or "abandon")||AtTheMoneyPut is not("exercise" or "abandon"))
            throw new MarketDataException("option_exercise_rule","Select an exact supported automatic exercise rule and explicit at-the-money decisions.");
        var moneyness=right==OptionRight.Call?settlement-strike:strike-settlement;
        var exercise=moneyness==default?(right==OptionRight.Call?AtTheMoneyCall:AtTheMoneyPut)=="exercise":moneyness.CompareTo(Threshold)>0;
        return exercise?"exercised":"abandoned";
    }
}

// Supplied only by the certified native source reader. A hypothetical expiry
// decision has no native event ID or accepted publication and cannot use this type.
public sealed record KnownFuturesOptionInstruction(Guid EventId,string Instruction,DateTimeOffset EffectiveAt,
    DateTimeOffset KnownAt,Guid SourceContextId);

public sealed class FuturesOptionLifecycleProjection:ILifecycleProjection
{
    public string Implementation=>"listed_option_expiry_economic_projection_v1";
    public AccountingPositionKey Owner{get;}
    public ReferenceKey Currency=>option.Currency;
    public MarketSnapshot SourceSnapshot{get;}
    public ImmutableArray<CurveRequest> Dependencies{get;}
    public LifecycleProjectionState Opening{get;}
    public ImmutableArray<LifecycleInputFinal> InputFinals{get;}
    public ImmutableArray<LifecycleProjectionEvent> Events{get;}
    private readonly Trade trade;
    private readonly TradeInstrument holding;
    private readonly FuturesOptionInstrument option;
    private readonly FuturesMarketBinding future;
    private readonly FuturesMarginPolicy margin;
    private readonly FuturesOptionAutomaticRule rule;
    private readonly KnownFuturesOptionInstruction? instruction;
    private readonly CurveRequest expiryMarket,marginMarket,underlyingMarket;
    private readonly ValuationSetup setup;
    private readonly CurveModel model;
    private readonly Guid expiryId;
    private readonly Guid? instructionId;
    private readonly ImmutableDictionary<Guid,FuturesMarginOccurrence> marginOccurrences;
    private readonly ImmutableDictionary<Guid,KnownFuturesOptionEvent> actualEvents;
    private readonly ImmutableDictionary<Guid,(FuturesLifecycleProjection Child,LifecycleProjectionEvent Event)> childEvents;
    private readonly ImmutableArray<FuturesLifecycleProjection> children;
    public FuturesOptionLifecycleProjection(Trade trade,Guid bindingId,ValuationSetup setup,FuturesMarketBinding future,
        FuturesMarginPolicy margin,FuturesOptionAutomaticRule rule,KnownFuturesOptionInstruction? instruction,
        CurveRequest expiryMarket,CurveRequest marginMarket,CurveModel model,MarketSnapshot source,FuturesOptionKnownHistory? known=null,
        ImmutableDictionary<Guid,FuturesKnownHistory>? futureHistories=null)
    {
        holding=trade.Instruments.SingleOrDefault(h=>h.Id==bindingId)??throw Error("Retain the exact native option holding.");
        option=holding.Instrument as FuturesOptionInstrument??throw Error("Select a listed futures option.");
        Owner=new(trade.Id,bindingId,trade.BookId);this.trade=trade;this.future=future;this.margin=margin;this.rule=rule;
        this.instruction=instruction;this.expiryMarket=expiryMarket;this.marginMarket=marginMarket;
        this.setup=setup=setup.ForPosition(Owner,option.Id);
        this.model=model;SourceSnapshot=source;
        var expiry=option.Series.Terms.ExpiryAt;
        if(source.Knowledge is not {} knowledge||source.ValuationAt>=expiry||trade.BookId==Guid.Empty||
            setup.Measure!=ValuationMeasure.PresentValue||!setup.OptionBindings.TryGetValue(option.Id,out var calibration)||
            calibration.Instrument.Series!=option.Series||calibration.SnapshotId!=source.Id||
            future.Series.Key!=option.Series.UnderlyingSeries||future.Instrument.Currency!=Currency||
            option.Series.Multiplier!=future.Instrument.LotSize.Amount*future.Series.Multiplier||
            expiry<future.EligibleFrom||expiry>=future.Contract.Expiry||
            rule.Key.Revision!=option.Series.Key.Revision||rule.Key.Code!=option.Series.AutomaticExerciseRule||
            model.ContractDefinition(expiryMarket.Contract)!=future.Contract||model.ContractDefinition(marginMarket.Contract)!=future.Contract||
            model.AssetDefinition(expiryMarket.Curve).Rule!=CurveRule.Direct||model.AssetDefinition(marginMarket.Curve).Rule!=CurveRule.Direct||
            PriceUnit.FromQuote(model.QuoteFor(expiryMarket))!=PriceUnit.FromQuote(future.Quote)||
            PriceUnit.FromQuote(model.QuoteFor(marginMarket))!=PriceUnit.FromQuote(future.Quote))
            throw Error("Retain exact live option terms, calibration, linked-future multiplier, source markets and source certificate.");
        underlyingMarket=calibration.Underlying;
        _=rule.Decide(option.Series.Terms.Right,option.Series.Terms.Strike,option.Series.Terms.Strike);
        if(instruction is not null&&(!option.Series.ContraryInstructionsAllowed||instruction.EventId==Guid.Empty||
            instruction.Instruction is not("exercise" or "abandon")||!TradingInstant.IsSupported(instruction.EffectiveAt)||
            !TradingInstant.IsSupported(instruction.KnownAt)||instruction.SourceContextId!=knowledge.ContextId||
            instruction.EffectiveAt>option.Series.InstructionCutoffAt||instruction.KnownAt>knowledge.KnownAt))
            throw Error("A contrary instruction requires its exact captured native event and eligible cutoff.");
        _=future.Instrument.ValidateMarginPolicy(future,margin,model,source);
        if(expiry<margin.From||expiry>=margin.Until)throw Error("The selected linked-future margin policy must cover its generated execution instant.");
        Opening=new([ValuationPortfolio.FromTrades([new(trade.Id,trade.BookId,[holding],trade.Booking)],setup)],[]);
        expiryId=LifecycleProjectionEvent.ScopedId(Owner,ContractIdentity.FromCanonical($"option_expiry_v1:{option.Id:D}"));
        instructionId=instruction is {} selectedInstruction&&selectedInstruction.EffectiveAt>source.ValuationAt?
            LifecycleProjectionEvent.ScopedId(Owner,selectedInstruction.EventId):null;
        var selected=margin.Occurrences.Where(o=>o.At>expiry||o.At==expiry&&margin.Opening==FuturesMarginOpening.IncludeOpeningInstant)
            .OrderBy(o=>o.At).ThenBy(o=>o.Phase).ToArray();
        marginOccurrences=selected.ToImmutableDictionary(o=>LifecycleProjectionEvent.ScopedId(Owner,o.Id));
        if(known is not null)RequireKnown(known);
        var native=known?.Events.Where(e=>e.EffectiveAt>source.ValuationAt&&e.Kind is "exercise" or "assignment" or "expiry").ToArray()??[];
        actualEvents=native.ToImmutableDictionary(e=>LifecycleProjectionEvent.ScopedId(Owner,e.EventId));
        var knownExpiry=native.Any(e=>e.Kind=="expiry");
        var events=actualEvents.Select(p=>new LifecycleProjectionEvent(p.Key,p.Value.EffectiveAt,
            [..actualEvents.Where(prior=>prior.Value.PublicationOrder<p.Value.PublicationOrder||prior.Value.PublicationOrder==p.Value.PublicationOrder&&
                prior.Value.EffectiveAt<p.Value.EffectiveAt).Select(prior=>prior.Key)],"known_option_event")).ToList();
        if(instructionId is {} knownId)events.Add(new(knownId,instruction!.EffectiveAt,[],"known_option_instruction"));
        if(!knownExpiry)
        {
            events.Add(new(expiryId,expiry,[..actualEvents.Keys,..instructionId is {} prior?new[]{prior}:[]],"option_expiry"));
            events.AddRange(selected.Select((o,n)=>new LifecycleProjectionEvent(LifecycleProjectionEvent.ScopedId(Owner,o.Id),o.At,
                [n==0?expiryId:LifecycleProjectionEvent.ScopedId(Owner,selected[n-1].Id)],o.Phase==FuturesMarginPhase.Final?"final_margin":"daily_margin")));
        }
        var children=ImmutableArray.CreateBuilder<FuturesLifecycleProjection>();
        var childEvents=ImmutableDictionary.CreateBuilder<Guid,(FuturesLifecycleProjection,LifecycleProjectionEvent)>();
        if(futureHistories is null)futureHistories=ImmutableDictionary<Guid,FuturesKnownHistory>.Empty;
        if(!native.Where(e=>e.Future is not null).Select(e=>e.EventId).ToHashSet().SetEquals(futureHistories.Keys))
            throw Error("Prepare exactly every accepted future-linked execution and its complete knowledge-only margin history.");
        foreach(var accepted in native.Where(e=>e.Future is not null))
        {
            var linked=accepted.Future!;var held=linked.Trade.Instruments.Single(h=>h.Id==linked.BindingId);var linkedMarket=FuturesExecution.Require(held).MarketBinding;
            var childSetup=setup with{InstrumentDiscounting=setup.InstrumentDiscounting.SetItem(linkedMarket.Instrument.Id,calibration.Discounting)};
            var child=new FuturesLifecycleProjection(accepted,futureHistories[accepted.EventId],childSetup,linkedMarket.Market,model,source);children.Add(child);
            foreach(var e in child.Events)
            {
                childEvents.Add(e.Id,(child,e));events.Add(e==child.Activation?e with{Requires=[LifecycleProjectionEvent.ScopedId(Owner,accepted.EventId)]}:e);
            }
        }
        this.children=children.ToImmutable();this.childEvents=childEvents.ToImmutable();Events=[..events.OrderBy(e=>e.At).ThenBy(e=>e.Id)];
        Opening=Opening with{KnownCashSources=[..this.children.SelectMany(c=>c.Opening.KnownCashSources)],
            KnownPayments=[..this.children.SelectMany(c=>c.Opening.KnownPayments)]};
        Dependencies=[..new[]{calibration.Underlying,future.Market,expiryMarket,marginMarket}.Concat(ProjectedFuturesDelivery.Dependencies(margin))
            .Concat(this.children.SelectMany(c=>c.Dependencies)).Distinct()];
        InputFinals=[..new[]{future.Market,marginMarket}.Distinct().Select(m=>new LifecycleInputFinal(m,
            margin.Occurrences.Single(o=>o.Phase==FuturesMarginPhase.Final).At,null)),..this.children.SelectMany(c=>c.InputFinals)];
    }
    public ImmutableArray<LifecycleMarketUse> MarketUsesFor(DateTimeOffset horizon)
    {
        var expiry=option.Series.Terms.ExpiryAt<horizon?option.Series.Terms.ExpiryAt:horizon;
        var final=margin.Occurrences.Single(o=>o.Phase==FuturesMarginPhase.Final).At;var through=final<horizon?final:horizon;
        return [..new[]{new LifecycleMarketUse(underlyingMarket,expiry),new(expiryMarket,expiry),new(future.Market,through),new(marginMarket,through)}
            .Concat(ProjectedFuturesDelivery.Dependencies(margin).Select(m=>new LifecycleMarketUse(m,horizon)))
            .Concat(children.SelectMany(c=>c.MarketUsesFor(horizon))).GroupBy(u=>u.Market).Select(g=>new LifecycleMarketUse(g.Key,g.Max(u=>u.Through)))];
    }
    public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)
    {
        foreach(var child in children)_=child.EventsFor(horizon);
        if(instruction?.Instruction!="abandon"&&future.SettlementMethod!="cash"&&Events.Any(e=>e.At<=horizon&&e.Kind=="final_margin"))
            ProjectedFuturesDelivery.Require(future.Instrument,margin,model,FutureSetup(),SourceSnapshot,false);
        return [..Events.Where(e=>e.At<=horizon)];
    }
    public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
    {
        if(!Events.Contains(occurrence))throw Error("Select this exact declared option or generated-future event.");
        if(childEvents.TryGetValue(occurrence.Id,out var delegated))
        {
            var child=delegated.Child;var ids=child.Events.Select(e=>e.Id).ToHashSet();
            var residuals=state.Residuals.Where(p=>p.Positions.All(p=>p.Owner==child.Owner.Owner)).ToImmutableArray();
            var current=new LifecycleProjectionState(residuals,[..state.Cash.Where(c=>ids.Contains(c.Id))]){
                KnownCashSources=child.Opening.KnownCashSources,KnownPayments=child.Opening.KnownPayments};
            var next=child.Apply(delegated.Event,current,context);
            return state with{Residuals=[..state.Residuals.Where(p=>!residuals.Contains(p)),..next.Residuals],
                Cash=[..state.Cash.Where(c=>!ids.Contains(c.Id)),..next.Cash]};
        }
        if(actualEvents.TryGetValue(occurrence.Id,out var actual))
        {
            var remaining=actual.RemainingLots*(holding.Quantity.Amount.CompareTo(default)<0?ExactDecimal.Parse("-1"):ExactDecimal.Parse("1"));
            return state with{Residuals=[..state.Residuals.Where(p=>!p.Positions.Any(p=>p.Owner==Owner.Owner&&p.BindingId==holding.Id)),
                ..remaining==default?Array.Empty<ValuationPortfolio>():new[]{OptionPortfolio(remaining)}]};
        }
        if(occurrence.Id==instructionId)return state;
        if(occurrence.Id==expiryId)
        {
            var active=state.Residuals.SingleOrDefault(p=>p.Positions.Any(p=>p.Owner==Owner.Owner&&p.BindingId==holding.Id&&p.ValuationId==option.Id));
            if(active is null)return state;
            var remaining=active.Positions.Single(p=>p.Owner==Owner.Owner&&p.BindingId==holding.Id).Quantity.Amount;
            var fixing=context.Fix(new(expiryMarket,ObservationRole.DailySettlement,occurrence.At));
            var decision=instruction is {} known?(known.Instruction=="exercise"?"exercised":"abandoned"):
                rule.Decide(option.Series.Terms.Right,option.Series.Terms.Strike,fixing.Price.ToUnitPriceExact().Value);
            return state with{Residuals=[..state.Residuals.Where(p=>!ReferenceEquals(p,active)),..decision=="abandoned"?Array.Empty<ValuationPortfolio>():
                new[]{FuturePortfolio(ExactUnitPrice.FromPrice(new(option.Series.Terms.Strike,future.Quote)),occurrence.At,false,
                    remaining*(option.Series.Terms.Right==OptionRight.Call?ExactDecimal.Parse("1"):ExactDecimal.Parse("-1")))}]};
        }
        var activeFuture=state.Residuals.SingleOrDefault(p=>p.Positions.Any(p=>p.Owner==Owner.Owner&&p.BindingId==holding.Id&&p.ValuationId==future.Instrument.Id));
        if(activeFuture is null)return state;
        var selected=marginOccurrences[occurrence.Id];
        var prior=(ProjectedFuturesMarginState)activeFuture.Setup.BindingStates[new(trade.Id,holding.Id)];
        var fact=context.Fix(new(marginMarket,selected.Phase==FuturesMarginPhase.Final?ObservationRole.FinalContractSettlement:ObservationRole.DailySettlement,selected.At));
        var(_,cash,_)=FuturesMargin.Difference(prior.Basis,fact.Price,prior.Quantity,margin.Rounding,margin.RoundingScope);
        var residual=selected.Phase==FuturesMarginPhase.Final&&future.SettlementMethod=="physical"?
            ProjectedFuturesDelivery.Residual(Owner,future.Instrument,margin,prior.Quantity,fact.Price,activeFuture.Setup):
            FuturePortfolio(fact.Price,selected.At,selected.Phase==FuturesMarginPhase.Final,prior.Quantity.Amount.DivideExact(future.Instrument.LotSize.Amount));
        return state with{Residuals=[..state.Residuals.Where(p=>!ReferenceEquals(p,activeFuture)),residual],
            Cash=cash.Amount==default?state.Cash:state.Cash.Add(new(occurrence.Id,occurrence.At,selected.DueAt,ExactMoney.FromMoney(cash)))};
    }
    private ValuationPortfolio FuturePortfolio(ExactUnitPrice basis,DateTimeOffset at,bool final,ExactDecimal lots)
    {
        var holding=FuturesExecution.Bind(this.holding.Id,future,lots,new(option.Series.Terms.Strike,future.Quote),option.Series.Terms.ExpiryAt,FuturesSettlementState.Unprocessed);
        var knowledge=SourceSnapshot.Knowledge!;
        var state=new ProjectedFuturesMarginState(trade.Id,holding.Id,trade.BookId,holding.Quantity,future.Series.Key,basis,at,final,knowledge.ContextId,knowledge.KnownAt);
        var projected=FutureSetup() with{FuturesBindings=setup.FuturesBindings.SetItem(future.Instrument.Id,future),
            BindingStates=setup.BindingStates.SetItem(new(trade.Id,holding.Id),state)};
        return ValuationPortfolio.FromTrades([new(trade.Id,trade.BookId,[holding],trade.Booking)],projected);
    }
    private ValuationSetup FutureSetup()=>new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(future.Instrument.Id,future),null){
        InstrumentDiscounting=ImmutableDictionary<Guid,ISideDiscounting>.Empty.Add(future.Instrument.Id,setup.OptionBindings[option.Id].Discounting)};
    private ValuationPortfolio OptionPortfolio(ExactDecimal lots)=>ValuationPortfolio.FromTrades([new(trade.Id,trade.BookId,
        [new(holding.Id,option,new(lots,holding.Quantity.Specification))],trade.Booking)],setup);
    private void RequireKnown(FuturesOptionKnownHistory known)
    {
        if(known.Owner!=Owner||known.Series!=option.Series.Key||known.TradeRevision<1||known.SourceContextId!=SourceSnapshot.Knowledge!.ContextId||
            known.KnownThrough!=SourceSnapshot.Knowledge.KnownAt||known.Events.IsDefault||known.OriginalLots==default||
            known.OriginalLots.CompareTo(default)!=holding.Quantity.Amount.CompareTo(default)||known.Events.Select(e=>e.EventId).Distinct().Count()!=known.Events.Length)
            throw Error("Retain complete native option events under this exact owner, series and certified source.");
        var remaining=known.OriginalLots.CompareTo(default)<0?-known.OriginalLots:known.OriginalLots;
        var atOpening=remaining;
        foreach(var e in known.Events.OrderBy(e=>e.EffectiveAt).ThenBy(e=>e.PublicationOrder))
        {
            if(e.EventId==Guid.Empty||!TradingInstant.IsSupported(e.EffectiveAt)||e.BusinessAt!=e.EffectiveAt||!TradingInstant.IsSupported(e.KnownAt)||
                e.KnownAt>known.KnownThrough||e.PublicationOrder<1||e.Kind is not("exercise" or "assignment" or "expiry" or "exercise_instruction" or "abandon_instruction"))
                throw Error("Every accepted option event requires exact native effect, publication and action terms.");
            if(e.Kind is "exercise" or "assignment")
            {
                if(e.Lots is not {} lots||lots.CompareTo(default)<=0||!lots.IsMultipleOf(ExactDecimal.Parse("1"))||lots.CompareTo(remaining)>0||
                    e.Future is null||e.EffectiveAt>=option.Series.Terms.ExpiryAt)throw Error("Accepted exercise requires exact closed lots and its native linked future.");
                remaining-=lots;
            }
            else if(e.Kind=="expiry")
            {
                if(e.EffectiveAt!=option.Series.Terms.ExpiryAt||e.Outcome is not("exercised" or "abandoned")||
                    (e.Outcome=="exercised")!=(e.Future is not null))throw Error("Accepted expiry retains its actual outcome and linked future without re-evaluation.");
                remaining=default;
            }
            if(e.RemainingLots!=remaining)throw Error("Native option events must retain their complete remaining-right trajectory.");
            if(e.Future is {} linked)
            {
                var h=linked.Trade.Instruments.SingleOrDefault(h=>h.Id==linked.BindingId)??throw Error("Retain the exact linked future binding.");
                var execution=FuturesExecution.Require(h);var closed=e.Kind=="expiry"?e.Lots??throw Error("Retain the native expired lots."):e.Lots!.Value;
                var sign=(known.OriginalLots.CompareTo(default)<0?ExactDecimal.Parse("-1"):ExactDecimal.Parse("1"))*(option.Series.Terms.Right==OptionRight.Call?ExactDecimal.Parse("1"):ExactDecimal.Parse("-1"));
                if(execution.Instrument.SeriesKey!=option.Series.UnderlyingSeries||linked.Trade.BookId!=Owner.BookId||
                    execution.ExecutedAt!=e.EffectiveAt||execution.ExecutionPrice.Value!=option.Series.Terms.Strike||
                    h.Quantity.Amount!=closed*sign*execution.Instrument.LotSize.Amount)
                    throw Error("Accepted linked futures must retain the exact strike, signed lots, underlying series, book and execution instant.");
            }
            if(e.EffectiveAt<=SourceSnapshot.ValuationAt)atOpening=remaining;
        }
        var open=holding.Quantity.Amount.CompareTo(default)<0?-holding.Quantity.Amount:holding.Quantity.Amount;
        if(atOpening!=open)throw Error("Source-known past events must reconcile the held opening option lots exactly.");
    }
    private static MarketDataException Error(string message)=>new("projection_option_state",message);
}
