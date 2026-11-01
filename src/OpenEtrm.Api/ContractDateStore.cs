using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContractDateSaveRequest(Guid RequestId,string Code,string Name,ScheduleContext Context,ContractFieldDraft[] Contracts,
    Guid? Id=null,int? Version=null,int? Generation=null,string? ExpectedState=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContractDatePublishRequest(Guid RequestId,Guid Id,int Version,int Generation);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContractDateResolveRequest(Guid Id,int Version,ScheduleContext Context);
public sealed record ContractDateCommandReceipt(Guid RequestId,Guid Id,int Version,int Generation,string State);
public sealed record ContractDateBinding(int Ordinal,string Role,DateOnly? LiteralDate,Guid? ScheduleCatalogId,Guid? ScheduleId,
    int? ScheduleVersion,int? Occurrence,int? BusinessDays,string Expression);
public sealed record ContractDateVersion(Guid Id,int Version,int Generation,string State,PublishedContractRequest Definition,
    ImmutableArray<ContractDateBinding> Bindings,int? PublicationOrder,DateTimeOffset? PublishedAt,string EngineVersion);

// The shared library retains date rules and exact source versions. Resolving one
// never admits instruments or replaces literal dates already held by a side.
public sealed class ContractDateStore(Guid catalog,ScheduleStore schedules)
{
    const string Engine="contract_dates_v1";
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    static TemporalException Invalid(string message)=>new("contract_dates",message);
    static MarketDataException Conflict()=>new("contract_date_conflict","This definition or request changed. Reload it or retry the exact original command.");
    static ContractDateCommandReceipt Receipt(Guid requestId,ContractDateVersion value)=>new(requestId,value.Id,value.Version,value.Generation,value.State);

    public static BoundContractDefinition Compile(PublishedContractRequest body,IEnumerable<ScheduleVersion> versions,ScheduleContext context,
        ImmutableArray<ContractDateBinding>? pinned=null)
    {
        if(body.Code is null||!System.Text.RegularExpressions.Regex.IsMatch(body.Code,@"\A[a-z][a-z0-9_]{0,31}\z")||
            string.IsNullOrWhiteSpace(body.Name)||body.Name.Length>80||body.Contracts is null||body.Contracts.Length is <1 or >120||
            body.Contracts.Any(c=>c is null)||body.Contracts.Select(c=>c.Label).Distinct().Count()!=body.Contracts.Length)
            throw Invalid("Name the definition and supply uniquely named contracts.");
        var resolver=new PublishedScheduleResolver(versions,context);
        var bindings=pinned?.ToDictionary(b=>(b.Ordinal,b.Role));
        var rows=body.Contracts.Select((draft,ordinal)=>DateWorkspace.Contract(new(draft.Label,draft.Start,draft.End,draft.LastTrading,draft.CutoffUtc),(role,expression)=>
        {
            var binding=bindings?.GetValueOrDefault((ordinal,role));
            if(pinned is not null&&(binding is null||binding.Expression!=expression))throw Invalid("The exact retained date-field binding is missing or inconsistent.");
            return resolver.Resolve(expression,binding?.ScheduleId,binding?.ScheduleVersion);
        })).ToImmutableArray();
        for(var i=1;i<rows.Length;i++)if(rows[i].DeliveryFrom<rows[i-1].DeliveryTo||rows[i].Expiry<=rows[i-1].Expiry)
            throw Invalid("Contract periods must be ordered without overlap and have increasing last-trading instants.");
        return new(body.Code,body.Name,context,[..rows.Select(c=>new DatedContractDefinition(c.Label,c.DeliveryFrom,c.DeliveryTo,c.Expiry,c.DeliveryStart,c.DeliveryEnd,c.LastTrading,c.CutoffUtc))]);
    }

    public async Task<ImmutableArray<ContractDateVersion>> Read(NpgsqlConnection connection,CancellationToken ct)
    {
        var headers=new List<(Guid Id,int Version,int Generation,string State,string Code,string Name,ScheduleContext Context,int? Order,DateTimeOffset? At,string Engine)>();
        var contracts=new Dictionary<(Guid,int),List<(int Ordinal,string Label,string Cutoff)>>();
        var fields=new Dictionary<(Guid,int),List<ContractDateBinding>>();var seen=new HashSet<(Guid,int)>();
        // One statement pins headers, contracts and source fields to one MVCC
        // snapshot, including concurrent authorized raw draft updates.
        await using(var command=ScheduleStore.Command(connection,"""
SELECT v.definition_id,v.version,v.generation,v.state,d.code,v.name,t.effective_on,t.known_through,t.known_at,t.id,v.publication_order,v.published_at,v.engine_version,
 c.ordinal,c.label,c.cutoff_utc,f.role,f.literal_date,f.schedule_catalog_id,f.schedule_id,f.schedule_version,f.occurrence,f.business_days,s.symbol
 FROM market_data.contract_date_versions v JOIN market_data.contract_date_definitions d ON(d.catalog_id,d.id)=(v.catalog_id,v.definition_id)
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(v.catalog_id,v.validation_context_id)
 LEFT JOIN market_data.contract_date_contracts c ON(c.catalog_id,c.definition_id,c.version)=(v.catalog_id,v.definition_id,v.version)
 LEFT JOIN market_data.contract_date_fields f ON(f.catalog_id,f.definition_id,f.version,f.ordinal)=(c.catalog_id,c.definition_id,c.version,c.ordinal)
 LEFT JOIN market_data.date_schedules s ON(s.catalog_id,s.id)=(f.schedule_catalog_id,f.schedule_id)
 WHERE v.catalog_id=$1 ORDER BY d.code,v.version,c.ordinal,f.role
""",catalog))
        await using(var reader=await command.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))
        {
            var key=(reader.GetGuid(0),reader.GetInt32(1));
            if(seen.Add(key))headers.Add((key.Item1,key.Item2,reader.GetInt32(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),
                new(reader.GetFieldValue<DateOnly>(6).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),reader.GetInt32(7),reader.GetFieldValue<DateTimeOffset>(8).ToString("O",CultureInfo.InvariantCulture),reader.GetGuid(9)),
                reader.IsDBNull(10)?null:reader.GetInt32(10),reader.IsDBNull(11)?null:reader.GetFieldValue<DateTimeOffset>(11),reader.GetString(12)));
            if(reader.IsDBNull(13))continue;
            var ordinal=reader.GetInt32(13);
            if(!contracts.TryGetValue(key,out var contractRows))contracts[key]=contractRows=[];
            if(!contractRows.Any(c=>c.Ordinal==ordinal))contractRows.Add((ordinal,reader.GetString(14),reader.GetFieldValue<TimeOnly>(15).ToString("HH:mm",CultureInfo.InvariantCulture)));
            if(reader.IsDBNull(16))continue;
            if(!fields.TryGetValue(key,out var fieldRows))fields[key]=fieldRows=[];
            DateOnly? literal=reader.IsDBNull(17)?null:reader.GetFieldValue<DateOnly>(17);
            int? occurrence=reader.IsDBNull(21)?null:reader.GetInt32(21),offset=reader.IsDBNull(22)?null:reader.GetInt32(22);
            if(literal is null&&(reader.IsDBNull(23)||occurrence is null))throw new PreviewDeniedException();
            var expression=literal?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)??reader.GetString(23)+"["+occurrence!.Value.ToString(CultureInfo.InvariantCulture)+"]"+
                (offset is null?"":offset>0?" + "+offset.Value.ToString(CultureInfo.InvariantCulture)+"bd":" - "+(-offset.Value).ToString(CultureInfo.InvariantCulture)+"bd");
            fieldRows.Add(new(ordinal,reader.GetString(16),literal,reader.IsDBNull(18)?null:reader.GetGuid(18),reader.IsDBNull(19)?null:reader.GetGuid(19),reader.IsDBNull(20)?null:reader.GetInt32(20),occurrence,offset,expression));
        }
        if(headers.Count>0)
        {
            await using var authority=ScheduleStore.Command(connection,"SELECT bool_and(security.contract_date_visible($1,definition_id,version)) FROM unnest($2::uuid[],$3::integer[]) AS selected(definition_id,version)",
                catalog,headers.Select(h=>h.Id).ToArray(),headers.Select(h=>h.Version).ToArray());
            if(!Equals(await authority.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        }
        return [..headers.Select(h=>
        {
            var key=(h.Id,h.Version);var bindings=fields.GetValueOrDefault(key)??[];
            string Field(int ordinal,string role)=>bindings.SingleOrDefault(b=>b.Ordinal==ordinal&&b.Role==role)?.Expression
                ??throw Invalid("A retained definition is missing a required date field.");
            if(!contracts.TryGetValue(key,out var savedContracts)||savedContracts.Count==0)throw Invalid("A retained definition has no contract rows.");
            var rows=savedContracts.Select(c=>new ContractFieldDraft(c.Label,Field(c.Ordinal,"start"),Field(c.Ordinal,"end"),Field(c.Ordinal,"last_trading"),c.Cutoff)).ToArray();
            return new ContractDateVersion(h.Id,h.Version,h.Generation,h.State,new(h.Code,h.Name,h.Context,rows),[..bindings],h.Order,h.At,h.Engine);
        })];
    }
    async Task Lock(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849102);SELECT pg_advisory_xact_lock(730849150)");
        await command.ExecuteNonQueryAsync(ct);
    }
    async Task<ContractDateCommandReceipt?> Replay(NpgsqlConnection connection,Guid requestId,string operation,string payload,string permission,CancellationToken ct)
    {
        if(requestId==Guid.Empty)throw Invalid("Supply a stable request identity.");
        await using var actor=ScheduleStore.Command(connection,"SELECT security.custom_curve_actor($1,$2)",catalog,permission);
        if(await actor.ExecuteScalarAsync(ct) is not Guid)throw new PreviewDeniedException();
        await using var command=ScheduleStore.Command(connection,"SELECT operation,payload_sha256=$3,definition_id,version,generation,state FROM market_data.contract_date_requests WHERE catalog_id=$1 AND actor_id=security.custom_curve_actor($1,$4) AND request_id=$2",catalog,requestId,payload,permission);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))return null;
        if(reader.GetString(0)!=operation||!reader.GetBoolean(1))throw Conflict();
        return new(requestId,reader.GetGuid(2),reader.GetInt32(3),reader.GetInt32(4),reader.GetString(5));
    }
    async Task Record(NpgsqlConnection connection,Guid requestId,string operation,string payload,ContractDateCommandReceipt receipt,string permission,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"INSERT INTO market_data.contract_date_requests VALUES($1,security.custom_curve_actor($1,$2),$3,$4,$5,$6,$7,$8,$9)",
            catalog,permission,requestId,operation,payload,receipt.Id,receipt.Version,receipt.Generation,receipt.State);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<ContractDateCommandReceipt> Save(NpgsqlConnection connection,ContractDateSaveRequest body,CancellationToken ct)
    {
        await Lock(connection,ct);var payload=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body,Json))));
        if(await Replay(connection,body.RequestId,"save",payload,"WriteMarketDefinitions",ct) is {} replay)return replay;
        var context=await new KnowledgeStore(catalog).Retained(connection,body.Context??throw Invalid("Choose an exact retained date context."),ct);
        var compiled=Compile(new(body.Code,body.Name,context,body.Contracts),await schedules.ReadAt(connection,context,ct),context);
        var all=await Read(connection,ct);Guid id;int version,generation;
        if(body.Id is null)
        {
            if(body.Version is not null||body.Generation is not null||body.ExpectedState is not null||all.Select(v=>v.Id).Distinct().Count()>=32)throw Conflict();
            id=Guid.NewGuid();version=1;generation=1;
            await using var identity=ScheduleStore.Command(connection,"INSERT INTO market_data.contract_date_definitions VALUES($1,$2,$3)",catalog,id,body.Code);await identity.ExecuteNonQueryAsync(ct);
        }
        else
        {
            id=body.Id.Value;var old=all.SingleOrDefault(v=>v.Id==id&&v.Version==body.Version);
            if(old is null||old.Generation!=body.Generation||old.State!=body.ExpectedState||old.Definition.Code!=body.Code)throw Conflict();
            if(old.State=="published")
            {
                if(all.Any(v=>v.Id==id&&v.State=="draft"))throw Conflict();
                version=all.Where(v=>v.Id==id).Max(v=>v.Version)+1;generation=1;
            }
            else {version=old.Version;generation=old.Generation+1;}
        }
        if(version>64)throw Invalid("Keep at most 64 versions per definition.");
        if(all.Any(v=>v.Id==id&&v.Version==version))
        {
            await using var update=ScheduleStore.Command(connection,"UPDATE market_data.contract_date_versions SET generation=$4,name=$5,validation_context_id=$6 WHERE(catalog_id,definition_id,version)=($1,$2,$3)",catalog,id,version,generation,body.Name,context.ContextId!.Value);await update.ExecuteNonQueryAsync(ct);
            foreach(var table in new[]{"fields","contracts"})
            {await using var delete=ScheduleStore.Command(connection,$"DELETE FROM market_data.contract_date_{table} WHERE(catalog_id,definition_id,version)=($1,$2,$3)",catalog,id,version);await delete.ExecuteNonQueryAsync(ct);}
        }
        else
        {
            await using var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.contract_date_versions(catalog_id,definition_id,version,generation,state,name,engine_version,validation_context_id) VALUES($1,$2,$3,$4,'draft',$5,$6,$7)",catalog,id,version,generation,body.Name,Engine,context.ContextId!.Value);await insert.ExecuteNonQueryAsync(ct);
        }
        for(var ordinal=0;ordinal<compiled.Contracts.Length;ordinal++)
        {
            var row=compiled.Contracts[ordinal];
            await using(var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.contract_date_contracts VALUES($1,$2,$3,$4,$5,$6)",catalog,id,version,ordinal,row.Label,TimeOnly.ParseExact(row.CutoffUtc,"HH:mm",CultureInfo.InvariantCulture)))await insert.ExecuteNonQueryAsync(ct);
            foreach(var (role,field) in new[]{("start",row.StartReference),("end",row.EndReference),("last_trading",row.LastTradingReference)})
            {
                var source=field.Source as PublishedDateSource;var reference=source is null?null:DateReference.Parse(field.Expression);
                await using var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.contract_date_fields VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)",catalog,id,version,ordinal,role,
                    source is null?field.Date:DBNull.Value,source is null?DBNull.Value:catalog,source is null?DBNull.Value:source.ScheduleId,source is null?DBNull.Value:source.Version,
                    reference is null?DBNull.Value:reference.Index??0,reference?.BusinessDays is {} offset?offset:DBNull.Value);
                await insert.ExecuteNonQueryAsync(ct);
            }
        }
        var receipt=Receipt(body.RequestId,(await Read(connection,ct)).Single(v=>v.Id==id&&v.Version==version));
        await Record(connection,body.RequestId,"save",payload,receipt,"WriteMarketDefinitions",ct);return receipt;
    }
    public async Task<ContractDateCommandReceipt> Publish(NpgsqlConnection connection,ContractDatePublishRequest body,CancellationToken ct)
    {
        await Lock(connection,ct);var payload=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body,Json))));
        await using(var access=ScheduleStore.Command(connection,"SELECT security.contract_date_visible($1,$2,$3)",catalog,body.Id,body.Version))
            if(!Equals(await access.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        if(await Replay(connection,body.RequestId,"publish",payload,"SealMarketDefinitions",ct) is {} replay)return replay;
        var value=(await Read(connection,ct)).SingleOrDefault(v=>v.Id==body.Id&&v.Version==body.Version);
        if(value is null||value.Generation!=body.Generation)throw Conflict();
        await ResolveRule(connection,value,value.Definition.Context!,ct);
        await using(var command=ScheduleStore.Command(connection,"SELECT security.publish_contract_dates($1,$2,$3,$4)",catalog,body.Id,body.Version,body.Generation))await command.ExecuteScalarAsync(ct);
        var receipt=Receipt(body.RequestId,(await Read(connection,ct)).Single(v=>v.Id==body.Id&&v.Version==body.Version));
        await Record(connection,body.RequestId,"publish",payload,receipt,"SealMarketDefinitions",ct);return receipt;
    }
    async Task<BoundContractDefinition> ResolveRule(NpgsqlConnection connection,ContractDateVersion value,ScheduleContext context,CancellationToken ct)
    {
        if(value.EngineVersion!=Engine)throw Invalid("This exact retained engine is unavailable.");
        // Exact pinned versions remain eligible even when a newer symbolic version exists.
        var versions=await schedules.Read(connection,ct);
        foreach(var binding in value.Bindings.Where(b=>b.ScheduleId is not null))
        {
            await using var visible=ScheduleStore.Command(connection,"SELECT security.contract_date_schedule_visible($1,$2,$3)",binding.ScheduleCatalogId!.Value,binding.ScheduleId!.Value,binding.ScheduleVersion!.Value);
            if(!Equals(await visible.ExecuteScalarAsync(ct),true))throw Invalid("An exact source schedule is unavailable in this view.");
        }
        return Compile(value.Definition,versions,context,value.Bindings);
    }
    public async Task<BoundContractDefinition> Resolve(NpgsqlConnection connection,ContractDateResolveRequest body,ScheduleContext? selectedContext,CancellationToken ct)
    {
        var context=await new KnowledgeStore(catalog).Retained(connection,body.Context??throw Invalid("Choose an exact retained date context."),ct);
        if(selectedContext is null)await MarketContextStore.Bind(connection,catalog,context,ct);
        else if(selectedContext.ContextId!=context.ContextId)throw Invalid("Resolve in the selected exact market context, or explicitly change that selection first.");
        var value=(await Read(connection,ct)).SingleOrDefault(v=>v.Id==body.Id&&v.Version==body.Version&&v.State=="published")
            ??throw Invalid("This exact published definition is unavailable in the selected knowledge view.");
        return (await ResolveRule(connection,value,context,ct)) with{DefinitionId=value.Id,Version=value.Version,EngineVersion=value.EngineVersion};
    }
}
