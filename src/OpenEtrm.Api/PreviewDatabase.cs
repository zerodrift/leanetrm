using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public sealed record PreviewSwapMarket(Guid RevisionId,PreviewSeedFrame[] Frames);
public sealed record PreviewSequenceCoverage(string Curve, DateTimeOffset From, DateTimeOffset To, DateTimeOffset ExampleAt);
public sealed record PreviewExample(DateTimeOffset At,string Curve);
public sealed record PreviewSeedFrame(DateTimeOffset At, Guid[] ObservationIds, Dictionary<string,Guid[]> CurveContracts);
public sealed record InputFrame(Guid Id, string Name, DateTimeOffset At, ImmutableArray<Guid> ObservationIds,
    ImmutableDictionary<string,ImmutableArray<Guid>> CurveContracts,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? ContextId=null);
public sealed class PreviewDeniedException : Exception;

public sealed class PreviewDatabase : IAsyncDisposable
{
    private readonly NpgsqlDataSource authenticator;
    private readonly NpgsqlDataSource runtime;
    private readonly string runtimeLogin;
    private readonly Func<string>? hostedSession;
    public PreviewManifest Manifest { get; }
    public static readonly string[] ReferenceTables = ["currencies", "dimensions", "units", "unit_conversions", "commodities",
        "measurement_bases", "quantity_specifications", "quote_specifications", "value_specifications", "locations",
        "clock_rules", "calendars", "calendar_days", "commodity_units", "location_types", "commodity_locations",
        "exchanges", "clearing_houses", "exchange_clearing_houses",
        "interest_rate_definitions", "price_index_definitions", "sources", "product_evidence",
        "delivery_profiles", "settlement_targets", "cash_rounding_conventions",
        "futures_products", "futures_series", "futures_milestones", "futures_lot_resolutions", "futures_product_terms", "futures_basis_settlements"];
    private static readonly string[] DefinitionTables = ["revisions", "contracts", "curves", "series", "curve_members", "curve_inputs", "constituent_mappings", "rate_curves", "rate_curve_points", "revision_periods"];

    public PreviewDatabase(string root,string manifestName="manifest.json", HostedConfiguration? hosted=null,Func<string>? hostedSession=null)
    {
        if(manifestName is not ("manifest.json" or "exchange-manifest.json"))throw new InvalidOperationException("Choose a configured preview manifest.");
        Manifest = JsonSerializer.Deserialize<PreviewManifest>(File.ReadAllText(Path.Combine(root, manifestName)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Missing preview manifest.");
        if (Manifest.Mode != (hosted is null ? "local-synthetic-preview" : "hosted-pilot")) throw new InvalidOperationException("Manifest does not match the explicitly selected host mode.");
        if (hosted is not null && Manifest.IdentityIssuer != hosted.IdentityIssuer) throw new InvalidOperationException("The exact hosted identity issuer must match the manifest.");
        NpgsqlDataSource Source(string variable, string login)
        {
            var config = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(variable)
                ?? throw new InvalidOperationException($"Missing {variable}."));
            if (Path.GetFullPath(config.Host!) != root || config.Username != login || config.Database != "postgres")
                throw new InvalidOperationException("Preview connections must use its private owned socket and dedicated test login.");
            // JSON timestamp transport follows the API UTC contract, independently
            // of the database host, process environment or display preference.
            config.Timezone = "UTC";
            config.CommandTimeout = 5; config.Timeout = 5; config.MaxPoolSize = 8;
            return NpgsqlDataSource.Create(config.ConnectionString);
        }
        if (hosted is null)
        {
            authenticator = Source("OPENETRM_PREVIEW_AUTHENTICATOR", "preview_authenticator");
            runtime = Source("OPENETRM_PREVIEW_RUNTIME", "preview_runtime");
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
            runtimeLogin=app.Username!;
        }
    }

    public async Task<T> WithIdentity<T>(string profile, string? permission, Func<NpgsqlConnection, CancellationToken, Task<T>> operation, CancellationToken ct, bool administration=false)
    {
        if (string.IsNullOrWhiteSpace(profile) || profile.Length>48) throw new PreviewDeniedException();
        await using var issue = authenticator.CreateCommand(hostedSession is null?"SELECT security.issue_context($1,$2,$3,$4,$5,NULL,$6::name,60)":"SELECT security.issue_hosted_context($1,$2::name)");
        foreach (var value in hostedSession is null?new object[] { Manifest.IdentityIssuer, profile, "human_interactive", Manifest.TenantId, Manifest.WorkspaceId, runtimeLogin }:new object[]{hostedSession(),runtimeLogin})
            issue.Parameters.Add(new NpgsqlParameter { Value = value });
        var token = (Guid)(await issue.ExecuteScalarAsync(ct) ?? throw new PreviewDeniedException());
        await using var connection = await runtime.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var bind = new NpgsqlCommand(administration?"SELECT security.use_administration_context($1)":"SELECT security.use_context($1)", connection, transaction))
        { bind.Parameters.AddWithValue(token); await bind.ExecuteNonQueryAsync(ct); }
        if(hostedSession is not null)await using(var sessionCheck=new NpgsqlCommand("SELECT security.require_hosted_session()",connection,transaction))await sessionCheck.ExecuteNonQueryAsync(ct);
        if(permission is not null) await using (var check = new NpgsqlCommand("SELECT security.has_permission($1)", connection, transaction))
        {
            check.Parameters.AddWithValue(permission);
            if (!Equals(await check.ExecuteScalarAsync(ct), true)) throw new PreviewDeniedException();
        }
        var result = await operation(connection, ct);
        if(hostedSession is not null)await using(var sessionCheck=new NpgsqlCommand("SELECT security.require_hosted_session()",connection,transaction))await sessionCheck.ExecuteNonQueryAsync(ct);
        if(permission is not null) await using (var finalCheck = new NpgsqlCommand("SELECT security.has_permission($1)", connection, transaction))
        {
            finalCheck.Parameters.AddWithValue(permission);
            if (!Equals(await finalCheck.ExecuteScalarAsync(ct), true)) throw new PreviewDeniedException();
        }
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task ValidateHostedBoundary(CancellationToken ct)
    {
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

    public async Task<PreviewData> Read(NpgsqlConnection connection, CancellationToken ct,Guid? selectedRevision=null,PreviewData? sharedReferences=null)
    {
        if(selectedRevision is {} requested&&requested!=Manifest.RevisionId&&!(Manifest.SwapMarkets??[]).Any(m=>m.RevisionId==requested))throw new PreviewDeniedException();
        var selectedManifest=selectedRevision is {} id?Manifest with {RevisionId=id}:Manifest;
        var definitions = new Dictionary<string, ImmutableArray<JsonElement>>();
        foreach (var table in DefinitionTables)
        {
            var revisionColumn = table == "revisions" ? "id" : "revision_id";
            definitions[table] = await Rows(connection, $"SELECT row_to_json(t)::text FROM (SELECT * FROM market_data.{table} WHERE catalog_id=$1 AND {revisionColumn}=$2) t", [selectedManifest.CatalogId, selectedManifest.RevisionId], ct);
        }
        if (definitions["revisions"].Length != 1 || definitions["revisions"][0].Text("state") != "sealed") throw new PreviewDeniedException();
        PreviewData.RequireBundlePin(selectedManifest, definitions["revisions"][0]);
        IReadOnlyDictionary<string,ImmutableArray<JsonElement>> references;
        if(sharedReferences is not null)
        {
            if(sharedReferences.ReferenceRevision!=new ReferenceRevision(selectedManifest.ReferenceCatalogId,selectedManifest.ReferenceRevisionId)||sharedReferences.DefinitionRevision.CatalogId!=selectedManifest.CatalogId)throw new MarketDataException("reference_pin_mismatch","Shared reference rows require the same exact authorized catalog and revision.");
            references=sharedReferences.References;
        }
        else
        {
            var loaded=new Dictionary<string,ImmutableArray<JsonElement>>();
            foreach(var table in ReferenceTables)loaded[table]=await Rows(connection,$"SELECT row_to_json(t)::text FROM(SELECT * FROM ref_data.{table} WHERE catalog_id=$1 AND revision_id=$2)t",[selectedManifest.ReferenceCatalogId,selectedManifest.ReferenceRevisionId],ct);
            references=loaded;
        }
        var observations = await Rows(connection, "SELECT row_to_json(t)::text FROM (SELECT * FROM market_data.observations WHERE catalog_id=$1 AND definition_revision_id=$2 ORDER BY observed_at,received_at,id) t", [selectedManifest.CatalogId, selectedManifest.RevisionId], ct);
        return new PreviewData(selectedManifest, definitions, references, observations);
    }

    public async Task<UnitCatalogData> ReadUnitCatalog(NpgsqlConnection connection,PreviewData selected,CancellationToken ct)
    {
        if(Manifest.UnitCatalog is not {} configured)
            return new(selected.ReferenceRevision,selected.References);
        var revision=new ReferenceRevision(Manifest.ReferenceCatalogId,configured.ReferenceRevisionId);
        // This exact published bundle must be visible under the same bound
        // effective/knowledge context. Never substitute the current unit version.
        var visible=await Rows(connection,"SELECT row_to_json(t)::text FROM(SELECT reference_catalog_id,reference_revision_id FROM market_data.revisions WHERE catalog_id=$1 AND id=$2 AND state='sealed')t",[Manifest.CatalogId,configured.DefinitionRevisionId],ct);
        if(visible.Length!=1 || visible[0].Id("reference_catalog_id")!=revision.CatalogId || visible[0].Id("reference_revision_id")!=revision.RevisionId)
            throw new MarketDataException("unit_catalog","The exact configured unit catalog is unavailable in this context.");
        var references=ImmutableDictionary.CreateBuilder<string,ImmutableArray<JsonElement>>();
        foreach(var table in new[]{"dimensions","units","unit_conversions","commodities","measurement_bases","quantity_specifications"})
            references.Add(table,await Rows(connection,$"SELECT row_to_json(t)::text FROM(SELECT * FROM ref_data.{table} WHERE catalog_id=$1 AND revision_id=$2)t",[revision.CatalogId,revision.RevisionId],ct));
        return new(revision,references.ToImmutable());
    }

    public async Task<ImmutableArray<(PreviewData Data,InputFrame Frame)>> ReadSwapMarkets(NpgsqlConnection connection,InputFrame selected,CancellationToken ct,PreviewData? sharedReferences=null)
    {
        var result=ImmutableArray.CreateBuilder<(PreviewData,InputFrame)>();
        foreach(var market in Manifest.SwapMarkets??[])
        {
            await using var available=ScheduleStore.Command(connection,"SELECT count(*) FROM market_data.revisions WHERE catalog_id=$1 AND id=$2",Manifest.CatalogId,market.RevisionId);
            if((long)(await available.ExecuteScalarAsync(ct))! == 0)continue;
            var seed=market.Frames.SingleOrDefault(f=>f.At==selected.At)??throw new MarketDataException("swap_context","No explicit daily-market frame exists for the selected As of time.");
            var data=await Read(connection,ct,market.RevisionId,sharedReferences);
            var frame=new InputFrame(selected.Id,"Selected swap market",selected.At,seed.ObservationIds.ToImmutableArray(),seed.CurveContracts.ToImmutableDictionary(p=>p.Key,p=>p.Value.ToImmutableArray()),selected.ContextId);
            result.Add((data,data.SelectPinned(frame)));
        }
        return result.ToImmutable();
    }

    public async Task<Guid> Correct(NpgsqlConnection connection, Guid observationId, string value, CancellationToken ct)
    {
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
        foreach (var parameter in new object[] { id, id.ToString(), parsed.ToString(), Manifest.CatalogId, Manifest.RevisionId, observationId })
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
        while (await reader.ReadAsync(ct))
        {
            var row = JsonNode.Parse(reader.GetString(0))!.AsObject();
            // Preserve database numeric lexemes over JSON. Only the ordering key
            // is consumed as an integer; financial arithmetic stays on the server.
            foreach (var field in row.ToArray())
                if (field.Key != "ordinal" && field.Value is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
                    row[field.Key] = value.ToJsonString();
            rows.Add(JsonSerializer.SerializeToElement(row));
        }
        return rows.ToImmutable();
    }
    public async ValueTask DisposeAsync() { await runtime.DisposeAsync(); await authenticator.DisposeAsync(); }
}

public sealed class PreviewData
{
    private readonly PreviewManifest manifest;
    public ImmutableDictionary<string, ImmutableArray<JsonElement>> Definitions { get; }
    public ImmutableDictionary<string, ImmutableArray<JsonElement>> References { get; }
    public ImmutableArray<JsonElement> Observations { get; }
    public CurveModel Model { get; }
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
        IReadOnlyDictionary<string, ImmutableArray<JsonElement>> references, ImmutableArray<JsonElement> observations)
    {
        RequireBundlePin(manifest,Single(definitions["revisions"]));
        this.manifest=manifest; Definitions=definitions.ToImmutableDictionary(); References=references.ToImmutableDictionary(); Observations=observations;
        revision=new(manifest.CatalogId,manifest.RevisionId); reference=new(manifest.ReferenceCatalogId,manifest.ReferenceRevisionId);
        var quantities=references["quantity_specifications"].Select(q => new QuantitySpecification(R(q.Text("code")),R(q.Text("commodity_code")),R(q.Text("basis_code")),R(q.Text("unit_code")),R(q.Text("dimension_code")))).ToImmutableArray();
        var quotes=Quotes=references["quote_specifications"].Select(q => new QuoteSpecification(R(q.Text("code")),q.Text("kind") switch {"outright"=>QuoteKind.Outright,"basis"=>QuoteKind.Basis,_=>throw Unsupported()},R(q.Text("currency_code")),R(q.Text("quantity_code")),q.Number("multiplier"),q.Number("trading_tick"),q.Number("clearing_increment"),q.Number("minimum"),q.Number("maximum"))).ToImmutableArray();
        var contracts=Contracts=definitions["contracts"].Select(c => new MarketContract(K(c.Id("id")),c.Text("kind") switch {"future"=>ContractKind.Future,"forward"=>ContractKind.Forward,_=>throw Unsupported()},DateOnly.ParseExact(c.Text("delivery_from"),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),DateOnly.ParseExact(c.Text("delivery_to"),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),c.Instant("expiry_at"),c.OptionalText("futures_series_code") is {} series ? R(series):null)).ToImmutableArray();
        var curves=definitions["curves"].Select(c =>
        {
            var code=c.Text("code"); var members=definitions["curve_members"].Where(m=>m.Text("curve_code")==code).OrderBy(m=>m.GetProperty("ordinal").GetInt32()).Select(m=>K(m.Id("contract_id"))).ToImmutableArray();
            var target=Single(references["settlement_targets"].Where(t=>t.Text("code")==c.Text("settlement_target_code")));
            var inputs=definitions["curve_inputs"].Where(i=>i.Text("curve_code")==code).ToImmutableArray();
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
                    definitions["constituent_mappings"].Where(m=>m.Text("curve_code")==code&&m.Text("slot")==i.Text("slot")).Select(m=>new ConstituentMapping(K(m.Id("target_contract_id")),K(m.Id("source_contract_id")))).ToImmutableArray())).ToImmutableArray(),
                c.OptionalText("direct_role") is {} directRole?ParseRole(directRole):null,c.OptionalText("direct_source_code") is {} directSource?R(directSource):null,multiplier);
        }).ToImmutableArray();
        var futures=FuturesSeries=contracts.Where(c=>c.Kind==ContractKind.Future).Select(c=>
        {
            var series=Single(references["futures_series"].Where(s=>s.Text("code")==c.FuturesSeries!.Value.Code));
            var product=Single(references["futures_products"].Where(p=>p.Text("code")==series.Text("product_code")));
            var expiry=Single(references["futures_milestones"].Where(m=>m.Text("series_code")==series.Text("code")&&m.Text("milestone")=="last_trading"));
            var first=Single(references["futures_milestones"].Where(m=>m.Text("series_code")==series.Text("code")&&m.Text("milestone")=="first_trading"));
            var terms=Single(references["futures_product_terms"].Where(t=>t.Text("product_code")==series.Text("product_code")));
            ReferenceKey Quantity(string unit)=>Single(quantities.Where(q=>q.Commodity.Code==product.Text("commodity_code")&&q.Unit.Code==unit)).Key;
            var resolution=references.GetValueOrDefault("futures_lot_resolutions",[]).Where(r=>r.Text("series_code")==series.Text("code")).ToArray();
            if(resolution.Length>1)throw Unsupported();
            var lot=resolution.SingleOrDefault();
            FuturesLotResolution? resolved=lot.ValueKind==JsonValueKind.Undefined?null:new(new(lot.Number("amount"),R(lot.Text("quantity_code"))),lot.Number("factor"),lot.Text("implementation"));
            var basis=references["futures_basis_settlements"].SingleOrDefault(b=>b.Text("series_code")==series.Text("code"));
            if(basis.ValueKind!=JsonValueKind.Undefined&&terms.OptionalText("index_code") is not null)throw new MarketDataException("futures_definition","A basis settlement index is retained once on the exact dated series.");
            FuturesBasisSettlement? basisSettlement=basis.ValueKind==JsonValueKind.Undefined?null:new(R(basis.Text("index_code")),R(basis.Text("benchmark_series_code")),basis.Text("implementation"));
            return new FuturesSeriesSpecification(c.FuturesSeries!.Value,Quantity(product.Text("quote_unit_code")),R(product.Text("quote_currency_code")),product.Number("quote_multiplier"),product.Number("outright_tick_size"),R(product.Text("location_code")),R(terms.Text("profile_code")),
                DateOnly.ParseExact(series.Text("delivery_from"),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),DateOnly.ParseExact(series.Text("delivery_to"),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),SchedulePreview.RequiredMilestoneInstant(expiry),Quantity(product.Text("lot_unit_code")),product.Number("contract_size"),
                product.Text("lot_basis") switch {"fixed_total"=>LotBasis.FixedTotal,"per_delivery_day"=>LotBasis.PerDeliveryDay,"per_delivery_hour"=>LotBasis.PerDeliveryHour,_=>throw Unsupported()},SchedulePreview.RequiredMilestoneInstant(first),product.Text("settlement_method"),resolved,basisSettlement);
        }).ToImmutableArray();
        var valueSpecs=references["value_specifications"].Select(v=>new ValueSpecification(R(v.Text("code")),v.Text("kind") switch {"quantity_ratio"=>NumericKind.QuantityRatio,"dimensionless"=>NumericKind.Dimensionless,_=>throw Unsupported()},v.OptionalText("numerator_quantity_code") is {} a?R(a):null,v.OptionalText("denominator_quantity_code") is {} b?R(b):null,v.Number("minimum"),v.Number("maximum"))).ToImmutableArray();
        var seriesDefinitions=definitions["series"].Select(s=>new MarketSeries(S(s.Id("id")),s.OptionalId("contract_id") is {} id?K(id):null,s.OptionalText("quote_code") is {} q?R(q):null,s.OptionalText("value_specification_code") is {} v?R(v):null)).ToImmutableArray();
        Model=new(revision,reference,quantities,quotes,contracts,curves,futures,valueSpecs,seriesDefinitions);
    }
    public static void RequireBundlePin(PreviewManifest manifest, JsonElement row)
    {
        if (row.Id("catalog_id") != manifest.CatalogId || row.Id("id") != manifest.RevisionId ||
            row.Id("reference_catalog_id") != manifest.ReferenceCatalogId || row.Id("reference_revision_id") != manifest.ReferenceRevisionId ||
            row.Text("state") != "sealed")
            throw new MarketDataException("reference_pin_mismatch", "The persisted sealed bundle must match every exact manifest reference pin.");
    }
    public static ObservationRole ParseRole(string role) => role switch {"trader_mark"=>ObservationRole.TraderMark,"daily_settlement"=>ObservationRole.DailySettlement,"final_contract_settlement"=>ObservationRole.FinalContractSettlement,"parameter"=>ObservationRole.Parameter,_=>throw Unsupported()};
    private static MarketDataException Unsupported()=>new("unsupported_definition","The selected definition uses an unsupported kind.");
    private static T Single<T>(IEnumerable<T> source)
    { var values=source.Take(2).ToArray(); return values.Length==1?values[0]:throw new MarketDataException("ambiguous_reference","A required exact reference is missing or ambiguous."); }
    public JsonElement Observation(Guid id)=>Single(Observations.Where(o=>o.Id("id")==id));
    public InputFrame SelectPinned(InputFrame frame)
    {
        var pins=frame.ObservationIds.Select(id=>
        {
            var current=Observation(id);
            while(true){var successors=Observations.Where(o=>o.OptionalId("corrects_id")==current.Id("id")).ToArray();
                if(successors.Length==0)return current.Id("id");
                if(successors.Length!=1)throw new MarketDataException("revision_conflict","An observation has conflicting successors.");
                current=successors[0];}
        }).ToImmutableArray();
        return frame with {ObservationIds=pins};
    }
    public MarketSnapshot Snapshot(InputFrame frame)
    {
        var selections=frame.ObservationIds.Select(id=>
        {
            var o=Observation(id);
            return new SelectedObservation(new(S(o.Id("series_id")),InputUse.Variable),new(new(manifest.CatalogId,id),S(o.Id("series_id")),R(o.Text("source_code")),ParseRole(o.Text("role")),
                o.Text("publication_state") switch {"final"=>PublicationState.Final,"preliminary"=>PublicationState.Preliminary,_=>throw Unsupported()},o.Instant("observed_at"),o.Number("value")));
        }).ToImmutableArray();
        return new(frame.Id,revision,frame.At,frame.At,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,selections);
    }
    public CurveBuild Build(InputFrame frame,string code)
    {
        if (string.IsNullOrEmpty(code) || !Definitions["curves"].Any(c=>c.Text("code")==code)) throw new MarketDataException("missing_curve","Select an exact curve definition.");
        if(!frame.CurveContracts.TryGetValue(code,out var members) || members.IsDefault)
            throw new MarketDataException("missing_curve_view","An explicit contract selection is required for this market date.");
        if(members.IsEmpty)throw new MarketDataException("empty_curve_view","This market date has no remaining contracts in the selected view.");
        var requests=members.Select(id=>new CurveRequest(new(revision,code),K(id))).ToImmutableArray();
        return Model.Build(Snapshot(frame),requests);
    }
    public ZeroRateDefinition RateDefinition(string code)
    {
        var curve=Single(Definitions["rate_curves"].Where(c=>c.Text("code")==code));
        var specification=Single(References["interest_rate_definitions"].Where(r=>r.Text("code")==curve.Text("interest_rate_code")));
        var pillars=Definitions["rate_curve_points"].Where(p=>p.Text("curve_code")==code).OrderBy(p=>p.GetProperty("ordinal").GetInt32()).Select(p=>new RatePillar(S(p.Id("series_id")),DateOnly.ParseExact(p.Text("maturity"),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture))).ToImmutableArray();
        return new(new(revision,code),R(specification.Text("code")),R(specification.Text("currency_code")),specification.Text("day_count"),specification.Text("compounding"),specification.Text("implementation"),curve.Text("date_basis"),R(curve.Text("source_code")),pillars);
    }
    public ZeroRateBuild BuildRates(InputFrame frame,string code) => new ZeroRateCurve(RateDefinition(code)).Build(Snapshot(frame));
    public bool IsRateCurve(string code)=>Definitions["rate_curves"].Any(c=>c.Text("code")==code);
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
            if(!Definitions["curves"].Any(c=>c.Text("code")==code))throw new MarketDataException("missing_curve","Select an exact curve definition.");
            return new {snapshotId=frame.Id,frame.Name,at=frame.At,curve=code,availability="no_remaining_contracts",
                evaluated=0,numericalPolicy=CurveModel.NumericalPolicy,trace=Array.Empty<object>(),points=Array.Empty<object>(),observationIds=frame.ObservationIds};
        }
        var build=Build(frame,code);
        var nodeIds=build.Evaluations.Select((e,i)=>(e,i)).ToDictionary(p=>(p.e.Result.Request,p.e.Use),p=>"step_"+p.i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var trace=build.Evaluations.Select(e=>
        {
            var v=e.Result;
            var c=Single(Definitions["contracts"].Where(c=>c.Id("id")==v.Request.Contract.Id));
            var definition=Single(Definitions["curves"].Where(c=>c.Text("code")==v.Request.Curve.Code));
            var multiplier=Definitions["curve_inputs"].SingleOrDefault(i=>i.Text("curve_code")==v.Request.Curve.Code&&i.Text("slot")=="multiplier");
            return new {id=nodeIds[(v.Request,e.Use)],curve=v.Request.Curve.Code,contractId=v.Request.Contract.Id,
                deliveryFrom=c.Text("delivery_from"),deliveryTo=c.Text("delivery_to"),priceTick=c.Text("kind")=="future"?Quotes.Single(q=>q.Key==v.Quote).ClearingIncrement.ToString():null,value=v.Price.Value.ToString(),quote=v.Quote.Code,
                rule=definition.Text("rule"),use=e.Use==InputUse.FrozenFact?"frozen":"variable",
                source=definition.OptionalText("direct_source_code"),role=e.Use==InputUse.FrozenFact?"final_contract_settlement":definition.OptionalText("direct_role"),
                inputs=e.Inputs.Select(i=>new {slot=i.Slot==InputSlot.Base?"base":"spread",id=nodeIds[(i.Source,i.Use)]}).ToArray(),
                multiplier=e.Multiplier is {} factor?new {value=factor.Value.ToString(),observationId=(Guid?)e.MultiplierObservation?.Id,valueSpecification=multiplier.ValueKind==JsonValueKind.Undefined?null:multiplier.OptionalText("value_specification_code")}:null,
                lineage=v.Lineage.Select(k=>k.Id).Order().ToArray()};
        }).ToArray();
        return new { snapshotId=frame.Id, frame.Name, at=frame.At, curve=code, evaluated=build.EvaluatedConstituents, numericalPolicy=build.NumericalPolicy,
            trace, points=build.Values.Select(v=>
            {
                var c=Single(Definitions["contracts"].Where(c=>c.Id("id")==v.Request.Contract.Id));
                return new { contractId=v.Request.Contract.Id,instrumentId=c.OptionalText("futures_series_code") is {} future?(Guid?)ContractIdentity.Futures(R(future)):null, deliveryFrom=c.Text("delivery_from"),deliveryTo=c.Text("delivery_to"),expiry=c.Text("expiry_at"),priceTick=c.Text("kind")=="future"?Quotes.Single(q=>q.Key==v.Quote).ClearingIncrement.ToString():null,value=v.Price.Value.ToString(),quote=v.Quote.Code,lineage=v.Lineage.Select(k=>k.Id).Order().ToArray() };
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
