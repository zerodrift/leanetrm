using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;

internal static class ExactPinReadChecks
{
    public static async Task Run(string fixturePath)
    {
        await using var database=new PreviewDatabase(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")!);
        using var fixture=JsonDocument.Parse(File.ReadAllText(fixturePath));
        foreach(var sample in fixture.RootElement.EnumerateArray())
        {
            await database.WithIdentity("viewer","ReadMarketData",async(connection,ct)=>
            {
                if(sample.GetProperty("contextId").ValueKind!=JsonValueKind.Null)
                {
                    await using var bind=ScheduleStore.Command(connection,"SELECT security.use_market_context($1,$2)",database.Manifest.CatalogId,sample.GetProperty("contextId").GetGuid());
                    await bind.ExecuteNonQueryAsync(ct);
                }
                await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_close_session($1,$2)",database.Manifest.CatalogId,sample.GetProperty("session").GetString()!))
                    await bind.ExecuteNonQueryAsync(ct);
                var pins=sample.GetProperty("pins").EnumerateArray().Select(p=>p.GetGuid()).ToImmutableArray();
                var actual=await PreviewDatabase.ReadObservationPins(connection,database.Manifest,pins,ct);
                var observed=JsonSerializer.SerializeToNode(actual)!.AsArray();var expected=JsonNode.Parse(sample.GetProperty("expectedJson").GetString()!)!.AsArray();
                if(!JsonNode.DeepEquals(observed,expected))
                {
                    var at=Enumerable.Range(0,Math.Min(observed.Count,expected.Count)).FirstOrDefault(i=>!JsonNode.DeepEquals(observed[i],expected[i]));
                    var fields=observed.Count>at&&expected.Count>at?observed[at]!.AsObject().Where(p=>!JsonNode.DeepEquals(p.Value,expected[at]![p.Key])).Select(p=>p.Key):[];
                    throw new Exception($"Exact observation retrieval changed native RLS rows/order: context={sample.GetProperty("contextId")}, expected={expected.Count}, actual={observed.Count}, index={at}, fields={string.Join(',',fields)}.");
                }
                var metadata=await database.Read(connection,ct,observationIds:[],includeCalendarDays:false);
                var frame=new InputFrame(Guid.NewGuid(),"Synthetic calendar frame",DateTimeOffset.Parse("2020-12-18T16:00:00Z"),pins,
                    ImmutableDictionary<string,ImmutableArray<Guid>>.Empty);
                foreach(var series in new ImmutableArray<Guid>?[]{null,[..actual.Select(row=>row.Id("series_id")).Distinct()],[..actual.Take(1).Select(row=>row.Id("series_id"))]})
                {
                    var wanted=series is null?expected:new JsonArray(expected.Where(row=>series.Value.Contains(Guid.Parse(row!["series_id"]!.GetValue<string>()))).Select(row=>row!.DeepClone()).ToArray());
                    var count=0;
                    await foreach(var selected in database.ReadFrameObservations(connection,metadata,[frame],ObservationReadScope.ExactPins,ct,series))
                    {
                        count++;
                        if(!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(selected.Observations),wanted))
                            throw new Exception("Calendar frame reads changed exact authorized pins or native row order.");
                        if(series is not null&&selected.CompletePins!=(actual.Length==pins.Length))
                            throw new Exception("Series filtering changed complete-frame visibility.");
                    }
                    if(count!=1)throw new Exception("A calendar frame disappeared from the bounded read.");
                }
                var incomplete=frame with{ObservationIds=[..pins,Guid.NewGuid()]};
                await foreach(var selected in database.ReadFrameObservations(connection,metadata,[incomplete],ObservationReadScope.ExactPins,ct,
                    [..actual.Take(1).Select(row=>row.Id("series_id"))]))
                    if(selected.CompletePins)throw new Exception("An unavailable pin outside the selected series was ignored.");
                return true;
            },CancellationToken.None);
        }
        Console.WriteLine("PASS managed exact-pin reads retain native RLS rows and order without enumerating the broad observation set");
    }
}
