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
                return true;
            },CancellationToken.None);
        }
        Console.WriteLine("PASS managed exact-pin reads retain native RLS rows and order without enumerating the broad observation set");
    }
}
