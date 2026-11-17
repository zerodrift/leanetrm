using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData.Trading;

// The native qualifier provides exact certified sources and independent expected
// outcomes. This caller exercises the ordinary managed command/receipt boundary.
internal static class GasTransportFinancialNativeChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(path));var packet=document.RootElement;
        var host=Path.GetFullPath(packet.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Financial Transport qualification requires its isolated owned PostgreSQL cluster.");
        var manifest=packet.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var store=new GasTransportFinancialStore(database);
        var context=new LifecycleReadContext(packet.Id("contextId"),TradingInstant.Parse(packet.Text("at"),"gas_transport_financial_input"),
            DateWorkspace.Date(packet.Text("businessDate")));
        var mode=packet.Text("mode");
        if(mode=="entry-authority")
        {
            var seconds=packet.GetProperty("holdSeconds").GetInt32();
            if(seconds is <0 or >15)throw new Exception("Select a bounded owned authority wait.");
            await database.WithIdentity(packet.OptionalText("profile")??"trader","ReadTrades",async(c,ct)=>{
                await GasTransportFinancialEntry.Capture(c,database,true,ct);
                Console.WriteLine("FINANCIAL_ENTRY_CAPTURED");
                await using(var wait=ScheduleStore.Command(c,"SELECT pg_sleep($1)",seconds))await wait.ExecuteNonQueryAsync(ct);
                var source=await LifecycleNative.Source(c,database,context,ct);
                await PinnedContext.BindExact(c,manifest.CatalogId,source,ct);
                await TradingContext.Bind(c,manifest.CatalogId,source,context.AsOf,ct);
                return await LifecycleNative.Read(c,"SELECT security.gas_transport_financial_capacity_facts($1,$2,$3,$4)::text",ct,
                    manifest.CatalogId,context.ContextId,context.AsOf,packet.Id("entitlementId"));
            },default);
            Console.WriteLine("FINANCIAL_ENTRY_READ_SUCCEEDED");return;
        }
        var trade=packet.Id("tradeId");
        if(mode=="lifecycle")
        {
            await GasTransportFinancialLifecycleChecks.Run(database,packet,context,trade);
            return;
        }
        if(mode=="read")
        {
            var row=await database.WithIdentity("trader","ReadTrades",async(c,ct)=>{
                var source=(await new KnowledgeStore(manifest.CatalogId).Read(c,context.ContextId,ct)).Context;
                await PinnedContext.BindExact(c,manifest.CatalogId,source,ct);
                await TradingContext.Bind(c,manifest.CatalogId,source,context.AsOf,ct);
                return await GasTransportFinancialStore.Read(c,database,context,trade,ct);
            },default);
            var actualState=row.GetProperty("financialState");
            if((actualState.ValueKind==JsonValueKind.Null?null:actualState.Text("status"))!=packet.OptionalText("expectedStateStatus")||
                row.OptionalText("unpricedReason")!=packet.OptionalText("expectedUnpricedReason"))
                throw new Exception("The ordinary financial reader lost the independently expected state or missing-mark reason.");
            Console.WriteLine("PASS managed native financial Transport read: exact current/historical state and explicit complete-value availability");return;
        }
        if(mode!="command")throw new Exception("Select the installed native financial read or command qualification mode.");
        var operation=packet.Id("operationId");var command=packet.GetProperty("command");
        if(command.Id("tradeId")!=trade)throw new Exception("Retain the exact financial qualification target.");
        var owner=await database.WithIdentity("trader","ReadTrades",async(c,ct)=>{
            await using var q=ScheduleStore.Command(c,"SELECT security.gas_transport_financial_owner($1)",manifest.CatalogId);
            return (string)(await q.ExecuteScalarAsync(ct))!;
        },default);
        var request=new GasTransportFinancialWriteRequest(context,owner,operation,packet.Text("effectiveAt"),command);
        var review=await database.WithIdentity("trader","AmendTrade",(c,ct)=>store.Preview(c,request,ct),default);
        var expected=packet.GetProperty("expectedChanged").GetBoolean();
        if(review.GetProperty("acknowledgement").GetBoolean()==expected)
            throw new Exception("The managed review misreported a new effect versus an acknowledgement.");
        var before=await database.WithIdentity("trader","ReadTrades",(c,ct)=>store.Status(c,new(operation,owner),ct),default);
        if(before.GetProperty("found").GetBoolean())throw new Exception("A financial preview persisted its provisional command.");
        var outcome=await database.WithIdentity("trader","AmendTrade",(c,ct)=>store.Record(c,request,ct),default);
        if(outcome.Id("tradeId")!=trade||outcome.GetProperty("changed").GetBoolean()!=expected)
            throw new Exception("The ordinary financial command differs from the expected native effect.");
        var saved=await database.WithIdentity("trader","ReadTrades",(c,ct)=>store.Status(c,new(operation,owner),ct),default);
        var replay=await database.WithIdentity("trader","AmendTrade",(c,ct)=>store.Record(c,request,ct),default);
        if(!saved.GetProperty("found").GetBoolean()||!JsonElement.DeepEquals(saved.GetProperty("outcome"),outcome)||!JsonElement.DeepEquals(replay,outcome))
            throw new Exception("Financial request recovery did not retain the first exact accepted outcome.");
        var acknowledged=await database.WithIdentity("trader","AmendTrade",(c,ct)=>store.Preview(c,request,ct),default);
        if(!acknowledged.GetProperty("acknowledgement").GetBoolean())
            throw new Exception("An exact accepted financial request must review as acknowledgement even when its original outcome reports changed=true.");
        try
        {
            _=await database.WithIdentity("trader","AmendTrade",(c,ct)=>store.Record(c,request with{Context=context with{BusinessDate=context.BusinessDate.AddDays(1)}},ct),default);
            throw new Exception("A recovered request reinterpreted its immutable certificate under a different business date.");
        }
        catch(OpenEtrm.MarketData.MarketDataException error)when(error.Code=="gas_transport_financial_context"){}
        Console.WriteLine("PASS managed native financial Transport command: rollback-only preview, truthful effect, original receipt and exact replay");
    }
}
