using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasQuantityBundleChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Mixed gas corrections require an owned disposable database.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var context=new LifecycleReadContext(fixture.Id("contextId"),fixture.Instant("at"),new(2026,11,6),Guid.NewGuid());
        var frame=new InputFrame(context.SnapshotId!.Value,"Synthetic mixed title and dispatch correction",context.AsOf,context.BusinessDate,[],
            ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context.ContextId);
        var operations=new LifecycleOperations(database,id=>id==frame.Id?frame:throw new Exception("An unrelated source frame was requested."));
        var account=fixture.Id("accountId");
        var request=new LifecycleActionRequest(fixture.Id("operationId"),context,new("gas_quantity_bundle",account,account),
            "gas.owned.reconcile_quantity",fixture.Id("policyId"),1,false,new('0',64),fixture.GetProperty("selection").Clone());
        var discovery=await database.WithIdentity("trader",null,(connection,ct)=>operations.Discover(connection,new(context,null),ct),default);
        if(!discovery.Actions.Any(offer=>offer.Target==request.Target&&offer.Action==request.Action&&offer.Eligible))
            throw new Exception("The ordinary registry did not offer the exact linked native title and dispatch correction.");
        var review=await database.WithIdentity("trader",null,(connection,ct)=>operations.Preview(connection,request,ct),default);
        if(!review.Details.GetProperty("willChange").GetBoolean()||review.Details.GetProperty("ordinary").GetProperty("sourceAllocations").GetArrayLength()!=1||
            review.Details.GetProperty("physical").GetProperty("sourceAllocations").GetArrayLength()!=1||
            review.Details.GetProperty("outcome").GetProperty("ordinary").GetProperty("positions")[0].Text("currentQuantity")!="0.125")
            throw new Exception("The mixed review omitted one product's explicit source allocation or revised title.");
        var accepted=request with{PreviewHash=review.Hash};
        try
        {
            await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted with{PreviewHash=new('f',64)},ct),default);
            throw new Exception("A mixed correction accepted another reviewed hash.");
        }
        catch(MarketDataException error)when(error.Code=="gas_quantity_stale_review"){}
        var first=await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted,ct),default);
        var evicted=new LifecycleOperations(database,_=>throw new Exception("A mixed correction retry reloaded an evicted frame."));
        var replay=await database.WithIdentity("trader",null,(connection,ct)=>evicted.Execute(connection,accepted,ct),default);
        if(first.Outcome.GetRawText()!=replay.Outcome.GetRawText()||!first.Outcome.GetProperty("changed").GetBoolean()||
            first.Outcome.GetProperty("ordinary").GetProperty("positions")[0].GetProperty("currentRevision").GetInt32()!=2||
            first.Outcome.GetProperty("physical").GetProperty("corrections")[0].GetProperty("currentRevision").GetInt32()!=2)
            throw new Exception("The complete native first outcome was lost on mixed correction retry.");
        var ack=await database.WithIdentity("trader",null,(connection,ct)=>evicted.Execute(connection,accepted with{OperationId=Guid.NewGuid()},ct),default);
        if(ack.Outcome.GetProperty("changed").GetBoolean()||!ack.Outcome.GetProperty("semanticAcknowledgement").GetBoolean())
            throw new Exception("An existing mixed fact created another financial publication.");
        Console.WriteLine("PASS managed mixed correction: both source allocation reviews, same atomic native producer, stale hash refusal, evicted-source first outcome and publication-free acknowledgement");
    }
}
