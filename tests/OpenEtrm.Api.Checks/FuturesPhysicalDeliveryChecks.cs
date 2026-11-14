using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class FuturesPhysicalDeliveryChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Physical final checks require an owned disposable cluster.");
        await using var runtime=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false}.ConnectionString);
        await using var database=new PreviewDatabase(manifest,runtime);
        await using var connection=await runtime.OpenConnectionAsync();await using var transaction=await connection.BeginTransactionAsync();
        using var owned=database.OwnReadTransaction(connection,transaction);
        await using(var identity=ScheduleStore.Command(connection,"SELECT security.use_context($1)",fixture.Id("token")))await identity.ExecuteNonQueryAsync();
        var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,fixture.Id("contextId"),default)).Context;
        await PinnedContext.BindExact(connection,manifest.CatalogId,context,default);
        var at=fixture.Instant("at");await TradingContext.Bind(connection,manifest.CatalogId,context,at,default);
        var pins=fixture.GetProperty("pins").EnumerateArray().Select(v=>v.GetGuid()).ToImmutableArray();
        var data=await database.Read(connection,default,selectedRevision:manifest.RevisionId,observationIds:pins,includeCalendarDays:false,observationScope:ObservationReadScope.ExactPins);
        var frame=new InputFrame(Guid.NewGuid(),"Synthetic physical final",at,DateWorkspace.Date(context.EffectiveOn),pins,
            ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("nymex_ng",[fixture.Id("markContract")]),context.ContextId);
        var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
        var futures=new FuturesStore(database);var books=await futures.Books(connection,default);
        var selected=await futures.ReadTrades(connection,data,books,[],[],default,trades:ImmutableHashSet.Create(fixture.Id("tradeId")));
        var trade=selected.Trades.Single();var binding=trade.Instruments.Single();var market=FuturesExecution.Require(binding).MarketBinding;
        var actual=await FuturesMarginStore.Read(connection,manifest.CatalogId,data,frame,snapshot,trade,binding.Id,default);
        if(actual.PhysicalAdmission is {} admission)database.RegisterCommitAdmission(connection,admission);
        if(actual.State.Status!=FuturesMarginStatus.FinalMarginDetermined||actual.State.AcceptedDelivery?.PositionId!=fixture.Id("positionId")||
            actual.State.SettledBasis.ToExactString()!="6"||actual.CashBindings.Length!=2)
            throw new Exception("Native physical final lost its exact corrected basis, atomic obligation or margin cash.");
        var held=await GasOwnedHoldings.Read(connection,database,frame,snapshot,[trade.BookId],default,[(data,frame)],true);
        var residual=held.Positions.Single();
        if(residual.Position.Owner!=new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,fixture.Id("positionId"))||residual.Position.TradeId!=Guid.Empty||
            residual.Instrument is not GasPhysicalDeliveryObligationInstrument obligation||obligation.FinalPrice.ToExactString()!="6"||
            obligation.Id!=actual.State.AcceptedDelivery.Plan.Obligation.Id||residual.Quantity!=binding.Quantity||!held.Fees.IsEmpty)
            throw new Exception("Physical continuation must be a separately owned mark-minus-principal residual, with no invented stock or consideration.");
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
            {InstrumentDiscounting=ImmutableDictionary<Guid,ISideDiscounting>.Empty.Add(obligation.Id,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Explicit))};
        var position=new RiskPosition(residual.Position.Owner,residual.Position.BindingId,trade.BookId,obligation.Id,residual.Quantity,
            new(new Money(default,obligation.Currency),residual.Quantity.Specification),new(default,obligation.Currency));
        var value=RiskEngine.CalculateExact(RiskEngine.Prepare(data.Model,snapshot,[new([obligation],[position],setup)]),[],RiskEngine.ExactNumericalPolicy)
            .Groups.SelectMany(g=>g.BaseValues).Select(v=>v.Value).Aggregate((a,b)=>a.Add(b));
        var expected=snapshot.Selections.Single(s=>s.Observation.Key.Id==fixture.Id("mark")).Observation.Value-ExactDecimal.Parse("6");
        var oracle=ExactUnitPrice.Parse(expected.ToString(),obligation.FinalPrice.Unit).ExactTimes(residual.Quantity);
        if(value!=oracle)throw new Exception("Ordinary physical residual PV double-counted principal or lost quantity scaling.");
        var prepared=await GasOwnedPortfolio.Prepare(connection,database,held,new(GasOwnedValuationPolicy.Version,GasOwnedValuationPolicy.WholeBooks,
            GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version),default);
        if(prepared.Length!=1||prepared.Single().Portfolio.Positions.Length!=1||prepared.Single().DiscountInputs.Length!=2)
            throw new Exception("The whole-book adapter must prepare both exact physical and principal carrying inputs once.");
        if(prepared.Single().Evidence.Any(e=>string.IsNullOrWhiteSpace(e.TradeReference)||!e.InstrumentReference.StartsWith("TR-",StringComparison.Ordinal)))
            throw new Exception("Native physical delivery labels must retain their saved account and exact causal future business reference.");
        _=RiskEngine.CalculateExact(RiskEngine.Prepare(data.Model,snapshot,[prepared.Single().Portfolio]),[],RiskEngine.ExactNumericalPolicy);
        await owned.Commit(default);
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var operations=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var request=fixture.GetProperty("profileRequest").Deserialize<LifecycleDeliveryProfileSave>(LifecycleNative.Json)!;
        var replay=await operations.WithIdentity("trader",null,(c,ct)=>LifecyclePhysicalSettings.Save(c,operations,request,
            ()=>throw new InvalidOperationException("Evicted host frame consulted"),ct),default);
        if(replay.Outcome.Id("profileId")!=request.Profile.Id||replay.Outcome.Text("revision")!="1")
            throw new Exception("Physical settings replay lost its first result after the host frame was evicted.");
        try
        {
            await operations.WithIdentity("trader",null,(c,ct)=>LifecyclePhysicalSettings.Save(c,operations,request with{OperationId=Guid.NewGuid()},
                ()=>throw new InvalidOperationException("Evicted host frame consulted"),ct),default);
            throw new Exception("A fresh physical settings command bypassed its host source selection.");
        }
        catch(InvalidOperationException error)when(error.Message=="Evicted host frame consulted"){}
        var action=fixture.GetProperty("marginRequest").Deserialize<LifecycleActionRequest>(LifecycleNative.Json)!;
        var handler=new FuturesLifecycleActions(operations,_=>{});
        var reviewed=await operations.WithIdentity("trader",null,(c,ct)=>handler.Preview(c,action,ct),default);
        var principal=reviewed.Details.GetProperty("physicalPrincipal");
        var marginRows=reviewed.Details.GetProperty("marginRows");
        if(marginRows.GetArrayLength()!=1||marginRows[0].Text("variation")!="60000"||marginRows[0].Text("currency")!="usd")
            throw new Exception("The futures review must expose the independently known rounded native payment and currency as exact display text.");
        if(principal.Text("finalPrice")!="6"||principal.GetProperty("performances").GetArrayLength()!=0)
            throw new Exception("The shared final review omitted its exact physical principal continuation.");
        action=action with{PreviewHash=reviewed.Hash};
        var selection=action.Selection.Deserialize<FuturesMarginActionSelection>(LifecycleNative.Json)!;
        var nativeRequest=JsonSerializer.SerializeToElement(new{action.OperationId,tradeId=action.Target.ScopeId,bindingId=action.Target.BindingId,
            selection.ExpectedRevision,selection.ExpectedMarginRevision,action.Context.ContextId,asOf=LifecycleNative.Instant(action.Context.AsOf),
            selection.ThroughOccurrence,implementation=FuturesMarginCoordinator.Version,selection.CorrectionDue,action.PreviewHash},LifecycleNative.Json);
        foreach(var wrong in new[]{action with{PreviewHash=new string('b',64)},action with{Selection=JsonSerializer.SerializeToElement(selection with{ExpectedMarginRevision=4},LifecycleNative.Json)}})
        {
            try
            {
                await operations.WithIdentity("trader",null,async(c,ct)=>
                {
                    var native=await FuturesMarginCoordinator.Determine(c,operations,nativeRequest,ct);
                    var submitted=wrong.Selection.Deserialize<FuturesMarginActionSelection>(LifecycleNative.Json)!;
                    var body=LifecycleNative.Body(wrong,submitted);
                    return await LifecycleNative.Read(c,"SELECT lifecycle.record_native_action($1,$2::jsonb,$3::jsonb,$4)::text",ct,
                        manifest.CatalogId,body.GetRawText(),native.Outcome.GetRawText(),native.AuthorityUntil);
                },default);
                throw new Exception("A shared action receipt misrepresented the exact accepted native selection or review.");
            }
            catch(Npgsql.PostgresException error)when(error.MessageText=="lifecycle_native_receipt"){}
        }
        var result=await operations.WithIdentity("trader",null,(c,ct)=>handler.Execute(c,action,ct),default);
        var evicted=new FuturesLifecycleActions(operations,_=>throw new InvalidOperationException("Evicted financial source consulted"));
        var retried=await operations.WithIdentity("trader",null,(c,ct)=>evicted.Execute(c,action,ct),default);
        if(result.Outcome.GetRawText()!=retried.Outcome.GetRawText()||result.Outcome.Id("operationId")!=action.OperationId)
            throw new Exception("The combined native continuation lost its actor-owned first outcome after frame eviction.");
        Console.WriteLine("PASS shared physical margin action: complete principal review, atomic native receipt and evicted-source retry");
        Console.WriteLine("PASS native physical final ordinary PV: corrected principal, independent owner, exact unit scaling and both instant carrying inputs");
    }
}
