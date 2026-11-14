using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;

internal static class PhysicalOptionWorkerChecks
{
    internal static async Task Submit(PreviewDatabase database,NpgsqlConnection connection,PreviewData data,InputFrame frame,
        PortfolioRiskCatalogRequest scope,PortfolioValuationPolicy pricing,string path,CancellationToken ct)
    {
        var calculator=new PortfolioRiskPreview(database);
        var history=new PortfolioHistoryRequest(scope,pricing,new(RiskHistoryPolicy.Version1,CurveModel.NumericalPolicy,RiskHistoryPolicy.Raw,
            RiskHistoryPolicy.Heads,RiskHistoryPolicy.ExactCloses,RiskHistoryPolicy.Absolute,RiskHistoryPolicy.Complete,RiskHistoryPolicy.Fixed,2),
            new(data.ReferenceRevision,"source_business_days"),"2026-10-14","2026-10-17","16:00:00","reject","reject");
        var captured=JsonSerializer.SerializeToElement(await calculator.History(connection,data,frame,history,ct),RiskJobs.Json);
        if(!captured.GetProperty("coverage").GetProperty("available").GetBoolean()||captured.GetProperty("coverage").GetProperty("samples").GetInt32()!=2)
            throw new Exception("Physical option workers require complete A/B history before admission.");
        history=captured.GetProperty("request").Deserialize<PortfolioHistoryRequest>(RiskJobs.Json)!;
        var factors=captured.GetProperty("columns").EnumerateArray().Select(c=>c.GetProperty("factor").GetProperty("factor")
            .Deserialize<CurveRiskAxisRequest>(RiskJobs.Json)!).ToImmutableArray();
        var historical=PortfolioRiskPreview.HistoricalRequest(new(history,new(HistoricalRiskPolicy.Version1,HistoricalRiskPolicy.Numerics,
            HistoricalRiskPolicy.Session,HistoricalRiskPolicy.Sampling,HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail,HistoricalRiskPolicy.Valuation,"0.99"),Factors:factors));
        var simulation=new MonteCarloRiskPolicy(MonteCarloRiskPolicy.Version1,MonteCarloRiskPolicy.Numerics,ParametricRiskPolicy.Sample,
            ParametricRiskPolicy.Centered,ParametricRiskPolicy.Zero,ParametricRiskPolicy.Session,HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail,
            HistoricalRiskPolicy.Valuation,"0.99",MonteCarloRiskPolicy.Gaussian,MonteCarloRiskPolicy.Generator,MonteCarloRiskPolicy.ShockRounding,"42",32,MonteCarloRiskPolicy.Uncertainty);
        var monteCarlo=PortfolioRiskPreview.MonteCarloRequest(new(history,simulation,factors));
        var jobs=ImmutableArray.CreateBuilder<JsonElement>();
        foreach(var (request,kind) in new[]{(historical,"historical"),(monteCarlo,"monte_carlo")})
        {
            // Match the fresh worker's numerical cache without inheriting the
            // earlier fixture's PV, source-choice and history calculations.
            var host=Path.GetDirectoryName(path)!;
            var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
            await using var interactive=new PreviewDatabase(database.Manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
            var report=await interactive.WithIdentity("trader",null,async(current,token)=>
            {
                var context=(await new KnowledgeStore(database.Manifest.CatalogId).Read(current,frame.ContextId!.Value,token)).Context;
                await PinnedContext.BindExact(current,database.Manifest.CatalogId,context,token);
                await TradingContext.Bind(current,database.Manifest.CatalogId,context,frame.At,token);
                var selected=await interactive.Read(current,token,selectedRevision:data.DefinitionRevision.RevisionId,
                    observationIds:frame.ObservationIds,includeCalendarDays:false,referenceScope:ReferenceReadScope.TradeValuation,observationScope:ObservationReadScope.ExactPins);
                var value=JsonSerializer.SerializeToNode(await new PortfolioRiskPreview(interactive).Calculate(current,selected,frame,request,token),RiskJobs.Json)!.AsObject();
                value["context"]=JsonSerializer.SerializeToNode(context,RiskJobs.Json);
                value["asOf"]=JsonSerializer.SerializeToNode(frame.At,RiskJobs.Json);
                return JsonSerializer.SerializeToElement(value,RiskJobs.Json);
            },ct);
            var requestId=Guid.NewGuid();var command=new SubmitPortfolioRiskJob(requestId,request);
            var saved=JsonSerializer.SerializeToElement(await RiskJobs.Submit(connection,database.Manifest,calculator,data,frame,command,ct),RiskJobs.Json);
            var retry=JsonSerializer.SerializeToElement(await RiskJobs.Submit(connection,database.Manifest,calculator,data,frame,command,ct),RiskJobs.Json);
            if(saved.Text("kind")!=kind||saved.Text("state")!="queued"||saved.Id("id")!=retry.Id("id"))
                throw new Exception("The ordinary worker must retain its selected full-PV method and exact idempotent first outcome.");
            jobs.Add(JsonSerializer.SerializeToElement(new{id=saved.Id("id"),requestId,kind,report},RiskJobs.Json));
        }
        await File.WriteAllTextAsync(Path.ChangeExtension(path,"economic-worker.json"),JsonSerializer.Serialize(new{engineVersion=RiskJobs.EngineVersion,jobs},RiskJobs.Json),ct);
    }
    internal static async Task Verify(string path)
    {
        using var fixture=JsonDocument.Parse(File.ReadAllText(path));var value=fixture.RootElement;
        var host=Path.GetFullPath(value.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Physical option workers require their owned disposable cluster.");
        var manifest=value.GetProperty("manifest").Deserialize<PreviewManifest>(RiskJobs.Json)!;
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        using var receipts=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path,"economic-worker.json")));
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            foreach(var saved in receipts.RootElement.GetProperty("jobs").EnumerateArray())
            {
                var actual=JsonSerializer.SerializeToElement(await RiskJobs.Read(connection,manifest.CatalogId,saved.Id("id"),ct),RiskJobs.Json);
                if(actual.Text("kind")!=saved.Text("kind")||actual.Id("requestId")!=saved.Id("requestId")||actual.Text("state")!="completed"||
                    !JsonElement.DeepEquals(actual.GetProperty("result"),saved.GetProperty("report")))
                    throw new Exception("The native worker must retain the full selected physical option model, A/B sources and complete interactive report.");
            }
            return true;
        },default);
        Console.WriteLine("PASS actual ordinary physical option historical/Monte Carlo workers: exact selected model, native sources, retained input custody and interactive parity");
    }
}
