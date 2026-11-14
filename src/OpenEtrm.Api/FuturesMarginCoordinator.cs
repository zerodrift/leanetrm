using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FuturesMarginCorrectionDue(Guid OccurrenceId,string DueAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FuturesMarginRunRequest(Guid OperationId,Guid TradeId,Guid BindingId,int ExpectedRevision,int ExpectedMarginRevision,
    Guid ContextId,string AsOf,Guid ThroughOccurrence,string Implementation,ImmutableArray<FuturesMarginCorrectionDue> CorrectionDue,string PreviewHash);
internal sealed record FuturesMarginReview(FuturesMarginTrajectory Plan,string Hash,int TradeRevision,int MarginRevision,
    [property:JsonIgnore]Guid Catalog,[property:JsonIgnore]DateTimeOffset AuthorityUntil):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.require_futures_margin_preview($1,$2)",Catalog,AuthorityUntil);
        await command.ExecuteNonQueryAsync(ct);
    }
}

// The action coordinator accepts selection/review coordinates only. It derives
// prices, quantities and cash operands from native holdings and official facts.
internal static class FuturesMarginCoordinator
{
    internal const string Version="native_futures_margin_journal_v1";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    internal static async Task<FuturesMarginReview> Preview(NpgsqlConnection connection,PreviewDatabase database,JsonElement body,CancellationToken ct)
    {
        var request=ReadRequest(body);var catalog=database.Manifest.CatalogId;
        await using var capture=ScheduleStore.Command(connection,"SELECT settlement_data.futures_margin_preview_authority($1)",catalog);
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        var at=TradingInstant.Parse(request.AsOf,"futures_margin_input");
        var correctionDue=request.CorrectionDue.ToImmutableDictionary(d=>d.OccurrenceId,d=>TradingInstant.Parse(d.DueAt,"futures_margin_correction_due"));
        FuturesMarginReview prepared;
        await using(var savepoint=ScheduleStore.Command(connection,"SAVEPOINT futures_margin_review"))await savepoint.ExecuteNonQueryAsync(ct);
        try
        {
            var context=(await new KnowledgeStore(catalog).Read(connection,request.ContextId,ct)).Context;
            await PinnedContext.BindExact(connection,catalog,context,ct);
            await using(var trading=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",catalog,at))
                await trading.ExecuteNonQueryAsync(ct);
            await using var native=ScheduleStore.Command(connection,
                "SELECT settlement_data.read_futures_margin_state($1,$2,$3,$4,$5)::text",catalog,request.TradeId,request.BindingId,request.ContextId,at);
            var envelope=JsonSerializer.Deserialize<JsonElement>((string)(await native.ExecuteScalarAsync(ct))!);
            if(!envelope.GetProperty("available").GetBoolean())throw Error("futures_margin_unavailable","Select an exact visible adopted holding.");
            var data=await database.Read(connection,ct,selectedRevision:envelope.GetProperty("market").Id("definition_revision_id"),
                observationIds:[],includeCalendarDays:false,observationScope:ObservationReadScope.ExactPins);
            var frame=new InputFrame(Guid.NewGuid(),"Margin review",at,DateWorkspace.Date(context.EffectiveOn),[],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,request.ContextId);
            var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),request.ContextId,SchedulePreview.ParseAt(context.KnownAt));
            var store=new FuturesStore(database);var books=await store.Books(connection,ct);
            var selected=await store.ReadTrades(connection,data,books,[],[],ct,trades:ImmutableHashSet.Create(request.TradeId));
            var trade=selected.Trades.SingleOrDefault()??throw Error("futures_margin_unavailable","The adopted holding is unavailable.");
            var binding=trade.Instruments.SingleOrDefault(b=>b.Id==request.BindingId)??throw Error("futures_margin_unavailable","The held binding is unavailable.");
            var execution=FuturesExecution.Require(binding);var market=execution.MarketBinding;
            var accepted=await FuturesMarginStore.Read(connection,catalog,data,frame,snapshot,trade,binding.Id,ct);
            var policy=accepted.History.Terms.Policy;
            var required=market.Instrument.RequiredMarginOccurrences(trade,binding.Id,policy,snapshot,request.ThroughOccurrence,accepted.History);
            if(required.Any(o=>o.At<envelope.GetProperty("origin").Instant("effective_at")))
                throw Error("futures_margin_continuity","A required settlement precedes this held revision; qualify its prior-quantity continuity before applying margin.");
            var facts=await FuturesMarginSources.Read(connection,database,data,frame,context,market,policy,required,ct);
            var plan=market.Instrument.PlanMarginTrajectory(trade,binding.Id,policy,snapshot,request.ThroughOccurrence,facts,accepted.History,correctionDue);
            // Hash the selected financial target. An acknowledgement of an
            // already-recorded fact neither reapplies cash nor resets today's
            // state. Exact source identities and due choices remain reviewed.
            var review=new{version=Version,tradeId=trade.Id,trade.BookId,bindingId=binding.Id,quantity=binding.Quantity.Amount.ToString(),binding.Quantity.Specification,
                execution=execution.ExecutionPrice.Value.ToString(),execution.ExecutedAt,market.Contract.Key,policyId=policy.Id,policy.Revision,at,request.ThroughOccurrence,
                due=request.CorrectionDue.OrderBy(d=>d.OccurrenceId).Select(d=>new{d.OccurrenceId,At=correctionDue[d.OccurrenceId]}),
                steps=plan.Steps.Select(s=>new{s.Occurrence.Id,s.Occurrence.At,s.Occurrence.Phase,
                    basis=s.PreviousBasis.ToExactString(),s.Evidence.Observation,s.Evidence.KnownAt,
                    exact=s.ExactVariation.ToExactString(),total=s.Variation.Amount.ToString()})};
            var hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(review,Json))));
            prepared=new(plan,hash,accepted.TradeRevision,accepted.MarginRevision,catalog,until);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT futures_margin_review; RELEASE SAVEPOINT futures_margin_review");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        await prepared.RequireCommit(connection,ct);
        return prepared;
    }

    internal static async Task<FuturesMarginWrite> Determine(NpgsqlConnection connection,PreviewDatabase database,JsonElement body,CancellationToken ct)
    {
        var request=ReadRequest(body);var catalog=database.Manifest.CatalogId;
        await using var capture=ScheduleStore.Command(connection,"SELECT settlement_data.futures_margin_command_authority($1,'adopt')",catalog);
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        await using var status=ScheduleStore.Command(connection,"SELECT settlement_data.futures_margin_run_status($1,$2,$3::jsonb)::text",
            catalog,request.OperationId,body.GetRawText());
        if(await status.ExecuteScalarAsync(ct) is string previous)
            return new(catalog,request.OperationId,"determine",body.Clone(),until,JsonSerializer.Deserialize<JsonElement>(previous));
        var prepared=await Preview(connection,database,body,ct);
        if(request.PreviewHash!=prepared.Hash)throw Error("futures_margin_stale_review","Settlement sources or reviewed terms changed; review the exact proposal again.");
        var steps=prepared.Plan.Steps.Select(s=>new{occurrenceId=s.Occurrence.Id,previousDeterminationId=s.PreviousDetermination,
            sourceObservationId=s.Evidence.Observation.Id,previousBasis=s.PreviousBasis.ToExactString(),exactVariation=s.ExactVariation.ToExactString(),
            roundedTotal=s.Variation.Amount.ToString(),adjustment=s.Adjustment.ToExactString(),
            adjustmentDue=s.AdjustmentDue is {} due?NativeInstant(due):null,noChange=s.NoChange});
        await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.determine_futures_margin($1,$2::jsonb,$3::jsonb)::text",
            catalog,body.GetRawText(),JsonSerializer.Serialize(steps,Json));
        var outcome=JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
        return new(catalog,request.OperationId,"determine",body.Clone(),until,outcome);
    }

    private static FuturesMarginRunRequest ReadRequest(JsonElement body)
    {
        FuturesMarginRunRequest request;
        try{request=body.Deserialize<FuturesMarginRunRequest>(Json)??throw new JsonException();}
        catch(JsonException error){throw new MarketDataException("futures_margin_input","Supply the explicit supported margin selection and review fields.",error);}
        if(request.OperationId==Guid.Empty||request.TradeId==Guid.Empty||request.BindingId==Guid.Empty||request.ContextId==Guid.Empty||
            request.ThroughOccurrence==Guid.Empty||request.ExpectedRevision<=0||request.ExpectedMarginRevision<=0||request.Implementation!=Version||
            request.CorrectionDue.IsDefault||request.CorrectionDue.Any(d=>d is null||d.OccurrenceId==Guid.Empty)||
            request.CorrectionDue.Select(d=>d.OccurrenceId).Distinct().Count()!=request.CorrectionDue.Length||
            request.PreviewHash is not {Length:64}||request.PreviewHash.Any(c=>!char.IsAsciiHexDigitLower(c)))
            throw Error("futures_margin_input","Select exact scope, positive expected revisions, explicit corrections and a retained preview hash.");
        _=TradingInstant.Parse(request.AsOf,"futures_margin_input");
        return request;
    }
    private static MarketDataException Error(string code,string message)=>new(code,message);
    private static string NativeInstant(DateTimeOffset at)
    {
        if(!TradingInstant.IsSupported(at))throw Error("futures_margin_instant","Retain an exact supported UTC microsecond instant.");
        return at.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",System.Globalization.CultureInfo.InvariantCulture);
    }
}
