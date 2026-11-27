using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.Automation;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasBillingEnrollmentSource(ImmutableArray<Guid> BookIds, string From, string To,
    string DiscoveryPolicy, string SelectionPolicy, string BillingProfile, string RoundingPolicy, string ScheduledPolicy,
    string ProcessingConvention, string KnowledgePolicy, Guid PolicyId, int PolicyRevision, string Mode,
    [property: JsonRequired] int ReadinessDelaySeconds, int PageLimit, int DispatchLimit,
    int ChildTimeoutSeconds, int ChildMaximumAttempts)
{
    public const string Discovery = "gas_billing_scope_reassessment_v1";
    internal void Validate()
    {
        var from = DateWorkspace.Date(From); var to = DateWorkspace.Date(To);
        if (BookIds.IsDefaultOrEmpty || BookIds.Length > 64 || BookIds.Any(id => id == Guid.Empty) || BookIds.Distinct().Count() != BookIds.Length ||
            from >= to || to.DayNumber - from.DayNumber > 366 || DiscoveryPolicy != Discovery ||
            SelectionPolicy is not (GasDailyQuantitySelection.BillingPolicy or GasDailyQuantitySelection.FacilityBillingPolicy) ||
            BillingProfile is not (GasCashBilling.Implementation or GasCashBilling.EntryImplementation) ||
            RoundingPolicy != GasCashBilling.RoundingImplementation || ScheduledPolicy != GasBillingReconciliation.ManualScheduled ||
            ProcessingConvention != GasBillingOperatingStore.Clock || KnowledgePolicy != LifecycleScheduleDefinition.CurrentKnowledge ||
            PolicyId == Guid.Empty || PolicyRevision < 1 || Mode is not ("review" or "automatic") || ReadinessDelaySeconds is < 0 or > 86400 ||
            PageLimit is < 1 or > 8 || DispatchLimit < 1 || DispatchLimit > PageLimit || ChildTimeoutSeconds is < 1 or > 30 || ChildMaximumAttempts is < 1 or > 3)
            throw new MarketDataException("gas_billing_enrollment", "Choose explicit books, a bounded delivery window, saved policy, mode, readiness and execution limits.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasBillingEnrollmentRequest(JobCreate Schedule, Guid EnrollmentId, [property: JsonRequired] int ExpectedRevision,
    [property: JsonRequired] Guid? JobId, [property: JsonRequired] int? ExpectedJobVersion,
    GasBillingEnrollmentSource Source);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasBillingEnrollmentPreview(GasBillingOperatingContext Context, GasBillingEnrollmentSource Source,
    [property: JsonRequired] GasBillingWorkCursor? After);

internal static class GasBillingEnrollmentJobs
{
    internal static async Task<object> Choices(NpgsqlConnection c, PreviewDatabase database, GasBillingOperatingContext request,
        ScheduleContext? selected, CancellationToken ct)
    {
        var store = new GasBillingOperatingStore(database);
        var scope = await store.Resolve(c, request, selected, ct);
        return await store.Bound(c, scope, async () =>
        {
            var books = await PreviewDatabase.Rows(c, "SELECT json_build_object('id',id,'name',name)::text FROM trading.books WHERE catalog_id=$1 ORDER BY name,id", [database.Manifest.CatalogId], ct);
            var policies = await PreviewDatabase.Rows(c, """
                SELECT row_to_json(p)::text FROM (
                    SELECT DISTINCT ON(id) id,revision,mode,from_at,until_at FROM lifecycle.action_policies
                    WHERE catalog_id=$1 AND action='gas.bill_daily' ORDER BY id,revision DESC
                )p WHERE mode IN('review','automatic') AND from_at<=$2 AND $2<until_at ORDER BY mode,id
                """, [database.Manifest.CatalogId, scope.At], ct);
            await using var permission = ScheduleStore.Command(c, "SELECT security.has_permission('RunJobs') AND security.has_permission('AmendTrade')");
            return new { context = scope.Context, books, policies = LifecycleSettingsTransport.Integers(policies, "revision"), canEnroll = await permission.ExecuteScalarAsync(ct) is true,
                discoveryPolicy = GasBillingEnrollmentSource.Discovery, processingConvention = GasBillingOperatingStore.Clock,
                knowledgePolicy = LifecycleScheduleDefinition.CurrentKnowledge, scheduledPolicy = GasBillingReconciliation.ManualScheduled,
                profiles = new[] { GasCashBilling.Implementation, GasCashBilling.EntryImplementation },
                selectionPolicies = new[] { GasDailyQuantitySelection.BillingPolicy, GasDailyQuantitySelection.FacilityBillingPolicy },
                roundingPolicy = GasCashBilling.RoundingImplementation, maximumPage = 8, maximumTimeoutSeconds = 30, maximumAttempts = 3 };
        }, ct);
    }

    internal static async Task<GasBillingWorklist> Preview(NpgsqlConnection c, PreviewDatabase database,
        GasBillingEnrollmentPreview request, ScheduleContext? selected, CancellationToken ct)
    {
        if (request.Source is null) throw Invalid();
        request.Source.Validate();
        var source = request.Source;
        var store = new GasBillingOperatingStore(database);
        var result = await store.Worklist(c, new(request.Context, source.BookIds, source.From, source.To,
            source.SelectionPolicy, source.BillingProfile, request.After, source.PageLimit), selected, ct);
        await store.Bound(c, new(result.Context, result.EvaluationAt), () => LifecycleNative.Read(c,
            "SELECT lifecycle.check_action_policy($1,$2,$3,'gas.bill_daily',$4,$5)::text", ct,
            database.Manifest.CatalogId, source.PolicyId, source.PolicyRevision, result.EvaluationAt, source.Mode == "automatic"), ct);
        return result with { Items = result.Items.Select(item => item.NativeEnd.AddSeconds(source.ReadinessDelaySeconds) > result.EvaluationAt
            ? item with { State = "not_due", Reason = "readiness_delay", Amount = null, Currency = null, SourceId = null,
                PreviousId = null, Reconciliation = null, ScheduledSourceId = null } : item).ToArray() };
    }

    internal static async Task<LifecycleScheduleWrite> Save(NpgsqlConnection c, PreviewDatabase database,
        GasBillingEnrollmentRequest request, CancellationToken ct)
    {
        if (request?.Schedule is null || request.Source is null || request.EnrollmentId == Guid.Empty || request.ExpectedRevision < 0 ||
            (request.ExpectedRevision == 0 ? request.JobId is not null || request.ExpectedJobVersion is not null
             : request.JobId is null || request.JobId == Guid.Empty || request.ExpectedJobVersion is null or < 1)) throw Invalid();
        request.Source.Validate(); request.Schedule.ValidateSchedule();
        if (request.Schedule.Operation != LifecycleScheduleJobs.Operation || request.Schedule.TimeoutSeconds > RuntimeSet.MaxReadSeconds ||
            request.Schedule.ProfileId is not null || request.Schedule.ProfileVersion is not null) throw Invalid();
        var body = JsonSerializer.SerializeToElement(request, LifecycleNative.Json);
        var value = await LifecycleNative.Read(c, "SELECT automation.save_gas_billing_enrollment($1,$2::jsonb)::text", ct,
            database.Manifest.CatalogId, body.GetRawText());
        var receipt = new LifecycleScheduleWrite(database.Manifest.CatalogId, body, value.Instant("authorityUntil"),
            JsonSerializer.SerializeToElement(new { operationId = request.Schedule.RequestId, job = value.GetProperty("outcome") }, LifecycleNative.Json));
        database.RegisterCommitAdmission(c, receipt); return receipt;
    }

    internal static async Task Process(NpgsqlConnection c, PreviewDatabase database, JsonElement retained, CancellationToken ct)
    {
        var source = retained.GetProperty("discovery").Deserialize<GasBillingEnrollmentSource>(LifecycleNative.Json) ?? throw Invalid();
        var context = retained.GetProperty("context").Deserialize<LifecycleReadContext>(LifecycleNative.Json) ?? throw Invalid();
        var inputs = await LifecycleNative.Source(c, database, context, ct);
        var operating = new GasBillingOperatingContext(source.ProcessingConvention,
            context.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), LifecycleNative.Instant(context.AsOf), "retained", inputs);
        var result = await Preview(c, database, new(operating, source,
            retained.GetProperty("after").Deserialize<GasBillingWorkCursor>(LifecycleNative.Json)), null, ct);
        var outcome = await LifecycleNative.Read(c, "SELECT automation.publish_gas_billing_page($1::jsonb)::text", ct,
            JsonSerializer.Serialize(result, LifecycleNative.Json));
        _ = await LifecycleNative.Read(c, "SELECT automation.publish_lifecycle_result('discovery',NULL,$1::jsonb)::text", ct, outcome.GetRawText());
    }
    static MarketDataException Invalid() => new("gas_billing_enrollment", "Select the explicit gas scope, saved policy, current enrollment version and execution schedule.");
}
