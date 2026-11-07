using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Rows being edited in the curve editor: the curve's own mapping, saved as its draft (a published version starts a new draft).
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveMappingEdit(Guid Id,int Version,int Generation,string ExpectedState,string Code,string Name,ContractFieldDraft[]? Contracts,DateListRowDraft[]? Dates);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveDefinitionSaveRequest(Guid RequestId,CurveDefinitionDraft Definition,Guid? SnapshotId,CurveMappingEdit? Mapping=null,
    Guid? Id=null,int? Version=null,int? Generation=null,string? ExpectedState=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveDefinitionPublishRequest(Guid RequestId,Guid Id,int Version,int Generation);
public sealed record CurveDefinitionCommandReceipt(Guid RequestId,Guid Id,int Version,int Generation,string State);
public sealed record CurveDefinitionVersion(Guid Id,int Version,int Generation,string State,CurveDefinitionDraft Definition,int MappingGeneration,
    ScheduleContext Context,int? PublicationOrder,DateTimeOffset? PublishedAt);

// D357: durable curve definitions. A version pins exact reference terms and one exact mapping version and generation. Saving
// with edited rows saves them as the mapping's draft first; publishing publishes that draft first, in the same transaction.
public sealed class CurveDefinitionStore(Guid catalog,ContractDateStore mappings)
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    static MarketDataException Conflict()=>new("curve_definition_conflict","This curve definition or request changed. Reload it or retry the exact original command.");
    static MarketDataException Invalid(string message)=>new("curve_definition",message);
    static string Payload<T>(T body)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body,Json))));
    // The mapping command a curve command carries out has its own stable identity, derived from the curve command's.
    static Guid MappingRequest(Guid request)=>AgentTools.NameUuid(request,"mapping");

    public async Task<ImmutableArray<CurveDefinitionVersion>> Read(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"""
SELECT v.definition_id,v.version,v.generation,v.state,d.code,v.name,v.kind,v.mapping_definition_id,v.mapping_version,v.mapping_generation,v.contract_count,
 v.commodity_code,v.location_code,v.quote_code,v.profile_code,v.futures_product_code,v.settlement_target_code,v.price_rule,v.mark_role,v.mark_source_code,
 v.missing_price,v.interpolation,v.interest_rate_code,t.effective_on,t.known_through,t.known_at,t.id,v.publication_order,v.published_at
 FROM market_data.curve_definition_versions v JOIN market_data.curve_definitions d ON(d.catalog_id,d.id)=(v.catalog_id,v.definition_id)
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(v.catalog_id,v.validation_context_id)
 WHERE v.catalog_id=$1 ORDER BY d.code,v.version
""",catalog);
        await using var reader=await command.ExecuteReaderAsync(ct);var rows=ImmutableArray.CreateBuilder<CurveDefinitionVersion>();
        string? Text(int i)=>reader.IsDBNull(i)?null:reader.GetString(i);
        while(await reader.ReadAsync(ct))
        {
            var definition=new CurveDefinitionDraft(reader.GetString(4),reader.GetString(5),Text(11),Text(12),Text(13),Text(14),reader.GetString(6),Text(15),
                new(reader.GetGuid(7),reader.GetInt32(8),reader.GetInt32(10)),Text(16),Text(17),Text(18),reader.GetString(19),Text(20),Text(21),Text(22));
            rows.Add(new(reader.GetGuid(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetString(3),definition,reader.GetInt32(9),
                new(reader.GetFieldValue<DateOnly>(23).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),reader.GetInt32(24),
                    reader.GetFieldValue<DateTimeOffset>(25).ToString("O",CultureInfo.InvariantCulture),reader.GetGuid(26)),
                reader.IsDBNull(27)?null:reader.GetInt32(27),reader.IsDBNull(28)?null:reader.GetFieldValue<DateTimeOffset>(28)));
        }
        return rows.ToImmutable();
    }

    // `validate` previews the definition, with any edited rows, at the selected close and returns that close's context and
    // reference revision; it runs only for a new command, so a retry returns its first outcome.
    public async Task<CurveDefinitionCommandReceipt> Save(NpgsqlConnection connection,CurveDefinitionSaveRequest body,
        Func<Task<(ScheduleContext Context,ReferenceRevision Reference)>> validate,CancellationToken ct)
    {
        await Lock(connection,ct);var payload=Payload(body);
        if(await Replay(connection,body.RequestId,"save",payload,"WriteMarketDefinitions",ct) is {} replay)return replay;
        var (context,reference)=await validate();
        var definition=body.Definition;var window=CurveDefinitions.Window(definition);int mappingVersion,mappingGeneration;
        if(body.Mapping is {} edit)
        {
            if(edit.Id!=window.MappingId)throw Invalid("Edit the rows of the mapping this curve names.");
            var mapping=await mappings.Save(connection,new(MappingRequest(body.RequestId),edit.Code,edit.Name,context,edit.Contracts,edit.Id,edit.Version,edit.Generation,edit.ExpectedState,edit.Dates),ct);
            (mappingVersion,mappingGeneration)=(mapping.Version,mapping.Generation);
        }
        else
        {
            var pinned=(await mappings.Read(connection,ct)).SingleOrDefault(v=>v.Id==window.MappingId&&v.Version==window.MappingVersion)
                ??throw Invalid("The exact mapping version this curve names is unavailable.");
            (mappingVersion,mappingGeneration)=(pinned.Version,pinned.Generation);
        }
        var all=await Read(connection,ct);Guid id;int version,generation;
        if(body.Id is null)
        {
            if(body.Version is not null||body.Generation is not null||body.ExpectedState is not null||all.Any(v=>v.Definition.Code==definition.Code)||
               all.Select(v=>v.Id).Distinct().Count()>=64)throw Conflict();
            id=Guid.NewGuid();version=1;generation=1;
            await using var identity=ScheduleStore.Command(connection,"INSERT INTO market_data.curve_definitions VALUES($1,$2,$3)",catalog,id,definition.Code!);await identity.ExecuteNonQueryAsync(ct);
        }
        else
        {
            id=body.Id.Value;var old=all.SingleOrDefault(v=>v.Id==id&&v.Version==body.Version);
            if(old is null||old.Generation!=body.Generation||old.State!=body.ExpectedState||old.Definition.Code!=definition.Code)throw Conflict();
            if(old.Definition.ContractKind!=definition.ContractKind)throw Invalid("A curve keeps its kind; define a new curve for another kind.");
            if(old.State=="published")
            {
                if(all.Any(v=>v.Id==id&&v.State=="draft"))throw Conflict();
                version=all.Where(v=>v.Id==id).Max(v=>v.Version)+1;generation=1;
            }
            else {version=old.Version;generation=old.Generation+1;}
        }
        if(version>64)throw Invalid("Keep at most 64 versions per curve definition.");
        object Value(string? text)=>(object?)text??DBNull.Value;
        object[] terms=[definition.Name!,reference.CatalogId,reference.RevisionId,window.MappingId,mappingVersion,mappingGeneration,window.Count,
            Value(definition.Commodity),Value(definition.Location),Value(definition.Quote),Value(definition.Profile),Value(definition.FuturesProduct),Value(definition.SettlementTarget),
            Value(definition.PriceRule),Value(definition.MarkRole),Value(definition.MissingPrice),Value(definition.Interpolation),Value(definition.InterestRate),definition.MarkSource!,context.ContextId!.Value];
        const string Columns="name,reference_catalog_id,reference_revision_id,mapping_definition_id,mapping_version,mapping_generation,contract_count,commodity_code,location_code,"+
            "quote_code,profile_code,futures_product_code,settlement_target_code,price_rule,mark_role,missing_price,interpolation,interest_rate_code,mark_source_code,validation_context_id";
        if(all.Any(v=>v.Id==id&&v.Version==version))
        {
            var assignments=string.Join(",",Columns.Split(',').Select((column,i)=>$"{column}=${i+5}"));
            await using var update=ScheduleStore.Command(connection,$"UPDATE market_data.curve_definition_versions SET generation=$4,{assignments} WHERE(catalog_id,definition_id,version)=($1,$2,$3)",
                [catalog,id,version,generation,..terms]);
            await update.ExecuteNonQueryAsync(ct);
        }
        else
        {
            var values=string.Join(",",Enumerable.Range(6,terms.Length).Select(i=>"$"+i.ToString(CultureInfo.InvariantCulture)));
            await using var insert=ScheduleStore.Command(connection,$"INSERT INTO market_data.curve_definition_versions(catalog_id,definition_id,version,generation,state,kind,{Columns}) VALUES($1,$2,$3,$4,'draft',$5,{values})",
                [catalog,id,version,generation,definition.ContractKind!,..terms]);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var receipt=Receipt(body.RequestId,(await Read(connection,ct)).Single(v=>v.Id==id&&v.Version==version));
        await Record(connection,body.RequestId,"save",payload,receipt,"WriteMarketDefinitions",ct);return receipt;
    }

    public async Task<CurveDefinitionCommandReceipt> Publish(NpgsqlConnection connection,CurveDefinitionPublishRequest body,CancellationToken ct)
    {
        await Lock(connection,ct);var payload=Payload(body);
        await using(var access=ScheduleStore.Command(connection,"SELECT security.curve_definition_visible($1,$2,$3)",catalog,body.Id,body.Version))
            if(!Equals(await access.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        if(await Replay(connection,body.RequestId,"publish",payload,"SealMarketDefinitions",ct) is {} replay)return replay;
        var value=(await Read(connection,ct)).SingleOrDefault(v=>v.Id==body.Id&&v.Version==body.Version)??throw Conflict();
        var window=value.Definition.Contracts!;
        var mapping=(await mappings.Read(connection,ct)).Single(v=>v.Id==window.MappingId&&v.Version==window.MappingVersion);
        // The pinned mapping draft publishes first; the curve's own publication below fails the whole transaction if it is stale.
        if(mapping.State=="draft")
        {
            if(mapping.Generation!=value.MappingGeneration)throw Conflict();
            await mappings.Publish(connection,new(MappingRequest(body.RequestId),mapping.Id,mapping.Version,mapping.Generation),ct);
        }
        await using(var command=ScheduleStore.Command(connection,"SELECT security.publish_curve_definition($1,$2,$3,$4)",catalog,body.Id,body.Version,body.Generation))await command.ExecuteScalarAsync(ct);
        var receipt=Receipt(body.RequestId,(await Read(connection,ct)).Single(v=>v.Id==body.Id&&v.Version==body.Version));
        await Record(connection,body.RequestId,"publish",payload,receipt,"SealMarketDefinitions",ct);return receipt;
    }

    static CurveDefinitionCommandReceipt Receipt(Guid requestId,CurveDefinitionVersion value)=>new(requestId,value.Id,value.Version,value.Generation,value.State);
    static async Task Lock(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849102);SELECT pg_advisory_xact_lock(730849150)");
        await command.ExecuteNonQueryAsync(ct);
    }
    async Task<CurveDefinitionCommandReceipt?> Replay(NpgsqlConnection connection,Guid requestId,string operation,string payload,string permission,CancellationToken ct)
    {
        if(requestId==Guid.Empty)throw Invalid("Supply a stable request identity.");
        await using var actor=ScheduleStore.Command(connection,"SELECT security.custom_curve_actor($1,$2)",catalog,permission);
        if(await actor.ExecuteScalarAsync(ct) is not Guid)throw new PreviewDeniedException();
        await using var command=ScheduleStore.Command(connection,"SELECT operation,payload_sha256=$3,definition_id,version,generation,state FROM market_data.curve_definition_requests WHERE catalog_id=$1 AND actor_id=security.custom_curve_actor($1,$4) AND request_id=$2",catalog,requestId,payload,permission);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))return null;
        if(reader.GetString(0)!=operation||!reader.GetBoolean(1))throw Conflict();
        return new(requestId,reader.GetGuid(2),reader.GetInt32(3),reader.GetInt32(4),reader.GetString(5));
    }
    async Task Record(NpgsqlConnection connection,Guid requestId,string operation,string payload,CurveDefinitionCommandReceipt receipt,string permission,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"INSERT INTO market_data.curve_definition_requests VALUES($1,security.custom_curve_actor($1,$2),$3,$4,$5,$6,$7,$8,$9)",
            catalog,permission,requestId,operation,payload,receipt.Id,receipt.Version,receipt.Generation,receipt.State);
        await command.ExecuteNonQueryAsync(ct);
    }
}
