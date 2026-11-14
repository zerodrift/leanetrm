using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using System.Reflection;
using System.Text.Json;
using Npgsql;

internal static class LifecycleActionChecks
{
    internal static void Run()
    {
        var from=TradingInstant.Parse("2020-12-18T16:00:00Z","test");
        var until=from.AddHours(1);
        var policy=new LifecycleActionPolicy(Guid.NewGuid(),1,LifecycleExecutionMode.Review,from,until,LifecycleActionPolicy.Version);
        policy.Require(from,false,true);
        Refuses(()=>policy.Require(until,false,true),"lifecycle_action_window");
        Refuses(()=>policy.Require(from,true,true),"lifecycle_automation_policy");
        Refuses(()=>policy.Require(from,false,false),"lifecycle_review_required");
        var automatic=policy with{Mode=LifecycleExecutionMode.Automatic};
        automatic.Require(from,true,true);
        Refuses(()=>LifecycleActionCatalog.RequireAutomatic("option.exercise"),"lifecycle_automation_unsupported");
        LifecycleActionCatalog.RequireAutomatic("option.expiry");
        Require(OptionLifecycleActions.WindowContains("option.expiry",until,until)&&
            !OptionLifecycleActions.WindowContains("option.exercise",until,until)&&
            OptionLifecycleActions.WindowContains("option.exercise_instruction",until,until)&&
            OptionLifecycleActions.WindowContains("option.abandon_instruction",until,until)&&
            !OptionLifecycleActions.WindowContains("option.exercise_instruction",until.AddTicks(10),until),
            "Discovery preserves the native inclusive instruction cutoff and exclusive exercise expiry.");
        var registry=new LifecycleActionRegistry([]);
        Refuses(()=>registry.Require("absent"),"lifecycle_action_unsupported");
        var actions=AssistantOperations.ForPage("lifecycle").ToDictionary(o=>o.Path);
        Require(actions.TryGetValue("lifecycle/discover",out var discover)&&!discover.RequiresApproval&&!discover.CurrentOnly,
            "Lifecycle discovery uses the exact pinned read context.");
        Require(actions.TryGetValue("lifecycle/execute",out var execute)&&execute.RequiresApproval&&execute.CurrentOnly&&
            !AssistantOperations.NeedsApprovalReceipt(execute),"Lifecycle execution retains native idempotence behind ordinary exact-request approval.");
        Require(AssistantWorkspace.CanOpen("lifecycle",["ReadMarketData","ReadTrades"])&&
            !AssistantWorkspace.CanOpen("lifecycle",["ReadMarketData"]),"Lifecycle opening requires market and trade access.");
        var schema=AssistantOperations.Schema(typeof(LifecycleActionChecks).GetMethod(nameof(Handler),BindingFlags.Static|BindingFlags.NonPublic)!);
        var selection=schema["body"]!["properties"]!["selection"]!;
        Require(selection["discriminator"]!.GetValue<string>()=="target.handler"&&selection["contracts"]!["listed_option"] is not null&&
            selection["contracts"]!["financial_swap"] is not null&&selection["contracts"]!["futures"] is not null,
            "Assistant lifecycle payloads describe their closed product-owned selection contracts.");
        Console.WriteLine("PASS lifecycle action contracts: explicit half-open windows, reviewed execution, automatic capability and closed discovery");
    }
    static void Handler(LifecycleActionRequest body){}
    internal static async Task RunNative(string path)
    {
        using var packet=JsonDocument.Parse(File.ReadAllText(path));var f=packet.RootElement;
        var manifest=f.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(f.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Lifecycle checks require an owned disposable cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var operations=new LifecycleOperations(database,_=>throw new Exception("This option action consumes no host pricing frame."));
        var request=f.GetProperty("request").Deserialize<LifecycleActionRequest>(LifecycleNative.Json)!;
        if(f.TryGetProperty("wrappedRead",out var wrapped)&&wrapped.GetBoolean())
        {
            try
            {
                _=await database.WithIdentity("trader",null,async(c,t)=>
                {
                    var review=await operations.Preview(c,request,t);
                    await using var delay=ScheduleStore.Command(c,"SELECT pg_sleep(4)");await delay.ExecuteNonQueryAsync(t);
                    return new{review.Hash};
                },default);
                throw new Exception("A lifecycle review chained a successor grant through its original read deadline.");
            }
            catch(PostgresException error)when(error.MessageText=="futures_margin_denied"){}
            _=await database.WithIdentity("trader",null,async(c,t)=>new{(await operations.Preview(c,request,t)).Hash},default);
            Console.WriteLine("PASS lifecycle original read authority: shaped result refuses successor chaining; a fresh authorized review succeeds");return;
        }
        var discovery=await database.WithIdentity("trader",null,(c,t)=>operations.Discover(c,new(request.Context,request.Target),t),default);
        var offer=discovery.Actions.Single(a=>a.Action=="option.exercise");
        Require(offer.Eligible&&offer.Subject is {Reference:not "",Product:not "",Book:not ""}&&offer.Selection is not null&&
            offer.PolicyId==request.PolicyId&&offer.RequiredInputs.SequenceEqual(new[]{"evidenceId","lots"})&&offer.AvailableQuantity=="1",
            "Native discovery must name the exact held subject and expose typed configured selection coordinates.");
        Require(discovery.Choices.PolicyImplementation==LifecycleActionPolicy.Version&&discovery.Choices.Actions.Any(a=>a.Code=="option.expiry"&&a.Automatic)&&
            discovery.MarginPolicies.Any(p=>p.Id("id")==f.Id("marginPolicyId")),"Lifecycle settings must expose authoritative supported choices and retained margin policies.");
        Require(discovery.OptionSources.Any(p=>p.Text("curve_code")=="nymex_ng_alternate")&&discovery.FuturesSetup.Any(),
            "Lifecycle setup must expose exact native source choices and held futures independently of an action picker.");
        var retainedPolicy=await database.WithIdentity("editor",null,(c,t)=>LifecycleFuturesSettings.Save(c,database,request.Context,
            f.GetProperty("marginPolicySave"),true,()=>throw new Exception("The original settings frame was evicted."),t),default);
        Require(retainedPolicy.Outcome.Id("policyId")==f.Id("marginPolicyId"),"Settings replay must recover its first native outcome before mutable frame lookup.");
        var review=await database.WithIdentity("trader",null,(c,t)=>operations.Preview(c,request,t),default);
        request=request with{PreviewHash=review.Hash};
        var first=await database.WithIdentity("trader",null,(c,t)=>operations.Execute(c,request,t),default);
        var retry=await database.WithIdentity("trader",null,(c,t)=>operations.Execute(c,request,t),default);
        Require(JsonElement.DeepEquals(first.Outcome,retry.Outcome),"The shared owned adapter must preserve its first native outcome on retry.");
        var same=await database.WithIdentity("trader",null,(c,t)=>operations.Execute(c,request with{OperationId=Guid.NewGuid()},t),default);
        Require(JsonElement.DeepEquals(first.Outcome,same.Outcome),"A new request for the same event evidence must acknowledge the original economic fact.");
        Console.WriteLine("PASS owned lifecycle adapter: exact native discovery, configured choices, reviewed exercise, first-outcome retry and same-fact acknowledgement");
    }
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Refuses(Action action,string code)
    {
        try{action();throw new Exception("Expected "+code);}
        catch(MarketDataException error)when(error.Code==code){}
    }
}
