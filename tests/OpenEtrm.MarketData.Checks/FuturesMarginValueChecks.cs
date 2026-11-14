using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class FuturesMarginValueChecks
{
    internal static void Run()
    {
        var f=new Fixture();var at=f.Snapshot().ValuationAt;var quote=f.Quotes[0];var contract=f.Contracts[0];
        var series=new FuturesSeriesSpecification(f.R("future_jan"),quote.Quantity,quote.Currency,quote.Multiplier,quote.TradingTick,f.R("location"),f.R("uniform"),
            contract.DeliveryFrom,contract.DeliveryTo,contract.Expiry,quote.Quantity,ExactDecimal.Parse("40"),LotBasis.FixedTotal,Fixture.Utc("2040-01-01T00:00:00Z"),"cash");
        var market=new FuturesMarketBinding(Guid.NewGuid(),new(f.C("future"),contract.Key),contract,series,quote,"cash",FuturesMarketBinding.Implementation);
        var binding=Guid.NewGuid();var book=Guid.NewGuid();var holding=FuturesExecution.Bind(binding,market,ExactDecimal.Parse("1"),new(ExactDecimal.Parse("2"),quote),at,FuturesSettlementState.Unprocessed);
        var trade=new Trade(Guid.NewGuid(),book,[holding]);var snapshot=f.Snapshot().WithKnowledge(Guid.NewGuid(),at);
        var occurrence=new FuturesMarginOccurrence(Guid.NewGuid(),at,at.AddDays(2),FuturesMarginPhase.Daily);
        var final=new FuturesMarginOccurrence(Guid.NewGuid(),contract.Expiry,contract.Expiry.AddDays(2),FuturesMarginPhase.Final);
        var rounding=new CashRoundingConvention(quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal);
        var policy=new FuturesMarginPolicy(Guid.NewGuid(),1,contract.Key,f.C("future"),at.AddDays(-1),contract.Expiry.AddDays(1),[occurrence,final],rounding,
            FuturesMargin.HeldBindingRounding,FuturesMarginOpening.IncludeOpeningInstant,FuturesMarginPolicy.Version);
        var terms=market.Instrument.ProposeMarginTerms(trade,binding,policy,f.Model(),snapshot);
        var native=new FuturesMarginState(trade.Id,binding,book,holding.Quantity,series.Key,ExactUnitPrice.FromPrice(terms.ExecutionPrice),at,at,
            FuturesMarginState.Version,FuturesMarginStatus.Open,null,snapshot.Knowledge!.ContextId,at,1,1);
        var selected=new FuturesMarginValueBinding(terms,[],FuturesMarginValueBinding.FlatReference);
        var owner=new MeasureBinding(trade.Id,binding);
        ValuationSetup Setup(FuturesMarginValueBinding? value,IBindingValuationState? state=null,ISideDiscounting? discount=null)=>new(ValuationMeasure.PresentValue,
            ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(market.Instrument.Id,market),discount??new ExplicitDiscounting(ExactDecimal.Parse("0.95"),DiscountFactorSource.Explicit)){
            BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(owner,state??native),
            FuturesValueBindings=value is null?ImmutableDictionary<MeasureBinding,FuturesMarginValueBinding>.Empty:ImmutableDictionary<MeasureBinding,FuturesMarginValueBinding>.Empty.Add(owner,value)};
        ExactMoney Value(ValuationSetup setup,Trade? held=null)=>RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),snapshot,[ValuationPortfolio.FromTrades([held??trade],setup)]),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value;
        Equal(Value(Setup(null)),"40");Equal(Value(Setup(selected)),"38");
        Equal(Value(Setup(selected,discount:new ExplicitDiscounting(ExactDecimal.Parse("1.05"),DiscountFactorSource.Explicit))),"42");
        var shortTrade=new Trade(trade.Id,book,[FuturesExecution.Bind(binding,market,ExactDecimal.Parse("-1"),terms.ExecutionPrice,at,FuturesSettlementState.Unprocessed)]);
        var shortTerms=market.Instrument.ProposeMarginTerms(shortTrade,binding,policy,f.Model(),snapshot);
        Equal(Value(Setup(selected with{Terms=shortTerms},native with{Quantity=shortTerms.Quantity}),shortTrade),"-38");
        Reject("futures_margin_value",()=>Value(Setup(selected with{Implementation="unknown"})));
        Reject("futures_margin_value",()=>Value(Setup(selected with{Terms=terms with{Owner=new(Guid.NewGuid(),binding)}})));
        Reject("futures_margin_value",()=>Value(Setup(selected with{Terms=terms with{ExecutionPrice=new(ExactDecimal.Parse("1"),quote)}})));
        Reject("futures_margin_value",()=>Value(Setup(selected with{Terms=terms with{BookId=Guid.NewGuid()}})));
        Reject("futures_margin_value",()=>Value(Setup(selected with{Terms=terms with{Quantity=new(ExactDecimal.Parse("80"),quote.Quantity)}})));
        Reject("futures_margin_value",()=>Value(Setup(selected with{CompletedOccurrences=[occurrence.Id]})));
        var evidence=market.Instrument.ResolveSettlementFact(market,policy.SettlementCurve,f.Model(),snapshot,snapshot.Selections[0].Observation.Key,at,at,false);
        var accepted=native with{SettledBasis=evidence.Price,LatestSettlement=evidence};
        Equal(Value(Setup(selected with{CompletedOccurrences=[occurrence.Id]},accepted)),"0");
        var cash=new TimedCashPayment(quote.Currency,occurrence.DueAt);var cashBinding=Guid.NewGuid();
        var cashTrade=new Trade(trade.Id,book,[new(cashBinding,cash,new(ExactDecimal.Parse("40"),quote.Currency))]);
        var cashState=new RecordedCashValuation(cashTrade,cashBinding,[],RecordedCashValuation.Policy,snapshot.Knowledge.ContextId,at);
        var cashSetup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
            new ExplicitDiscounting(ExactDecimal.Parse("0.95"),DiscountFactorSource.Explicit)){
            BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,cashBinding),cashState)};
        var paid=RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),snapshot,[ValuationPortfolio.FromTrades([cashTrade],cashSetup)]),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value;
        Equal(paid,"38");
        var daily=occurrence with{At=at.AddDays(1),DueAt=at.AddDays(3)};
        var forwardPolicy=policy with{Occurrences=[daily,final]};var forwardTerms=terms with{Policy=forwardPolicy};
        var horizon=at.AddDays(2);var curve=new FundingCurve(snapshot,quote.Currency,horizon,daily.DueAt,final.DueAt);
        var forwardSetup=Setup(new(forwardTerms,[],FuturesMarginValueBinding.FlatReference),discount:curve);
        var component=new FuturesLifecycleProjection(trade,binding,native,new(forwardTerms,snapshot.Knowledge.ContextId,at,[]),
            forwardSetup,market.Market,f.Model(),snapshot);
        var h=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_funding_close",1),f.R("calendar"),[new(new(2040,12,20),horizon,0)]);
        var pathPolicy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Linear,LifecycleProjectionPolicy.Outstanding,
            LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
        var projection=LifecycleProjection.Prepare(f.Model(),snapshot,h,pathPolicy,[component],[],cashCurves:ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(quote.Currency,curve));
        var noShock=projection.Evaluate("funded_carry",[]).Values.Single();
        Equal(noShock.OpeningValue,"24");Equal(noShock.ResidualValue,"30");Equal(noShock.EconomicPnl,"6");
        var factor=projection.Factors.Single();
        var moved=projection.Evaluate("funded_linear",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("0.2"),QuoteShift.Convention)]).Values.Single();
        // Half the change fixes tomorrow: 44 cash *.6/.8=33 at H.
        // Remaining VM=(3.2-3.1)*40 *.4/.8=2. Opening is24.
        Equal(moved.ResidualValue,"35");Equal(moved.EconomicPnl,"11");
        Console.WriteLine("PASS explicit pending VM valuation: saved next due, same-clock native reset/cash conservation, negative-rate factors, strict terms and unchanged nominal diagnostic");
    }
    private sealed class FundingCurve(MarketSnapshot source,ReferenceKey currency,DateTimeOffset horizon,DateTimeOffset daily,DateTimeOffset final):IProjectionCurrencyCurve
    {
        public MarketSnapshot SourceSnapshot=>source;public ReferenceKey Currency=>currency;
        public string TimingPolicy=>RateDiscountTiming.Version;
        public ProjectionCurrencyCurveKey CurveIdentity{get;}=new(new(source.Definitions,"synthetic_funding"),Guid.NewGuid(),Guid.NewGuid(),1);
        private ExactDecimal Factor(DateTimeOffset at)=>ExactDecimal.Parse(at==horizon?"0.8":at==daily?"0.6":at==final?"0.4":"1");
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot selected)=>ResolveForValuation(payments,selected,source.ValuationAt,null);
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> payments,MarketSnapshot selected,DateTimeOffset at,string? clock)=>
            [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt).DivideExact(Factor(at)),selected,DiscountFactorSource.Explicit))];
    }
    private static void Equal(ExactMoney actual,string expected){if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
    private static void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
