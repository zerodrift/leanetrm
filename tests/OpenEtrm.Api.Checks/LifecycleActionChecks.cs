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
        Require(actions.TryGetValue("lifecycle/gas/setup",out var gasSetup)&&!gasSetup.RequiresApproval&&!gasSetup.CurrentOnly,
            "Gas form choices use the same source-pinned authorized lifecycle read for assistants and UI.");
        Require(actions.TryGetValue("lifecycle/execute",out var execute)&&execute.RequiresApproval&&execute.CurrentOnly&&
            !AssistantOperations.NeedsApprovalReceipt(execute),"Lifecycle execution retains native idempotence behind ordinary exact-request approval.");
        Require(actions.TryGetValue("jobs/lifecycle/create",out var task)&&task.RequiresApproval&&task.CurrentOnly&&!AssistantOperations.NeedsApprovalReceipt(task),
            "The lifecycle assistant schedules through the same exact typed native task operation behind approval.");
        Require(AssistantWorkspace.CanOpen("lifecycle",["ReadMarketData","ReadTrades"])&&
            !AssistantWorkspace.CanOpen("lifecycle",["ReadMarketData"]),"Lifecycle opening requires market and trade access.");
        var schema=AssistantOperations.Schema(typeof(LifecycleActionChecks).GetMethod(nameof(Handler),BindingFlags.Static|BindingFlags.NonPublic)!);
        var selection=schema["body"]!["properties"]!["selection"]!;
        Require(selection["discriminator"]!.GetValue<string>()=="target.handler"&&selection["contracts"]!["listed_option"] is not null&&
            selection["contracts"]!["financial_swap"] is not null&&selection["contracts"]!["futures"] is not null&&
            selection["contracts"]![GasFuturesDeliveryActions.Handler]!["contracts"]!["gas.futures.delivery"]!["properties"]!["supply"] is not null,
            "Assistant lifecycle payloads describe their closed product-owned selection contracts.");
        var shaped=LifecycleSettingsTransport.Integers([JsonSerializer.SerializeToElement(new{revision="3",policy_revision="2",amount="0.12345678901234567890123456789"})],"revision","policy_revision").Single();
        Require(shaped.GetProperty("revision").GetInt32()==3&&shaped.GetProperty("policy_revision").GetInt32()==2&&
            shaped.GetProperty("amount").ValueKind==JsonValueKind.String&&shaped.Text("amount")=="0.12345678901234567890123456789",
            "Lifecycle settings expose structural versions as integer metadata and preserve financial decimal text exactly.");
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
        if(f.TryGetProperty("historyContext",out var selectedHistory))
        {
            var source=selectedHistory.Deserialize<LifecycleReadContext>(LifecycleNative.Json)!;
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic retained option source",source.AsOf,source.BusinessDate,[],
                System.Collections.Immutable.ImmutableDictionary<string,System.Collections.Immutable.ImmutableArray<Guid>>.Empty,ContextId:source.ContextId);
            var history=await database.WithIdentity("trader",null,async(c,t)=>
            {
                await using var capture=ScheduleStore.Command(c,"SELECT settlement_data.futures_margin_preview_authority($1)",manifest.CatalogId);
                var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(t))!);
                database.RegisterCommitAdmission(c,new LifecycleReadAdmission(manifest.CatalogId,until));
                var context=(await new KnowledgeStore(manifest.CatalogId).Read(c,source.ContextId,t)).Context;
                await PinnedContext.BindExact(c,manifest.CatalogId,context,t);
                await TradingContext.Bind(c,manifest.CatalogId,context,frame.At,t);
                var positions=await FuturesOptionPositions.Read(c,manifest,[],[],t,trades:System.Collections.Immutable.ImmutableHashSet.Create(request.Target.ScopeId));
                var position=positions.Single();
                Require(position.OpenLots==position.BookedLots,"Future-effective accepted events must not close the opening right early.");
                var data=await database.Read(c,t,observationIds:[],referenceScope:ReferenceReadScope.TradeValuation,observationScope:ObservationReadScope.ExactPins);
                var known=await FuturesOptionKnownHistoryStore.Read(c,database,data,frame,context,position,t);
                var snapshot=data.Snapshot(frame).WithKnowledge(source.ContextId,SchedulePreview.ParseAt(context.KnownAt));
                foreach(var causal in known.Events.Where(e=>e.Future is not null))
                {
                    var child=await FuturesKnownHistoryStore.ReadLinkedFuture(c,database,data,frame,snapshot,causal,t);
                    Require(child.Owner.TradeId==causal.Future!.Trade.Id&&child.Terms.Policy.Id==causal.Future.MarginPolicy.Id,
                        "Source-only future history must retain the exact already-booked child and adopted policy.");
                }
                var original=known.Events.First();
                async Task Refuse(Func<Task> read)
                {
                    await using(var save=ScheduleStore.Command(c,"SAVEPOINT forged_linked_source"))await save.ExecuteNonQueryAsync(t);
                    try{await read();throw new Exception("A forged linked-future source was accepted.");}
                    catch(PostgresException error)when(error.MessageText=="futures_known_history_context"){}
                    catch(MarketDataException error)when(error.Code is "futures_known_history_context" or "futures_margin_state"){}
                    finally
                    {
                        await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT forged_linked_source;RELEASE SAVEPOINT forged_linked_source");
                        await restore.ExecuteNonQueryAsync(t);
                    }
                }
                await Refuse(()=>FuturesKnownHistoryStore.ReadLinkedFuture(c,database,data,frame,snapshot,original with{EventId=Guid.NewGuid()},t));
                await Refuse(()=>FuturesKnownHistoryStore.ReadLinkedFuture(c,database,data,frame with{ContextId=Guid.NewGuid()},snapshot,original,t));
                var future=original.Future!;var held=future.Trade.Instruments.Single();var execution=FuturesExecution.Require(held);
                var forged=FuturesExecution.Bind(held.Id,execution.MarketBinding,execution.Lots(held.Quantity)*ExactDecimal.Parse("2"),
                    execution.ExecutionPrice,execution.ExecutedAt,FuturesSettlementState.Unprocessed,held.RetainedInstrumentId);
                await Refuse(()=>FuturesKnownHistoryStore.ReadLinkedFuture(c,database,data,frame,snapshot,
                    original with{Future=future with{Trade=new Trade(future.Trade.Id,future.Trade.BookId,[forged],future.Trade.Booking)}},t));
                return known;
            },default);
            Require(history.Events.Length==2&&history.Events.All(e=>e.EffectiveAt>frame.At&&e.Future is not null)&&
                history.Events.Select(e=>e.Future!.Trade.Id).Distinct().Count()==2,
                "Knowledge-only option history must retain every future-effective actual event and its independent native future without closing the opening right.");
            Console.WriteLine("PASS known option source: future-effective accepted events, exact causal futures and margin policy, unchanged opening lots");return;
        }
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
        var status=await database.WithIdentity("trader",null,(c,t)=>new FuturesOptionStore(database).Status(c,request.OperationId,t),default);
        Require(status.GetProperty("found").GetBoolean()&&status.Id("eventId")==first.Outcome.Id("eventId"),"Owned status must recover the exact actor's accepted option event with final native admission.");
        var other=await database.WithIdentity("editor",null,(c,t)=>new FuturesOptionStore(database).Status(c,request.OperationId,t),default);
        Require(!other.GetProperty("found").GetBoolean(),"An authorized reader cannot recover another actor's command receipt.");
        var retry=await database.WithIdentity("trader",null,(c,t)=>operations.Execute(c,request,t),default);
        Require(JsonElement.DeepEquals(first.Outcome,retry.Outcome),"The shared owned adapter must preserve its first native outcome on retry.");
        var acknowledged=request with{OperationId=Guid.NewGuid()};
        var same=await database.WithIdentity("trader",null,(c,t)=>operations.Execute(c,acknowledged,t),default);
        var expected=System.Text.Json.Nodes.JsonNode.Parse(first.Outcome.GetRawText())!.AsObject();
        expected["operationId"]=acknowledged.OperationId;expected["acknowledgedOperationId"]=request.OperationId;expected["semanticAcknowledgement"]=true;
        Require(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected,RiskJobs.Json),same.Outcome),
            "A new command must retain its own identity while acknowledging the unchanged original event and linked future.");
        Console.WriteLine("PASS owned lifecycle adapter: exact native discovery, configured choices, reviewed exercise, first-outcome retry and same-fact acknowledgement");
    }
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Refuses(Action action,string code)
    {
        try{action();throw new Exception("Expected "+code);}
        catch(MarketDataException error)when(error.Code==code){}
    }
}
