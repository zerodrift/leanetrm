using System.Text.Json;
using Npgsql;

namespace OpenEtrm.Api;

// The database remains the atomic idempotency and authorization authority.
// Callers retain their input validation order and their exact serialized payload.
internal static class SettlementCommandReplay
{
    internal static async Task<JsonElement?> Read(NpgsqlConnection connection, Guid catalog, Guid operation, string payload, CancellationToken ct)
    {
        await using var command = ScheduleStore.Command(connection,
            "SELECT settlement_data.swap_command_status($1,$2,$3::jsonb)::text", catalog, operation, payload);
        return await command.ExecuteScalarAsync(ct) is string prior ? JsonSerializer.Deserialize<JsonElement>(prior) : null;
    }
}
