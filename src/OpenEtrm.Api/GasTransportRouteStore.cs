using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasTransportRouteReadRequest([property:JsonRequired]FacilityQuantityContext Context,Guid RouteId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasTransportRouteWriteRequest([property:JsonRequired]FacilityQuantityContext Context,[property:JsonRequired]string OwnerId,
    Guid OperationId,[property:JsonRequired]string EffectiveAt,[property:JsonRequired]JsonElement Command);

internal sealed record GasTransportRouteAdmission(Guid Catalog,string? Kind,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection c,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.require_gas_transport_route_commit($1,$2,$3)",Catalog,(object?)Kind??DBNull.Value,Until);
        await q.ExecuteNonQueryAsync(ct);
    }
}

public sealed partial class GasTransportRouteStore(PreviewDatabase database)
{
    private readonly OperationalBusinessContext contexts=new(database);
    private Guid Catalog=>database.Manifest.CatalogId;
    internal static string Permission(JsonElement command)
    {
        if(command.ValueKind!=JsonValueKind.Object||!command.TryGetProperty("kind",out var value)||value.ValueKind!=JsonValueKind.String)throw Invalid();
        return value.GetString() switch{"define"=>"ConfigureGasServices","plan" or "release"=>"RecordGasOperations",_=>throw Invalid()};
    }
    private static MarketDataException Invalid()=>new("gas_transport_route_input","Supply the complete explicit route operation, owning account and operating business context.");
    private static MarketDataException Source()=>new("gas_transport_route_source","Retain the requested route, exact definition and complete certified source envelope.");
    private static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,GasOwnedOperations.Json);
    private Task<JsonElement> Native(NpgsqlConnection c,string sql,CancellationToken ct,params object[] args)=>LifecycleNative.Read(c,sql,ct,args);
    private Task<JsonElement> Facts(NpgsqlConnection c,Guid route,CancellationToken ct)=>Native(c,"SELECT security.read_gas_transport_route($1,$2)::text",ct,Catalog,route);
    private Task<JsonElement> Outcome(NpgsqlConnection c,Guid operation,CancellationToken ct)=>Native(c,"SELECT security.gas_transport_route_status($1,$2)::text",ct,Catalog,operation);
    private async Task<bool> PriorFact(NpgsqlConnection c,JsonElement command,DateTimeOffset at,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.gas_transport_route_prior_fact($1,$2::jsonb,$3)",Catalog,command.GetRawText(),at);
        return (bool)(await q.ExecuteScalarAsync(ct))!;
    }
    private async Task Capture(NpgsqlConnection c,string? kind,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.gas_transport_route_authority($1,$2)",Catalog,(object?)kind??DBNull.Value);
        var until=new DateTimeOffset((DateTime)(await q.ExecuteScalarAsync(ct))!);
        database.RegisterCommitAdmission(c,new GasTransportRouteAdmission(Catalog,kind,until));
    }
    private async Task<string> Owner(NpgsqlConnection c,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.gas_transport_route_owner($1)",Catalog);
        return (string)(await q.ExecuteScalarAsync(ct))!;
    }
    private async Task RequireOwner(NpgsqlConnection c,string expected,CancellationToken ct)
    {
        if(expected is null||expected!=await Owner(c,ct))
            throw new MarketDataException("gas_transport_route_owner_changed","The account changed. Return to the original account to reconcile this request.");
    }
    private async Task<(ScheduleContext Context,DateTimeOffset At,bool Working)> Bind(NpgsqlConnection c,FacilityQuantityContext request,ScheduleContext? selected,CancellationToken ct)
    {
        var scope=await contexts.Resolve(c,request,selected,"gas_transport_route_context",ct);
        await contexts.BindResolved(c,scope,ct);return scope;
    }
    internal static void RequireSource(JsonElement facts,ScheduleContext context,DateTimeOffset at,Guid route)
    {
        try
        {
            var future=facts.GetProperty("hasFutureHeads").GetBoolean();var header=facts.GetProperty("route");var definition=facts.GetProperty("definition");
            if(route==Guid.Empty||header.Id("id")!=route||header.Id("definition_id")==Guid.Empty||
                (definition.ValueKind==JsonValueKind.Null?!future:definition.Id("route_id")!=route||definition.Id("id")!=header.Id("definition_id")))throw Source();
            if(facts.Id("contextId")!=context.ContextId||TradingInstant.Parse(facts.Text("asOf"),"gas_transport_route_context")!=at||context.KnownAt is null||
                TradingInstant.Parse(facts.Text("knownAt"),"gas_transport_route_context")!=TradingInstant.Parse(context.KnownAt,"gas_transport_route_context"))
                throw new MarketDataException("gas_transport_route_context","The route source does not retain the exact selected business and knowledge context.");
        }
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException){throw Source();}
    }
    internal static GasTransportRouteAdmission StatusAdmission(Guid catalog,JsonElement status)
    {
        try
        {
            var absent=status.TryGetProperty("found",out var found)&&!found.GetBoolean();
            var kind=absent?null:status.Text("kind");if(!absent&&kind is not("define" or "plan" or "release"))throw Source();
            return new(catalog,kind,TradingInstant.Parse(status.Text("authorityUntil"),"gas_transport_route_source"));
        }
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException){throw Source();}
    }
    public async Task<JsonElement> Status(NpgsqlConnection c,FacilityStatusRequest request,CancellationToken ct)
    {
        if(request.OperationId==Guid.Empty)throw Invalid();
        // Native status captures nullable conditional authority before any
        // waiting scope or owner read. Retain that first deadline through the
        // remaining managed work, including an absent receipt's read bound.
        var value=await Outcome(c,request.OperationId,ct);
        var admission=StatusAdmission(Catalog,value);database.RegisterCommitAdmission(c,admission);
        await RequireOwner(c,request.OwnerId,ct);await admission.RequireCommit(c,ct);
        if(!value.GetProperty("found").GetBoolean())return value;
        return Json(new{operationId=request.OperationId,routeId=value.Id("routeId"),kind=value.Text("kind"),outcome=value.GetProperty("outcome"),
            command=GasTransportRouteCommands.Normalize(request.OperationId,value.GetProperty("command")),effectiveAt=TradingInstant.Parse(value.Text("effectiveAt"),"gas_transport_route_context")});
    }
    public static string Refusal(string code)=>code switch{
        "gas_transport_route_stale"=>"The route, source capacity, claim or membership head changed. Refresh the exact source and review again.",
        "gas_transport_route_conflict"=>"This request or business revision has different terms. Reconcile the original request or submit a reviewed revision.",
        "gas_transport_route_denied"=>"This operation requires current authority over the complete route, source scopes and exact references.",
        "gas_transport_route_future_source"=>"A latest known source is not yet effective in this business context. Select a supported later context for adoption.",
        "gas_transport_route_membership"=>"A selected logical capacity-plan stream is unavailable or already assigned. Review its exact current membership.",
        "gas_transport_route_conservation"=>"The selected claims do not conserve the declared fuel, loss, transit or intermediate-node quantities.",
        "gas_transport_route_limit"=>"This route list exceeds the installed complete discovery scope. A searched discovery operation is required before selecting from it.",
        _=>"Supply complete explicit graph, source, membership and operating terms under the installed route laws. The operation was refused."};
}
