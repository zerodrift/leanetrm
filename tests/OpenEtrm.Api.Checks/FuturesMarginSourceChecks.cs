using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using Npgsql;

internal static class FuturesMarginSourceChecks
{
    public static async Task RunNative(string fixturePath)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(fixturePath));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var host=fixture.Text("host");
        if(!Path.GetFullPath(host).StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&
            !Path.GetFullPath(host).StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Native margin source checks require an owned disposable database.");
        await using var runtime=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder{
            Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false}.ConnectionString);
        await using var database=new PreviewDatabase(manifest,runtime);
        FuturesMarginTrajectory? originalPlan=null;FuturesSettlementFact? originalFact=null;
        foreach(var sample in fixture.GetProperty("samples").EnumerateArray())
        {
            await using var connection=await runtime.OpenConnectionAsync();
            await using var transaction=await connection.BeginTransactionAsync();
            await using(var identity=ScheduleStore.Command(connection,"SELECT security.use_context($1)",sample.Id("token")))
                await identity.ExecuteNonQueryAsync();
            var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,sample.Id("contextId"),default)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,context,default);
            var pin=fixture.Id("original");var at=fixture.Instant("at");
            var data=await database.Read(connection,default,selectedRevision:manifest.RevisionId,observationIds:[pin],includeCalendarDays:false,
                observationScope:ObservationReadScope.ExactPins);
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic margin evidence",at,DateOnly.FromDateTime(at.UtcDateTime),[pin],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context.ContextId);
            var market=data.Model.BindFutures(fixture.Id("marketBinding"),new(new(data.DefinitionRevision,"future"),new(data.DefinitionRevision,fixture.Id("contract"))),
                "cash",FuturesMarketBinding.Implementation);
            var occurrence=new FuturesMarginOccurrence(fixture.Id("occurrence"),at,at.AddDays(1),FuturesMarginPhase.Daily);
            var rounding=new CashRoundingConvention(market.Quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal);
            var policy=new FuturesMarginPolicy(fixture.Id("policy"),1,market.Contract.Key,new(data.DefinitionRevision,"future"),at.AddYears(-1),
                market.Contract.Expiry.AddDays(1),[occurrence,new(fixture.Id("terminal"),market.Contract.Expiry,market.Contract.Expiry,FuturesMarginPhase.Final)],
                rounding,FuturesMargin.HeldBindingRounding,FuturesMarginOpening.IncludeOpeningInstant,FuturesMarginPolicy.Version);
            var facts=await FuturesMarginSources.Read(connection,database,data,frame,context,market,policy,[occurrence],default);
            var fact=facts[occurrence.Id];
            if(fact.Observation.Id!=sample.Id("expected")||fact.KnownAt!=sample.Instant("knownAt"))
                throw new Exception("The native reader mixed certified source versions or publication times.");
            // Native source observations/receipts are real. These synthetic
            // holding/history operands qualify the pure planner integration;
            // they do not stand in for native trading/adoption certificates.
            var trade=new Trade(fixture.Id("trade"),fixture.Id("book"),[FuturesExecution.Bind(fixture.Id("binding"),market,ExactDecimal.Parse("2"),
                new(ExactDecimal.Parse("3"),market.Quote),at.AddDays(-1),FuturesSettlementState.Unprocessed)]);
            ImmutableArray<RecordedFuturesMargin> prior=originalPlan is null?[]:[new(occurrence.Id,fixture.Id("determination"),null,
                originalPlan.Steps[0].PreviousBasis,originalFact!,originalPlan.Steps[0].ExactVariation,originalPlan.Steps[0].Variation,originalFact!.KnownAt,1)];
            var isCorrection=sample.Id("expected")!=pin;
            var dues=isCorrection?ImmutableDictionary<Guid,DateTimeOffset>.Empty.Add(occurrence.Id,at.AddDays(3)):
                ImmutableDictionary<Guid,DateTimeOffset>.Empty;
            var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
            var proposed=market.Instrument.ProposeMarginTerms(trade,fixture.Id("binding"),policy,data.Model,snapshot);
            var history=new FuturesMarginHistory(proposed,context.ContextId.Value,
                snapshot.Knowledge!.KnownAt,prior);
            var plan=market.Instrument.PlanMarginTrajectory(trade,fixture.Id("binding"),policy,snapshot,occurrence.Id,facts,history,dues);
            if(plan.Steps.Single().Adjustment.CompareTo(ExactMoney.Parse(sample.Text("adjustment"),market.Quote.Currency))!=0)
                throw new Exception("The exact native source did not produce its independently calculated margin adjustment.");
            if(originalPlan is null){originalPlan=plan;originalFact=fact;}
            else if(!isCorrection&&(!plan.Steps.Single().NoChange||fact!=originalFact))
                throw new Exception("Re-reading the original certified view changed its source or financial outcome.");
            await transaction.RollbackAsync();
        }
        foreach(var sample in fixture.GetProperty("finalSamples").EnumerateArray())
        {
            await using var connection=await runtime.OpenConnectionAsync();
            await using var transaction=await connection.BeginTransactionAsync();
            await using(var identity=ScheduleStore.Command(connection,"SELECT security.use_context($1)",sample.Id("token")))
                await identity.ExecuteNonQueryAsync();
            var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,sample.Id("contextId"),default)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,context,default);
            var pin=fixture.Id("original");var at=sample.Instant("at");
            var data=await database.Read(connection,default,selectedRevision:manifest.RevisionId,observationIds:[pin],includeCalendarDays:false,
                observationScope:ObservationReadScope.ExactPins);
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic final evidence",at,DateOnly.FromDateTime(at.UtcDateTime),[pin],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context.ContextId);
            var market=data.Model.BindFutures(fixture.Id("marketBinding"),new(new(data.DefinitionRevision,"future"),new(data.DefinitionRevision,fixture.Id("contract"))),
                "cash",FuturesMarketBinding.Implementation);
            var terminal=new FuturesMarginOccurrence(fixture.Id("terminal"),at,at,FuturesMarginPhase.Final);
            var rounding=new CashRoundingConvention(market.Quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal);
            var policy=new FuturesMarginPolicy(fixture.Id("policy"),1,market.Contract.Key,new(data.DefinitionRevision,"future"),at.AddYears(-1),
                at.AddDays(1),[terminal],rounding,FuturesMargin.HeldBindingRounding,FuturesMarginOpening.IncludeOpeningInstant,FuturesMarginPolicy.Version);
            if(sample.GetProperty("expected").ValueKind==JsonValueKind.Null)
            {
                try
                {
                    await FuturesMarginSources.Read(connection,database,data,frame,context,market,policy,[terminal],default);
                    throw new Exception("Daily settlement was substituted for missing official final settlement.");
                }
                catch(MarketDataException error)when(error.Code=="missing_observation"){}
            }
            else
            {
                var result=await FuturesMarginSources.Read(connection,database,data,frame,context,market,policy,[terminal],default);
                var fact=result[terminal.Id];
                if(fact.Observation.Id!=sample.Id("expected")||fact.KnownAt!=sample.Instant("knownAt")||fact.Role!=ObservationRole.FinalContractSettlement)
                    throw new Exception("Final margin requires its exact frozen official final-settlement source and publication receipt.");
            }
            await transaction.RollbackAsync();
        }
        Console.WriteLine("PASS native margin source pipeline: certified historical/current corrections, exact publication receipts and managed causal planning");
        Console.WriteLine("PASS native final settlement: exact official terminal evidence; due daily marks cannot substitute for absent finals");
    }
    public static void Run()
    {
        var f=PreviewDataChecks.Fixture(2);
        var data=new PreviewData(f.Manifest,f.Definitions,f.References,f.Observations);
        var at=f.Observations[0].Instant("observed_at");var cutoff=at.AddDays(3);
        var market=data.Model.BindFutures(Guid.NewGuid(),new(new(data.DefinitionRevision,"future"),data.Contracts[0].Key),
            "cash",FuturesMarketBinding.Implementation);
        var context=data.Snapshot(f.Frame).WithKnowledge(Guid.NewGuid(),cutoff);
        var occurrence=new FuturesMarginOccurrence(Guid.NewGuid(),at,at.AddDays(1),FuturesMarginPhase.Daily);
        var original=f.Observations[0].Id("id");var correction=Guid.NewGuid();var later=Guid.NewGuid();
        static JsonElement Change(JsonElement row,params (string Key,object? Value)[] changes)
        {
            var fields=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(row.GetRawText())!;
            foreach(var (key,value) in changes)fields[key]=JsonSerializer.SerializeToElement(value);
            return JsonSerializer.SerializeToElement(fields);
        }
        var revised=Change(f.Observations[0],("id",correction),("corrects_id",original),("value","101"));
        var future=Change(revised,("id",later),("corrects_id",correction),("value","102"));
        var known=new Dictionary<Guid,DateTimeOffset>{{original,at.AddHours(1)},{correction,at.AddDays(2)},{later,at.AddDays(4)}};
        FuturesSettlementFact Select(ImmutableArray<JsonElement> rows,MarketSnapshot? view=null,IReadOnlyDictionary<Guid,DateTimeOffset>? receipts=null)=>
            FuturesMarginSources.SelectCurrentCorrection(data.WithObservations(rows),view??context,market,
                new(data.DefinitionRevision,"future"),occurrence,receipts??known);
        var selected=Select([future,revised,f.Observations[0]]);
        if(selected.Observation.Id!=correction||selected.Price.ToExactString()!="101"||selected.KnownAt!=known[correction])
            throw new Exception("Select only the exact source's terminal visible correction and retain its original certified publication time.");
        var older=Select([future,revised,f.Observations[0]],context.WithKnowledge(Guid.NewGuid(),at.AddDays(1)));
        if(older.Observation.Id!=original)throw new Exception("A later correction entered an earlier knowledge view.");
        if(Select([f.Observations[0],revised,future])!=selected)
            throw new Exception("Source row order cannot decide financial revision selection.");
        var changedAudit=Change(revised,("received_at","2190-01-01T00:00:00Z"));
        if(Select([f.Observations[0],changedAudit])!=selected)
            throw new Exception("Observation receipt audit time cannot replace certified source knowledge time.");
        var laterView=context.WithKnowledge(Guid.NewGuid(),cutoff.AddDays(5));
        if(Select([f.Observations[0],revised],laterView)!=selected)
            throw new Exception("A later reader context must not manufacture a changed source fact.");
        Reject("missing_observation",()=>Select([f.Observations[0],Change(revised,("publication_state","preliminary"))]));
        Reject("futures_settlement_evidence",()=>Select([f.Observations[0],Change(revised,("corrects_id",null))]));
        Reject("futures_settlement_evidence",()=>Select([revised]));
        Reject("futures_settlement_evidence",()=>Select([f.Observations[0]],receipts:new Dictionary<Guid,DateTimeOffset>()));
        Reject("missing_observation",()=>Select([Change(f.Observations[0],("source_code","other_source"))]));
        Reject("missing_observation",()=>Select([f.Observations[1]]));
        Reject("missing_observation",()=>Select([Change(f.Observations[0],("role","trader_mark"))]));
        using(var unopened=NpgsqlDataSource.Create("Host=127.0.0.1;Database=synthetic_unopened;Username=synthetic"))
        {
            var database=new PreviewDatabase(f.Manifest,unopened);
            using var connection=unopened.CreateConnection();
            var selectedContext=new ScheduleContext(f.Frame.BusinessDate.ToString("yyyy-MM-dd"),1,cutoff.ToString("O"),context.Knowledge!.ContextId);
            var wrongDay=f.Frame with{BusinessDate=f.Frame.BusinessDate.AddDays(1),ContextId=selectedContext.ContextId};
            var rounding=new CashRoundingConvention(market.Quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal);
            var policy=new FuturesMarginPolicy(Guid.NewGuid(),1,market.Contract.Key,new(data.DefinitionRevision,"future"),at.AddYears(-1),
                market.Contract.Expiry.AddDays(1),[occurrence,new(Guid.NewGuid(),market.Contract.Expiry,market.Contract.Expiry,FuturesMarginPhase.Final)],
                rounding,FuturesMargin.HeldBindingRounding,FuturesMarginOpening.IncludeOpeningInstant,FuturesMarginPolicy.Version);
            try
            {
                FuturesMarginSources.Read(connection,database,data,wrongDay,selectedContext,market,policy,[occurrence],default).GetAwaiter().GetResult();
                throw new Exception("A different business date reached native source history.");
            }
            catch(OpenEtrm.Shared.Temporal.TemporalException error)when(error.Code=="temporal_context"){}
        }
        Console.WriteLine("PASS futures margin sources: exact official history, certified correction cutoff, ambiguity refusal and stable source identity");
    }
    static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception($"Expected {code}.");}
}
