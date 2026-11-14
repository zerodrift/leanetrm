using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;

internal static class EconomicRiskNativeChecks
{
    internal static async Task Verify(PreviewDatabase database,NpgsqlConnection connection,PreviewData data,InputFrame frame,
        PortfolioRiskCatalogRequest scope,PortfolioValuationPolicy pricing,PortfolioEconomicProjectionRequest projection,
        PreparedLifecyclePortfolio expected,CancellationToken ct)
    {
        var calculator=new PortfolioRiskPreview(database);
        var source=new PortfolioEconomicSourceRequest(scope,pricing,projection);
        var discovered=JsonSerializer.SerializeToElement(await calculator.EconomicSources(connection,data,frame,source,ct),RiskJobs.Json);
        if(discovered.GetProperty("factors").GetArrayLength()!=expected.Factors.Length||
            !JsonElement.DeepEquals(discovered.GetProperty("horizon"),expected.HorizonEvidence)||
            discovered.GetProperty("parametricAvailable").GetBoolean())
            throw new Exception("Economic source discovery omitted the complete event factor union or changed the selected next close.");
        var request=new PortfolioRiskRequest(scope.Context,scope.BookIds,scope.PortfolioIds,[],PortfolioRiskPreview.Convention,RiskEngine.ExactNumericalPolicy,
            pricing,"holdings",[],scope.TradeIds,Economic:new(projection,new(LifecycleEconomicRiskPolicy.Deterministic,"0.99",HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail),null,null,[]));
        if(expected.Factors.IsEmpty)
        {
            var prepared=await calculator.Prepare(connection,data,frame,request,ct);
            var report=JsonSerializer.SerializeToElement(PortfolioRiskPreview.Calculate(prepared,ct),RiskJobs.Json);
            var partitions=report.GetProperty("result").GetProperty("economic");
            if(report.GetProperty("measure").GetString()!=LifecycleEconomicRiskPolicy.Measure||partitions.GetArrayLength()!=1||
                partitions[0].GetProperty("valueAtRisk").GetString()!="0"||partitions[0].GetProperty("expectedShortfall").GetString()!="0")
                throw new Exception("A completed native swap with its actual future cash must conserve wealth through the economic API and exact point-mass risk reducer.");
            RiskJobs.RequireEconomicFrame(RiskJobs.EconomicFrame(prepared.Economic!),prepared.Economic);
            var replay=await calculator.Prepare(connection,data,frame,prepared.Request,ct,retained:new(prepared.Portfolio.ConnectedMarkets,[]));
            RiskJobs.RequireEconomicFrame(RiskJobs.EconomicFrame(prepared.Economic!),replay.Economic);
            if(!JsonElement.DeepEquals(report,JsonSerializer.SerializeToElement(PortfolioRiskPreview.Calculate(replay,ct),RiskJobs.Json)))
                throw new Exception("Retained complete economic API replay changed its signed tail or immutable evidence.");
            Console.WriteLine("PASS economic API native deterministic projection, exact signed tails, complete source discovery and immutable retained replay");
        }
        else
        {
            try{await calculator.Prepare(connection,data,frame,request,ct);throw new Exception("Economic deterministic method accepted a variable factor.");}
            catch(MarketDataException error)when(error.Code=="risk_economic_factors"){}
            Console.WriteLine("PASS economic API complete source discovery and explicit no-factor scope refusal");
        }
    }
}
