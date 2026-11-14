using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;

// Fixture writes go through the same reviewed business operation as the UI.
// The caller supplies observed quantities and title/supply, never principal.
internal static class GasPhysicalCompletionFixture
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Physical completion requires its owned disposable fixture.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var at=fixture.Instant("at");var selection=fixture.GetProperty("selection").Deserialize<GasFuturesPerformanceSelection>(GasOwnedOperations.Json)!;
        var context=new LifecycleReadContext(fixture.Id("contextId"),at,DateWorkspace.Date(fixture.Text("businessDate")),Guid.NewGuid());
        var frame=new InputFrame(context.SnapshotId!.Value,"Synthetic observed physical completion",at,context.BusinessDate,[],
            ImmutableDictionary<string,ImmutableArray<Guid>>.Empty);
        var operations=new LifecycleOperations(database,id=>id==frame.Id?frame:throw new Exception("Unexpected physical fixture source."));
        var request=new LifecycleActionRequest(Guid.NewGuid(),context,new(GasFuturesDeliveryActions.Handler,selection.PositionId,selection.PositionId),
            "gas.futures.delivery",fixture.Id("policyId"),1,false,new('0',64),JsonSerializer.SerializeToElement(selection,GasOwnedOperations.Json));
        var review=await database.WithIdentity("trader",null,(connection,ct)=>operations.Preview(connection,request,ct),default);
        var accepted=request with{PreviewHash=review.Hash};
        var first=await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted,ct),default);
        var replay=await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted,ct),default);
        if(!first.Outcome.GetProperty("changed").GetBoolean()||!JsonElement.DeepEquals(first.Outcome,replay.Outcome))
            throw new Exception("Observed physical completion lost its exact accepted first outcome.");
        Console.WriteLine("NATIVE_PHYSICAL_COMPLETION "+first.Outcome.GetRawText());
    }
}
