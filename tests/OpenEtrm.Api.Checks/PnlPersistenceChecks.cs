using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class PnlPersistenceChecks
{
    internal static async Task Run(string path)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new Exception("Use the owned preview runner.");
        await using var database=new PreviewDatabase(root,"exchange-manifest.json");
        using var fixture=JsonDocument.Parse(File.ReadAllText(path));var request=fixture.RootElement;
        var command=request.GetProperty("command").Deserialize<SavePnlReportRequest>(RiskJobs.Json)!;
        var id=request.GetProperty("id").GetGuid();var expected=request.GetProperty("report");
        // Reconstruct the normal provider in another process from the owned private key store.
        var provider=DataProtectionProvider.Create(ApplicationDataProtection.PreviewKeys(root),b=>ApplicationDataProtection.ConfigurePreview(b,root));
        using var store=new PnlReportArtifacts(database,provider.CreateProtector(ApplicationDataProtection.PnlPurpose));
        await database.WithIdentity("risk","RunScenario",async(connection,ct)=>
        {
            var owner=await PnlReportArtifacts.Owner(connection,ct);
            try{store.Open(command.Artifact,owner,DateTimeOffset.UtcNow,false);throw new Exception("A new process unexpectedly retained another process's unsaved CPU memo.");}
            catch(MarketDataException e)when(e.Code=="pnl_artifact"){}
            using var foreign=new PnlReportArtifacts(database,new EphemeralDataProtectionProvider().CreateProtector(ApplicationDataProtection.PnlPurpose));
            try{await foreign.Read(connection,id,ct);throw new Exception("A different key authenticated a durable report.");}
            catch(MarketDataException e)when(e.Code=="pnl_saved_integrity"){}
            var read=await store.Read(connection,id,ct);
            if(!JsonElement.DeepEquals(read.Report,expected))throw new Exception("Durable result changed across a process restart.");
            var replay=JsonSerializer.SerializeToElement(await store.Save(connection,command,ct),RiskJobs.Json);
            if(!JsonElement.DeepEquals(replay.GetProperty("report"),expected))throw new Exception("First-outcome replay changed across restart.");
            var noOp=JsonSerializer.SerializeToElement(await store.Save(connection,command with{RequestId=Guid.NewGuid()},ct),RiskJobs.Json);
            if(!JsonElement.DeepEquals(noOp.GetProperty("report"),expected))throw new Exception("Same-fact replay across restart changed the saved result.");
            // An authenticated result with a new identity has never been saved.
            var unsavedReport=System.Text.Json.Nodes.JsonNode.Parse(expected.GetRawText())!;
            unsavedReport["id"]=Guid.NewGuid();
            var unavailable=store.Seal(owner,new(JsonSerializer.SerializeToElement(unsavedReport),[]),DateTimeOffset.UtcNow);
            await using(var otherDatabase=new PreviewDatabase(root))
            using(var otherCatalogue=new PnlReportArtifacts(otherDatabase,provider.CreateProtector(ApplicationDataProtection.PnlPurpose)))
            {
                try{otherCatalogue.Open(unavailable,owner,DateTimeOffset.UtcNow,false);throw new Exception("A result ticket authenticated in another catalogue.");}
                catch(MarketDataException e)when(e.Code=="pnl_artifact"){}
            }
            store.Release(unavailable);
            try{await store.Save(connection,new(Guid.NewGuid(),"Unretained result",unavailable),ct);throw new Exception("A lost unsaved result was saved without its calculation.");}
            catch(MarketDataException e)when(e.Code=="pnl_unsaved"){}
            // Native row-write capability cannot turn a Run ticket into an
            // authenticated durable result, even with copied native context FKs.
            await using(var savepoint=ScheduleStore.Command(connection,"SAVEPOINT forged_pnl"))await savepoint.ExecuteNonQueryAsync(ct);
            try
            {
                var forged=Guid.NewGuid();
                await using(var insert=ScheduleStore.Command(connection,"""
                    INSERT INTO risk.saved_pnl_reports(catalog_id,id,request_id,tenant_id,workspace_id,owner_id,name,opening_context_id,closing_context_id,result_hash,artifact_hash,artifact,saved_at)
                    SELECT catalog_id,$3,$4,tenant_id,workspace_id,owner_id,name,opening_context_id,closing_context_id,result_hash,artifact_hash,$5,saved_at
                    FROM risk.saved_pnl_reports WHERE(catalog_id,id)=($1,$2)
                    """,database.Manifest.CatalogId,id,forged,Guid.NewGuid(),command.Artifact))await insert.ExecuteNonQueryAsync(ct);
                try{await store.Read(connection,forged,ct);throw new Exception("A native row authenticated a transport ticket as a saved result.");}
                catch(MarketDataException e)when(e.Code=="pnl_saved_integrity"){}
            }
            finally{await using var rollback=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT forged_pnl; RELEASE SAVEPOINT forged_pnl");await rollback.ExecuteNonQueryAsync(ct);}
            return true;
        },CancellationToken.None);
        Console.WriteLine("PASS durable P&L reads, command replay and same-fact no-op survive process restart under retained private protection keys without pricing");
    }
}
