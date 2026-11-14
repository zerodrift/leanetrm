using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record LifecycleNativeAdmission(Guid Catalog,Guid Operation,string Kind,JsonElement Request,string Permission,
    DateTimeOffset AuthorityUntil):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var finish=ScheduleStore.Command(connection,"SELECT lifecycle.finish_command($1,$2,$3,$4::jsonb,$5,$6)",
            Catalog,Operation,Kind,Request.GetRawText(),Permission,AuthorityUntil);
        await finish.ExecuteNonQueryAsync(ct);
    }
}
internal sealed record LifecycleActionReadAdmission(Guid Catalog,string Kind,JsonElement Request,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT lifecycle.require_action_read($1,$2,$3::jsonb,$4)",Catalog,Kind,Request.GetRawText(),Until);
        await command.ExecuteNonQueryAsync(ct);
    }
}
internal static class LifecycleNative
{
    internal static string Refusal(string code)=>code switch{
        "lifecycle_denied"=>"Your current authority or the original admission no longer permits this lifecycle operation.",
        "lifecycle_configuration_required" or "lifecycle_policy_required"=>"Select and save the explicit action policy and product settings before reviewing this action.",
        "lifecycle_context" or "lifecycle_preview_stale" or "lifecycle_source"=>"The lifecycle source or accepted state changed. Reload the actions and review the current inputs.",
        "lifecycle_conflict" or "lifecycle_command_conflict"=>"This request or revision changed. Retry the exact original request or reload the current state.",
        "lifecycle_action_window"=>"The selected policy does not permit this action at the chosen time. Check its effective window.",
        _=>"Check the selected lifecycle policy, product terms, source and action inputs. The operation was refused."};
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){AllowDuplicateProperties=false};
    internal static JsonElement Body(LifecycleActionRequest request,object selection)=>JsonSerializer.SerializeToElement(new{
        request.OperationId,context=new{request.Context.ContextId,asOf=Instant(request.Context.AsOf),businessDate=request.Context.BusinessDate.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture)},request.Target,request.Action,
        request.PolicyId,request.PolicyRevision,request.Automatic,request.PreviewHash,selection},Json);
    internal static string Instant(DateTimeOffset at)
    {
        if(!TradingInstant.IsSupported(at))throw new MarketDataException("lifecycle_instant","Supply an exact UTC microsecond instant.");
        return at.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",System.Globalization.CultureInfo.InvariantCulture);
    }
    internal static async Task<DateTimeOffset> Authority(NpgsqlConnection connection,Guid catalog,string permission,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT lifecycle.capture_authority($1,$2)",catalog,permission);
        return new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync(ct))!);
    }
    internal static async Task<DateTimeOffset> ActionAuthority(NpgsqlConnection connection,PreviewDatabase database,string kind,JsonElement body,CancellationToken ct)
    {
        var catalog=database.Manifest.CatalogId;
        await using var command=ScheduleStore.Command(connection,"SELECT lifecycle.capture_action_authority($1,$2,$3::jsonb)",catalog,kind,body.GetRawText());
        var until=new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync(ct))!);
        database.RegisterCommitAdmission(connection,new LifecycleActionReadAdmission(catalog,kind,body.Clone(),until));return until;
    }
    internal static async Task<JsonElement> Read(NpgsqlConnection connection,string sql,CancellationToken ct,params object[] values)
    {
        await using var command=ScheduleStore.Command(connection,sql,values);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    internal static async Task<ScheduleContext> Source(NpgsqlConnection connection,PreviewDatabase database,LifecycleReadContext request,CancellationToken ct)
    {
        var source=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,request.ContextId,ct)).Context;
        if(PublishedSchedules.ContextDate(source)!=request.BusinessDate)throw new MarketDataException("lifecycle_context","Retain the exact source business date.");
        await TradingContext.AdmitSource(database,connection,source,request.AsOf,ct);return source;
    }
    internal static void Require(LifecycleActionRequest request,string handler)
    {
        if(request.OperationId==Guid.Empty||request.Context is null||request.Context.ContextId==Guid.Empty||request.Context.AsOf==default||request.Target is null||
            request.Target.Handler!=handler||request.Target.ScopeId==Guid.Empty||request.Target.BindingId==Guid.Empty||request.PolicyId==Guid.Empty||request.PolicyRevision<1||
            request.Context.BusinessDate==default||request.Selection.ValueKind!=JsonValueKind.Object||request.PreviewHash is not {Length:64}||request.PreviewHash.Any(c=>!char.IsAsciiHexDigitLower(c)))
            throw new MarketDataException("lifecycle_input","Supply exact native scope, context, saved policy and review coordinates.");
        _=Instant(request.Context.AsOf);
    }
}
