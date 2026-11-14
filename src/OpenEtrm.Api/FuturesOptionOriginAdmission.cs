using Npgsql;

namespace OpenEtrm.Api;

internal sealed record FuturesOptionOriginAdmission(Guid Catalog,Guid Trade,int Revision,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT lifecycle.require_option_origin($1,$2,$3,$4)",Catalog,Trade,Revision,Until);
        await command.ExecuteNonQueryAsync(ct);
    }
}
