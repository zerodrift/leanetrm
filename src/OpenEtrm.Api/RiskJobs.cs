using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitRiskJob(Guid RequestId,FuturesRiskRequest Request);

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

    public static async Task<object> Submit(NpgsqlConnection connection,PreviewManifest manifest,FuturesRiskPreview calculator,
        PreviewData data,InputFrame frame,SubmitRiskJob body,CancellationToken ct)
    {
        if(body.RequestId==Guid.Empty||body.Request?.Context is not {KnowledgeMode:"retained",ContextId:{} contextId})
            throw new MarketDataException("risk_job_context","Queue an exact retained financial context with a stable request identity.");
        var prepared=await calculator.Prepare(connection,data,frame,body.Request,ct);
        var selected=prepared.Books.Where(b=>(body.Request.BookIds.IsEmpty||body.Request.BookIds.Contains(b.Id("id")))&&
            (body.Request.PortfolioIds.IsEmpty||body.Request.PortfolioIds.Contains(b.Id("portfolio_id"))))
            .Select(b=>b.Id("id")).Order().ToImmutableArray();
        var request=body.Request with {BookIds=selected,PortfolioIds=[],Detail=prepared.Detail==OpenEtrm.MarketData.Risk.RiskDetail.Books?"books":"holdings",
            Stress=body.Request.Stress.IsDefault?[]:body.Request.Stress};
        var payload=JsonSerializer.SerializeToNode(request,Json)!.AsObject();
        payload["engineVersion"]=EngineVersion;
        var pinnedFrame=frame with {ContextId=contextId};
        await using var command=ScheduleStore.Command(connection,
            "SELECT risk.submit($1,$2,$3,$4,$5,$6::jsonb,$7::jsonb)::text",manifest.CatalogId,body.RequestId,contextId,
            data.DefinitionRevision.RevisionId,frame.At,payload.ToJsonString(Json),JsonSerializer.Serialize(pinnedFrame,Json));
        var submitted=JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!)!.AsObject();
        var id=(submitted["id"]??submitted["jobId"])!.GetValue<Guid>();
        return await Read(connection,manifest.CatalogId,id,ct);
    }

    public static async Task<object> Read(NpgsqlConnection connection,Guid catalog,Guid id,CancellationToken ct,bool cancel=false)
    {
        if(cancel)
        {
            await using var cancellation=ScheduleStore.Command(connection,"SELECT risk.cancel($1)",id);
            await cancellation.ExecuteScalarAsync(ct);
        }
        await using var command=ScheduleStore.Command(connection,"SELECT risk.read($1)::text",id);
        var value=JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!)!.AsObject();
        return await Receipt(connection,catalog,value,ct);
    }

    public static async Task<object> List(NpgsqlConnection connection,Guid catalog,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT risk.list($1)::text",catalog);
        var values=JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!)!.AsArray();
        foreach(var value in values)await Receipt(connection,catalog,value!.AsObject(),ct);
        return values;
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
