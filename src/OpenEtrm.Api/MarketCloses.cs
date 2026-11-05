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
public static class MarketCloses
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static async Task<MarketCloseInfo> Register(PreviewDatabase database,NpgsqlConnection connection,
        PinnedContext pin,InputFrame frame,MarketCloseRegister request,CancellationToken ct)
    {
        if(pin.Context is not null)throw new MarketDataException("historical_read_only","Register closes in the current workspace.");
        if(request.RequestId==Guid.Empty||request.SnapshotId!=frame.Id||string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>80)
            throw new MarketDataException("pilot_close_input","Select an exact existing frame and name this retained close.");
        // A close records main-market pins only, so it cannot keep a separate swap-bundle selection (D172).
        if(frame.SwapMarketSelections is not null)
            throw new MarketDataException("close_swap_market_pins","A close cannot record this frame's selected swap-market revisions.");
        var data=await database.Read(connection,ct,observationIds:frame.ObservationIds,
            observationScope:ObservationReadScope.ExactPins,referenceScope:ReferenceReadScope.TradeValuation);
        // Qualify exactly the declared frame; no implicit membership, latest
        // observations or price filling is introduced by persistence.
        foreach(var curve in frame.CurveContracts.Keys)_=data.Build(frame,curve);
        var snapshot=data.Snapshot(frame);
        var pins=snapshot.Selections.Select(pin=>new{seriesId=pin.Input.Series.Id,observationId=pin.Observation.Key.Id,
            inputUse=pin.Input.Use==InputUse.FrozenFact?"frozen":"variable"});
        var members=frame.CurveContracts.OrderBy(p=>p.Key,StringComparer.Ordinal).SelectMany(p=>p.Value.Select((id,index)=>new{curve=p.Key,contractId=id,ordinal=index}));
        await using var command=ScheduleStore.Command(connection,"SELECT security.register_market_close($1,$2,$3,$4,$5,$6,$7,$8::jsonb,$9::jsonb)",
            database.Manifest.CatalogId,database.Manifest.RevisionId,request.RequestId,request.Name,request.Session,request.BusinessDate,frame.At,
            JsonSerializer.Serialize(pins,Json),JsonSerializer.Serialize(members,Json));
        var close=(Guid)(await command.ExecuteScalarAsync(ct)??throw new MarketDataException("pilot_close_outcome","The close receipt was not returned."));
        return await Read(connection,database.Manifest.CatalogId,close,ct);
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
    public static async Task<MarketCloseFrames> Frames(NpgsqlConnection connection,Guid catalog,Guid definition,Guid? contextId,CancellationToken ct,int limit=128)
    {
        if(limit is <0 or >128)throw new MarketDataException("preview_limit","The retained frame catalog is limited to 128 choices.");
        var rows=await PreviewDatabase.Rows(connection,"SELECT row_to_json(t)::text FROM(SELECT id,name,at,session_code,session_version,business_date,"+
            "security.close_in_force(catalog_id,session_code,business_date) in_force,security.close_recorded_after_cutoff(catalog_id,id) recorded_after "+
            "FROM market_data.market_closes WHERE catalog_id=$1 AND definition_revision_id=$2 ORDER BY at DESC,id DESC LIMIT $3)t",[catalog,definition,limit+1],ct);
        var headers=rows.Take(limit).OrderBy(row=>row.Instant("at")).ThenBy(row=>row.Id("id")).ToArray();
        if(headers.Length==0)return new([],limit,rows.Length>limit);
        var ids=headers.Select(row=>row.Id("id")).ToArray();
        var parts=await Parts(connection,catalog,ids,contextId,ct);
        var frames=headers.Select(header=>CreateFrame(header.Id("id"),header.Text("name"),header.Instant("at"),parts[header.Id("id")],contextId,
            Close(header.Id("id"),header.Text("session_code"),int.Parse(header.Text("session_version"),CultureInfo.InvariantCulture),DateOnly.Parse(header.Text("business_date"),CultureInfo.InvariantCulture),header.Id("in_force"),
            header.OptionalText("recorded_after") is {} after?after=="true":null))).ToImmutableArray();
        return new(frames,limit,rows.Length>limit);
    }
    public static async Task<InputFrame> Frame(NpgsqlConnection connection,Guid catalog,Guid close,Guid? contextId,CancellationToken ct)
    {
        var header=await Read(connection,catalog,close,ct);
        var parts=(await Parts(connection,catalog,[close],contextId,ct))[close];
        return CreateFrame(close,header.Name,header.At,parts,contextId,new(header.Session,header.SessionVersion,header.BusinessDate,header.ReplacedBy));
    }
    private sealed record CloseParts(ImmutableArray<Guid> Pins,ImmutableDictionary<string,ImmutableArray<Guid>> Members,ImmutableArray<Guid> Frozen);
    private static async Task<Dictionary<Guid,CloseParts>> Parts(NpgsqlConnection connection,Guid catalog,Guid[] ids,Guid? contextId,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.read_market_close_parts($1,$2,$3::uuid)::text",catalog,ids,(object?)contextId??DBNull.Value);
        using var json=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct)??throw new PreviewDeniedException()));
        return json.RootElement.EnumerateArray().ToDictionary(row=>row.Id("close_id"),row=>new CloseParts(
            [..row.GetProperty("pins").EnumerateArray().Select(pin=>pin.Id("observation_id"))],
            row.GetProperty("members").EnumerateArray().GroupBy(member=>member.Text("curve_code"),StringComparer.Ordinal)
                .ToImmutableDictionary(group=>group.Key,group=>group.Select(member=>member.Id("contract_id")).ToImmutableArray(),StringComparer.Ordinal),
            [..row.GetProperty("pins").EnumerateArray().Where(pin=>pin.Text("input_use")=="frozen").Select(pin=>pin.Id("observation_id"))]));
    }
    private static FrameClose Close(Guid id,string session,int version,DateOnly day,Guid inForce,bool? recordedAfter)=>new(session,version,day,inForce==id?null:inForce,recordedAfter);
    private static InputFrame CreateFrame(Guid close,string name,DateTimeOffset at,CloseParts parts,Guid? contextId,FrameClose session)
    {
        if(parts.Pins.IsEmpty||parts.Members.IsEmpty)throw new MarketDataException("pilot_close_inputs","The retained close has no complete exact input selection.");
        return new(close,name,at,parts.Pins,parts.Members,contextId,parts.Frozen){OriginCloseId=close,Close=session};
    }
}
