using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class EconomicRiskContractChecks
{
    internal static void Run()
    {
        var context=new FuturesReadRequest(Guid.NewGuid(),TradingBlotter.Convention,"retained",Guid.NewGuid());
        var pricing=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesRisk.Implementation,SwapStore.Convention,
            RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1);
        var projection=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version,"synthetic_close",1,
            PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
            new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry),[]);
        var policy=new LifecycleEconomicRiskPolicy(LifecycleEconomicRiskPolicy.Deterministic,"0.99",HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail);
        var deterministic=new PortfolioEconomicRiskRequest(projection,policy,null,null,[]);
        var outer=new PortfolioRiskRequest(context,[Guid.NewGuid()],[],[],PortfolioRiskPreview.Convention,RiskEngine.ExactNumericalPolicy,pricing,"holdings",[],Economic:deterministic);
        PortfolioRiskPreview.ValidateEconomic(outer,deterministic);
        Reject("risk_economic_scope",()=>PortfolioRiskPreview.ValidateEconomic(outer with{Detail="books"},deterministic));
        Reject("risk_economic_scope",()=>PortfolioRiskPreview.ValidateEconomic(outer with{Stress=default},deterministic));
        Reject("risk_economic_scope",()=>PortfolioRiskPreview.ValidateEconomic(outer,deterministic with{Factors=default}));
        var reference=new ReferenceKey(new(Guid.NewGuid(),Guid.NewGuid()),"calendar");
        var history=new PortfolioHistoryRequest(new(context,outer.BookIds,[],null),pricing,
            new(RiskHistoryPolicy.Version1,CurveModel.NumericalPolicy,RiskHistoryPolicy.Raw,RiskHistoryPolicy.Heads,RiskHistoryPolicy.ExactCloses,
                RiskHistoryPolicy.Absolute,RiskHistoryPolicy.Complete,RiskHistoryPolicy.Fixed,2),reference,
            "2026-01-01","2026-02-01","16:00:00","reject","reject",new('a',64));
        Reject("risk_economic_scope",()=>PortfolioRiskPreview.ValidateEconomic(outer,deterministic with{History=history}));
        var historical=deterministic with{Policy=policy with{Implementation=LifecycleEconomicRiskPolicy.Historical},History=history};
        PortfolioRiskPreview.ValidateEconomic(outer with{Economic=historical},historical);
        Reject("risk_economic_scope",()=>PortfolioRiskPreview.ValidateEconomic(outer,historical with{History=history with{ExpectedManifestId=null}}));
        Reject("risk_economic_scope",()=>PortfolioRiskPreview.ValidateEconomic(outer,historical with{History=history with{Scope=history.Scope with{TradeIds=[]}}}));
        Reject("risk_economic_scope",()=>PortfolioRiskPreview.ValidateEconomic(outer with{Historical=new(history,new(HistoricalRiskPolicy.Version1,HistoricalRiskPolicy.Numerics,
            HistoricalRiskPolicy.Session,HistoricalRiskPolicy.Sampling,HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail,HistoricalRiskPolicy.Valuation,"0.99"))},historical));
        Reject("risk_economic_policy",()=>PortfolioRiskPreview.ValidateEconomic(outer,deterministic with{Policy=policy with{Implementation="parametric"}}));
        var json=JsonSerializer.Serialize(deterministic,RiskJobs.Json);
        foreach(var field in new[]{"history","simulation","factors"})
        {
            var node=System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();node.Remove(field);
            try{JsonSerializer.Deserialize<PortfolioEconomicRiskRequest>(node.ToJsonString(),RiskJobs.Json);throw new Exception("Missing explicit economic choice accepted: "+field);}
            catch(JsonException){}
        }
        if(JsonSerializer.Serialize(outer with{Economic=null},RiskJobs.Json).Contains("economic",StringComparison.Ordinal))
            throw new Exception("An absent economic envelope changed legacy serialized requests.");
        var empty=new PortfolioValuation.Prepared(context.SnapshotId,new("2026-01-01",0,"2026-01-02T00:00:00Z",context.ContextId),pricing,[],[],[],[]);
        var unprepared=new PortfolioRiskPreview.Prepared(outer,RiskDetail.Holdings,outer.BookIds,[],empty,[],[],[]);
        Reject("risk_economic_scope",()=>PortfolioRiskPreview.Calculate(unprepared,default));
        var proof=new PreparedLifecyclePortfolio(projection,JsonSerializer.SerializeToElement(new{openingAt="2026-01-01T00:00:00Z",closeAt="2026-01-02T00:00:00Z"}),[],empty,
            [JsonSerializer.SerializeToElement(new{owner="synthetic",revision=2})],[]);
        var retained=RiskJobs.EconomicFrame(proof);
        RiskJobs.RequireEconomicFrame(retained,proof);RiskJobs.RequireEconomicFrame(null,null);
        Reject("risk_economic_evidence",()=>RiskJobs.RequireEconomicFrame(null,proof));
        Reject("risk_economic_evidence",()=>RiskJobs.RequireEconomicFrame(retained,null));
        foreach(var field in new[]{"horizon","products","optionCalibrations"})
        {
            var changed=retained.DeepClone().AsObject();changed.Remove(field);
            Reject("risk_economic_evidence",()=>RiskJobs.RequireEconomicFrame(changed,proof));
        }
        var altered=retained.DeepClone();altered["products"]![0]!["revision"]=1;
        Reject("risk_economic_evidence",()=>RiskJobs.RequireEconomicFrame(altered,proof));
        Console.WriteLine("PASS economic risk contract: exclusive complete scope, explicit no-factor method, required null choices and separate event-aware policy");
    }
    private static void Reject(string code,Action work)
    {
        try{work();}catch(MarketDataException error)when(error.Code==code){return;}
        throw new Exception("Expected economic refusal "+code);
    }
}
