using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class CashOptionBindingChecks
{
    internal static void Run()
    {
        var f=new FuturesOptionChecks.OptionMarket();var series=f.Underlying with{SettlementMethod="cash"};var model=f.Model(underlying:series);
        var source=f.Snapshot().WithKnowledge(Guid.NewGuid(),FuturesOptionChecks.OptionMarket.At);var at=source.ValuationAt;
        var option=f.Option(13);var a=f.UnderlyingRequest;var build=model.Build(source,[a,f.SettlementRequest(13)]);
        var numerical=OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein) with{Steps=1};
        var one=new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted);
        var reference=FuturesOptionPricing.Prepare(option,build,source,one,FuturesOptionPricing.DailySettlement,numerical,true,default);
        var future=model.BindFutures(Guid.NewGuid(),a,"cash",FuturesMarketBinding.Implementation);
        var margin=new FuturesMarginPolicy(Guid.NewGuid(),1,a.Contract,a.Curve,at.AddDays(-1),series.LastTradingAt.AddDays(1),
            [new(Guid.NewGuid(),at.AddDays(1),at.AddDays(3),FuturesMarginPhase.Daily),new(Guid.NewGuid(),series.LastTradingAt,series.LastTradingAt.AddDays(2),FuturesMarginPhase.Final)],
            new(option.Currency,D("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal),FuturesMargin.HeldBindingRounding,
            FuturesMarginOpening.AfterOpeningInstant,FuturesMarginPolicy.Version);
        var policy=new CashFuturesOptionPolicy(CashFuturesOptionPolicy.Version,CashFuturesOptionPolicy.ReferenceStopping,
            CashFuturesOptionPolicy.ReferenceCalibration,CashFuturesOptionPolicy.IntrinsicStrategy,FuturesMarginValueBinding.FlatReference);
        var rule=new FuturesOptionAutomaticRule(f.R(option.Series.AutomaticExerciseRule),1,"settlement_moneyness_v1",D("0"),"abandon","abandon");
        var selected=new FuturesOptionCashValueBinding(policy,rule,PhysicalOptionTerminal.Automatic,margin,
            new ExplicitDiscounting(D("0.95"),DiscountFactorSource.Explicit),future);
        var first=FuturesOptionChecks.OptionMarket.Holding(91,1,option,"2");
        var second=FuturesOptionChecks.OptionMarket.Holding(92,1,option,"-1");var vanilla=FuturesOptionChecks.OptionMarket.Holding(93,1,option,"1");
        MeasureBinding Key(Trade trade)=>new(trade.Id,trade.Instruments.Single().Id);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null){
            OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(option.Id,reference),
            CashOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionCashValueBinding>.Empty.Add(Key(first),selected).Add(Key(second),selected)};
        var portfolio=ValuationPortfolio.FromTrades([first,second,vanilla],setup);
        if(portfolio.Scopes.Length!=3||portfolio.Scopes.Any(s=>s.BindingState is not null)||portfolio.Scopes.Count(s=>s.PricingBinding is not null)!=2)
            throw new Exception("Cash funding selections must retain distinct held scopes without accepted states.");
        var prepared=RiskEngine.Prepare(model,source,[portfolio]);
        var values=RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings).Groups.Single().BasePositions.ToDictionary(p=>p.TradeId);
        Equal(values[first.Id].Value,"19000");Equal(values[second.Id].Value,"-9500");Equal(values[vanilla.Id].Value,"10000");
        if(values[first.Id].Components.Length!=3||values[first.Id].Components.Aggregate(ExactMoney.Parse("0",option.Currency),(sum,c)=>sum.Add(c.Value))!=values[first.Id].Value)
            throw new Exception("Cash funding components must sum to the once-rounded value without creating cash.");
        if(portfolio.Declare(source).Count(d=>d.StaticDerivatives is not null)!=2)
            throw new Exception("Unqualified static derivatives must remain explicit for the selected cash strategy.");
        Reject("risk_static_derivatives_unavailable",()=>RiskEngine.CalculateExact(prepared,[f.FuturesBump],RiskEngine.ExactNumericalPolicy));
        var child=RiskEngine.WithShifts(prepared,[new(f.FuturesBump.Input,f.FuturesBump.Quote,f.FuturesBump.Unit,D("0.1"),QuoteShift.Convention)]);
        Equal(RiskEngine.CalculateExact(child,[],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings).Groups.Single().BasePositions.Single(p=>p.TradeId==first.Id).Value,"20900");
        var v2=ValuationPortfolio.FromTrades([first],setup with{CashOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionCashValueBinding>.Empty
            .Add(Key(first),selected with{Margin=margin with{Implementation=FuturesMarginPolicy.DeliveryVersion}})});
        Equal(RiskEngine.CalculateExact(RiskEngine.Prepare(model,source,[v2]),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"19000");
        var inception=margin with{Opening=FuturesMarginOpening.IncludeOpeningInstant,Occurrences=[margin.Occurrences[0] with{At=at,DueAt=at},margin.Occurrences[1]]};
        var immediate=ValuationPortfolio.FromTrades([first],setup with{CashOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionCashValueBinding>.Empty
            .Add(Key(first),selected with{Margin=inception})});
        if(immediate.Declare(source).SelectMany(d=>d.DiscountInputs).Any(d=>d.Payment.PaymentAt<=at))
            throw new Exception("A due inception VM requested an unavailable discount instead of explicit nominal treatment.");
        Equal(RiskEngine.CalculateExact(RiskEngine.Prepare(model,source,[immediate]),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,"20000");
        var filtered=setup.ForPosition(new(first.Id,first.Instruments.Single().Id,first.BookId),option.Id);
        if(filtered.CashOptionValueBindings.Count!=1||!filtered.CashOptionValueBindings.ContainsKey(Key(first)))throw new Exception("A funding map leaked another held selection.");
        void Bad(FuturesOptionCashValueBinding choice)=>Reject("cash_option_binding",()=>RiskEngine.Prepare(model,source,[ValuationPortfolio.FromTrades([first],setup with{
            CashOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionCashValueBinding>.Empty.Add(Key(first),choice)})]));
        Bad(selected with{Underlying=null!});Bad(selected with{Underlying=new(future.Id,future.Market,future.Contract,future.Series,future.Quote,
            future.SettlementMethod,future.Convention,future.Instrument,at.AddMinutes(1))});
        Bad(selected with{Rule=rule with{Key=f.R("foreign_rule")}});Bad(selected with{MarginDiscounting=null!});
        Bad(selected with{Margin=margin with{Implementation="unsupported"}});
        Bad(selected with{Margin=margin with{Implementation=FuturesMarginPolicy.DeliveryVersion,Delivery=new(Guid.NewGuid(),1,null!)}});
        var missingMargin=setup with{CashOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionCashValueBinding>.Empty.Add(Key(first),selected with{Margin=null!})};
        Reject("projection_option_state",()=>new FuturesOptionLifecycleProjection(first,first.Instruments.Single().Id,missingMargin,future,margin,rule,null,a,a,model,source));
        Reject("cash_option_binding",()=>ValuationPortfolio.FromTrades([vanilla],setup));
        Solved();Economic();HeldRightDailyBoundary();
        Console.WriteLine("PASS selected cash option binding: exact real scopes, signed nonunit funded PV, named shocks and explicit static restrictions");
        void Equal(ExactMoney actual,string expected){if(actual!=ExactMoney.Parse(expected,option.Currency))throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
        void Solved()
        {
            var atm=f.Option(11);var basis=model.Build(source,[a,f.SettlementRequest(11)]);
            var calibration=FuturesOptionPricing.Prepare(atm,basis,source,one,FuturesOptionPricing.DailySettlement,numerical,false,default);
            var auto=FuturesOptionChecks.OptionMarket.Holding(94,1,atm,"1");var abandon=FuturesOptionChecks.OptionMarket.Holding(95,1,atm,"1");
            var choices=setup with{OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(atm.Id,calibration),
                CashOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionCashValueBinding>.Empty.Add(Key(auto),selected)
                    .Add(Key(abandon),selected with{Terminal=PhysicalOptionTerminal.Abandon})};
            var held=ValuationPortfolio.FromTrades([auto,abandon],choices);
            var result=RiskEngine.CalculateExact(RiskEngine.Prepare(model,source,[held]),[],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings).Groups.Single();
            var volatility=((SolvedVolatility)calibration.Calibration).Volatility;
            var up=(volatility*AmericanFuturesOptionModel.YearFraction(at,atm.Series.Terms.ExpiryAt,numerical).Sqrt()).Exp();
            var p=(L("1")-L("1")/up)/(up-L("1")/up);
            var expected=ExactMoney.FromMoney(new((p*(L("3")*up-L("3"))*L("0.95")).ToExact(),atm.Currency)).Scale(atm.Series.Multiplier);
            if(held.Scopes.Length!=2||result.BasePositions.Single(p=>p.TradeId==auto.Id).Value!=expected||
                result.BasePositions.Single(p=>p.TradeId==abandon.Id).Value!=ExactMoney.Parse("0",atm.Currency)||calibration.TreePrices!=2)
                throw new Exception("Same-policy cash holdings reused another terminal decision or omitted reference tree work.");
        }
        void Economic()
        {
            var reset=FuturesOptionChecks.OptionMarket.Expiry.AddHours(1);
            var schedule=margin with{Occurrences=[margin.Occurrences[0] with{At=reset,DueAt=reset.AddDays(1)},margin.Occurrences[1]]};
            var curve=new DatedFunding(source,option.Currency,[..schedule.Occurrences.Select(o=>(o.DueAt,"0.95"))]);
            var choice=selected with{Margin=schedule,MarginDiscounting=curve};
            var selectedSetup=setup with{CashOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionCashValueBinding>.Empty.Add(Key(first),choice)};
            var component=new FuturesOptionLifecycleProjection(first,first.Instruments.Single().Id,selectedSetup,future,schedule,rule,null,a,a,model,source);
            var policy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,
                LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
            foreach(var h in new[]{reset.AddMinutes(-30),reset.AddMinutes(30)})
            {
                var end=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_cash_option_close",1),f.R("calendar"),[new(DateOnly.FromDateTime(h.UtcDateTime),h,0)]);
                var result=LifecycleProjection.Prepare(model,source,end,policy,[component],[],ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(option.Currency,curve)).Evaluate("funded",[]);
                var value=result.Values.Single();Equal(value.OpeningValue,"19000");Equal(value.ResidualValue,"19000");Equal(value.PeriodCash,"0");Equal(value.EconomicPnl,"0");
                if(result.States.Single().Residuals.SelectMany(p=>p.Setup.FuturesValueBindings.Values).Any(binding=>binding.Physical is not null))
                    throw new Exception("The cash strategy invented a physical-delivery binding.");
            }
        }
        void HeldRightDailyBoundary()
        {
            var curve=new DatedFunding(source,option.Currency,[(margin.Occurrences[0].DueAt,"0.95"),(margin.Occurrences[1].DueAt,"0.8")]);
            var choice=selected with{MarginDiscounting=curve};
            var heldSetup=setup with{CashOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionCashValueBinding>.Empty.Add(Key(first),choice)};
            var component=new FuturesOptionLifecycleProjection(first,first.Instruments.Single().Id,heldSetup,future,margin,rule,null,a,a,model,source);
            var policy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,
                LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
            // No accepted early exercise exists. After the first daily event,
            // a still-held intrinsic right selects the next candidate-stop due.
            // The 3,000 difference is model/time/held-decision P&L, never cash.
            foreach(var h in new[]{margin.Occurrences[0].At.AddHours(1),FuturesOptionChecks.OptionMarket.Expiry.AddTicks(-10),FuturesOptionChecks.OptionMarket.Expiry,FuturesOptionChecks.OptionMarket.Expiry.AddHours(1),margin.Occurrences[1].At.AddHours(1)})
            {
                var end=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_held_right",1),f.R("calendar"),[new(DateOnly.FromDateTime(h.UtcDateTime),h,0)]);
                var result=LifecycleProjection.Prepare(model,source,end,policy,[component],[],ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(option.Currency,curve)).Evaluate("held_right",[]);
                var value=result.Values.Single();Equal(value.OpeningValue,"19000");Equal(value.ResidualValue,"16000");Equal(value.PeriodCash,"0");Equal(value.EconomicPnl,"-3000");
                if(h<FuturesOptionChecks.OptionMarket.Expiry&&result.States.Single().Residuals.Single().Setup.BindingStates.Count!=0)
                    throw new Exception("A reference model's early stopping decision became a fabricated exercise.");
            }
        }
    }
    private sealed class DatedFunding(MarketSnapshot original,ReferenceKey currency,ImmutableArray<(DateTimeOffset At,string Factor)> dues):IProjectionCurrencyCurve
    {
        public ReferenceKey Currency=>currency;
        public MarketSnapshot SourceSnapshot=>original;
        public ProjectionCurrencyCurveKey CurveIdentity{get;}=new(new(original.Definitions,"synthetic_cash_funding"),Guid.NewGuid(),Guid.NewGuid(),1);
        public string TimingPolicy=>RateDiscountTiming.Version;
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> keys,MarketSnapshot selected)
        {
            if(!selected.HasSameInputs(original)||keys.Any(p=>dues.Count(d=>d.At==p.PaymentAt)!=1))throw new Exception("Funding escaped its saved due/source set.");
            return [..keys.Select(p=>new SidePaymentFactor(p,D(dues.Single(d=>d.At==p.PaymentAt).Factor),original,DiscountFactorSource.Explicit))];
        }
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> keys,MarketSnapshot source,DateTimeOffset at,string? policy)=>Resolve(keys,source);
    }
    private static ExactDecimal D(string value)=>ExactDecimal.Parse(value);
    private static LatticeDecimal L(string value)=>LatticeDecimal.Parse(value);
    private static void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
