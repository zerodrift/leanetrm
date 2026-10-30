using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunRiskDefinition(Guid RequestId,int Version,FuturesReadRequest Context);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
// The latest run of a definition at a context, or the tied run the caller chose explicitly.
public sealed record LatestRiskRun(FuturesReadRequest Context,Guid? RunId=null);
public sealed record RiskRunAt(Guid RunId,DateTimeOffset ValuationAt);
// A run tied for the latest, told apart from the others by its version and when it became known.
public sealed record TiedRiskRun(Guid RunId,int Version,DateTimeOffset ValuationAt,DateTimeOffset KnownAt);
public sealed record RiskDefinitionName(string Name,int Version);
// Completed runs of one definition tied for the latest: none is chosen for the caller.
public sealed class RiskRunTieException(RiskDefinitionName definition,ImmutableArray<TiedRiskRun> runs):Exception("Completed runs of this definition share the latest valuation time. Choose one of them explicitly.")
{
    public const string Code="risk_run_tie";
    public RiskDefinitionName Definition{get;}=definition;
    public ImmutableArray<TiedRiskRun> Runs{get;}=runs;
}

// Saved setups for risk runs. A definition keeps what to shock (books and a preset) and by how much (a size per price unit);
// resolving the preset to targets happens when it runs, from the risk catalog's held targets at the run's retained context.
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
        json_build_object('definitionId',v.definition_id,'version',v.version,'name',v.name,'preset',v.preset,'savedAt',v.saved_at,
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
            SELECT coalesce(json_agg({VersionJson}::json ORDER BY v.saved_at DESC,v.definition_id),'[]')::text
            FROM risk.run_definition_versions v WHERE v.catalog_id=$1
             AND v.version=(SELECT max(l.version) FROM risk.run_definition_versions l WHERE(l.catalog_id,l.definition_id)=(v.catalog_id,v.definition_id))
            """,connection);
        command.Parameters.AddWithValue(catalogId);
        return Parse((string)(await command.ExecuteScalarAsync(ct))!);
    }

    public static async Task<JsonElement> Save(NpgsqlConnection connection,Guid catalogId,SaveRiskRunDefinition submitted,CancellationToken ct)
    {
        var request=Canonical(submitted);
        var hash=SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request with{RequestId=Guid.Empty}));
        if(await Replay(connection,request.RequestId,hash,ct) is {} prior)return prior;
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
        return await Remember(connection,request.RequestId,hash,(string)(await read.ExecuteScalarAsync(ct))!,ct);
    }

    // A run's shocks in order, a holdings request's worth to each part (D222). More than a run's parts take is refused.
    public static ImmutableArray<T[]> Parts<T>(ImmutableArray<T> shocks)=>shocks.Length<=RiskJobRun.MaxParts*PortfolioRiskPreview.MaxHoldingsBumps
        ?[..shocks.Chunk(PortfolioRiskPreview.MaxHoldingsBumps)]
        :throw new MarketDataException("risk_definition_targets",$"The definition's books hold more than the {RiskJobRun.MaxParts*PortfolioRiskPreview.MaxHoldingsBumps} targets one run shocks. Choose fewer books.");

    // Run now: queues the held targets the version's preset shocks in its books at an exact retained context, each bumped by the size
    // for its price unit, in parts that are the jobs of one run. A refusal queues nothing.
    public static async Task<JsonElement> Run(NpgsqlConnection connection,PreviewManifest manifest,PortfolioRiskPreview calculator,
        PreviewData data,InputFrame frame,Guid definitionId,RunRiskDefinition request,CancellationToken ct)
    {
        if(request.RequestId==Guid.Empty||request.Context is not {KnowledgeMode:"retained",ContextId:{} contextId})
            throw new MarketDataException("risk_job_context","Run a definition at an exact retained financial context with a stable request identity.");
        var hash=SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{definitionId,request.Version,request.Context}));
        if(await Replay(connection,request.RequestId,hash,ct) is {} prior)return prior;
        var (preset,bookIds,sizes)=await Version(connection,manifest.CatalogId,definitionId,request.Version,ct);
        await RequireBooks(connection,manifest.CatalogId,bookIds,ct);
        var held=await calculator.Held(connection,data,frame,bookIds,preset=="held_curves",ct);
        if(held.IsEmpty)throw new MarketDataException("risk_definition_targets","The definition's books hold nothing its preset shocks at this context.");
        var unsized=held.Where(h=>!sizes.ContainsKey(h.Axis.Unit)).Select(h=>h.Unit).Distinct().ToArray();
        if(unsized.Length>0)throw new MarketDataException("risk_definition_size",
            $"The definition has no shock size for {string.Join(", ",unsized)}. Add a size for each and save a new version.");
        var parts=Parts([..held.Select(h=>(h.Axis.Request,sizes[h.Axis.Unit]))]);
        var runId=Guid.NewGuid();
        await Execute(connection,"INSERT INTO risk.definition_runs VALUES($1,$2,$3,$4,$5)",ct,runId,manifest.CatalogId,definitionId,request.Version,contextId);
        var jobs=new List<object>();
        foreach(var (part,index) in parts.Select((part,index)=>(part,index+1)))
        {
            var job=(JsonObject)await RiskJobs.Submit(connection,manifest,calculator,data,frame,
                new(Guid.NewGuid(),PortfolioRiskPreview.Request(request.Context,bookIds,part),new(runId,index,parts.Length)),ct);
            var jobId=job["id"]!.GetValue<Guid>();
            await Execute(connection,"INSERT INTO risk.definition_run_jobs VALUES($1,$2,$3)",ct,runId,index,jobId);
            jobs.Add(new{part=index,jobId});
        }
        return await Remember(connection,request.RequestId,hash,JsonSerializer.Serialize(new{runId,definitionId,version=request.Version,jobs}),ct);
    }

    // A run as known at a context: its creation and each part's terminal state are known only from their certified receipts (D265).
    private sealed record KnownPart(int Part,string State,DateTimeOffset KnownFrom);
    private sealed record KnownRun(Guid RunId,int Version,DateTimeOffset CreatedKnownFrom,DateTimeOffset ValuationAt,int Parts,ImmutableArray<KnownPart> Finished)
    {
        public bool Completed=>Finished.Length==Parts&&Finished.All(p=>p.State=="completed");
        public ImmutableArray<int> FailedParts=>[..Finished.Where(p=>p.State!="completed").Select(p=>p.Part)];
        public DateTimeOffset KnownAt=>Finished.Select(p=>p.KnownFrom).Append(CreatedKnownFrom).Max();
        // Runs are ordered by valuation time, then by when each became known; never by row order.
        public (DateTimeOffset,DateTimeOffset) Recency=>(ValuationAt,CreatedKnownFrom);
    }

    // The definition's latest completed run in the view: of the runs known at its cutoff whose every part completed by then, the one
    // with the greatest valuation time at or before the view's close. A tie is refused, listing the tied runs; the caller may then choose
    // one of them, and only one of them. The newest later run with a failed or cancelled part is named, and in current knowledge so is a
    // completed run valued after the close. A run valued before the close is said to be (`valuedBefore`), a chosen one too. Each part is read under current authority.
    public static async Task<object> Latest(NpgsqlConnection connection,Guid catalogId,Guid definitionId,TradingView view,Guid? chosen,CancellationToken ct)
    {
        KnownRun[] runs;
        await using(var history=new NpgsqlCommand("SELECT risk.definition_run_history($1,$2,$3)::text",connection))
        {
            history.Parameters.AddWithValue(catalogId);history.Parameters.AddWithValue(definitionId);history.Parameters.AddWithValue(view.Context.ContextId!.Value);
            runs=JsonSerializer.Deserialize<KnownRun[]>((string)(await history.ExecuteScalarAsync(ct))!,RiskJobs.Json)!;
        }
        var definition=await Name(connection,catalogId,definitionId,ct);
        var at=view.Frame.At;
        var eligible=runs.Where(r=>r.ValuationAt<=at).ToArray();
        var completed=eligible.Where(r=>r.Completed).ToArray();
        var newer=view.Working?runs.Where(r=>r.Completed&&r.ValuationAt>at).MaxBy(r=>r.Recency):null;
        var latest=completed.MaxBy(r=>r.ValuationAt);
        var tied=completed.Where(r=>r.ValuationAt==latest?.ValuationAt).OrderBy(r=>r.CreatedKnownFrom).ToArray();
        var shown=chosen is {} runId?(tied.Length>1?tied.SingleOrDefault(r=>r.RunId==runId):null)
                ??throw new MarketDataException("risk_run_choice","Choose one of the completed runs tied for the latest at this context.")
            :tied.Length>1?throw new RiskRunTieException(definition,[..tied.Select(r=>new TiedRiskRun(r.RunId,r.Version,r.ValuationAt,r.KnownAt))])
            :latest;
        var incomplete=eligible.Where(r=>!r.FailedParts.IsEmpty&&(shown is null||r.Recency.CompareTo(shown.Recency)>0)).MaxBy(r=>r.Recency);
        var stated=new{newer=newer is null?null:new RiskRunAt(newer.RunId,newer.ValuationAt),
            incomplete=incomplete is null?null:new{incomplete.RunId,incomplete.ValuationAt,incomplete.FailedParts}};
        if(shown is null)return new{state="none",definition,stated.newer,stated.incomplete};
        await RequireBooks(connection,catalogId,(await Version(connection,catalogId,definitionId,shown.Version,ct)).BookIds,ct);
        var parts=new List<object>();
        foreach(var jobId in await PartJobs(connection,shown.RunId,ct))
            parts.Add(await RiskJobs.Read(connection,catalogId,jobId,ct,expectedConvention:PortfolioRiskPreview.Convention));
        var valuedBefore=shown.ValuationAt<at;
        return new{state=chosen is not null?"chosen":valuedBefore?"earlier_close":newer is not null?"newer_available":"latest",valuedBefore,definition,
            run=new{shown.RunId,shown.Version,shown.ValuationAt,shown.KnownAt,parts},stated.newer,stated.incomplete};
    }

    // The name and latest version of one of the account's definitions.
    private static async Task<RiskDefinitionName> Name(NpgsqlConnection connection,Guid catalogId,Guid definitionId,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("SELECT name,version FROM risk.run_definition_versions WHERE(catalog_id,definition_id)=($1,$2) ORDER BY version DESC LIMIT 1",connection);
        command.Parameters.AddWithValue(catalogId);command.Parameters.AddWithValue(definitionId);
        await using var reader=await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)?new(reader.GetString(0),reader.GetInt32(1))
            :throw new MarketDataException("risk_definition_version","Choose a saved version of one of your definitions.");
    }

    private static async Task<Guid[]> PartJobs(NpgsqlConnection connection,Guid runId,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("SELECT array_agg(job_id ORDER BY part) FROM risk.definition_run_jobs WHERE run_id=$1",connection);
        command.Parameters.AddWithValue(runId);
        return (Guid[])(await command.ExecuteScalarAsync(ct))!;
    }

    // Every book of a version must still be one the account can read at the bound context, under current authority.
    private static async Task RequireBooks(NpgsqlConnection connection,Guid catalogId,ImmutableArray<Guid> bookIds,CancellationToken ct)
    {
        await using var visible=new NpgsqlCommand("SELECT count(*) FROM trading.books WHERE catalog_id=$1 AND id=ANY($2)",connection);
        visible.Parameters.AddWithValue(catalogId);visible.Parameters.AddWithValue(bookIds.ToArray());
        if((long)(await visible.ExecuteScalarAsync(ct))!!=bookIds.Length)throw new PreviewDeniedException();
    }

    // A saved version of one of the account's definitions: its preset, its books in order and its size for each price unit.
    private static async Task<(string Preset,ImmutableArray<Guid> BookIds,ImmutableDictionary<PriceUnit,ExactDecimal> Sizes)> Version(
        NpgsqlConnection connection,Guid catalogId,Guid definitionId,int version,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("""
            SELECT v.preset,(SELECT array_agg(b.book_id ORDER BY b.book_id) FROM risk.run_definition_books b WHERE(b.catalog_id,b.definition_id,b.version)=(v.catalog_id,v.definition_id,v.version)),
             s.reference_catalog_id,s.revision_id,s.currency_code,s.quantity_code,s.multiplier::text,s.size::text
            FROM risk.run_definition_versions v JOIN risk.run_definition_sizes s ON(s.catalog_id,s.definition_id,s.version)=(v.catalog_id,v.definition_id,v.version)
            WHERE(v.catalog_id,v.definition_id,v.version)=($1,$2,$3)
            """,connection);
        command.Parameters.AddWithValue(catalogId);command.Parameters.AddWithValue(definitionId);command.Parameters.AddWithValue(version);
        await using var reader=await command.ExecuteReaderAsync(ct);
        string? preset=null;Guid[] bookIds=[];var sizes=ImmutableDictionary.CreateBuilder<PriceUnit,ExactDecimal>();
        while(await reader.ReadAsync(ct))
        {
            preset=reader.GetString(0);bookIds=reader.GetFieldValue<Guid[]>(1);
            var revision=new ReferenceRevision(reader.GetGuid(2),reader.GetGuid(3));
            sizes.Add(new(new(revision,reader.GetString(4)),new(revision,reader.GetString(5)),ExactDecimal.Parse(reader.GetString(6))),ExactDecimal.Parse(reader.GetString(7)));
        }
        return preset is null?throw new MarketDataException("risk_definition_version","Choose a saved version of one of your definitions.")
            :(preset,[..bookIds],sizes.ToImmutable());
    }

    // Serializes this account's definition commands so a replay and the writes see one state. A request id's first outcome, if it has
    // one; reusing it for another command is a conflict.
    private static async Task<JsonElement?> Replay(NpgsqlConnection connection,Guid requestId,byte[] hash,CancellationToken ct)
    {
        await using(var guard=new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(tenant_id::text||workspace_id::text||principal_id::text||'risk_definition',0)) FROM risk.definition_identity()",connection))
        {
            await using var reader=await guard.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct))throw new PreviewDeniedException();
        }
        await using var prior=new NpgsqlCommand("SELECT body_hash,response::text FROM risk.run_definition_requests WHERE request_id=$1",connection);
        prior.Parameters.AddWithValue(requestId);
        await using var replay=await prior.ExecuteReaderAsync(ct);
        if(!await replay.ReadAsync(ct))return null;
        return hash.AsSpan().SequenceEqual((byte[])replay[0])?Parse(replay.GetString(1))
            :throw new MarketDataException("risk_definition_request_conflict","This request id already belongs to a different definition command. Reload before trying again.");
    }
    private static async Task<JsonElement> Remember(NpgsqlConnection connection,Guid requestId,byte[] hash,string response,CancellationToken ct)
    {
        await Execute(connection,"""
            INSERT INTO risk.run_definition_requests(tenant_id,workspace_id,principal_id,request_id,body_hash,response)
            SELECT tenant_id,workspace_id,principal_id,$1,$2,$3 FROM risk.definition_identity()
            """,ct,requestId,hash,JsonParameter(response));
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
