using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class GasParkCustodyWorkerChecks
{
    internal static async Task Run(string path,bool verify,bool nativeOnly=false)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(RiskJobs.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Custody worker qualification requires its owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        if(nativeOnly&&fixture.TryGetProperty("setupAuthorityProbe",out var setupProbe)&&setupProbe.GetBoolean())
        {
            await RequireSetupAuthority(database,fixture,path);
            Console.WriteLine("PASS custody setup original authority: "+fixture.Text("checkpoint"));
            return;
        }
        var receiptPath=Path.ChangeExtension(path,"custody-price-worker.json");
        if(!verify&&fixture.TryGetProperty("requirePnlRefusal",out var requirePnl)&&requirePnl.GetBoolean())
            await database.WithIdentity("trader",null,async(connection,ct)=>
            {
                var source=(await new KnowledgeResolver(manifest.CatalogId).Read(connection,fixture.Id("contextId"),ct)).Context;
                var frame=new InputFrame(Guid.NewGuid(),"Synthetic cross-clock custody refusal",fixture.Instant("at"),DateWorkspace.Date(fixture.Text("businessDate")),
                    [fixture.Id("markId"),..fixture.GetProperty("rateIds").EnumerateArray().Select(p=>p.GetGuid())],
                    ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[fixture.Id("contractId")]),fixture.Id("contextId"));
                var openingSource=fixture.TryGetProperty("pnlOpeningContextId",out var openingId)
                    ?(await new KnowledgeResolver(manifest.CatalogId).Read(connection,openingId.GetGuid(),ct)).Context:source;
                await RequirePnlRefusal(connection,database,frame,openingSource,source,fixture.Id("bookId"),fixture.Id("tradeId"),ct);
                return true;
            },default);
        if(nativeOnly&&fixture.TryGetProperty("pnlOnly",out var pnlOnly)&&pnlOnly.GetBoolean())
        {
            if(!fixture.GetProperty("requirePnlRefusal").GetBoolean())throw new Exception("Select the explicit cross-clock refusal check.");
            Console.WriteLine("PASS custody cross-clock P&L refusal: "+fixture.Text("checkpoint"));
            return;
        }
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            if(verify)
            {
                using var receipt=JsonDocument.Parse(File.ReadAllText(receiptPath));var saved=receipt.RootElement;
                var row=JsonSerializer.SerializeToElement(await RiskJobs.Read(connection,manifest.CatalogId,saved.Id("id"),ct),RiskJobs.Json);
                if(row.Text("state")!="completed"||row.Id("requestId")!=saved.Id("requestId")||!JsonElement.DeepEquals(row.GetProperty("result"),saved.GetProperty("report")))
                    throw new Exception("Retained custody PRICE changed the complete ordinary report or original request identity.");
                return true;
            }
            var at=fixture.Instant("at");var context=fixture.Id("contextId");var book=fixture.Id("bookId");var trade=fixture.Id("tradeId");
            var source=(await new KnowledgeResolver(manifest.CatalogId).Read(connection,context,ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);await TradingContext.Bind(connection,manifest.CatalogId,source,at,ct);
            if(nativeOnly&&fixture.TryGetProperty("expectedDirectoryRefusal",out var expectedDirectory))
            {
                if(expectedDirectory.GetString()!="position_facts")throw new Exception("Select the exact missing-origin refusal contract.");
                var directoryFrame=new InputFrame(Guid.NewGuid(),"Synthetic incomplete retained origin",at,DateWorkspace.Date(fixture.Text("businessDate")),[],
                    ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context);
                try{await new TradePositionFacts(database).Read(connection,TradingView.Retained(directoryFrame,source),[book],[],0,64,ct);}
                catch(MarketDataException error)when(error.Code=="position_facts"){return true;}
                throw new Exception("The complete retained directory must continue to refuse a trade without any supported typed origin.");
            }
            var agreement=await GasParkCustodyTicketStore.Read(connection,database,new(context,at,DateWorkspace.Date(fixture.Text("businessDate"))),trade,ct);
            var omitCommodity=nativeOnly&&fixture.GetProperty("omitCommodityMarks").GetBoolean();
            var omitRates=nativeOnly&&fixture.GetProperty("omitRateMarks").GetBoolean();
            var pins=ImmutableArray.CreateBuilder<Guid>();
            if(!omitCommodity)pins.Add(fixture.Id("markId"));
            if(!omitRates)pins.AddRange(fixture.GetProperty("rateIds").EnumerateArray().Select(p=>p.GetGuid()));
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic retained custody PRICE source",at,DateWorkspace.Date(fixture.Text("businessDate")),pins.ToImmutable(),
                omitCommodity?ImmutableDictionary<string,ImmutableArray<Guid>>.Empty:ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[fixture.Id("contractId")]),context);
            if(nativeOnly)
            {
                var terms=agreement.GetProperty("terms");
                frame=frame with{Source=new(terms.Id("definitionRevisionId"),terms.Id("referenceCatalogId"),terms.Id("referenceRevisionId"))};
            }
            var data=await MarketInputSelection.Root(database,connection,frame,ct);
            var owned=fixture.GetProperty("nativeOwnership").GetBoolean();
            var policy=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesMarginValuationPolicy.Convention,
                SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                FuturesOptions:FuturesOptionValuationPolicy.Release1,NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version),
                GasOwned:owned?new(GasOwnedValuationPolicy.Version,GasOwnedValuationPolicy.WholeBooks,GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version):null);
            if(nativeOnly)
            {
                var snapshot=data.Snapshot(frame).WithKnowledge(context,SchedulePreview.ParseAt(source.KnownAt));
                var value=await new GasParkCustodyTicketStore(database).Value(connection,data,frame,snapshot,agreement,false,ct);
                if(ExactDecimal.Parse(value)!=ExactDecimal.Parse(fixture.Text("expectedServicePv")))throw new Exception("The independent service fee/cash oracle differs.");
                PortfolioValuation.Prepared? portfolio=null;
                var emptyScope=fixture.TryGetProperty("expectedPortfolioRefusal",out var expectedPortfolio);
                if(emptyScope&&expectedPortfolio.GetString()!="risk_position")throw new Exception("Select the exact empty economic scope refusal.");
                try{portfolio=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,source,[book],policy,[],[],ct);}
                catch(MarketDataException error)when(emptyScope&&error.Code=="risk_position")
                {
                    if(error.Message!="The selected books have no financial holdings to revalue.")
                        throw new Exception("An empty economic scope must distinguish financial holdings from its retained visible custody quantity.");
                }
                if(emptyScope&&portfolio is not null||!emptyScope&&portfolio is null)
                    throw new Exception("An empty economic scope must retain its explicit refusal, without fabricating a zero owner or total.");
                if(portfolio is not null)
                {
                    Memberships(portfolio,fixture,trade,agreement.Id("instrumentId"));
                    var run=PortfolioValuation.Calculate(portfolio,[],[],RiskDetail.Holdings,ct);
                    var total=run.Totals.Aggregate(ExactMoney.FromMoney(new(default,GasParkCustodyAdapter.Terms(manifest.CatalogId,agreement).Currency)),(sum,row)=>sum.Add(row.Value));
                    if(total!=ExactMoney.Parse(fixture.Text("expectedBase"),total.Currency))throw new Exception("The independent complete-book custody value differs from the ordinary leaf portfolio.");
                }
                var positions=await GasParkCustodyPositions.Read(connection,database,frame,[book],[],ct,new(trade,agreement.Id("instrumentId"),1,GasParkCustodyTicket.Product));
                if(positions.Length!=1||positions[0].Fact().BookedQuantity!=agreement.Text("quantity"))throw new Exception("The native custody position lost its exact retained booking identity.");
                ILifecycleActionHandler handler=new GasParkCustodyActions(database);
                var captured=new LifecycleDiscoveryAccess(ImmutableHashSet.Create(StringComparer.Ordinal,"ReadMarketData","ReadTrades","ReadGasOperations"));
                var target=new LifecycleTarget("gas_park_custody",trade,agreement.Id("instrumentId"));
                if(agreement.GetProperty("terms").Text("perspective")=="customer")
                {
                    try
                    {
                        await handler.Discover(connection,new(context,at,frame.BusinessDate),target,captured,ct);
                        throw new Exception("Current customer stock authority must not replace missing original captured discovery authority.");
                    }
                    catch(PreviewDeniedException){}
                }
                else if((await handler.Discover(connection,new(context,at,frame.BusinessDate),target,captured,ct)).Length!=1)
                    throw new Exception("Provider discovery must remain usable without original or current owned-gas authority.");
                var facts=await new TradePositionFacts(database).Read(connection,TradingView.Retained(frame,source),[book],[],0,64,ct);
                if(facts.Positions.Count(p=>p.TradeId==trade&&p.BindingId==agreement.Id("instrumentId")&&p.InstrumentType==GasParkCustodyTicket.Product)!=1)
                    throw new Exception("The ordinary retained position directory must recognize each exact custody origin once.");
                return true;
            }
            // The selected shared market scenario is independent of custody's
            // point. Provider cash has no declared commodity-price dependency.
            var factor=new CurveRiskAxisRequest("market_quote",new("contract",new(data.DefinitionRevision,"future"),Contract:new(data.DefinitionRevision,fixture.Id("contractId"))));
            var request=new PortfolioRiskRequest(new(frame.Id,TradingBlotter.Convention,"retained",context),[book],[],
                [new(factor,"1",ConnectedRiskBump.Convention)],PortfolioRiskPreview.Convention,RiskEngine.ExactNumericalPolicy,policy,"holdings",[],
                TradeIds:fixture.GetProperty("selectedTrade").GetBoolean()?[trade]:null);
            var calculator=new PortfolioRiskPreview(database);var prepared=await calculator.Prepare(connection,data,frame,request,ct);
            Memberships(prepared.Portfolio,fixture,trade,agreement.Id("instrumentId"));
            if(prepared.Portfolio.InstantDiscountInputs.Any(p=>p.Input.DiscountFactor!=ExactDecimal.Parse("1")))
                throw new Exception("The independent custody oracle requires the explicitly selected synthetic zero-rate observations.");
            var envelope=JsonSerializer.SerializeToNode(PortfolioRiskPreview.Calculate(prepared,ct),RiskJobs.Json)!.AsObject();
            envelope["context"]=JsonSerializer.SerializeToNode(source,RiskJobs.Json);envelope["asOf"]=JsonSerializer.SerializeToNode(at,RiskJobs.Json);
            var report=JsonSerializer.SerializeToElement(envelope,RiskJobs.Json);
            var totals=report.GetProperty("factors")[0].GetProperty("totals").EnumerateArray().ToArray();
            foreach(var (field,expected) in new[]{("base","expectedBase"),("up","expectedUp"),("down","expectedDown")})
            {
                var actual=totals.Aggregate(default(ExactDecimal),(sum,row)=>sum+ExactDecimal.Parse(row.GetProperty("values").Text(field)));
                if(actual!=ExactDecimal.Parse(fixture.Text(expected)))throw new Exception($"The independent custody {field} oracle differs: {actual}.");
            }
            var requestId=Guid.NewGuid();var command=new SubmitPortfolioRiskJob(requestId,request);
            var first=JsonSerializer.SerializeToElement(await RiskJobs.Submit(connection,manifest,calculator,data,frame,command,ct),RiskJobs.Json);
            var repeated=JsonSerializer.SerializeToElement(await RiskJobs.Submit(connection,manifest,calculator,data,frame,command,ct),RiskJobs.Json);
            if(first.Text("state")!="queued"||first.Id("id")!=repeated.Id("id"))throw new Exception("Custody PRICE admission must retain its idempotent original queued request.");
            await File.WriteAllTextAsync(receiptPath,JsonSerializer.Serialize(new{id=first.Id("id"),requestId,engineVersion=RiskJobs.EngineVersion,report},RiskJobs.Json),ct);
            return true;
        },default);
        Console.WriteLine("PASS custody "+(nativeOnly&&fixture.TryGetProperty("expectedDirectoryRefusal",out _)?"normal directory missing-origin refusal":
            nativeOnly&&fixture.TryGetProperty("expectedPortfolioRefusal",out _)?"native service PV, exact empty-portfolio refusal and retained position":
            nativeOnly?"native service/complete-book PV and exact independent input demand":verify?"PRICE retained complete-report parity":"PRICE ordinary numerical oracle and idempotent queue")+": "+fixture.Text("checkpoint"));
    }
    static async Task RequireSetupAuthority(PreviewDatabase database,JsonElement fixture,string path)
    {
        var expectedDenial=fixture.GetProperty("expectedDenial").GetBoolean();
        try
        {
            await database.WithIdentity("trader","ReadGasOperations",async(connection,ct)=>
            {
                var owned=await PreviewDatabase.HasPermission(connection,"ReadGasOwnedHoldings",ct);
                if(owned!=fixture.GetProperty("expectedInitialOwned").GetBoolean())
                    throw new Exception("The setup probe did not enter under its intended original owned-gas authority.");
                await using var capture=ScheduleStore.Command(connection,"SELECT security.authority_until('ReadGasOperations')");
                var opsUntil=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
                if(fixture.TryGetProperty("expectedOpsUntil",out _)&&opsUntil!=fixture.Instant("expectedOpsUntil"))
                    throw new Exception("The setup probe missed its intended original custody-permission deadline.");
                if(fixture.TryGetProperty("expectedOwnedUntil",out _))
                {
                    await using var stockCapture=ScheduleStore.Command(connection,"SELECT security.authority_until('ReadGasOwnedHoldings')");
                    var stockUntil=new DateTimeOffset((DateTime)(await stockCapture.ExecuteScalarAsync(ct))!);
                    if(stockUntil!=fixture.Instant("expectedOwnedUntil"))throw new Exception("The setup probe missed its intended original stock-permission deadline.");
                }
                await File.WriteAllTextAsync(Path.ChangeExtension(path,"setup-entry.json"),JsonSerializer.Serialize(new{owned,opsUntil}),ct);
                var context=new LifecycleReadContext(fixture.Id("contextId"),fixture.Instant("at"),DateWorkspace.Date(fixture.Text("businessDate")),Guid.NewGuid());
                var setup=await GasParkCustodySetup.Read(connection,database,new(context,new("gas_park_custody",fixture.Id("tradeId"),fixture.Id("instrumentId"))),ct);
                if(!expectedDenial&&!setup.Accounts.IsEmpty)throw new Exception("Provider setup must retain no inventory account or stock choices.");
                return true;
            },default);
        }
        catch(PreviewDeniedException)when(expectedDenial){return;}
        catch(PostgresException error)when(expectedDenial&&error.SqlState=="42501"){return;}
        if(expectedDenial)throw new Exception("Custody setup must retain its original mandatory and conditional authority before the first business-source read.");
    }
    static async Task RequirePnlRefusal(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,ScheduleContext openingSource,ScheduleContext source,Guid book,Guid trade,CancellationToken ct)
    {
        var opening=frame with{Id=Guid.NewGuid(),At=frame.At.AddMinutes(-1),ContextId=openingSource.ContextId};
        var request=new TradingPnlRequest(new(opening.Id,TradingBlotter.Convention,"retained",openingSource.ContextId),
            new(frame.Id,TradingBlotter.Convention,"retained",source.ContextId),[book],[],[trade],TradingPnlAccounting.Version1,
            TradingPnlAttribution.OpeningGreeks,TradingPnlReport.SelectedBump,source.KnownAt??throw new Exception("Retain the exact certified P&L knowledge cut."),
            new(TradingPnlReport.Roll,ClockChange.FrozenInputs,null),new(1,2,"0.000001","0.15","0.1"));
        var refused=false;
        try
        {
            await new TradingPnlReport(database).Run(connection,TradingView.Retained(opening,openingSource),TradingView.Retained(frame,source),request,ct);
        }
        catch(MarketDataException error)when(error.Code=="gas_custody_pnl"){refused=true;}
        if(!refused)throw new Exception("Custody requires an explicit whole-report cross-clock P&L refusal even when its remaining service components are empty.");
        // An unrelated empty book reaches the pre-existing P&L position refusal.
        // This also proves both absence-probe savepoints restore their bindings.
        var excluded=request with{BookIds=[Guid.Parse("d1000000-0000-0000-0000-000000000002")],TradeIds=null};
        try
        {
            await new TradingPnlReport(database).Run(connection,TradingView.Retained(opening,openingSource),TradingView.Retained(frame,source),excluded,ct);
        }
        catch(MarketDataException error)when(error.Code=="pnl_position"){return;}
        throw new Exception("Explicitly excluded custody must preserve ordinary P&L scope and source binding behavior.");
    }
    static void Memberships(PortfolioValuation.Prepared portfolio,JsonElement fixture,Guid trade,Guid instrument)
    {
        var services=portfolio.Positions.Where(p=>p.InstrumentType==GasParkCustodyTicket.Product).ToArray();
        if(services.Length!=fixture.GetProperty("expectedCashBindings").GetInt32()||services.Any(p=>p.TradeId!=trade||p.Owner.Kind!=AccountingOwnerKind.Trade||p.InstrumentId!=instrument))
            throw new Exception("Conditional custody fee or independent accepted cash was duplicated or lost its exact service owner.");
        var native=portfolio.Positions.Where(p=>p.InstrumentType!=GasParkCustodyTicket.Product).ToArray();
        if(native.Any(p=>p.Owner.Kind!=AccountingOwnerKind.NativeHolding)||!native.Select(p=>(p.Owner.Id,p.BindingId)).Order().SequenceEqual(
            fixture.GetProperty("expectedNativePositions").EnumerateArray().Select(p=>(p.Id("ownerId"),p.Id("bindingId"))).Order()))
            throw new Exception("Custody must preserve independently owned customer stock exactly once and invent no provider stock or debt.");
    }
}
