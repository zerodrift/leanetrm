using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using OpenEtrm.Api.Presentation;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

public sealed record PreviewManifest(string Mode, Guid CatalogId, Guid RevisionId, Guid ReferenceCatalogId,
    Guid ReferenceRevisionId, Guid TenantId, Guid WorkspaceId, PreviewSeedFrame[] Frames,PreviewExample? Example,
    PreviewSequenceCoverage[]? Schedules = null,PreviewSwapMarket[]? SwapMarkets=null, PreviewUnitCatalog? UnitCatalog=null,string IdentityIssuer="https://preview.invalid",string FuturesConvention="fixed_total_cash_execution_pnl_v1");
public sealed record PreviewUnitCatalog(Guid ReferenceRevisionId,Guid DefinitionRevisionId);
public sealed record UnitCatalogData(ReferenceRevision Revision,ImmutableDictionary<string,ImmutableArray<JsonElement>> References);
public enum ReferenceReadScope { DefinitionClosure, AllVisible, TradeValuation }
public enum ObservationReadScope { FactHistory, ExactPins }
public sealed record ReferencePage(string Table,ImmutableArray<JsonElement> Rows,
    ImmutableDictionary<string,ImmutableArray<JsonElement>> References,int Offset,int PageSize,int MaximumOffset,bool HasMore,string Search,
    ReferenceRevision ReferenceRevision,Guid DefinitionRevisionId);
public sealed record PreviewSwapMarket(Guid RevisionId,PreviewSeedFrame[] Frames);
public sealed record PreviewSequenceCoverage(string Curve, DateTimeOffset From, DateTimeOffset To, DateTimeOffset ExampleAt);
public sealed record PreviewExample(DateTimeOffset At,string Curve);
public sealed record PreviewSeedFrame(DateTimeOffset At, Guid[] ObservationIds, Dictionary<string,Guid[]> CurveContracts);
public sealed record InputFrame(Guid Id, string Name, DateTimeOffset At, ImmutableArray<Guid> ObservationIds,
    ImmutableDictionary<string,ImmutableArray<Guid>> CurveContracts,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? ContextId=null,
    [property:System.Text.Json.Serialization.JsonIgnore] ImmutableArray<Guid> FrozenObservationIds=default)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? OriginCloseId{get;init;}
    // Pins explicitly selected in separate swap bundles, by bundle revision (D172).
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<Guid,ImmutableArray<Guid>>? SwapMarketSelections{get;init;}
    // Set only on frames a certified context resolves under its explicit latest-correction policy (D172).
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ResolvesLatestCorrection{get;init;}
    public InputFrame SelectRevision(Guid previous,Guid selected,string name)=>this with
    {
        Id=Guid.NewGuid(),Name=name,OriginCloseId=null,ObservationIds=ObservationIds.Replace(previous,selected),
        FrozenObservationIds=FrozenObservationIds.IsDefault?default:FrozenObservationIds.Contains(previous)?FrozenObservationIds.Replace(previous,selected):FrozenObservationIds
    };
    public InputFrame SelectSwapMarketRevision(Guid market,ImmutableArray<Guid> pins,Guid previous,Guid selected,string name)=>this with
    {
        Id=Guid.NewGuid(),Name=name,OriginCloseId=null,
        SwapMarketSelections=(SwapMarketSelections??ImmutableDictionary<Guid,ImmutableArray<Guid>>.Empty).SetItem(market,pins.Replace(previous,selected))
    };
}
// How a request binds its context: shared, like every request, or exclusive, taking the epoch row before binding so
// a control-table write cannot deadlock against requests already admitted (docs/ai-assistant.md §5.4).
public enum Admission { Shared, Administration, Agents, Grants }
public sealed class PreviewDeniedException : Exception;

public sealed class PreviewDatabase : IAsyncDisposable
{
    private readonly NpgsqlDataSource? authenticator;
    private readonly NpgsqlDataSource runtime;
    private readonly NpgsqlDataSource? calculationRuntime;
    // The page assistant's reads have pools of their own on the same logins, never the pages' (docs/ai-assistant.md §7.4).
    private readonly NpgsqlDataSource? agentAuthenticator,agentRuntime;
    private const string AgentReads="openetrm-agent-reads";
    private readonly bool ownsSources=true;
    private readonly string runtimeLogin;
    private readonly Func<string>? hostedSession;
    public PreviewManifest Manifest { get; }
    public OpenEtrm.MarketData.Calculation.ConnectedCalculationCache Calculations {get;}=new(16,250_000);
    internal NpgsqlConnection CalculationListener()=>calculationRuntime?.CreateConnection()
        ??throw new InvalidOperationException("This database adapter has no calculation listener.");
    public const int ReferenceMaximumOffset=100000;
    // Each reference and definition table with its primary key within a revision. Every read returns
    // rows in key order, so identical pins give identical rows in the same order whatever plan the database uses.
    private static readonly (string Table,string Key)[] ReferenceKeys = [("currencies","code"), ("dimensions","code"), ("units","code"),
        ("unit_conversions","from_unit_code,to_unit_code"), ("commodities","code"), ("measurement_bases","code"), ("quantity_specifications","code"),
        ("quote_specifications","code"), ("value_specifications","code"), ("locations","code"), ("clock_rules","code"), ("calendars","code"),
        ("calendar_days","calendar_code,business_date"), ("commodity_units","commodity_code,unit_code"), ("location_types","code"),
        ("commodity_locations","commodity_code,location_code"), ("exchanges","code"), ("clearing_houses","code"),
        ("exchange_clearing_houses","exchange_code,clearing_house_code"), ("interest_rate_definitions","code"), ("price_index_definitions","code"),
        ("sources","code"), ("product_evidence","product_code,source_code,fact_group"), ("delivery_profiles","code"), ("settlement_targets","code"),
        ("cash_rounding_conventions","code"), ("futures_products","code"), ("futures_series","code"),
        ("futures_execution_policies","series_code,implementation"), ("futures_milestones","series_code,milestone"),
        ("futures_lot_resolutions","series_code"), ("futures_product_terms","product_code"), ("futures_basis_settlements","series_code")];
    public static readonly string[] ReferenceTables = [..ReferenceKeys.Select(r=>r.Table)];
    private static readonly (string Table,string Key)[] DefinitionTables = [("revisions","id"), ("contracts","id"), ("curves","code"), ("series","id"),
        ("curve_members","curve_code,contract_id"), ("curve_inputs","curve_code,slot"), ("constituent_mappings","curve_code,slot,target_contract_id"),
        ("rate_curves","code"), ("rate_curve_points","curve_code,maturity"), ("revision_periods","revision_id"),
        ("index_fixing_observations","index_code,observation_from,observation_to"), ("contract_intervals","contract_id"),
        ("curve_blocks","curve_code"), ("shaped_curves","code"), ("shaped_curve_blocks","shaped_curve_code,block_curve_code")];
    private static string ReferenceKey(string table)=>ReferenceKeys.Single(r=>r.Table==table).Key;
    internal static readonly ImmutableArray<string> UnitTables = ["dimensions", "units", "unit_conversions", "commodities", "measurement_bases", "quantity_specifications"];

    // The scheduler's application adapter receives an already-qualified runtime
    // source. It has no authenticator capability or HTTP host construction.
    internal PreviewDatabase(PreviewManifest manifest,NpgsqlDataSource runtime)
    {
        Manifest=manifest;this.runtime=runtime;runtimeLogin="";ownsSources=false;
    }

    public PreviewDatabase(string root,string manifestName="manifest.json", HostedConfiguration? hosted=null,Func<string>? hostedSession=null)
    {
        if(manifestName is not ("manifest.json" or "exchange-manifest.json"))throw new InvalidOperationException("Choose a configured preview manifest.");
        Manifest = JsonSerializer.Deserialize<PreviewManifest>(File.ReadAllText(Path.Combine(root, manifestName)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Missing preview manifest.");
        if (Manifest.Mode != (hosted is null ? "local-synthetic-preview" : "hosted-pilot")) throw new InvalidOperationException("Manifest does not match the explicitly selected host mode.");
        if (hosted is not null && Manifest.IdentityIssuer != hosted.IdentityIssuer) throw new InvalidOperationException("The exact hosted identity issuer must match the manifest.");
        if (hosted is null)
        {
            authenticator = PreviewSource(root, "OPENETRM_PREVIEW_AUTHENTICATOR", "preview_authenticator", 8);
            runtime = PreviewSource(root, "OPENETRM_PREVIEW_RUNTIME", "preview_runtime", 8);
            calculationRuntime=PreviewSource(root,"OPENETRM_PREVIEW_RUNTIME","preview_runtime",null,"openetrm-calculation-listener");
            agentAuthenticator = PreviewSource(root, "OPENETRM_PREVIEW_AUTHENTICATOR", "preview_authenticator", RuntimeSet.AgentPool, AgentReads);
            agentRuntime = PreviewSource(root, "OPENETRM_PREVIEW_RUNTIME", "preview_runtime", RuntimeSet.AgentPool, AgentReads);
            runtimeLogin = "preview_runtime";
        }
        else
        {
            this.hostedSession=hostedSession??throw new InvalidOperationException("Hosted capabilities require a verified server session.");
            var auth = HostedConfiguration.Connection(hosted.AuthenticatorConnectionFile);
            var app = HostedConfiguration.Connection(hosted.RuntimeConnectionFile);
            if(auth.Username==app.Username || auth.Database!=app.Database || auth.Host!=app.Host || auth.Port!=app.Port)
                throw new InvalidOperationException("Hosted runtime and authenticator require separate logins to the same dedicated database.");
            authenticator=NpgsqlDataSource.Create(auth.ConnectionString); runtime=NpgsqlDataSource.Create(app.ConnectionString);
            calculationRuntime=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(app.ConnectionString){Pooling=false,ApplicationName="openetrm-calculation-listener"}.ConnectionString);
            agentAuthenticator=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(auth.ConnectionString){MaxPoolSize=RuntimeSet.AgentPool,ApplicationName=AgentReads}.ConnectionString);
            agentRuntime=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(app.ConnectionString){MaxPoolSize=RuntimeSet.AgentPool,ApplicationName=AgentReads}.ConnectionString);
            runtimeLogin=app.Username!;
        }
    }

    // A pool of the given size, or unpooled connections without one, named for the server when an application is given.
    private static NpgsqlDataSource PreviewSource(string root, string variable, string login, int? pool, string? application=null)
    {
        var config = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidOperationException($"Missing {variable}."));
        if (Path.GetFullPath(config.Host!) != root || config.Username != login || config.Database != "postgres")
            throw new InvalidOperationException("Preview connections must use its private owned socket and dedicated test login.");
        // JSON timestamp transport follows the API UTC contract, independently
        // of the database host, process environment or display preference.
        config.Timezone = "UTC";
        config.CommandTimeout = 5; config.Timeout = 5;
        if (pool is {} size) config.MaxPoolSize = size; else config.Pooling = false;
        if (application is not null) config.ApplicationName = application;
        return NpgsqlDataSource.Create(config.ConnectionString);
    }

    // The agent worker connects on the runtime login outside the request pools (docs/ai-assistant.md §7.4, §11). The
    // connection that holds its lock is not pooled, so closing it ends its session and releases the lock; its writes,
    // which hold no session state, have a pool of their own.
    public static (NpgsqlDataSource Holder, NpgsqlDataSource Writes) WorkerSources(string root, HostedConfiguration? hosted)
    {
        if (hosted is null)
            return (PreviewSource(root, "OPENETRM_PREVIEW_RUNTIME", "preview_runtime", null), PreviewSource(root, "OPENETRM_PREVIEW_RUNTIME", "preview_runtime", 8));
        var app = HostedConfiguration.Connection(hosted.RuntimeConnectionFile).ConnectionString;
        return (NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(app) { Pooling = false }.ConnectionString),
            NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(app) { MaxPoolSize = 8 }.ConnectionString));
    }

    public async Task<T> WithIdentity<T>(string profile, string? permission, Func<NpgsqlConnection, CancellationToken, Task<T>> operation, CancellationToken ct, Admission admission=Admission.Shared)
    {
        if(authenticator is null)throw new InvalidOperationException("This direct reader has no identity-issuing capability.");
        if (string.IsNullOrWhiteSpace(profile) || profile.Length>48) throw new PreviewDeniedException();
        await using var issue = authenticator.CreateCommand(hostedSession is null?"SELECT security.issue_context($1,$2,$3,$4,$5,NULL,$6::name,60)":"SELECT security.issue_hosted_context($1,$2::name)");
        foreach (var value in hostedSession is null?new object[] { Manifest.IdentityIssuer, profile, "human_interactive", Manifest.TenantId, Manifest.WorkspaceId, runtimeLogin }:new object[]{hostedSession(),runtimeLogin})
            issue.Parameters.Add(new NpgsqlParameter { Value = value });
        Guid token;
        // A hosted request whose sign-in session was revoked or ended since it was authenticated (docs/ai-assistant.md §11).
        try { token = (Guid)(await issue.ExecuteScalarAsync(ct) ?? throw new PreviewDeniedException()); }
        catch (PostgresException e) when (hostedSession is not null && e.SqlState == "42501") { throw new PreviewSessionEnded(); }
        return await Bound(runtime, token, hostedSession is not null, admission switch{Admission.Administration=>"SELECT security.use_administration_context($1)",
            Admission.Agents=>"SELECT assistant.use_agent_admission($1)",Admission.Grants=>"SELECT assistant.use_grant_admission($1)",
            _=>"SELECT security.use_context($1)"}, async(connection,ct)=>
        {
            await Hold(connection,permission,ct);
            var result = await operation(connection, ct);
            await Hold(connection,permission,ct);
            return result;
        }, ct);
    }

    // A read in a request context issued to a turn's agent under the turn's grant (docs/ai-assistant.md §7.1, §7.4), on the
    // agent pools: `boundAt` receives the instant on the database clock once the context is bound, then each permission the
    // tool needs is checked before and after the read. The check after it is defense in depth: the bind holds the epoch
    // row FOR SHARE until the read's transaction ends, so no revocation can commit during the read, and a grant or role
    // grant whose term ends during it, the one change it can see, also fails the step's publication recheck.
    public async Task<T> WithAgent<T>(AgentIdentity agent, ImmutableArray<string> permissions, Action<DateTimeOffset> boundAt,
        Func<NpgsqlConnection, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        if(agentAuthenticator is null||agentRuntime is null)throw new InvalidOperationException("This database adapter has no agent pools.");
        await using var issue = agentAuthenticator.CreateCommand("SELECT security.issue_context($1,$2,'agent_credential',$3,$4,$5,$6::name,60)");
        foreach (var value in new object[] { Manifest.IdentityIssuer, agent.Principal, Manifest.TenantId, Manifest.WorkspaceId, agent.Delegation, runtimeLogin })
            issue.Parameters.Add(new NpgsqlParameter { Value = value });
        var token = (Guid)(await issue.ExecuteScalarAsync(ct) ?? throw new PreviewDeniedException());
        return await Bound(agentRuntime, token, false, "SELECT security.use_context($1)", async(connection,ct)=>
        {
            await using(var now = new NpgsqlCommand("SELECT clock_timestamp()", connection))
            await using(var reader = await now.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct);
                boundAt(reader.GetFieldValue<DateTimeOffset>(0));
            }
            foreach(var permission in permissions)await Hold(connection,permission,ct);
            var result = await read(connection, ct);
            foreach(var permission in permissions)await Hold(connection,permission,ct);
            return result;
        }, ct);
    }

    // Runs an operation in one READ COMMITTED transaction bound to an issued request context, which a hosted request
    // also holds to its verified sign-in session before and after the operation. An agent's context has no sign-in
    // session: revoking its delegator's cancels the turn instead (docs/ai-assistant.md §11).
    private async Task<T> Bound<T>(NpgsqlDataSource source, Guid token, bool signedIn, string bind, Func<NpgsqlConnection, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var command = new NpgsqlCommand(bind, connection, transaction))
        { command.Parameters.AddWithValue(token); await command.ExecuteNonQueryAsync(ct); }
        async Task Session()
        {
            if(!signedIn)return;
            await using var check=new NpgsqlCommand("SELECT security.require_hosted_session()",connection,transaction);
            try{await check.ExecuteNonQueryAsync(ct);}
            catch(PostgresException e) when(e.SqlState=="42501"){throw new PreviewSessionEnded();}
        }
        await Session();
        var result = await operation(connection, ct);
        await Session();
        await transaction.CommitAsync(ct);
        return result;
    }

    // The bound request context holds a permission, or the operation is denied.
    private static async Task Hold(NpgsqlConnection connection, string? permission, CancellationToken ct)
    {
        if(permission is not null && !await HasPermission(connection,permission,ct)) throw new PreviewDeniedException();
    }

    // Whether the request context bound to this connection currently holds a permission.
    public static async Task<bool> HasPermission(NpgsqlConnection connection,string permission,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.has_permission($1)",permission);
        return Equals(await command.ExecuteScalarAsync(ct),true);
    }

    public async Task ValidateHostedBoundary(CancellationToken ct)
    {
        if(authenticator is null)throw new InvalidOperationException("This direct reader has no authenticator capability.");
        foreach(var entry in new[]{(Source:runtime,Role:"openetrm_runtime",Other:"openetrm_authenticator"),(Source:authenticator,Role:"openetrm_authenticator",Other:"openetrm_runtime")})
        {
            await using var command=entry.Source.CreateCommand("""
SELECT NOT(rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole) AND pg_has_role(current_user,$1,'MEMBER') AND NOT pg_has_role(current_user,$2,'MEMBER') AND NOT pg_has_role(current_user,'openetrm_security_owner','MEMBER') AND current_setting('track_commit_timestamp')='on'
 AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname NOT LIKE 'pg_%' AND n.nspname<>'information_schema' AND pg_has_role(current_user,c.relowner,'MEMBER'))
 AND NOT EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspname NOT LIKE 'pg_%' AND n.nspname<>'information_schema' AND pg_has_role(current_user,n.nspowner,'MEMBER'))
 AND NOT EXISTS(SELECT 1 FROM pg_database d WHERE d.datname=current_database() AND pg_has_role(current_user,d.datdba,'MEMBER')) FROM pg_roles WHERE rolname=current_user
""");
            command.Parameters.AddWithValue(entry.Role);command.Parameters.AddWithValue(entry.Other);
            if(!Equals(await command.ExecuteScalarAsync(ct),true))throw new InvalidOperationException("Hosted database roles or commit tracking do not meet the installation boundary.");
        }
    }

    public async Task<PreviewData> Read(NpgsqlConnection connection, CancellationToken ct,Guid? selectedRevision=null,PreviewData? sharedReferences=null,ImmutableArray<Guid>? observationIds=null,
        ReferenceReadScope referenceScope=ReferenceReadScope.DefinitionClosure,bool includeCalendarDays=true,
        ObservationReadScope observationScope=ObservationReadScope.FactHistory)
    {
        if(selectedRevision is {} requested&&!DeclaredBundle(requested))throw new PreviewDeniedException();
        var selectedManifest=selectedRevision is {} id?Manifest with {RevisionId=id}:Manifest;
        var definitions = new Dictionary<string, ImmutableArray<JsonElement>>();
        foreach (var (table,key) in DefinitionTables)
        {
            var revisionColumn = table == "revisions" ? "id" : "revision_id";
            definitions[table] = await Rows(connection, $"SELECT row_to_json(t)::text FROM (SELECT * FROM market_data.{table} WHERE catalog_id=$1 AND {revisionColumn}=$2 ORDER BY {key}) t", [selectedManifest.CatalogId, selectedManifest.RevisionId], ct);
        }
        if (definitions["revisions"].Length != 1 || definitions["revisions"][0].Text("state") != "sealed") throw new PreviewDeniedException();
        PreviewData.RequireBundlePin(selectedManifest, definitions["revisions"][0]);
        IReadOnlyDictionary<string,ImmutableArray<JsonElement>> references;
        if(sharedReferences is not null && (sharedReferences.ReferenceRevision!=new ReferenceRevision(selectedManifest.ReferenceCatalogId,selectedManifest.ReferenceRevisionId)||sharedReferences.DefinitionRevision.CatalogId!=selectedManifest.CatalogId))
            throw new MarketDataException("reference_pin_mismatch","Shared reference rows require the same exact authorized catalog and revision.");
        async Task RequireSharedReferenceAccess()
        {
            if(sharedReferences is null)return;
            await using var access=ScheduleStore.Command(connection,"SELECT security.reference_access($1,'ReadMarketData') AND security.reference_version_visible($1,$2)",selectedManifest.ReferenceCatalogId,selectedManifest.ReferenceRevisionId);
            if(!Equals(await access.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        }
        await RequireSharedReferenceAccess();
        references=await ReadReferences(connection,selectedManifest,definitions,referenceScope,includeCalendarDays,sharedReferences,ct);
        var observations = await (observationScope switch
        {
            ObservationReadScope.ExactPins => ReadObservationPins(connection,selectedManifest,
                observationIds??throw new ArgumentException("Exact observation reads require explicit pins.",nameof(observationIds)),ct),
            ObservationReadScope.FactHistory when observationIds is {} pins => ReadObservationFacts(connection,selectedManifest,pins,ct),
            ObservationReadScope.FactHistory => Rows(connection, "SELECT row_to_json(t)::text FROM (SELECT * FROM market_data.observations WHERE catalog_id=$1 AND definition_revision_id=$2 ORDER BY observed_at,received_at,id) t", [selectedManifest.CatalogId, selectedManifest.RevisionId], ct),
            _ => throw new ArgumentOutOfRangeException(nameof(observationScope))
        });
        await RequireSharedReferenceAccess();
        return new PreviewData(selectedManifest, definitions, references, observations,Calculations){CalendarDaysRead=includeCalendarDays};
    }

    private static async Task<IReadOnlyDictionary<string,ImmutableArray<JsonElement>>> ReadReferences(NpgsqlConnection connection,
        PreviewManifest manifest,IReadOnlyDictionary<string,ImmutableArray<JsonElement>> definitions,
        ReferenceReadScope scope,bool includeCalendarDays,PreviewData? sharedReferences,CancellationToken ct)
    {
        var loaded=new Dictionary<string,ImmutableArray<JsonElement>>();
        var bounded=new HashSet<string>(["futures_series","futures_products","futures_milestones","futures_lot_resolutions",
            "futures_product_terms","futures_basis_settlements","futures_execution_policies","product_evidence","locations","commodity_locations","calendar_days"]);
        async Task<ImmutableArray<JsonElement>> ReadTable(string table,string? column=null,IEnumerable<string>? codes=null)
        {
            // These immutable tables are complete in every read scope. Reuse
            // stays within this authorized operation; bounded tables always
            // resolve against this definition's own explicit relationships.
            if(column is null&&!bounded.Contains(table)&&sharedReferences is not null)
                return sharedReferences.References[table];
            var parameters=new object[]{manifest.ReferenceCatalogId,manifest.ReferenceRevisionId};
            var predicate="";
            if(column is not null)
            {
                var selected=codes!.Distinct().ToArray();
                if(selected.Length==0)return ImmutableArray<JsonElement>.Empty;
                predicate=$" AND {column}=ANY($3::text[])";
                parameters=[..parameters,selected];
            }
            if(scope==ReferenceReadScope.TradeValuation&&table=="futures_milestones")
                predicate+=" AND milestone IN ('first_trading','last_trading')";
            return await Rows(connection,$"SELECT row_to_json(t)::text FROM(SELECT * FROM ref_data.{table} WHERE catalog_id=$1 AND revision_id=$2{predicate} ORDER BY {ReferenceKey(table)})t",parameters,ct);
        }
        if(scope==ReferenceReadScope.AllVisible)
        {
            foreach(var table in ReferenceTables)loaded[table]=await ReadTable(table);
            return loaded;
        }
        foreach(var table in ReferenceTables.Where(t=>!bounded.Contains(t)))loaded[table]=await ReadTable(table);

        // Follow only exact-version declared settlement links. These metadata
        // references do not introduce market dependencies or pricing inputs.
        var pending=Codes(definitions["contracts"],"futures_series_code").ToHashSet();
        var seen=new HashSet<string>();
        var series=ImmutableArray.CreateBuilder<JsonElement>();
        var basis=ImmutableArray.CreateBuilder<JsonElement>();
        while(pending.Count>0)
        {
            var batch=pending.ToArray();seen.UnionWith(batch);
            series.AddRange(await ReadTable("futures_series","code",batch));
            var links=await ReadTable("futures_basis_settlements","series_code",batch);
            basis.AddRange(links);
            pending=Codes(links,"benchmark_series_code").Where(code=>!seen.Contains(code)).ToHashSet();
        }
        loaded["futures_series"]=series.ToImmutable();
        loaded["futures_basis_settlements"]=basis.ToImmutable();
        var products=Codes(loaded["futures_series"],"product_code").ToArray();
        loaded["futures_products"]=await ReadTable("futures_products","code",products);
        foreach(var table in new[]{"futures_product_terms","product_evidence"})loaded[table]=await ReadTable(table,"product_code",products);
        foreach(var table in new[]{"futures_milestones","futures_lot_resolutions","futures_execution_policies"})loaded[table]=await ReadTable(table,"series_code",seen);
        var locations=Codes(definitions["curves"],"location_code")
            .Concat(Codes(loaded["futures_products"],"location_code"))
            .Concat(Codes(loaded["price_index_definitions"],"location_code")).Distinct().ToArray();
        loaded["locations"]=await ReadTable("locations","code",locations);
        loaded["commodity_locations"]=await ReadTable("commodity_locations","location_code",locations);
        loaded["calendar_days"]=includeCalendarDays?await ReadTable("calendar_days"):ImmutableArray<JsonElement>.Empty;
        return loaded;
    }

    private static IEnumerable<string> Codes(IEnumerable<JsonElement> rows,string column)=>rows
        .Where(row=>row.TryGetProperty(column,out var value)&&value.ValueKind==JsonValueKind.String)
        .Select(row=>row.GetProperty(column).GetString()!).Distinct();

    public async Task<ReferencePage> ReadReferencePage(NpgsqlConnection connection,string table,int offset,string? search,CancellationToken ct)
    {
        search??="";
        if(!ReferenceTables.Contains(table)||offset is <0 or >ReferenceMaximumOffset||search.Length>160)
            throw new MarketDataException("reference_page",$"Choose a supported reference table, an offset from 0 to {ReferenceMaximumOffset} and search text up to 160 characters.");
        var revision=new ReferenceRevision(Manifest.ReferenceCatalogId,Manifest.ReferenceRevisionId);
        var definition=Manifest.RevisionId;
        if(UnitTables.Contains(table)&&Manifest.UnitCatalog is {} configured)
        {
            revision=new(Manifest.ReferenceCatalogId,configured.ReferenceRevisionId);
            definition=configured.DefinitionRevisionId;
        }
        async Task RequireReferenceAccess()
        {
            await using var access=ScheduleStore.Command(connection,"SELECT security.reference_access($1,'ReadMarketData')",revision.CatalogId);
            if(!Equals(await access.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        }
        await RequireReferenceAccess();
        var visible=await Rows(connection,"SELECT row_to_json(t)::text FROM(SELECT reference_catalog_id,reference_revision_id FROM market_data.revisions WHERE catalog_id=$1 AND id=$2 AND state='sealed')t",[Manifest.CatalogId,definition],ct);
        if(visible.Length!=1||visible[0].Id("reference_catalog_id")!=revision.CatalogId||visible[0].Id("reference_revision_id")!=revision.RevisionId)
            throw new MarketDataException(UnitTables.Contains(table)?"unit_catalog":"reference_catalog","The exact reference catalog is unavailable in this context.");
        const int pageSize=200;
        // Table and key identifiers come only from the fixed public catalog.
        // Search is literal, including SQL wildcard characters, and never runs
        // a full-result count before returning the bounded selected page.
        var found=await Rows(connection,$"""
            SELECT row_to_json(t)::text FROM (
                SELECT r.* FROM ref_data.{table} r
                WHERE catalog_id=$1 AND revision_id=$2
                  AND ($3='' OR strpos(lower(row_to_json(r)::text),lower($3))>0)
                ORDER BY {ReferenceKey(table)} LIMIT 201 OFFSET $4
            ) t
            """,[revision.CatalogId,revision.RevisionId,search,offset],ct);
        var rows=found.Take(pageSize).ToImmutableArray();
        var references=ImmutableDictionary.CreateBuilder<string,ImmutableArray<JsonElement>>();
        references.Add(table,rows);
        // Names for inspector cells are exact-version linked rows, not labels
        // borrowed from the business catalog or a preceding page/context.
        foreach(var (target,columns) in ReferenceLabels)
        {
            var codes=columns.SelectMany(column=>Codes(rows,column)).Distinct().ToArray();
            if(codes.Length==0)continue;
            var linked=await Rows(connection,$"SELECT row_to_json(t)::text FROM(SELECT * FROM ref_data.{target} WHERE catalog_id=$1 AND revision_id=$2 AND code=ANY($3::text[]) ORDER BY {ReferenceKey(target)})t",[revision.CatalogId,revision.RevisionId,codes],ct);
            references[target]=references.TryGetValue(target,out var existing)
                ? existing.Concat(linked).DistinctBy(row=>row.GetProperty("code").GetString()).ToImmutableArray():linked;
        }
        await RequireReferenceAccess();
        return new(table,rows,references.ToImmutable(),offset,pageSize,ReferenceMaximumOffset,found.Length>pageSize,search,revision,definition);
    }

    private static readonly (string Table,string[] Columns)[] ReferenceLabels=
    [
        ("commodities",["commodity_code","fuel_commodity_code","output_commodity_code"]),
        ("currencies",["currency_code","quote_currency_code"]),
        ("units",["unit_code","lot_unit_code","from_unit_code","to_unit_code","quote_unit_code","fuel_unit_code","output_unit_code"]),
        ("locations",["location_code"]),("location_types",["type_code"]),
        ("exchanges",["exchange_code"]),("clearing_houses",["clearing_house_code"]),
        ("sources",["source_code","qualification_source_code"]),("futures_products",["product_code"]),("futures_series",["series_code"]),
        ("clock_rules",["clock_code","market_clock_code"]),("calendars",["calendar_code","trading_calendar_code"]),
        ("quantity_specifications",["quantity_code","numerator_quantity_code","denominator_quantity_code"]),
        ("measurement_bases",["basis_code"]),("price_index_definitions",["index_a_code","index_b_code"])
    ];

    private static Task<ImmutableArray<JsonElement>> ReadObservationPins(NpgsqlConnection connection,PreviewManifest manifest,ImmutableArray<Guid> pins,CancellationToken ct)
    {
        if(pins.IsEmpty)return Task.FromResult(ImmutableArray<JsonElement>.Empty);
        return Rows(connection,"""
            SELECT row_to_json(t)::text FROM (
                SELECT * FROM market_data.observations
                WHERE catalog_id=$1 AND definition_revision_id=$2 AND id=ANY($3::uuid[])
                ORDER BY observed_at,received_at,id
            ) t
            """,[manifest.CatalogId,manifest.RevisionId,pins.ToArray()],ct);
    }

    private static Task<ImmutableArray<JsonElement>> ReadObservationFacts(NpgsqlConnection connection,PreviewManifest manifest,ImmutableArray<Guid> pins,CancellationToken ct)
    {
        if(pins.IsEmpty)return Task.FromResult(ImmutableArray<JsonElement>.Empty);
        // Both the anchor pins and their same-fact versions pass the bound RLS
        // and historical context. This does not select a newer pricing input.
        return Rows(connection,"""
            WITH pins AS MATERIALIZED (
                SELECT DISTINCT series_id,source_code,source_event_id,observed_at,role
                FROM market_data.observations
                WHERE catalog_id=$1 AND definition_revision_id=$2 AND id=ANY($3::uuid[])
            )
            SELECT row_to_json(t)::text FROM (
                SELECT o.* FROM market_data.observations o
                WHERE o.catalog_id=$1 AND o.definition_revision_id=$2
                  AND EXISTS(SELECT 1 FROM pins p WHERE
                    (p.series_id,p.source_code,p.source_event_id,p.observed_at,p.role)=
                    (o.series_id,o.source_code,o.source_event_id,o.observed_at,o.role))
                ORDER BY o.observed_at,o.received_at,o.id
            ) t
            """,[manifest.CatalogId,manifest.RevisionId,pins.ToArray()],ct);
    }

    public async Task<UnitCatalogData> ReadUnitCatalog(NpgsqlConnection connection,PreviewData selected,CancellationToken ct)
    {
        if(Manifest.UnitCatalog is not {} configured)
            return new(selected.ReferenceRevision,UnitTables.ToImmutableDictionary(table=>table,table=>selected.References[table]));
        var revision=new ReferenceRevision(Manifest.ReferenceCatalogId,configured.ReferenceRevisionId);
        // This exact published bundle must be visible under the same bound
        // effective/knowledge context. Never substitute the current unit version.
        var visible=await Rows(connection,"SELECT row_to_json(t)::text FROM(SELECT reference_catalog_id,reference_revision_id FROM market_data.revisions WHERE catalog_id=$1 AND id=$2 AND state='sealed')t",[Manifest.CatalogId,configured.DefinitionRevisionId],ct);
        if(visible.Length!=1 || visible[0].Id("reference_catalog_id")!=revision.CatalogId || visible[0].Id("reference_revision_id")!=revision.RevisionId)
            throw new MarketDataException("unit_catalog","The exact configured unit catalog is unavailable in this context.");
        var references=ImmutableDictionary.CreateBuilder<string,ImmutableArray<JsonElement>>();
        foreach(var table in UnitTables)
            references.Add(table,await Rows(connection,$"SELECT row_to_json(t)::text FROM(SELECT * FROM ref_data.{table} WHERE catalog_id=$1 AND revision_id=$2 ORDER BY {ReferenceKey(table)})t",[revision.CatalogId,revision.RevisionId],ct));
        return new(revision,references.ToImmutable());
    }

    public async Task<ImmutableArray<(PreviewData Data,InputFrame Frame)>> ReadSwapMarkets(NpgsqlConnection connection,InputFrame selected,CancellationToken ct,PreviewData? sharedReferences=null,
        IReadOnlySet<Guid>? requiredRevisions=null)
    {
        var result=ImmutableArray.CreateBuilder<(PreviewData,InputFrame)>();
        foreach(var market in Manifest.SwapMarkets??[])
        {
            if(requiredRevisions is not null&&!requiredRevisions.Contains(market.RevisionId))continue;
            if(!await SwapMarketAvailable(connection,market,ct))continue;
            var seed=market.Frames.SingleOrDefault(f=>f.At==selected.At)??throw new MarketDataException("swap_context","No explicit daily-market frame exists for the selected As of time.");
            var pins=SwapMarketPins(selected,market)!.Value;
            var data=await Read(connection,ct,market.RevisionId,sharedReferences,pins);
            var frame=new InputFrame(selected.Id,"Selected swap market",selected.At,pins,seed.CurveContracts.ToImmutableDictionary(p=>p.Key,p=>p.Value.ToImmutableArray()),selected.ContextId);
            // A snapshot prices its exact pins; only a certified context's own frames apply its
            // latest-correction policy at that cutoff (D172).
            result.Add((data,selected.ResolvesLatestCorrection?data.SelectPinned(frame):frame));
        }
        return result.ToImmutable();
    }

    public async Task<bool> SwapMarketAvailable(NpgsqlConnection connection,PreviewSwapMarket market,CancellationToken ct)
    {
        await using var available=ScheduleStore.Command(connection,"SELECT count(*) FROM market_data.revisions WHERE catalog_id=$1 AND id=$2",Manifest.CatalogId,market.RevisionId);
        return (long)(await available.ExecuteScalarAsync(ct))!>0;
    }

    // A separate swap bundle uses the pins its snapshot selected, otherwise the bundle
    // frame declared for the snapshot's instant; null when neither exists (D172).
    public ImmutableArray<Guid>? SwapMarketPins(InputFrame frame,PreviewSwapMarket market)=>
        frame.SwapMarketSelections is {} selections&&selections.TryGetValue(market.RevisionId,out var pins)?pins:
        market.Frames.SingleOrDefault(f=>f.At==frame.At)?.ObservationIds.ToImmutableArray();

    // The main market revision or an explicitly allow-listed supplemental swap bundle.
    private bool DeclaredBundle(Guid revision)=>revision==Manifest.RevisionId||(Manifest.SwapMarkets??[]).Any(m=>m.RevisionId==revision);

    public async Task<Guid> Correct(NpgsqlConnection connection, Guid revision, Guid observationId, string value, CancellationToken ct)
    {
        if (!DeclaredBundle(revision)) throw new PreviewDeniedException();
        var parsed = ExactDecimal.Parse(value);
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            INSERT INTO market_data.observations
              (catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,
               source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value,corrects_id)
            SELECT catalog_id,$1,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,
               source_event_id,$2,role,publication_state,observed_at,clock_timestamp(),$3::numeric,id
            FROM market_data.observations WHERE catalog_id=$4 AND definition_revision_id=$5 AND id=$6
            """, connection);
        foreach (var parameter in new object[] { id, id.ToString(), parsed.ToString(), Manifest.CatalogId, revision, observationId })
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new MarketDataException("missing_observation", "The selected observation is unavailable.");
        return id;
    }

    internal static async Task<ImmutableArray<JsonElement>> Rows(NpgsqlConnection connection, string sql, object[] parameters, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in parameters) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = ImmutableArray.CreateBuilder<JsonElement>();
        while (await reader.ReadAsync(ct))rows.Add(ReferenceJson.PreserveNumericLexemes(reader.GetString(0)));
        return rows.ToImmutable();
    }
    public async ValueTask DisposeAsync()
    {
        if(!ownsSources)return;
        foreach(var source in new[]{runtime,authenticator,agentRuntime,agentAuthenticator,calculationRuntime})if(source is not null)await source.DisposeAsync();
    }
}

public sealed class PreviewData
{
    private readonly PreviewManifest manifest;
    private readonly Dictionary<(string Table,string Field),ILookup<string,JsonElement>> referenceRows,definitionRows;
    private readonly ILookup<Guid,JsonElement> observationRows,contractDefinitions;
    private readonly ILookup<Guid?,JsonElement> correctionRows;
    private readonly ILookup<Guid,MarketContract> contractRows;
    private readonly ILookup<string,QuoteSpecification> quoteRows;
    private readonly HashSet<(string Curve,Guid Contract)> curveMembers;
    public ImmutableDictionary<string, ImmutableArray<JsonElement>> Definitions { get; }
    public ImmutableDictionary<string, ImmutableArray<JsonElement>> References { get; }
    public FuturesExecutionPolicies ExecutionPolicies { get; }
    public string FuturesConvention => manifest.FuturesConvention;
    public ImmutableArray<JsonElement> Observations { get; }
    public CurveModel Model { get; }
    // Whether this read loaded calendar days; without them no holiday is known, which is not the same as none.
    public bool CalendarDaysRead { get; init; }
    public ImmutableArray<MarketContract> Contracts { get; }
    public ImmutableArray<QuoteSpecification> Quotes { get; }
    public ImmutableArray<FuturesSeriesSpecification> FuturesSeries { get; }
    private readonly DefinitionRevision revision;
    public DefinitionRevision DefinitionRevision => revision;
    private readonly ReferenceRevision reference;
    public ReferenceRevision ReferenceRevision=>reference;
    private ReferenceKey R(string code) => new(reference, code);
    private ContractKey K(Guid id) => new(revision, id);
    private SeriesKey S(Guid id) => new(revision, id);
    public PreviewData(PreviewManifest manifest, IReadOnlyDictionary<string, ImmutableArray<JsonElement>> definitions,
        IReadOnlyDictionary<string, ImmutableArray<JsonElement>> references, ImmutableArray<JsonElement> observations,
        OpenEtrm.MarketData.Calculation.ConnectedCalculationCache? calculations=null)
    {
        RequireBundlePin(manifest,Single(definitions["revisions"]));
        this.manifest=manifest; Definitions=definitions.ToImmutableDictionary(); References=references.ToImmutableDictionary(); ExecutionPolicies=new(References); Observations=observations;
        // Index only the rows captured by this authorized read. Grouping retains
        // duplicate keys so each consumer still applies its exact cardinality rule.
        referenceRows=IndexRows(References,[("quantity_specifications","code"),("quote_specifications","code"),
            ("settlement_targets","code"),("futures_series","code"),("futures_products","code"),
            ("futures_milestones","series_code"),("futures_product_terms","product_code"),
            ("futures_lot_resolutions","series_code"),("futures_basis_settlements","series_code"),
            ("units","code"),("currencies","code"),("sources","code"),("interest_rate_definitions","code"),
            ("delivery_profiles","code"),("clock_rules","code")]);
        definitionRows=IndexRows(Definitions,[("curves","code"),("curve_members","curve_code"),
            ("curve_inputs","curve_code"),("rate_curves","code"),("rate_curve_points","curve_code"),
            ("curve_blocks","curve_code"),("shaped_curves","code"),("shaped_curve_blocks","shaped_curve_code")]);
        contractDefinitions=Definitions["contracts"].ToLookup(row=>row.Id("id"));
        curveMembers=Definitions["curve_members"].Select(row=>(row.Text("curve_code"),row.Id("contract_id"))).ToHashSet();
        observationRows=observations.ToLookup(row=>row.Id("id"));
        correctionRows=observations.ToLookup(row=>row.OptionalId("corrects_id"));
        revision=new(manifest.CatalogId,manifest.RevisionId); reference=new(manifest.ReferenceCatalogId,manifest.ReferenceRevisionId);
        var quantities=references["quantity_specifications"].Select(q => new QuantitySpecification(R(q.Text("code")),R(q.Text("commodity_code")),R(q.Text("basis_code")),R(q.Text("unit_code")),R(q.Text("dimension_code")))).ToImmutableArray();
        var quantitiesByUnit=quantities.ToLookup(q=>(q.Commodity.Code,q.Unit.Code));
        var quotes=Quotes=references["quote_specifications"].Select(q => new QuoteSpecification(R(q.Text("code")),q.Text("kind") switch {"outright"=>QuoteKind.Outright,"basis"=>QuoteKind.Basis,_=>throw Unsupported()},R(q.Text("currency_code")),R(q.Text("quantity_code")),q.Number("multiplier"),q.Number("trading_tick"),q.Number("clearing_increment"),q.Number("minimum"),q.Number("maximum"))).ToImmutableArray();
        quoteRows=quotes.ToLookup(q=>q.Key.Code,StringComparer.Ordinal);
        var intervals=definitions["contract_intervals"].ToDictionary(i=>i.Id("contract_id"),i=>new DeliveryInterval(i.Instant("starts_at"),i.Instant("ends_at"),
            DateOnly.ParseExact(i.Text("operating_date"),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(i.Text("hour_beginning"),System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(i.Text("minute"),System.Globalization.CultureInfo.InvariantCulture),int.Parse(i.Text("occurrence"),System.Globalization.CultureInfo.InvariantCulture)));
        var contracts=Contracts=definitions["contracts"].Select(c => new MarketContract(K(c.Id("id")),c.Text("kind") switch {"future"=>ContractKind.Future,"forward"=>ContractKind.Forward,_=>throw Unsupported()},DateOnly.ParseExact(c.Text("delivery_from"),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),DateOnly.ParseExact(c.Text("delivery_to"),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),c.Instant("expiry_at"),c.OptionalText("futures_series_code") is {} series ? R(series):null,intervals.GetValueOrDefault(c.Id("id")))).ToImmutableArray();
        contractRows=contracts.ToLookup(c=>c.Key.Id);
        var mappings=definitions["constituent_mappings"].ToLookup(row=>(row.Text("curve_code"),row.Text("slot")));
        var curves=definitions["curves"].Select(c =>
        {
            var code=c.Text("code"); var members=DefinitionRows("curve_members","curve_code",code).OrderBy(m=>m.GetProperty("ordinal").GetInt32()).Select(m=>K(m.Id("contract_id"))).ToImmutableArray();
            var target=Single(ReferenceRows("settlement_targets","code",c.Text("settlement_target_code")));
            var inputs=DefinitionRows("curve_inputs","curve_code",code).ToImmutableArray();
            var numeric=inputs.Where(i=>i.Text("slot")=="multiplier").ToImmutableArray();
            NumericInput? multiplier=null;
            if (numeric.Length!=0)
            {
                var n=Single(numeric);
                multiplier=new(n.OptionalId("source_series_id") is {} id ? S(id):null,
                    n.OptionalText("value_specification_code") is {} spec ? R(spec):null,
                    n.OptionalText("constant_value") is {} constant ? ExactDecimal.Parse(constant):null,
                    n.OptionalText("numeric_role") is {} role ? ParseRole(role):null,
                    n.OptionalText("numeric_source_code") is {} source ? R(source):null);
            }
            return new Curve(new(revision,code),R(c.Text("quote_code")),R(c.Text("location_code")),R(c.Text("profile_code")),
                new(target.Text("kind") switch {"future_series"=>SettlementKind.FutureSeries,"index"=>SettlementKind.Index,"index_difference"=>SettlementKind.IndexDifference,_=>throw Unsupported()},target.OptionalText("index_a_code") is {} a?R(a):null,target.OptionalText("index_b_code") is {} b?R(b):null),
                c.Text("rule") switch {"direct"=>CurveRule.Direct,"add"=>CurveRule.Add,"scale_add"=>CurveRule.ScaleAdd,_=>throw Unsupported()},members,
                inputs.Where(i=>i.Text("slot")!="multiplier").Select(i=>new CurveInput(i.Text("slot") switch {"base"=>InputSlot.Base,"spread"=>InputSlot.Spread,_=>throw Unsupported()},new(revision,i.Text("source_curve_code")),i.Text("expiry_rule") switch {"require_live"=>SourceExpiryRule.RequireLive,"freeze_final_future"=>SourceExpiryRule.FreezeFinalFuture,_=>throw Unsupported()},
                    mappings[(code,i.Text("slot"))].Select(m=>new ConstituentMapping(K(m.Id("target_contract_id")),K(m.Id("source_contract_id")))).ToImmutableArray())).ToImmutableArray(),
                c.OptionalText("direct_role") is {} directRole?ParseRole(directRole):null,c.OptionalText("direct_source_code") is {} directSource?R(directSource):null,multiplier);
        }).ToImmutableArray();
        var futures=FuturesSeries=contracts.Where(c=>c.Kind==ContractKind.Future).Select(c=>
        {
            var series=Single(ReferenceRows("futures_series","code",c.FuturesSeries!.Value.Code));
            var product=Single(ReferenceRows("futures_products","code",series.Text("product_code")));
            var milestones=ReferenceRows("futures_milestones","series_code",series.Text("code"));
            var expiry=Single(milestones.Where(m=>m.Text("milestone")=="last_trading"));
            var first=Single(milestones.Where(m=>m.Text("milestone")=="first_trading"));
            var terms=Single(ReferenceRows("futures_product_terms","product_code",series.Text("product_code")));
            ReferenceKey Quantity(string unit)=>Single(quantitiesByUnit[(product.Text("commodity_code"),unit)]).Key;
            var resolution=references.ContainsKey("futures_lot_resolutions")?ReferenceRows("futures_lot_resolutions","series_code",series.Text("code")).ToArray():[];
            if(resolution.Length>1)throw Unsupported();
            var lot=resolution.SingleOrDefault();
            FuturesLotResolution? resolved=lot.ValueKind==JsonValueKind.Undefined?null:new(new(lot.Number("amount"),R(lot.Text("quantity_code"))),lot.Number("factor"),lot.Text("implementation"));
            var basis=ReferenceRows("futures_basis_settlements","series_code",series.Text("code")).SingleOrDefault();
            if(basis.ValueKind!=JsonValueKind.Undefined&&terms.OptionalText("index_code") is not null)throw new MarketDataException("futures_definition","A basis settlement index is retained once on the exact dated series.");
            FuturesBasisSettlement? basisSettlement=basis.ValueKind==JsonValueKind.Undefined?null:new(R(basis.Text("index_code")),R(basis.Text("benchmark_series_code")),basis.Text("implementation"));
            return new FuturesSeriesSpecification(c.FuturesSeries!.Value,Quantity(product.Text("quote_unit_code")),R(product.Text("quote_currency_code")),product.Number("quote_multiplier"),product.Number("outright_tick_size"),R(product.Text("location_code")),R(terms.Text("profile_code")),
                DateOnly.ParseExact(series.Text("delivery_from"),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),DateOnly.ParseExact(series.Text("delivery_to"),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),SchedulePreview.RequiredMilestoneInstant(expiry),Quantity(product.Text("lot_unit_code")),product.Number("contract_size"),
                product.Text("lot_basis") switch {"fixed_total"=>LotBasis.FixedTotal,"per_delivery_day"=>LotBasis.PerDeliveryDay,"per_delivery_hour"=>LotBasis.PerDeliveryHour,_=>throw Unsupported()},first.OptionalText("event_at") is null?null:SchedulePreview.RequiredMilestoneInstant(first),product.Text("settlement_method"),resolved,basisSettlement);
        }).ToImmutableArray();
        var valueSpecs=references["value_specifications"].Select(v=>new ValueSpecification(R(v.Text("code")),v.Text("kind") switch {"quantity_ratio"=>NumericKind.QuantityRatio,"dimensionless"=>NumericKind.Dimensionless,_=>throw Unsupported()},v.OptionalText("numerator_quantity_code") is {} a?R(a):null,v.OptionalText("denominator_quantity_code") is {} b?R(b):null,v.Number("minimum"),v.Number("maximum"))).ToImmutableArray();
        var seriesDefinitions=definitions["series"].Select(s=>new MarketSeries(S(s.Id("id")),s.OptionalId("contract_id") is {} id?K(id):null,s.OptionalText("quote_code") is {} q?R(q):null,s.OptionalText("value_specification_code") is {} v?R(v):null)).ToImmutableArray();
        Model=new(revision,reference,quantities,quotes,contracts,curves,futures,valueSpecs,seriesDefinitions,calculations);
    }
    private static Dictionary<(string Table,string Field),ILookup<string,JsonElement>> IndexRows(
        ImmutableDictionary<string,ImmutableArray<JsonElement>> tables,(string Table,string Field)[] keys)
        =>keys.Where(key=>tables.ContainsKey(key.Table)).ToDictionary(key=>key,
            key=>tables[key.Table].ToLookup(row=>row.Text(key.Field),StringComparer.Ordinal));
    public IEnumerable<JsonElement> ReferenceRows(string table,string field,string key)=>referenceRows[(table,field)][key];
    public IEnumerable<JsonElement> DefinitionRows(string table,string field,string key)=>definitionRows[(table,field)][key];
    public IEnumerable<MarketContract> ContractRows(Guid id)=>contractRows[id];
    public IEnumerable<QuoteSpecification> QuoteRows(string code)=>quoteRows[code];
    public bool HasCurveMember(string curve,Guid contract)=>curveMembers.Contains((curve,contract));
    public static void RequireBundlePin(PreviewManifest manifest, JsonElement row)
    {
        if (row.Id("catalog_id") != manifest.CatalogId || row.Id("id") != manifest.RevisionId ||
            row.Id("reference_catalog_id") != manifest.ReferenceCatalogId || row.Id("reference_revision_id") != manifest.ReferenceRevisionId ||
            row.Text("state") != "sealed")
            throw new MarketDataException("reference_pin_mismatch", "The persisted sealed bundle must match every exact manifest reference pin.");
    }
    public static ObservationRole ParseRole(string role) => role switch {"trader_mark"=>ObservationRole.TraderMark,"daily_settlement"=>ObservationRole.DailySettlement,"final_contract_settlement"=>ObservationRole.FinalContractSettlement,"parameter"=>ObservationRole.Parameter,"index_fixing"=>ObservationRole.IndexFixing,_=>throw Unsupported()};
    private static MarketDataException Unsupported()=>new("unsupported_definition","The selected definition uses an unsupported kind.");
    private static T Single<T>(IEnumerable<T> source)
    { var values=source.Take(2).ToArray(); return values.Length==1?values[0]:throw new MarketDataException("ambiguous_reference","A required exact reference is missing or ambiguous."); }
    public JsonElement Observation(Guid id)=>Single(observationRows[id]);
    public bool HasObservation(Guid id)=>observationRows.Contains(id);
    public InputFrame SelectPinned(InputFrame frame)
    {
        var pins=frame.ObservationIds.Select(id=>
        {
            if(!frame.FrozenObservationIds.IsDefault&&frame.FrozenObservationIds.Contains(id))return id;
            var current=Observation(id);
            while(true){var successors=correctionRows[current.Id("id")].ToArray();
                if(successors.Length==0)return current.Id("id");
                if(successors.Length!=1)throw new MarketDataException("revision_conflict","An observation has conflicting successors.");
                current=successors[0];}
        }).ToImmutableArray();
        return frame with {ObservationIds=pins};
    }
    public MarketSnapshot Snapshot(InputFrame frame)
    {
        if(frame.ObservationIds.Distinct().Count()!=frame.ObservationIds.Length)
            throw new MarketDataException("snapshot_inputs","A frame must name each native observation once; mixed uses of the same observation require a distinct explicit input contract.");
        var selections=frame.ObservationIds.Select(id=>
        {
            var o=Observation(id);
            return new SelectedObservation(new(S(o.Id("series_id")),o.Text("role")=="index_fixing"||!frame.FrozenObservationIds.IsDefault&&frame.FrozenObservationIds.Contains(id)?InputUse.FrozenFact:InputUse.Variable),new(new(manifest.CatalogId,id),S(o.Id("series_id")),R(o.Text("source_code")),ParseRole(o.Text("role")),
                o.Text("publication_state") switch {"final"=>PublicationState.Final,"preliminary"=>PublicationState.Preliminary,_=>throw Unsupported()},o.Instant("observed_at"),o.Number("value")));
        }).ToImmutableArray();
        return new(frame.Id,revision,frame.At,frame.At,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,selections);
    }
    public CurveBuild Build(InputFrame frame,string code)=>Build(frame,[code]);
    // One build of each curve's selected contracts in the frame.
    public CurveBuild Build(InputFrame frame,IReadOnlyList<string> codes)
    {
        var requests=ImmutableArray.CreateBuilder<CurveRequest>();
        foreach(var code in codes)
        {
            if (string.IsNullOrEmpty(code) || !DefinitionRows("curves","code",code).Any()) throw new MarketDataException("missing_curve","Select an exact curve definition.");
            if(!frame.CurveContracts.TryGetValue(code,out var members) || members.IsDefault)
                throw new MarketDataException("missing_curve_view","An explicit contract selection is required for this market date.");
            if(members.IsEmpty)throw new MarketDataException("empty_curve_view","This market date has no remaining contracts in the selected view.");
            requests.AddRange(members.Select(id=>new CurveRequest(new(revision,code),K(id))));
        }
        return Model.Build(Snapshot(frame),requests.ToImmutable());
    }
    public ZeroRateDefinition RateDefinition(string code)
    {
        var curve=Single(DefinitionRows("rate_curves","code",code));
        var specification=Single(ReferenceRows("interest_rate_definitions","code",curve.Text("interest_rate_code")));
        var pillars=DefinitionRows("rate_curve_points","curve_code",code).OrderBy(p=>p.GetProperty("ordinal").GetInt32()).Select(p=>new RatePillar(S(p.Id("series_id")),DateOnly.ParseExact(p.Text("maturity"),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture))).ToImmutableArray();
        return new(new(revision,code),R(specification.Text("code")),R(specification.Text("currency_code")),specification.Text("day_count"),specification.Text("compounding"),specification.Text("implementation"),curve.Text("date_basis"),R(curve.Text("source_code")),pillars);
    }
    public ZeroRateBuild BuildRates(InputFrame frame,string code) => new ZeroRateCurve(RateDefinition(code)).Build(Snapshot(frame));
    public bool IsRateCurve(string code)=>DefinitionRows("rate_curves","code",code).Any();
    public (string Value,Guid[] Inputs) HistoryValue(InputFrame frame,string code,Guid? contract,Guid? series)
    {
        if(IsRateCurve(code))
        {
            if(contract is not null||series is null)throw new MarketDataException("history_request","Select an exact rate sample.");
            var point=BuildRates(frame,code).Values.SingleOrDefault(p=>p.Series.Id==series)??throw new MarketDataException("missing_rate_point","Rate sample does not belong to this curve.");
            return (point.RatePercent,[point.Observation.Id]);
        }
        if(series is not null||contract is null)throw new MarketDataException("history_request","Select an exact market contract.");
        // History follows the exact dated member, even outside another frame's
        // displayed window. Never replace it with that frame's rolling selection.
        var value=Model.Build(Snapshot(frame),[new(new(revision,code),K(contract.Value))]).Values[0];
        return (value.Price.Value.ToString(),value.Lineage.Select(k=>k.Id).Order().ToArray());
    }
    public object Result(InputFrame frame,string code)
    {
        if(IsRateCurve(code))
        {
            var rates=BuildRates(frame,code);
            return new {kind="rates",snapshotId=frame.Id,frame.Name,at=frame.At,curve=code,referenceDate=rates.ReferenceDate.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),currency=rates.Currency.Code,numericalPolicy=rates.NumericalPolicy,convention=new {dayCount=OpenEtrm.Rates.RateConventions.DayCountName,compounding=OpenEtrm.Rates.RateConventions.CompoundingName},
                points=rates.Values.Select(p=>new {seriesId=p.Series.Id,maturity=p.Maturity.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),annualRate=p.AnnualRate,value=p.RatePercent,discountFactor=p.DiscountFactor,displayDiscountFactor=p.DisplayDiscountFactor,actualDays=p.Days,lineage=new[]{p.Observation.Id}}).ToArray()};
        }
        if(frame.CurveContracts.TryGetValue(code,out var selected) && selected.IsEmpty)
        {
            if(!DefinitionRows("curves","code",code).Any())throw new MarketDataException("missing_curve","Select an exact curve definition.");
            return new {snapshotId=frame.Id,frame.Name,at=frame.At,curve=code,availability="no_remaining_contracts",
                evaluated=0,numericalPolicy=CurveModel.NumericalPolicy,trace=Array.Empty<object>(),points=Array.Empty<object>(),observationIds=frame.ObservationIds};
        }
        var build=Build(frame,code);
        var nodeIds=build.Evaluations.Select((e,i)=>(e,i)).ToDictionary(p=>(p.e.Result.Request,p.e.Use),p=>"step_"+p.i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var trace=build.Evaluations.Select(e=>
        {
            var v=e.Result;
            var c=Single(contractDefinitions[v.Request.Contract.Id]);
            var definition=Single(DefinitionRows("curves","code",v.Request.Curve.Code));
            var multiplier=DefinitionRows("curve_inputs","curve_code",v.Request.Curve.Code).SingleOrDefault(i=>i.Text("slot")=="multiplier");
            return new {id=nodeIds[(v.Request,e.Use)],curve=v.Request.Curve.Code,contractId=v.Request.Contract.Id,
                deliveryFrom=c.Text("delivery_from"),deliveryTo=c.Text("delivery_to"),priceTick=c.Text("kind")=="future"?QuoteRows(v.Quote.Code).Single(q=>q.Key==v.Quote).ClearingIncrement.ToString():null,value=v.Price.Value.ToString(),quote=v.Quote.Code,
                rule=definition.Text("rule"),use=e.Use==InputUse.FrozenFact?"frozen":"variable",
                source=definition.OptionalText("direct_source_code"),role=e.Use==InputUse.FrozenFact?"final_contract_settlement":definition.OptionalText("direct_role"),
                inputs=e.Inputs.Select(i=>new {slot=i.Slot==InputSlot.Base?"base":"spread",id=nodeIds[(i.Source,i.Use)]}).ToArray(),
                multiplier=e.Multiplier is {} factor?new {value=factor.Value.ToString(),observationId=(Guid?)e.MultiplierObservation?.Id,valueSpecification=multiplier.ValueKind==JsonValueKind.Undefined?null:multiplier.OptionalText("value_specification_code")}:null,
                lineage=v.Lineage.Select(k=>k.Id).Order().ToArray()};
        }).ToArray();
        // The display count describes the complete resolved trace, including
        // memoized results. Physical work remains in the graph's work counters.
        return new { snapshotId=frame.Id, frame.Name, at=frame.At, curve=code, evaluated=build.Evaluations.Length, numericalPolicy=build.NumericalPolicy,
            trace, points=build.Values.Select(v=>
            {
                var c=Single(contractDefinitions[v.Request.Contract.Id]);
                return new { contractId=v.Request.Contract.Id,instrumentId=c.OptionalText("futures_series_code") is {} future?(Guid?)ContractIdentity.Futures(R(future)):null, deliveryFrom=c.Text("delivery_from"),deliveryTo=c.Text("delivery_to"),expiry=c.Text("expiry_at"),priceTick=c.Text("kind")=="future"?QuoteRows(v.Quote.Code).Single(q=>q.Key==v.Quote).ClearingIncrement.ToString():null,value=v.Price.Value.ToString(),quote=v.Quote.Code,lineage=v.Lineage.Select(k=>k.Id).Order().ToArray(),
                    interval=IntervalCurveDefinitions.Label(contractRows[v.Request.Contract.Id].Single().Interval) };
            }).ToArray(), observationIds=frame.ObservationIds };
    }
}
public static class JsonFields
{
    public static string Text(this JsonElement row,string field)=>row.GetProperty(field).ValueKind==JsonValueKind.String?row.GetProperty(field).GetString()!:row.GetProperty(field).GetRawText();
    public static string? OptionalText(this JsonElement row,string field)=>row.GetProperty(field).ValueKind==JsonValueKind.Null?null:row.Text(field);
    public static Guid Id(this JsonElement row,string field)=>Guid.Parse(row.Text(field));
    public static Guid? OptionalId(this JsonElement row,string field)=>row.OptionalText(field) is {} s?Guid.Parse(s):null;
    public static ExactDecimal Number(this JsonElement row,string field)=>ExactDecimal.Parse(row.Text(field));
    public static DateTimeOffset Instant(this JsonElement row,string field)=>DateTimeOffset.Parse(row.Text(field),System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
}
