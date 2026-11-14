using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasQuantityBatchChecks
{
    internal static async Task Contract()
    {
        var account=Guid.NewGuid();var context=new LifecycleReadContext(Guid.NewGuid(),new(2026,11,6,16,0,0,TimeSpan.Zero),new(2026,11,6));
        var request=new LifecycleActionRequest(Guid.NewGuid(),context,new(GasFuturesQuantityBatchActions.Handler,account,account),
            GasFuturesReconciliationActions.Action,Guid.NewGuid(),1,false,new('0',64),JsonSerializer.SerializeToElement(new{corrections=new object?[]{null}}));
        try
        {
            await new GasFuturesQuantityBatchActions(null!).Preview(null!,request,default);
            throw new Exception("A null correction row reached source admission.");
        }
        catch(MarketDataException error)when(error.Code=="gas_quantity_input"){}
        Console.WriteLine("PASS batch contract: null correction rows receive a structured refusal before source admission");
    }
    internal static async Task Run(string path)
    {
        await Contract();
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Batch quantity checks require an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var context=new LifecycleReadContext(fixture.Id("contextId"),fixture.Instant("at"),new(2026,11,6),Guid.NewGuid());
        var frame=new InputFrame(context.SnapshotId!.Value,"Synthetic linked physical correction",context.AsOf,context.BusinessDate,[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context.ContextId);
        var operations=new LifecycleOperations(database,id=>id==frame.Id?frame:throw new Exception("Batch review requested an unrelated host source."));
        var body=fixture.GetProperty("selection");
        // Exercise the public identifier through the actual default registry.
        var request=new LifecycleActionRequest(fixture.Id("operationId"),context,new("gas_futures_reconciliation_batch",fixture.Id("accountId"),fixture.Id("accountId")),
            GasFuturesReconciliationActions.Action,fixture.Id("policyId"),1,false,new('0',64),body.Clone());
        var review=await database.WithIdentity("trader",null,(connection,ct)=>operations.Preview(connection,request,ct),default);
        var plans=review.Details.GetProperty("principal").EnumerateArray().ToImmutableArray();
        if(!review.Details.GetProperty("willChange").GetBoolean()||plans.Length!=2||plans.Select(p=>p.Id("positionId")).Distinct().Count()!=2)
            throw new Exception("The batch review must contain one complete principal prefix per changed obligation.");
        var adjustment=plans.SelectMany(p=>p.GetProperty("performances").EnumerateArray())
            .Aggregate(default(ExactDecimal),(sum,p)=>sum+ExactDecimal.Parse(p.Text("adjustment")));
        if(adjustment!=ExactDecimal.Parse("1.50"))throw new Exception("The linked eighth-unit prefix must refund exactly 1.50, without another dispatch payment.");
        if(review.Details.GetProperty("sourceAllocations").GetArrayLength()!=2)
            throw new Exception("Review both the withdrawn source and the explicitly replaced dispatch funding.");
        var accepted=request with{PreviewHash=review.Hash};
        try
        {
            await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted with{PreviewHash=new('f',64)},ct),default);
            throw new Exception("A batch accepted a different complete-prefix review hash.");
        }
        catch(MarketDataException error)when(error.Code=="gas_quantity_stale_review"){}
        var first=await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted,ct),default);
        var evicted=new LifecycleOperations(database,_=>throw new Exception("A committed batch retry tried to reload its evicted frame."));
        var replay=await database.WithIdentity("trader",null,(connection,ct)=>evicted.Execute(connection,accepted,ct),default);
        if(first.Outcome.GetRawText()!=replay.Outcome.GetRawText()||!first.Outcome.GetProperty("changed").GetBoolean()||first.Outcome.GetProperty("corrections").GetArrayLength()!=2)
            throw new Exception("The atomic linked correction lost its native first outcome.");
        var acknowledgement=await database.WithIdentity("trader",null,(connection,ct)=>evicted.Execute(connection,accepted with{OperationId=Guid.NewGuid()},ct),default);
        if(acknowledgement.Outcome.GetProperty("changed").GetBoolean()||!acknowledgement.Outcome.GetProperty("semanticAcknowledgement").GetBoolean())
            throw new Exception("An accepted linked correction created another financial batch.");
        Console.WriteLine("PASS managed linked batch: default registry, complete source and per-obligation principal review, same producer, stale hash, atomic first outcome and evicted-source retry");
    }
}
