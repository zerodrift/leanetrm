using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasOwnedScopeAdmissionChecks
{
    internal static async Task Run(string path)
    {
        using var packet=JsonDocument.Parse(File.ReadAllText(path));var fixture=packet.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Gas scope authority checks require an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var denied=false;var testCase=fixture.Text("testCase");
        if(testCase is not("original" or "changed_source" or "gas_successor" or "no_gas"))throw new Exception("Select one closed scope admission test case.");
        try
        {
            await database.WithIdentity("trader",null,async(connection,ct)=>
            {
                // Deliberately register no earlier gas admission: this directly
                // callable scope boundary must preserve its own original term.
                var context=fixture.Id("contextId");var at=fixture.Instant("at");
                var source=(await new KnowledgeStore(manifest.CatalogId).Read(connection,context,ct)).Context;
                await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);
                await TradingContext.Bind(connection,manifest.CatalogId,source,at,ct);
                var frame=new InputFrame(Guid.NewGuid(),"Synthetic scope authority",at,PublishedSchedules.ContextDate(source),[],
                    ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context);
                var present=await GasOwnedKnownHistoryStore.RequireScope(connection,database,frame,[fixture.Id("bookId")],testCase!="no_gas",ct);
                if(present!=(testCase!="no_gas"))
                    throw new Exception("The scope authority fixture changed its exact native gas population.");
                if(testCase=="changed_source")
                {
                    var changed=(await new KnowledgeStore(manifest.CatalogId).Read(connection,fixture.Id("otherContextId"),ct)).Context;
                    await PinnedContext.BindExact(connection,manifest.CatalogId,changed,ct);
                    await TradingContext.Bind(connection,manifest.CatalogId,changed,at,ct);
                }
                return true;
            },default);
        }
        catch(PostgresException error)when(error.SqlState=="42501")
        {
            denied=true;
        }
        if(denied!=(testCase is "changed_source" or "gas_successor"))
            throw new Exception(denied?"The original exact gas scope fixture was refused before the delayed authority case.":
                "Gas projection scope replaced its original gas deadline with a successor after base-authority work.");
        Console.WriteLine(testCase switch{
            "original"=>"PASS exact gas projection scope and its original registered commit admission",
            "changed_source"=>"PASS gas projection scope commit callback retains its original exact source tuple",
            "no_gas"=>"PASS an explicit source scope without gas does not capture an unrelated gas permission deadline",
            _=>"PASS gas projection scope retains original gas authority across base-authority work despite an active successor"});
    }
}
