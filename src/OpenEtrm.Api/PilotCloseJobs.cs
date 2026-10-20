using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.Automation;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PilotLeafMove(string Curve,int MaxTicks);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PilotCloseDefinition(Guid PriorCloseId,ImmutableArray<Guid> BookIds,ImmutableArray<string> CurveCodes,
    string SourceCode,[property:JsonRequired]int Seed,ImmutableArray<PilotLeafMove> LeafMoves,string Generator,string TickBasis,
    string ParameterPolicy,string FrozenPolicy,string MembershipPolicy,string CalendarPolicy,string ClosePolicy,
    PortfolioValuationPolicy ValuationPolicy);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PilotCloseJobCreate(JobCreate Schedule,PilotCloseDefinition Definition);

public static class PilotCloseJobs
{
    public const string Operation="generated_pilot_close_v1";
    public const string Name="Generated pilot close — futures execution P&L, swap PV and open risk";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal static readonly string[] Permissions=["RunJobs","ReadMarketData","ReadTrades","RunValuation","RunScenario","ImportObservations","PublishMarketData"];
    public static PortfolioValuationPolicy QualifiedPolicy=>new(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesRisk.Implementation,
        SwapStore.Convention,RateConventions.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1);

    public static async Task<object> Catalog(PreviewDatabase database,NpgsqlConnection connection,PinnedContext pin,
        ImmutableArray<InputFrame> frames,CancellationToken ct)
    {
        if(pin.Context is not null)throw new MarketDataException("historical_read_only","Configure generated pilot jobs in the current workspace.");
        foreach(var permission in Permissions)if(!await PreviewDatabase.HasPermission(connection,permission,ct))throw new PreviewDeniedException();
        var data=await database.Read(connection,ct,includeCalendarDays:false);
        var closeRows=await PreviewDatabase.Rows(connection,"SELECT row_to_json(t)::text FROM(SELECT id,name,at,kind FROM market_data.market_closes WHERE catalog_id=$1 AND definition_revision_id=$2 ORDER BY at DESC,id LIMIT 128)t",[database.Manifest.CatalogId,database.Manifest.RevisionId],ct);
        await using var clock=ScheduleStore.Command(connection,"SELECT clock_timestamp()");
        var observed=new DateTimeOffset((DateTime)(await clock.ExecuteScalarAsync(ct))!);
        var current=(await new KnowledgeStore(database.Manifest.CatalogId).Capture(connection,
            new(DateOnly.FromDateTime(observed.UtcDateTime).ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),null,"current"),ct)).Context;
        await MarketContextStore.Bind(connection,database.Manifest.CatalogId,current,ct);
        await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",database.Manifest.CatalogId,observed))await bind.ExecuteNonQueryAsync(ct);
        var books=await PreviewDatabase.Rows(connection,"SELECT row_to_json(t)::text FROM(SELECT id,name FROM trading.books ORDER BY name,id)t",[],ct);
        // This named current-directory read captures its own certified current
        // view. It does not alter the user's selected session context or anchor.
        var definitions=data.Definitions["curves"];
        string[] Leaves(string curve,HashSet<string> path)
        {
            if(!path.Add(curve))throw new MarketDataException("pilot_profile","Curve dependencies are cyclic.");
            var row=definitions.Single(p=>p.Text("code")==curve);
            var result=row.Text("rule")=="direct"?[curve]:data.Definitions["curve_inputs"].Where(p=>p.Text("curve_code")==curve&&p.TryGetProperty("source_curve_code",out var value)&&value.ValueKind==JsonValueKind.String)
                .SelectMany(p=>Leaves(p.Text("source_curve_code"),new(path,StringComparer.Ordinal))).Distinct(StringComparer.Ordinal).ToArray();
            return result;
        }
        object Choice(string value,string label)=>new{value,label};
        var result=new{operation=Operation,operationName=Name,canCreate=true,
            metadataContext="current_working_exact_bundle",model=data.DefinitionRevision,reference=data.ReferenceRevision,bookContext=current,
            priorCloses=closeRows.Select(r=>new{id=r.Id("id"),name=r.Text("name"),at=r.Instant("at"),kind=r.Text("kind")}).ToImmutableArray(),
            anchorFrames=frames.Select(f=>new{f.Id,f.Name,f.At}).ToImmutableArray(),
            books,curves=definitions.Select(r=>new{code=r.Text("code"),name=r.Text("code"),leafCurves=Leaves(r.Text("code"),new(StringComparer.Ordinal))}).ToImmutableArray(),
            sources=data.References["sources"].Where(r=>r.Text("kind")=="synthetic").Select(r=>new{code=r.Text("code"),name=r.Text("code")}).ToImmutableArray(),
            policies=new{
                generator=new[]{Choice(PilotMarkGenerator.Implementation,"Deterministic seeded clearing-increment moves (simulation)")},
                tickBasis=new[]{Choice("clearing_increment","Native clearing increment")},
                parameterPolicy=new[]{Choice("carry_exact_anchor_v1","Retain exact anchor parameters")},
                frozenPolicy=new[]{Choice("carry_exact_anchor_frozen_facts_v1","Retain exact anchor final settlements and published index fixings")},
                membershipPolicy=new[]{Choice("retained_live_members_v1","Retain explicitly anchored contracts still live at the close")},
                calendarPolicy=new[]{Choice("every_utc_calendar_day_v1","Every UTC calendar day — simulation, not an exchange calendar")},
                closePolicy=new[]{Choice("utc_due_instant_v1","Scheduled UTC due instant")}},
            valuationPolicyChoices=new[]{new{value="mixed_futures_swaps_exact_v1",label="Futures execution P&L and swap PV; exact shared-scenario risk",policy=QualifiedPolicy}},
            scheduleChoices=new[]{new{value="once",label="Once",intervalSeconds=(int?)null},new{value="daily",label="Every UTC calendar day",intervalSeconds=(int?)86400}},
            schedulePolicy=new{timeZone="UTC",misfire="run_once",overlap="forbid"},
            fields=new{seed=new{minimum=0,maximum=int.MaxValue},maximumTicks=new{minimum=1,maximum=PilotMarkGenerator.MaximumTickMove},bumpTicks=new{minimum=1,maximum=100},
                timeoutSeconds=new{minimum=1,maximum=3600},maximumAttempts=new{minimum=1,maximum=3}},
            warning="Generated prices are synthetic pilot data. Futures execution P&L is not settlement PV or variation margin. Every selected book is evaluated in full; unsupported positions or missing inputs reject the occurrence."};
        foreach(var permission in Permissions)if(!await PreviewDatabase.HasPermission(connection,permission,ct))throw new PreviewDeniedException();
        await using var scope=ScheduleStore.Command(connection,"SELECT security.market_access($1,'ReadMarketData') AND security.reference_access($2,'ReadMarketData')",data.DefinitionRevision.CatalogId,data.ReferenceRevision.CatalogId);
        if(!Equals(await scope.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        return result;
    }

    public static async Task<JsonElement> Create(NpgsqlConnection connection,PilotCloseJobCreate request,CancellationToken ct)
    {
        var schedule=request.Schedule;var definition=request.Definition;
        if(schedule is null||definition is null)throw Invalid();
        // Reuse the common schedule validation; the business operation has its
        // own exact typed profile rather than a Swarm execution profile.
        (schedule with{Operation="worker_check"}).Validate();
        if(schedule.Operation!=Operation||schedule.ProfileId is not null||schedule.ProfileVersion is not null||schedule.IntervalSeconds is not(null or 86400)||schedule.DueAt.Ticks%10!=0||schedule.ExpiresAt.Ticks%10!=0||schedule.DueAt.Year is <1901 or >2199||
            definition.PriorCloseId==Guid.Empty||definition.BookIds.IsDefaultOrEmpty||definition.BookIds.Length>128||definition.BookIds.Any(id=>id==Guid.Empty)||definition.BookIds.Distinct().Count()!=definition.BookIds.Length||
            definition.CurveCodes.IsDefaultOrEmpty||definition.CurveCodes.Length>128||definition.CurveCodes.Distinct(StringComparer.Ordinal).Count()!=definition.CurveCodes.Length||
            string.IsNullOrWhiteSpace(definition.SourceCode)||definition.Seed<0||definition.LeafMoves.IsDefaultOrEmpty||definition.LeafMoves.Length>128||
            definition.LeafMoves.Any(m=>m is null||string.IsNullOrWhiteSpace(m.Curve)||m.MaxTicks is <1 or >PilotMarkGenerator.MaximumTickMove)||
            definition.LeafMoves.Select(m=>m.Curve).Distinct(StringComparer.Ordinal).Count()!=definition.LeafMoves.Length||
            definition.Generator!=PilotMarkGenerator.Implementation||definition.TickBasis!="clearing_increment"||definition.ParameterPolicy!="carry_exact_anchor_v1"||
            definition.FrozenPolicy!="carry_exact_anchor_frozen_facts_v1"||definition.MembershipPolicy!="retained_live_members_v1"||definition.CalendarPolicy!="every_utc_calendar_day_v1"||definition.ClosePolicy!="utc_due_instant_v1"||
            definition.ValuationPolicy is null)throw Invalid();
        PortfolioValuation.ValidatePolicy(definition.ValuationPolicy);
        await using var command=ScheduleStore.Command(connection,"SELECT automation.create_pilot_close_job($1::jsonb,$2::jsonb)::text",JsonSerializer.Serialize(schedule,Json),JsonSerializer.Serialize(definition,Json));
        using var document=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct)??throw Invalid()));
        return document.RootElement.Clone();
    }
    private static MarketDataException Invalid()=>new("pilot_profile","Select complete explicit pilot policies, anchor, books, curves, seed and bounded native tick moves.");
}
