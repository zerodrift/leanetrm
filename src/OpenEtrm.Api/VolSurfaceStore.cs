using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

/// <summary>A fit of one option product's surface at a working frame, under an explicit vol model.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VolSurfaceFitRequest(Guid RequestId,Guid SnapshotId,string Product,string Model);

/// <summary>A read of one option product's stored surface at a trading context, under an explicit vol model.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VolSurfaceReadRequest(FuturesReadRequest Context,string Product,string Model);

// A fitted surface as its job's completion records it: the pinned inputs, the canonical bytes and their digest, and the work done.
internal sealed record VolSurfaceFitOutcome(ImmutableArray<Guid> Inputs,string Surface,string Digest,int Series,int Solves,long ElapsedMs);

// Volatility surfaces are fitted by jobs of kind vol_surface_fit_v1 on the retained risk-job queue and read from their stored,
// bitemporal revisions. Admission pins the frame, product, business date, model and actor; the risk worker fits and records.
internal static class VolSurfaceStore
{
    internal const string Convention="vol_surface_fit_v1";
    // The surface's canonical form: camel-case fields in the record's own order, exact decimals as text and option rights by name.
    // The stored content and its digest are these exact bytes.
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
        {Converters={new ExactDecimalJsonConverter(),new LatticeDecimalJsonConverter(),new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)}};
    private static string SolverPolicy=>FuturesOptionValuationPolicy.Release1.NumericalPolicy;

    // Admits a fit job at the view's context, captured from current knowledge over the working frame.
    internal static async Task<object> Submit(NpgsqlConnection connection,PreviewManifest manifest,TradingView view,VolSurfaceFitRequest body,CancellationToken ct)
    {
        if(body.RequestId==Guid.Empty)throw new MarketDataException("vol_surface_input","Supply a stable request identity for the fit.");
        VolModels.Require(body.Model);
        var contextId=view.Context.ContextId??throw new MarketDataException("vol_surface_context","The fit requires its captured trading context.");
        var request=new JsonObject{["convention"]=Convention,["engineVersion"]=RiskJobs.EngineVersion,["product"]=Product(manifest,body.Product),
            ["businessDate"]=BusinessDate(view),["model"]=body.Model,["solverPolicy"]=SolverPolicy,["futuresConvention"]=manifest.FuturesConvention};
        var frame=RiskJobs.PinFrame(view.CalculationFrame with{SwapMarketSelections=null});
        await using var command=ScheduleStore.Command(connection,"SELECT risk.submit_vol_surface_fit($1,$2,$3,$4,$5,$6::jsonb,$7::jsonb)::text",
            manifest.CatalogId,body.RequestId,contextId,manifest.RevisionId,view.Frame.At,request.ToJsonString(),frame.ToJsonString(RiskJobs.Json));
        var job=JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!)!;
        return new{jobId=job["jobId"]!.GetValue<Guid>(),state=job["state"]!.GetValue<string>()};
    }

    // The stored surface of a key at the view's bound context, whether its inputs were revised since, the caller's newest fit job
    // for the key and whether the caller may fit.
    internal static async Task<object> Read(NpgsqlConnection connection,PreviewManifest manifest,TradingView view,VolSurfaceReadRequest body,CancellationToken ct)
    {
        VolModels.Require(body.Model);
        var product=Product(manifest,body.Product).ToJsonString();var date=DateOnly.ParseExact(BusinessDate(view),"yyyy-MM-dd",CultureInfo.InvariantCulture);
        async Task<JsonNode?> Value(string sql,params object[] arguments)
        {
            await using var command=ScheduleStore.Command(connection,sql,arguments);
            return await command.ExecuteScalarAsync(ct) is string text?JsonNode.Parse(text):null;
        }
        var stored=await Value("SELECT security.read_vol_surface($1,$2::jsonb,$3,$4)::text",manifest.CatalogId,product,date,body.Model);
        var job=await Value("SELECT risk.vol_surface_fit_job($1,$2::jsonb,$3,$4)::text",manifest.CatalogId,product,date,body.Model);
        JsonObject? revision=null;
        if(stored is not null)
        {
            var id=stored["revision"]!.GetValue<Guid>();
            await using var revised=ScheduleStore.Command(connection,"SELECT security.vol_surface_inputs_revised($1,$2)",manifest.CatalogId,id);
            revision=new JsonObject{["id"]=id,["knownFrom"]=stored["knownFrom"]?.DeepClone(),["actor"]=stored["actor"]!.DeepClone(),
                ["inputsRevised"]=(bool)(await revised.ExecuteScalarAsync(ct))!};
        }
        var canFit=await PreviewDatabase.HasPermission(connection,"PublishMarketData",ct)&&await PreviewDatabase.HasPermission(connection,"ReadTrades",ct);
        return new JsonObject{["snapshotId"]=view.Frame.Id,["model"]=body.Model,
            ["solverPolicy"]=stored?["solverPolicy"]?.DeepClone()??SolverPolicy,["state"]=stored is null?"notFitted":"fitted",
            ["revision"]=revision,["job"]=job,["canFit"]=canFit,["surface"]=stored?["content"]?.DeepClone()};
    }

    // Fits a claimed job from its pinned frame in the worker: the actor's retained authority is bound to the read, which the
    // fit's discount-default lookup also uses, and the attempt is rechecked again when the job completes.
    internal static async Task<VolSurfaceFitOutcome> Fit(NpgsqlDataSource source,JsonObject job,Guid id,Guid lease,CancellationToken ct)
    {
        var request=job["request"]!.AsObject();
        if(request["engineVersion"]?.GetValue<string>()!=RiskJobs.EngineVersion)
            throw new MarketDataException("risk_engine_version","The retained job requires its exact managed engine binaries.");
        if(request["solverPolicy"]?.GetValue<string>()!=SolverPolicy||request["futuresConvention"]?.GetValue<string>() is not {} futuresConvention||
            !FuturesMarketBinding.Supported(futuresConvention))
            throw new MarketDataException("risk_job_input","The retained fit must name the supported solver policy and futures convention.");
        var model=VolModels.Require(request["model"]!.GetValue<string>());
        var product=request["product"]!.AsObject();
        var frame=RiskJobs.ReadFrame(job["frame"]??throw new JsonException("Missing fit frame."));
        if(frame.ContextId!=job["contextId"]!.GetValue<Guid>()||frame.At!=job["valuationAt"]!.GetValue<DateTimeOffset>()||frame.SwapMarketSelections is {Count:>0})
            throw new MarketDataException("risk_job_context","The queued fit and its retained frame must identify the same context.");
        var catalog=job["catalogId"]!.GetValue<Guid>();var revision=job["revisionId"]!.GetValue<Guid>();
        var manifest=new PreviewManifest("retained-vol-surface-job",catalog,revision,product["referenceCatalogId"]!.GetValue<Guid>(),
            product["referenceRevisionId"]!.GetValue<Guid>(),job["tenantId"]!.GetValue<Guid>(),job["workspaceId"]!.GetValue<Guid>(),[],null,FuturesConvention:futuresConvention);
        await using var connection=await source.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        await using(var bind=ScheduleStore.Command(connection,"SELECT risk.bind($1,$2)",id,lease))await bind.ExecuteScalarAsync(ct);
        await using var database=new PreviewDatabase(manifest,source);
        var data=await database.Read(connection,ct,observationIds:frame.ObservationIds,observationScope:ObservationReadScope.ExactPins);
        var elapsed=Stopwatch.StartNew();
        var fitted=await VolSurfaceFit.Compute(connection,database,data,frame,data.Snapshot(frame),product["code"]!.GetValue<string>(),model,ct);
        elapsed.Stop();
        await transaction.CommitAsync(ct);
        var surface=JsonSerializer.Serialize(fitted.Result,Json);
        return new(fitted.ObservationIds,surface,Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(surface))),
            fitted.Result.Series.Length,fitted.Result.Series.Count(s=>s.Series.Outcome==FuturesOptionPricing.SolvedMode),elapsed.ElapsedMilliseconds);
    }

    // Records the outcome and completes the job's attempt in one transaction.
    internal static NpgsqlCommand Finish(NpgsqlDataSource source,Guid id,Guid lease,VolSurfaceFitOutcome fit)
    {
        var command=source.CreateCommand("SELECT risk.finish_vol_surface_fit($1,$2,$3,$4::jsonb,$5,$6::jsonb)");
        command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(lease);command.Parameters.AddWithValue(fit.Inputs.ToArray());
        command.Parameters.AddWithValue(fit.Surface);command.Parameters.AddWithValue(fit.Digest);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(new{series=fit.Series,solves=fit.Solves,elapsedMs=fit.ElapsedMs}));
        return command;
    }

    // The deterministic refusals a fit job's completion can meet: each fails the job with its own code instead of being retried.
    internal static bool Refusal(string code)=>code is "vol_surface_nondeterministic" or "vol_surface_input" or "vol_surface_product" or "vol_model_unknown"
        or "historical_read_only" or "risk_job_input" or "risk_result_limit";

    internal static bool Fits(JsonObject job)=>job["request"]?["convention"]?.GetValue<string>()==Convention;

    // The exact product version: the code in the market's own reference revision.
    private static JsonObject Product(PreviewManifest manifest,string code)=>string.IsNullOrWhiteSpace(code)
        ?throw new MarketDataException("vol_surface_product","Name the option product.")
        :new(){["referenceCatalogId"]=manifest.ReferenceCatalogId,["referenceRevisionId"]=manifest.ReferenceRevisionId,["code"]=code};

    private static string BusinessDate(TradingView view)=>view.Context.EffectiveOn??throw new MarketDataException("vol_surface_context","The trading context states no business date.");

    private sealed class LatticeDecimalJsonConverter : JsonConverter<LatticeDecimal>
    {
        public override LatticeDecimal Read(ref Utf8JsonReader reader,Type typeToConvert,JsonSerializerOptions options)=>
            reader.TokenType==JsonTokenType.String?LatticeDecimal.Parse(reader.GetString()!):throw new JsonException("A lattice decimal must use decimal text.");
        public override void Write(Utf8JsonWriter writer,LatticeDecimal value,JsonSerializerOptions options)=>writer.WriteStringValue(value.ToString());
    }
}
