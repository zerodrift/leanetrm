using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasOwnedQuantityCorrectionChecks
{
    internal static async Task Contract()
    {
        var account=Guid.NewGuid();var context=new LifecycleReadContext(Guid.NewGuid(),new(2020,12,20,16,0,0,TimeSpan.Zero),new(2020,12,20));
        var request=new LifecycleActionRequest(Guid.NewGuid(),context,new(GasOwnedQuantityReconciliationActions.Handler,account,account),
            GasOwnedQuantityReconciliationActions.Action,Guid.NewGuid(),1,false,new('0',64),
            JsonSerializer.SerializeToElement(new{positions=new object?[]{null},consumptions=Array.Empty<object>(),evidence="Synthetic explicit correction"}));
        try
        {
            await new GasOwnedQuantityReconciliationActions(null!).Preview(null!,request,default);
            throw new Exception("A null ordinary correction row reached source admission.");
        }
        catch(MarketDataException error)when(error.Code=="gas_quantity_input"){}
        Console.WriteLine("PASS ordinary correction contract: a null row receives a structured refusal before source admission");
    }
    internal static async Task Run(string path)
    {
        await Contract();
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Ordinary gas correction checks require an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var context=new LifecycleReadContext(fixture.Id("contextId"),fixture.Instant("at"),new(2020,12,20),Guid.NewGuid());
        var frame=new InputFrame(context.SnapshotId!.Value,"Synthetic corrected owned title",context.AsOf,context.BusinessDate,[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context.ContextId);
        var operations=new LifecycleOperations(database,id=>id==frame.Id?frame:throw new Exception("Ordinary correction requested an unrelated source frame."));
        var account=fixture.Id("accountId");
        var request=new LifecycleActionRequest(fixture.Id("operationId"),context,new("gas_owned_quantity_reconciliation",account,account),
            "gas.owned.reconcile_quantity",fixture.Id("policyId"),1,false,new('0',64),fixture.GetProperty("selection").Clone());
        var review=await database.WithIdentity("trader",null,(connection,ct)=>operations.Preview(connection,request,ct),default);
        if(!review.Details.GetProperty("willChange").GetBoolean()||review.Details.GetProperty("sourceAllocations").GetArrayLength()!=1||
            review.Details.GetProperty("outcome").GetProperty("positions")[0].Text("currentQuantity")!="0.875")
            throw new Exception("Review the explicit revised seven-eighths title and its complete source allocation.");
        var accepted=request with{PreviewHash=review.Hash};
        try
        {
            await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted with{PreviewHash=new('f',64)},ct),default);
            throw new Exception("An ordinary correction accepted a different reviewed source or quantity.");
        }
        catch(MarketDataException error)when(error.Code=="gas_quantity_stale_review"){}
        var unknown=JsonSerializer.SerializeToElement(new{positions=new[]{new{positionId=Guid.NewGuid(),expectedRevision=2,quantity="0.875",retracted=false,
            evidenceId=fixture.GetProperty("selection").GetProperty("positions")[0].Id("evidenceId"),sourceAllocation="preserve_unallocated_source_quantity_v1"}},
            consumptions=Array.Empty<object>(),evidence="Synthetic unknown native title"});
        try
        {
            await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted with{OperationId=Guid.NewGuid(),Selection=unknown},ct),default);
            throw new Exception("An unknown native title reached correction execution.");
        }
        catch(PostgresException error)when(error.SqlState=="P0001"&&error.MessageText=="gas_quantity_source"){}
        var first=await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted,ct),default);
        var evicted=new LifecycleOperations(database,_=>throw new Exception("An ordinary correction retry reloaded its evicted source frame."));
        var replay=await database.WithIdentity("trader",null,(connection,ct)=>evicted.Execute(connection,accepted,ct),default);
        if(first.Outcome.GetRawText()!=replay.Outcome.GetRawText()||!first.Outcome.GetProperty("changed").GetBoolean()||
            first.Outcome.GetProperty("positions")[0].GetProperty("currentRevision").GetInt32()!=3)
            throw new Exception("An ordinary correction lost its exact atomic native first outcome.");
        var acknowledged=await database.WithIdentity("trader",null,(connection,ct)=>evicted.Execute(connection,accepted with{OperationId=Guid.NewGuid()},ct),default);
        if(acknowledged.Outcome.GetProperty("changed").GetBoolean()||!acknowledged.Outcome.GetProperty("semanticAcknowledgement").GetBoolean())
            throw new Exception("An existing ordinary source fact created another financial publication.");
        Console.WriteLine("PASS managed ordinary correction: default registry, complete source allocation review, same native producer, stale hash/unknown source refusal and evicted-source first outcome");
    }
}
