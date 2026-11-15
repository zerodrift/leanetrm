using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class CashOptionPolicyChecks
{
    internal static void Run()
    {
        var model=new CashFuturesOptionPolicy(CashFuturesOptionPolicy.Version,CashFuturesOptionPolicy.ReferenceStopping,CashFuturesOptionPolicy.ReferenceCalibration,
            CashFuturesOptionPolicy.IntrinsicStrategy,FuturesMarginValueBinding.FlatReference);
        var selected=new FuturesOptionCashValuationSelection(Guid.NewGuid(),Guid.NewGuid(),model);
        var cash=new FuturesOptionCashValuationPolicy(FuturesOptionCashValuationPolicy.Version,[selected]);cash.Validate();
        var optionPolicy=FuturesOptionValuationPolicy.Release1 with{CashFunding=cash};_=optionPolicy.Validate();
        var retained=JsonSerializer.Deserialize<FuturesOptionValuationPolicy>(JsonSerializer.Serialize(optionPolicy,RiskJobs.Json),RiskJobs.Json)!;
        if(retained.Physical is not null||retained.CashFunding?.Implementation!=cash.Implementation||!retained.CashFunding.Options.SequenceEqual(cash.Options)||
            JsonSerializer.SerializeToElement(FuturesOptionValuationPolicy.Release1,RiskJobs.Json).EnumerateObject().Count()!=4)
            throw new Exception("The selected cash strategy must retain every exact model input while legacy option transport stays unchanged.");
        foreach(var invalid in new[]{cash with{Implementation=""},cash with{Options=default},cash with{Options=[selected,selected]},cash with{Options=[null!]},
            cash with{Options=[selected with{TradeId=Guid.Empty}]},cash with{Options=[selected with{BindingId=Guid.Empty}]},cash with{Options=[selected with{Model=null!}]}})
        {
            try{invalid.Validate();throw new Exception("An incomplete cash binding choice was accepted.");}
            catch(MarketDataException error)when(error.Code=="cash_option_policy"){}
        }
        var encoded=JsonSerializer.SerializeToNode(optionPolicy,RiskJobs.Json)!.AsObject();
        var saved=encoded["cashFunding"]!["options"]![0]!["model"]!.AsObject();saved["unselectedFallback"]=true;Closed(encoded);
        saved.Remove("unselectedFallback");saved.Remove("funding");Closed(encoded);
        var missing=JsonSerializer.SerializeToNode(optionPolicy,RiskJobs.Json)!.AsObject();missing["cashFunding"]!.AsObject().Remove("options");Closed(missing);
        var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string code)=>new(reference,code);
        var at=new DateTimeOffset(2040,12,18,0,0,0,TimeSpan.Zero);var expiry=at.AddDays(10);
        var series=new FuturesSeriesSpecification(R("synthetic_cash"),R("quantity"),R("usd"),ExactDecimal.Parse("1"),ExactDecimal.Parse("0.01"),
            R("location"),R("uniform"),new(2041,1,1),new(2041,2,1),expiry,R("quantity"),ExactDecimal.Parse("10000"),LotBasis.FixedTotal,at.AddYears(-1),"cash");
        var contract=new MarketContract(new(revision,Guid.NewGuid()),ContractKind.Future,series.DeliveryFrom,series.DeliveryTo,expiry,series.Key);
        var quote=new QuoteSpecification(R("quote"),QuoteKind.Outright,R("usd"),R("quantity"),ExactDecimal.Parse("1"),ExactDecimal.Parse("0.01"),
            ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0"),ExactDecimal.Parse("1000"));
        var underlying=new FuturesMarketBinding(Guid.NewGuid(),new(new(revision,"synthetic_cash"),contract.Key),contract,series,quote,"cash",FuturesMarketBinding.Implementation);
        var legacy=PortfolioRiskPreview.AccountingPricingPolicy with{FuturesConvention=FuturesMarginValuationPolicy.Convention,
            NativeFutures=new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version)};
        LifecyclePortfolioProjection.RequireFullOptionValue(underlying,legacy);
        var full=legacy with{FuturesConvention=FuturesEconomicValuationPolicy.Version,NativeFutures=legacy.NativeFutures! with{
            Economic=new(FuturesEconomicValuationPolicy.Version,FuturesMarginValueBinding.FlatReference,[])}};
        PortfolioValuation.ValidatePolicy(legacy);PortfolioValuation.ValidatePolicy(full);
        var physical=new FuturesMarketBinding(Guid.NewGuid(),underlying.Market,contract,series with{SettlementMethod="physical"},quote,"physical",FuturesMarketBinding.Implementation);
        LifecyclePortfolioProjection.RequireFullOptionValue(physical,legacy);
        try{LifecyclePortfolioProjection.RequireFullOptionValue(physical,full);throw new Exception("Full physical option preparation accepted vanilla reference value without an exact selected physical binding.");}
        catch(MarketDataException error)when(error.Code=="projection_physical_option_value"){}
        try{LifecyclePortfolioProjection.RequireFullOptionValue(underlying,full);}
        catch(MarketDataException error)when(error.Code=="projection_cash_option_value")
        {
            Console.WriteLine("PASS full cash-option admission: preserve the vanilla diagnostic and refuse an unselected saved-VM funding law");return;
        }
        throw new Exception("Full economic risk admitted vanilla cash-option value without an explicit qualified saved-margin-due funding model.");
    }
    private static void Closed(JsonNode value)
    {try{_=JsonSerializer.Deserialize<FuturesOptionValuationPolicy>(value.ToJsonString(),RiskJobs.Json);}catch(JsonException){return;}throw new Exception("A selected cash strategy lost missing or unknown model fields during retained transport.");}
}
