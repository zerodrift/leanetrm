using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;

internal static class SelectedPhysicalAuthorityChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;var host=fixture.Text("host");
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Selected physical authority checks require an owned disposable cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var mode=fixture.Text("mode");var profile=mode=="policy_save"?"editor":"trader";
        var deny=fixture.GetProperty("deny").GetBoolean();var delay=fixture.GetProperty("delay").GetBoolean();
        var refused=false;
        try
        {
            await database.WithIdentity(profile,null,async(connection,ct)=>
            {
                object result;
                if(mode=="discovery")
                {
                    var selected=fixture.GetProperty("request").Deserialize<LifecycleActionRequest>(LifecycleNative.Json)!.Context;
                    var operations=new LifecycleOperations(database,id=>
                    {
                        if(delay)Thread.Sleep(6000);
                        return new(id,"Synthetic delayed discovery",selected.AsOf,selected.BusinessDate,[],
                            ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,selected.ContextId);
                    });
                    var discovery=await operations.Discover(connection,new(selected,null),ct);
                    if(fixture.GetProperty("expectedGas").GetBoolean())
                    {
                        if(discovery.DeliveryProfiles.IsEmpty||discovery.DeliveryAccounts.IsEmpty)
                            throw new Exception("A fresh authorized discovery lost its explicit physical choices.");
                    }
                    else if(!discovery.DeliveryProfiles.IsEmpty||!discovery.DeliveryAccounts.IsEmpty||discovery.Actions.Any(a=>a.Target.Handler is "gas_owned" or "gas_futures_delivery" or "futures"))
                        throw new Exception("Discovery added physical data using a permission acquired after source admission.");
                    result=new{discovery.Context};
                }
                else if(mode is "policy_save" or "adopt")
                {
                    var body=fixture.GetProperty("request");
                    result=mode=="policy_save"?await FuturesMarginCommands.SavePolicy(connection,manifest.CatalogId,body,ct):
                        await FuturesMarginCommands.Adopt(connection,manifest.CatalogId,body,ct);
                    await using var flush=ScheduleStore.Command(connection,"SET CONSTRAINTS ALL IMMEDIATE");await flush.ExecuteNonQueryAsync(ct);
                }
                else if(mode=="option_preview")
                {
                    var request=fixture.GetProperty("request").Deserialize<LifecycleActionRequest>(LifecycleNative.Json)!;
                    var prepared=await new OptionLifecycleActions(database,_=>{}).Preview(connection,request,ct);
                    result=new{prepared.Offer,prepared.Hash,prepared.Details};
                }
                else throw new Exception("Select a qualified physical authority check mode.");
                if(delay&&mode!="discovery")
                {
                    await using(var label=ScheduleStore.Command(connection,"SELECT set_config('application_name',$1,true)",fixture.Text("application")))await label.ExecuteNonQueryAsync(ct);
                    await using var wait=ScheduleStore.Command(connection,"SELECT pg_sleep(6)");await wait.ExecuteNonQueryAsync(ct);
                }
                return result;
            },default);
        }
        catch(PostgresException error)when(error.SqlState=="42501"&&error.MessageText is "gas_owned_denied" or "futures_margin_denied" or "lifecycle_denied"){refused=true;}
        if(refused!=deny)throw new Exception("Selected physical authority was extended by a successor grant or lost before owned commit: "+mode);
        Console.WriteLine("PASS selected physical original authority and current permission: "+mode);
    }
}
