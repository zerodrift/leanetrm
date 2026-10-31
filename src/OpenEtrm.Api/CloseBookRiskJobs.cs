using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.Automation;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloseBookRiskDefinition(Guid CloseId,ImmutableArray<Guid> BookIds,string KnownAtPolicy,string PendingActuals,
    string CoveragePolicy,string RevisionPolicy,string PpaImplementation,PortfolioValuationPolicy ValuationPolicy);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloseBookRiskCreate(JobCreate Schedule,CloseBookRiskDefinition Definition);

public sealed record ClosePortfolioPreparation(PortfolioValuation.Prepared Prepared,ImmutableArray<PortfolioCurveBump> Bumps,
    long DeclaredRows,long DeclaredRowScenarios)
{
    public ScheduleContext Context=>Prepared.Context;
    public ImmutableArray<CloseQuoteFactor> Factors=>[..Bumps.Select(bump=>new CloseQuoteFactor(bump.Axis.Quote!,bump.Size))];
}

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
        var preparation=await Prepare(database,connection,data,frame,context,request.Definition,ct);
        await using var command=ScheduleStore.Command(connection,"SELECT automation.create_close_risk_job($1::jsonb,$2,$3::jsonb,$4,$5)::text",
            JsonSerializer.Serialize(request,Json),context.ContextId!.Value,JsonSerializer.Serialize(FactorPins(preparation),Json),preparation.DeclaredRows,preparation.DeclaredRowScenarios);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    internal static async Task<ClosePortfolioPreparation> Prepare(PreviewDatabase database,NpgsqlConnection connection,PreviewData data,
        InputFrame frame,ScheduleContext context,CloseBookRiskDefinition definition,CancellationToken ct)
    {
        Validate(definition);
        if(definition.ValuationPolicy.PpaPendingActuals is {} pending&&pending!=definition.PendingActuals)
            throw new MarketDataException("pilot_close_inputs","The portfolio and close pending-actual policies must agree explicitly.");
        var exact=PinnedContext.Exact(context);
        await new KnowledgeStore(database.Manifest.CatalogId).Retained(connection,exact,ct);
        await using(var guard=ScheduleStore.Command(connection,"SELECT security.require_bound_market_context($1,$2,$3)",database.Manifest.CatalogId,exact.ContextId!.Value,frame.At))
            await guard.ExecuteNonQueryAsync(ct);
        await using(var scope=ScheduleStore.Command(connection,"SELECT automation.close_risk_scope($1,$2,$3,$4::uuid[])::text",data.DefinitionRevision.CatalogId,exact.ContextId!.Value,frame.At,definition.BookIds.ToArray()))
        {
            using var rows=JsonDocument.Parse((string)(await scope.ExecuteScalarAsync(ct))!);
            var incompatible=rows.RootElement.EnumerateArray().Where(row=>row.OptionalId("definition_revision_id")!=data.DefinitionRevision.RevisionId||
                row.OptionalId("reference_catalog_id")!=data.ReferenceRevision.CatalogId||row.OptionalId("reference_revision_id")!=data.ReferenceRevision.RevisionId||
                row.OptionalText("implementation") is null).ToArray();
            if(incompatible.Length>0)throw CloseRiskInputsException.For(incompatible,"exact_bundle_whole_operation_v1");
        }
        var prepared=await new PortfolioValuation(database).Prepare(connection,data,frame,data.Snapshot(frame),exact,definition.BookIds,
            definition.ValuationPolicy,[],[],ct,closePolicy:definition);
        RequireNativeEvidence(prepared.ConnectedMarkets);
        var bumps=prepared.Factors.Select(factor=>new PortfolioCurveBump(CurveRiskAxes.Quote(prepared,factor,ct),
            factor.TradingTick*ExactDecimal.Parse(definition.ValuationPolicy.BumpTicks.ToString(CultureInfo.InvariantCulture)))).ToImmutableArray();
        var work=prepared.Markets.Select(market=>RiskEngine.IntervalWork(market.Connected)).ToArray();
        var rowsCount=work.Sum(item=>item.Rows);var rowScenarios=work.Sum(item=>item.RowScenarios);
        CloseRiskAdmission.RequireRowScenarios(rowScenarios,bumps.Length);
        return new(prepared,bumps,rowsCount,rowScenarios);
    }
    internal static void RequireNativeEvidence(ImmutableArray<ConnectedMarketEvidence> markets)
    {
        if(markets.IsDefaultOrEmpty||markets.Any(market=>market.Recipes.IsDefault||market.Samples.IsDefault||!market.Recipes.IsEmpty||!market.Samples.IsEmpty))
            throw new MarketDataException("close_risk_source_evidence","This close operation retains native frame factor pins only; published recipes and dated source samples require their own complete retained evidence.");
    }
    internal static object FactorPins(ClosePortfolioPreparation preparation)=>preparation.Factors.Select(f=>new {seriesId=f.Factor.Input.Series.Id,observationId=f.Factor.Observation.Id,
        quote=f.Factor.Quote.Code,bump=f.BumpSize.ToString()}).OrderBy(f=>f.seriesId).ToArray();
}
