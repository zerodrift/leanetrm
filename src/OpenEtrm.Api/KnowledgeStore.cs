using Npgsql;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

public sealed record ContextCaptureRequest(string? EffectiveOn,string? KnownAt,string? Mode,string? TimeZone=null);
public sealed record KnowledgeSnapshot(ScheduleContext Context,DateTimeOffset CoverageFrom,DateTimeOffset CertifiedThrough);

public sealed class KnowledgeStore(Guid catalog)
{
    public async Task<KnowledgeSnapshot> Capture(NpgsqlConnection connection,ContextCaptureRequest body,CancellationToken ct)
    {
        var effective=DateWorkspace.Date(body.EffectiveOn);
        if(body.Mode=="end_of_day")
        {
            if(body.KnownAt is not null)throw Error("temporal_context","The same-day policy resolves its own cutoff.");
            var end=UserTime.EndOfDay(effective,body.TimeZone);
            var current=await Capture(connection,new(body.EffectiveOn,null,"current"),ct);
            return end>=current.CertifiedThrough?current:
                await Capture(connection,new(body.EffectiveOn,end.ToString("O"),"at"),ct);
        }
        if(body.Mode is not ("current" or "at") || (body.Mode=="current"?body.KnownAt is not null:body.KnownAt is null))
            throw Error("temporal_context","Choose current knowledge explicitly or supply an exact UTC cutoff.");
        if(body.Mode=="at" && SchedulePreview.ParseAt(body.KnownAt).Ticks%10!=0)
            throw Error("temporal_precision","Knowledge timestamps must be representable at PostgreSQL microsecond precision.");
        object at=body.Mode=="at"?SchedulePreview.ParseAt(body.KnownAt):DBNull.Value;
        await using var command=ScheduleStore.Command(connection,"SELECT security.capture_date_context($1,$2,$3::timestamptz)",catalog,effective,at);
        var id=(Guid)(await command.ExecuteScalarAsync(ct))!;
        return await Read(connection,id,ct);
    }
    public async Task<KnowledgeSnapshot> Read(NpgsqlConnection connection,Guid id,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT effective_on,known_at,known_through,coverage_from,certified_through FROM market_data.temporal_contexts WHERE catalog_id=$1 AND id=$2",catalog,id);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))throw Error("temporal_context","This context is absent or outside current access.");
        return new(new(reader.GetFieldValue<DateOnly>(0).ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),reader.GetInt32(2),
            reader.GetFieldValue<DateTimeOffset>(1).ToString("O",System.Globalization.CultureInfo.InvariantCulture),id),
            reader.GetFieldValue<DateTimeOffset>(3),reader.GetFieldValue<DateTimeOffset>(4));
    }
    public async Task<ScheduleContext> Resolve(NpgsqlConnection connection,ScheduleContext request,CancellationToken ct)
    {
        if(request.ContextId is not null)return await Retained(connection,request,ct);
        return Prefixed((await Capture(connection,new(request.EffectiveOn,request.KnownAt,"at"),ct)).Context,request);
    }
    // The stored context a request names, refused unless it has exactly the request's coordinates. Never captures.
    public async Task<ScheduleContext> Retained(NpgsqlConnection connection,ScheduleContext request,CancellationToken ct)
    {
        var stored=(await Read(connection,request.ContextId??throw Error("temporal_context","Name an exact retained context."),ct)).Context;
        if(stored.EffectiveOn!=request.EffectiveOn || request.KnownAt is null ||
            SchedulePreview.ParseAt(stored.KnownAt)!=SchedulePreview.ParseAt(request.KnownAt))
            throw Error("temporal_context","A retained context cannot be reinterpreted with different coordinates.");
        return Prefixed(stored,request);
    }
    private static ScheduleContext Prefixed(ScheduleContext context,ScheduleContext request)=>
        request.KnownThrough is int prefix && prefix!=context.KnownThrough
            ?throw Error("temporal_context","The requested knowledge timestamp and publication prefix disagree."):context;
    private static TemporalException Error(string code,string message)=>new(code,message);
}
