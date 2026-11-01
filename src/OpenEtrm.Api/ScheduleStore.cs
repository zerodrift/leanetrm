using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

public sealed class ScheduleStore(Guid catalog,Guid referenceCatalog,Guid referenceRevision)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal static NpgsqlCommand Command(NpgsqlConnection connection,string sql,params object[] args)
    {
        var command=new NpgsqlCommand(sql,connection);
        foreach(var arg in args)command.Parameters.Add(new NpgsqlParameter{Value=arg});return command;
    }
    public async Task<ImmutableArray<ScheduleVersion>> Read(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=Command(connection,"SELECT schedule_id,version,generation,state,definition::text,selection,effective_from,effective_to,publication_order,published_at,engine_version,calendar_catalog_id,calendar_revision_id,calendar_code FROM market_data.date_schedule_versions WHERE catalog_id=$1 ORDER BY schedule_id,version",catalog);
        await using var reader=await command.ExecuteReaderAsync(ct);var result=ImmutableArray.CreateBuilder<ScheduleVersion>();
        while(await reader.ReadAsync(ct))result.Add(Version(reader,11));
        await reader.DisposeAsync();
        return await WithCalendars(connection,result.ToImmutable(),ct);
    }
    public async Task<ImmutableArray<ScheduleVersion>> ReadAt(NpgsqlConnection connection,ScheduleContext context,CancellationToken ct)
    {
        await using var command=Command(connection,"SELECT f.schedule_id,f.version,f.generation,f.state,f.definition::text,f.selection,f.effective_from,f.effective_to,f.publication_order,f.published_at,f.engine_version,f.known_from,f.known_to,v.calendar_catalog_id,v.calendar_revision_id,v.calendar_code FROM market_data.date_schedule_facts_at($1,$4) f JOIN market_data.date_schedule_versions v ON (v.catalog_id,v.schedule_id,v.version)=(f.catalog_id,f.schedule_id,f.version) WHERE f.catalog_id=$1 AND f.effective_from<=$2 AND $2<f.effective_to AND f.known_from<=$3 AND $3<f.known_to ORDER BY f.schedule_id,f.version",
            catalog,DateWorkspace.Date(context.EffectiveOn),SchedulePreview.ParseAt(context.KnownAt),context.KnownThrough!.Value);
        await using var reader=await command.ExecuteReaderAsync(ct);var result=ImmutableArray.CreateBuilder<ScheduleVersion>();
        while(await reader.ReadAsync(ct))
        {
            var from=reader.GetFieldValue<DateOnly>(6);var to=reader.GetFieldValue<DateOnly>(7);
            result.Add(Version(reader,13,new(from,to,reader.GetFieldValue<DateTimeOffset>(11),reader.GetFieldValue<DateTimeOffset>(12))));
        }
        await reader.DisposeAsync();
        return await WithCalendars(connection,result.ToImmutable(),ct);
    }
    // Exact graph references survive symbolic successors. Their own publication
    // receipt and effective interval still constrain every historical read.
    internal async Task<(ScheduleVersion Version,DateTimeOffset KnownFrom)> ReadPublishedAt(NpgsqlConnection connection,
        Guid id,int version,ScheduleContext context,CancellationToken ct)
    {
        var exact=PinnedContext.Exact(context);
        await using var command=Command(connection,"""
SELECT v.schedule_id,v.version,v.generation,v.state,v.definition::text,v.selection,v.effective_from,v.effective_to,
 v.publication_order,v.published_at,v.engine_version,v.calendar_catalog_id,v.calendar_revision_id,v.calendar_code,k.known_from
FROM market_data.date_schedule_versions v JOIN market_data.knowledge_publications k
 ON(k.catalog_id,k.publication_order,k.publication_xid)=(v.catalog_id,v.publication_order,v.publication_xid)
WHERE(v.catalog_id,v.schedule_id,v.version)=($1,$2,$3) AND v.state='published'
 AND v.effective_from<=$4 AND $4<v.effective_to AND v.publication_order<=$5 AND k.known_from<=$6
""",catalog,id,version,DateWorkspace.Date(exact.EffectiveOn),exact.KnownThrough!.Value,SchedulePreview.ParseAt(exact.KnownAt));
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))throw Error("schedule_context","The exact published schedule is unavailable at this effective date and knowledge cutoff.");
        var selected=Version(reader,11);var knownFrom=reader.GetFieldValue<DateTimeOffset>(14);
        await reader.DisposeAsync();
        var loaded=(await WithCalendars(connection,[selected],ct)).Single();
        return(loaded,knownFrom);
    }
    static ScheduleVersion Version(NpgsqlDataReader reader,int calendarColumn,BitemporalRange? temporal=null)=>new(
        reader.GetGuid(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetString(3),
        JsonSerializer.Deserialize<MonthlyDraft>(reader.GetString(4),Json)!,reader.GetString(5),reader.GetFieldValue<DateOnly>(6),reader.GetFieldValue<DateOnly>(7),
        reader.IsDBNull(8)?null:reader.GetInt32(8),reader.IsDBNull(9)?null:reader.GetFieldValue<DateTimeOffset>(9),reader.GetString(10),temporal,CalendarAt(reader,calendarColumn));
    public async Task<int> Capture(NpgsqlConnection connection,CancellationToken ct)
    {
        await using(var gate=Command(connection,"SELECT pg_advisory_xact_lock(730849102)"))await gate.ExecuteNonQueryAsync(ct);
        await using var command=Command(connection,"SELECT coalesce(max(publication_order),0) FROM market_data.date_schedule_versions WHERE catalog_id=$1",catalog);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }
    public async Task<ScheduleVersion> Save(NpgsqlConnection connection,ScheduleSaveRequest body,CancellationToken ct)
    {
        // Generated schedules require a calendar; date lists bind one only when explicitly supplied.
        var engine=body.Definition is not {Dates:not null}?"date_schedule_v2":"date_schedule_v3";
        var calendar=body.Calendar is null&&engine=="date_schedule_v3"?null:await Calendar(connection,body.Calendar,ct);
        PublishedSchedules.Validate(body,engine,calendar);
        // One catalog lock serializes create/fork and quota admission across sessions.
        await using(var gate=Command(connection,"SELECT pg_advisory_xact_lock(730849103)"))await gate.ExecuteNonQueryAsync(ct);
        var all=await Read(connection,ct);Guid id;int version,generation;
        bool Same(ScheduleVersion value)=>value.Definition.Symbol==body.Definition.Symbol&&
            JsonSerializer.Serialize(value.Definition,Json)==JsonSerializer.Serialize(body.Definition,Json)&&value.Selection==body.Selection&&
            value.EffectiveFrom==DateWorkspace.Date(body.EffectiveFrom)&&value.EffectiveTo==DateWorkspace.Date(body.EffectiveTo)&&value.Calendar==body.Calendar;
        var repeated=all.Where(v=>v.Definition.Symbol==body.Definition.Symbol).OrderByDescending(v=>v.Version).FirstOrDefault();
        if(repeated is not null&&Same(repeated)&&(body.Id is null&&body.Version is null&&body.Generation is null&&body.ExpectedState is null||body.Id==repeated.Id&&body.Version==repeated.Version&&body.Generation==repeated.Generation&&body.ExpectedState==repeated.State))return repeated;
        if(body.Id is null&&all.Any(v=>v.Definition.Symbol==body.Definition.Symbol))throw Error("schedule_conflict","This symbol already exists. Open its version to edit it, or import dates to append.");
        if(body.Id is null)
        {
            if(body.Version is not null || body.Generation is not null || body.ExpectedState is not null || all.Select(v=>v.Id).Distinct().Count()>=32)
                throw Error("schedule_conflict","Create a new schedule without an existing version; maximum 32 schedules.");
            id=Guid.NewGuid();version=1;generation=1;
            await using var insert=Command(connection,"INSERT INTO market_data.date_schedules VALUES($1,$2,$3)",catalog,id,body.Definition.Symbol!);
            await insert.ExecuteNonQueryAsync(ct);
        }
        else
        {
            id=body.Id.Value;var old=all.SingleOrDefault(v=>v.Id==id&&v.Version==body.Version);
            if(old is null || old.State!=body.ExpectedState || old.Generation!=body.Generation || old.Definition.Symbol!=body.Definition.Symbol)
                throw Error("schedule_conflict","The selected version changed or its reference identity differs. Reload before editing.");
            if(old.State=="published")
            {
                if(all.Any(v=>v.Id==id&&v.State=="draft"))throw Error("schedule_conflict","This schedule already has a draft. Open that draft.");
                version=all.Where(v=>v.Id==id).Max(v=>v.Version)+1;generation=1;
            }
            else {version=old.Version;generation=old.Generation+1;}
        }
        if(version>64)throw Error("preview_limit","A schedule can have at most 64 versions in this preview.");
        var definition=JsonSerializer.Serialize(body.Definition,Json);
        if(body.Id is not null && all.Any(v=>v.Id==id&&v.Version==version&&v.State=="draft"))
        {
            await using var update=Command(connection,"UPDATE market_data.date_schedule_versions SET generation=$1,definition=$2::jsonb,selection=$3,effective_from=$4,effective_to=$5,engine_version=$10,calendar_catalog_id=$11,calendar_revision_id=$12,calendar_code=$13 WHERE catalog_id=$6 AND schedule_id=$7 AND version=$8 AND generation=$9 AND state='draft'",
                generation,definition,body.Selection!,DateWorkspace.Date(body.EffectiveFrom),DateWorkspace.Date(body.EffectiveTo),catalog,id,version,body.Generation!.Value,
                engine,(object?)calendar?.Reference.CatalogId??DBNull.Value,(object?)calendar?.Reference.RevisionId??DBNull.Value,(object?)calendar?.Reference.Code??DBNull.Value);
            if(await update.ExecuteNonQueryAsync(ct)!=1)throw Error("schedule_conflict","The draft changed. Reload it.");
        }
        else
        {
            await using var insert=Command(connection,"INSERT INTO market_data.date_schedule_versions(catalog_id,schedule_id,version,generation,state,definition,selection,effective_from,effective_to,engine_version,calendar_catalog_id,calendar_revision_id,calendar_code,publication_order,published_at) VALUES($1,$2,$3,$4,'draft',$5::jsonb,$6,$7,$8,$9,$10,$11,$12,NULL,NULL)",
                catalog,id,version,generation,definition,body.Selection!,DateWorkspace.Date(body.EffectiveFrom),DateWorkspace.Date(body.EffectiveTo),
                engine,(object?)calendar?.Reference.CatalogId??DBNull.Value,(object?)calendar?.Reference.RevisionId??DBNull.Value,(object?)calendar?.Reference.Code??DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct);
        }
        return (await Read(connection,ct)).Single(v=>v.Id==id&&v.Version==version);
    }
    public async Task<ScheduleVersion> Publish(NpgsqlConnection connection,SchedulePublishRequest body,CancellationToken ct)
    {
        var row=(await Read(connection,ct)).SingleOrDefault(v=>v.Id==body.Id&&v.Version==body.Version);
        if(row is null || row.Generation!=body.Generation)throw Error("schedule_conflict","Reload the changed schedule before publishing.");
        _=PublishedSchedules.Compile(row);
        PublishedSchedules.Validate(new(row.Id,row.Version,row.Generation,row.Definition,row.Selection,row.EffectiveFrom.ToString("yyyy-MM-dd"),row.EffectiveTo.ToString("yyyy-MM-dd")),row.EngineVersion,row.ResolvedCalendar);
        await using var command=Command(connection,"SELECT security.publish_date_schedule($1,$2,$3,$4)",catalog,body.Id,body.Version,body.Generation);
        await command.ExecuteScalarAsync(ct);
        return (await Read(connection,ct)).Single(v=>v.Id==body.Id&&v.Version==body.Version);
    }
    // Every calendar of this workspace's pinned reference revision, as offered to schedule authors.
    public async Task<ImmutableArray<ReferenceCalendar>> Calendars(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=Command(connection,"SELECT code FROM ref_data.calendars WHERE catalog_id=$1 AND revision_id=$2 ORDER BY code",referenceCatalog,referenceRevision);
        var codes=new List<CalendarReference>();
        await using(var reader=await command.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))codes.Add(new(referenceCatalog,referenceRevision,reader.GetString(0)));
        return [..(await Calendars(connection,codes,ct)).Values.OrderBy(c=>c.Reference.Code,StringComparer.Ordinal)];
    }
    // Reads exact calendar versions visible to the bound identity and context; absent ones are omitted.
    private static async Task<Dictionary<CalendarReference,ReferenceCalendar>> Calendars(NpgsqlConnection connection,IReadOnlyCollection<CalendarReference> references,CancellationToken ct)
    {
        var result=new Dictionary<CalendarReference,ReferenceCalendar>();
        foreach(var reference in references.Distinct())
        {
            await using var head=Command(connection,"SELECT coverage_from,coverage_to,weekend_mask FROM ref_data.calendars WHERE catalog_id=$1 AND revision_id=$2 AND code=$3",reference.CatalogId,reference.RevisionId,reference.Code);
            DateOnly from,to;int mask;
            await using(var reader=await head.ExecuteReaderAsync(ct))
            {
                if(!await reader.ReadAsync(ct))continue;
                from=reader.GetFieldValue<DateOnly>(0);to=reader.GetFieldValue<DateOnly>(1);mask=reader.GetInt16(2);
            }
            await using var days=Command(connection,"SELECT business_date,is_business_day FROM ref_data.calendar_days WHERE catalog_id=$1 AND revision_id=$2 AND calendar_code=$3 ORDER BY business_date",reference.CatalogId,reference.RevisionId,reference.Code);
            var rows=ImmutableArray.CreateBuilder<(DateOnly,bool)>();
            await using(var reader=await days.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))rows.Add((reader.GetFieldValue<DateOnly>(0),reader.GetBoolean(1)));
            result[reference]=new(reference,from,to,mask,rows.ToImmutable());
        }
        return result;
    }
    public async Task<ReferenceCalendar> Calendar(NpgsqlConnection connection,CalendarReference? reference,CancellationToken ct)
    {
        if(reference is null || reference.CatalogId!=referenceCatalog || reference.RevisionId!=referenceRevision)
            throw Error("calendar_reference","Choose a calendar from this workspace's reference data.");
        return (await Calendars(connection,[reference],ct)).GetValueOrDefault(reference)??throw Error("calendar_reference","The chosen reference calendar is unavailable.");
    }
    private static CalendarReference? CalendarAt(NpgsqlDataReader reader,int column)=>reader.IsDBNull(column)?null:
        new(reader.GetGuid(column),reader.GetGuid(column+1),reader.GetString(column+2));
    private static async Task<ImmutableArray<ScheduleVersion>> WithCalendars(NpgsqlConnection connection,ImmutableArray<ScheduleVersion> versions,CancellationToken ct)
    {
        var calendars=await Calendars(connection,versions.Where(v=>v.Calendar is not null).Select(v=>v.Calendar!).ToArray(),ct);
        return [..versions.Select(v=>v.Calendar is null?v:v with{ResolvedCalendar=calendars.GetValueOrDefault(v.Calendar)})];
    }
    private static TemporalException Error(string code,string message)=>new(code,message);
}
