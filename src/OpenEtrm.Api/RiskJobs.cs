using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitPortfolioRiskJob(Guid RequestId,PortfolioRiskRequest Request);

public static class RiskJobs
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new ExactDecimalJsonConverter() } };
    // A worker must execute the same managed implementation admitted by the API.
    // Rolling out different binaries never silently changes an already queued run.
    private static readonly Lazy<string> engine = new(() =>
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var assembly in new[]{typeof(RiskJobs).Assembly,typeof(MarketSnapshot).Assembly,
            typeof(OpenEtrm.Shared.Temporal.TemporalException).Assembly,typeof(OpenEtrm.Rates.ZeroRateDefinition).Assembly}
            .Distinct().OrderBy(a=>a.GetName().Name,StringComparer.Ordinal))
            hash.AppendData(File.ReadAllBytes(assembly.Location));
        return "risk_job_v1:"+Convert.ToHexStringLower(hash.GetHashAndReset());
    });
    internal static string EngineVersion=>engine.Value;

    public static async Task<object> Submit(NpgsqlConnection connection,PreviewManifest manifest,PortfolioRiskPreview calculator,
        PreviewData data,InputFrame frame,SubmitPortfolioRiskJob body,CancellationToken ct)
    {
        if(body.RequestId==Guid.Empty||body.Request?.Context is not {KnowledgeMode:"retained",ContextId:{} contextId})
            throw new MarketDataException("risk_job_context","Queue an exact retained financial context with a stable request identity.");
        var prepared=await calculator.Prepare(connection,data,frame,body.Request,ct);
        var request=prepared.Request with {BookIds=prepared.BookIds,PortfolioIds=[],
            Detail=prepared.Detail==OpenEtrm.MarketData.Risk.RiskDetail.Books?"books":"holdings",
            Stress=prepared.Request.Stress};
        var payload=JsonSerializer.SerializeToNode(request,Json)!.AsObject();
        payload["engineVersion"]=EngineVersion;
        var supplemental=new JsonObject();
        foreach(var market in prepared.SupplementalMarkets.OrderBy(m=>m.Data.DefinitionRevision.RevisionId))
            supplemental.Add(market.Data.DefinitionRevision.RevisionId.ToString(),PinFrame(market.Frame with {ContextId=contextId}));
        var pinnedFrame=PinFrame(frame with {ContextId=contextId,SwapMarketSelections=prepared.SupplementalMarkets
            .ToImmutableDictionary(m=>m.Data.DefinitionRevision.RevisionId,m=>m.Frame.ObservationIds)});
        pinnedFrame["supplementalMarkets"]=supplemental;
        pinnedFrame["connectedMarkets"]=JsonSerializer.SerializeToNode(prepared.Portfolio.ConnectedMarkets,Json);
        pinnedFrame["scenarioAxes"]=JsonSerializer.SerializeToNode(CurveRiskAxes.Retained(prepared),Json);
        // The worker leaves out exactly the positions this submission left out (D218).
        pinnedFrame["excludedPositions"]=JsonSerializer.SerializeToNode(prepared.Portfolio.Excluded,Json);
        await using var command=ScheduleStore.Command(connection,
            "SELECT risk.submit($1,$2,$3,$4,$5,$6::jsonb,$7::jsonb)::text",manifest.CatalogId,body.RequestId,contextId,
            data.DefinitionRevision.RevisionId,frame.At,payload.ToJsonString(Json),pinnedFrame.ToJsonString(Json));
        var submitted=JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!)!.AsObject();
        return await Read(connection,manifest.CatalogId,(submitted["id"]??submitted["jobId"])!.GetValue<Guid>(),ct);
    }

    internal static JsonObject PinFrame(InputFrame frame)
    {
        var pinned=JsonSerializer.SerializeToNode(frame,Json)!.AsObject();
        // Public frame JSON omits input uses; the retained execution envelope
        // carries them explicitly so a frozen fact never becomes shockable.
        pinned["frozenObservationIds"]=JsonSerializer.SerializeToNode(
            frame.FrozenObservationIds.IsDefault?ImmutableArray<Guid>.Empty:frame.FrozenObservationIds,Json);
        return pinned;
    }

    internal static InputFrame ReadFrame(JsonNode pinned)
    {
        var frame=pinned.Deserialize<InputFrame>(Json)??throw new JsonException("Missing risk frame.");
        var frozen=pinned["frozenObservationIds"]?.Deserialize<ImmutableArray<Guid>>(Json);
        if(frozen is null||frozen.Value.IsDefault||frozen.Value.Distinct().Count()!=frozen.Value.Length||
            frozen.Value.Any(id=>!frame.ObservationIds.Contains(id)))
            throw new MarketDataException("risk_job_context","Frozen inputs must be an explicit exact subset of the retained observations.");
        return frame with {FrozenObservationIds=frozen.Value};
    }

    public static async Task<object> Read(NpgsqlConnection connection,Guid catalog,Guid id,CancellationToken ct,bool cancel=false,string? expectedConvention=null)
    {
        async Task<JsonObject> ReadValue()
        {
            await using var command=ScheduleStore.Command(connection,"SELECT risk.read($1)::text",id);
            var value=JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!)!.AsObject();
            if(expectedConvention is not null&&value["convention"]?.GetValue<string>()!=expectedConvention)
                throw new MarketDataException("risk_job_convention","This job belongs to a different retained risk operation.");
            return value;
        }
        var value=await ReadValue();
        if(cancel)
        {
            await using var cancellation=ScheduleStore.Command(connection,"SELECT risk.cancel($1)",id);
            await cancellation.ExecuteScalarAsync(ct);
            value=await ReadValue();
        }
        var receipt=await Receipt(connection,catalog,value,ct);
        await RequireCurrentAccess(connection,catalog,ct);
        return receipt;
    }

    public static async Task<object> List(NpgsqlConnection connection,Guid catalog,CancellationToken ct,string? convention=null)
    {
        await using var command=convention is null?ScheduleStore.Command(connection,"SELECT risk.list($1)::text",catalog):
            ScheduleStore.Command(connection,"SELECT risk.list($1,$2)::text",catalog,convention);
        var values=JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!)!.AsArray();
        foreach(var value in values)await Receipt(connection,catalog,value!.AsObject(),ct);
        await RequireCurrentAccess(connection,catalog,ct);
        return values;
    }

    private static async Task RequireCurrentAccess(NpgsqlConnection connection,Guid catalog,CancellationToken ct)
    {
        await using var check=ScheduleStore.Command(connection,
            "SELECT security.trading_access($1,'ReadTrades') AND security.market_access($1,'ReadMarketData') AND security.has_permission('RunScenario')",catalog);
        if(!Equals(await check.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
    }

    private static async Task<JsonObject> Receipt(NpgsqlConnection connection,Guid catalog,JsonObject value,CancellationToken ct)
    {
        if(value["id"] is null)value["id"]=value["jobId"]?.DeepClone();
        var contextId=value["contextId"]!.GetValue<Guid>();
        var context=await new KnowledgeStore(catalog).Read(connection,contextId,ct);
        value["context"]=JsonSerializer.SerializeToNode(context.Context,Json);
        value["asOf"]=value["valuationAt"]?.DeepClone();
        var code=value["errorCode"]?.GetValue<string>();
        var message=code switch
        {
            "risk_result_limit"=>"The report exceeds the retained result limit. Select book totals or a smaller explicit scope for a new run.",
            "risk_input_timeout"=>"Loading the retained inputs exceeded the worker's authority window. Review worker and database capacity before retrying.",
            "risk_timeout"=>"The calculation exceeded the configured worker timeout. Review the workload and worker limits before submitting a new run.",
            "risk_engine_version"=>"This run requires the exact engine version captured at submission.",
            "risk_job_authority_changed"=>"Authority changed after submission. Review current access before submitting a new run.",
            "price_domain"=>"A stressed or bumped quote is outside its permitted price range. Review the explicit shifts and bump sizes.",
            _=>"The risk job could not complete under its retained inputs and current authority."
        };
        value["error"]=code is null?null:JsonSerializer.SerializeToNode(new {code,message},Json);
        return value;
    }
}
