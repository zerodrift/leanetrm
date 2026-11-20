using System.Data;
using Npgsql;
using OpenEtrm.Automation;

namespace OpenEtrm.Api;

// First selection is a short committed admission, independent of calculation.
// A failed calculation or replacement worker keeps the occurrence's inputs.
internal static class OccurrenceReadAdmission
{
    internal static async Task Admit(NpgsqlDataSource scheduler,NpgsqlDataSource runtime,PreviewManifest manifest,JobClaim claim,CancellationToken ct)
    {
        var issueSql=claim.Operation switch
        {
            LifecycleScheduleJobs.Operation=>"SELECT automation.issue_lifecycle_context($1,$2,$3)",
            PilotCloseJobs.Operation=>"SELECT automation.issue_pilot_context($1,$2,$3)",
            _=>throw new AutomationException("job_operation","Select an occurrence with an explicit input-admission policy.")
        };
        await using var issue=scheduler.CreateCommand(issueSql);
        issue.Parameters.AddWithValue(claim.Id);issue.Parameters.AddWithValue(claim.Fence);issue.Parameters.AddWithValue(claim.WorkerId);
        var token=(Guid)(await issue.ExecuteScalarAsync(ct)??throw new InvalidOperationException("Missing occurrence authority."));
        await using var connection=await runtime.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        await using var database=new PreviewDatabase(manifest,runtime);
        using var admission=database.OwnReadTransaction(connection,transaction);
        await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_context($1)",token))await bind.ExecuteNonQueryAsync(ct);
        await using(var select=ScheduleStore.Command(connection,"SELECT automation.admit_occurrence_inputs()"))await select.ExecuteNonQueryAsync(ct);
        await admission.Commit(ct);
    }
}
