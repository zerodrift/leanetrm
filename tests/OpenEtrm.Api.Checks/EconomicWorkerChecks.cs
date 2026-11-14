using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class EconomicWorkerChecks
{
    internal static async Task Run(string path,bool verify)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(RiskJobs.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Economic worker checks require their owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var receiptPath=Path.ChangeExtension(path,"economic-worker.json");
        if(verify)
        {
            using var receipts=JsonDocument.Parse(File.ReadAllText(receiptPath));
            await database.WithIdentity("trader",null,async(connection,ct)=>
            {
                foreach(var saved in receipts.RootElement.GetProperty("jobs").EnumerateArray())
                {
                    var read=JsonSerializer.SerializeToElement(await RiskJobs.Read(connection,manifest.CatalogId,saved.Id("id"),ct),RiskJobs.Json);
                    if(read.Text("kind")!="economic_lifecycle"||read.Id("requestId")!=saved.Id("requestId")||read.Text("state")!=saved.Text("expectedState"))
                        throw new Exception("The managed economic worker did not preserve its exact retained operation, method or terminal state.");
                    if(saved.Text("expectedState")=="completed"&&!JsonElement.DeepEquals(read.GetProperty("result"),saved.GetProperty("report")))
                        throw new Exception("The read-only managed worker changed the interactive economic report, native ownership, horizon, retained history or event-source proof.");
                    if(saved.Text("expectedState")=="cancelled"&&read.GetProperty("result").ValueKind!=JsonValueKind.Null)
                        throw new Exception("A cancelled economic worker published a financial result.");
                }
                return true;
            },default);
            Console.WriteLine("PASS actual managed economic worker: historical and Monte Carlo match complete interactive reports; owned cancellation remains cancelled with no result");
            return;
        }
        var jobs=await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var context=fixture.Id("contextId");var at=fixture.Instant("at");var captured=(await new KnowledgeStore(manifest.CatalogId).Read(connection,context,ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,captured,ct);await TradingContext.Bind(connection,manifest.CatalogId,captured,at,ct);
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic complete economic worker source",at,PublishedSchedules.ContextDate(captured),
                [fixture.Id("markId"),..fixture.GetProperty("rateIds").EnumerateArray().Select(p=>p.GetGuid())],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[fixture.Id("contractId")]),context);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);var books=ImmutableArray.Create(fixture.Id("bookId"));
            var scope=new PortfolioRiskCatalogRequest(new(frame.Id,TradingBlotter.Convention,"retained",context),books,[],null);
            var pricing=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesMarginValuationPolicy.Convention,
                SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version),
                GasOwned:new(GasOwnedValuationPolicy.Version,GasOwnedValuationPolicy.WholeBooks,GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version));
            var projection=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version,fixture.Text("economicSession"),1,
                PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                    LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),
                [new(fixture.Id("accountId"),fixture.Id("profileId"),1)]);
            var calculator=new PortfolioRiskPreview(database);
            var history=new PortfolioHistoryRequest(scope,pricing,new(RiskHistoryPolicy.Version1,CurveModel.NumericalPolicy,RiskHistoryPolicy.Raw,
                RiskHistoryPolicy.Heads,RiskHistoryPolicy.ExactCloses,RiskHistoryPolicy.Absolute,RiskHistoryPolicy.Complete,RiskHistoryPolicy.Fixed,2),
                new(data.ReferenceRevision,"test_business"),"2020-12-15","2020-12-18","16:00:00","reject","reject");
            var capture=JsonSerializer.SerializeToElement(await calculator.EconomicHistory(connection,data,frame,new(new(scope,pricing,projection),history),ct),RiskJobs.Json);
            var retained=capture.GetProperty("request").Deserialize<PortfolioHistoryRequest>(RiskJobs.Json)!;
            var factors=capture.GetProperty("factors").Deserialize<ImmutableArray<CurveRiskAxisRequest>>(RiskJobs.Json);
            var policy=new LifecycleEconomicRiskPolicy(LifecycleEconomicRiskPolicy.Historical,"0.99",HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail);
            var historical=new PortfolioRiskRequest(scope.Context,books,[],[],PortfolioRiskPreview.Convention,RiskEngine.ExactNumericalPolicy,pricing,"holdings",[],
                Economic:new(projection,policy,retained,null,factors));
            var simulation=new MonteCarloRiskPolicy(MonteCarloRiskPolicy.Economic,MonteCarloRiskPolicy.Numerics,ParametricRiskPolicy.Sample,ParametricRiskPolicy.Centered,
                ParametricRiskPolicy.Zero,ParametricRiskPolicy.Session,HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail,MonteCarloRiskPolicy.EconomicValuation,
                "0.99",MonteCarloRiskPolicy.Gaussian,MonteCarloRiskPolicy.Generator,MonteCarloRiskPolicy.ShockRounding,"42",32,MonteCarloRiskPolicy.Uncertainty);
            var monteCarlo=historical with{Economic=historical.Economic! with{Policy=policy with{Implementation=LifecycleEconomicRiskPolicy.MonteCarlo},Simulation=simulation}};
            var receipts=ImmutableArray.CreateBuilder<JsonElement>();
            foreach(var (request,cancel) in new[]{(historical,false),(monteCarlo,false),(historical,true)})
            {
                var report=JsonSerializer.SerializeToElement(await calculator.Calculate(connection,data,frame,request,ct),RiskJobs.Json);
                var value=report.GetProperty("result");var samples=value.GetProperty("samples").GetInt32();
                if(samples!=(cancel||request.Economic!.Simulation is null?2:32)||report.GetProperty("holdings").GetArrayLength()<2||
                    report.GetProperty("holdings").EnumerateArray().Any(h=>h.GetProperty("owner").GetProperty("owner").GetProperty("kind").GetInt32()!=2))
                    throw new Exception("Economic worker fixture must retain complete independent commodity and cash ownership and every declared sample.");
                if(request.Economic!.Simulation is null&&value.GetProperty("economic")[0].Text("valueAtRisk")!=ExactDecimal.Parse(fixture.Text("expectedHistoricalVar")).ToString())
                    throw new Exception("The actual worker fixture lost its independent signed historical-tail oracle.");
                var requestId=Guid.NewGuid();var submit=new SubmitPortfolioRiskJob(requestId,request);
                var queued=JsonSerializer.SerializeToElement(await RiskJobs.Submit(connection,manifest,calculator,data,frame,submit,ct),RiskJobs.Json);
                var retry=JsonSerializer.SerializeToElement(await RiskJobs.Submit(connection,manifest,calculator,data,frame,submit,ct),RiskJobs.Json);
                if(queued.Text("state")!="queued"||queued.Text("kind")!="economic_lifecycle"||queued.Id("id")!=retry.Id("id"))
                    throw new Exception("The managed economic job must retain its exact idempotent first outcome.");
                receipts.Add(JsonSerializer.SerializeToElement(new{id=queued.Id("id"),requestId,expectedState=cancel?"cancelled":"completed",report},RiskJobs.Json));
            }
            return receipts.ToImmutable();
        },default);
        await File.WriteAllTextAsync(receiptPath,JsonSerializer.Serialize(new{engineVersion=RiskJobs.EngineVersion,jobs},RiskJobs.Json));
        Console.WriteLine("PASS managed economic admission: complete historical and Monte Carlo requests, independent signed-tail oracle and exact retry receipts committed for the real worker");
    }
}
