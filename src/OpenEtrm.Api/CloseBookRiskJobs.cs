using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.Automation;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloseBookRiskDefinition(Guid CloseId,ImmutableArray<Guid> BookIds,string KnownAtPolicy,string PendingActuals,
    string CoveragePolicy,string RevisionPolicy,string PpaImplementation,PortfolioValuationPolicy ValuationPolicy);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloseBookRiskCreate(JobCreate Schedule,CloseBookRiskDefinition Definition);

public static class CloseBookRiskJobs
{
    public const string Operation="close_book_risk_v1";
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new ExactDecimalJsonConverter()}};
    public static void Validate(CloseBookRiskDefinition definition)
    {
        if(definition is null||definition.CloseId==Guid.Empty||definition.BookIds.IsDefaultOrEmpty||definition.BookIds.Length>128||
            definition.BookIds.Any(id=>id==Guid.Empty)||definition.BookIds.Distinct().Count()!=definition.BookIds.Length||
            definition.KnownAtPolicy!="valuation_instant_v1"||definition.PendingActuals is not("require_actuals_v1" or "estimate_until_actual_v1")||
            definition.CoveragePolicy!="ppa_coverage_v1"||definition.RevisionPolicy!="exact_bundle_whole_operation_v1"||definition.PpaImplementation!=PpaCapabilities.Version)
            throw new MarketDataException("pilot_close_inputs","Select an existing close, complete books and every explicit PPA policy.");
        try{PortfolioValuation.ValidatePolicy(definition.ValuationPolicy);}
        catch(MarketDataException){throw new MarketDataException("pilot_close_inputs","Select every supported valuation and symmetric bump policy explicitly.");}
    }
    public static async Task<JsonElement> Create(PreviewDatabase database,NpgsqlConnection connection,CloseBookRiskCreate request,CancellationToken ct)
    {
        if(request.Schedule is null)throw new MarketDataException("pilot_close_inputs","Supply the complete schedule.");
        (request.Schedule with {Operation="worker_check"}).Validate();Validate(request.Definition);
        if(request.Schedule.Operation!=Operation||request.Schedule.IntervalSeconds is not null)
            throw new MarketDataException("pilot_close_inputs","Existing-close risk is an explicit one-off operation.");
        // Replay returns the original admission pins; no current inputs are recaptured.
        await using(var replay=ScheduleStore.Command(connection,"SELECT automation.close_risk_replay($1::jsonb)::text",JsonSerializer.Serialize(request,Json)))
            if(await replay.ExecuteScalarAsync(ct) is string prior)return JsonSerializer.Deserialize<JsonElement>(prior);
        var close=await MarketCloses.Read(connection,database.Manifest.CatalogId,request.Definition.CloseId,ct);
        var context=(await new KnowledgeStore(close.Definition.CatalogId).Capture(connection,
            new(DateOnly.FromDateTime(close.At.UtcDateTime).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),null,"current"),ct)).Context;
        await MarketContextStore.Bind(connection,close.Definition.CatalogId,context,ct);
        await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",close.Definition.CatalogId,close.At))await bind.ExecuteNonQueryAsync(ct);
        var frame=await MarketCloses.Frame(connection,close.Definition.CatalogId,close.Id,context.ContextId,ct);
        var data=await database.Read(connection,ct,observationIds:frame.ObservationIds,referenceScope:ReferenceReadScope.TradeValuation,includeCalendarDays:true,
            observationScope:ObservationReadScope.ExactPins,ppaBundle:new(close.Definition.RevisionId,close.Reference.CatalogId,close.Reference.RevisionId));
        var preparation=await new ClosePortfolioValuation(database).Prepare(connection,data,data.Snapshot(frame),context,request.Definition.BookIds,request.Definition.ValuationPolicy,ct,request.Definition,frame);
        await using var command=ScheduleStore.Command(connection,"SELECT automation.create_close_risk_job($1::jsonb,$2,$3::jsonb,$4,$5)::text",
            JsonSerializer.Serialize(request,Json),context.ContextId!.Value,JsonSerializer.Serialize(FactorPins(preparation),Json),preparation.DeclaredRows,preparation.DeclaredRowScenarios);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    internal static object FactorPins(ClosePortfolioPreparation preparation)=>preparation.Factors.Select(f=>new {seriesId=f.Factor.Input.Series.Id,observationId=f.Factor.Observation.Id,
        quote=f.Factor.Quote.Code,bump=f.BumpSize.ToString()}).OrderBy(f=>f.seriesId).ToArray();
}
