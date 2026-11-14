using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;

internal static class LifecycleProjectionChecks
{
    internal static void Run()
    {
        var f=new Fixture();var source=f.Snapshot();source=source.WithKnowledge(Guid.NewGuid(),source.ValuationAt);
        var owner=new AccountingPositionKey(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        var policy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Linear,
            LifecycleProjectionPolicy.PayAtDue,LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice);
        var horizon=new LifecycleProjectionHorizon(Guid.NewGuid(),1,f.R("calendar"),
            [new(new(2040,12,18),source.ValuationAt.AddHours(8),0),new(new(2040,12,19),source.ValuationAt.AddDays(1),0)]);
        var request=new CurveRequest(f.C("local_gas"),f.K(3));
        var component=new FixAndCash(owner,f.R("currency"),request,source.ValuationAt.AddHours(4));
        var prepared=LifecycleProjection.Prepare(f.Model(),source,horizon,policy,[component],[]);
        var factor=prepared.Factors.Single(factor=>factor.Input.Series==f.S(1));
        var result=prepared.Evaluate("path",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]);
        // Opening price is 3 + .1 + .2. Half of a +2 terminal future move
        // is fixed at hour four. That cash must remain 4.3 at hour eight.
        Equal(result.Values.Single().PeriodCash,"4.3");
        Equal(result.Values.Single().EconomicPnl,"4.3");
        if(result.Fixings.Length!=1||result.Fixings[0].Price.ToExactString()!="4.3")throw new Exception("A path-local fixing did not freeze at its event.");
        if(source.Selections.Single(s=>s.Input==factor.Input).Observation.Value.ToString()!="3")throw new Exception("Projection changed an actual source observation.");
        var hold=LifecycleProjection.Prepare(f.Model(),source,horizon,policy with{PricePath=LifecycleProjectionPolicy.Hold},[component],[]);
        Equal(hold.Evaluate("hold",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]).Values.Single().PeriodCash,"5.3");
        var outstanding=LifecycleProjection.Prepare(f.Model(),source,horizon,policy with{CashDisposition=LifecycleProjectionPolicy.Outstanding},[component],[]);
        var due=outstanding.Evaluate("unpaid",[]).Values.Single();Equal(due.PeriodCash,"0");Equal(due.ResidualValue,"3.3");
        var rational=LifecycleProjection.Prepare(f.Model(),source,horizon,policy with{CashDisposition=LifecycleProjectionPolicy.Outstanding},
            [component with{ExactAmount=ExactMoney.Parse("1/3",component.Currency)}],[]);
        Equal(rational.Evaluate("rational_cash",[]).Values.Single().ResidualValue,"1/3");
        var curves=ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(component.Currency,new Curve(source,component.Currency));
        var accumulated=LifecycleProjection.Prepare(f.Model(),source,horizon,policy with{CashAccumulation=LifecycleProjectionPolicy.Accumulated},[component],[],curves);
        Equal(accumulated.Evaluate("accumulated",[]).Values.Single().PeriodCash,"3.7125");
        var future=LifecycleProjection.Prepare(f.Model(),source,horizon,policy,[component with{DueAt=source.ValuationAt.AddHours(12)}],[],curves);
        var futureValue=future.Evaluate("future_cash",[]).Values.Single();Equal(futureValue.PeriodCash,"0");Equal(futureValue.ResidualValue,"2.8875");
        var native=LifecycleProjection.Prepare(f.Model(),source,horizon,policy with{CashDisposition=LifecycleProjectionPolicy.Outstanding},
            [component with{Owner=new(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),owner.BindingId,owner.BookId)}],[]);
        var nativeValue=native.Evaluate("native",[]).Values.Single();Equal(nativeValue.ResidualValue,"3.3");
        if(nativeValue.Owner.TradeId!=Guid.Empty||nativeValue.Owner.Owner.Kind!=AccountingOwnerKind.NativeHolding)
            throw new Exception("A native owned cash continuation manufactured a trade identity.");
        ScopeRefusals(f,source,horizon,policy,component,prepared);
        var prerequisite=component.Events[0] with{At=source.ValuationAt};
        var dependent=component.Events[0] with{Id=Guid.NewGuid(),Requires=[prerequisite.Id]};
        try{LifecycleProjection.Prepare(f.Model(),source,horizon,policy,[component with{EventSelection=[prerequisite,dependent]}],[]);
            throw new Exception("An elapsed date was treated as a committed event prerequisite.");}
        catch(MarketDataException error)when(error.Code=="projection_events"){}
        Futures(f,source,horizon,policy);
        FinalFreeze(f,policy);
        Economic(f,prepared,component,horizon,policy);
        Console.WriteLine("PASS lifecycle projection: elapsed primitive path, rebuilt derived curve and path-local fixing freeze");
    }
    private static void FinalFreeze(Fixture f,LifecycleProjectionPolicy policy)
    {
        var model=f.Model();var source=f.Snapshot() with{ValuationAt=Fixture.Utc("2040-12-27T12:00:00Z"),MarketCutoff=Fixture.Utc("2040-12-27T12:00:00Z")};
        source=source.WithKnowledge(Guid.NewGuid(),source.ValuationAt);
        var market=model.BindFutures(Guid.NewGuid(),new(f.C("future"),f.K(1)),"cash","contract_lot_execution_pnl_v2",Fixture.Utc("2040-01-01T00:00:00Z"));
        var binding=Guid.NewGuid();var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[FuturesExecution.Bind(binding,market,ExactDecimal.Parse("2"),
            new(ExactDecimal.Parse("3"),market.Quote),source.ValuationAt.AddDays(-1),FuturesSettlementState.Unprocessed)]);
        var holding=trade.Instruments.Single();var state=new FuturesMarginState(trade.Id,binding,trade.BookId,holding.Quantity,market.Series.Key,
            ExactUnitPrice.FromPrice(new(ExactDecimal.Parse("3"),market.Quote)),source.ValuationAt.AddDays(-1),source.ValuationAt,
            FuturesMarginState.Version,FuturesMarginStatus.Open,null,source.Knowledge!.ContextId,source.Knowledge.KnownAt,1,1);
        var margin=new FuturesMarginPolicy(Guid.NewGuid(),1,market.Contract.Key,f.C("future"),source.ValuationAt.AddYears(-1),source.ValuationAt.AddYears(1),
            [new(Guid.NewGuid(),market.Contract.Expiry,market.Contract.Expiry,FuturesMarginPhase.Final)],
            new(market.Quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal),FuturesMargin.HeldBindingRounding,
            FuturesMarginOpening.AfterOpeningInstant,FuturesMarginPolicy.Version);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(market.Instrument.Id,market),null){
            BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),state)};
        var history=new FuturesMarginHistory(market.Instrument.ProposeMarginTerms(trade,binding,margin,model,source),source.Knowledge.ContextId,source.Knowledge.KnownAt,[]);
        var end=source.ValuationAt.AddDays(1);var horizon=new LifecycleProjectionHorizon(Guid.NewGuid(),1,f.R("calendar"),[new(new(2040,12,28),end,0)]);
        var derived=new FixAndCash(new(Guid.NewGuid(),Guid.NewGuid(),trade.BookId),market.Quote.Currency,new(f.C("local_gas"),f.K(3)),end);
        var prepared=LifecycleProjection.Prepare(model,source,horizon,policy,[new FuturesLifecycleProjection(trade,binding,state,history,setup,market.Market,model,source),derived],[]);
        var factor=prepared.Factors.Single(factor=>factor.Input.Series==f.S(1));
        var result=prepared.Evaluate("final_freeze",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]);
        Equal(result.Values[0].ResidualValue,"0");Equal(result.Values[0].PeriodCash,"80");
        Equal(result.Values[1].PeriodCash,"4.3");
    }
    private static void Economic(Fixture f,PreparedLifecycleProjection prepared,FixAndCash component,LifecycleProjectionHorizon horizon,LifecycleProjectionPolicy policy)
    {
        var dates=Enumerable.Range(0,5).Select(n=>new DateOnly(2040,12,13).AddDays(n)).ToImmutableArray();
        var calendar=new RiskHistoryCalendar(f.R("calendar"),dates[0],dates[^1].AddDays(1),dates);
        var clock=FactorHistory.CaptureClock(calendar,f.R("utc"),PowerClock.Capture(TimeZoneInfo.Utc),"16:00:00","reject","reject");
        var source=prepared.SourceSnapshot;
        var forecast=new RiskHistoryForecast(source.Knowledge!.ContextId,source.Definitions.CatalogId,source.ValuationAt,source.MarketCutoff,
            source.Knowledge.KnownAt,100,[prepared.Market]);
        var rows=ImmutableArray.CreateBuilder<RiskHistoryObservation>();
        foreach(var factor in prepared.Factors)
        {
        for(var n=0;n<dates.Length;n++)
        {
            var value=factor.Input.Series==f.S(1)?new[]{"5","3","2","3","5"}[n]:factor.BaseValue.ToString();
            rows.Add(new(new(new(source.Definitions.CatalogId,Guid.NewGuid()),factor.Input.Series,factor.Source,factor.Role,
                PublicationState.Final,clock.ResolvedCloses[n].Instant,ExactDecimal.Parse(value)),factor.Quote,factor.Unit,factor.Contract,
                null,n+1,clock.ResolvedCloses[n].Instant.AddMinutes(1)));
        }
        rows.Add(new(source.Selections.Single(s=>s.Input==factor.Input).Observation,factor.Quote,factor.Unit,factor.Contract,
            null,100,source.Knowledge.KnownAt));
        }
        var history=FactorHistory.Align(forecast,new(RiskHistoryPolicy.Version1,CurveModel.NumericalPolicy,RiskHistoryPolicy.Raw,
            RiskHistoryPolicy.Heads,RiskHistoryPolicy.ExactCloses,RiskHistoryPolicy.Absolute,RiskHistoryPolicy.Complete,RiskHistoryPolicy.Fixed,2),
            calendar,clock,[..prepared.Factors.Select(f=>new RiskHistoryFactor(f,PublicationState.Final))],rows.ToImmutable());
        var p=new LifecycleEconomicRiskPolicy(LifecycleEconomicRiskPolicy.Historical,"0.75",HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail);
        var historical=LifecycleEconomicRisk.Historical([prepared],history,p);
        if(historical.Economic.Single().ValueAtRisk!="-2.8"||historical.Economic.Single().ExpectedShortfall!="-2.3"||
            historical.MarketOnlyDiagnostic.Single().ValueAtRisk!="0.5"||historical.MarketOnlyDiagnostic.Single().ExpectedShortfall!="1")
            throw new Exception("Economic loss must include no-shock drift; market-only risk is a separate explicitly labelled diagnostic.");
        var mc=MonteCarloRiskChecks.Policy(32) with{Convention=MonteCarloRiskPolicy.Economic,ValuationPolicy=MonteCarloRiskPolicy.EconomicValuation,Confidence="0.75"};
        var simulation=LifecycleEconomicRisk.MonteCarlo([prepared],history,p with{Implementation=LifecycleEconomicRiskPolicy.MonteCarlo},mc);
        if(simulation.Samples!=32||ExactMoney.Parse(simulation.Economic.Single().ValueAtRisk,component.Currency).Add(ExactMoney.Parse("3.3",component.Currency))
            .CompareTo(ExactMoney.Parse(simulation.MarketOnlyDiagnostic.Single().ValueAtRisk,component.Currency))!=0)
            throw new Exception("Monte Carlo did not fully revalue every economic path or silently removed deterministic carry.");
        var failing=LifecycleProjection.Prepare(f.Model(),source,horizon,policy,[component with{FailOnDown=true}],[]);
        try{LifecycleEconomicRisk.Historical([failing],history,p);throw new Exception("A failed economic path was dropped or resampled.");}
        catch(MarketDataException error)when(error.Code=="synthetic_path_unavailable"){}
        try{LifecycleEconomicRisk.RequireParametricAvailable();throw new Exception("Unqualified event-aware parametric risk succeeded.");}
        catch(MarketDataException error)when(error.Code=="risk_economic_parametric_unavailable"){}
        Console.WriteLine("PASS economic risk: independent signed empirical tails, no-shock carry, separate market diagnostic, full MC paths and no failed-path omission");
    }
    private static void Futures(Fixture f,MarketSnapshot source,LifecycleProjectionHorizon horizon,LifecycleProjectionPolicy policy)
    {
        var model=f.Model();var market=model.BindFutures(Guid.NewGuid(),new(f.C("future"),f.K(1)),"cash","contract_lot_execution_pnl_v2",
            Fixture.Utc("2040-01-01T00:00:00Z"));
        var binding=Guid.NewGuid();var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[FuturesExecution.Bind(binding,market,ExactDecimal.Parse("2"),
            new(ExactDecimal.Parse("3"),market.Quote),source.ValuationAt.AddDays(-1),FuturesSettlementState.Unprocessed)]);
        var holding=trade.Instruments.Single();var state=new FuturesMarginState(trade.Id,binding,trade.BookId,holding.Quantity,market.Series.Key,
            ExactUnitPrice.FromPrice(new(ExactDecimal.Parse("3"),market.Quote)),source.ValuationAt.AddDays(-1),source.ValuationAt,
            FuturesMarginState.Version,FuturesMarginStatus.Open,null,source.Knowledge!.ContextId,source.Knowledge.KnownAt,1,1);
        var first=new FuturesMarginOccurrence(Guid.NewGuid(),source.ValuationAt.AddHours(4),source.ValuationAt.AddHours(4),FuturesMarginPhase.Daily);
        var margin=new FuturesMarginPolicy(Guid.NewGuid(),1,market.Contract.Key,f.C("future"),source.ValuationAt.AddYears(-1),source.ValuationAt.AddYears(1),
            [first,new(Guid.NewGuid(),market.Contract.Expiry,market.Contract.Expiry,FuturesMarginPhase.Final)],
            new(market.Quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal),FuturesMargin.HeldBindingRounding,
            FuturesMarginOpening.AfterOpeningInstant,FuturesMarginPolicy.Version);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(market.Instrument.Id,market),null){
            BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),state)};
        var history=new FuturesMarginHistory(market.Instrument.ProposeMarginTerms(trade,binding,margin,model,source),source.Knowledge.ContextId,source.Knowledge.KnownAt,[]);
        var missingPast=margin with{Occurrences=margin.Occurrences.SetItem(0,first with{At=source.ValuationAt.AddHours(-1),DueAt=source.ValuationAt})};
        var missingHistory=history with{Terms=history.Terms with{Policy=missingPast}};
        try{_ =new FuturesLifecycleProjection(trade,binding,state,missingHistory,setup,market.Market,model,source);
            throw new Exception("Missing actual opening margin was silently projected from its scheduled date.");}
        catch(MarketDataException error)when(error.Code=="projection_futures_state"){}
        var projection=LifecycleProjection.Prepare(model,source,horizon,policy,[new FuturesLifecycleProjection(trade,binding,state,history,setup,market.Market,model,source)],[]);
        var factor=projection.Factors.Single();var run=projection.Evaluate("future",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]);
        Equal(run.Values.Single().OpeningValue,"0");Equal(run.Values.Single().PeriodCash,"80");
        Equal(run.Values.Single().ResidualValue,"80");Equal(run.Values.Single().EconomicPnl,"160");
        var secondBinding=Guid.NewGuid();var secondTrade=new Trade(Guid.NewGuid(),trade.BookId,[FuturesExecution.Bind(secondBinding,market,ExactDecimal.Parse("2"),
            new(ExactDecimal.Parse("3"),market.Quote),source.ValuationAt.AddDays(-1),FuturesSettlementState.Unprocessed)]);
        var secondState=state with{TradeId=secondTrade.Id,BindingId=secondBinding};
        var secondSetup=setup with{BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(secondTrade.Id,secondBinding),secondState)};
        var secondHistory=new FuturesMarginHistory(market.Instrument.ProposeMarginTerms(secondTrade,secondBinding,margin,model,source),source.Knowledge.ContextId,source.Knowledge.KnownAt,[]);
        var shared=LifecycleProjection.Prepare(model,source,horizon,policy,
            [new FuturesLifecycleProjection(trade,binding,state,history,setup,market.Market,model,source),
             new FuturesLifecycleProjection(secondTrade,secondBinding,secondState,secondHistory,secondSetup,market.Market,model,source)],[]);
        var both=shared.Evaluate("shared_policy",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]);
        Equal(both.Values.Select(v=>v.EconomicPnl).Aggregate((a,b)=>a.Add(b)),"320");
        try{RiskEngine.Prepare(model,source,run.States.Single().Residuals);throw new Exception("A hypothetical margin basis entered actual valuation.");}
        catch(MarketDataException error)when(error.Code=="projection_futures_state"){}
    }
    private static void ScopeRefusals(Fixture f,MarketSnapshot source,LifecycleProjectionHorizon horizon,LifecycleProjectionPolicy policy,FixAndCash component,PreparedLifecycleProjection prepared)
    {
        var cash=new TimedCashPayment(component.Currency,source.ValuationAt.AddHours(-1));var binding=Guid.NewGuid();
        var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(binding,cash,new(ExactDecimal.Parse("1"),component.Currency))]);
        var state=new RecordedCashValuation(trade,binding,[],RecordedCashValuation.Policy,source.Knowledge!.ContextId,source.Knowledge.KnownAt);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null){
            BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),state)};
        var portfolio=ValuationPortfolio.FromTrades([trade],setup);
        var first=component with{Owner=new(trade.Id,binding,trade.BookId),OpeningSelection=[portfolio]};
        var second=component with{Owner=new(Guid.NewGuid(),Guid.NewGuid(),trade.BookId),OpeningSelection=[portfolio]};
        Reject("projection_scope",()=>LifecycleProjection.Prepare(f.Model(),source,horizon,policy,[first,second],[]));
        var late=LifecycleProjection.Prepare(f.Model(),source,horizon,policy,[first,second with{OpeningSelection=[],ResidualOnEvent=[portfolio]}],[]);
        Reject("projection_scope",()=>late.Evaluate("duplicate_residual",[]));
        var changed=source with{Id=Guid.NewGuid(),Selections=source.Selections.SetItem(0,source.Selections[0] with{
            Observation=source.Selections[0].Observation with{Value=ExactDecimal.Parse("4")}})};
        var inconsistent=LifecycleProjection.Prepare(f.Model(),changed,horizon,policy,[component with{Owner=second.Owner}],[]);
        Reject("risk_economic_history",()=>LifecycleEconomicRisk.Factors([prepared,inconsistent]));
    }
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
    private static void Equal(ExactMoney value,string expected)
    {if(value.CompareTo(ExactMoney.Parse(expected,value.Currency))!=0)throw new Exception($"Expected {expected}; got {value.ToExactString()}.");}
    private sealed record FixAndCash(AccountingPositionKey Owner,ReferenceKey Currency,CurveRequest Request,DateTimeOffset At):ILifecycleProjection
    {
        public bool FailOnDown{get;init;}
        public DateTimeOffset? DueAt{get;init;}
        public ImmutableArray<LifecycleProjectionEvent>? EventSelection{get;init;}
        public ImmutableArray<ValuationPortfolio>? OpeningSelection{get;init;}
        public ImmutableArray<ValuationPortfolio>? ResidualOnEvent{get;init;}
        public ExactMoney? ExactAmount{get;init;}
        public string Implementation=>"synthetic_fixing_cash_v1";
        public ImmutableArray<CurveRequest> Dependencies=>[Request];
        public LifecycleProjectionState Opening=>new(OpeningSelection??[],[]);
        public ImmutableArray<LifecycleProjectionEvent> Events=>EventSelection??[new(LifecycleProjectionEvent.ScopedId(Owner,Guid.Parse("11000000-0000-0000-0000-000000000001")),At,[],"fix")];
        public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
        {
            if(FailOnDown&&context.Price(Request).ToUnitPriceExact().Value.CompareTo(ExactDecimal.Parse("3.3"))<0)
                throw new MarketDataException("synthetic_path_unavailable","The synthetic path deliberately refuses this region.");
            var fixing=context.Fix(new(Request,ObservationRole.IndexFixing,occurrence.At));
            return state with{Residuals=ResidualOnEvent??state.Residuals,Cash=state.Cash.Add(new(occurrence.Id,occurrence.At,DueAt??occurrence.At,
                ExactAmount??fixing.Price.ExactTimes(new(ExactDecimal.Parse("1"),fixing.Price.Unit.Quantity))))};
        }
    }
    private sealed class Curve(MarketSnapshot Source,ReferenceKey CurrencyKey):IProjectionCurrencyCurve
    {
        public ReferenceKey Currency=>CurrencyKey;
        public MarketSnapshot SourceSnapshot=>Source;
        public ProjectionCurrencyCurveKey CurveIdentity{get;}=new(new(Source.Definitions,"synthetic_rates"),Guid.NewGuid(),Guid.NewGuid(),1);
        public string TimingPolicy=>RateDiscountTiming.Version;
        private DateTimeOffset opening=>Source.ValuationAt;
        private ExactDecimal Factor(DateTimeOffset at)=>ExactDecimal.Parse(at==opening.AddHours(4)?"0.9":at==opening.AddHours(8)?"0.8":"0.7");
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot source)=>
            [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt),source,DiscountFactorSource.Explicit))];
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> payments,MarketSnapshot source,DateTimeOffset at,string? policy)=>
            [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt).DivideExact(Factor(at)),source,DiscountFactorSource.Explicit))];
    }
}
