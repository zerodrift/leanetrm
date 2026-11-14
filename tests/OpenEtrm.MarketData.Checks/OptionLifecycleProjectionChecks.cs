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
        Console.WriteLine("PASS option lifecycle: explicit automatic rule, exercise into margin-reset futures, abandoned branch and physical handoff gate");
    }
}
