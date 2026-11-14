using OpenEtrm.MarketData.CustomCurves;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;

internal static class CatalogueTradingChecks
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal static async Task Run(string path)
    {
        using var fixture=JsonDocument.Parse(File.ReadAllText(path));var root=fixture.RootElement;
        var manifest=root.GetProperty("manifest").Deserialize<PreviewManifest>(Json)!;
        using var counts=new MetadataReadCounter();
        using var logging=LoggerFactory.Create(builder=>builder.SetMinimumLevel(LogLevel.Information).AddProvider(counts));
        var builder=new NpgsqlDataSourceBuilder(root.GetProperty("connection").GetString()!);builder.UseLoggerFactory(logging);
        await using var runtime=builder.Build();
        await using var database=new PreviewDatabase(manifest,runtime);
        await using var connection=await runtime.OpenConnectionAsync();
        await using var transaction=await connection.BeginTransactionAsync();
        await using(var use=ScheduleStore.Command(connection,"SELECT security.use_context($1)",root.GetProperty("token").GetGuid()))
            await use.ExecuteNonQueryAsync();
        var context=await new KnowledgeStore(manifest.CatalogId).Read(connection,root.GetProperty("context").GetGuid(),default);
        await MarketContextStore.Bind(connection,manifest.CatalogId,context.Context,default);
        var seed=root.GetProperty("frame").Deserialize<PreviewSeedFrame>(Json)!;
        var frame=new InputFrame(Guid.NewGuid(),"Synthetic catalogue check",seed.At,DateWorkspace.Date(context.Context.EffectiveOn),
            [..seed.ObservationIds],seed.CurveContracts.ToImmutableDictionary(p=>p.Key,p=>p.Value.ToImmutableArray()),context.Context.ContextId);
        frame=await MarketCloses.Frame(connection,manifest.CatalogId,root.GetProperty("close").GetGuid(),context.Context.ContextId,default);
        if(frame.SourceInputs?.Count!=2)throw new Exception("The recorded input matrix did not retain both exact sources.");
        await using(var trading=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",manifest.CatalogId,frame.At))
            await trading.ExecuteNonQueryAsync();
        var data=await database.Read(connection,default,observationIds:frame.ObservationIds,observationScope:ObservationReadScope.ExactPins);
        if(data.DefinitionRevision.RevisionId!=root.GetProperty("original").GetGuid())
            throw new Exception("Deployment configuration replaced context-selected catalogue membership.");
        var references=await database.ReferenceScope(connection,"futures_products",default);
        if(references.Definition!=data.DefinitionRevision.RevisionId||references.Revision!=data.ReferenceRevision)
            throw new Exception("Reference reads used deployment configuration instead of the context's published table source.");
        var physical=await PhysicalMarketSelection.Gas.Read(database,connection,frame,default);
        if(physical.Data.DefinitionRevision!=data.DefinitionRevision||!physical.Frame.ObservationIds.SequenceEqual(frame.ObservationIds))
            throw new Exception("Physical choices used deployment routing instead of their dated published role and exact selected inputs.");
        var added=frame.SourceInputs!.Values.Single(p=>p.Source.DefinitionRevisionId!=data.DefinitionRevision.RevisionId).Source;
        var volatility=await VolSurfaceStore.SelectedInputs(connection,database,frame,default);
        if(volatility.Source!=added||volatility.Frame.OriginCloseId!=frame.OriginCloseId||
            volatility.Frame.ContextId!=frame.ContextId||!frame.SourceInputs[added.DefinitionRevisionId].Matches(volatility.Frame))
            throw new Exception("Volatility calibration must select the published product source and its exact recorded close pins.");
        var retainedPhysical=await PhysicalMarketSelection.Gas.PortfolioSource(database,connection,frame,added.DefinitionRevisionId,
            [data.ReferenceRevision],[(data,frame)],false,default);
        if(retainedPhysical.Data.DefinitionRevision.RevisionId!=added.DefinitionRevisionId)
            throw new Exception("Retained physical terms required a deployment allow-list instead of their native exact source.");
        var metadataScope=new MarketInputReadScope(database,connection,frame.ContextId);
        var before=counts.Curves;
        for(var sample=0;sample<128;sample++)
        {
            var own=await MarketInputSelection.Root(database,connection,frame,default,reads:metadataScope);
            _=await MarketInputSelection.Resolve(database,connection,frame,added,[(own,frame)],false,default,metadataScope);
        }
        if(counts.Curves-before!=2)
            throw new Exception($"Repeated exact input reads reloaded source metadata: expected two source reads, got {counts.Curves-before}.");
        var ppas=new PpaStore(database,new TouProductStore(manifest.CatalogId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId));
        await ppas.Recheck(connection,frame,default);
        var ppaDefinitions=await ppas.Definitions(connection,frame,true,default);
        if(ppas.SelectedManifest(frame).RevisionId!=data.DefinitionRevision.RevisionId||
            ppas.ReferenceFor(frame)!=data.ReferenceRevision.CatalogId||ppaDefinitions.DefinitionRevision!=data.DefinitionRevision||
            !ppaDefinitions.Observations.Select(o=>o.Id("id")).ToHashSet().SetEquals(frame.ObservationIds))
            throw new Exception("PPA source reads or authority rechecks substituted deployment coordinates for the retained frame.");
        var secondaryPpa=await ppas.SelectInputs(connection,frame,added.DefinitionRevisionId,default);
        if(secondaryPpa.Data.DefinitionRevision.RevisionId!=added.DefinitionRevisionId||secondaryPpa.Frame.Source!=added||
            !frame.SourceInputs![added.DefinitionRevisionId].Matches(secondaryPpa.Frame))
            throw new Exception("Explicit PPA terms did not project their own selected source inputs.");
        var actual=JsonSerializer.SerializeToElement(await new FuturesStore(database).Holdings(connection,data,frame,default),Json);
        var holdings=actual.GetProperty("holdings").EnumerateArray().ToArray();
        var expected=root.GetProperty("expected").EnumerateArray().ToArray();
        if(holdings.Length!=expected.Length)throw new Exception($"A catalogue addition removed or duplicated a holding: expected {expected.Length}, got {holdings.Length}.");
        foreach(var item in expected)
        {
            var held=holdings.Single(h=>h.GetProperty("tradeId").GetGuid()==item.GetProperty("trade").GetGuid());
            if(held.GetProperty("definitionRevisionId").GetGuid()!=item.GetProperty("definition").GetGuid()||
                held.GetProperty("mark").GetString()!=item.GetProperty("mark").GetString()||held.GetProperty("unavailableReason").ValueKind!=JsonValueKind.Null)
                throw new Exception("A holding changed exact terms or selected another source's price.");
        }
        if(await CurveVersionInputs.Resolve(database,connection,root.GetProperty("original").GetGuid(),default)!=root.GetProperty("original").GetGuid())
            throw new Exception("The current deployment blocked a retained graph's exact source.");
        if(await CurveVersionInputs.Resolve(database,connection,null,default)!=root.GetProperty("original").GetGuid())
            throw new Exception("New graph inputs used deployment configuration instead of the context-selected catalogue.");
        await using(var guardConnection=await runtime.OpenConnectionAsync())
        await using(var guardTransaction=await guardConnection.BeginTransactionAsync())
        {
            await using(var use=ScheduleStore.Command(guardConnection,"SELECT security.use_context($1)",root.GetProperty("shapeToken").GetGuid()))
                await use.ExecuteNonQueryAsync();
            var reads=new MarketReads(database);var pin=PinnedContext.Retained(context.Context,frame);
            await pin.Bind(guardConnection,manifest.CatalogId,default);
            var selectedCatalogue=await new CatalogueStore(manifest.CatalogId).ReadBound(guardConnection,default,frame.Time.Date);
            try
            {
                await reads.Curves(guardConnection,selectedCatalogue,frame with{SourceInputs=frame.SourceInputs!.Remove(added.DefinitionRevisionId)},default);
                throw new Exception("Navigation accepted a recorded close with an omitted exact source part.");
            }
            catch(OpenEtrm.MarketData.MarketDataException error)when(error.Code=="market_inputs"){}
        }
        await using(var shapeConnection=await runtime.OpenConnectionAsync())
        await using(var shapeTransaction=await shapeConnection.BeginTransactionAsync())
        {
            await using(var use=ScheduleStore.Command(shapeConnection,"SELECT security.use_context($1)",root.GetProperty("shapeToken").GetGuid()))
                await use.ExecuteNonQueryAsync();
            var reads=new MarketReads(database);var pin=PinnedContext.Retained(context.Context,frame);
            var shapes=await reads.CurveShapes(shapeConnection,pin,["future","additional_future"],default);
            if(shapes[0].Points is not {Length:>0}||shapes[1].UnavailableReason!="curve_not_selected")
                throw new Exception("An unavailable new curve broke another selected-close card or became a signed close root.");
            var catalogue=await new CatalogueStore(manifest.CatalogId).ReadBound(shapeConnection,default,frame.Time.Date);
            var rateChoices=await MarketInputSelection.SelectedRates(database,shapeConnection,frame,catalogue,default);
            var rateChoice=rateChoices.Single(c=>c.Code=="interest_rates");
            if(rateChoice.DefinitionRevisionId!=data.DefinitionRevision.RevisionId)
                throw new Exception("A retained duplicate rate replaced the published generic rate owner.");
            var navigation=await reads.Curves(shapeConnection,catalogue,frame,default);
            if(navigation.Single(c=>c.Code=="interest_rates").Source!=rateChoice.Source)
                throw new Exception("Navigation disagrees with the exact published rate owner.");

        }
        await using(var rateConnection=await runtime.OpenConnectionAsync())
        await using(var rateTransaction=await rateConnection.BeginTransactionAsync())
        {
            await using(var use=ScheduleStore.Command(rateConnection,"SELECT security.use_context($1)",root.GetProperty("shapeToken").GetGuid()))
                await use.ExecuteNonQueryAsync();
            var ownerContext=await new KnowledgeStore(manifest.CatalogId).Read(rateConnection,root.GetProperty("rateOwnerContext").GetGuid(),default);
            var ownerFrame=frame with{ContextId=ownerContext.Context.ContextId};
            var read=await new MarketReads(database).Evaluate(rateConnection,PinnedContext.Retained(ownerContext.Context,ownerFrame),"interest_rates",default);
            var result=JsonSerializer.SerializeToElement(read.Result,Json);
            if(result.GetProperty("points")[0].GetProperty("mark").GetString()!="0.035")
                throw new Exception("A published rate ownership change fell back to primary-source marks.");
            var packet=JsonSerializer.SerializeToElement(read,Json);
            if(!packet.TryGetProperty("inputSelection",out var selectedInputs)||selectedInputs.GetProperty("snapshotId").GetGuid()!=frame.Id||
                selectedInputs.GetProperty("source").Deserialize<CatalogueSource>(Json)!=added||selectedInputs.GetProperty("correctionsSupported").GetBoolean()||
                !selectedInputs.GetProperty("observationIds").EnumerateArray().Select(id=>id.GetGuid()).ToHashSet()
                    .SetEquals(frame.SourceInputs![added.DefinitionRevisionId].ObservationIds))
                throw new Exception("Price-source presentation must carry the evaluated source's exact pins, not the primary close pins.");
            var catalogue=await new CatalogueStore(manifest.CatalogId).ReadBound(rateConnection,default,frame.Time.Date);
            var ownerRates=await MarketInputSelection.SelectedRates(database,rateConnection,ownerFrame,catalogue,default);
            if(ownerRates.Single(c=>c.Code=="interest_rates").DefinitionRevisionId!=root.GetProperty("rateOwnerRevision").GetGuid())
                throw new Exception("The rate owner did not follow the selected catalogue knowledge.");
        }
        await using(var rateCalendarConnection=await runtime.OpenConnectionAsync())
        await using(var rateCalendarTransaction=await rateCalendarConnection.BeginTransactionAsync())
        {
            await using(var use=ScheduleStore.Command(rateCalendarConnection,"SELECT security.use_context($1)",root.GetProperty("shapeToken").GetGuid()))
                await use.ExecuteNonQueryAsync();
            var ownerContext=await new KnowledgeStore(manifest.CatalogId).Read(rateCalendarConnection,root.GetProperty("rateOwnerContext").GetGuid(),default);
            var ownerFrame=frame with{ContextId=ownerContext.Context.ContextId};
            var dates=await MarketDates.Read(database,rateCalendarConnection,PinnedContext.Retained(ownerContext.Context,ownerFrame),[ownerFrame],
                new("interest_rates","UTC",frame.Close!.Session),default);
            if(dates.Dates.Length!=1||dates.Dates[0].Frames.Single().Id!=frame.Id)
                throw new Exception("Rate date availability disagrees with the explicit published owner and exact selected inputs.");
        }
        await using(var missingRateConnection=await runtime.OpenConnectionAsync())
        await using(var missingRateTransaction=await missingRateConnection.BeginTransactionAsync())
        {
            await using(var use=ScheduleStore.Command(missingRateConnection,"SELECT security.use_context($1)",root.GetProperty("shapeToken").GetGuid()))
                await use.ExecuteNonQueryAsync();
            var ownerContext=await new KnowledgeStore(manifest.CatalogId).Read(missingRateConnection,root.GetProperty("rateOwnerContext").GetGuid(),default);
            var working=frame with{OriginCloseId=null,Close=null,ContextId=ownerContext.Context.ContextId,
                SourceInputs=frame.SourceInputs!.Remove(added.DefinitionRevisionId)};
            try
            {
                await new MarketReads(database).Evaluate(missingRateConnection,PinnedContext.Retained(ownerContext.Context,working),"interest_rates",default);
                throw new Exception("A missing published rate owner silently selected another retained source.");
            }
            catch(OpenEtrm.MarketData.MarketDataException error)when(error.Code=="market_inputs_missing"){}
        }
        await using(var calendarConnection=await runtime.OpenConnectionAsync())
        await using(var calendarTransaction=await calendarConnection.BeginTransactionAsync())
        {
            await using(var use=ScheduleStore.Command(calendarConnection,"SELECT security.use_context($1)",root.GetProperty("shapeToken").GetGuid()))
                await use.ExecuteNonQueryAsync();
            var pin=PinnedContext.Retained(context.Context,frame);
            var dates=await MarketDates.Read(database,calendarConnection,pin,[frame],
                new("future","UTC",frame.Close!.Session),default);
            if(dates.Dates.Length!=1||dates.Dates[0].Frames.Single().Id!=frame.Id)
                throw new Exception("Date availability substituted deployment metadata for the recorded source.");
        }
        await using(var graphConnection=await runtime.OpenConnectionAsync())
        await using(var graphTransaction=await graphConnection.BeginTransactionAsync())
        {
            await using(var use=ScheduleStore.Command(graphConnection,"SELECT security.use_context($1)",root.GetProperty("shapeToken").GetGuid()))
                await use.ExecuteNonQueryAsync();
            var template=CustomCurves.Templates(data).Select(t=>JsonSerializer.SerializeToElement(t,Json)).Single(t=>t.GetProperty("code").GetString()=="ng_prompt")
                .GetProperty("definition").Deserialize<RecipeDefinition>(Json)!;
            var graph=template with{Nodes=[..template.Nodes.Select(n=>n.Kind=="source"?n with{Parameters=n.Parameters.SetItem("curve","additional_future")}:n)]};
            var earlier=await MarketCloses.Frame(graphConnection,manifest.CatalogId,root.GetProperty("earlierClose").GetGuid(),null,default);
            var dates=await MarketDates.Read(database,graphConnection,PinnedContext.Current(),[earlier,frame with{ContextId=null}],
                new(null,"UTC",frame.Close!.Session,graph),default);
            if(dates.Dates.Length!=1||dates.Dates[0].Frames.Single().Id!=frame.Id||
                !dates.Unavailable.Any(d=>d.Date=="2020-12-17"&&d.Reason=="market_inputs_missing"))
                throw new Exception("A missing secondary source on one calendar candidate hid another complete selected source.");
        }
        await using(var historyConnection=await runtime.OpenConnectionAsync())
        await using(var historyTransaction=await historyConnection.BeginTransactionAsync())
        {
            await using(var use=ScheduleStore.Command(historyConnection,"SELECT security.use_context($1)",root.GetProperty("shapeToken").GetGuid()))
                await use.ExecuteNonQueryAsync();
            var earlier=await MarketCloses.Frame(connection,manifest.CatalogId,root.GetProperty("earlierClose").GetGuid(),frame.ContextId,default);
            var contract=frame.SourceInputs![added.DefinitionRevisionId].CurveContracts["additional_future"][0];
            var samples=await new MarketReads(database).History(historyConnection,PinnedContext.Retained(context.Context,frame),[earlier,frame],
                "additional_future",contract,null,HistoryRevisionPolicy.AsKnownAtCutoff,default);
            var expectedPrice=expected.Single(e=>e.GetProperty("definition").GetGuid()==added.DefinitionRevisionId).GetProperty("mark").GetString();
            if(samples.Length!=2||samples[0].UnavailableReason!="market_inputs_missing"||samples[1].Value!=expectedPrice||
                samples[1].ObservationIds.Any(id=>!frame.SourceInputs[added.DefinitionRevisionId].ObservationIds.Contains(id)))
                throw new Exception("Curve history borrowed another source's inputs or hid a complete later sample.");
        }
        await using(var datesConnection=await runtime.OpenConnectionAsync())
        await using(var datesTransaction=await datesConnection.BeginTransactionAsync())
        {
            await using(var use=ScheduleStore.Command(datesConnection,"SELECT security.use_context($1)",root.GetProperty("shapeToken").GetGuid()))
                await use.ExecuteNonQueryAsync();
            var pin=PinnedContext.Retained(context.Context,frame);
            var latest=await MarketDates.LastCloses(database,datesConnection,pin,[frame],new(frame.Close.Session),default);
            if(!latest.Curves.Any(c=>c.Curve=="future"&&c.Id==frame.Id))
                throw new Exception("Last closes lost an ordinary retained curve after a catalogue addition.");
        }
        await using(var inputConnection=await runtime.OpenConnectionAsync())
        await using(var inputTransaction=await inputConnection.BeginTransactionAsync())
        {
            await using(var use=ScheduleStore.Command(inputConnection,"SELECT security.use_context($1)",root.GetProperty("shapeToken").GetGuid()))
                await use.ExecuteNonQueryAsync();
            var inspected=await new MarketReads(database).InspectCurveShapes(inputConnection,PinnedContext.Retained(context.Context,frame),
                [new("future",data.DefinitionRevision.RevisionId),new("additional_future",added.DefinitionRevisionId)],default);
            if(inspected[0].DefinitionRevisionId!=data.DefinitionRevision.RevisionId||inspected[1].DefinitionRevisionId!=added.DefinitionRevisionId||
                inspected.Any(s=>s.Points is not {Length:>0}))
                throw new Exception("Exact retained input inspection substituted a primary revision or expanded root membership.");
        }
        var rates=await MarketInputSelection.ForCurve(database,connection,frame,"interest_rates",default);
        if(rates.Data.DefinitionRevision!=data.DefinitionRevision||rates.Frame.Source!=frame.Source)
            throw new Exception("Rate inspection left the recorded primary source or relied on commodity signed-root membership.");
        _=await RateCurveResolver.Resolve(connection,rates.Data,rates.Frame,"interest_rates",default);
        var pricedChoices=JsonSerializer.SerializeToElement(await new FuturesStore(database).Catalog(connection,data,frame,default),RiskJobs.Json)
            .GetProperty("contracts").EnumerateArray().ToArray();
        if(!pricedChoices.Any(c=>c.GetProperty("definitionRevisionId").GetGuid()==added.DefinitionRevisionId))
            throw new Exception("New futures choices did not include the added ordinary catalogue member.");
        var choice=pricedChoices.First(c=>c.GetProperty("definitionRevisionId").GetGuid()==added.DefinitionRevisionId);
        var draft=new FuturesDraftRequest(new(frame.Id,manifest.FuturesConvention,"retained",context.Context.ContextId),
            choice.GetProperty("curve").GetString()!,choice.GetProperty("id").GetGuid(),"1","3",added.DefinitionRevisionId);
        var draftValue=JsonSerializer.SerializeToElement(await new FuturesStore(database).PriceDraft(connection,data,frame,draft,default),RiskJobs.Json);
        var addedMark=root.GetProperty("expected").EnumerateArray().Single(e=>e.GetProperty("definition").GetGuid()==added.DefinitionRevisionId).GetProperty("mark").GetString();
        if(draftValue.GetProperty("mark").GetString()!=addedMark)throw new Exception("The added contract's draft silently used the primary source price.");
        var calculator=new PortfolioRiskPreview(database);
        var readContext=new FuturesReadRequest(frame.Id,TradingBlotter.Convention,"retained",context.Context.ContextId);
        Guid book=Guid.Parse("d1000000-0000-0000-0000-000000000001");
        var riskCatalog=JsonSerializer.SerializeToElement(await calculator.Catalog(connection,data,frame,new(readContext,[book],[]),default),RiskJobs.Json);
        var target=riskCatalog.GetProperty("scenarioTargets")[0].GetProperty("factor").Deserialize<CurveRiskAxisRequest>(RiskJobs.Json)!;
        var policy=riskCatalog.GetProperty("pricingPolicy").Deserialize<PortfolioValuationPolicy>(RiskJobs.Json)!;
        var request=new PortfolioRiskRequest(readContext,[book],[],[new(target,"0.01",riskCatalog.GetProperty("bumpConvention").GetString()!)],
            riskCatalog.GetProperty("convention").GetString(),riskCatalog.GetProperty("numericalPolicy").GetString(),policy,Stress:[]);
        var interactive=JsonSerializer.SerializeToElement(await calculator.Calculate(connection,data,frame,request,default),RiskJobs.Json);
        var queued=JsonSerializer.SerializeToElement(await RiskJobs.Submit(connection,manifest,calculator,data,frame,new(Guid.NewGuid(),request),default),RiskJobs.Json);
        if(queued.GetProperty("state").GetString()!="queued")throw new Exception("The exact mixed-source calculation was not admitted to the queue.");
        await File.WriteAllTextAsync(Path.ChangeExtension(path,"report.json"),JsonSerializer.Serialize(new
        {
            job=queued.GetProperty("jobId").GetGuid(),
            factors=interactive.GetProperty("factors"),positions=interactive.GetProperty("inputs").GetProperty("positions")
        },Json));
        await transaction.CommitAsync();
        Console.WriteLine("PASS catalogue trading: old and added futures coexist with unchanged exact terms, independent price selections and advanced deployment configuration");
    }
}
