using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class LifecycleSwapChecks
{
    internal static async Task Run(string path)
    {
        using var packet=JsonDocument.Parse(File.ReadAllText(path));var f=packet.RootElement;
        var manifest=f.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(f.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Lifecycle swap checks require an owned disposable cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        if(f.TryGetProperty("status",out var status))
        {
            var expire=status.GetProperty("expire").GetBoolean();
            try
            {
                var result=await database.WithIdentity("trader",null,async(c,t)=>
                {
                    var receipt=await SwapSettlementStatus.Read(c,database,status.Id("operationId"),t);
                    if(expire)await Task.Delay(6000,t);
                    return receipt;
                },default);
                if(expire||!result.GetProperty("found").GetBoolean()||result.Id("operationId")!=status.Id("operationId"))
                    throw new Exception("Retained status lost its exact first result or allowed a successor grant to extend the original read.");
            }
            catch(PostgresException error)when(expire&&error.SqlState=="42501"&&error.MessageText=="futures_margin_denied"){}
            Console.WriteLine("PASS owned retained swap status: original read admission survives transport shaping and a fresh successor request remains usable");
            return;
        }
        var seed=manifest.Frames.Single(s=>s.At==(f.TryGetProperty("frameAt",out var chosen)?chosen.GetDateTimeOffset():DateTimeOffset.Parse("2020-12-18T16:00:00Z")));
        var frame=new InputFrame(f.Id("snapshotId"),"Synthetic selected swap frame",seed.At,new(2020,12,18),[..seed.ObservationIds],
            seed.CurveContracts.ToImmutableDictionary(p=>p.Key,p=>p.Value.ToImmutableArray()));
        if(f.TryGetProperty("context",out var context))
        {
            frame=frame with{At=context.GetProperty("asOf").GetDateTimeOffset(),BusinessDate=DateOnly.Parse(context.Text("businessDate"))};
            if(f.TryGetProperty("selectedObservations",out var pins))frame=frame with{ObservationIds=[..pins.EnumerateArray().Select(p=>p.GetGuid())]};
            var selected=context.Deserialize<LifecycleReadContext>(LifecycleNative.Json)!;
            if(f.TryGetProperty("sourceViews",out var sourceClose))
            {
                await database.WithIdentity("trader",null,async(c,t)=>
                {
                    var source=(await new KnowledgeStore(manifest.CatalogId).Read(c,selected.ContextId,t)).Context;
                    await TradingContext.AdmitSource(database,c,source,selected.AsOf,t);
                    await PinnedContext.BindExact(c,manifest.CatalogId,source,t);
                    await TradingContext.Bind(c,manifest.CatalogId,source,selected.AsOf,t);
                    var recorded=await MarketCloses.Frame(c,manifest.CatalogId,sourceClose.GetGuid(),selected.ContextId,t);
                    var data=await MarketInputSelection.Root(database,c,recorded,t);
                    var target=f.GetProperty("target").Deserialize<LifecycleTarget>(LifecycleNative.Json)!;
                    var read=new FuturesReadRequest(recorded.Id,SwapStore.Convention,"retained",selected.ContextId);
                    var store=new SwapStore(database);
                    var editor=JsonSerializer.SerializeToElement(await store.Editor(c,data,recorded,target.ScopeId,null,t),LifecycleNative.Json);
                    var inspect=JsonSerializer.SerializeToElement(await store.InspectSettlement(c,data,recorded,new(read,target.ScopeId,null,"swap_due_nominal_v1","swap_completed_period_trading_pnl_v1"),t),LifecycleNative.Json);
                    var holdings=await store.Holdings(c,data,recorded,[f.Id("bookId")],[],t,trades:ImmutableHashSet.Create(target.ScopeId));
                    var pnl=(SwapPnlSide)await store.PnlRead(c,data,recorded,new(read,read,[target.ScopeId],[],"reject_contract_changes_v1","swap_due_nominal_v1","swap_completed_period_trading_pnl_v1"),t);
                    if(editor.GetProperty("state").Id("tradeId")!=target.ScopeId||inspect.GetProperty("valuation").Text("remainingValue")!="50"||
                        holdings.Rows.Length!=1||holdings.Rows[0]["presentValue"]?.ToString()!="50"||pnl.Holdings.Single().Value.RemainingValue.ToExactString()!="50")
                        throw new Exception("Retained editor, settlement, Blotter and P&L did not select the exact independent price source.");
                    return 0;
                },default);
                Console.WriteLine("PASS retained swap readers: editor, settlement, Blotter and P&L use the recorded secondary definition independently of deployment primary");return;
            }
            if(f.TryGetProperty("profileIds",out var savedProfiles))
            {
                var operations=new LifecycleOperations(database,id=>id==frame.Id?frame:throw new Exception("Unknown test frame."));
                var target=f.GetProperty("target").Deserialize<LifecycleTarget>(LifecycleNative.Json)!;
                var discovery=await database.WithIdentity("trader",null,(c,t)=>operations.Discover(c,new(selected,target),t),default);
                var offers=discovery.Actions.Where(a=>a.Action=="swap.determine").ToArray();
                var expected=savedProfiles.EnumerateArray().Select(p=>p.GetGuid()).ToHashSet();
                if(offers.Length!=expected.Count||offers.Select(a=>a.OccurrenceId).Distinct().Count()!=expected.Count||
                    !offers.Select(a=>a.Selection!.Value.Deserialize<SwapActionSelection>(LifecycleNative.Json)!.ProfileId).ToHashSet().SetEquals(expected))
                    throw new Exception("Concurrent native swap periods lost their distinct occurrence or saved profile identity.");
                foreach(var profile in discovery.SwapProfiles.Where(p=>p.Id("trade_id")==target.ScopeId))
                {
                    if(offers.Single(a=>a.Selection!.Value.Deserialize<SwapActionSelection>(LifecycleNative.Json)!.ProfileId==profile.Id("id")).OccurrenceId!=profile.Id("occurrence_id"))
                        throw new Exception("Saved profile metadata must carry the same exact native occurrence as its offer.");
                    var offer=offers.Single(a=>a.Selection!.Value.Deserialize<SwapActionSelection>(LifecycleNative.Json)!.ProfileId==profile.Id("id"));
                    if(!offer.Label.Contains("Side "+(profile.GetProperty("membership_ordinal").GetInt32()+1),StringComparison.Ordinal)||
                        !offer.Label.Contains(profile.Text("delivery_from"),StringComparison.Ordinal)||!offer.Label.Contains(profile.Text("delivery_to"),StringComparison.Ordinal))
                        throw new Exception("A saved swap occurrence must name its native side and delivery interval so another period is distinguishable.");
                }
                Console.WriteLine("PASS native multi-period swap discovery: every saved occurrence and profile remains independently selectable");return;
            }
            if(f.TryGetProperty("economicProjection",out var economic)&&economic.GetBoolean())
            {
                await database.WithIdentity("trader",null,async(c,t)=>
                {
                    var source=(await new KnowledgeStore(manifest.CatalogId).Read(c,selected.ContextId,t)).Context;
                    await PinnedContext.BindExact(c,manifest.CatalogId,source,t);
                    await using(var bind=ScheduleStore.Command(c,"SELECT security.use_trading_context($1,$2,$3)",manifest.CatalogId,frame.At,frame.BusinessDate))await bind.ExecuteNonQueryAsync(t);
                    var retainedFrame=frame with{ContextId=selected.ContextId};
                    var data=await database.Read(c,t,selectedRevision:manifest.RevisionId,observationIds:retainedFrame.ObservationIds,observationScope:ObservationReadScope.ExactPins);
                    var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(retainedFrame),selected.ContextId,SchedulePreview.ParseAt(source.KnownAt));
                    var target=f.GetProperty("target");var book=f.Id("bookId");
                    var scope=await RiskTradeScope.Resolve(c,manifest.CatalogId,[book],[target.Id("scopeId")],t);
                    var policy=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesMarginValuationPolicy.Convention,
                        SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                        FuturesOptions:FuturesOptionValuationPolicy.Release1,NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version));
                    var opening=await new PortfolioValuation(database).Prepare(c,data,retainedFrame,snapshot,source,[book],policy,[],[],t,selection:scope);
                    var request=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version,f.Text("session"),1,
                        PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                        new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,LifecycleProjectionPolicy.Nominal,
                            LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),[]);
                    var prepared=await LifecyclePortfolioProjection.Prepare(c,database,opening,request,null,t);
                    if(prepared.Projections.Length!=1||prepared.Projections[0].Evaluate("native_swap_no_shock",[],t).Values.Single().EconomicPnl.Numerator!="0"||
                        prepared.Projections[0].Components.Single().Opening.KnownPayments.Length!=1||
                        prepared.HistoryPortfolio.ConnectedMarkets.Single().ManifestId!=prepared.Projections[0].Market.GraphManifest)
                        throw new Exception("Economic native swap preparation must conserve no-shock wealth, retain future actual cash and reuse its complete market manifest.");
                    var replay=await LifecyclePortfolioProjection.Prepare(c,database,opening,request,prepared.HistoryPortfolio.ConnectedMarkets,t);
                    if(!prepared.ProductEvidence.Select(p=>p.GetRawText()).SequenceEqual(replay.ProductEvidence.Select(p=>p.GetRawText())))
                        throw new Exception("Retained economic source replay changed its exact native product evidence.");
                    await EconomicRiskNativeChecks.Verify(database,c,data,retainedFrame,
                        new(new(frame.Id,TradingBlotter.Convention,"retained",selected.ContextId),[book],[],[target.Id("scopeId")]),policy,request,prepared,t);
                    if(f.TryGetProperty("economicWorker",out var economicWorker)&&economicWorker.GetBoolean())
                        await EconomicWorkerChecks.Submit(database,c,data,retainedFrame,
                            new(new(frame.Id,TradingBlotter.Convention,"retained",selected.ContextId),[book],[],[target.Id("scopeId")]),policy,request,prepared,path,t);
                    return 0;
                },default);
                Console.WriteLine("PASS native economic swap source: exact profiles, future actual cash, retained graph replay and no-shock wealth");return;
            }
            if(f.TryGetProperty("history",out var history)&&history.GetBoolean())
            {
                var actual=await database.WithIdentity("trader",null,async(c,t)=>
                {
                    var retainedSource=(await new KnowledgeStore(manifest.CatalogId).Read(c,selected.ContextId,t)).Context;
                    var owner=new OpenEtrm.MarketData.Trading.AccountingPositionKey(f.GetProperty("target").Id("scopeId"),f.GetProperty("target").Id("bindingId"),f.Id("bookId"));
                    return await SwapKnownPaymentStore.Read(c,database,frame with{ContextId=selected.ContextId},retainedSource,owner,1,t);
                },default);
                if(actual.Payments.Length!=f.GetProperty("paymentCount").GetInt32()||actual.Payments.Any(p=>p.KnownAt>actual.KnownThrough)||
                    f.TryGetProperty("future",out var future)&&future.GetBoolean()&&!actual.Payments.Any(p=>p.EffectiveAt>frame.At))
                    throw new Exception("Knowledge-only history dropped known future cash or changed the selected source cutoff.");
                if(f.TryGetProperty("determinationCount",out var dc)&&actual.Determinations.Length!=dc.GetInt32()||
                    f.TryGetProperty("futureDeterminations",out var fd)&&fd.GetBoolean()&&!actual.Determinations.Any(d=>d.EffectiveAt>frame.At))
                    throw new Exception("Knowledge-only history dropped a source-known future actual determination.");
                Console.WriteLine("PASS native knowledge-only swap cash: source cutoff, composite allocation identity and future effective instants retained");return;
            }
            if(f.TryGetProperty("expectedError",out var refused))
            {
                try{await Action(database,frame,f,selected);throw new Exception("Expected "+refused.GetString());}
                catch(Exception error)when((error is PostgresException pg?pg.MessageText:error is MarketDataException market?market.Code:"")==refused.GetString())
                {Console.WriteLine("PASS explicit swap lifecycle refusal: "+refused.GetString());return;}
            }
            await Action(database,frame,f,selected);return;
        }
        var store=new SwapStore(database);
        var body=f.GetProperty("booking").Deserialize<SwapWriteRequest>(LifecycleNative.Json)!;
        var retained=new RetainedScheduleStore(database);
        if(f.TryGetProperty("expectedBookingError",out var bookingError))
        {
            try
            {
                await database.WithIdentity("trader","BookTrade",async(c,t)=>await store.PrepareScheduleTerms(c,
                    await database.Read(c,t,observationIds:frame.ObservationIds,observationScope:ObservationReadScope.ExactPins),frame,body,t),default);
            }
            catch(MarketDataException error)when(error.Code==bookingError.GetString())
            {Console.WriteLine("PASS unsupported native swap composition refuses before financial enrollment: "+error.Code);return;}
            throw new Exception("Unsupported swap composition was accepted.");
        }
        var prepared=await database.WithIdentity("trader","BookTrade",async(c,t)=>
        {
            var terms=await store.PrepareScheduleTerms(c,await database.Read(c,t,observationIds:frame.ObservationIds,observationScope:ObservationReadScope.ExactPins),frame,body,t);
            return(Terms:terms,Command:await store.ScheduleCommand(c,frame,body,terms.Reference,t));
        },default);
        await retained.Stage("trader",body.OperationId,prepared.Command,"BookTrade",null,null,prepared.Terms.Generate(default),default);
        var receipt=await database.WithIdentity("trader","BookTrade",async(c,t)=>
        {
            await retained.Lock(c,body.OperationId,t);
            var value=await store.Save(c,await database.Read(c,t,observationIds:frame.ObservationIds,observationScope:ObservationReadScope.ExactPins),frame,body,t);
            var instrument=await retained.Publish(c,body.OperationId,t);
            await store.VerifyPublishedPrograms(c,await database.Read(c,t,observationIds:frame.ObservationIds,observationScope:ObservationReadScope.ExactPins),frame,body.OperationId,instrument,t);
            return value;
        },default);
        Console.WriteLine("LIFECYCLE_SWAP_BOOKED "+receipt.GetRawText());
    }
    private static async Task Action(PreviewDatabase database,InputFrame frame,JsonElement fixture,LifecycleReadContext context)
    {
        var operations=new LifecycleOperations(database,id=>id==frame.Id?frame:throw new Exception("Unknown test frame."));
        LifecycleActionRequest request;
        Guid? expectedOccurrence=null;
        if(fixture.TryGetProperty("reviewedRequest",out var prior))request=prior.Deserialize<LifecycleActionRequest>(LifecycleNative.Json)! with{OperationId=Guid.NewGuid()};
        else
        {
            var target=fixture.GetProperty("target").Deserialize<LifecycleTarget>(LifecycleNative.Json)!;
            var discovery=await database.WithIdentity("trader",null,(c,t)=>operations.Discover(c,new(context,target),t),default);
            var action=fixture.TryGetProperty("action",out var code)?code.GetString():"swap.determine";
            var offer=discovery.Actions.Single(a=>a.Action==action&&(!fixture.TryGetProperty("membershipOrdinal",out var membership)||
                a.Selection!.Value.GetProperty("determination").GetProperty("preview").GetProperty("membershipOrdinal").GetInt32()==membership.GetInt32()));
            if(fixture.TryGetProperty("checkReversalChoice",out var reversalChoice)&&
                !offer.InputChoices.GetValueOrDefault("paymentReversal",[]).Any(c=>JsonSerializer.Deserialize<JsonElement>(c.Value).Id("reversesId")==reversalChoice.GetGuid()))
                throw new Exception("Payment discovery omitted the exact original native reversal choice.");
            expectedOccurrence=offer.OccurrenceId;
            if(offer.Selection is not {} selection||offer.PolicyId is not {} policy||offer.PolicyRevision is not {} revision)
                throw new Exception("The selected native swap profile has no executable closed selection.");
            request=new(Guid.NewGuid(),context,target,offer.Action,policy,revision,false,new('0',64),selection);
            if(fixture.TryGetProperty("payment",out var payment))
            {
                var chosen=selection.Deserialize<SwapActionSelection>(LifecycleNative.Json)!;
                request=request with{Selection=JsonSerializer.SerializeToElement(chosen with{Payment=payment.Deserialize<SwapPaymentActionSelection>(LifecycleNative.Json)},LifecycleNative.Json)};
            }
            if(fixture.TryGetProperty("net",out var net))
            {
                var chosen=selection.Deserialize<SwapNetActionSelection>(LifecycleNative.Json)!;
                request=request with{Selection=JsonSerializer.SerializeToElement(chosen with{Net=net.Deserialize<SwapNetActionTerms>(LifecycleNative.Json)!},LifecycleNative.Json)};
            }
            if(request.Action=="swap.determine"&&fixture.TryGetProperty("correctionDueAt",out var due))
            {
                var chosen=selection.Deserialize<SwapActionSelection>(LifecycleNative.Json)!;
                request=request with{Selection=JsonSerializer.SerializeToElement(chosen with{Determination=chosen.Determination! with{CorrectionDueAt=due.GetString()}},LifecycleNative.Json)};
            }
        }
        var reviewed=await database.WithIdentity("trader",null,(c,t)=>operations.Preview(c,request,t),default);
        if(expectedOccurrence is {} occurrence&&reviewed.Offer.OccurrenceId!=occurrence)
            throw new Exception("The reviewed action must retain the exact discovered native payment occurrence.");
        request=request with{PreviewHash=reviewed.Hash};
        var first=await database.WithIdentity("trader",null,(c,t)=>operations.Execute(c,request,t),default);
        var restarted=new LifecycleOperations(database,_=>throw new Exception("The original frame was evicted."));
        var recovered=await database.WithIdentity("trader",null,(c,t)=>restarted.Execute(c,request,t),default);
        if(!JsonElement.DeepEquals(first.Outcome,recovered.Outcome))throw new Exception("An evicted working frame prevented first-outcome recovery.");
        var retry=await database.WithIdentity("trader",null,(c,t)=>operations.Execute(c,request,t),default);
        if(!JsonElement.DeepEquals(first.Outcome,retry.Outcome))throw new Exception("A swap lifecycle retry lost its first outcome.");
        var acknowledged=await database.WithIdentity("trader",null,(c,t)=>operations.Execute(c,request with{OperationId=Guid.NewGuid()},t),default);
        var effect=request.Action is "swap.payment" or "swap.net"?"paymentId":"determinationId";
        if(acknowledged.Outcome.Id(effect)!=first.Outcome.Id(effect))throw new Exception("A financial snapshot acknowledgement created another economic effect.");
        if(fixture.TryGetProperty("expectedVersion",out var version)&&acknowledged.Outcome.GetProperty("version").GetInt32()!=version.GetInt32()||
            fixture.TryGetProperty("expectedCurrentVersion",out var head)&&acknowledged.Outcome.GetProperty("currentVersion").GetInt32()!=head.GetInt32())
            throw new Exception("A swap snapshot acknowledgement changed its accepted version or concealed the current financial head.");
        Console.WriteLine("LIFECYCLE_SWAP_REVIEWED "+JsonSerializer.Serialize(request,LifecycleNative.Json));
        Console.WriteLine("LIFECYCLE_SWAP_OUTCOME "+first.Outcome.GetRawText());
        Console.WriteLine("PASS owned swap lifecycle adapter: exact selected source, product-native determination and same-snapshot acknowledgement");
    }
}
