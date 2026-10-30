using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.Api;

public sealed record CurveNodePosition(decimal X,decimal Y);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CustomCurveSaveRequest(Guid RequestId,RecipeDefinition Definition,
    ImmutableDictionary<string,CurveNodePosition> Layout,Guid SnapshotId,Guid? Id=null,int? Version=null,int? Generation=null,string? ExpectedState=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CustomCurvePublishRequest(Guid RequestId,Guid Id,int Version,int Generation);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CustomCurveCompileRequest(string Formula,string Code,string Name,Guid SnapshotId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CustomCurveFormatRequest(RecipeDefinition Definition);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CustomCurvePreviewRequest(RecipeDefinition Definition,Guid SnapshotId,Guid? Id=null,int? Version=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ComponentHistoryRequest(RecipeDefinition Definition,Guid SnapshotId,string Node,RecipeNode Dates,string Policy,Guid? Id=null,int? Version=null);
public sealed record CurveSourceReference(Guid CatalogId,Guid RevisionId,string Code);
public sealed record CurveScheduleReference(Guid CatalogId,Guid Id,int Version);
// Who saved a generation, as reads show it (D260): `person`, `assistant`, or null for a generation saved before
// savers were recorded, never a person's by default. The assistant's turn and step stay stored and are not read here.
public sealed record CurveSaver(string? SavedBy)
{
    public static readonly CurveSaver Person=new("person");
}
// Who saves through the store: a person, or the assistant for one step of its running turn, holding that turn's fencing
// token, which only the draft claim reads. None of it is part of a read, receipt or log.
public sealed class CurveDraftSaver
{
    public static readonly CurveDraftSaver Person=new("person",null,null,null);
    public static CurveDraftSaver Assistant(Guid turn,int step,byte[] token)=>new("assistant",turn,step,token.ToArray());
    CurveDraftSaver(string savedBy,Guid? turn,int? step,byte[]? token){SavedBy=savedBy;Turn=turn;Step=step;Token=token;}
    internal string SavedBy{get;}
    internal Guid? Turn{get;}
    internal int? Step{get;}
    internal byte[]? Token{get;}
    internal bool IsAssistant=>Token is not null;
}
public sealed record CustomCurveVersion(Guid Id,int Version,int Generation,string State,RecipeDefinition Definition,
    ImmutableDictionary<string,CurveNodePosition> Layout,int? PublicationOrder,DateTimeOffset? PublishedAt,
    ImmutableDictionary<string,CurveSourceReference> Sources,ImmutableDictionary<string,CalendarReference> Calendars,
    ImmutableDictionary<string,CurveScheduleReference> Schedules,CurveSaver Saver,string EngineVersion="custom_curve_recipe_v2")
{
    // Named from the exact saved definition at read time, never stored.
    public string Kind=>CurveKinds.Of(Definition);
}

// Normalized graph persistence. Only operator scalars and layout are JSON; reference relationships use FKs.
public sealed class CustomCurvesStore(PreviewDatabase database)
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    Guid Catalog=>database.Manifest.CatalogId;
    static MarketDataException Conflict()=>new("custom_curve_conflict","This curve or request changed. Reload it or retry the exact original request.");
    static MarketDataException Invalid(string message)=>new("custom_curve_definition",message);
    public async Task<ImmutableArray<CustomCurveVersion>> Read(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"""
SELECT v.curve_id,v.version,v.generation,v.state,c.code,v.name,v.output_node,v.layout::text,v.publication_order,v.published_at,v.engine_version,
 coalesce((SELECT jsonb_agg(jsonb_build_object('id',n.node_id,'kind',n.kind,'parameters',n.parameters
 ||coalesce((SELECT jsonb_build_object('curve',s.source_curve_code) FROM market_data.custom_curve_node_sources s WHERE(s.catalog_id,s.curve_id,s.version,s.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)),'{}'::jsonb)
 ||coalesce((SELECT jsonb_build_object('calendar',s.calendar_code) FROM market_data.custom_curve_node_calendars s WHERE(s.catalog_id,s.curve_id,s.version,s.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)),'{}'::jsonb)
 ||coalesce((SELECT jsonb_build_object('schedule',s.schedule_id::text||':'||s.schedule_version::text) FROM market_data.custom_curve_node_schedules s WHERE(s.catalog_id,s.curve_id,s.version,s.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)),'{}'::jsonb),
 'publishedSource',(SELECT jsonb_build_object('catalogId',s.source_catalog_id,'curveId',s.source_curve_id,'version',s.source_version,'outputNode',s.source_output_node) FROM market_data.custom_curve_node_published_sources s WHERE(s.catalog_id,s.curve_id,s.version,s.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)),
 'contractWeights',coalesce((SELECT jsonb_agg(jsonb_build_object('outputPosition',w.output_position,'sourcePort',w.source_port,'contractId',w.contract_id,'weight',w.weight::text) ORDER BY w.output_position,w.source_port,w.contract_id) FROM market_data.custom_curve_contract_weights w WHERE(w.catalog_id,w.curve_id,w.version,w.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)),'[]'::jsonb),
 'panelWeights',coalesce((SELECT jsonb_agg(jsonb_build_object('sourcePort',w.source_port,'weight',w.weight::text) ORDER BY w.source_port) FROM market_data.custom_curve_panel_weights w WHERE(w.catalog_id,w.curve_id,w.version,w.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)),'[]'::jsonb),
 'inputs',coalesce((SELECT jsonb_object_agg(e.port,e.source_node) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)),'{}'::jsonb)) ORDER BY n.node_id)
 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(v.catalog_id,v.curve_id,v.version)),'[]'::jsonb)::text,
 coalesce((SELECT jsonb_object_agg(s.node_id,jsonb_build_object('catalogId',s.source_catalog_id,'revisionId',s.source_revision_id,'code',s.source_curve_code)) FROM market_data.custom_curve_node_sources s WHERE(s.catalog_id,s.curve_id,s.version)=(v.catalog_id,v.curve_id,v.version)),'{}'::jsonb)::text,
 coalesce((SELECT jsonb_object_agg(s.node_id,jsonb_build_object('catalogId',s.reference_catalog_id,'revisionId',s.reference_revision_id,'code',s.calendar_code)) FROM market_data.custom_curve_node_calendars s WHERE(s.catalog_id,s.curve_id,s.version)=(v.catalog_id,v.curve_id,v.version)),'{}'::jsonb)::text,
 coalesce((SELECT jsonb_object_agg(s.node_id,jsonb_build_object('catalogId',s.schedule_catalog_id,'id',s.schedule_id,'version',s.schedule_version)) FROM market_data.custom_curve_node_schedules s WHERE(s.catalog_id,s.curve_id,s.version)=(v.catalog_id,v.curve_id,v.version)),'{}'::jsonb)::text,
 g.saved_by
 FROM market_data.custom_curve_versions v JOIN market_data.custom_curves c ON(c.catalog_id,c.id)=(v.catalog_id,v.curve_id)
 LEFT JOIN market_data.custom_curve_generation_savers g ON(g.catalog_id,g.curve_id,g.version,g.generation)=(v.catalog_id,v.curve_id,v.version,v.generation)
 WHERE v.catalog_id=$1 ORDER BY c.code,v.version
""",Catalog);
        var result=ImmutableArray.CreateBuilder<CustomCurveVersion>();
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))result.Add(new(reader.GetGuid(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetString(3),
            new(reader.GetString(4),reader.GetString(5),JsonSerializer.Deserialize<ImmutableArray<RecipeNode>>(reader.GetString(11),Json),reader.GetString(6)),
            JsonSerializer.Deserialize<ImmutableDictionary<string,CurveNodePosition>>(reader.GetString(7),Json)!,reader.IsDBNull(8)?null:reader.GetInt32(8),reader.IsDBNull(9)?null:reader.GetFieldValue<DateTimeOffset>(9),
            JsonSerializer.Deserialize<ImmutableDictionary<string,CurveSourceReference>>(reader.GetString(12),Json)!,
            JsonSerializer.Deserialize<ImmutableDictionary<string,CalendarReference>>(reader.GetString(13),Json)!,
            JsonSerializer.Deserialize<ImmutableDictionary<string,CurveScheduleReference>>(reader.GetString(14),Json)!,
            new(reader.IsDBNull(15)?null:reader.GetString(15)),
            reader.GetString(10)));
        return result.ToImmutable();
    }
    async Task Lock(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849102);SELECT pg_advisory_xact_lock(730849141)");
        await command.ExecuteNonQueryAsync(ct);
    }
    async Task<CustomCurveVersion?> Replay(NpgsqlConnection connection,Guid requestId,string operation,string payload,string permission,CancellationToken ct)
    {
        if(requestId==Guid.Empty)throw Invalid("Supply a stable request identity.");
        await using var bundle=ScheduleStore.Command(connection,"SELECT security.custom_curve_source_visible($1,$2)",Catalog,database.Manifest.RevisionId);
        if(!Equals(await bundle.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        await using var command=ScheduleStore.Command(connection,"SELECT operation,payload=$3::jsonb,response::text FROM market_data.custom_curve_requests WHERE catalog_id=$1 AND actor_id=security.custom_curve_actor($1,$4) AND request_id=$2",Catalog,requestId,payload,permission);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))return null;
        if(reader.GetString(0)!=operation||!reader.GetBoolean(1))throw Conflict();
        var result=JsonSerializer.Deserialize<CustomCurveVersion>(reader.GetString(2),Json)!;
        await reader.DisposeAsync();
        // A retained mutation result contains graph and exact reference content.
        // Accepted request identity cannot restore access that has since expired.
        await using var visible=ScheduleStore.Command(connection,"SELECT security.custom_curve_visible($1,$2,$3)",Catalog,result.Id,result.Version);
        if(!Equals(await visible.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        return result;
    }
    async Task Record(NpgsqlConnection connection,Guid requestId,string operation,string payload,CustomCurveVersion result,string permission,CancellationToken ct)
    {
        if(result.Sources.Values.Any(source=>source.CatalogId!=Catalog||source.RevisionId!=database.Manifest.RevisionId))
            throw Invalid("The retained response must use the selected exact definition bundle.");
        await using var command=ScheduleStore.Command(connection,"""
INSERT INTO market_data.custom_curve_requests(catalog_id,actor_id,request_id,operation,payload,response,definition_revision_id,curve_id,version)
 VALUES($1,security.custom_curve_actor($1,$6),$2,$3,$4::jsonb,$5::jsonb,$7,$8,$9)
""",Catalog,requestId,operation,payload,JsonSerializer.Serialize(result,Json),permission,database.Manifest.RevisionId,result.Id,result.Version);
        await command.ExecuteNonQueryAsync(ct);
    }
    // A save by `saver`. The assistant first claims the one curve version its step saves (D260), so it reads and writes
    // only that draft, and its receipt names its own principal, who holds ReadMarketData and never WriteMarketDefinitions.
    // The assistant names the identity its new curve takes (`created`), which its step's key fixes, and the exact references
    // it previewed at its turn's pin (`pinned`), which must still be the ones the save resolves to.
    public async Task<CustomCurveVersion> Save(NpgsqlConnection connection,CustomCurveSaveRequest body,PreviewData data,CurveDraftSaver saver,CancellationToken ct,
        Guid? created=null,(ImmutableDictionary<string,CurveSourceReference> Sources,ImmutableDictionary<string,CalendarReference> Calendars,
            ImmutableDictionary<string,CurveScheduleReference> Schedules)? pinned=null)
    {
        await Lock(connection,ct);
        var payload=JsonSerializer.Serialize(body,Json);
        var receipt=saver.IsAssistant?"ReadMarketData":"WriteMarketDefinitions";
        // A new curve names no version, generation or state; a revision names its version. Checked before the claim, so a
        // malformed request is the same conflict whoever saves it.
        if(body.Id is null?body.Version is not null||body.Generation is not null||body.ExpectedState is not null:body.Version is null)throw Conflict();
        if(created is not null&&(!saver.IsAssistant||body.Id is not null))throw Conflict();
        var target=body.Id??created??Guid.NewGuid();
        if(saver.IsAssistant)await Claim(connection,target,body.Version??1,saver,ct);
        if(await Replay(connection,body.RequestId,"save",payload,receipt,ct) is {} replay)return replay;
        CustomCurves.Validate(body.Definition);
        ValidateLayout(body.Definition,body.Layout);
        var references=await References(connection,body.Definition,data,ct);
        if(pinned is {} previewed&&!SameReferences(previewed,references))throw Conflict();
        var all=await Read(connection,ct);Guid id;int version,generation;
        if(body.Id is null)
        {
            if(all.Select(v=>v.Id).Distinct().Count()>=64)throw Conflict();
            id=target;version=1;generation=1;
            await using var create=ScheduleStore.Command(connection,"INSERT INTO market_data.custom_curves VALUES($1,$2,$3)",Catalog,id,body.Definition.Code);
            await create.ExecuteNonQueryAsync(ct);
        }
        else
        {
            id=body.Id.Value;
            var old=all.SingleOrDefault(v=>v.Id==id&&v.Version==body.Version);
            // The assistant revises the draft its claim holds, at the generation the claim locked, so its request may leave
            // the generation and state to the claim; a person names both.
            var claimed=saver.IsAssistant&&body.Generation is null&&body.ExpectedState is null;
            if(old is null||old.Generation!=(claimed?old.Generation:body.Generation)||old.State!=(claimed?old.State:body.ExpectedState)
                ||old.Definition.Code!=body.Definition.Code)throw Conflict();
            // The assistant saving the graph its draft already holds, pinned to the same exact references, writes nothing at
            // all (D260); a person's save is always a new generation.
            if(saver.IsAssistant&&old.State=="draft"&&Unchanged(old,body.Definition,references))return old;
            if(old.State=="published")
            {
                if(all.Any(v=>v.Id==id&&v.State=="draft"))throw Conflict();
                version=all.Where(v=>v.Id==id).Max(v=>v.Version)+1;generation=1;
            }
            else{version=old.Version;generation=old.Generation+1;}
        }
        if(version>64)throw Invalid("Keep at most 64 versions of a curve.");
        if(all.Any(v=>v.Id==id&&v.Version==version&&v.State=="draft"))
        {
            await using var update=ScheduleStore.Command(connection,"UPDATE market_data.custom_curve_versions SET generation=$4,name=$5,output_node=$6,layout=$7::jsonb WHERE(catalog_id,curve_id,version)=($1,$2,$3)",Catalog,id,version,generation,body.Definition.Name,body.Definition.Output,JsonSerializer.Serialize(body.Layout,Json));
            await update.ExecuteNonQueryAsync(ct);
            foreach(var table in new[]{"panel_weights","contract_weights","edges","node_published_sources","node_sources","node_calendars","node_schedules","nodes"})
            {
                await using var delete=ScheduleStore.Command(connection,$"DELETE FROM market_data.custom_curve_{table} WHERE(catalog_id,curve_id,version)=($1,$2,$3)",Catalog,id,version);
                await delete.ExecuteNonQueryAsync(ct);
            }
        }
        else
        {
            await using var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.custom_curve_versions(catalog_id,curve_id,version,generation,state,name,output_node,engine_version,layout) VALUES($1,$2,$3,$4,'draft',$5,$6,'custom_curve_recipe_v2',$7::jsonb)",Catalog,id,version,generation,body.Definition.Name,body.Definition.Output,JsonSerializer.Serialize(body.Layout,Json));
            await insert.ExecuteNonQueryAsync(ct);
        }
        foreach(var node in body.Definition.Nodes)
        {
            var scalars=node.Parameters.RemoveRange(["curve","calendar","schedule"]);
            await using var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.custom_curve_nodes VALUES($1,$2,$3,$4,$5,$6::jsonb)",Catalog,id,version,node.Id,node.Kind,JsonSerializer.Serialize(scalars,Json));
            await insert.ExecuteNonQueryAsync(ct);
        }
        foreach(var node in body.Definition.Nodes)
        {
            if(node.PublishedSource is {} published)
            {
                await using var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.custom_curve_node_published_sources VALUES($1,$2,$3,$4,$5,$6,$7,$8)",
                    Catalog,id,version,node.Id,published.CatalogId,published.CurveId,published.Version,published.OutputNode);
                await insert.ExecuteNonQueryAsync(ct);
            }
            foreach(var input in node.Inputs)
            {
                await using var edge=ScheduleStore.Command(connection,"INSERT INTO market_data.custom_curve_edges VALUES($1,$2,$3,$4,$5,$6)",Catalog,id,version,node.Id,input.Key,input.Value);
                await edge.ExecuteNonQueryAsync(ct);
            }
            if(references.Sources.TryGetValue(node.Id,out var source))
            {
                await using var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.custom_curve_node_sources VALUES($1,$2,$3,$4,$5,$6,$7)",Catalog,id,version,node.Id,source.CatalogId,source.RevisionId,source.Code);
                await insert.ExecuteNonQueryAsync(ct);
            }
            if(references.Calendars.TryGetValue(node.Id,out var calendar))
            {
                await using var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.custom_curve_node_calendars VALUES($1,$2,$3,$4,$5,$6,$7)",Catalog,id,version,node.Id,calendar.CatalogId,calendar.RevisionId,calendar.Code);
                await insert.ExecuteNonQueryAsync(ct);
            }
            if(references.Schedules.TryGetValue(node.Id,out var schedule))
            {
                await using var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.custom_curve_node_schedules VALUES($1,$2,$3,$4,$5,$6,$7)",Catalog,id,version,node.Id,schedule.CatalogId,schedule.Id,schedule.Version);
                await insert.ExecuteNonQueryAsync(ct);
            }
        }
        foreach(var node in body.Definition.Nodes.Where(n=>n.Kind=="weighted_contracts"))
        foreach(var weight in node.ContractWeights)
        {
            var sourceNode=node.Inputs[weight.SourcePort];var source=references.Sources[sourceNode];
            await using var insert=ScheduleStore.Command(connection,"""
INSERT INTO market_data.custom_curve_contract_weights(catalog_id,curve_id,version,node_id,output_position,source_port,source_node,
 source_catalog_id,source_revision_id,source_curve_code,contract_id,weight)
 VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12::numeric)
""",Catalog,id,version,node.Id,weight.OutputPosition,weight.SourcePort,sourceNode,source.CatalogId,source.RevisionId,source.Code,weight.ContractId,weight.Weight);
            await insert.ExecuteNonQueryAsync(ct);
        }
        foreach(var node in body.Definition.Nodes.Where(n=>n.Kind=="weighted_panels"))
        foreach(var weight in node.PanelWeights)
        {
            await using var insert=ScheduleStore.Command(connection,"""
INSERT INTO market_data.custom_curve_panel_weights(catalog_id,curve_id,version,node_id,source_port,source_node,weight)
 VALUES($1,$2,$3,$4,$5,$6,$7::numeric)
""",Catalog,id,version,node.Id,weight.SourcePort,node.Inputs[weight.SourcePort],weight.Weight);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using(var visible=ScheduleStore.Command(connection,"SELECT security.custom_curve_visible($1,$2,$3)",Catalog,id,version))
            if(!Equals(await visible.ExecuteScalarAsync(ct),true))throw Invalid("The saved dependency chain is unavailable, cyclic or exceeds its explicit bounds.");
        await using(var record=ScheduleStore.Command(connection,"""
INSERT INTO market_data.custom_curve_generation_savers(catalog_id,curve_id,version,generation,saved_by,actor_id,turn_id,step)
 VALUES($1,$2,$3,$4,$5,security.custom_curve_saver($1,$2,$3,$5,$6::uuid,$7::integer),$6::uuid,$7::integer)
""",Catalog,id,version,generation,saver.SavedBy,(object?)saver.Turn??DBNull.Value,(object?)saver.Step??DBNull.Value))
            await record.ExecuteNonQueryAsync(ct);
        var saved=(await Read(connection,ct)).Single(v=>v.Id==id&&v.Version==version);
        await Record(connection,body.RequestId,"save",payload,saved,receipt,ct);
        return saved;
    }
    // The assistant's claim of the one curve version its step saves, fenced by its turn's token. The database's refusals
    // become structured ones, and the transaction they abort rolls back with nothing written.
    async Task Claim(NpgsqlConnection connection,Guid curve,int version,CurveDraftSaver saver,CancellationToken ct)
    {
        await using var claim=ScheduleStore.Command(connection,"SELECT security.claim_custom_curve_draft($1,$2,$3,$4,$5,$6)",
            Catalog,curve,version,saver.Turn!.Value,saver.Token!,saver.Step!.Value);
        try{await claim.ExecuteNonQueryAsync(ct);}
        catch(PostgresException e)when(e.SqlState is "42501" or "P0001"&&ClaimRefusals.TryGetValue(e.MessageText,out var message))
        {throw new MarketDataException(e.MessageText,message);}
    }
    // Whether a saved version already holds this graph pinned to these exact sources, calendars and schedules.
    internal static bool Unchanged(CustomCurveVersion saved,RecipeDefinition definition,
        (ImmutableDictionary<string,CurveSourceReference> Sources,ImmutableDictionary<string,CalendarReference> Calendars,ImmutableDictionary<string,CurveScheduleReference> Schedules) references)=>
        saved.Definition.SameGraph(definition)&&SameReferences((saved.Sources,saved.Calendars,saved.Schedules),references);
    static bool SameReferences(
        (ImmutableDictionary<string,CurveSourceReference> Sources,ImmutableDictionary<string,CalendarReference> Calendars,ImmutableDictionary<string,CurveScheduleReference> Schedules) a,
        (ImmutableDictionary<string,CurveSourceReference> Sources,ImmutableDictionary<string,CalendarReference> Calendars,ImmutableDictionary<string,CurveScheduleReference> Schedules) b)=>
        Same(a.Sources,b.Sources)&&Same(a.Calendars,b.Calendars)&&Same(a.Schedules,b.Schedules);
    static bool Same<T>(ImmutableDictionary<string,T> a,ImmutableDictionary<string,T> b)=>
        a.Count==b.Count&&a.All(p=>b.TryGetValue(p.Key,out var other)&&EqualityComparer<T>.Default.Equals(p.Value,other));
    static readonly ImmutableDictionary<string,string> ClaimRefusals=new Dictionary<string,string>
    {
        ["delegation_denied"]="The assistant may save a curve draft only while you may and its grant allows it.",
        ["custom_curve_agent_scope"]="The assistant saves only a new curve or a draft it saved in this conversation.",
        ["custom_curve_agent_claimed"]="The assistant saves one curve draft per step.",
        ["custom_curve_catalog_limit"]="This catalog already holds 64 custom curves, the most it can.",
        ["agent_runtime_unavailable"]="The assistant's turn is no longer running here."
    }.ToImmutableDictionary();
    public async Task<CustomCurveVersion> Publish(NpgsqlConnection connection,CustomCurvePublishRequest body,CancellationToken ct)
    {
        await Lock(connection,ct);
        var payload=JsonSerializer.Serialize(body,Json);
        if(await Replay(connection,body.RequestId,"publish",payload,"SealMarketDefinitions",ct) is {} replay)return replay;
        var row=(await Read(connection,ct)).SingleOrDefault(v=>v.Id==body.Id&&v.Version==body.Version);
        if(row is null||row.Generation!=body.Generation)throw Conflict();
        CustomCurves.Validate(row.Definition);
        await using var command=ScheduleStore.Command(connection,"SELECT security.publish_custom_curve($1,$2,$3,$4)",Catalog,body.Id,body.Version,body.Generation);
        await command.ExecuteScalarAsync(ct);
        var result=(await Read(connection,ct)).Single(v=>v.Id==body.Id&&v.Version==body.Version);
        await Record(connection,body.RequestId,"publish",payload,result,"SealMarketDefinitions",ct);
        return result;
    }
    public async Task<(ImmutableDictionary<string,CurveSourceReference> Sources,ImmutableDictionary<string,CalendarReference> Calendars,ImmutableDictionary<string,CurveScheduleReference> Schedules)> References(NpgsqlConnection connection,RecipeDefinition definition,PreviewData data,CancellationToken ct)
    {
        var sources=ImmutableDictionary.CreateBuilder<string,CurveSourceReference>();
        var calendars=ImmutableDictionary.CreateBuilder<string,CalendarReference>();
        var schedules=ImmutableDictionary.CreateBuilder<string,CurveScheduleReference>();
        foreach(var node in definition.Nodes)
        {
            if(node.PublishedSource is {} published)await RequirePublishedSource(connection,published,data.DefinitionRevision.RevisionId,ct);
            if(node.Parameters.TryGetValue("curve",out var code))
            {
                if(node.Kind!="source"||!data.Definitions["curves"].Any(c=>c.Text("code")==code))throw Invalid("Choose a visible source curve from the selected exact definition bundle.");
                sources.Add(node.Id,new(data.DefinitionRevision.CatalogId,data.DefinitionRevision.RevisionId,code));
            }
            if(node.Parameters.TryGetValue("calendar",out var calendar))
            {
                var reference=new CalendarReference(data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId,calendar);
                var store=new ScheduleStore(Catalog,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId);
                _=await store.Calendar(connection,reference,ct);calendars.Add(node.Id,reference);
            }
            if(node.Parameters.TryGetValue("schedule",out var schedule))
            {
                var parts=schedule.Split(':');
                if(parts.Length!=2||!Guid.TryParseExact(parts[0],"D",out var scheduleId)||!int.TryParse(parts[1],out var scheduleVersion))throw Invalid("Choose an exact published schedule version.");
                await using var command=ScheduleStore.Command(connection,"SELECT state FROM market_data.date_schedule_versions WHERE(catalog_id,schedule_id,version)=($1,$2,$3)",Catalog,scheduleId,scheduleVersion);
                if(!Equals(await command.ExecuteScalarAsync(ct),"published"))throw Invalid("The exact published schedule is unavailable in this context.");
                schedules.Add(node.Id,new(Catalog,scheduleId,scheduleVersion));
            }
        }
        foreach(var node in definition.Nodes.Where(n=>n.Kind=="weighted_contracts"))
        foreach(var weight in node.ContractWeights)
        {
            var source=sources[node.Inputs[weight.SourcePort]];
            if(!data.DefinitionRows("curve_members","curve_code",source.Code).Any(m=>m.Id("contract_id")==weight.ContractId))
                throw Invalid("Every mapped contract must belong to its declared exact source curve.");
        }
        return(sources.ToImmutable(),calendars.ToImmutable(),schedules.ToImmutable());
    }
    internal async Task RequirePublishedSource(NpgsqlConnection connection,PublishedRecipeReference source,Guid revision,CancellationToken ct)
    {
        if(source.CatalogId!=Catalog)throw Invalid("Published sources must belong to the selected market catalog.");
        await using var command=ScheduleStore.Command(connection,"""
SELECT security.custom_curve_visible($1,$2,$3) AND security.custom_curve_uses_revision($1,$2,$3,$5)
 AND EXISTS(SELECT 1 FROM market_data.custom_curve_versions WHERE(catalog_id,curve_id,version,output_node,state)=($1,$2,$3,$4,'published'))
""",Catalog,source.CurveId,source.Version,source.OutputNode,revision);
        if(!Equals(await command.ExecuteScalarAsync(ct),true))throw Invalid("Choose a visible published output whose complete source chain uses this exact market revision.");
    }
    static void ValidateLayout(RecipeDefinition definition,ImmutableDictionary<string,CurveNodePosition>? layout)
    {
        if(layout is null||layout.Count>64||layout.Any(p=>!definition.Nodes.Any(n=>n.Id==p.Key)||p.Value is null||p.Value.X is <-10000 or >10000||p.Value.Y is <-10000 or >10000))throw Invalid("Supply graph layout coordinates only for its retained nodes.");
    }
}
