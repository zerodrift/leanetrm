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
    public ImmutableArray<LifecycleProjectionEvent> Events{get;}
    private readonly Trade trade;
    private readonly TradeInstrument holding;
    private readonly FuturesOptionInstrument option;
    private readonly FuturesMarketBinding future;
    private readonly FuturesMarginPolicy margin;
    private readonly FuturesOptionAutomaticRule rule;
    private readonly KnownFuturesOptionInstruction? instruction;
    private readonly CurveRequest expiryMarket,marginMarket;
    private readonly ValuationSetup setup;
    private readonly Guid expiryId;
    private readonly Guid? instructionId;
    private readonly TradeInstrument futureHolding;
    private readonly ImmutableDictionary<Guid,FuturesMarginOccurrence> marginOccurrences;
    public FuturesOptionLifecycleProjection(Trade trade,Guid bindingId,ValuationSetup setup,FuturesMarketBinding future,
        FuturesMarginPolicy margin,FuturesOptionAutomaticRule rule,KnownFuturesOptionInstruction? instruction,
        CurveRequest expiryMarket,CurveRequest marginMarket,CurveModel model,MarketSnapshot source)
    {
        holding=trade.Instruments.SingleOrDefault(h=>h.Id==bindingId)??throw Error("Retain the exact native option holding.");
        option=holding.Instrument as FuturesOptionInstrument??throw Error("Select a listed futures option.");
        Owner=new(trade.Id,bindingId,trade.BookId);this.trade=trade;this.future=future;this.margin=margin;this.rule=rule;
        this.instruction=instruction;this.expiryMarket=expiryMarket;this.marginMarket=marginMarket;this.setup=setup;SourceSnapshot=source;
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
        _=rule.Decide(option.Series.Terms.Right,option.Series.Terms.Strike,option.Series.Terms.Strike);
        if(instruction is not null&&(!option.Series.ContraryInstructionsAllowed||instruction.EventId==Guid.Empty||
            instruction.Instruction is not("exercise" or "abandon")||!TradingInstant.IsSupported(instruction.EffectiveAt)||
            !TradingInstant.IsSupported(instruction.KnownAt)||instruction.SourceContextId!=knowledge.ContextId||
            instruction.EffectiveAt>option.Series.InstructionCutoffAt||instruction.KnownAt>knowledge.KnownAt))
            throw Error("A contrary instruction requires its exact captured native event and eligible cutoff.");
        _=future.Instrument.ValidateMarginPolicy(future,margin,model,source);
        if(expiry<margin.From||expiry>=margin.Until)throw Error("The selected linked-future margin policy must cover its generated execution instant.");
        var lots=holding.Quantity.Amount*(option.Series.Terms.Right==OptionRight.Call?ExactDecimal.Parse("1"):ExactDecimal.Parse("-1"));
        futureHolding=FuturesExecution.Bind(bindingId,future,lots,new(option.Series.Terms.Strike,future.Quote),expiry,FuturesSettlementState.Unprocessed);
        Opening=new([ValuationPortfolio.FromTrades([new(trade.Id,trade.BookId,[holding],trade.Booking)],setup)],[]);
        expiryId=LifecycleProjectionEvent.ScopedId(Owner,ContractIdentity.FromCanonical($"option_expiry_v1:{option.Id:D}"));
        instructionId=instruction is {} selectedInstruction&&selectedInstruction.EffectiveAt>source.ValuationAt?
            LifecycleProjectionEvent.ScopedId(Owner,selectedInstruction.EventId):null;
        var selected=margin.Occurrences.Where(o=>o.At>expiry||o.At==expiry&&margin.Opening==FuturesMarginOpening.IncludeOpeningInstant)
            .OrderBy(o=>o.At).ThenBy(o=>o.Phase).ToArray();
        marginOccurrences=selected.ToImmutableDictionary(o=>LifecycleProjectionEvent.ScopedId(Owner,o.Id));
        Events=[..instructionId is {} knownId?new[]{new LifecycleProjectionEvent(knownId,instruction!.EffectiveAt,[],"known_option_instruction")}:[],
            new(expiryId,expiry,instructionId is {} prior?[prior]:[],"option_expiry"),..selected.Select((o,n)=>new LifecycleProjectionEvent(
            LifecycleProjectionEvent.ScopedId(Owner,o.Id),o.At,[n==0?expiryId:LifecycleProjectionEvent.ScopedId(Owner,selected[n-1].Id)],
            o.Phase==FuturesMarginPhase.Final?"final_margin":"daily_margin"))];
        Dependencies=[..new[]{calibration.Underlying,future.Market,expiryMarket,marginMarket}.Distinct()];
    }
    public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)
    {
        if(instruction?.Instruction!="abandon"&&future.SettlementMethod!="cash"&&Events.Any(e=>e.At<=horizon&&e.Kind=="final_margin"))
            throw new MarketDataException("futures_delivery_transition","Prepare a qualified gas handoff for every possible exercised physical-future branch.");
        return [..Events.Where(e=>e.At<=horizon)];
    }
    public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
    {
        if(!Events.Contains(occurrence))throw Error("Select this exact declared option or generated-future event.");
        if(occurrence.Id==instructionId)return state;
        if(occurrence.Id==expiryId)
        {
            var fixing=context.Fix(new(expiryMarket,ObservationRole.DailySettlement,occurrence.At));
            var decision=instruction is {} known?(known.Instruction=="exercise"?"exercised":"abandoned"):
                rule.Decide(option.Series.Terms.Right,option.Series.Terms.Strike,fixing.Price.ToUnitPriceExact().Value);
            if(decision=="abandoned")return state with{Residuals=[]};
            return state with{Residuals=[FuturePortfolio(ExactUnitPrice.FromPrice(new(option.Series.Terms.Strike,future.Quote)),occurrence.At,false)]};
        }
        if(state.Residuals.IsEmpty)return state;
        var selected=marginOccurrences[occurrence.Id];
        if(selected.Phase==FuturesMarginPhase.Final&&future.SettlementMethod!="cash")
            throw new MarketDataException("futures_delivery_transition","A physical future needs its qualified gas delivery handoff.");
        var prior=(ProjectedFuturesMarginState)state.Residuals.Single().Setup.BindingStates[new(trade.Id,holding.Id)];
        var fact=context.Fix(new(marginMarket,selected.Phase==FuturesMarginPhase.Final?ObservationRole.FinalContractSettlement:ObservationRole.DailySettlement,selected.At));
        var(_,cash,_)=FuturesMargin.Difference(prior.Basis,fact.Price,futureHolding.Quantity,margin.Rounding,margin.RoundingScope);
        return state with{Residuals=[FuturePortfolio(fact.Price,selected.At,selected.Phase==FuturesMarginPhase.Final)],
            Cash=cash.Amount==default?state.Cash:state.Cash.Add(new(occurrence.Id,occurrence.At,selected.DueAt,ExactMoney.FromMoney(cash)))};
    }
    private ValuationPortfolio FuturePortfolio(ExactUnitPrice basis,DateTimeOffset at,bool final)
    {
        var knowledge=SourceSnapshot.Knowledge!;
        var state=new ProjectedFuturesMarginState(trade.Id,holding.Id,trade.BookId,futureHolding.Quantity,future.Series.Key,basis,at,final,knowledge.ContextId,knowledge.KnownAt);
        var projected=setup with{FuturesBindings=setup.FuturesBindings.SetItem(future.Instrument.Id,future),
            BindingStates=setup.BindingStates.SetItem(new(trade.Id,holding.Id),state)};
        return ValuationPortfolio.FromTrades([new(trade.Id,trade.BookId,[futureHolding],trade.Booking)],projected);
    }
    private static MarketDataException Error(string message)=>new("projection_option_state",message);
}
