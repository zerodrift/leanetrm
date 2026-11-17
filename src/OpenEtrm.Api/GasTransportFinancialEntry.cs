using Npgsql;

namespace OpenEtrm.Api;

internal static class GasTransportFinancialEntry
{
    internal static async Task Capture(NpgsqlConnection connection,PreviewDatabase database,bool requireCapacity,CancellationToken ct)
    {
        DateTimeOffset? operatingUntil=null;
        if(requireCapacity)
        {
            // Capture the independent operating grant before any source or base
            // scope can wait. A later grant cannot supply missing entry authority.
            await using var capture=ScheduleStore.Command(connection,"""
                SELECT candidate FROM(SELECT security.authority_until('ReadGasOperations') candidate)s
                WHERE candidate IS NOT NULL AND isfinite(candidate) AND candidate>clock_timestamp()
                """);
            var value=await capture.ExecuteScalarAsync(ct);
            if(value is not DateTime at)throw new PreviewDeniedException();
            operatingUntil=new DateTimeOffset(at);
        }
        var financial=await new GasTransportFinancialStore(database).Capture(connection,null,null,ct);
        if(operatingUntil is {} until)
        {
            until=until<financial.Until?until:financial.Until;
            var operating=new GasTransportCapacityAdmission(database.Manifest.CatalogId,null,until);
            var final=new GasTransportFinancialAdmission(database.Manifest.CatalogId,null,null,until);
            database.RegisterCommitAdmission(connection,operating);
            // The financial guard checks the original minimum both before and
            // after current scope checks, including when a successor is active.
            database.RegisterCommitAdmission(connection,final);
            await operating.RequireCommit(connection,ct);
            await final.RequireCommit(connection,ct);
        }
    }
}
