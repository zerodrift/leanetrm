using System.Globalization;
using Npgsql;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// A value owned by a single logical calendar read, including its publication
// recheck. The effective day varies across the strip. Knowledge never does.
internal sealed record CalendarReadCut(Guid Catalog,DateTimeOffset KnownAt,int SchedulePrefix,string SystemId,int Timeline)
{
    internal static async Task<CalendarReadCut> Resolve(NpgsqlConnection connection,Guid catalog,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT * FROM security.calendar_read_cut($1)",catalog);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))throw new TemporalException("knowledge_coverage","The calendar knowledge cutoff is unavailable.");
        return new(catalog,reader.GetFieldValue<DateTimeOffset>(0),reader.GetInt32(1),reader.GetString(2),reader.GetInt32(3));
    }

    // The database verifies this value using uuid_send, date_send and
    // timestamptz_send. PostgreSQL's binary integers use the 2000 UTC epoch.
    internal ScheduleContext On(DateOnly day)
    {
        Span<byte> input=stackalloc byte[28];
        Catalog.TryWriteBytes(input,bigEndian:true,out _);
        var epoch=new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(input[16..],day.DayNumber-DateOnly.FromDateTime(epoch.UtcDateTime).DayNumber);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(input[20..],(KnownAt-epoch).Ticks/10);
        var id=new Guid(System.Security.Cryptography.SHA256.HashData(input).AsSpan(0,16),bigEndian:true);
        return new(day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),SchedulePrefix,KnownAt.ToString("O",CultureInfo.InvariantCulture),id){ReadCut=this};
    }

    internal async Task RetainWriteSource(NpgsqlConnection connection,ScheduleContext source,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.retain_workspace_write_source($1,$2,$3,$4,$5,$6,$7)",
            Catalog,source.ContextId!.Value,DateWorkspace.Date(source.EffectiveOn),KnownAt,SchedulePrefix,SystemId,Timeline);
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NpgsqlConnection,object> WorkspaceBindings=new();
    internal async Task Verify(NpgsqlConnection connection,Guid catalog,ScheduleContext context,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.workspace_cut_visible($1,$2,$3,$4,$5,$6,$7)",
            catalog,context.ContextId!.Value,DateWorkspace.Date(context.EffectiveOn),KnownAt,SchedulePrefix,SystemId,Timeline);
        if(!Equals(await command.ExecuteScalarAsync(ct),true))throw new TemporalException("knowledge_coverage","This workspace selection is outside certified coverage.");
    }

    internal async Task Bind(NpgsqlConnection connection,Guid catalog,ScheduleContext context,CancellationToken ct)
    {
        if(catalog!=Catalog||context.KnownThrough!=SchedulePrefix||context.KnownAt is null||
            SchedulePreview.ParseAt(context.KnownAt)!=KnownAt||context.ContextId is null||context.ReadCut!=this)
            throw new TemporalException("temporal_context","The read context differs from its certified catalog or cutoff.");
        if(context.WorkspaceCut)WorkspaceBindings.GetOrCreateValue(connection);
        var sql=context.WorkspaceCut?"SELECT security.bind_workspace_read($1,$2,$3,$4,$5,$6,$7)":"SELECT security.bind_calendar_read($1,$2,$3,$4,$5,$6,$7)";
        await using var command=ScheduleStore.Command(connection,sql,
            catalog,context.ContextId!.Value,DateWorkspace.Date(context.EffectiveOn),KnownAt,SchedulePrefix,SystemId,Timeline);
        await command.ExecuteNonQueryAsync(ct);
    }
}
