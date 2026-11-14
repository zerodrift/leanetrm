using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarketCloseRegister([property:JsonRequired] Guid RequestId,[property:JsonRequired] Guid SnapshotId,[property:JsonRequired] string Name,
    [property:JsonRequired] string Session,[property:JsonRequired] DateOnly BusinessDate);
public sealed record MarketCloseInfo(Guid Id,string Name,DateTimeOffset At,string Kind,
    DefinitionRevision Definition,ReferenceRevision Reference,Guid? AnchorCloseId,Guid? RunId,
    string Session,int SessionVersion,DateOnly BusinessDate,Guid? ReplacedBy)
{
    public Guid RequestId{get;init;}
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarketCloseHistoryRequest([property:JsonRequired] string Session,[property:JsonRequired] DateOnly BusinessDate);
public sealed record MarketCloseFrames(ImmutableArray<InputFrame> Frames,int Limit,bool HasMore);

// Durable frames pin native observations and explicit membership. Registering a
// prior frame records when it was retained; it never backdates its publication.
public static partial class MarketCloses
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static Task<MarketCloseInfo> Register(PreviewDatabase database,NpgsqlConnection connection,
        PinnedContext pin,Func<Guid,InputFrame> frame,MarketCloseRegister request,CancellationToken ct)
        =>Capture(database,connection,pin,frame,request,false,ct);
    public static Task<MarketCloseInfo> CaptureLive(PreviewDatabase database,NpgsqlConnection connection,
        PinnedContext pin,Func<Guid,InputFrame> frame,MarketCloseRegister request,CancellationToken ct)
        =>Capture(database,connection,pin,frame,request,true,ct);
    private static async Task<MarketCloseInfo> Capture(PreviewDatabase database,NpgsqlConnection connection,
        PinnedContext pin,Func<Guid,InputFrame> frame,MarketCloseRegister request,bool live,CancellationToken ct)
    {
        if(pin.Context is not null)throw new MarketDataException("historical_read_only","Capture closes in the current workspace.");
        RequireCommand(request);
        await using var command=ScheduleStore.Command(connection,"SELECT security.market_close_capture_receipt($1,$2,$3,$4,$5,$6,$7)::text",
            database.Manifest.CatalogId,request.RequestId,request.SnapshotId,request.Name,request.Session,request.BusinessDate,live?CurveModel.LiveOutputsPolicy:ExactCapturePolicy);
        var previous=await command.ExecuteScalarAsync(ct);
        if(previous is string json)return JsonSerializer.Deserialize<MarketCloseInfo>(json,Json)!;
        return await RegisterSelection(database,connection,pin,frame(request.SnapshotId),request,live,ct);
    }
    private static async Task<MarketCloseInfo> RegisterSelection(PreviewDatabase database,NpgsqlConnection connection,
        PinnedContext pin,InputFrame frame,MarketCloseRegister request,bool captureLive,CancellationToken ct)
    {
        if(pin.Context is not null)throw new MarketDataException("historical_read_only","Register closes in the current workspace.");
        RequireCommand(request);
        if(request.SnapshotId!=frame.Id)
            throw new MarketDataException("pilot_close_input","Select an exact existing frame and name this retained close.");
        var data=await MarketInputSelection.Root(database,connection,frame,ct,referenceScope:ReferenceReadScope.TradeValuation);
        var primary=new CatalogueSource(data.DefinitionRevision.RevisionId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId);
        var inputs=new Dictionary<Guid,(PreviewData Data,InputFrame Frame)>
            {[primary.DefinitionRevisionId]=(data,frame with{Source=primary})};
        if(frame.SourceInputs is {} matrix)
        {
            foreach(var part in matrix.Values.Where(p=>p.Source!=primary))
                inputs.Add(part.Source.DefinitionRevisionId,(await database.Read(connection,ct,ppaBundle:part.Source,
                    observationIds:part.ObservationIds,observationScope:ObservationReadScope.ExactPins),part.Project(frame)));
        }
        else
        {
            foreach(var source in await database.ReadInputMarkets(connection,frame,ct))
                if(source.Data.DefinitionRevision.RevisionId!=primary.DefinitionRevisionId)inputs.Add(source.Data.DefinitionRevision.RevisionId,source);
        }
        if(captureLive)
        {
            var roots=await PreviewDatabase.Rows(connection,"SELECT row_to_json(k)::text FROM market_data.close_session_curves k "+
                "WHERE(k.catalog_id,k.code,k.version)=($1,$2,security.close_session_version_on($1,$2,$3))",
                [database.Manifest.CatalogId,request.Session,request.BusinessDate],ct);
            if(roots.IsEmpty||roots.Any(r=>!inputs.TryGetValue(r.Id("definition_revision_id"),out var source)||
                !source.Frame.CurveContracts.ContainsKey(r.Text("curve_code"))))
                throw new MarketDataException("close_incomplete","The selected inputs must include every exact curve of the chosen close session.");
            var parts=inputs.Values.Select(source=>LivePart(source.Data,source.Frame,
                [..roots.Where(r=>r.Id("definition_revision_id")==source.Data.DefinitionRevision.RevisionId).Select(r=>r.Text("curve_code"))]))
                .ToImmutableDictionary(p=>p.Source.DefinitionRevisionId);
            frame=FreshFrame(frame,request.RequestId,parts,primary);
            inputs=inputs.ToDictionary(p=>p.Key,p=>(p.Value.Data,parts[p.Key].Project(frame)));
        }
        var pins=new List<object>();var members=new List<object>();
        // Qualify each explicit part without filling marks or inventing members.
        foreach(var source in inputs.Values.OrderBy(s=>s.Data.DefinitionRevision.RevisionId))
        {
            var definitionRevisionId=source.Data.DefinitionRevision.RevisionId;
            var roots=frame.SourceInputs is {} declaredParts?declaredParts[definitionRevisionId].RootCurves:[..source.Frame.CurveContracts.Keys];
            QualifyPart(source.Data,source.Frame,roots);
            pins.AddRange(source.Data.Snapshot(source.Frame).Selections.Select(pin=>(object)new{definitionRevisionId,
                seriesId=pin.Input.Series.Id,observationId=pin.Observation.Key.Id,inputUse=pin.Input.Use==InputUse.FrozenFact?"frozen":"variable"}));
            members.AddRange(source.Frame.CurveContracts.OrderBy(p=>p.Key,StringComparer.Ordinal).SelectMany(p=>p.Value.Select((id,index)=>(object)new{
                definitionRevisionId,curve=p.Key,contractId=id,ordinal=index,scope=frame.SourceInputs is {} declared&&!declared[definitionRevisionId].RootCurves.Contains(p.Key,StringComparer.Ordinal)?"retained":"root"})));
        }
        await using var command=ScheduleStore.Command(connection,"SELECT security.capture_market_close($1,$2,$3,$4,$5,$6,$7,$8,$9,$10::jsonb,$11::jsonb)::text",
            database.Manifest.CatalogId,primary.DefinitionRevisionId,request.RequestId,request.SnapshotId,request.Name,request.Session,request.BusinessDate,
            captureLive?CurveModel.LiveOutputsPolicy:ExactCapturePolicy,frame.At,
            JsonSerializer.Serialize(pins,Json),JsonSerializer.Serialize(members,Json));
        return JsonSerializer.Deserialize<MarketCloseInfo>((string)(await command.ExecuteScalarAsync(ct))!,Json)!;
    }
    private static void RequireCommand(MarketCloseRegister request)
    {
        if(request.RequestId==Guid.Empty||request.SnapshotId==Guid.Empty||string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>80||
            string.IsNullOrWhiteSpace(request.Session)||request.Session.Length>64)
            throw new MarketDataException("pilot_close_input","Select the exact source snapshot and close session, and name this retained close.");
    }
    public static async Task<MarketCloseInfo> Read(NpgsqlConnection connection,Guid catalog,Guid id,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT name,at,kind,definition_revision_id,reference_catalog_id,reference_revision_id,anchor_close_id,run_id,request_id,"+
            "session_code,session_version,business_date,security.close_in_force(catalog_id,session_code,business_date) FROM market_data.market_closes WHERE catalog_id=$1 AND id=$2",catalog,id);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))throw new PreviewDeniedException();
        return new(id,reader.GetString(0),reader.GetFieldValue<DateTimeOffset>(1),reader.GetString(2),new(catalog,reader.GetGuid(3)),new(reader.GetGuid(4),reader.GetGuid(5)),
            reader.IsDBNull(6)?null:reader.GetGuid(6),reader.IsDBNull(7)?null:reader.GetGuid(7),reader.GetString(9),reader.GetInt32(10),
            reader.GetFieldValue<DateOnly>(11),reader.GetGuid(12) is var current&&current!=id?current:null){RequestId=reader.GetGuid(8)};
    }
    // A session business day's visible recorded closes, newest first: the close in force and the history it replaced.
    public static async Task<JsonElement> History(NpgsqlConnection connection,Guid catalog,MarketCloseHistoryRequest request,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.close_history($1,$2,$3)::text",catalog,request.Session,request.BusinessDate);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    public static async Task<MarketCloseFrames> Frames(NpgsqlConnection connection,Guid catalog,Guid? contextId,CancellationToken ct,int limit=128)
    {
        if(limit is <0 or >128)throw new MarketDataException("preview_limit","The retained frame catalog is limited to 128 choices.");
        var rows=await PreviewDatabase.Rows(connection,"SELECT row_to_json(t)::text FROM(SELECT id,name,at,session_code,session_version,business_date,"+
            "security.close_in_force(catalog_id,session_code,business_date) in_force "+
            "FROM market_data.market_closes WHERE catalog_id=$1 AND security.close_session_is_listed(catalog_id,session_code) ORDER BY at DESC,id DESC LIMIT $2)t",[catalog,limit+1],ct);
        var headers=rows.Take(limit).OrderBy(row=>row.Instant("at")).ThenBy(row=>row.Id("id")).ToArray();
        if(headers.Length==0)return new([],limit,rows.Length>limit);
        var ids=headers.Select(row=>row.Id("id")).ToArray();
        var parts=await Parts(connection,catalog,ids,contextId,ct);
        var frames=headers.Select(header=>CreateFrame(header.Id("id"),header.Text("name"),header.Instant("at"),parts[header.Id("id")],contextId,
            Close(header.Id("id"),header.Text("session_code"),int.Parse(header.Text("session_version"),CultureInfo.InvariantCulture),DateOnly.Parse(header.Text("business_date"),CultureInfo.InvariantCulture),header.Id("in_force")))).ToImmutableArray();
        return new(frames,limit,rows.Length>limit);
    }
    public static async Task<InputFrame> Frame(NpgsqlConnection connection,Guid catalog,Guid close,Guid? contextId,CancellationToken ct)
    {
        var header=await Read(connection,catalog,close,ct);
        var parts=(await Parts(connection,catalog,[close],contextId,ct))[close];
        return CreateFrame(close,header.Name,header.At,parts,contextId,new(header.Session,header.SessionVersion,header.BusinessDate,header.ReplacedBy));
    }
    private sealed record CloseParts(CatalogueSource Primary,ImmutableDictionary<Guid,MarketSourceInputs> Sources);
    private static async Task<Dictionary<Guid,CloseParts>> Parts(NpgsqlConnection connection,Guid catalog,Guid[] ids,Guid? contextId,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.read_market_close_parts($1,$2,$3::uuid)::text",catalog,ids,(object?)contextId??DBNull.Value);
        using var json=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct)??throw new PreviewDeniedException()));
        return json.RootElement.EnumerateArray().ToDictionary(row=>row.Id("close_id"),row=>
        {
            var sources=row.GetProperty("sources").EnumerateArray().Select(source=>
            {
                var owner=new CatalogueSource(source.Id("definition_revision_id"),source.Id("reference_catalog_id"),source.Id("reference_revision_id"));
                var pins=source.GetProperty("pins").EnumerateArray().ToArray();
                return new MarketSourceInputs(owner,[..pins.Select(p=>p.Id("observation_id"))],
                    [..pins.Where(p=>p.Text("input_use")=="frozen").Select(p=>p.Id("observation_id"))],
                    source.GetProperty("members").EnumerateArray().GroupBy(m=>m.Text("curve_code"),StringComparer.Ordinal)
                        .ToImmutableDictionary(g=>g.Key,g=>g.Select(m=>m.Id("contract_id")).ToImmutableArray(),StringComparer.Ordinal),
                    [..source.GetProperty("members").EnumerateArray().Where(m=>m.Text("scope")=="root").Select(m=>m.Text("curve_code")).Distinct(StringComparer.Ordinal)]);
            }).ToImmutableDictionary(part=>part.Source.DefinitionRevisionId);
            if(!sources.TryGetValue(row.Id("primary_definition_revision_id"),out var primary))
                throw new MarketDataException("pilot_close_inputs","The recorded close has no exact primary source.");
            return new CloseParts(primary.Source,sources);
        });
    }
    private static FrameClose Close(Guid id,string session,int version,DateOnly day,Guid inForce)=>new(session,version,day,inForce==id?null:inForce);
    private static InputFrame CreateFrame(Guid close,string name,DateTimeOffset at,CloseParts parts,Guid? contextId,FrameClose session)
    {
        var primary=parts.Sources[parts.Primary.DefinitionRevisionId];
        if(!parts.Sources.Values.Any(part=>!part.ObservationIds.IsEmpty)||
            !parts.Sources.Values.Any(part=>!part.CurveContracts.IsEmpty))
            throw new MarketDataException("pilot_close_inputs","The retained close has no complete exact input selection.");
        return new(close,name,at,session.BusinessDate,primary.ObservationIds,primary.CurveContracts,contextId,primary.FrozenObservationIds){OriginCloseId=close,Close=session,Source=parts.Primary,SourceInputs=parts.Sources};
    }
}
