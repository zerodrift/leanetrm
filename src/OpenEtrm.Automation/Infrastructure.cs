using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace OpenEtrm.Automation;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NodeCommand(Guid RequestId, string ClusterId, string NodeId, [property: JsonRequired] long ExpectedVersion, string Availability)
{
    public void Validate()
    {
        if (RequestId == Guid.Empty || ClusterId is null || NodeId is null ||
            !Regex.IsMatch(ClusterId, "^[a-z0-9]{12,64}$") || !Regex.IsMatch(NodeId, "^[a-z0-9]{12,64}$") ||
            ExpectedVersion < 0 || Availability is not ("pause" or "active" or "drain"))
            throw new AutomationException("infrastructure_input", "Choose a current node and an available action.");
    }
}
public sealed record NodeCommandClaim(Guid Id, string NodeId, long ExpectedVersion, string Availability, long Fence, Guid WorkerId, DateTimeOffset LeaseUntil, bool IsResolution = false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NodeResolution(Guid RequestId, Guid Id, [property: JsonRequired] long ExpectedVersion)
{
    public void Validate()
    {
        if (RequestId == Guid.Empty || Id == Guid.Empty || ExpectedVersion < 0) throw new AutomationException("infrastructure_input", "Select the uncertain action and current node version.");
    }
}

public sealed class InfrastructureWorkerStore(NpgsqlDataSource source, string clusterId)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task Observe(IReadOnlyList<SwarmNode> nodes, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT automation.observe_nodes($1,$2::jsonb)");
        command.Parameters.AddWithValue(clusterId); command.Parameters.AddWithValue(JsonSerializer.Serialize(nodes, Json));
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<NodeCommandClaim?> Claim(Guid worker, int leaseSeconds, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT automation.claim_node_command($1,$2,$3)::text");
        command.Parameters.AddWithValue(clusterId); command.Parameters.AddWithValue(worker); command.Parameters.AddWithValue(leaseSeconds);
        return await command.ExecuteScalarAsync(ct) is string value ? JsonSerializer.Deserialize<NodeCommandClaim>(value, Json) : null;
    }
    public async Task Authorize(NodeCommandClaim claim, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT automation.authorize_node_command($1,$2,$3)");
        command.Parameters.AddWithValue(claim.Id); command.Parameters.AddWithValue(claim.WorkerId); command.Parameters.AddWithValue(claim.Fence);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task Complete(NodeCommandClaim claim, string outcome, string? error, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT automation.complete_node_command($1,$2,$3,$4,$5)");
        command.Parameters.AddWithValue(claim.Id); command.Parameters.AddWithValue(claim.WorkerId); command.Parameters.AddWithValue(claim.Fence);
        command.Parameters.AddWithValue(outcome); command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)error ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed class InfrastructureController(SwarmGateway gateway, InfrastructureWorkerStore store, int leaseSeconds, WorkloadController? workloads = null)
{
    private readonly Guid worker = Guid.NewGuid();
    public async Task Tick(CancellationToken ct)
    {
        await store.Observe(await gateway.Nodes(ct), ct);
        var claim = await store.Claim(worker, leaseSeconds, ct);
        if (claim is null) { if (workloads is not null) await workloads.Tick(ct); return; }
        string outcome; string? error;
        try
        {
            await gateway.SetAvailability(claim.NodeId, claim.ExpectedVersion, claim.Availability, ct, token => store.Authorize(claim, token),
                claim.IsResolution ? $"{claim.Id:N}-{claim.Fence}" : null);
            outcome = "applied"; error = null;
        }
        catch (AutomationException e) when (e.Code is "infrastructure_conflict" or "infrastructure_busy" or "infrastructure_input")
        { outcome = "failed"; error = e.Code; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or AutomationException or NpgsqlException)
        { outcome = "unknown"; error = "outcome_unknown"; }
        // An uncertain external command blocks later cluster maintenance; a lost
        // database response must not cause another Docker mutation.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.Complete(claim, outcome, error, cleanup.Token);
        if (workloads is not null) await workloads.Tick(ct);
    }
}
