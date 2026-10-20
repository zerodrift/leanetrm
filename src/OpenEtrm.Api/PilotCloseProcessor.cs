using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Automation;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

// A non-HTTP application adapter. The primary login only claims work; a second,
// restricted runtime login binds a short-lived context for the registered
// service and exact claim. No authenticator or owner's credentials are loaded.
public sealed class PilotCloseProcessor(NpgsqlDataSource scheduler,NpgsqlDataSource runtime,PreviewManifest manifest)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new ExactDecimalJsonConverter()}};
    public async Task Process(JobClaim claim,CancellationToken ct)
    {
        if(claim.Operation!=PilotCloseJobs.Operation)throw new AutomationException("job_operation","Select an admitted pilot close occurrence.");
        await WithRun(claim,async(connection,cancellation)=>
        {
            var existing=await PreviewDatabase.Rows(connection,"SELECT row_to_json(t)::text FROM(SELECT id FROM market_data.market_closes WHERE run_id=$1)t",[claim.Id],cancellation);
            if(!existing.IsEmpty)return true;
            var definition=await Definition(connection,cancellation);
            var header=await MarketCloses.Read(connection,manifest.CatalogId,definition.PriorCloseId,cancellation);
            await using var database=new PreviewDatabase(PinnedManifest(header),runtime);
            var context=(await new KnowledgeStore(header.Definition.CatalogId).Capture(connection,
                new(Date(claim.DueAt),null,"current"),cancellation)).Context;
            await Bind(connection,header.Definition.CatalogId,context,claim.DueAt,cancellation);
            var anchor=await MarketCloses.Frame(connection,header.Definition.CatalogId,header.Id,context.ContextId,cancellation);
            var data=await database.Read(connection,cancellation,observationIds:anchor.ObservationIds,
                referenceScope:ReferenceReadScope.TradeValuation,includeCalendarDays:true,observationScope:ObservationReadScope.ExactPins);
            var contracts=data.Contracts.ToDictionary(c=>c.Key.Id);
            var members=definition.CurveCodes.ToImmutableDictionary(code=>code,code=>
            {
                if(!anchor.CurveContracts.TryGetValue(code,out var selected))throw Invalid("Every selected root curve requires exact anchor membership.");
                if(selected.Any(id=>!contracts.ContainsKey(id)))throw Invalid("Every anchored member must remain visible in the exact definition bundle.");
                var live=selected.Where(id=>contracts[id].Expiry>claim.DueAt).ToImmutableArray();
                if(live.IsEmpty)throw Invalid("Every selected root curve requires retained contracts live at the scheduled close.");
                return live;
            },StringComparer.Ordinal);
            var frame=anchor with{Id=claim.Id,At=claim.DueAt,CurveContracts=members};
            var snapshot=data.Snapshot(frame);
            var requests=members.SelectMany(p=>p.Value.Select(id=>new CurveRequest(new(data.DefinitionRevision,p.Key),new(data.DefinitionRevision,id)))).ToImmutableArray();
            var build=data.Model.Build(snapshot,requests,cancellation);
            var lineage=build.Values.SelectMany(v=>v.Lineage).ToHashSet();
            var factors=data.Model.QuoteFactors(snapshot).Where(f=>lineage.Contains(f.Observation)).ToImmutableArray();
            // Whole-book preflight occurs before any mark is written. It uses
            // exactly the same black-box adapter as the eventual report.
            var preflight=await new PortfolioValuation(database).Calculate(connection,data,snapshot,context,definition.BookIds,definition.ValuationPolicy,cancellation);
            var covered=factors.Select(f=>f.Input).ToHashSet();
            if(preflight.Factors.Any(f=>!covered.Contains(f.Factor.Input)))throw Invalid("Selected root curves must cover every market factor used by every selected book.");
            var moves=definition.LeafMoves.ToDictionary(m=>m.Curve,m=>m.MaxTicks,StringComparer.Ordinal);
            var usedLeaves=build.Evaluations.Where(e=>e.Inputs.IsEmpty).Select(e=>e.Result.Request.Curve.Code).ToHashSet(StringComparer.Ordinal);
            if(moves.Keys.Any(code=>!usedLeaves.Contains(code)))throw Invalid("Native tick policies must name only direct leaves of the selected roots.");
            var writes=factors.Select(factor=>
            {
                var policies=build.Evaluations.Where(e=>e.Inputs.IsEmpty&&e.Use==InputUse.Variable&&e.Result.Request.Contract==factor.Contract&&e.Result.Quote==factor.Quote&&e.Result.Lineage.Contains(factor.Observation))
                    .Select(e=>moves.TryGetValue(e.Result.Request.Curve.Code,out var maximum)?maximum:throw Invalid("Choose a native tick bound for each live direct leaf.")).Distinct().ToArray();
                if(policies.Length!=1||factor.Source.Code!=definition.SourceCode)throw Invalid("Aliases of one native factor require the same tick bound and exact configured synthetic source.");
                var observation=data.Observation(factor.Observation.Id);
                var source=data.References["sources"].Single(r=>r.Text("code")==factor.Source.Code);
                var price=PilotMarkGenerator.Generate(new(factor.Input.Series,factor.Observation,factor.Source,source.Text("kind"),
                    observation.Instant("observed_at"),factor.Role,factor.BaseValue,data.QuoteRows(factor.Quote.Code).Single()),claim.DueAt,definition.Seed,policies[0],definition.Generator);
                return new{seriesId=factor.Input.Series.Id,anchorObservationId=factor.Observation.Id,moveTicks=price.MoveTicks,value=price.Value.ToString()};
            }).ToImmutableArray();
            if(writes.IsEmpty)throw Invalid("The selected roots have no supported live native price factors to generate.");
            var carried=snapshot.Selections.Where(s=>s.Observation.Role==ObservationRole.Parameter||s.Input.Use==InputUse.FrozenFact)
                .Select(s=>new{seriesId=s.Input.Series.Id,observationId=s.Observation.Key.Id,inputUse=s.Input.Use==InputUse.FrozenFact?"frozen":"variable"});
            var selectedMembers=members.OrderBy(p=>p.Key,StringComparer.Ordinal).SelectMany(p=>p.Value.Select((id,index)=>new{curve=p.Key,contractId=id,ordinal=index}));
            await using var publish=ScheduleStore.Command(connection,"SELECT automation.publish_pilot_close($1::jsonb,$2::jsonb,$3::jsonb)",
                JsonSerializer.Serialize(writes,Json),JsonSerializer.Serialize(carried,Json),JsonSerializer.Serialize(selectedMembers,Json));
            var close=(Guid)(await publish.ExecuteScalarAsync(cancellation)??throw Invalid("No close publication receipt was returned."));
            if(close!=claim.Id)throw Invalid("The close receipt does not identify this exact occurrence.");
            return true;
        },ct);
        await WithRun(claim,async(connection,cancellation)=>
        {
            if(await PilotCloseReports.Exists(connection,claim.Id,cancellation))return true;
            var definition=await Definition(connection,cancellation);
            var header=await MarketCloses.Read(connection,manifest.CatalogId,claim.Id,cancellation);
            await using var database=new PreviewDatabase(PinnedManifest(header),runtime);
            await using var stamp=ScheduleStore.Command(connection,"SELECT automation.pilot_close_commit()");
            var known=new DateTimeOffset((DateTime)(await stamp.ExecuteScalarAsync(cancellation))!);
            var context=(await new KnowledgeStore(header.Definition.CatalogId).Capture(connection,
                new(Date(header.At),known.ToString("O",CultureInfo.InvariantCulture),"at"),cancellation)).Context;
            await Bind(connection,header.Definition.CatalogId,context,header.At,cancellation);
            var frame=await MarketCloses.Frame(connection,header.Definition.CatalogId,header.Id,context.ContextId,cancellation);
            var data=await database.Read(connection,cancellation,observationIds:frame.ObservationIds,
                referenceScope:ReferenceReadScope.TradeValuation,includeCalendarDays:true,observationScope:ObservationReadScope.ExactPins);
            var result=await new PortfolioValuation(database).Calculate(connection,data,data.Snapshot(frame),context,definition.BookIds,definition.ValuationPolicy,cancellation);
            _=await PilotCloseReports.Publish(connection,claim.Id,result,cancellation);
            return true;
        },ct);
    }
    private PreviewManifest PinnedManifest(MarketCloseInfo close)
    {
        if(close.Definition.CatalogId!=manifest.CatalogId)throw Invalid("This worker is configured for a different market catalog.");
        return manifest with{RevisionId=close.Definition.RevisionId,ReferenceCatalogId=close.Reference.CatalogId,ReferenceRevisionId=close.Reference.RevisionId};
    }
    private static async Task<PilotCloseDefinition> Definition(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT automation.pilot_run_definition()::text");
        return JsonSerializer.Deserialize<PilotCloseDefinition>((string)(await command.ExecuteScalarAsync(ct)??throw Invalid("No exact pilot profile is available.")),Json)
            ??throw Invalid("No exact pilot profile is available.");
    }
    private static async Task Bind(NpgsqlConnection connection,Guid catalog,ScheduleContext context,DateTimeOffset valuation,CancellationToken ct)
    {
        await MarketContextStore.Bind(connection,catalog,context,ct);
        await using var trading=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",catalog,valuation);
        await trading.ExecuteNonQueryAsync(ct);
    }
    private async Task<T> WithRun<T>(JobClaim claim,Func<NpgsqlConnection,CancellationToken,Task<T>> work,CancellationToken ct)
    {
        await using var issue=scheduler.CreateCommand("SELECT automation.issue_pilot_context($1,$2,$3)");
        issue.Parameters.AddWithValue(claim.Id);issue.Parameters.AddWithValue(claim.Fence);issue.Parameters.AddWithValue(claim.WorkerId);
        var token=(Guid)(await issue.ExecuteScalarAsync(ct)??throw Invalid("The service run context was not admitted."));
        await using var connection=await runtime.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_context($1)",token))await bind.ExecuteNonQueryAsync(ct);
        await using(var fence=ScheduleStore.Command(connection,"SELECT automation.lock_pilot_run()"))await fence.ExecuteNonQueryAsync(ct);
        var result=await work(connection,ct);
        await using(var fence=ScheduleStore.Command(connection,"SELECT automation.lock_pilot_run()"))await fence.ExecuteNonQueryAsync(ct);
        // A lost commit acknowledgement leaves the occurrence unresolved. The
        // worker never guesses failure or repeats the native facts on retry.
        await transaction.CommitAsync(ct);
        return result;
    }
    private static string Date(DateTimeOffset value)=>DateOnly.FromDateTime(value.UtcDateTime).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    private static MarketDataException Invalid(string message)=>new("pilot_close_inputs",message);
}
