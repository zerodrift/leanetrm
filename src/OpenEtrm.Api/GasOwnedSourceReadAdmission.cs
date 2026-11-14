using Npgsql;

namespace OpenEtrm.Api;

// The same exact source contract protects interactive holdings, knowledge-only
// history and read-only economic profile consumers, using the original deadline.
internal sealed record GasOwnedSourceReadAdmission(Guid Catalog,Guid Context,DateTimeOffset At,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.require_gas_owned_read($1,$2,$3,$4)",Catalog,Context,At,Until);
        await command.ExecuteNonQueryAsync(ct);
    }
}
