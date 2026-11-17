using Npgsql;

namespace OpenEtrm.Api;

// Foreign custody requires its own source/current-authority proof. Only actual
// customer stock consumers additionally require owned-inventory authority.
internal sealed record GasParkCustodyReadAdmission(Guid Catalog,Guid Context,DateTimeOffset At,DateTimeOffset Until,bool Inventory):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var guard=ScheduleStore.Command(connection,"SELECT security.require_gas_park_custody_read($1,$2,$3,$4,$5)",Catalog,Context,At,Until,Inventory);
        await guard.ExecuteNonQueryAsync(ct);
    }
}
