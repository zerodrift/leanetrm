using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class CashOptionFundingChecks
{
    internal static void Run()
    {
        var f=new FuturesOptionChecks.OptionMarket();var series=f.Underlying with{SettlementMethod="cash"};
        var model=f.Model(underlying:series);var source=f.Snapshot().WithKnowledge(Guid.NewGuid(),FuturesOptionChecks.OptionMarket.At);
        var option=f.Option(13);var held=FuturesOptionChecks.OptionMarket.Holding(41,1,option,"2");
        var one=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var requests=FuturesOptionPricing.Requests(model,option.Series,FuturesOptionPricing.DailySettlement);
        var calibration=FuturesOptionPricing.Prepare(option,model.Build(source,[requests.Underlying,requests.Settlement]),source,one,
            FuturesOptionPricing.DailySettlement,OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein),true,default);
        var optionSetup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null){
            OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(option.Id,calibration)};
        var vanilla=Value(ValuationPortfolio.FromTrades([held],optionSetup));
        var market=new FuturesMarketBinding(Guid.NewGuid(),f.UnderlyingRequest,model.ContractDefinition(f.K(1)),series,f.FuturesQuote,"cash",FuturesMarketBinding.Implementation);
        var binding=Guid.NewGuid();var holding=FuturesExecution.Bind(binding,market,ExactDecimal.Parse("2"),new(ExactDecimal.Parse("2"),f.FuturesQuote),
            source.ValuationAt,FuturesSettlementState.Unprocessed);
        var trade=new Trade(Guid.NewGuid(),held.BookId,[holding]);var key=new MeasureBinding(trade.Id,binding);
        var rounding=new CashRoundingConvention(option.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal);
        var margin=new FuturesMarginPolicy(Guid.NewGuid(),1,f.K(1),f.C("ng"),source.ValuationAt.AddDays(-1),series.LastTradingAt.AddDays(1),
            [new(Guid.NewGuid(),source.ValuationAt.AddDays(1),source.ValuationAt.AddDays(3),FuturesMarginPhase.Daily),
                new(Guid.NewGuid(),series.LastTradingAt,series.LastTradingAt.AddDays(2),FuturesMarginPhase.Final)],rounding,
            FuturesMargin.HeldBindingRounding,FuturesMarginOpening.AfterOpeningInstant,FuturesMarginPolicy.Version);
        var terms=market.Instrument.ProposeMarginTerms(trade,binding,margin,model,source);
        var state=new FuturesMarginState(trade.Id,binding,trade.BookId,holding.Quantity,series.Key,ExactUnitPrice.FromPrice(terms.ExecutionPrice),
            source.ValuationAt,source.ValuationAt,FuturesMarginState.Version,FuturesMarginStatus.Open,null,source.Knowledge!.ContextId,source.Knowledge.KnownAt,1,1);
        var selected=new FuturesMarginValueBinding(terms,[],FuturesMarginValueBinding.FlatReference);
        var futureSetup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(market.Instrument.Id,market),
            new ExplicitDiscounting(ExactDecimal.Parse("0.95"),DiscountFactorSource.Explicit)){
            BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(key,state),
            FuturesValueBindings=ImmutableDictionary<MeasureBinding,FuturesMarginValueBinding>.Empty.Add(key,selected)};
        var funded=Value(ValuationPortfolio.FromTrades([trade],futureSetup));
        if(vanilla!=ExactMoney.Parse("20000",option.Currency)||funded!=ExactMoney.Parse("19000",option.Currency)||
            funded.Subtract(vanilla)!=ExactMoney.Parse("-1000",option.Currency))
            throw new Exception("Two 10,000-unit intrinsic calls at A=3, K=2 differ from their .95-funded cash-future continuation by exactly 1,000 without a price or clock change.");
        Console.WriteLine("PASS cash-option funding boundary: unchanged vanilla diagnostic 20,000 and exact saved-due funded continuation 19,000 require an explicit option funding law");
        ExactMoney Value(ValuationPortfolio portfolio)=>RiskEngine.CalculateExact(RiskEngine.Prepare(model,source,[portfolio]),[],RiskEngine.ExactNumericalPolicy)
            .Groups.Single().BaseValues.Single().Value;
    }
}
