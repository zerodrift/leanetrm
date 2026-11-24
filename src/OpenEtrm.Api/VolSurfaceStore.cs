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

/// <summary>A fit of one option product's surface at a working frame, under an explicit vol model and option sensitivity selection.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VolSurfaceFitRequest(Guid RequestId,Guid SnapshotId,string Product,string Model,OptionSensitivityRequest Sensitivities,Guid? DefinitionRevisionId=null);

/// <summary>A read of one option product's stored surface at a trading context, under an explicit vol model.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VolSurfaceReadRequest(FuturesReadRequest Context,string Product,string Model,Guid? DefinitionRevisionId=null,string? Axis=null,JsonElement Points=default);

/// <summary>Axis metadata for explicit client selection; computes no surface coordinates.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VolSurfaceAxesRequest(FuturesReadRequest Context,string Product,string Model,Guid? DefinitionRevisionId=null);

/// <summary>A page of the surfaces stored at or before a trading context, under an explicit vol model.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VolSurfaceLibraryRequest(FuturesReadRequest Context,string Model,int Offset,string? Product=null,Guid? DefinitionRevisionId=null);

// A fitted surface as its job's completion records it: the pinned inputs, the canonical bytes and their digest, and the work done:
// the fit's series, solves and time, then the analytics' tree prices and time.
internal sealed record VolSurfaceFitOutcome(ImmutableArray<Guid> Inputs,string Surface,string Digest,int Series,int Solves,long ElapsedMs,int AnalyticsTreePrices,long AnalyticsMs);

// Volatility surfaces are fitted by jobs of kind vol_surface_fit_v1 on the retained risk-job queue and read from their stored,
// bitemporal revisions. Admission pins the frame, product, business date, model and actor; the risk worker fits and records.
internal static class VolSurfaceStore
{
    internal const string Convention="vol_surface_fit_v1";
    // The surface's canonical form: camel-case fields in the record's own order, exact decimals as text and option rights by name.
    // The stored content and its digest are these exact bytes.
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
        {Converters={new ExactDecimalJsonConverter(),new LatticeDecimalJsonConverter(),new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)}};
    // Stored projections require the fields the fit recorded, including explicitly zero coefficients and grid coordinates.
    private static readonly JsonSerializerOptions ProjectionJson=new(Json){RespectRequiredConstructorParameters=true};
    // The numerical policy a fit of a product names: that of the model the stated option valuation policy selects for
    // the product's exercise style. The stored tables accept exactly that policy for the product.
    internal static string SolverPolicy(string exerciseStyle)=>exerciseStyle switch
    {
        FuturesOptionTerms.American=>FuturesOptionValuationPolicy.Release2.NumericalPolicy,
        FuturesOptionTerms.European=>FuturesOptionValuationPolicy.Release2.European!.NumericalPolicy,
        _=>throw new MarketDataException("option_terms",$"Unsupported exercise style '{exerciseStyle}'."),
    };
    internal static void RequireSolverPolicy(string? named,string exerciseStyle)
    {
        if(named!=SolverPolicy(exerciseStyle))
            throw new MarketDataException("risk_job_input","The retained fit must name the solver policy of its product's exercise style.");
    }
    private static async Task<string> ExerciseStyle(NpgsqlConnection connection,PreviewManifest manifest,string product,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"SELECT row_to_json(p)::text FROM(SELECT exercise_style FROM ref_data.futures_option_products WHERE(catalog_id,revision_id,code)=($1,$2,$3))p",
            [manifest.ReferenceCatalogId,manifest.ReferenceRevisionId,product],ct);
        return rows.Length==1?rows[0].Text("exercise_style"):throw new MarketDataException("vol_surface_product","Select a listed option product.");
    }

    // Admits a fit job at the view's context, captured from current knowledge over the working frame.
    internal static async Task<object> Submit(NpgsqlConnection connection,PreviewDatabase database,TradingView view,VolSurfaceFitRequest body,CancellationToken ct)
    {
        if(body.RequestId==Guid.Empty)throw new MarketDataException("vol_surface_input","Supply a stable request identity for the fit.");
        VolModels.Require(body.Model);
        var selected=await SelectedInputs(connection,database,view.CalculationFrame,ct,body.DefinitionRevisionId);
        var manifest=selected.Source.Apply(database.Manifest);
        var contextId=view.Context.ContextId??throw new MarketDataException("vol_surface_context","The fit requires its captured trading context.");
        // A fit is new work: at a recorded close it requires the close in force for its session and business day (D334).
        if(view.Frame.OriginCloseId is {} close)await SelectedClose.RequireInForce(connection,manifest.CatalogId,close,ct);
        // The sensitivity selection and the clock its one calendar day is counted on are pinned here, with the fit's other inputs.
        var sensitivities=OptionSurfaceAnalytics.Pin(body.Sensitivities,view.Frame.Close is {} recorded?await CloseConventions.Read(connection,manifest.CatalogId,recorded,ct):null);
        var request=new JsonObject{["convention"]=Convention,["engineVersion"]=RiskJobs.EngineVersion,["product"]=Product(manifest,body.Product),
            ["businessDate"]=BusinessDate(view),["model"]=body.Model,["solverPolicy"]=SolverPolicy(await ExerciseStyle(connection,manifest,body.Product,ct)),
            ["futuresConvention"]=manifest.FuturesConvention,
            ["sensitivities"]=sensitivities};
        var frame=RetainedFrames.Record(selected.Frame with{SwapMarketSelections=null});
        await using var command=ScheduleStore.Command(connection,"SELECT risk.submit_vol_surface_fit($1,$2,$3,$4,$5,$6::jsonb,$7::jsonb)::text",
            manifest.CatalogId,body.RequestId,contextId,manifest.RevisionId,view.Frame.At,request.ToJsonString(),frame.ToJsonString(RiskJobs.Json));
        var job=JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!)!;
        return new{jobId=job["jobId"]!.GetValue<Guid>(),state=job["state"]!.GetValue<string>()};
    }

    // The stored surface of a key fitted at the view's own close (or working snapshot), instant and pins, known at its bound
    // context, whether its inputs were revised since, the caller's newest fit job for the key there and whether the caller may fit.
    internal static async Task<object> Read(NpgsqlConnection connection,PreviewDatabase database,TradingView view,VolSurfaceReadRequest body,CancellationToken ct)
    {
        var selection=AxisSelection(body);
        SurfaceAxes.Points(selection);
        var (manifest,key)=await Locate(connection,database,view,body.Product,body.Model,body.DefinitionRevisionId,ct);
        var stored=await Value(connection,StoredSurface,key,ct);
        var job=await Value(connection,"SELECT risk.vol_surface_fit_job($1,$2,$3::jsonb,$4,$5,$6::uuid,$7,$8)::text",key,ct);
        JsonObject? revision=null;
        if(stored is not null)
        {
            var id=stored["revision"]!.GetValue<Guid>();
            await using var revised=ScheduleStore.Command(connection,"SELECT security.vol_surface_inputs_revised($1,$2)",manifest.CatalogId,id);
            revision=new JsonObject{["id"]=id,["knownFrom"]=stored["knownFrom"]?.DeepClone(),["actor"]=stored["actor"]!.DeepClone(),
                ["inputsRevised"]=(bool)(await revised.ExecuteScalarAsync(ct))!};
        }
        var quoteRows=await PreviewDatabase.Rows(connection,"""
SELECT row_to_json(q)::text FROM ref_data.futures_option_products p JOIN ref_data.quote_specifications q
 ON(q.catalog_id,q.revision_id,q.code)=(p.catalog_id,p.revision_id,p.quote_code)
 WHERE(p.catalog_id,p.revision_id,p.code)=($1,$2,$3)
""",[manifest.ReferenceCatalogId,manifest.ReferenceRevisionId,body.Product],ct);
        var quote=quoteRows.Single();var reference=new ReferenceRevision(manifest.ReferenceCatalogId,manifest.ReferenceRevisionId);
        var priceUnit=new PriceUnit(new(reference,quote.Text("currency_code")),new(reference,quote.Text("quantity_code")),quote.Number("multiplier"));
        var labels=await ReferenceDisplay.Read(connection,[priceUnit],ct);
        var canFit=await PreviewDatabase.HasPermission(connection,"PublishMarketData",ct)&&await PreviewDatabase.HasPermission(connection,"ReadTrades",ct);
        return new JsonObject{["snapshotId"]=view.Frame.Id,["definitionRevisionId"]=manifest.RevisionId,["model"]=body.Model,
            ["referenceRevision"]=JsonSerializer.SerializeToNode(reference,Json),["priceUnit"]=labels.Price(priceUnit),
            ["priceUnitReference"]=JsonSerializer.SerializeToNode(new{priceUnit.Currency,priceUnit.Quantity,multiplier=priceUnit.Multiplier.ToString()},Json),
            ["solverPolicy"]=stored?["solverPolicy"]?.DeepClone()??SolverPolicy(await ExerciseStyle(connection,manifest,body.Product,ct)),["state"]=stored is null?"notFitted":"fitted",
            ["revision"]=revision,["job"]=job,["canFit"]=canFit,["surface"]=stored is null?null:ProjectContent(stored["content"],selection),
            // What a fit may select: offered for the page to send back, with the clocks a working snapshot may name.
            ["sensitivities"]=new JsonObject{["offered"]=JsonSerializer.SerializeToNode(OptionSurfaceAnalytics.Offered,RiskJobs.Json),
                ["clocks"]=JsonSerializer.SerializeToNode(TradingPnlReport.Clocks(),RiskJobs.Json),["clockRequired"]=view.Frame.Close is null}};
    }

    internal static SurfaceAxisRequest AxisSelection(VolSurfaceReadRequest body)
    {
        if(body.Points.ValueKind!=JsonValueKind.Array||body.Points.EnumerateArray().Any(p=>p.ValueKind!=JsonValueKind.String))
            throw new MarketDataException("vol_axis_points","Supply an explicit array of exact decimal point strings.");
        return new(body.Axis,body.Points.EnumerateArray().Select(p=>p.GetString()!).ToArray());
    }

    internal static string Encode(VolSurfaceResult surface)=>JsonSerializer.Serialize(surface,Json);
    private static VolSurfaceResult? DecodeProjectionInputs(JsonNode? content)
    {
        try{return content?.Deserialize<VolSurfaceResult>(ProjectionJson);}
        catch(JsonException){return null;}
        catch(MarketDataException error)when(error.Code is "numeric_format" or "numeric_range"){return null;}
    }
    internal static JsonNode ProjectContent(JsonNode? content,SurfaceAxisRequest selection)=>
        JsonSerializer.SerializeToNode(SurfaceAxes.Project(DecodeProjectionInputs(content)??
            throw new MarketDataException("vol_axis_inputs_missing","The stored fit lacks its recorded projection inputs. Refit explicitly."),selection),Json)!;

    internal static async Task<object> Axes(NpgsqlConnection connection,PreviewDatabase database,TradingView view,VolSurfaceAxesRequest body,CancellationToken ct)
    {
        var (manifest,key)=await Locate(connection,database,view,body.Product,body.Model,body.DefinitionRevisionId,ct);
        var stored=await Value(connection,StoredSurface,key,ct);
        var surface=DecodeProjectionInputs(stored?["content"]);
        var declared=SurfaceAxes.TryDeclare(surface,out var axes);
        var requiresRefit=stored is not null && !declared;
        if(!declared)
        {
            var source=await ProductSource(connection,database,view.CalculationFrame,ct,body.DefinitionRevisionId);
            var data=await database.Read(connection,ct,ppaBundle:source,observationIds:[],includeCalendarDays:false);
            if(!data.ReferenceRows("futures_option_products","code",body.Product).Any())
                throw new MarketDataException("vol_surface_product","Select a listed option product.");
            var listed=data.ReferenceRows("futures_option_products","code",body.Product).Single();
            axes=SurfaceAxes.Declare(new(body.Model,[],[],[]){PlotInputs=new(VolSurfaceFit.StrikeUnit(data,listed),SolverPolicy(listed.Text("exercise_style")),SurfaceAxes.NumericalPolicy,NormalDistribution.ImplementationId)});
        }
        var job=await Value(connection,"SELECT risk.vol_surface_fit_job($1,$2,$3::jsonb,$4,$5,$6::uuid,$7,$8)::text",key,ct);
        return new{snapshotId=view.Frame.Id,definitionRevisionId=manifest.RevisionId,model=body.Model,
            initialAxis=SurfaceAxes.LogMoneyness,axes=JsonSerializer.SerializeToNode(axes,Json),requiresRefit,job,
            canFit=await PreviewDatabase.HasPermission(connection,"PublishMarketData",ct)&&await PreviewDatabase.HasPermission(connection,"ReadTrades",ct),
            sensitivities=new{offered=OptionSurfaceAnalytics.Offered,clocks=TradingPnlReport.Clocks(),clockRequired=view.Frame.Close is null}};
    }

    // The stored surface itself for a product at the view's location, or none: what another read scales or draws from.
    internal static async Task<VolSurfaceResult?> Surface(NpgsqlConnection connection,PreviewDatabase database,TradingView view,string product,string model,
        Guid? definitionRevisionId,CancellationToken ct)=>
        (await Value(connection,StoredSurface,(await Locate(connection,database,view,product,model,definitionRevisionId,ct)).Key,ct))?["content"].Deserialize<VolSurfaceResult>(Json);

    private const string StoredSurface="SELECT security.read_vol_surface($1,$2,$3::jsonb,$4,$5,$6::uuid,$7,$8)::text";
    // The key a product's surface and its fit job are read by at a view: the exact product version, the business date and the
    // model, at the close the frame is (or none), its valuation instant and its pins.
    private static async Task<(PreviewManifest Manifest,object[] Key)> Locate(NpgsqlConnection connection,PreviewDatabase database,TradingView view,string product,string model,
        Guid? definitionRevisionId,CancellationToken ct)
    {
        VolModels.Require(model);
        var selected=await SelectedInputs(connection,database,view.CalculationFrame,ct,definitionRevisionId);
        var manifest=selected.Source.Apply(database.Manifest);
        var (close,at,pins)=Location(selected.Frame);
        return(manifest,[manifest.CatalogId,manifest.RevisionId,Product(manifest,product).ToJsonString(),
            DateOnly.ParseExact(BusinessDate(view),"yyyy-MM-dd",CultureInfo.InvariantCulture),model,close,at,pins]);
    }
    private static async Task<JsonNode?> Value(NpgsqlConnection connection,string sql,object[] arguments,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,sql,arguments);
        return await command.ExecuteScalarAsync(ct) is string text?JsonNode.Parse(text):null;
    }

    // The surfaces stored at or before the view's bound context, the view's own first and then newest business date first, each
    // with the close it belongs to, and whether the caller may fit, a page at a time, for every product or one.
    internal static async Task<object> Library(NpgsqlConnection connection,PreviewDatabase database,TradingView view,VolSurfaceLibraryRequest body,CancellationToken ct)
    {
        VolModels.Require(body.Model);
        var source=await ProductSource(connection,database,view.CalculationFrame,ct,body.DefinitionRevisionId);
        var manifest=source.Apply(database.Manifest);
        var (close,at,pins)=Location(view.CalculationFrame);
        try
        {
            (_,_,pins)=Location(await ProjectInputs(connection,database,view.CalculationFrame,source,ct));
        }
        catch(MarketDataException error)when(error.Code=="market_inputs_missing")
        {
            // A library can list fits from other closes even when this close
            // has no inputs for this source. No fit can match an empty pin set.
            pins=[];
        }
        await using var command=ScheduleStore.Command(connection,"SELECT security.vol_surface_library($1,$2,$3,$4,$5::text,$6,$7::uuid,$8,$9,$10,$11)::text",
            manifest.CatalogId,manifest.RevisionId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId,(object?)body.Product??DBNull.Value,body.Model,close,at,pins,body.Offset,LibraryPage);
        var page=JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!)!.AsObject();
        var canFit=await PreviewDatabase.HasPermission(connection,"PublishMarketData",ct)&&await PreviewDatabase.HasPermission(connection,"ReadTrades",ct);
        return new JsonObject{["snapshotId"]=view.Frame.Id,["definitionRevisionId"]=manifest.RevisionId,["model"]=body.Model,["product"]=body.Product,["offset"]=body.Offset,["pageSize"]=LibraryPage,
            ["surfaces"]=page["surfaces"]!.DeepClone(),["hasMore"]=page["hasMore"]!.DeepClone(),["canFit"]=canFit};
    }
    // The product directory and calibration select the same published reference
    // role. A recorded close supplies the exact pins of that role's definition.
    internal static async Task<(CatalogueSource Source,InputFrame Frame)> SelectedInputs(
        NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,CancellationToken ct,Guid? definitionRevisionId=null)
    {
        var source=await ProductSource(connection,database,frame,ct,definitionRevisionId);
        return(source,await ProjectInputs(connection,database,frame,source,ct));
    }

    internal static async Task<CatalogueSource> ProductSource(
        NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,CancellationToken ct,Guid? definitionRevisionId)
    {
        if(definitionRevisionId is {} exact)
            return await MarketInputSelection.ExactSource(database,connection,exact,ct);
        var catalogue=await new CatalogueStore(database.Manifest.CatalogId).ReadBound(connection,ct,frame.Time.Date);
        return catalogue.RequireRole("reference:futures_option_products");
    }

    private static async Task<InputFrame> ProjectInputs(
        NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,CatalogueSource source,CancellationToken ct)
    {
        var data=await database.Read(connection,ct,ppaBundle:source,observationIds:[],includeCalendarDays:false);
        return await MarketInputSelection.FrameFor(database,connection,data,frame,ct);
    }

    private const int LibraryPage=48;

    // Where the view reads. A frame id is session-scoped, so a surface is located by the close the frame is, or for a working
    // snapshot by none, and its valuation instant; a surface is the frame's only when every input is one of its pins, since
    // working snapshots of one instant can pin different observations.
    private static (object Close,DateTimeOffset At,Guid[] Pins) Location(InputFrame frame)=>
        ((object?)frame.OriginCloseId??DBNull.Value,frame.At,[..frame.ObservationIds]);

    // Fits a claimed job from its pinned frame in the worker: the actor's retained authority is bound to the read, which the
    // fit's discount-default lookup also uses, and the attempt is rechecked again when the job completes.
    internal static async Task<VolSurfaceFitOutcome> Fit(NpgsqlDataSource source,JsonObject job,Guid id,Guid lease,CancellationToken ct)
    {
        var request=job["request"]!.AsObject();
        if(request["engineVersion"]?.GetValue<string>()!=RiskJobs.EngineVersion)
            throw new MarketDataException("risk_engine_version","The retained job requires its exact managed engine binaries.");
        if(request["futuresConvention"]?.GetValue<string>() is not {} futuresConvention||!FuturesMarketBinding.Supported(futuresConvention))
            throw new MarketDataException("risk_job_input","The retained fit must name the supported solver policy and futures convention.");
        var model=VolModels.Require(request["model"]!.GetValue<string>());
        var product=request["product"]!.AsObject();
        var frame=RetainedFrames.Read(job["frame"]??throw new JsonException("Missing fit frame."));
        if(frame.ContextId!=job["contextId"]!.GetValue<Guid>()||frame.At!=job["valuationAt"]!.GetValue<DateTimeOffset>()||frame.SwapMarketSelections is {Count:>0})
            throw new MarketDataException("risk_job_context","The queued fit and its retained frame must identify the same context.");
        var catalog=job["catalogId"]!.GetValue<Guid>();var revision=job["revisionId"]!.GetValue<Guid>();
        var manifest=new PreviewManifest("retained-vol-surface-job",catalog,revision,product["referenceCatalogId"]!.GetValue<Guid>(),
            product["referenceRevisionId"]!.GetValue<Guid>(),job["tenantId"]!.GetValue<Guid>(),job["workspaceId"]!.GetValue<Guid>(),[],null,FuturesConvention:futuresConvention);
        await using var connection=await source.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        await using(var bind=ScheduleStore.Command(connection,"SELECT risk.bind($1,$2)",id,lease))await bind.ExecuteScalarAsync(ct);
        await using var database=new PreviewDatabase(manifest,source);
        using var admissions=database.OwnReadTransaction(connection,transaction);
        var data=await RetainedFrames.ReadBoundJobInputs(database,connection,frame,revision,ReferenceReadScope.DefinitionClosure,ct);
        // The policy the job names is the one of its product's exercise style, read from the job's own pinned definitions.
        if(data.ReferenceRows("futures_option_products","code",product["code"]!.GetValue<string>()).ToArray() is [var listedProduct])
            RequireSolverPolicy(request["solverPolicy"]?.GetValue<string>(),listedProduct.Text("exercise_style"));
        var plan=OptionSurfaceAnalytics.Plan(request["sensitivities"],frame.At);
        // Calibration and analytics value under the job's one certified context, as a portfolio valuation does.
        var context=PinnedContext.Exact((await new KnowledgeResolver(catalog).Read(connection,frame.ContextId!.Value,ct)).Context);
        var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),frame.ContextId.Value,SchedulePreview.ParseAt(context.KnownAt));
        var elapsed=Stopwatch.StartNew();
        var fitted=await VolSurfaceFit.Compute(connection,database,data,frame,snapshot,product["code"]!.GetValue<string>(),model,ct);
        elapsed.Stop();
        var analysing=Stopwatch.StartNew();
        var analysed=await OptionSurfaceAnalytics.Attach(connection,database,data,frame,snapshot,context,fitted,plan,ct);
        analysing.Stop();
        await admissions.Commit(ct);
        var surface=Encode(analysed.Surface);
        return new(analysed.ObservationIds,surface,Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(surface))),
            fitted.Result.Series.Length,fitted.Result.Series.Count(s=>s.Series.Outcome==FuturesOptionPricing.SolvedMode),elapsed.ElapsedMilliseconds,
            analysed.TreePrices,analysing.ElapsedMilliseconds);
    }

    // Records the outcome and completes the job's attempt in one transaction.
    internal static NpgsqlCommand Finish(NpgsqlDataSource source,Guid id,Guid lease,VolSurfaceFitOutcome fit)
    {
        var command=source.CreateCommand("SELECT risk.finish_vol_surface_fit($1,$2,$3,$4::jsonb,$5,$6::jsonb)");
        command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(lease);command.Parameters.AddWithValue(fit.Inputs.ToArray());
        command.Parameters.AddWithValue(fit.Surface);command.Parameters.AddWithValue(fit.Digest);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(new{series=fit.Series,solves=fit.Solves,elapsedMs=fit.ElapsedMs,
            analyticsTreePrices=fit.AnalyticsTreePrices,analyticsMs=fit.AnalyticsMs}));
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
