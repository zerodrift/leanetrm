using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class GasParkLoanWorkerChecks
{
    internal static async Task Run(string path,bool verify)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(RiskJobs.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Service worker checks require an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var receiptPath=Path.ChangeExtension(path,"price-worker.json");
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            if(verify)
            {
                using var receipt=JsonDocument.Parse(File.ReadAllText(receiptPath));var saved=receipt.RootElement;
                var read=JsonSerializer.SerializeToElement(await RiskJobs.Read(connection,manifest.CatalogId,saved.Id("id"),ct),RiskJobs.Json);
                if(read.Text("state")!="completed"||read.Id("requestId")!=saved.Id("requestId")||
                    !JsonElement.DeepEquals(read.GetProperty("result"),saved.GetProperty("report")))
                    throw new Exception("The retained service worker changed the complete ordinary PRICE report or original request identity.");
                return true;
            }
            var at=fixture.Instant("at");var context=fixture.Id("contextId");var book=fixture.Id("bookId");
            var source=(await new KnowledgeResolver(manifest.CatalogId).Read(connection,context,ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);
            await TradingContext.Bind(connection,manifest.CatalogId,source,at,ct);
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic frozen service PRICE source",at,DateWorkspace.Date(fixture.Text("businessDate")),
                [fixture.Id("markId"),..fixture.GetProperty("rateIds").EnumerateArray().Select(p=>p.GetGuid())],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[fixture.Id("contractId")]),context);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);
            var pricing=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesMarginValuationPolicy.Convention,
                SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                FuturesOptions:FuturesOptionValuationPolicy.Release1,NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version),
                GasOwned:fixture.GetProperty("nativeOwnership").GetBoolean()?
                    new(GasOwnedValuationPolicy.Version,GasOwnedValuationPolicy.WholeBooks,GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version):null);
            var factor=new CurveRiskAxisRequest("market_quote",new("contract",new(data.DefinitionRevision,"future"),
                Contract:new(data.DefinitionRevision,fixture.Id("contractId"))));
            var request=new PortfolioRiskRequest(new(frame.Id,TradingBlotter.Convention,"retained",context),[book],[],
                [new(factor,"1",ConnectedRiskBump.Convention)],PortfolioRiskPreview.Convention,RiskEngine.ExactNumericalPolicy,pricing,"holdings",[],
                TradeIds:fixture.GetProperty("selectedTrade").GetBoolean()?[fixture.Id("tradeId")]:null);
            var calculator=new PortfolioRiskPreview(database);
            var prepared=await calculator.Prepare(connection,data,frame,request,ct);
            var service=prepared.Portfolio.Positions.Where(p=>p.InstrumentType==GasParkLoanTicket.Product).ToArray();
            var native=prepared.Portfolio.Positions.Where(p=>p.InstrumentType!=GasParkLoanTicket.Product).ToArray();
            if(service.Length!=fixture.GetProperty("expectedServiceComponents").GetInt32()||service.Any(p=>p.TradeId!=fixture.Id("tradeId")))
                throw new Exception("Frozen service and native ownership handoff changed the retained portfolio membership.");
            if(native.Any(p=>p.Owner.Kind!=AccountingOwnerKind.NativeHolding)||
                !native.Select(p=>(p.Owner.Id,p.BindingId)).Order().SequenceEqual(fixture.GetProperty("expectedNativePositions").EnumerateArray()
                    .Select(p=>(p.Id("ownerId"),p.Id("bindingId"))).Order()))
                throw new Exception("Every independent native stock, claim, return liability and cash owner must survive exactly once in the frozen complete book.");
            // The ordinary HTTP operation attaches the same exact source/clock
            // envelope as the worker around this shared calculation report.
            var envelope=JsonSerializer.SerializeToNode(PortfolioRiskPreview.Calculate(prepared,ct),RiskJobs.Json)!.AsObject();
            envelope["context"]=JsonSerializer.SerializeToNode(source,RiskJobs.Json);
            envelope["asOf"]=JsonSerializer.SerializeToNode(at,RiskJobs.Json);
            var report=JsonSerializer.SerializeToElement(envelope,RiskJobs.Json);
            var totals=report.GetProperty("factors")[0].GetProperty("totals").EnumerateArray().ToArray();
            foreach(var (field,expected) in new[]{("base","expectedBase"),("up","expectedUp"),("down","expectedDown")})
            {
                var sum=totals.Aggregate(default(ExactDecimal),(value,row)=>value+ExactDecimal.Parse(row.GetProperty("values").Text(field)));
                if(sum!=ExactDecimal.Parse(fixture.Text(expected)))throw new Exception($"Independent service PRICE oracle differs for {field}: {sum}.");
            }
            if(prepared.Portfolio.InstantDiscountInputs.Any(p=>p.Input.DiscountFactor!=ExactDecimal.Parse("1")))
                throw new Exception("The service worker oracle requires its selected synthetic zero-rate inputs.");
            var requestId=Guid.NewGuid();var command=new SubmitPortfolioRiskJob(requestId,request);
            var first=JsonSerializer.SerializeToElement(await RiskJobs.Submit(connection,manifest,calculator,data,frame,command,ct),RiskJobs.Json);
            var retry=JsonSerializer.SerializeToElement(await RiskJobs.Submit(connection,manifest,calculator,data,frame,command,ct),RiskJobs.Json);
            if(first.Text("state")!="queued"||first.Id("id")!=retry.Id("id"))
                throw new Exception("The service PRICE worker did not retain its idempotent original outcome.");
            await File.WriteAllTextAsync(receiptPath,JsonSerializer.Serialize(new{id=first.Id("id"),requestId,engineVersion=RiskJobs.EngineVersion,report},RiskJobs.Json),ct);
            return true;
        },default);
        Console.WriteLine("PASS service frozen PRICE "+(verify?"retained read parity":"ordinary oracle and idempotent queued admission")+": "+fixture.Text("checkpoint"));
    }
}
