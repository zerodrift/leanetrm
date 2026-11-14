using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasFuturesDeliveryChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Physical gas action checks require an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var handler=new GasFuturesDeliveryActions(database);var position=fixture.Id("positionId");
        var context=new LifecycleReadContext(fixture.Id("contextId"),fixture.Instant("at"),new(2026,11,6),Guid.NewGuid());
        var selection=new GasFuturesPerformanceSelection(position,"synthetic_managed_delivery",5,"0.25",fixture.Instant("performedAt"),
            fixture.Id("evidenceId"),fixture.Instant("performedAt"),[]);
        var request=new LifecycleActionRequest(Guid.NewGuid(),context,new(GasFuturesDeliveryActions.Handler,position,position),"gas.futures.delivery",
            fixture.Id("policyId"),1,false,new('0',64),JsonSerializer.SerializeToElement(selection,GasOwnedOperations.Json));
        var review=await database.WithIdentity("trader",null,(connection,ct)=>handler.Preview(connection,request,ct),default);
        var principal=review.Details.GetProperty("principal");
        if(!review.Details.GetProperty("willChange").GetBoolean()||principal.GetProperty("performances").GetArrayLength()!=4||
            principal.GetProperty("performances")[3].GetProperty("contribution").GetString()!="-1.51")
            throw new Exception("The physical action review lost its complete native principal prefix or independent consideration.");
        var accepted=request with{PreviewHash=review.Hash};
        try
        {
            await database.WithIdentity("trader",null,(connection,ct)=>handler.Execute(connection,accepted with{PreviewHash=new('f',64)},ct),default);
            throw new Exception("A physical performance accepted another review.");
        }
        catch(MarketDataException error)when(error.Code=="futures_delivery_stale_review"){}
        var first=await database.WithIdentity("trader",null,(connection,ct)=>handler.Execute(connection,accepted,ct),default);
        var replay=await database.WithIdentity("trader",null,(connection,ct)=>handler.Execute(connection,accepted,ct),default);
        if(first.Outcome.GetRawText()!=replay.Outcome.GetRawText()||first.Outcome.GetProperty("principalVersion").GetInt32()!=6)
            throw new Exception("Physical performance replay lost its complete first outcome.");
        // The old source excludes the effect just accepted. An existing fact
        // must acknowledge it before any attempt to reread mutable source heads.
        var same=await database.WithIdentity("trader",null,(connection,ct)=>handler.Execute(connection,accepted with{OperationId=Guid.NewGuid()},ct),default);
        if(same.Outcome.GetProperty("changed").GetBoolean()||!same.Outcome.GetProperty("semanticAcknowledgement").GetBoolean())
            throw new Exception("A repeated observed delivery created stock or principal again.");
        Console.WriteLine("PASS physical gas action: closed typed review, native-source principal plan, stale review refusal, atomic title/cash and first outcomes before source reread");
    }
}
