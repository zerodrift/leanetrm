using Npgsql;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

public sealed record ContextCaptureRequest(string? EffectiveOn,string? KnownAt,string? Mode,string? TimeZone=null);
public sealed record KnowledgeSnapshot(ScheduleContext Context,DateTimeOffset CoverageFrom,DateTimeOffset CertifiedThrough)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RestoreKey {get;init;}
}

public sealed class KnowledgeResolver(Guid catalog)
{
    public Task<KnowledgeSnapshot> Capture(NpgsqlConnection connection,ContextCaptureRequest body,CancellationToken ct)=>
        WorkspaceReadContext.Resolve(connection,catalog,body,ct);
    internal Task<KnowledgeSnapshot> Source(NpgsqlConnection connection,ContextCaptureRequest body,CancellationToken ct)=>
        WorkspaceReadContext.Resolve(connection,catalog,body,ct,expose:false);
    // A correlation can resolve only within this operation or from its bounded
    // authenticated transport reference. It never looks up a saved object.
    internal Guid Catalog=>catalog;
    public async Task<KnowledgeSnapshot> Read(NpgsqlConnection connection,Guid id,CancellationToken ct)
    {
        KnowledgeSnapshot? selected=null;
        var operation=ReadReferences.Operation.Value;
        operation?.Sources.TryGetValue(id,out selected);
        if(operation?.Incoming.TryGetValue(id,out var readReference)==true)
        {
            var owner=WorkspaceReadContext.Page.Value??throw Error("read_reference","Open this read in its signed-in workspace.");
            selected=owner.Knowledge.References.Open(catalog,id,readReference);
        }
        if(selected is {Context.ReadCut:{} verified})
        {
            await verified.Verify(connection,catalog,selected.Context,ct);
            await verified.Register(connection,selected.Context,ct);
            return selected;
        }
        var captured=WorkspaceReadContext.Executing.Value;
        var selection=captured is null?WorkspaceReadContext.Page.Value?.Knowledge.Find(id):captured.Context.ContextId==id?captured:null;
        if(selection is {Context.ReadCut:{} cut} view)
        {
            await cut.Verify(connection,catalog,view.Context,ct);
            await cut.Register(connection,view.Context,ct);
            return view;
        }
        await using var command=ScheduleStore.Command(connection,"""
            SELECT effective_on,known_at,schedule_prefix,coverage_from,certified_through,system_id,timeline
            FROM security.operation_read_inputs WHERE catalog_id=$1 AND id=$2
            """,catalog,id);
        await using var row=await command.ExecuteReaderAsync(ct);
        if(!await row.ReadAsync(ct))throw Error("temporal_context","Supply complete input coordinates or this operation's exact read reference.");
        var resolvedCut=new CalendarReadCut(catalog,row.GetFieldValue<DateTimeOffset>(1),row.GetInt32(2),row.GetString(5),row.GetInt32(6));
        var result=new KnowledgeSnapshot(new(row.GetFieldValue<DateOnly>(0).ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),resolvedCut.SchedulePrefix,
            resolvedCut.KnownAt.ToString("O",System.Globalization.CultureInfo.InvariantCulture),id){ReadCut=resolvedCut},
            row.GetFieldValue<DateTimeOffset>(3),row.GetFieldValue<DateTimeOffset>(4));
        await row.DisposeAsync();
        await resolvedCut.Verify(connection,catalog,result.Context,ct);
        await resolvedCut.Register(connection,result.Context,ct);
        return result;
    }
    public async Task<ScheduleContext> Resolve(NpgsqlConnection connection,ScheduleContext request,CancellationToken ct)
    {
        if(request.ContextId is not null)return await Retained(connection,request,ct);
        return Prefixed((await Capture(connection,new(request.EffectiveOn,request.KnownAt,"at"),ct)).Context,request);
    }
    // Reconstruct from explicit coordinates and verify the correlation. Missing
    // inputs cannot be filled in by a database lookup or current-data fallback.
    public async Task<ScheduleContext> Retained(NpgsqlConnection connection,ScheduleContext request,CancellationToken ct)
    {
        if(request.ContextId is not {} id||request.EffectiveOn is null||request.KnownAt is null||request.KnownThrough is null)
            throw Error("temporal_context","Supply every exact read coordinate.");
        if(request.ReadCut is {} cut){await cut.Verify(connection,catalog,request,ct);await cut.Register(connection,request,ct);return request;}
        var exact=(await Read(connection,id,ct)).Context;
        if(exact.EffectiveOn!=request.EffectiveOn||SchedulePreview.ParseAt(exact.KnownAt)!=SchedulePreview.ParseAt(request.KnownAt))
            throw Error("temporal_context","The correlation differs from its certified read coordinates.");
        return Prefixed(exact,request);
    }
    private static ScheduleContext Prefixed(ScheduleContext context,ScheduleContext request)=>
        request.KnownThrough is int prefix && prefix!=context.KnownThrough
            ?throw Error("temporal_context","The requested knowledge timestamp and publication prefix disagree."):context;
    private static TemporalException Error(string code,string message)=>new(code,message);
}
