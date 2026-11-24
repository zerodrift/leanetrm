using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class OptionLifecycleProjectionChecks
{
    internal static void Run()
    {
        var f=new FuturesOptionChecks.OptionMarket();
        var rule=new FuturesOptionAutomaticRule(f.R("synthetic_auto_exercise_v1"),1,"settlement_moneyness_v1",ExactDecimal.Parse("0.01"),"abandon","exercise");
        void Outcome(OptionRight right,string settlement,string expected)
        {
            if(rule.Decide(right,ExactDecimal.Parse("3"),ExactDecimal.Parse(settlement))!=expected)throw new Exception("Automatic exercise differs from the independent threshold table.");
        }
        Outcome(OptionRight.Call,"3","abandoned");Outcome(OptionRight.Put,"3","exercised");
        Outcome(OptionRight.Call,"3.01","abandoned");Outcome(OptionRight.Call,"3.011","exercised");
        Outcome(OptionRight.Put,"2.99","abandoned");Outcome(OptionRight.Put,"2.989","exercised");
        var source=f.Snapshot().WithKnowledge(Guid.NewGuid(),FuturesOptionChecks.OptionMarket.At);
        var model=f.Model();var option=f.Option(13);var trade=FuturesOptionChecks.OptionMarket.Holding(20,1,option,"2");
        var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var requests=FuturesOptionPricing.Requests(model,option.Series,FuturesOptionPricing.DailySettlement);
        var basis=model.Build(source,[requests.Underlying,requests.Settlement]);
        var calibration=FuturesOptionPricing.Prepare(option,basis,source,discount,FuturesOptionPricing.DailySettlement,
            OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein),true,default);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null){
            OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(option.Id,calibration)};
        var future=new FuturesMarketBinding(Guid.NewGuid(),f.UnderlyingRequest,model.ContractDefinition(f.K(1)),f.Underlying,
            f.FuturesQuote,f.Underlying.SettlementMethod,FuturesMarketBinding.Implementation);
        var marginAt=FuturesOptionChecks.OptionMarket.Expiry.AddHours(1);
        var margin=new FuturesMarginPolicy(Guid.NewGuid(),1,f.K(1),f.C("ng"),source.ValuationAt,source.ValuationAt.AddYears(1),
            [new(Guid.NewGuid(),marginAt,marginAt,FuturesMarginPhase.Daily),new(Guid.NewGuid(),f.Underlying.LastTradingAt,f.Underlying.LastTradingAt,FuturesMarginPhase.Final)],
            new(option.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal),FuturesMargin.HeldBindingRounding,
            FuturesMarginOpening.AfterOpeningInstant,FuturesMarginPolicy.Version);
        var component=new FuturesOptionLifecycleProjection(trade,trade.Instruments[0].Id,setup,future,margin,rule,null,
            f.UnderlyingRequest,f.UnderlyingRequest,model,source);
        // The same holding with its series stated as European, calibrated by the formula: the projection is qualified
        // for American exercise only and refuses it by name before it prices anything.
        var european=f.EuropeanOption(13);var europeanTrade=FuturesOptionChecks.OptionMarket.Holding(21,1,european,"2");
        var europeanSetup=setup with{OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(european.Id,FuturesOptionPricing.Prepare(european,basis,source,
            discount,FuturesOptionPricing.DailySettlement,OptionNumericalPolicies.Require(OptionNumericalPolicies.Black76),true,default))};
        Reject("projection_option_style",()=>new FuturesOptionLifecycleProjection(europeanTrade,europeanTrade.Instruments[0].Id,europeanSetup,future,margin,rule,null,
            f.UnderlyingRequest,f.UnderlyingRequest,model,source));
        Console.WriteLine("PASS a European series is refused with projection_option_style");
        var policy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,
            LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
        var h=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(new(2040,12,24),marginAt.AddHours(1),0)]);
        var prepared=LifecycleProjection.Prepare(model,source,h,policy,[component],[]);
        var value=prepared.Evaluate("exercise",[]).Values.Single();
        void Equal(ExactMoney actual,string expected){if(actual.CompareTo(ExactMoney.Parse(expected,option.Currency))!=0)throw new Exception($"Expected {expected}; got {actual.ToExactString()}.");}
        // Two calls receive two 10,000-unit futures at strike 2. The next
        // settlement at 3 pays 20,000 once and resets residual futures to zero.
        Equal(value.OpeningValue,"20000");Equal(value.PeriodCash,"20000");Equal(value.ResidualValue,"0");Equal(value.EconomicPnl,"0");
        var factor=prepared.Factors.Single();
        var down=prepared.Evaluate("abandon",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("-2"),QuoteShift.Convention)]).Values.Single();
        Equal(down.ResidualValue,"0");Equal(down.PeriodCash,"0");Equal(down.EconomicPnl,"-20000");
        try{component.EventsFor(f.Underlying.LastTradingAt);throw new Exception("An exercised physical future lost its delivery transition.");}
        catch(MarketDataException e)when(e.Code=="futures_delivery_transition"){}
        var physicalTerms=new OpenEtrm.MarketData.Gas.GasPhysicalDeliveryTerms(Guid.NewGuid(),future.Series.Key,future.Quote.Quantity,option.Currency,future.Series.Location,
            f.Underlying.LastTradingAt,f.Underlying.LastTradingAt.AddDays(30),f.Underlying.LastTradingAt.AddDays(35),f.UnderlyingRequest,
            OpenEtrm.MarketData.Gas.GasHoldingCarrying.Nominal,OpenEtrm.MarketData.Gas.GasPhysicalDeliveryTerms.Version);
        var physicalMargin=margin with{Implementation=FuturesMarginPolicy.DeliveryVersion,Delivery=new(Guid.NewGuid(),1,physicalTerms)};
        var combinedSetup=setup with{InstrumentDiscounting=ImmutableDictionary<Guid,ISideDiscounting>.Empty.Add(option.Id,discount)
            .Add(Guid.NewGuid(),new ExplicitDiscounting(ExactDecimal.Parse("0.5"),DiscountFactorSource.Explicit))};
        var physical=new FuturesOptionLifecycleProjection(trade,trade.Instruments[0].Id,combinedSetup,future,physicalMargin,rule,null,
            f.UnderlyingRequest,f.UnderlyingRequest,model,source);
        var afterFinal=h with{Closes=[new(new(2040,12,28),f.Underlying.LastTradingAt.AddHours(1),0)]};
        // An A-only option calibration cannot silently select the contingent
        // delivery-basis model for its generated physical future.
        try{_=LifecycleProjection.Prepare(model,source,afterFinal,policy,[physical],[]);throw new Exception("A physical option projected full wealth without its selected continuation model.");}
        catch(MarketDataException e)when(e.Code=="futures_physical_value"){}
        var selectedPolicy=new PhysicalOptionPolicy(PhysicalOptionPolicy.Version,PhysicalOptionPolicy.FrozenBasis,PhysicalOptionPolicy.ReferenceStopping,
            PhysicalOptionPolicy.ReferenceCalibration,PhysicalOptionPolicy.IntrinsicStrategy);
        var selectedPhysical=new FuturesOptionPhysicalValueBinding(selectedPolicy,rule,PhysicalOptionTerminal.Automatic,physicalMargin,
            new(f.UnderlyingRequest,basis.Connected,basis.PricingBindings[f.UnderlyingRequest]),null,discount,FuturesPhysicalBasisValueBinding.SameDeliverable,future);
        var physicalSetup=setup with{PhysicalOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionPhysicalValueBinding>.Empty
            .Add(new(trade.Id,trade.Instruments[0].Id),selectedPhysical)};
        var selectedProjection=new FuturesOptionLifecycleProjection(trade,trade.Instruments[0].Id,physicalSetup,future,physicalMargin,rule,null,
            f.UnderlyingRequest,f.UnderlyingRequest,model,source);
        var selectedValue=LifecycleProjection.Prepare(model,source,afterFinal,policy,[selectedProjection],[]).Evaluate("selected_physical",[]).Values.Single();
        Equal(selectedValue.OpeningValue,"20000");Equal(selectedValue.PeriodCash,"20000");Equal(selectedValue.ResidualValue,"0");Equal(selectedValue.EconomicPnl,"0");
        var substitutedEligibility=new FuturesMarketBinding(future.Id,future.Market,future.Contract,future.Series,future.Quote,future.SettlementMethod,future.Convention,
            future.Instrument,future.EligibleFrom.AddDays(1));
        try{_=new FuturesOptionLifecycleProjection(trade,trade.Instruments[0].Id,physicalSetup,substitutedEligibility,physicalMargin,rule,null,
            f.UnderlyingRequest,f.UnderlyingRequest,model,source);throw new Exception("The selected option lost its exact qualified underlying eligibility.");}
        catch(MarketDataException e)when(e.Code=="projection_option_state"){}
        var official=new CurveRequest(f.C("official"),f.K(1));
        var officialModel=new CurveModel(f.Revision,f.Reference,
            [new(f.R("mmbtu"),f.R("gas"),f.R("energy_basis"),f.R("mmbtu_unit"),f.R("energy")),new(f.R("on_contract"),f.R("gas"),f.R("contract_basis"),f.R("contract_unit"),f.R("count"))],
            [f.FuturesQuote,f.OptionQuote],[model.ContractDefinition(f.K(1)),..FuturesOptionChecks.OptionMarket.Options.Select(o=>model.ContractDefinition(f.K(o.Contract)))],
            [model.AssetDefinition(f.C("ng")),model.AssetDefinition(f.C("on_settle")),model.AssetDefinition(f.C("ng")) with{Key=official.Curve}],
            [f.Underlying],[],[new(f.S(1),f.K(1),f.FuturesQuote.Key,null),..FuturesOptionChecks.OptionMarket.Options.Select(o=>new MarketSeries(f.S(o.Contract),f.K(o.Contract),f.OptionQuote.Key,null))]);
        var officialMargin=physicalMargin with{SettlementCurve=official.Curve};
        var officialSetup=physicalSetup with{PhysicalOptionValueBindings=physicalSetup.PhysicalOptionValueBindings.SetItem(new(trade.Id,trade.Instruments[0].Id),selectedPhysical with{Margin=officialMargin})};
        try{_=new FuturesOptionLifecycleProjection(trade,trade.Instruments[0].Id,officialSetup,future,officialMargin,rule,null,official,official,officialModel,source);
            throw new Exception("A separate official curve bypassed the selected option's exact A-trigger contract.");}
        catch(MarketDataException e)when(e.Code=="projection_option_state"){}
        var otm=f.Option(14);var otmTrade=FuturesOptionChecks.OptionMarket.Holding(24,1,otm,"2");
        var otmBasis=model.Build(source,[f.UnderlyingRequest,f.SettlementRequest(14)]);
        var otmReference=FuturesOptionPricing.Prepare(otm,otmBasis,source,discount,FuturesOptionPricing.DailySettlement,calibration.Policy,true,default);
        var otmSetup=physicalSetup with{OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(otm.Id,otmReference),
            PhysicalOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionPhysicalValueBinding>.Empty.Add(new(otmTrade.Id,otmTrade.Instruments[0].Id),selectedPhysical)};
        var firstInstruction=new KnownFuturesOptionEvent(Guid.NewGuid(),"abandon_instruction",null,ExactDecimal.Parse("2"),source.ValuationAt.AddHours(1),
            source.ValuationAt.AddHours(1),source.Knowledge!.KnownAt,1,null,null,null,null,null,null);
        var laterInstruction=firstInstruction with{EventId=Guid.NewGuid(),Kind="exercise_instruction",BusinessAt=source.ValuationAt.AddHours(3),
            EffectiveAt=source.ValuationAt.AddHours(3),PublicationOrder=2};
        var instructions=new FuturesOptionKnownHistory(new(otmTrade.Id,otmTrade.Instruments[0].Id,otmTrade.BookId),1,otm.SeriesKey,ExactDecimal.Parse("2"),
            source.Knowledge.ContextId,source.Knowledge.KnownAt,[firstInstruction,laterInstruction]);
        FuturesOptionLifecycleProjection InstructionProjection(FuturesOptionKnownHistory known)=>new(otmTrade,otmTrade.Instruments[0].Id,otmSetup,future,physicalMargin,rule,null,
            f.UnderlyingRequest,f.UnderlyingRequest,model,source,known);
        LifecycleProjectionResult AtInstruction(FuturesOptionKnownHistory known,int hours)=>LifecycleProjection.Prepare(model,source,
            h with{Closes=[new(new(2040,12,18),source.ValuationAt.AddHours(hours),0)]},policy,[InstructionProjection(known)],[]).Evaluate("native_instruction",[]);
        var between=AtInstruction(instructions,2);
        if(between.States.Single().Residuals.Single().Setup.PhysicalOptionValueBindings.Values.Single().Terminal!=PhysicalOptionTerminal.Abandon)
            throw new Exception("The surviving right skipped its first effective accepted instruction.");
        Equal(between.Values.Single().ResidualValue,"0");
        Equal(AtInstruction(instructions,4).Values.Single().ResidualValue,"-40000");
        var nativeHead=instructions with{Events=[firstInstruction with{PublicationOrder=3},laterInstruction with{
            BusinessAt=firstInstruction.BusinessAt,EffectiveAt=firstInstruction.EffectiveAt}]};
        Equal(AtInstruction(nativeHead,4).Values.Single().ResidualValue,"0"); // Native publication head governs a shared effective instant.
        try{_=InstructionProjection(instructions with{Events=[firstInstruction with{PublicationOrder=3},laterInstruction]});
            throw new Exception("A backdated native instruction bypassed the source reader's publication chronology.");}
        catch(MarketDataException e)when(e.Code=="projection_option_state"){}
        var abandonInstruction=new KnownFuturesOptionInstruction(Guid.NewGuid(),"abandon",source.ValuationAt.AddHours(1),source.Knowledge!.KnownAt,source.Knowledge.ContextId);
        var instructed=new FuturesOptionLifecycleProjection(trade,trade.Instruments[0].Id,setup,future,margin,rule,abandonInstruction,
            f.UnderlyingRequest,f.UnderlyingRequest,model,source);
        var instructedValue=LifecycleProjection.Prepare(model,source,h,policy,[instructed],[]).Evaluate("known_abandon",[]).Values.Single();
        Equal(instructedValue.ResidualValue,"0");Equal(instructedValue.PeriodCash,"0");Equal(instructedValue.EconomicPnl,"-20000");
        _=instructed.EventsFor(f.Underlying.LastTradingAt); // this known decision has no exercised physical branch
        // A source-known later exercise is a committed decision, even when a
        // hypothetical market path would make the remaining right worthless.
        var retrospective=source.WithKnowledge(source.Knowledge!.ContextId,marginAt.AddDays(1));
        var executed=source.ValuationAt.AddHours(1);var futureBinding=Guid.NewGuid();
        var actualFuture=new Trade(Guid.NewGuid(),trade.BookId,[FuturesExecution.Bind(futureBinding,future,ExactDecimal.Parse("1"),
            new(option.Series.Terms.Strike,f.FuturesQuote),executed,FuturesSettlementState.Unprocessed)]);
        var eventId=Guid.NewGuid();var knownEvent=new KnownFuturesOptionEvent(eventId,"exercise",ExactDecimal.Parse("1"),ExactDecimal.Parse("1"),
            executed,executed,retrospective.Knowledge!.KnownAt,1,null,null,null,null,null,
            new(actualFuture,futureBinding,margin,Guid.NewGuid(),1,executed,retrospective.Knowledge.KnownAt));
        var optionKnown=new FuturesOptionKnownHistory(new(trade.Id,trade.Instruments[0].Id,trade.BookId),1,option.Series.Key,ExactDecimal.Parse("2"),
            retrospective.Knowledge.ContextId,retrospective.Knowledge.KnownAt,[knownEvent]);
        var futureKnown=new FuturesKnownHistory(new(actualFuture.Id,futureBinding,trade.BookId),future.Instrument.RetainKnownMarginTerms(
            actualFuture,futureBinding,margin,model,retrospective),retrospective.Knowledge.ContextId,retrospective.Knowledge.KnownAt,[],[]);
        var replayed=new FuturesOptionLifecycleProjection(trade,trade.Instruments[0].Id,setup,future,margin,rule,null,
            f.UnderlyingRequest,f.UnderlyingRequest,model,retrospective,optionKnown,ImmutableDictionary<Guid,FuturesKnownHistory>.Empty.Add(eventId,futureKnown));
        // The native future is real, but this fixture supplies no delivery
        // profile/full valuation model for its surviving physical obligation.
        try{_=LifecycleProjection.Prepare(model,retrospective,h,policy,[replayed],[]);throw new Exception("A known physical child omitted its full continuation contract.");}
        catch(MarketDataException e)when(e.Code=="futures_delivery_transition"){}
        var physicalEvent=knownEvent with{Future=knownEvent.Future! with{MarginPolicy=physicalMargin}};
        var physicalKnown=optionKnown with{Events=[physicalEvent]};
        var captured=model.Build(retrospective,[requests.Underlying,requests.Settlement]);
        var nativeSelection=selectedPhysical with{Asset=new(f.UnderlyingRequest,captured.Connected,captured.PricingBindings[f.UnderlyingRequest])};
        var nativeSetup=physicalSetup with{PhysicalOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionPhysicalValueBinding>.Empty
            .Add(new(trade.Id,trade.Instruments[0].Id),nativeSelection)};
        var nativeTerms=future.Instrument.RetainKnownMarginTerms(actualFuture,futureBinding,physicalMargin,model,retrospective);
        var nativeHistory=futureKnown with{Terms=nativeTerms};
        var childSetup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(future.Instrument.Id,future),null){
            InstrumentDiscounting=ImmutableDictionary<Guid,ISideDiscounting>.Empty.Add(future.Instrument.Id,discount),
            FuturesValueBindings=ImmutableDictionary<MeasureBinding,FuturesMarginValueBinding>.Empty.Add(new(actualFuture.Id,futureBinding),
                new(nativeTerms,[],FuturesMarginValueBinding.FlatReference){Physical=new(FuturesPhysicalBasisValueBinding.Version,nativeSelection.MarkInterpretation,
                    physicalMargin.Delivery!.ProfileId,physicalMargin.Delivery.ProfileRevision,nativeSelection.Asset,null)})};
        var independentChild=new FuturesLifecycleProjection(physicalEvent,nativeHistory,childSetup,f.UnderlyingRequest,model,retrospective,parent:physicalKnown);
        var prerequisite=LifecycleProjectionEvent.ScopedId(physicalKnown.Owner,physicalEvent.EventId);
        if(!independentChild.Activation!.Requires.SequenceEqual([prerequisite]))
            throw new Exception("An independent native child has no exact accepted parent-event prerequisite.");
        Reject("projection_futures_state",()=>new FuturesLifecycleProjection(physicalEvent,nativeHistory,childSetup,f.UnderlyingRequest,model,retrospective,
            parent:physicalKnown with{SourceContextId=Guid.NewGuid()}));
        Reject("projection_futures_state",()=>new FuturesLifecycleProjection(physicalEvent,nativeHistory,childSetup,f.UnderlyingRequest,model,retrospective,
            parent:physicalKnown with{Owner=new(trade.Id,trade.Instruments[0].Id,Guid.NewGuid())}));
        Reject("projection_futures_state",()=>new FuturesLifecycleProjection(physicalEvent with{RemainingLots=ExactDecimal.Parse("2")},nativeHistory,childSetup,
            f.UnderlyingRequest,model,retrospective,parent:physicalKnown));
        Reject("projection_futures_state",()=>new FuturesLifecycleProjection(physicalEvent,nativeHistory,childSetup,f.UnderlyingRequest,model,retrospective,
            parent:physicalKnown with{Events=[physicalEvent with{Future=physicalEvent.Future! with{AdoptionContextId=Guid.NewGuid()}}]}));
        void BadChildren(ImmutableDictionary<Guid,FuturesLifecycleProjection>? selected)=>Reject("projection_option_state",()=>new FuturesOptionLifecycleProjection(
            trade,trade.Instruments[0].Id,nativeSetup,future,physicalMargin,rule,null,f.UnderlyingRequest,f.UnderlyingRequest,model,retrospective,physicalKnown,nativeContinuations:selected));
        BadChildren(null);BadChildren(ImmutableDictionary<Guid,FuturesLifecycleProjection>.Empty);
        BadChildren(ImmutableDictionary<Guid,FuturesLifecycleProjection>.Empty.Add(Guid.NewGuid(),independentChild));
        BadChildren(ImmutableDictionary<Guid,FuturesLifecycleProjection>.Empty.Add(eventId,
            new(physicalEvent,nativeHistory,childSetup,f.UnderlyingRequest,model,retrospective)));
        var external=new FuturesOptionLifecycleProjection(trade,trade.Instruments[0].Id,nativeSetup,future,physicalMargin,rule,null,
            f.UnderlyingRequest,f.UnderlyingRequest,model,retrospective,physicalKnown,nativeContinuations:ImmutableDictionary<Guid,FuturesLifecycleProjection>.Empty.Add(eventId,independentChild));
        if(!external.ExternalNativeFutures.SequenceEqual([independentChild])||external.Events.Any(e=>independentChild.Events.Contains(e)))
            throw new Exception("The option retained an independently owned child's events or lost its exact native child.");
        var separated=LifecycleProjection.Prepare(model,retrospective,h,policy,[external,independentChild],[]).Evaluate("independent_native_child",[]);
        Equal(separated.Values.Single(v=>v.Owner==external.Owner).PeriodCash,"10000");
        Equal(separated.Values.Single(v=>v.Owner==independentChild.Owner).PeriodCash,"10000");
        Equal(separated.Values.Select(v=>v.EconomicPnl).Aggregate((a,b)=>a.Add(b)),"0");
        var abandonedExpiry=new KnownFuturesOptionEvent(Guid.NewGuid(),"expiry",ExactDecimal.Parse("1"),default,option.Series.Terms.ExpiryAt,option.Series.Terms.ExpiryAt,
            retrospective.Knowledge.KnownAt,1,"abandoned",null,rule.Key,new(f.Revision.CatalogId,Guid.NewGuid()),ExactUnitPrice.FromPrice(new(ExactDecimal.Parse("1"),f.FuturesQuote)),null);
        FuturesOptionLifecycleProjection ClosedParent(KnownFuturesOptionEvent expiry)=>new(trade,trade.Instruments[0].Id,nativeSetup,future,physicalMargin,rule,null,
            f.UnderlyingRequest,f.UnderlyingRequest,model,retrospective,physicalKnown with{Events=[expiry]},nativeContinuations:ImmutableDictionary<Guid,FuturesLifecycleProjection>.Empty);
        try{_=ClosedParent(abandonedExpiry);throw new Exception("An accepted expiry closed fewer lots than the complete remaining native right.");}
        catch(MarketDataException e)when(e.Code=="projection_option_state"){}
        var closed=ClosedParent(abandonedExpiry with{Lots=ExactDecimal.Parse("2")});
        if(!closed.InputFinals.IsEmpty||closed.Events.Any(e=>e.Kind is "option_expiry" or "daily_margin" or "final_margin")||
            closed.MarketUsesFor(afterFinal.Closes.Single().Instant).Any(use=>use.Through!=retrospective.ValuationAt))
            throw new Exception("The fully closed parent still demands its unused current-profile market or hypothetical future.");
        var nativeAbandon=LifecycleProjection.Prepare(model,retrospective,afterFinal,policy,[closed],[]).Evaluate("accepted_abandon",[]);
        Equal(nativeAbandon.Values.Single().ResidualValue,"0");Equal(nativeAbandon.Values.Single().PeriodCash,"0");
        var shortTrade=FuturesOptionChecks.OptionMarket.Holding(22,1,option,"-1");
        var shortComponent=new FuturesOptionLifecycleProjection(shortTrade,shortTrade.Instruments[0].Id,setup,future,margin,rule,null,
            f.UnderlyingRequest,f.UnderlyingRequest,model,source);
        var shortValue=LifecycleProjection.Prepare(model,source,h,policy,[shortComponent],[]).Evaluate("short_call",[]).Values.Single();
        Equal(shortValue.OpeningValue,"-10000");Equal(shortValue.PeriodCash,"-10000");Equal(shortValue.EconomicPnl,"0");
        try
        {
            _=new FuturesOptionLifecycleProjection(trade,trade.Instruments[0].Id,setup,future,margin,rule,
                abandonInstruction with{SourceContextId=Guid.NewGuid()},f.UnderlyingRequest,f.UnderlyingRequest,model,source);
            throw new Exception("A contrary instruction from another source context was accepted.");
        }
        catch(MarketDataException e)when(e.Code=="projection_option_state"){}
        // A source-live option survives the instruction cutoff until expiry.
        // The projection clock must value its held IV after that cutoff.
        var solved=f.Option(11);var solvedTrade=FuturesOptionChecks.OptionMarket.Holding(21,1,solved,"1");
        var solvedBasis=model.Build(source,[f.UnderlyingRequest,f.SettlementRequest(11)]);
        var solvedCalibration=FuturesOptionPricing.Prepare(solved,solvedBasis,source,discount,FuturesOptionPricing.DailySettlement,
            OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein),true,default);
        var solvedSetup=setup with{OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(solved.Id,solvedCalibration)};
        var solvedComponent=new FuturesOptionLifecycleProjection(solvedTrade,solvedTrade.Instruments[0].Id,solvedSetup,future,margin,rule,null,
            f.UnderlyingRequest,f.UnderlyingRequest,model,source);
        var beforeExpiry=option.Series.Terms.ExpiryAt.AddHours(-1);
        var beforeH=h with{Closes=[new(new(2040,12,24),beforeExpiry,0)]};
        var rolled=LifecycleProjection.Prepare(model,source,beforeH,policy,[solvedComponent],[]).Evaluate("after_cutoff",[]);
        var iv=((SolvedVolatility)solvedCalibration.Calibration).Volatility;
        var oracle=AmericanFuturesOptionModel.UnitPrice(solved.Series.Terms,LatticeDecimal.Parse("3"),iv,LatticeDecimal.Parse("1"),beforeExpiry,
            OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein),default).ToExact();
        if(rolled.Values.Single().ResidualValue.CompareTo(ExactMoney.FromMoney(new(oracle,solved.Currency)).Scale(solved.Series.Multiplier))!=0)
            throw new Exception("The post-cutoff option did not retain its exact source volatility and new calculation time.");
        PhysicalOptionProjectionChecks.Run();
        Console.WriteLine("PASS option lifecycle: explicit automatic rule, exercise into margin-reset futures, abandoned branch and physical handoff gate");
    }
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
