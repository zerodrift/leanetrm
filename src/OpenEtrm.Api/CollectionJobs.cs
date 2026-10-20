using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.Automation;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CollectionJobUpdate(Guid RequestId,Guid Id,Guid ExpectedGeneration,string Name,
    [property:JsonRequired]bool ScheduleEnabled,[property:JsonRequired]int FromDay,[property:JsonRequired]int ThroughDay);

public static class CollectionJobs
{
    public static async Task<JsonObject> Directory(NpgsqlConnection connection,CancellationToken ct,int page,int collectionPage)
    {
        var result=JsonNode.Parse((await JobOperations.List(connection,ct,page)).GetRawText())!.AsObject();
        await using var command=ScheduleStore.Command(connection,"SELECT power_data.job_directory($1)::text",collectionPage);
        result["collectionJobs"]=JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        return result;
    }
    public static async Task<JsonElement> Update(NpgsqlConnection connection,CollectionJobUpdate request,CancellationToken ct)
    {
        if(request.RequestId==Guid.Empty||request.Id==Guid.Empty||request.ExpectedGeneration==Guid.Empty)
            throw new AutomationException("job_input","Choose an existing collection job and its current generation.");
        await using var command=ScheduleStore.Command(connection,"SELECT power_data.update_job($1::jsonb)::text",JsonSerializer.Serialize(request,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
}
