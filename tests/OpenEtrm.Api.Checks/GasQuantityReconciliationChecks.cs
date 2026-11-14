using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

internal static class GasQuantityReconciliationChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Quantity reconciliation checks require an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var context=new LifecycleReadContext(fixture.Id("contextId"),fixture.Instant("at"),new(2026,11,6),Guid.NewGuid());
        var frame=new InputFrame(context.SnapshotId!.Value,"Synthetic quantity correction",context.AsOf,context.BusinessDate,[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context.ContextId);
        var operations=new LifecycleOperations(database,id=>id==frame.Id?frame:throw new Exception("Quantity correction requested a different host source."));
        var selection=new GasFuturesQuantityReconciliationSelection(fixture.Id("positionId"),fixture.Id("performanceId"),fixture.GetProperty("revision").GetInt32(),
            fixture.GetProperty("principalVersion").GetInt32(),"0.125",false,fixture.Id("evidenceId"),[],GasSourceAllocation.Preserve,"Synthetic reviewed partial-title correction");
        var request=new LifecycleActionRequest(Guid.NewGuid(),context,new(GasFuturesReconciliationActions.Handler,selection.PositionId,selection.PerformanceId),
            GasFuturesReconciliationActions.Action,fixture.Id("policyId"),1,false,new('0',64),JsonSerializer.SerializeToElement(selection,GasOwnedOperations.Json));
        var setup=await database.WithIdentity("trader","ReadGasOwnedHoldings",(connection,ct)=>GasOwnedSetup.Read(connection,database,new(context,request.Target),ct),default);
        var fields=setup.Forms[request.Action].Fields;
        var selectedVersion=JsonSerializer.SerializeToElement(fields.Single(f=>f.Name=="observedPerformance").Choices.Single().Value,GasOwnedOperations.Json);
        if(selectedVersion.Id("positionId")!=selection.PositionId||selectedVersion.Id("performanceId")!=selection.PerformanceId||
            selectedVersion.GetProperty("expectedRevision").GetInt32()!=selection.ExpectedRevision||
            !setup.Policies.Any(p=>p.Action==request.Action&&p.Id==request.PolicyId.ToString()&&p.Revision==request.PolicyRevision))
            throw new Exception("The actual correction form lost its exact native performance revision or saved bound action policy.");
        var review=await database.WithIdentity("trader",null,(connection,ct)=>operations.Preview(connection,request,ct),default);
        if(!review.Details.GetProperty("willChange").GetBoolean()||review.Details.GetProperty("principal").GetProperty("performances").GetArrayLength()!=4)
            throw new Exception("Quantity review lost its complete retained native principal prefix.");
        var adjustments=review.Details.GetProperty("principal").GetProperty("performances").EnumerateArray()
            .Aggregate(default(ExactDecimal),(total,row)=>total+ExactDecimal.Parse(row.Text("adjustment")));
        if(adjustments!=ExactDecimal.Parse("0.75"))throw new Exception("An eighth-unit title reduction at 6.03 must create a 0.75 net contractual principal refund.");
        var allocation=review.Details.GetProperty("sourceAllocations");var source=allocation.GetProperty("sources").EnumerateArray().Single();
        if(allocation.Text("policy")!=GasSourceAllocation.Preserve||source.Text("oldCapacity")!="0.25"||source.Text("capacity")!="0.125"||
            source.Text("previouslyUnused")!="0"||source.Text("unused")!="0")throw new Exception("The review omitted exact source capacities or invented unallocated stock.");
        var accepted=request with{PreviewHash=review.Hash};
        try
        {
            await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted with{PreviewHash=new('f',64)},ct),default);
            throw new Exception("A quantity correction accepted a different review hash.");
        }
        catch(MarketDataException error)when(error.Code=="gas_quantity_stale_review"){}
        var first=await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted,ct),default);
        var withoutFrame=new LifecycleOperations(database,_=>throw new Exception("A committed quantity retry attempted to resolve an evicted frame."));
        var replay=await database.WithIdentity("trader",null,(connection,ct)=>withoutFrame.Execute(connection,accepted,ct),default);
        if(first.Outcome.GetRawText()!=replay.Outcome.GetRawText()||first.Outcome.GetProperty("currentRevision").GetInt32()!=selection.ExpectedRevision+1)
            throw new Exception("Quantity reconciliation lost its complete native first outcome after frame eviction.");
        var acknowledgement=await database.WithIdentity("trader",null,(connection,ct)=>withoutFrame.Execute(connection,accepted with{OperationId=Guid.NewGuid()},ct),default);
        if(acknowledgement.Outcome.GetProperty("changed").GetBoolean()||!acknowledgement.Outcome.GetProperty("semanticAcknowledgement").GetBoolean())
            throw new Exception("An existing corrected title created another financial revision.");
        Console.WriteLine("PASS managed quantity correction: complete source/principal review, same native producer, stale-hash refusal, atomic first outcome and evicted-frame retry");
    }
}
