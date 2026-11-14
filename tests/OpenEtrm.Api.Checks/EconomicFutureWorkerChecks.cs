using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class EconomicFutureWorkerChecks
{
    internal static async Task Submit(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(RiskJobs.Json)!;var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Economic futures checks require their owned disposable cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,fixture.Id("contextId"),ct)).Context;
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic economic futures source",fixture.Instant("at"),PublishedSchedules.ContextDate(context),
                [..fixture.GetProperty("pins").EnumerateArray().Select(v=>v.GetGuid())],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[fixture.Id("contractId")]),context.ContextId);
            await PinnedContext.BindExact(connection,manifest.CatalogId,context,ct);await TradingContext.Bind(connection,manifest.CatalogId,context,frame.At,ct);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);
            var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
            var books=ImmutableArray.Create(fixture.Id("bookId"));var trades=ImmutableArray.Create(fixture.Id("tradeId"));
            var scope=await RiskTradeScope.Resolve(connection,manifest.CatalogId,books,trades,ct);
            var pricing=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesMarginValuationPolicy.Convention,
                SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version));
            var opening=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,context,books,pricing,[],[],ct,selection:scope);
            var projection=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version,fixture.Text("session"),1,
                PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Linear,LifecycleProjectionPolicy.PayAtDue,LifecycleProjectionPolicy.Nominal,
                    LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),[]);
            var prepared=await LifecyclePortfolioProjection.Prepare(connection,database,opening,projection,null,ct);
            if(prepared.Factors.Length!=1||prepared.Projections.Single().Components.Single() is not FuturesLifecycleProjection)
                throw new Exception("The future worker must prepare its native adopted owner, margin state and full primitive factor closure.");
            await EconomicWorkerChecks.Submit(database,connection,data,frame,new(new(frame.Id,TradingBlotter.Convention,"retained",context.ContextId),books,[],trades),
                pricing,projection,prepared,path,ct);
            return true;
        },default);
        Console.WriteLine("PASS managed futures economic source and complete retained historical/Monte Carlo requests");
    }
}
