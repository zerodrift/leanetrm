using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class EconomicRiskNativeChecks
{
    internal static async Task VerifyGasSamples(PreviewDatabase database,NpgsqlConnection connection,PreviewData data,InputFrame frame,
        ImmutableArray<Guid> books,PortfolioValuationPolicy pricing,PortfolioEconomicProjectionRequest projection,string expectedTail,CancellationToken ct,
        ImmutableArray<AccountingPositionKey> futureOwners=default)
    {
        var scope=new PortfolioRiskCatalogRequest(new(frame.Id,TradingBlotter.Convention,"retained",frame.ContextId),books,[],null);
        var source=new PortfolioEconomicSourceRequest(scope,pricing,projection);
        var history=new PortfolioHistoryRequest(scope,pricing,new(RiskHistoryPolicy.Version1,CurveModel.NumericalPolicy,RiskHistoryPolicy.Raw,
            RiskHistoryPolicy.Heads,RiskHistoryPolicy.ExactCloses,RiskHistoryPolicy.Absolute,RiskHistoryPolicy.Complete,RiskHistoryPolicy.Fixed,2),
            new(data.ReferenceRevision,"test_business"),"2020-12-15","2020-12-18","16:00:00","reject","reject");
        var calculator=new PortfolioRiskPreview(database);
        var capture=JsonSerializer.SerializeToElement(await calculator.EconomicHistory(connection,data,frame,new(source,history),ct),RiskJobs.Json);
        var captured=capture.GetProperty("request").Deserialize<PortfolioHistoryRequest>(RiskJobs.Json)!;
        var factors=capture.GetProperty("factors").Deserialize<ImmutableArray<CurveRiskAxisRequest>>(RiskJobs.Json);
        var policy=new LifecycleEconomicRiskPolicy(LifecycleEconomicRiskPolicy.Historical,"0.99",HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail);
        var request=new PortfolioRiskRequest(captured.Scope.Context,books,[],[],PortfolioRiskPreview.Convention,RiskEngine.ExactNumericalPolicy,
            pricing,"holdings",[],Economic:new(projection,policy,captured,null,factors));
        var prepared=await calculator.Prepare(connection,data,frame,request,ct);
        var report=JsonSerializer.SerializeToElement(PortfolioRiskPreview.Calculate(prepared,ct),RiskJobs.Json);
        var result=report.GetProperty("result");var partition=result.GetProperty("economic").EnumerateArray().Single();
        expectedTail=ExactDecimal.Parse(expectedTail).ToString();
        var owners=prepared.Portfolio.Positions.Select(p=>new AccountingPositionKey(p.Owner,p.BindingId,p.BookId))
            .Concat(futureOwners.IsDefault?[]:futureOwners).ToHashSet();
        // The first native move is +0.2 and the next is a decline. The
        // complete portfolio is short fifteen units, so both 99% signed
        // tails lose three quotation multipliers; fixed cash adds no loss.
        if(result.GetProperty("samples").GetInt32()!=2||prepared.History!.Samples[0].Changes.Single()!=ExactDecimal.Parse("0.2")||
            prepared.History.Samples[1].Changes.Single().CompareTo(default)>=0||partition.Text("valueAtRisk")!=expectedTail||partition.Text("expectedShortfall")!=expectedTail||
            !report.GetProperty("holdings").EnumerateArray().Select(h=>h.GetProperty("owner").Deserialize<AccountingPositionKey>(RiskJobs.Json)).ToHashSet().SetEquals(owners))
            throw new Exception($"Native gas historical economic oracle expected 2 samples and VaR/ES {expectedTail}; received {result.GetProperty("samples").GetInt32()} samples, VaR {partition.Text("valueAtRisk")}, ES {partition.Text("expectedShortfall")}; moves {string.Join(';',prepared.History!.Samples.Select(s=>string.Join(',',s.Changes)))}.");
        var retained=RetainedFactorHistory.Capture(prepared.History!,await calculator.CaptureHistoryClock(prepared.History!,ct));
        var replay=await calculator.Prepare(connection,data,frame,prepared.Request,ct,retained:new(prepared.Portfolio.ConnectedMarkets,[]),
            historyPlan:retained,historyEvidence:prepared.History!.Evidence);
        if(!JsonElement.DeepEquals(report,JsonSerializer.SerializeToElement(PortfolioRiskPreview.Calculate(replay,ct),RiskJobs.Json)))
            throw new Exception("Retained economic historical inputs did not reproduce the complete native report.");
        var simulation=new MonteCarloRiskPolicy(MonteCarloRiskPolicy.Economic,MonteCarloRiskPolicy.Numerics,ParametricRiskPolicy.Sample,ParametricRiskPolicy.Centered,
            ParametricRiskPolicy.Zero,ParametricRiskPolicy.Session,HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail,MonteCarloRiskPolicy.EconomicValuation,
            "0.99",MonteCarloRiskPolicy.Gaussian,MonteCarloRiskPolicy.Generator,MonteCarloRiskPolicy.ShockRounding,"42",32,MonteCarloRiskPolicy.Uncertainty);
        var simulated=request with{Economic=request.Economic! with{Policy=policy with{Implementation=LifecycleEconomicRiskPolicy.MonteCarlo},Simulation=simulation}};
        var monteCarlo=JsonSerializer.SerializeToElement(await calculator.Calculate(connection,data,frame,simulated,ct),RiskJobs.Json);
        if(monteCarlo.GetProperty("result").GetProperty("samples").GetInt32()!=32||
            monteCarlo.GetProperty("result").GetProperty("monteCarloDiagnostics").GetArrayLength()!=1||
            monteCarlo.GetProperty("holdings").GetArrayLength()!=report.GetProperty("holdings").GetArrayLength())
            throw new Exception("Economic Monte Carlo did not revalue every path for the exact native owner scope.");
        Console.WriteLine("PASS native economic historical and Monte Carlo API: independently calculated signed tails, complete commodity/cash owners, captured calibration and exact retained replay");
    }
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
