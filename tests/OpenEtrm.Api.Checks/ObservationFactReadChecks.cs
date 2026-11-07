using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;

internal static class ObservationFactReadChecks
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
                var data=await database.Read(connection,ct,observationIds:pins,includeCalendarDays:false);
                var actual=JsonSerializer.SerializeToNode(data.Observations);
                var expected=JsonNode.Parse(sample.GetProperty("expectedFactsJson").GetString()!);
                if(!JsonNode.DeepEquals(actual,expected))throw new Exception("A selected fact history changed its native authorized rows or order.");
                return true;
            },CancellationToken.None);
        }
        Console.WriteLine("PASS managed fact history retains native correction rows and order without broad observation enumeration");
    }
}
