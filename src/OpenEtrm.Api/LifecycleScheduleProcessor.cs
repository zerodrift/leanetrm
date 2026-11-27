using System.Data;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Automation;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Dispatch only: the registered product handler derives and reviews every
// financial input. The scheduler records no synthetic observed facts.
public sealed class LifecycleScheduleProcessor(NpgsqlDataSource scheduler,NpgsqlDataSource runtime,PreviewManifest manifest)
{
    public async Task Process(JobClaim claim,CancellationToken ct)
    {
        if(claim.Operation!=LifecycleScheduleJobs.Operation)throw new AutomationException("job_operation","Select an owned lifecycle occurrence.");
        await OccurrenceReadAdmission.Admit(scheduler,runtime,manifest,claim,ct);
        await using var issue=scheduler.CreateCommand("SELECT automation.issue_lifecycle_context($1,$2,$3)");
        issue.Parameters.AddWithValue(claim.Id);issue.Parameters.AddWithValue(claim.Fence);issue.Parameters.AddWithValue(claim.WorkerId);
        var token=(Guid)(await issue.ExecuteScalarAsync(ct)??throw Invalid());
        await using var connection=await runtime.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_context($1)",token))await bind.ExecuteNonQueryAsync(ct);
        await using(var limits=ScheduleStore.Command(connection,"SET LOCAL statement_timeout='30s';SET LOCAL lock_timeout='5s'"))await limits.ExecuteNonQueryAsync(ct);
        var retained=await LifecycleNative.Read(connection,"SELECT automation.capture_lifecycle_source()::text",ct);
        if(retained.TryGetProperty("discovery",out _))
        {
            await using var scopeDatabase=new PreviewDatabase(manifest,runtime);
            using var scopeAdmission=scopeDatabase.OwnReadTransaction(connection,transaction);
            await GasBillingEnrollmentJobs.Process(connection,scopeDatabase,retained,ct);
            await scopeAdmission.Commit(ct);
            return;
        }
        var definition=retained.GetProperty("definition").Deserialize<LifecycleScheduleDefinition>(LifecycleNative.Json)??throw Invalid();
        var context=retained.GetProperty("context").Deserialize<LifecycleReadContext>(LifecycleNative.Json)??throw Invalid();
        definition.Validate();
        var mode=retained.Text("mode");
        if(mode is not("manual" or "review" or "automatic"))throw Invalid();
        var close=definition.CloseId is {} closeId?await MarketCloses.Read(connection,manifest.CatalogId,closeId,ct):null;
        if(close is not null&&(close.Definition.CatalogId!=manifest.CatalogId||close.At!=context.AsOf||close.BusinessDate!=context.BusinessDate||context.SnapshotId!=close.Id))throw Invalid();
        var selectedManifest=close is null?manifest:manifest with{RevisionId=close.Definition.RevisionId,ReferenceCatalogId=close.Reference.CatalogId,ReferenceRevisionId=close.Reference.RevisionId};
        await using var database=new PreviewDatabase(selectedManifest,runtime);
        using var admitted=database.OwnReadTransaction(connection,transaction);
        InputFrame? frame=null;
        if(close is not null)
        {
            await using(var save=ScheduleStore.Command(connection,"SAVEPOINT lifecycle_scheduled_frame"))await save.ExecuteNonQueryAsync(ct);
            try
            {
                var source=(await new KnowledgeResolver(manifest.CatalogId).Read(connection,context.ContextId,ct)).Context;
                await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);
                await TradingContext.Bind(connection,manifest.CatalogId,source,context.AsOf,ct);
                frame=(await MarketCloses.Frame(connection,manifest.CatalogId,close.Id,context.ContextId,ct)) with{ContextId=null};
            }
            finally
            {
                await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT lifecycle_scheduled_frame;RELEASE SAVEPOINT lifecycle_scheduled_frame");
                await restore.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        var operations=new LifecycleOperations(database,id=>frame is not null&&id==frame.Id?frame:throw Invalid());
        JsonElement? proposal=null;JsonElement? outcome=null;
        if(definition.Settings.GasSource is not null)
            (proposal,outcome)=await new GasBillingLifecycleActions(database).RunScheduled(connection,definition,context,claim.Id,mode,ct);
        else
        {
            var discovery=await operations.Discover(connection,new(context,definition.Target),ct);
            var request=LifecycleScheduledSelection.Create(definition,context,claim.Id,discovery.Actions,mode=="automatic");
            if(mode!="manual")
            {
                var review=await operations.Preview(connection,request,ct);
                request=request with{PreviewHash=review.Hash};
                if(mode=="automatic")outcome=(await operations.Execute(connection,request,ct)).Outcome;
                else proposal=JsonSerializer.SerializeToElement(new{request,review=new{review.Hash,review.Offer,review.Details}},LifecycleNative.Json);
            }
        }
        // Record the result after all product work. At commit, native financial
        // seals and the shared command's required-result seal precede the final
        // occurrence seal; owned admissions still run while it is running.
        await using(var publish=ScheduleStore.Command(connection,"SELECT automation.publish_lifecycle_result($1,$2::jsonb,$3::jsonb)",mode,
            proposal is {} selected?selected.GetRawText():DBNull.Value,outcome is {} executed?executed.GetRawText():DBNull.Value))await publish.ExecuteScalarAsync(ct);
        await admitted.Commit(ct);
    }
    private static MarketDataException Invalid()=>new("lifecycle_schedule_source","Retain the exact recorded close, certified execution knowledge, saved native versions and source-only action.");
}
