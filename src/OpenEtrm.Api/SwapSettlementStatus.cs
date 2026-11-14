using System.Text.Json;
using Npgsql;

namespace OpenEtrm.Api;

internal static class SwapSettlementStatus
{
    private sealed record Admission(Guid Catalog,Guid Operation,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.require_swap_operation_read($1,$2,$3)",Catalog,Operation,Until);
            await command.ExecuteNonQueryAsync(ct);
        }
    }
    internal static async Task<JsonElement> Read(NpgsqlConnection connection,PreviewDatabase database,Guid operation,CancellationToken ct)
    {
        var catalog=database.Manifest.CatalogId;
        await using var capture=ScheduleStore.Command(connection,"SELECT settlement_data.futures_margin_preview_authority($1)",catalog);
        var admission=new Admission(catalog,operation,new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!));
        database.RegisterCommitAdmission(connection,admission);
        await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.swap_operation_status($1,$2)::text",catalog,operation);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
}
