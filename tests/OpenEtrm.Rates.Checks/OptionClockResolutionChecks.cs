using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class OptionClockResolutionChecks
{
    internal static int Run()
    {
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string code)=>new(reference,code);CurveKey C(string code)=>new(revision,code);
        var at=new DateTimeOffset(2031,1,1,0,0,0,TimeSpan.Zero);var horizon=at.AddDays(182);var expiry=at.AddDays(365);
        var futureKey=new ContractKey(revision,Guid.NewGuid());var optionKey=new ContractKey(revision,Guid.NewGuid());
        var futureSeries=new FuturesSeriesSpecification(R("future"),R("quantity"),R("usd"),D("1"),D("0.001"),R("location"),R("shape"),
            new(2032,2,1),new(2032,3,1),expiry.AddDays(5),R("quantity"),D("100"),LotBasis.FixedTotal,at.AddDays(-1),"physical");
        var option=new FuturesOptionInstrument(new(R("option"),R("option_product"),R("future"),
            new(OptionRight.Call,D("3"),expiry,"american","exercise_into_future"),D("100"),R("usd"),R("option_lot"),D("0.001"),
            "synthetic_automatic_rule",false,null,FuturesOptionInstrument.PremiumPaidUpFront));
        QuoteSpecification Quote(string code)=>new(R(code),QuoteKind.Outright,R("usd"),R("quantity"),D("1"),D("0.001"),D("0.001"),D("0"),D("100"));
        var futureQuote=Quote("future_quote");var optionQuote=Quote("option_quote");
        var underlyingSeries=new SeriesKey(revision,Guid.NewGuid());var settlementSeries=new SeriesKey(revision,Guid.NewGuid());
        var rateSeries=new[]{new SeriesKey(revision,Guid.NewGuid()),new SeriesKey(revision,Guid.NewGuid())};
        Curve Direct(string code,QuoteSpecification quote,ContractKey key)=>new(C(code),quote.Key,R("location"),R("shape"),new(SettlementKind.FutureSeries,null,null),
            CurveRule.Direct,[key],[],ObservationRole.DailySettlement,R("source"),null);
        var model=new CurveModel(revision,reference,
            [new(R("quantity"),R("asset"),R("basis"),R("unit"),R("energy")),new(R("option_lot"),R("asset"),R("contracts"),R("contract"),R("count"))],
            [futureQuote,optionQuote],[new(futureKey,ContractKind.Future,new(2032,2,1),new(2032,3,1),futureSeries.LastTradingAt,futureSeries.Key),
                new(optionKey,ContractKind.FutureOption,new(2032,2,1),new(2032,3,1),expiry,option.SeriesKey)],
            [Direct("future",futureQuote,futureKey),Direct("option",optionQuote,optionKey)],[futureSeries],
            [new(R("rate"),NumericKind.Dimensionless,null,null,D("-0.5"),D("1"))],
            [new(underlyingSeries,futureKey,futureQuote.Key,null),new(settlementSeries,optionKey,optionQuote.Key,null),..rateSeries.Select(s=>new MarketSeries(s,null,null,R("rate")))]);
        SelectedObservation Select(SeriesKey series,string value,ObservationRole role)=>new(new(series,InputUse.Variable),
            new(new(revision.CatalogId,Guid.NewGuid()),series,R("source"),role,PublicationState.Final,at,D(value)));
        var source=new MarketSnapshot(Guid.NewGuid(),revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [Select(underlyingSeries,"3",ObservationRole.DailySettlement),Select(settlementSeries,"0.3",ObservationRole.DailySettlement),
                Select(rateSeries[0],"-0.1",ObservationRole.Parameter),Select(rateSeries[1],"0",ObservationRole.Parameter)]);
        var definition=new RateCurveDefinition(C("rates"),R("usd"),R("source"),
            RateConventionSet.Parse("actual_365_fixed","simple","linear_zero","none","decimal_per_year"),
            [new("182D",rateSeries[0],DateOnly.FromDateTime(horizon.UtcDateTime)),new("365D",rateSeries[1],DateOnly.FromDateTime(expiry.UtcDateTime))],Guid.NewGuid(),1);
        var payment=new SidePaymentKey(option.Id,0,expiry,R("usd"));
        var provider=new CurrencyDefaultDiscounting([payment],source,true,new(Guid.NewGuid(),R("usd"),definition));
        var sourceFactor=provider.Resolve([payment],source).Single();
        var rolledFactor=provider.ResolveForValuation([payment],source,horizon,ClockChange.FrozenInputs).Single();
        // Independent rational curve oracle: D(0,H)=round18(1825/1734);
        // D(0,T)=1, so D(H,T)=round18(1/round18(1825/1734)).
        if(sourceFactor.DiscountFactor!=D("1")||rolledFactor.DiscountFactor!=D("0.950136986301369863")||
            rolledFactor.ClockRoll?.SourceFactor!=D("1")||!rolledFactor.Snapshot.HasSameInputs(source))throw new Exception("Unexpected retained nonflat rate curve.");
        var numerical=OptionNumericalPolicies.Require(OptionNumericalPolicies.LeisenReimer);
        var requests=FuturesOptionPricing.Requests(model,option.Series,FuturesOptionPricing.DailySettlement);
        var built=model.Build(source,[requests.Underlying,requests.Settlement]);
        var binding=FuturesOptionPricing.Prepare(option,built,source,provider,FuturesOptionPricing.DailySettlement,numerical,true,default);
        if(binding.Calibration is not SolvedVolatility solved)throw new Exception("This resolution case requires a real solved reference calibration.");
        var receipt=binding.Receipt;
        var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),option,new(D("1"),R("option_lot")))]);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null){
            OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(option.Id,binding)};
        var portfolio=ValuationPortfolio.FromTrades([trade],setup);
        var export=model.Compile(source,[requests.Underlying,requests.Settlement],false,built.Graph.Plan.Context);
        var bindings=export.Points.Where(p=>p.Key.Use==InputUse.Variable).ToImmutableDictionary(p=>p.Key.Request,p=>p.Value);
        var prepared=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(built.Graph,source),bindings,[portfolio],built);
        if(!prepared.DeclaredPriceSteps.IsEmpty)throw new Exception("The source LR tree at unit discount has no price step.");
        var rolled=RiskEngine.WithClock(prepared,new(horizon,ClockChange.FrozenInputs));
        var expected=AmericanFuturesOptionModel.PriceStep(option.Series.Terms,LatticeDecimal.Parse("3"),solved.Volatility,
            LatticeDecimal.From(rolledFactor.DiscountFactor),horizon,numerical)!.Value.ToExact();
        if(rolled.DeclaredPriceSteps.Count!=1||rolled.DeclaredPriceSteps.Single().Value!=expected||expected.CompareTo(default)<=0)
            throw new Exception("The rolled LR model lost its actual conditional-discount price step.");
        var value=RiskEngine.CalculateExact(rolled,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value;
        var pv=AmericanFuturesOptionModel.UnitPrice(option.Series.Terms,LatticeDecimal.Parse("3"),solved.Volatility,
            LatticeDecimal.From(rolledFactor.DiscountFactor),horizon,numerical,default).ToExact()*D("100");
        if(value!=ExactMoney.FromMoney(new(pv,R("usd")))||binding.Receipt!=receipt||!ReferenceEquals(rolled.Valuation.Snapshot,source)||!prepared.DeclaredPriceSteps.IsEmpty)
            throw new Exception("The actual rolled model resolution must retain original calibration, source and base metadata.");
        Reject("valuation_time_coverage",()=>RiskEngine.WithClock(prepared,new(expiry,ClockChange.FrozenInputs)));
        var changed=binding with{Discounting=new Changed(provider,f=>f with{Snapshot=source with{Id=Guid.NewGuid()}})};
        var changedPortfolio=ValuationPortfolio.FromTrades([trade],setup with{OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(option.Id,changed)});
        var changedPrepared=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(built.Graph,source),bindings,[changedPortfolio],built);
        Reject("risk_dependency",()=>RiskEngine.WithClock(changedPrepared,new(horizon,ClockChange.FrozenInputs)));
        var escape=new ResolutionEscape(Guid.NewGuid(),requests.Underlying,futureQuote,payment,at,expiry);
        var escaped=ValuationPortfolio.FromTrades([new(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),escape,new(D("1"),R("quantity")))])],
            new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        Reject("risk_dependency",()=>RiskEngine.PrepareConnected(ConnectedValuation.Prepare(built.Graph,source),bindings,[escaped],built));
        Console.WriteLine("PASS rolled LR resolution: independent nonflat DF1-to-discounted curve, actual conditional provider and unchanged source calibration");
        return 1;
    }
    private static ExactDecimal D(string text)=>ExactDecimal.Parse(text);
    private static void Reject(string code,Action run)
    {try{run();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
    private sealed class Changed(ISideDiscounting source,Func<SidePaymentFactor,SidePaymentFactor> change):ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> keys,MarketSnapshot snapshot)=>source.Resolve(keys,snapshot);
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> keys,MarketSnapshot snapshot,DateTimeOffset at,string? policy)=>
            [..source.ResolveForValuation(keys,snapshot,at,policy).Select(f=>at==snapshot.ValuationAt?f:change(f))];
    }
    private sealed record ResolutionEscape:Instrument
    {
        private readonly CurveRequest market;private readonly QuoteSpecification quote;private readonly SidePaymentKey payment;private readonly DateTimeOffset from,until;
        public ResolutionEscape(Guid id,CurveRequest market,QuoteSpecification quote,SidePaymentKey payment,DateTimeOffset from,DateTimeOffset until):base(id)
        {(this.market,this.quote,this.payment,this.from,this.until)=(market,quote,payment,from,until);}
        public override ReferenceKey Currency=>quote.Currency;
        public override Quantity UnitQuantity=>new(D("1"),quote.Quantity);
        public override bool SupportsQuantityScaling=>true;
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,"synthetic_resolution_fence_v1",[market],quote.Quantity,quote.Currency)
            {Time=ValuationTimeDependency.Between(from,until)};
        public override ImmutableArray<DeclaredPriceStep> ResolvePriceSteps(UnitValuationContext context,UnitPricingContract declaration)
        {_=context.Inputs.Discounts([payment]);return [];}
    }
}
