using System.Text.Json;
using Npgsql;

namespace OpenEtrm.Api;

// Return this pending result directly from WithIdentity. Only its successful
// owned transaction exposes Outcome; a failed/unknown commit is reconciled by
// repeating the same request ID in a fresh authorized request.
internal sealed record FuturesMarginWrite(Guid Catalog,Guid OperationId,string Kind,JsonElement Request,
    DateTimeOffset AuthorityUntil,JsonElement Outcome):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,
            "SELECT settlement_data.finish_futures_margin_command($1,$2,$3,$4::jsonb,$5)",
            Catalog,OperationId,Kind,Request.GetRawText(),AuthorityUntil);
        await command.ExecuteNonQueryAsync(ct);
    }
}

internal static class FuturesMarginCommands
{
    internal static Task<FuturesMarginWrite> SavePolicy(NpgsqlConnection connection,Guid catalog,JsonElement request,CancellationToken ct)=>
        Run(connection,catalog,request,"policy_save","SELECT settlement_data.save_futures_margin_policy($1,$2::jsonb)::text",ct);
    internal static Task<FuturesMarginWrite> Adopt(NpgsqlConnection connection,Guid catalog,JsonElement request,CancellationToken ct)=>
        Run(connection,catalog,request,"adopt","SELECT settlement_data.adopt_futures_margin($1,$2::jsonb)::text",ct);

    private static async Task<FuturesMarginWrite> Run(NpgsqlConnection connection,Guid catalog,JsonElement request,string kind,string sql,CancellationToken ct)
    {
        // Capture before the native command's receipt, publication or row waits.
        await using var capture=ScheduleStore.Command(connection,"SELECT settlement_data.futures_margin_command_authority($1,$2)",catalog,kind);
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        await using var command=ScheduleStore.Command(connection,sql,catalog,request.GetRawText());
        var outcome=JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
        return new(catalog,request.Id("operationId"),kind,request.Clone(),until,outcome);
    }
}
