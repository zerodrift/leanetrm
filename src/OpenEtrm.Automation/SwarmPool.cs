using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace OpenEtrm.Automation;

public sealed partial class SwarmGateway
{
    // The installation registers the inspected service Spec, including image,
    // network and secret identities. Only replica count is mutable here.
    public async Task ReconcilePool(NpgsqlDataSource source,CancellationToken ct)
    {
        await using var config=source.CreateCommand("SELECT automation.pool_config($1)::text");
        config.Parameters.AddWithValue(clusterId);
        if(await config.ExecuteScalarAsync(ct) is not string json)return;
        var registration=JsonNode.Parse(json)!;
        var serviceId=registration["service"]!.GetValue<string>();
        await RequireCluster(ct);
        using var service=await Find("services/"+serviceId,ct);
        var current=service==null?null:JsonNode.Parse(service.RootElement.GetRawText());
        var spec=current?["Spec"]?.DeepClone();
        var count=spec?["Mode"]?["Replicated"]?["Replicas"]?.GetValue<int>()??0;
        var approved=registration["spec"]!.DeepClone();
        static void RemoveCount(JsonNode? value)
        {
            if(value?["Mode"]?["Replicated"] is JsonObject mode)mode.Remove("Replicas");
        }
        const string generationLabel="openetrm.pool.generation";
        var observedGeneration=spec?["Labels"]?[generationLabel]?.GetValue<string>();
        if(spec?["Labels"] is JsonObject labels)labels.Remove(generationLabel);
        RemoveCount(approved);RemoveCount(spec);
        var matched=current?["ID"]?.GetValue<string>()==serviceId && JsonNode.DeepEquals(spec,approved) && spec?["Mode"]?["Replicated"] is JsonObject;
        var tasks=new List<object>();
        if(matched)
        {
            var filter=Uri.EscapeDataString(JsonSerializer.Serialize(new{service=new[]{serviceId}}));
            using var reply=await Get("tasks?filters="+filter,ct);
            foreach(var task in reply.RootElement.EnumerateArray())
            {
                if(task.GetProperty("ServiceID").GetString()!=serviceId)throw Protocol();
                tasks.Add(new{id=task.GetProperty("ID").GetString(),node=task.TryGetProperty("NodeID",out var node)?node.GetString():null,
                    slot=task.GetProperty("Slot").GetInt32(),state=task.GetProperty("Status").GetProperty("State").GetString()});
            }
        }
        // Serialize the last permission check and external CAS against later
        // pool requests. The preceding desired-state command is already durable.
        await using var connection=await source.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        await using var inspect=new NpgsqlCommand("SELECT automation.inspect_pool($1,$2,$3,$4,$5::jsonb,$6,$7)",connection,transaction);
        inspect.Parameters.AddWithValue(clusterId);inspect.Parameters.AddWithValue(serviceId);
        inspect.Parameters.AddWithValue(registration["generation"]!.GetValue<long>());inspect.Parameters.AddWithValue(count);
        inspect.Parameters.AddWithValue(JsonSerializer.Serialize(tasks));inspect.Parameters.AddWithValue(matched);
        inspect.Parameters.AddWithValue(observedGeneration??string.Empty);
        if(await inspect.ExecuteScalarAsync(ct) is int desired)
        {
            spec!["Mode"]!["Replicated"]!["Replicas"]=desired;
            spec["Labels"]![generationLabel]=registration["generation"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var version=current!["Version"]!["Index"]!.GetValue<long>();
            using var response=await client.PostAsJsonAsync(Api+"services/"+serviceId+"/update?version="+version+"&registryAuthFrom=spec",spec,ct);
            if(!response.IsSuccessStatusCode)throw Protocol();
        }
        await transaction.CommitAsync(ct);
    }
}
