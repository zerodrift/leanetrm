using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;

internal static class MarketCalendarDemandChecks
{
    public static async Task Run()
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById("America/Goose_Bay");
        InputFrame At(string at)=>new(Guid.NewGuid(),"Synthetic backward-midnight close",DateTimeOffset.Parse(at),[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty);
        var newest=At("2010-11-07T04:00:00Z");var previousDate=At("2010-11-07T03:02:00Z");var tiedDate=At("2010-11-07T03:00:00Z");
        var choices=ImmutableArray.Create(newest,previousDate,tiedDate);
        var groups=MarketDates.CandidateBatches(choices,zone,true).ToArray();
        var full=MarketDates.CandidateBatches(choices,zone,false).Single();
        var oracle=full.Where(frame=>DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(frame.At,zone).DateTime)==new DateOnly(2010,11,7)).Select(frame=>frame.Id).ToHashSet();
        if(groups.Length!=2||!groups[0].Select(frame=>frame.Id).ToHashSet().SetEquals(oracle)||
            !groups[0].SequenceEqual([newest,tiedDate])||!groups[1].SequenceEqual([previousDate]))
            throw new Exception("Limited date reads must retain every local-date tie and pair each batch's own frames even across a backward-midnight transition.");
        Console.WriteLine("PASS local-date batches match the full-calendar oracle across a backward-midnight transition");
        await using var database=new PreviewDatabase(Path.GetFullPath(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")!));
        await database.WithIdentity("viewer","ReadMarketData",async(connection,ct)=>
        {
            var metadata=await database.Read(connection,ct,observationIds:[]);
            // The built-in frame is a recorded close (D334), read as the close it was seeded as.
            var seeded=database.Manifest.Frames.Single(frame=>frame.At==DateTimeOffset.Parse("2020-12-18T16:00:00Z")).CloseId!.Value;
            var frame=await MarketCloses.Frame(connection,database.Manifest.CatalogId,seeded,null,ct);
            var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var template=CustomCurves.Templates(metadata).Select(value=>JsonSerializer.SerializeToElement(value,json)).Single(row=>row.GetProperty("code").GetString()=="ng_average");
            var definition=template.GetProperty("definition").Deserialize<RecipeDefinition>(json)!;
            var references=await new CustomCurvesStore(database).References(connection,definition,metadata,ct);
            // Each candidate is legal: 12 contracts x20 prior dates. Distinct
            // second-of-day sampling gives 70x240=16,800 distinct windows.
            // Calendar candidates are recorded closes of one session (D334); these synthetic ones all close the frame's day.
            var closeDay=new FrameClose(frame.Close!.Session,frame.Close.SessionVersion,DateOnly.FromDateTime(frame.At.UtcDateTime),null);
            var frames=Enumerable.Range(1,70).Select(second=>frame with{Id=Guid.NewGuid(),At=frame.At.AddSeconds(second),Close=closeDay}).ToImmutableArray();
            await using(var save=ScheduleStore.Command(connection,"SAVEPOINT calendar_demand"))await save.ExecuteNonQueryAsync(ct);
            try
            {
                var context=(await new KnowledgeStore(database.Manifest.CatalogId).Capture(connection,new("2020-12-18",null,"current"),ct)).Context;
                await PinnedContext.BindExact(connection,database.Manifest.CatalogId,context,ct);
                var demand=await CustomCurves.ObservationDemand(connection,database,definition,metadata,frames[0] with{ContextId=context.ContextId},
                    references.Sources,references.Calendars,references.Schedules,context,ct);
                if(demand.Windows.Length!=240||demand.Windows.Length*frames.Length<=PreviewDatabase.MaximumObservationWindows)
                    throw new Exception("The overflow fixture must exceed the aggregate read bound with individually legal candidates.");
                var prompt=CustomCurves.Templates(metadata).Select(value=>JsonSerializer.SerializeToElement(value,json)).Single(row=>row.GetProperty("code").GetString()=="ng_prompt")
                    .GetProperty("definition").Deserialize<RecipeDefinition>(json)!;
                var promptReferences=await new CustomCurvesStore(database).References(connection,prompt,metadata,ct);
                var bound=frame with{ContextId=context.ContextId};
                var currentDemand=await CustomCurves.ObservationDemand(connection,database,prompt,metadata,bound,
                    promptReferences.Sources,promptReferences.Calendars,promptReferences.Schedules,context,ct);
                var exact=new List<PreviewData>();
                await foreach(var selected in database.ReadFrameObservations(connection,metadata,[bound],ObservationReadScope.ExactPins,ct,currentDemand.ExactSeries))exact.Add(selected);
                var current=exact.Single();var original=current.Observations.First();
                bound=bound with{FrozenObservationIds=[original.Id("id")]};
                var projected=current.HistoryFrame(bound);
                if(!projected.FrozenObservationIds.SequenceEqual(bound.FrozenObservationIds)||projected.ObservationIds.Length>=bound.ObservationIds.Length)
                    throw new Exception("The exact source projection must be narrower while retaining frozen markers.");
                var correction=JsonNode.Parse(original.GetRawText())!;
                correction["id"]=Guid.NewGuid();correction["corrects_id"]=original.Id("id");correction["value"]="999";
                var corrected=current.WithObservations([..current.Observations,JsonSerializer.SerializeToElement(correction)]);
                var full=await CustomCurves.EvaluateDefinition(connection,database,prompt,metadata,bound,
                    promptReferences.Sources,promptReferences.Calendars,promptReferences.Schedules,context,ct);
                var narrowed=await CustomCurves.EvaluateDefinition(connection,database,prompt,corrected,bound,
                    promptReferences.Sources,promptReferences.Calendars,promptReferences.Schedules,context,ct,retainedObservations:true,sourceFrame:projected);
                if(JsonSerializer.Serialize(full)!=JsonSerializer.Serialize(narrowed))
                    throw new Exception("Projected current sampling must preserve the full-frame calculation and ignore an unselected later correction.");
            }
            finally
            {
                await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT calendar_demand;RELEASE SAVEPOINT calendar_demand");
                await restore.ExecuteNonQueryAsync(CancellationToken.None);
            }
            var one=await MarketDates.Read(database,connection,PinnedContext.Current(),[frames[0]],new(null,"UTC",closeDay.Session,definition,1),ct);
            var qualifying=frame with{Id=Guid.NewGuid(),At=frame.At.AddDays(1),Close=closeDay with{BusinessDate=closeDay.BusinessDate.AddDays(1)}};
            var expected=await MarketDates.Read(database,connection,PinnedContext.Current(),[qualifying],new(null,"UTC",closeDay.Session,definition,1),ct);
            var all=await MarketDates.Read(database,connection,PinnedContext.Current(),frames.Add(qualifying),new(null,"UTC",closeDay.Session,definition),ct);
            if(!one.Dates.IsEmpty||one.Unavailable.Single().Reason!="missing_observation"||expected.Dates.Length!=1||
                JsonSerializer.Serialize(all.Dates)!=JsonSerializer.Serialize(expected.Dates)||!all.Unavailable.SequenceEqual(one.Unavailable))
                throw new Exception("An aggregate overflow must preserve candidate availability and its explicit missing-sample refusal.");
            return true;
        },CancellationToken.None);
        Console.WriteLine("PASS oversized custom-calendar demand falls back to bounded candidate reads without truncation or an unhandled error");
    }
}
