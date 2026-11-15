using System.Collections.Immutable;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// The expiry decision is contractual. It is independent of the pricing tree's
// optimal early-exercise decisions and retains the exact reference rule version.
public sealed record FuturesOptionAutomaticRule(ReferenceKey Key,int Version,string Evaluation,ExactDecimal Threshold,
    string AtTheMoneyCall,string AtTheMoneyPut)
{
    public string Decide(OptionRight right,ExactDecimal strike,ExactDecimal settlement)
    {
        Require(right,strike.CompareTo(default)>0);
        var moneyness=right==OptionRight.Call?settlement-strike:strike-settlement;
        return Decide(right,moneyness==default,moneyness.CompareTo(Threshold));
    }
    internal string Decide(OptionRight right,LatticeDecimal strike,LatticeDecimal settlement)
    {
        Require(right,strike.CompareTo(default)>0);
        var moneyness=right==OptionRight.Call?settlement-strike:strike-settlement;
        return Decide(right,moneyness==default,moneyness.CompareTo(LatticeDecimal.From(Threshold)));
    }
    private void Require(OptionRight right,bool positiveStrike)
    {
        Money.RequireKey(Key);
        if(Version<1||Evaluation!="settlement_moneyness_v1"||Threshold.CompareTo(default)<0||!positiveStrike||
            !Enum.IsDefined(right)||AtTheMoneyCall is not("exercise" or "abandon")||AtTheMoneyPut is not("exercise" or "abandon"))
            throw new MarketDataException("option_exercise_rule","Select an exact supported automatic exercise rule and explicit at-the-money decisions.");
    }
    private string Decide(OptionRight right,bool atTheMoney,int comparison)
    {
        var exercise=atTheMoney?(right==OptionRight.Call?AtTheMoneyCall:AtTheMoneyPut)=="exercise":comparison>0;
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
    public ImmutableArray<MarketSnapshot> MarketSources=>deliveryMarket is null||deliveryMarket.Source.HasSameInputs(SourceSnapshot)?[SourceSnapshot]:[SourceSnapshot,deliveryMarket.Source];
    public MarketSnapshot DependencySource(CurveRequest request)=>deliveryMarket is {} selected&&request==selected.Terms.Mark?selected.Source:SourceSnapshot;
    public MarketSnapshot ValuationSource(AccountingPositionKey position,Instrument instrument)=>deliveryMarket is {} selected&&
        instrument is GasPhysicalDeliveryObligationInstrument obligation&&obligation.Terms==selected.Terms?selected.Source:SourceSnapshot;
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
    private readonly FuturesOptionPhysicalValueBinding? physical;
    private readonly FuturesDeliveryProjectionMarket? deliveryMarket;
    private readonly DateTimeOffset? nativeClosedAt;
    private readonly Guid expiryId;
    private readonly Guid? instructionId;
    private readonly ImmutableDictionary<Guid,FuturesMarginOccurrence> marginOccurrences;
    private readonly ImmutableDictionary<Guid,KnownFuturesOptionEvent> actualEvents;
    private readonly ImmutableArray<KnownFuturesOptionEvent> knownInstructions;
    private readonly ImmutableDictionary<Guid,KnownFuturesOptionEvent> instructionEvents;
    private readonly ImmutableDictionary<Guid,(FuturesLifecycleProjection Child,LifecycleProjectionEvent Event)> childEvents;
    private readonly ImmutableArray<FuturesLifecycleProjection> children;
    public ImmutableArray<FuturesLifecycleProjection> ExternalNativeFutures{get;}=[];
    public FuturesOptionLifecycleProjection(Trade trade,Guid bindingId,ValuationSetup setup,FuturesMarketBinding future,
        FuturesMarginPolicy margin,FuturesOptionAutomaticRule rule,KnownFuturesOptionInstruction? instruction,
        CurveRequest expiryMarket,CurveRequest marginMarket,CurveModel model,MarketSnapshot source,FuturesOptionKnownHistory? known=null,
        ImmutableDictionary<Guid,FuturesKnownHistory>? futureHistories=null,FuturesDeliveryProjectionMarket? deliveryMarket=null,
        ImmutableDictionary<Guid,FuturesLifecycleProjection>? nativeContinuations=null)
    {
        holding=trade.Instruments.SingleOrDefault(h=>h.Id==bindingId)??throw Error("Retain the exact native option holding.");
        option=holding.Instrument as FuturesOptionInstrument??throw Error("Select a listed futures option.");
        Owner=new(trade.Id,bindingId,trade.BookId);this.trade=trade;this.future=future;this.margin=margin;this.rule=rule;
        this.instruction=instruction;this.expiryMarket=expiryMarket;this.marginMarket=marginMarket;
        this.setup=setup=setup.ForPosition(Owner,option.Id);
        this.model=model;SourceSnapshot=source;
        physical=setup.PhysicalOptionValueBindings.GetValueOrDefault(new(trade.Id,bindingId));
        if(physical is not null)
        {
            if((physical.Margin with{Occurrences=ImmutableArray<FuturesMarginOccurrence>.Empty})!=(margin with{Occurrences=ImmutableArray<FuturesMarginOccurrence>.Empty})||
                !physical.Margin.Occurrences.SequenceEqual(margin.Occurrences)||physical.Rule!=rule||physical.Underlying!=future||
                margin.Delivery?.Terms is not GasPhysicalDeliveryTerms gas)
                throw Error("The option continuation requires its exact selected model, underlying, rule and saved margin/delivery policy.");
            deliveryMarket??=physical.Asset.Snapshot.HasSameInputs(source)&&gas.Mark.Curve.Revision==source.Definitions?
                new(gas,model,source,physical.Carrying):null;
            if(deliveryMarket is null||deliveryMarket.Terms!=gas||!deliveryMarket.Source.HasSameInputs(physical.Asset.Snapshot)||
                !ReferenceEquals(deliveryMarket.Discounting,physical.Carrying))
                throw new MarketDataException("projection_delivery_source","Retain the option's exact selected delivery model, original source and carrying provider.");
        }
        else if(deliveryMarket is not null)throw Error("A delivery market requires an explicitly selected physical option model.");
        this.deliveryMarket=deliveryMarket;
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
        if(physical is not null&&(future.Market!=underlyingMarket||expiryMarket!=underlyingMarket||marginMarket!=underlyingMarket||margin.SettlementCurve!=underlyingMarket.Curve))
            throw Error("The selected physical option stopping law requires the exact calibrated reference request for future, expiry and margin.");
        _=rule.Decide(option.Series.Terms.Right,option.Series.Terms.Strike,option.Series.Terms.Strike);
        if(instruction is not null&&(!option.Series.ContraryInstructionsAllowed||instruction.EventId==Guid.Empty||
            instruction.Instruction is not("exercise" or "abandon")||!TradingInstant.IsSupported(instruction.EffectiveAt)||
            !TradingInstant.IsSupported(instruction.KnownAt)||instruction.SourceContextId!=knowledge.ContextId||
            instruction.EffectiveAt>option.Series.InstructionCutoffAt||instruction.KnownAt>knowledge.KnownAt))
            throw Error("A contrary instruction requires its exact captured native event and eligible cutoff.");
        _=future.Instrument.ValidateMarginPolicy(future,margin,model,source);
        if(expiry<margin.From||expiry>=margin.Until)throw Error("The selected linked-future margin policy must cover its generated execution instant.");
        if(physical is not null)ProjectedFuturesDelivery.Require(future.Instrument,margin,model,FutureSetup(),source,null,deliveryMarket);
        Opening=new([ValuationPortfolio.FromTrades([new(trade.Id,trade.BookId,[holding],trade.Booking)],setup)],[]);
        expiryId=LifecycleProjectionEvent.ScopedId(Owner,ContractIdentity.FromCanonical($"option_expiry_v1:{option.Id:D}"));
        instructionId=instruction is {} selectedInstruction&&selectedInstruction.EffectiveAt>source.ValuationAt?
            LifecycleProjectionEvent.ScopedId(Owner,selectedInstruction.EventId):null;
        var selected=margin.Occurrences.Where(o=>o.At>expiry||o.At==expiry&&margin.Opening==FuturesMarginOpening.IncludeOpeningInstant)
            .OrderBy(o=>o.At).ThenBy(o=>o.Phase).ToArray();
        marginOccurrences=selected.ToImmutableDictionary(o=>LifecycleProjectionEvent.ScopedId(Owner,o.Id));
        if(known is not null)RequireKnown(known);
        knownInstructions=physical is null||known is null?[]:[..known.Events.Where(e=>e.Kind is "exercise_instruction" or "abandon_instruction")];
        instructionEvents=knownInstructions.Where(e=>e.EffectiveAt>source.ValuationAt).ToImmutableDictionary(e=>LifecycleProjectionEvent.ScopedId(Owner,e.EventId));
        if(physical is not null&&known is not null)
        {
            if(instruction is not null&&!knownInstructions.Any(e=>e.EventId==instruction.EventId&&e.EffectiveAt==instruction.EffectiveAt&&e.KnownAt==instruction.KnownAt&&
                e.Kind==(instruction.Instruction=="exercise"?"exercise_instruction":"abandon_instruction")))
                throw Error("The selected instruction must belong to this complete native option history.");
            instructionId=null;
        }
        if(physical is not null&&physical.Terminal!=TerminalAt(source.ValuationAt))
            throw Error("The selected option value must retain the native instruction active at its opening clock.");
        var native=known?.Events.Where(e=>e.EffectiveAt>source.ValuationAt&&e.Kind is "exercise" or "assignment" or "expiry").ToArray()??[];
        actualEvents=native.ToImmutableDictionary(e=>LifecycleProjectionEvent.ScopedId(Owner,e.EventId));
        var knownExpiry=native.Any(e=>e.Kind=="expiry");
        nativeClosedAt=physical is null?null:native.Where(e=>e.RemainingLots==default).OrderBy(e=>e.PublicationOrder).Select(e=>(DateTimeOffset?)e.EffectiveAt).FirstOrDefault();
        var hypotheticalExpiry=!knownExpiry&&nativeClosedAt is null;
        var events=actualEvents.Select(p=>new LifecycleProjectionEvent(p.Key,p.Value.EffectiveAt,
            [..actualEvents.Where(prior=>prior.Value.PublicationOrder<p.Value.PublicationOrder||prior.Value.PublicationOrder==p.Value.PublicationOrder&&
                prior.Value.EffectiveAt<p.Value.EffectiveAt).Select(prior=>prior.Key)],"known_option_event")).ToList();
        foreach(var entry in instructionEvents)
            events.Add(new(entry.Key,entry.Value.EffectiveAt,[..instructionEvents.Where(p=>p.Value.EffectiveAt==entry.Value.EffectiveAt&&p.Value.PublicationOrder<entry.Value.PublicationOrder)
                .Select(p=>p.Key)],"known_option_instruction"));
        if(instructionId is {} knownId)events.Add(new(knownId,instruction!.EffectiveAt,[],"known_option_instruction"));
        if(hypotheticalExpiry)
        {
            events.Add(new(expiryId,expiry,[..actualEvents.Keys,..instructionEvents.Keys,..instructionId is {} prior?new[]{prior}:[]],"option_expiry"));
            events.AddRange(selected.Select((o,n)=>new LifecycleProjectionEvent(LifecycleProjectionEvent.ScopedId(Owner,o.Id),o.At,
                [n==0?expiryId:LifecycleProjectionEvent.ScopedId(Owner,selected[n-1].Id)],o.Phase==FuturesMarginPhase.Final?"final_margin":"daily_margin")));
        }
        var children=ImmutableArray.CreateBuilder<FuturesLifecycleProjection>();
        var childEvents=ImmutableDictionary.CreateBuilder<Guid,(FuturesLifecycleProjection,LifecycleProjectionEvent)>();
        if(physical is not null&&native.Any(e=>e.Future is not null)&&nativeContinuations is null)
            throw Error("Prepare independent native children explicitly for the selected physical option model.");
        if(nativeContinuations is not null)
        {
            if(physical is null||known is null||futureHistories is{Count:>0}||
                !native.Where(e=>e.Future is not null).Select(e=>e.EventId).ToHashSet().SetEquals(nativeContinuations.Keys))
                throw Error("Select exactly the future-effective native children of this physical option, with independent ownership.");
            foreach(var accepted in native.Where(e=>e.Future is not null))
            {
                var child=nativeContinuations[accepted.EventId];var linked=accepted.Future!;
                var prerequisite=LifecycleProjectionEvent.ScopedId(Owner,accepted.EventId);
                if(child is null||child.Owner!=new AccountingPositionKey(linked.Trade.Id,linked.BindingId,linked.Trade.BookId)||child.Currency!=Currency||
                    child.NativeOptionEvent!=accepted||child.NativeOptionParent!=known||child.Activation is not{} activation||
                    !activation.Requires.SequenceEqual([prerequisite])||!child.Opening.Residuals.IsEmpty||!child.Opening.Cash.IsEmpty||
                    child.SourceSnapshot.Knowledge!=source.Knowledge||child.SourceSnapshot.ValuationAt!=source.ValuationAt||
                    child.SourceSnapshot.MarketCutoff!=source.MarketCutoff||child.SourceSnapshot.Definitions.CatalogId!=source.Definitions.CatalogId)
                    throw Error("Retain each exact native child, independent opening and certified parent activation without transferring its wealth to the option.");
            }
            ExternalNativeFutures=[..native.Where(e=>e.Future is not null).OrderBy(e=>e.PublicationOrder).ThenBy(e=>e.EventId).Select(e=>nativeContinuations[e.EventId])];
        }
        if(futureHistories is null)futureHistories=ImmutableDictionary<Guid,FuturesKnownHistory>.Empty;
        if(nativeContinuations is null&&!native.Where(e=>e.Future is not null).Select(e=>e.EventId).ToHashSet().SetEquals(futureHistories.Keys))
            throw Error("Prepare exactly every accepted future-linked execution and its complete knowledge-only margin history.");
        foreach(var accepted in native.Where(e=>e.Future is not null&&nativeContinuations is null))
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
        Dependencies=[..new[]{calibration.Underlying,future.Market,expiryMarket,marginMarket}.Concat(ProjectedFuturesDelivery.Dependencies(margin,deliveryMarket))
            .Concat(this.children.SelectMany(c=>c.Dependencies)).Distinct()];
        InputFinals=[..hypotheticalExpiry?new[]{future.Market,marginMarket}.Distinct().Select(m=>new LifecycleInputFinal(m,
            margin.Occurrences.Single(o=>o.Phase==FuturesMarginPhase.Final).At,null)):[],..this.children.SelectMany(c=>c.InputFinals)];
    }
    public ImmutableArray<LifecycleMarketUse> MarketUsesFor(DateTimeOffset horizon)
    {
        if(nativeClosedAt is {} closed&&closed<=horizon)return [..Dependencies.Select(m=>new LifecycleMarketUse(m,SourceSnapshot.ValuationAt))];
        var expiry=option.Series.Terms.ExpiryAt<horizon?option.Series.Terms.ExpiryAt:horizon;
        var final=margin.Occurrences.Single(o=>o.Phase==FuturesMarginPhase.Final).At;var through=final<horizon?final:horizon;
        return [..new[]{new LifecycleMarketUse(underlyingMarket,expiry),new(expiryMarket,expiry),new(future.Market,through),new(marginMarket,through)}
            .Concat(ProjectedFuturesDelivery.Dependencies(margin,deliveryMarket).Select(m=>new LifecycleMarketUse(m,horizon)))
            .Concat(children.SelectMany(c=>c.MarketUsesFor(horizon))).GroupBy(u=>u.Market).Select(g=>new LifecycleMarketUse(g.Key,g.Max(u=>u.Through)))];
    }
    public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)
    {
        foreach(var child in children)_=child.EventsFor(horizon);
        if((physical is null?instruction?.Instruction!="abandon":TerminalAt(option.Series.Terms.ExpiryAt)!=PhysicalOptionTerminal.Abandon)&&
            future.SettlementMethod!="cash"&&Events.Any(e=>e.At<=horizon&&e.Kind=="final_margin"))
            ProjectedFuturesDelivery.Require(future.Instrument,margin,model,FutureSetup(),SourceSnapshot,null,deliveryMarket);
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
                ..remaining==default?Array.Empty<ValuationPortfolio>():new[]{OptionPortfolio(remaining,physical is null?null:TerminalAt(occurrence.At))}]};
        }
        if(physical is not null&&(instructionEvents.ContainsKey(occurrence.Id)||occurrence.Id==instructionId))
        {
            var right=state.Residuals.SingleOrDefault(p=>p.Positions.Any(p=>p.Owner==Owner.Owner&&p.BindingId==holding.Id&&p.ValuationId==option.Id));
            if(right is null)return state;
            var remaining=right.Positions.Single(p=>p.Owner==Owner.Owner&&p.BindingId==holding.Id).Quantity.Amount;
            return state with{Residuals=[..state.Residuals.Where(p=>!ReferenceEquals(p,right)),OptionPortfolio(remaining,TerminalAt(occurrence.At))]};
        }
        if(occurrence.Id==instructionId)return state;
        if(occurrence.Id==expiryId)
        {
            var active=state.Residuals.SingleOrDefault(p=>p.Positions.Any(p=>p.Owner==Owner.Owner&&p.BindingId==holding.Id&&p.ValuationId==option.Id));
            if(active is null)return state;
            var remaining=active.Positions.Single(p=>p.Owner==Owner.Owner&&p.BindingId==holding.Id).Quantity.Amount;
            var fixing=context.Fix(new(expiryMarket,ObservationRole.DailySettlement,occurrence.At));
            var terminal=physical is null?(instruction is null?PhysicalOptionTerminal.Automatic:instruction.Instruction=="exercise"?PhysicalOptionTerminal.Exercise:PhysicalOptionTerminal.Abandon):TerminalAt(occurrence.At);
            var decision=terminal==PhysicalOptionTerminal.Automatic?rule.Decide(option.Series.Terms.Right,option.Series.Terms.Strike,fixing.Price.ToUnitPriceExact().Value):
                terminal==PhysicalOptionTerminal.Exercise?"exercised":"abandoned";
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
            FuturePortfolio(fact.Price,selected.At,selected.Phase==FuturesMarginPhase.Final,prior.Quantity.Amount.DivideExact(future.Instrument.LotSize.Amount),
                activeFuture.Setup.FuturesValueBindings.GetValueOrDefault(new(trade.Id,holding.Id))?.Advance(selected.Id));
        return state with{Residuals=[..state.Residuals.Where(p=>!ReferenceEquals(p,activeFuture)),residual],
            Cash=cash.Amount==default?state.Cash:state.Cash.Add(new(occurrence.Id,occurrence.At,selected.DueAt,ExactMoney.FromMoney(cash)))};
    }
    private ValuationPortfolio FuturePortfolio(ExactUnitPrice basis,DateTimeOffset at,bool final,ExactDecimal lots,FuturesMarginValueBinding? funding=null)
    {
        var holding=FuturesExecution.Bind(this.holding.Id,future,lots,new(option.Series.Terms.Strike,future.Quote),option.Series.Terms.ExpiryAt,FuturesSettlementState.Unprocessed);
        var knowledge=SourceSnapshot.Knowledge!;
        var state=new ProjectedFuturesMarginState(trade.Id,holding.Id,trade.BookId,holding.Quantity,future.Series.Key,basis,at,final,knowledge.ContextId,knowledge.KnownAt);
        var projected=FutureSetup(holding,funding) with{FuturesBindings=setup.FuturesBindings.SetItem(future.Instrument.Id,future),
            BindingStates=setup.BindingStates.SetItem(new(trade.Id,holding.Id),state)};
        return ValuationPortfolio.FromTrades([new(trade.Id,trade.BookId,[holding],trade.Booking)],projected);
    }
    private ValuationSetup FutureSetup(TradeInstrument? projectedHolding=null,FuturesMarginValueBinding? funding=null)
    {
        var result=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(future.Instrument.Id,future),null){
            InstrumentDiscounting=ImmutableDictionary<Guid,ISideDiscounting>.Empty.Add(future.Instrument.Id,physical?.MarginDiscounting??setup.OptionBindings[option.Id].Discounting)};
        if(physical is null)return result;
        projectedHolding??=FuturesExecution.Bind(holding.Id,future,holding.Quantity.Amount*(option.Series.Terms.Right==OptionRight.Call?ExactDecimal.Parse("1"):ExactDecimal.Parse("-1")),
            new(option.Series.Terms.Strike,future.Quote),option.Series.Terms.ExpiryAt,FuturesSettlementState.Unprocessed);
        var projectedTrade=new Trade(trade.Id,trade.BookId,[projectedHolding],trade.Booking);
        funding??=new(future.Instrument.RetainKnownMarginTerms(projectedTrade,projectedHolding.Id,margin,model,SourceSnapshot),[],FuturesMarginValueBinding.FlatReference){
            Physical=new(FuturesPhysicalBasisValueBinding.Version,physical.MarkInterpretation,margin.Delivery!.ProfileId,margin.Delivery.ProfileRevision,physical.Asset,physical.Carrying)};
        return result with{FuturesValueBindings=ImmutableDictionary<MeasureBinding,FuturesMarginValueBinding>.Empty.Add(new(trade.Id,holding.Id),funding)};
    }
    private PhysicalOptionTerminal TerminalAt(DateTimeOffset at)
    {
        var active=knownInstructions.Where(e=>e.EffectiveAt<=at).MaxBy(e=>e.PublicationOrder);
        if(active is not null)return active.Kind=="exercise_instruction"?PhysicalOptionTerminal.Exercise:PhysicalOptionTerminal.Abandon;
        return instruction is {} selected&&selected.EffectiveAt<=at?(selected.Instruction=="exercise"?PhysicalOptionTerminal.Exercise:PhysicalOptionTerminal.Abandon):PhysicalOptionTerminal.Automatic;
    }
    private ValuationPortfolio OptionPortfolio(ExactDecimal lots,PhysicalOptionTerminal? terminal=null)=>ValuationPortfolio.FromTrades([new(trade.Id,trade.BookId,
        [new(holding.Id,option,new(lots,holding.Quantity.Specification))],trade.Booking)],physical is null||terminal is null?setup:
            setup with{PhysicalOptionValueBindings=setup.PhysicalOptionValueBindings.SetItem(new(trade.Id,holding.Id),physical with{Terminal=terminal.Value})});
    private void RequireKnown(FuturesOptionKnownHistory known)
    {
        if(known.Owner!=Owner||known.Series!=option.Series.Key||known.TradeRevision<1||known.SourceContextId!=SourceSnapshot.Knowledge!.ContextId||
            known.KnownThrough!=SourceSnapshot.Knowledge.KnownAt||known.Events.IsDefault||known.OriginalLots==default||!known.OriginalLots.IsMultipleOf(ExactDecimal.Parse("1"))||
            known.OriginalLots.CompareTo(default)!=holding.Quantity.Amount.CompareTo(default)||known.Events.Select(e=>e.EventId).Distinct().Count()!=known.Events.Length)
            throw Error("Retain complete native option events under this exact owner, series and certified source.");
        var remaining=known.OriginalLots.CompareTo(default)<0?-known.OriginalLots:known.OriginalLots;
        var atOpening=remaining;
        DateTimeOffset? previous=null;
        foreach(var e in known.Events.OrderBy(e=>e.PublicationOrder).ThenBy(e=>e.EffectiveAt))
        {
            if(e.EventId==Guid.Empty||!TradingInstant.IsSupported(e.EffectiveAt)||e.BusinessAt!=e.EffectiveAt||!TradingInstant.IsSupported(e.KnownAt)||
                e.KnownAt>known.KnownThrough||e.PublicationOrder<1||previous is {} prior&&e.EffectiveAt<prior||
                e.Kind is not("exercise" or "assignment" or "expiry" or "exercise_instruction" or "abandon_instruction"))
                throw Error("Every accepted option event requires exact native effect, publication and action terms.");
            previous=e.EffectiveAt;
            if(e.Kind is "exercise" or "assignment")
            {
                if(e.Lots is not {} lots||lots.CompareTo(default)<=0||!lots.IsMultipleOf(ExactDecimal.Parse("1"))||lots.CompareTo(remaining)>0||
                    e.Future is null||e.EffectiveAt>=option.Series.Terms.ExpiryAt)throw Error("Accepted exercise requires exact closed lots and its native linked future.");
                remaining-=lots;
            }
            else if(e.Kind=="expiry")
            {
                if(e.EffectiveAt!=option.Series.Terms.ExpiryAt||e.Lots!=remaining||e.Outcome is not("exercised" or "abandoned")||
                    (e.Outcome=="exercised")!=(e.Future is not null))throw Error("Accepted expiry retains its actual outcome and linked future without re-evaluation.");
                remaining=default;
            }
            else if(e.Lots is not null||e.Future is not null||!option.Series.ContraryInstructionsAllowed||e.EffectiveAt>option.Series.InstructionCutoffAt)
                throw Error("Accepted instructions require their exact eligible native event without an exercise or linked future.");
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
