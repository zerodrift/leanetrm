using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// The exact currency and quantity specification a PriceUnit holds, and its quote multiplier.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PriceUnitReference(ReferenceKey Currency,ReferenceKey Quantity,string Multiplier);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DefinitionSize(PriceUnitReference Unit,string Size);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveRiskRunDefinition(Guid RequestId,Guid? DefinitionId,int? ExpectedVersion,string Name,ImmutableArray<Guid> BookIds,string Preset,ImmutableArray<DefinitionSize> Sizes);

// Saved setups for risk runs. A definition keeps what to shock (books and a preset) and by how much (a size per price unit);
// resolving the preset to targets happens when it runs, never here.
public static class RiskRunDefinitions
{
    private const int MaxBooks=4096,MaxSizes=64,MaxNameLength=80;
    private static readonly string[] Presets=["held_contracts","held_curves"];
    private static MarketDataException Invalid()=>new("risk_definition","Supply a name, at least one book, a supported preset and one positive size for each distinct price unit.");
    private static MarketDataException Conflict()=>new("risk_definition_conflict","This definition changed. Refresh before saving it.");
    private static string Decimal(string? text)
    {
        try{var value=ExactDecimal.Parse(text??throw Invalid());return value.CompareTo(default)>0?value.ToString():throw Invalid();}
        catch(MarketDataException e) when(e.Code is "numeric_format" or "numeric_range"){throw Invalid();}
    }
    private static ReferenceKey Reference(ReferenceKey key)=>key.Code is not null?key:throw Invalid();

    // The request in canonical form: books in order, decimals plain, sizes in a fixed order. Equal setups give equal bodies.
    private static SaveRiskRunDefinition Canonical(SaveRiskRunDefinition request)
    {
        if(request.RequestId==Guid.Empty||request.Name is null||request.Name.Trim() is not {Length:>0 and <=MaxNameLength}||request.ExpectedVersion is<1
            ||(request.DefinitionId is null)!=(request.ExpectedVersion is null)||!Presets.Contains(request.Preset)
            ||request.BookIds.IsDefault||request.BookIds.Length is 0 or>MaxBooks||request.BookIds.Distinct().Count()!=request.BookIds.Length
            ||request.Sizes.IsDefault||request.Sizes.Length is 0 or>MaxSizes||request.Sizes.Any(s=>s?.Unit is null))throw Invalid();
        var sizes=request.Sizes.Select(s=>new DefinitionSize(new(Reference(s.Unit.Currency),Reference(s.Unit.Quantity),Decimal(s.Unit.Multiplier)),Decimal(s.Size))).ToArray();
        if(sizes.Any(s=>s.Unit.Currency.Revision!=s.Unit.Quantity.Revision)||sizes.Select(s=>s.Unit).Distinct().Count()!=sizes.Length)throw Invalid();
        return request with{Name=request.Name.Trim(),BookIds=[..request.BookIds.Order()],
            Sizes=[..sizes.OrderBy(s=>JsonSerializer.Serialize(s.Unit),StringComparer.Ordinal)]};
    }

    private const string VersionJson="""
        json_build_object('definitionId',v.definition_id,'version',v.version,'name',v.name,'preset',v.preset,'knownFrom',v.known_from,
         'bookIds',(SELECT coalesce(json_agg(b.book_id ORDER BY b.book_id),'[]') FROM risk.run_definition_books b WHERE(b.catalog_id,b.definition_id,b.version)=(v.catalog_id,v.definition_id,v.version)),
         'sizes',(SELECT coalesce(json_agg(json_build_object('size',s.size::text,'unit',json_build_object('multiplier',s.multiplier::text,
           'currency',json_build_object('revision',json_build_object('catalogId',s.reference_catalog_id,'revisionId',s.revision_id),'code',s.currency_code),
           'quantity',json_build_object('revision',json_build_object('catalogId',s.reference_catalog_id,'revisionId',s.revision_id),'code',s.quantity_code)))
           ORDER BY s.currency_code,s.quantity_code,s.multiplier),'[]') FROM risk.run_definition_sizes s WHERE(s.catalog_id,s.definition_id,s.version)=(v.catalog_id,v.definition_id,v.version)))::text
        """;
    private static JsonElement Parse(string json)=>JsonSerializer.Deserialize<JsonElement>(json);

    // Each of the account's definitions at its latest version, newest first.
    public static async Task<JsonElement> List(NpgsqlConnection connection,Guid catalogId,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand($"""
            SELECT coalesce(json_agg({VersionJson}::json ORDER BY v.known_from DESC,v.definition_id),'[]')::text
            FROM risk.run_definition_versions v WHERE v.catalog_id=$1
             AND v.version=(SELECT max(l.version) FROM risk.run_definition_versions l WHERE(l.catalog_id,l.definition_id)=(v.catalog_id,v.definition_id))
            """,connection);
        command.Parameters.AddWithValue(catalogId);
        return Parse((string)(await command.ExecuteScalarAsync(ct))!);
    }

    public static async Task<JsonElement> Save(NpgsqlConnection connection,Guid catalogId,SaveRiskRunDefinition submitted,CancellationToken ct)
    {
        var request=Canonical(submitted);
        // Serialize this account's saves so a replay, the version check and the writes see one state.
        await using(var guard=new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(tenant_id::text||workspace_id::text||principal_id::text||'risk_definition',0)) FROM risk.definition_identity()",connection))
        {
            await using var reader=await guard.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct))throw new PreviewDeniedException();
        }
        var hash=SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request with{RequestId=Guid.Empty}));
        await using(var prior=new NpgsqlCommand("SELECT body_hash,response::text FROM risk.run_definition_requests WHERE request_id=$1",connection))
        {
            prior.Parameters.AddWithValue(request.RequestId);
            await using var reader=await prior.ExecuteReaderAsync(ct);
            if(await reader.ReadAsync(ct))
                return hash.AsSpan().SequenceEqual((byte[])reader[0])?Parse(reader.GetString(1))
                    :throw new MarketDataException("risk_definition_request_conflict","This request id already saved a different definition. Reload before saving again.");
        }
        await using(var access=new NpgsqlCommand("SELECT security.trading_access($1,'ReadTrades')",connection))
        {
            access.Parameters.AddWithValue(catalogId);
            if(await access.ExecuteScalarAsync(ct) is not true)throw new PreviewDeniedException();
        }
        var definitionId=request.DefinitionId??Guid.NewGuid();
        var version=1;
        if(request.DefinitionId is null) await Execute(connection,"""
            INSERT INTO risk.run_definitions(catalog_id,id,tenant_id,workspace_id,owner_id)
            SELECT $1,$2,tenant_id,workspace_id,principal_id FROM risk.definition_identity()
            """,ct,catalogId,definitionId);
        else
        {
            await using var latest=new NpgsqlCommand("SELECT max(version) FROM risk.run_definition_versions WHERE(catalog_id,definition_id)=($1,$2)",connection);
            latest.Parameters.AddWithValue(catalogId);latest.Parameters.AddWithValue(definitionId);
            if(await latest.ExecuteScalarAsync(ct) is not int current||current!=request.ExpectedVersion)throw Conflict();
            version=current+1;
        }
        await Execute(connection,"INSERT INTO risk.run_definition_versions VALUES($1,$2,$3,$4,$5,clock_timestamp())",ct,catalogId,definitionId,version,request.Name,request.Preset);
        try{await Execute(connection,"INSERT INTO risk.run_definition_books SELECT $1,$2,$3,unnest($4::uuid[])",ct,catalogId,definitionId,version,request.BookIds.ToArray());}
        // A book that does not exist in this catalog is one the account cannot read.
        catch(PostgresException e) when(e.SqlState==PostgresErrorCodes.ForeignKeyViolation){throw new PreviewDeniedException();}
        try
        {
            foreach(var (unit,size) in request.Sizes.Select(s=>(s.Unit,s.Size)))
                await Execute(connection,"INSERT INTO risk.run_definition_sizes VALUES($1,$2,$3,$4,$5,$6,$7,$8::numeric,$9::numeric)",ct,
                    catalogId,definitionId,version,unit.Currency.Revision.CatalogId,unit.Currency.Revision.RevisionId,unit.Currency.Code,unit.Quantity.Code,unit.Multiplier,size);
        }
        catch(PostgresException e) when(e.SqlState==PostgresErrorCodes.ForeignKeyViolation){throw Invalid();}
        await using var read=new NpgsqlCommand($"SELECT {VersionJson} FROM risk.run_definition_versions v WHERE(v.catalog_id,v.definition_id,v.version)=($1,$2,$3)",connection);
        read.Parameters.AddWithValue(catalogId);read.Parameters.AddWithValue(definitionId);read.Parameters.AddWithValue(version);
        var response=(string)(await read.ExecuteScalarAsync(ct))!;
        await Execute(connection,"""
            INSERT INTO risk.run_definition_requests(tenant_id,workspace_id,principal_id,request_id,body_hash,response)
            SELECT tenant_id,workspace_id,principal_id,$1,$2,$3 FROM risk.definition_identity()
            """,ct,request.RequestId,hash,JsonParameter(response));
        return Parse(response);
    }

    private static NpgsqlParameter JsonParameter(string json)=>new(){NpgsqlDbType=NpgsqlDbType.Jsonb,Value=json};
    private static async Task Execute(NpgsqlConnection connection,string sql,CancellationToken ct,params object[] values)
    {
        await using var command=new NpgsqlCommand(sql,connection);
        foreach(var value in values)command.Parameters.Add(value as NpgsqlParameter??new NpgsqlParameter{Value=value});
        await command.ExecuteNonQueryAsync(ct);
    }
}
