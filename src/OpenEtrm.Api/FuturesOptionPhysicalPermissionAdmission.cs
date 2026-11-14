using System.Text.Json;
using Npgsql;

namespace OpenEtrm.Api;

internal sealed record FuturesOptionPhysicalPermissionAdmission(DateTimeOffset Until):ICommitAdmission
{
    internal static async Task Capture(NpgsqlConnection connection,PreviewDatabase database,CancellationToken ct)
    {
        if(!await TryCapture(connection,database,ct))throw new PreviewDeniedException();
    }
    internal static async Task<bool> TryCapture(NpgsqlConnection connection,PreviewDatabase database,CancellationToken ct)
    {
        // Resolve the original bound before the permission check can wait on
        // the authorization epoch. A successor grant cannot replace it.
        var source=await LifecycleNative.Read(connection,"SELECT security.capture_physical_valuation_read()::text",ct);
        if(source.Text("implementation")!="physical_valuation_read_admission_v1")throw new PreviewDeniedException();
        if(source.GetProperty("allowed").ValueKind!=JsonValueKind.True||source.GetProperty("authorityUntil").ValueKind==JsonValueKind.Null)return false;
        var admission=new FuturesOptionPhysicalPermissionAdmission(source.Instant("authorityUntil"));
        database.RegisterCommitAdmission(connection,admission);await admission.RequireCommit(connection,ct);return true;
    }
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.require_physical_valuation_read($1)",Until);
        await command.ExecuteNonQueryAsync(ct);
    }
}
