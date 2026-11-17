using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GasTransportFinancialExpirySelection(Guid ExpectedFinancialStateId,string Evidence);

internal sealed class GasTransportFinancialActions(PreviewDatabase database):ILifecycleActionHandler
{
    internal const string Handler="gas_transport_financial",Expire="gas.transport_financial.expire";
    public string Kind=>Handler;
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","AmendTrade"];
    internal static JsonElement Command(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,Handler);
        if(request.Automatic||request.Action!=Expire)throw new MarketDataException("lifecycle_policy","Select the installed reviewed Transport expiry action.");
        try
        {
            var selection=request.Selection.Deserialize<GasTransportFinancialExpirySelection>(GasOwnedOperations.Json)??throw GasTransportFinancialStore.Source();
            return GasTransportFinancialCommands.Normalize(JsonSerializer.SerializeToElement(new{kind="expire",tradeId=request.Target.ScopeId,
                selection.ExpectedFinancialStateId,request.PolicyId,request.PolicyRevision,selection.Evidence},GasOwnedOperations.Json));
        }
        catch(JsonException error){throw new MarketDataException("gas_transport_financial_input","Select this exact financial state and expiry evidence.",error);}
    }

    internal static async Task<JsonElement> ReadSource(NpgsqlConnection c,PreviewDatabase database,LifecycleReadContext context,Guid trade,CancellationToken ct)
    {
        var source=await LifecycleNative.Source(c,database,context,ct);
        await using(var save=ScheduleStore.Command(c,"SAVEPOINT transport_financial_lifecycle_source"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await PinnedContext.BindExact(c,database.Manifest.CatalogId,source,ct);
            await TradingContext.Bind(c,database.Manifest.CatalogId,source,context.AsOf,ct);
            var agreement=await GasTransportFinancialStore.Read(c,database,context,trade,ct);
            var books=(await BookScope.Read(c,database.Manifest.CatalogId,ct)).ToImmutableDictionary(book=>book.Id("id"));
            return WithBook(agreement,books);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT transport_financial_lifecycle_source;RELEASE SAVEPOINT transport_financial_lifecycle_source");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    internal static void RequireTarget(LifecycleTarget target,JsonElement outcome)
    {
        if(target.Handler!=Handler||outcome.Id("tradeId")!=target.ScopeId||outcome.Id("instrumentId")!=target.BindingId)throw GasTransportFinancialStore.Source();
    }
    static JsonElement WithBook(JsonElement source,ImmutableDictionary<Guid,JsonElement> books)
    {
        if(!books.TryGetValue(source.Id("bookId"),out var book)||string.IsNullOrWhiteSpace(book.Text("name")))
            throw GasTransportFinancialStore.Source();
        var view=JsonNode.Parse(source.GetRawText())!.AsObject();view["bookName"]=book.Text("name");
        return JsonSerializer.SerializeToElement(view,GasOwnedOperations.Json);
    }
    static string Hash(LifecycleActionRequest request,JsonElement source)
    {
        var stable=JsonNode.Parse(source.GetRawText())!.AsObject();stable.Remove("authorityUntil");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{
            request.Context,request.Target,request.Action,request.PolicyId,request.PolicyRevision,request.Selection,source=stable},GasOwnedOperations.Json))));
    }
    static DateTimeOffset RequireEvent(PreviewDatabase database,LifecycleActionRequest request,JsonElement command,JsonElement source)
    {
        RequireTarget(request.Target,source);
        var facts=GasTransportFinancialAdapter.Read(database.Manifest.CatalogId,source);
        if(facts.State is not {Status:"live"} state||state.Id!=command.Id("expectedFinancialStateId")||
            request.Context.AsOf<state.Through)throw new MarketDataException("gas_transport_financial_expiry","The selected live rights state has not reached its contractual expiry event.");
        return state.Through;
    }
    async Task<JsonElement> RecordNative(NpgsqlConnection c,LifecycleActionRequest request,JsonElement command,DateTimeOffset at,CancellationToken ct)
    {
        var outcome=await LifecycleNative.Read(c,"SELECT security.record_gas_transport_financial($1,$2,$3::jsonb,$4,$5,$6)::text",ct,
            database.Manifest.CatalogId,request.OperationId,command.GetRawText(),at,request.Context.ContextId,request.Context.AsOf);
        RequireTarget(request.Target,outcome);
        var receipt=JsonNode.Parse(outcome.GetRawText())!.AsObject();
        receipt["operationId"]=JsonSerializer.SerializeToNode(request.OperationId,GasOwnedOperations.Json);
        return JsonSerializer.SerializeToElement(receipt,GasOwnedOperations.Json);
    }

    async Task<JsonElement> AcceptedExpiry(NpgsqlConnection c,LifecycleActionRequest request,JsonElement command,CancellationToken ct)
    {
        var accepted=await LifecycleNative.Read(c,"SELECT security.gas_transport_financial_accepted_expiry($1,$2::jsonb)::text",ct,
            database.Manifest.CatalogId,command.GetRawText());
        if(accepted.GetProperty("found").GetBoolean())
        {
            RequireTarget(request.Target,accepted.GetProperty("outcome"));
            _=TradingInstant.Parse(accepted.Text("effectiveAt"),"gas_transport_financial_source");
        }
        return accepted;
    }

    public async Task<LifecycleActionReview> Preview(NpgsqlConnection c,LifecycleActionRequest request,CancellationToken ct)
    {
        var command=Command(request);var admission=await new GasTransportFinancialStore(database).Capture(c,request.Target.ScopeId,"expire",ct);
        await GasTransportFinancialStore.RequireCoordinates(c,database.Manifest.CatalogId,request.Context,ct);
        var accepted=await AcceptedExpiry(c,request,command,ct);
        var acknowledgement=accepted.GetProperty("found").GetBoolean();
        var source=acknowledgement?accepted:await ReadSource(c,database,request.Context,request.Target.ScopeId,ct);
        var at=acknowledgement?TradingInstant.Parse(accepted.Text("effectiveAt"),"gas_transport_financial_source"):RequireEvent(database,request,command,source);
        await using(var save=ScheduleStore.Command(c,"SAVEPOINT transport_expiry_preview"))await save.ExecuteNonQueryAsync(ct);
        try{_=await RecordNative(c,request,command,at,ct);}
        finally
        {
            await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT transport_expiry_preview;RELEASE SAVEPOINT transport_expiry_preview");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var offer=acknowledgement?new LifecycleActionOffer(request.Target,Expire,"Acknowledge accepted Transport expiry",
            command.Id("expectedFinancialStateId"),at,null,true,null,1,accepted.GetProperty("outcome").GetProperty("revision").GetInt32()):Offer(source,request.Context);
        return new(offer with{PolicyId=request.PolicyId,PolicyRevision=request.PolicyRevision,Selection=request.Selection},Hash(request,source),
            JsonSerializer.SerializeToElement(new{acknowledgement,overviewSections=new[]{new{label="Transport expiry review",values=new object[]{
                new{label="Rights close at",value=LifecycleNative.Instant(at),kind="instant"},
                new{label="Result",value=acknowledgement?"Retain the accepted expiry without another rights revision":"The accepted closed rights state has zero remaining service value"},
                new{label="Independent obligations",value="Fee payments and physical or owned-gas activity retain their own records"}}}}},GasOwnedOperations.Json),
            database.Manifest.CatalogId,admission.Until);
    }

    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection c,LifecycleActionRequest request,CancellationToken ct)
    {
        var command=Command(request);var admission=await new GasTransportFinancialStore(database).Capture(c,request.Target.ScopeId,"expire",ct);
        await GasTransportFinancialStore.RequireCoordinates(c,database.Manifest.CatalogId,request.Context,ct);
        var prior=await LifecycleNative.Read(c,"SELECT security.gas_transport_financial_status($1,$2)::text",ct,database.Manifest.CatalogId,request.OperationId);
        DateTimeOffset at;
        if(prior.GetProperty("found").GetBoolean())
            at=TradingInstant.Parse(prior.GetProperty("request").Text("effectiveAt"),"gas_transport_financial_source");
        else
        {
            var accepted=await AcceptedExpiry(c,request,command,ct);
            var acknowledgement=accepted.GetProperty("found").GetBoolean();
            var source=acknowledgement?accepted:await ReadSource(c,database,request.Context,request.Target.ScopeId,ct);
            at=acknowledgement?TradingInstant.Parse(accepted.Text("effectiveAt"),"gas_transport_financial_source"):RequireEvent(database,request,command,source);
            if(Hash(request,source)!=request.PreviewHash)throw new MarketDataException("lifecycle_preview_stale","The exact financial state or reviewed Transport expiry changed. Review it again.");
        }
        return new(await RecordNative(c,request,command,at,ct),admission);
    }

    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection c,LifecycleReadContext context,LifecycleTarget? target,LifecycleDiscoveryAccess access,CancellationToken ct)
    {
        await new GasTransportFinancialStore(database).Capture(c,target?.ScopeId,null,ct);
        var books=(await BookScope.Read(c,database.Manifest.CatalogId,ct)).ToImmutableDictionary(book=>book.Id("id"));
        ImmutableArray<JsonElement> rows;
        if(target is not null)
        {
            if(target.Handler!=Handler)throw GasTransportFinancialStore.Source();
            rows=[await GasTransportFinancialStore.Read(c,database,context,target.ScopeId,ct)];
            RequireTarget(target,rows[0]);
        }
        else
        {
            if(books.Count==0)return [];
            var directory=await LifecycleNative.Read(c,"SELECT security.gas_transport_financial_directory($1,$2,$3,$4::uuid[],NULL)::text",ct,
                database.Manifest.CatalogId,context.ContextId,context.AsOf,books.Keys.ToArray());
            rows=directory.EnumerateArray().ToImmutableArray();
        }
        foreach(var row in rows)
        {
            GasTransportFinancialStore.RequireSource(row,context,row.Id("tradeId"),row.Instant("knownAt"));
            database.RegisterCommitAdmission(c,new GasTransportFinancialReadAdmission(database.Manifest.CatalogId,context.ContextId,context.AsOf,row.Instant("authorityUntil")));
        }
        return [..rows.Select(row=>Offer(WithBook(row,books),context))];
    }

    internal static LifecycleActionOffer Offer(JsonElement source,LifecycleReadContext context)
    {
        var state=source.GetProperty("financialState");var future=state.ValueKind==JsonValueKind.Null;
        if(future)state=source.GetProperty("stateHistory").EnumerateArray().OrderBy(s=>s.GetProperty("revision").GetInt32()).Last();
        var at=state.Instant("through");var closed=state.Text("status")=="closed";
        var policy=source.GetProperty("policies").EnumerateArray().Where(p=>p.Text("action")==Expire&&p.Text("mode") is "manual" or "review"&&
            p.Instant("from_at")<=context.AsOf&&context.AsOf<p.Instant("until_at")).OrderBy(p=>p.Id("id")).FirstOrDefault();
        var reason=future?"financial_state_future":closed?"gas_transport_financial_closed":context.AsOf<at?"gas_transport_financial_not_due":
            policy.ValueKind==JsonValueKind.Undefined?"lifecycle_configuration_required":null;
        var target=new LifecycleTarget(Handler,source.Id("tradeId"),source.Id("instrumentId"));
        if(string.IsNullOrWhiteSpace(source.Text("bookName")))throw GasTransportFinancialStore.Source();
        return new(target,Expire,"Expire Transport rights",state.Id("id"),at,null,reason is null,reason,1,state.GetProperty("revision").GetInt32())
            {Subject=new(TradingReference.Trade(source.GetProperty("tradeReference").GetInt32()),"Transport",source.Id("bookId"),source.Text("bookName"))};
    }
}
