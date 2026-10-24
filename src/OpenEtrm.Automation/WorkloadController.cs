using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace OpenEtrm.Automation;

public sealed class WorkloadStore(NpgsqlDataSource source)
{
    public async Task Authorize(JobClaim claim, bool stopping, CancellationToken ct)
    {
        await using var command = ClaimCommand("SELECT automation.authorize_workload($1,$2,$3,$4)", claim);
        command.Parameters.AddWithValue(stopping); await command.ExecuteNonQueryAsync(ct);
    }
    public async Task Record(JobClaim claim, string? serviceId, string observation, CancellationToken ct)
    {
        await using var command = ClaimCommand("SELECT automation.record_service($1,$2,$3,$4,$5)", claim);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)serviceId ?? DBNull.Value });
        command.Parameters.AddWithValue(observation); await command.ExecuteNonQueryAsync(ct);
    }
    public async Task Stopped(JobClaim claim, CancellationToken ct)
    {
        await using var command = ClaimCommand("SELECT automation.stop_workload($1,$2,$3)", claim);
        await command.ExecuteNonQueryAsync(ct);
    }
    private NpgsqlCommand ClaimCommand(string sql, JobClaim claim)
    {
        var command = source.CreateCommand(sql);
        command.Parameters.AddWithValue(claim.Id); command.Parameters.AddWithValue(claim.WorkerId); command.Parameters.AddWithValue(claim.Fence);
        return command;
    }
    public async Task<JobClaim?> Cleanup(CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT automation.cleanup_workload(NULL,NULL,false)::text");
        return await command.ExecuteScalarAsync(ct) is string value ? JsonSerializer.Deserialize<JobClaim>(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null;
    }
    public async Task Removed(JobClaim claim, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT automation.cleanup_workload($1,$2,true)");
        command.Parameters.AddWithValue(claim.Id);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)claim.ServiceId ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed class WorkloadController(SwarmGateway gateway, JobWorkerStore jobs, WorkloadStore store, int leaseSeconds)
{
    private readonly Guid worker = Guid.NewGuid();
    public async Task Tick(CancellationToken ct)
    {
        var claim = await jobs.Claim(worker, leaseSeconds, ct);
        try
        {
            if (claim is not null) await Reconcile(claim, ct);
        }
        finally
        {
            // A problematic resource rotates in the cleanup queue and cannot
            // prevent other jobs from being polled on the following tick.
            var cleanup = await store.Cleanup(ct);
            if (cleanup is not null)
            {
                await gateway.RemoveCompletedWorkload(cleanup, ct);
                await store.Removed(cleanup, ct);
            }
        }
    }
    private async Task Reconcile(JobClaim claim, CancellationToken ct)
    {
        WorkloadObservation observation;
        try
        {
            observation = await gateway.ReconcileWorkload(claim, claim.MustStop, token => store.Authorize(claim, claim.MustStop, token), ct);
        }
        catch (AutomationException e) when (!claim.LaunchPrepared && e.Code is "workload_image" or "workload_network" or "workload_profile")
        {
            await jobs.Finish(claim, false, e.Code, ct); return;
        }
        catch (Exception e) when (e is AutomationException or HttpRequestException or OperationCanceledException)
        {
            using var receipt = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await store.Record(claim, null, "unknown", receipt.Token);
            throw;
        }
        await store.Record(claim, observation.ServiceId, observation.State, ct);
        if (observation.State == "stopped") await store.Stopped(claim, ct);
        else if (observation.State == "completed") await jobs.Finish(claim, true, null, ct);
        else if (observation.State == "failed") await jobs.Finish(claim, false, "workload_failed", ct);
        // Pending/unknown/running keeps its durable reservation. No second
        // create is issued after dispatch admission, including transport loss.
    }
}
