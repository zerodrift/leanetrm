using Npgsql;

namespace OpenEtrm.Api;

// Created while reading the selected frame under the original booking authority.
// The trusted issuer binds it to the publication token before the runtime can use it.
internal sealed record GasSwingBookingFrame(Guid Actor,Guid Frame,DateTimeOffset At,string Command,DateTimeOffset Until)
{
    internal static async Task<GasSwingBookingFrame> Select(NpgsqlConnection connection,Guid catalog,string permission,
        InputFrame frame,string command,DateTimeOffset? sessionEnds,CancellationToken ct)
    {
        await using var select=ScheduleStore.Command(connection,"""
            SELECT (security.booking_actor($1)->>'id')::uuid,
                   least(security.authority_until($2),security.authority_until('ReadTrades'),security.authority_until('ReadMarketData'))
            """,catalog,permission);
        await using var row=await select.ExecuteReaderAsync(ct);
        if(!await row.ReadAsync(ct)||row.IsDBNull(0))throw new PreviewDeniedException();
        var until=row.GetFieldValue<DateTimeOffset>(1);
        return new(row.GetGuid(0),frame.Id,frame.At,command,sessionEnds<until?sessionEnds.Value:until);
    }

    internal async Task Attest(NpgsqlDataSource issuer,Guid token,Guid catalog,CancellationToken ct)
    {
        await using var connection=await issuer.OpenConnectionAsync(ct);
        await using var attest=ScheduleStore.Command(connection,
            "SELECT security.attest_gas_swing_booking_frame($1,$2,$3,$4::jsonb,$5,$6,$7)",token,Actor,catalog,Command,Frame,At,Until);
        await attest.ExecuteNonQueryAsync(ct);
    }
}
