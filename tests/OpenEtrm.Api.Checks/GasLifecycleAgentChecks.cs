using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasLifecycleAgentChecks
{
    internal static Task Run(string path)=>Run(path,["ReadMarketData","ReadTrades","ReadGasOwnedHoldings","RecordGasOwnedHoldings"]);
    internal static Task RunConfigured(string path)=>Run(path,["ReadMarketData","ReadTrades","ReadGasOwnedHoldings","RecordGasOwnedHoldings","UseAssistantWorkspace"]);

    static async Task Run(string path,ImmutableArray<string> expectedPermissions)
    {
        using var packet=JsonDocument.Parse(File.ReadAllText(path));var f=packet.RootElement;
        var manifest=f.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(f.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Delegated gas checks require an owned disposable PostgreSQL cluster.");
        await using var database=new PreviewDatabase(host);
        if(database.Manifest.CatalogId!=manifest.CatalogId)throw new Exception("The delegated source differs from the owned manifest.");
        var context=new LifecycleReadContext(f.Id("contextId"),f.Instant("at"),new(2020,12,20),Guid.NewGuid());
        var frame=new InputFrame(context.SnapshotId!.Value,"Synthetic delegated owned title",context.AsOf,context.BusinessDate,[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context.ContextId);
        var operations=new LifecycleOperations(database,id=>id==frame.Id?frame:throw new Exception("The delegated request replaced its exact source frame."));
        var target=new LifecycleTarget(GasOwnedQuantityReconciliationActions.Handler,f.Id("accountId"),f.Id("accountId"));
        var request=new LifecycleActionRequest(f.Id("operationId"),context,target,GasOwnedQuantityReconciliationActions.Action,f.Id("policyId"),1,false,new('0',64),f.GetProperty("selection").Clone());
        var agent=new AgentIdentity(f.Text("principal"),f.Id("delegation"));
        JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,LifecycleNative.Json);
        async Task Denied(Func<Task<object>> work,string? code=null)
        {
            try{await work();}
            catch(PreviewDeniedException)when(code is null){return;}
            catch(PostgresException error)when(error.SqlState=="42501"&&code is null){return;}
            catch(MarketDataException error)when(error.Code==code){return;}
            throw new Exception("The delegated gas operation accepted a forbidden scope or authority: "+code);
        }
        if(f.TryGetProperty("deniedReplay",out var deniedReplay))
        {
            var retained=f.GetProperty("approvedRequest").Deserialize<LifecycleActionRequest>(LifecycleNative.Json)!;
            await Denied(()=>operations.ExecuteApproved(agent,retained,default));
            Console.WriteLine("PASS retained delegated gas receipt refuses current "+deniedReplay.GetString()+" loss without financial mutation");return;
        }
        var permissions=await database.WithAgent(agent,TimeSpan.FromSeconds(5),["ReadMarketData"],_=>{},async(connection,ct)=>
        {
            await using var read=ScheduleStore.Command(connection,"SELECT security.current_permissions()");
            return (string[])(await read.ExecuteScalarAsync(ct))!;
        },default);
        if(!permissions.ToHashSet().SetEquals(expectedPermissions))
            throw new Exception("The opted-in gas agent acquired a configuration, jobs or unrelated capability.");
        await database.WithIdentity("admin","ManageAccess",async(connection,ct)=>
        {
            var roles=await LifecycleNative.Read(connection,"SELECT security.administration_directory()::text",ct);
            var role=roles.GetProperty("roles").EnumerateArray().Single(r=>r.Text("code")=="gas_lifecycle_operator");
            if(role.GetProperty("assignable").GetBoolean())throw new Exception("The agent-only gas role was offered to human accounts.");
            return true;
        },default);
        try
        {
            await database.WithIdentity("admin","ManageAccess",async(connection,ct)=>
            {
                var body=Json(new{operationId=Guid.NewGuid(),action="create",login="synthetic_invalid_gas_agent",name="Synthetic invalid gas role",kind="human",enabled=true,roles=new[]{"gas_lifecycle_operator"}});
                await using var command=ScheduleStore.Command(connection,"SELECT security.apply_administration($1::jsonb)",body.GetRawText());
                return await command.ExecuteScalarAsync(ct);
            },default,Admission.Administration);
            throw new Exception("Human administration assigned the agent-only gas role.");
        }
        catch(PostgresException error)when(error.SqlState=="P0001"&&error.MessageText=="agent_managed"){}
        var read=Json(await operations.Preview(agent,request,default));
        var accepted=request with{PreviewHash=read.Text("hash")};
        await Denied(()=>operations.Preview(agent with{Principal=f.Text("readonlyPrincipal"),Delegation=f.Id("readonlyDelegation")},request,default));
        await Denied(()=>operations.Preview(agent with{Delegation=f.Id("revokedDelegation")},request,default));
        await Denied(()=>operations.Preview(agent with{Delegation=f.Id("foreignDelegation")},request,default));
        await Denied(()=>operations.Preview(agent,request with{Context=context with{ContextId=f.Id("otherContextId")}},default),"lifecycle_context");
        await Denied(()=>operations.ExecuteApproved(agent,accepted with{PreviewHash=new('f',64)},default),"gas_quantity_stale_review");
        var first=Json(await operations.ExecuteApproved(agent,accepted,default));
        var evicted=new LifecycleOperations(database,_=>throw new Exception("A delegated retry reloaded the source frame."));
        var retry=Json(await evicted.ExecuteApproved(agent,accepted,default));
        if(first.GetRawText()!=retry.GetRawText()||!first.GetProperty("changed").GetBoolean()||first.Id("operationId")!=request.OperationId)
            throw new Exception("A delegated ordinary correction lost its exact native first outcome.");
        await Denied(()=>evicted.ExecuteApproved(agent with{Principal=f.Text("readonlyPrincipal"),Delegation=f.Id("readonlyDelegation")},accepted,default));
        await Denied(()=>evicted.ExecuteApproved(agent with{Delegation=f.Id("revokedDelegation")},accepted,default));
        var saved=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(f.GetRawText())!;
        saved["approvedRequest"]=Json(accepted);File.WriteAllText(path,JsonSerializer.Serialize(saved,LifecycleNative.Json));
        Console.WriteLine("PASS opt-in delegated gas role: exact manual review/execute/first outcome, read-only/revoked/foreign authority and source refusal, human assignment exclusion");
    }
}
