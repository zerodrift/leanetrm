using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.Automation;

internal static class LifecycleScheduleNativeChecks
{
    internal static async Task Read(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var packet=document.RootElement;
        var host=Path.GetFullPath(packet.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))throw new Exception("Use an owned disposable PostgreSQL cluster.");
        var manifest=packet.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Pooling=false,Timezone="UTC"};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var delayed=packet.GetProperty("delayed").GetBoolean();
        try
        {
            var read=await database.WithIdentity("trader",null,async(connection,ct)=>
            {
                var value=await LifecycleScheduleJobs.Read(connection,database,packet.Id("jobId"),ct);
                if(delayed)await Task.Delay(4100,ct);return value;
            },default);
            if(delayed)throw new Exception("A task result chained a successor gas grant after its original source read.");
            if(read.Outcome.GetProperty("results").GetArrayLength()!=1)throw new Exception("The retained task result is incomplete.");
        }
        catch(PostgresException denied)when(delayed&&denied.MessageText=="job_denied"){}
        Console.WriteLine(delayed?"PASS task result read retains its original gas deadline through managed commit":"PASS fresh authorized task result reopens exact retained source and occurrence");
    }
    internal static async Task WithoutResult(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var packet=document.RootElement;
        var host=Path.GetFullPath(packet.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))throw new Exception("Use an owned disposable PostgreSQL cluster.");
        var manifest=packet.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var claim=packet.GetProperty("claim").Deserialize<JobClaim>(LifecycleNative.Json)!;
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="pilot_fixture_scheduler",Pooling=false,Timezone="UTC"};
        await using var scheduler=NpgsqlDataSource.Create(login.ConnectionString);
        await using var runtime=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="pilot_fixture_runtime"}.ConnectionString);
        await using var issue=scheduler.CreateCommand("SELECT automation.issue_lifecycle_context($1,$2,$3)");
        issue.Parameters.AddWithValue(claim.Id);issue.Parameters.AddWithValue(claim.Fence);issue.Parameters.AddWithValue(claim.WorkerId);
        var token=(Guid)(await issue.ExecuteScalarAsync())!;
        await using var connection=await runtime.OpenConnectionAsync();await using var transaction=await connection.BeginTransactionAsync();
        await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_context($1)",token))await bind.ExecuteNonQueryAsync();
        var retained=await LifecycleNative.Read(connection,"SELECT automation.capture_lifecycle_source()::text",default);
        var definition=retained.GetProperty("definition").Deserialize<LifecycleScheduleDefinition>(LifecycleNative.Json)!;
        var context=retained.GetProperty("context").Deserialize<LifecycleReadContext>(LifecycleNative.Json)!;
        var close=await MarketCloses.Read(connection,manifest.CatalogId,definition.CloseId,default);
        await using var database=new PreviewDatabase(manifest with{RevisionId=close.Definition.RevisionId,ReferenceCatalogId=close.Reference.CatalogId,ReferenceRevisionId=close.Reference.RevisionId},runtime);
        using var admitted=database.OwnReadTransaction(connection,transaction);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT task_fixture_frame"))await save.ExecuteNonQueryAsync();
        var source=(await new KnowledgeStore(manifest.CatalogId).Read(connection,context.ContextId,default)).Context;
        await PinnedContext.BindExact(connection,manifest.CatalogId,source,default);await TradingContext.Bind(connection,manifest.CatalogId,source,context.AsOf,default);
        var frame=(await MarketCloses.Frame(connection,manifest.CatalogId,close.Id,context.ContextId,default)) with{ContextId=null};
        await using(var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT task_fixture_frame;RELEASE SAVEPOINT task_fixture_frame"))await restore.ExecuteNonQueryAsync();
        var operations=new LifecycleOperations(database,id=>id==frame.Id?frame:throw new Exception("Another task source was requested."));
        var discovered=await operations.Discover(connection,new(context,definition.Target),default);
        var request=LifecycleScheduledSelection.Create(definition,context,claim.Id,discovered.Actions,true);
        var review=await operations.Preview(connection,request,default);
        _=await operations.Execute(connection,request with{PreviewHash=review.Hash},default);
        try
        {
            if(packet.GetProperty("earlySeal").GetBoolean())
            {
                await using var seal=ScheduleStore.Command(connection,"SET CONSTRAINTS ALL IMMEDIATE");await seal.ExecuteNonQueryAsync();
            }
            else await admitted.Commit(default);
        }
        catch(PostgresException refused)when(refused.MessageText=="lifecycle_schedule_result_required")
        {
            Console.WriteLine("PASS native scheduled financial work without its exact complete task result rolls back before publication");return;
        }
        throw new Exception("A scheduled shared financial command committed without its owned task result.");
    }
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var packet=document.RootElement;
        var host=Path.GetFullPath(packet.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Scheduled lifecycle checks require their owned disposable PostgreSQL cluster.");
        var manifest=packet.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var claim=packet.GetProperty("claim").Deserialize<JobClaim>(LifecycleNative.Json)!;
        var settings=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="pilot_fixture_scheduler",Pooling=false,Timezone="UTC"};
        await using var scheduler=NpgsqlDataSource.Create(settings.ConnectionString);
        await using var runtime=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(settings.ConnectionString){Username="pilot_fixture_runtime"}.ConnectionString);
        await new LifecycleScheduleProcessor(scheduler,runtime,manifest).Process(claim,default);
        if(!await new JobWorkerStore(scheduler,true).LifecycleCompleted(claim,default))throw new Exception("The scheduled native action and complete occurrence did not commit together.");
        Console.WriteLine("PASS managed lifecycle scheduler: exact saved source, shared product discovery/review/execution and atomic occurrence receipt");
    }
}
