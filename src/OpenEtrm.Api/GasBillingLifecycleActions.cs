using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GasBillingActionSelection(GasBillingSelection Billing, string ProcessingConvention, string ScheduledPolicy,
    Guid SourceId, [property: JsonRequired] Guid? ExpectedPreviousId, [property: JsonRequired] string? CorrectionDueAt,
    [property: JsonRequired] string? Reason, string Evidence);

// Scheduling and operator actions share the daily billing producer. This adapter
// adds policy admission and a lifecycle receipt; it owns no cash arithmetic.
internal sealed class GasBillingLifecycleActions(PreviewDatabase database) : ILifecycleActionHandler
{
    public string Kind => "gas_billing";
    public ImmutableArray<string> Permissions => ["ReadTrades", "ReadMarketData", "AmendTrade"];
    const string Action = "gas.bill_daily";
    Guid Catalog => database.Manifest.CatalogId;

    static GasBillingActionSelection Read(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request, "gas_billing");
        GasBillingActionSelection selection;
        try { selection = request.Selection.Deserialize<GasBillingActionSelection>(LifecycleNative.Json) ?? throw new JsonException(); }
        catch (JsonException error) { throw new MarketDataException("gas_billing_input", "Select exact billing terms, source, predecessor and reconciliation policy.", error); }
        if (request.Action != Action || request.Context.SnapshotId is not null || selection.Billing is null ||
            selection.ProcessingConvention != GasBillingOperatingStore.Clock || selection.ScheduledPolicy != GasBillingReconciliation.ManualScheduled ||
            selection.Billing.TradeId != request.Target.ScopeId || selection.SourceId == Guid.Empty ||
            request.Context.BusinessDate != DateOnly.FromDateTime(request.Context.AsOf.UtcDateTime) ||
            string.IsNullOrWhiteSpace(selection.Evidence) || selection.Evidence.Length > 500)
            throw new MarketDataException("gas_billing_input", "Retain the explicit processing clock and native billing scope.");
        // This action's request names no fixing, so it cannot carry a reviewed indexed determination.
        if (selection.Billing.Profile == OpenEtrm.MarketData.Gas.GasCashBilling.IndexedImplementation)
            throw new MarketDataException("gas_billing_profile", "The daily billing action does not bill an indexed payment. Review it in Billing & corrections.");
        if (selection.CorrectionDueAt is {} due) _ = OpenEtrm.MarketData.Trading.TradingInstant.Parse(due, "gas_billing_correction");
        return selection;
    }

    async Task<GasCashBillingStore.Prepared> Prepare(NpgsqlConnection c, LifecycleActionRequest request, GasBillingActionSelection selection, CancellationToken ct)
    {
        var source = await LifecycleNative.Source(c, database, request.Context, ct);
        var store = new GasBillingOperatingStore(database);
        return await store.Bound(c, new(source, request.Context.AsOf), async () =>
        {
            _ = await LifecycleNative.Read(c, "SELECT lifecycle.check_action_policy($1,$2,$3,$4,$5,$6)::text", ct,
                Catalog, request.PolicyId, request.PolicyRevision, request.Action, request.Context.AsOf, request.Automatic);
            var billing = new GasCashBillingStore(database);
            var components = await billing.Components(c, selection.Billing.TradeId, new(null, source.ContextId!.Value, request.Context.AsOf), ct);
            if (!components.Any(m => m.Id("instrument_id") == request.Target.BindingId))
                throw new MarketDataException("gas_billing_conflict", "Retain the selected booked instrument.");
            var value = await billing.Prepare(c, new(null, source.ContextId!.Value, request.Context.AsOf), selection.Billing, ct, components);
            if (value.SourceId != selection.SourceId || value.PreviousId != selection.ExpectedPreviousId)
                throw new MarketDataException("gas_billing_stale_preview", "Review the current final quantity and accepted predecessor.");
            return value;
        }, ct);
    }

    static string? Refusal(GasCashBillingStore.Prepared value, bool automatic) => !automatic ? null :
        value.PreviousId is not null ? "gas_billing_automatic_correction" : value.Reconciliation.Status != "matched" ? "gas_billing_reconciliation" : null;
    static string Hash(GasCashBillingStore.Prepared value, LifecycleActionRequest request, GasBillingActionSelection selection) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { nativeHash = value.Hash, request.Context, request.Target, request.PolicyId, request.PolicyRevision, request.Automatic, selection }, LifecycleNative.Json)));

    public async Task<LifecycleActionReview> Preview(NpgsqlConnection c, LifecycleActionRequest request, CancellationToken ct)
    {
        var selection = Read(request);
        var until = await LifecycleNative.ActionAuthority(c, database, Action, LifecycleNative.Body(request, selection), ct);
        var value = await Prepare(c, request, selection, ct);
        return Review(request, selection, value, until);
    }

    LifecycleActionReview Review(LifecycleActionRequest request, GasBillingActionSelection selection,
        GasCashBillingStore.Prepared value, DateTimeOffset until)
    {
        var reason = Refusal(value, request.Automatic);
        var details = JsonSerializer.SerializeToElement(new
        {
            amount = value.Calculation.Amount.Amount.ToString(), contribution = value.Calculation.Contribution.Amount.ToString(),
            currency = value.Calculation.Amount.Currency.Code, sourceId = value.SourceId, previousId = value.PreviousId,
            reconciliation = GasCashBillingStore.ReconciliationView(value.Reconciliation), value.DueAt
        }, LifecycleNative.Json);
        return new(new(request.Target, Action, value.PreviousId is null ? "Determine daily gas cash" : "Review daily gas correction",
            Occurrence(Catalog, selection.Billing), request.Context.AsOf, null, reason is null, reason, selection.Billing.ExpectedRevision, 0)
            { PolicyId = request.PolicyId, PolicyRevision = request.PolicyRevision, Selection = request.Selection }, Hash(value, request, selection), details, Catalog, until);
    }

    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection c, LifecycleActionRequest request, CancellationToken ct)
    {
        var selection = Read(request); var body = LifecycleNative.Body(request, selection);
        var until = await LifecycleNative.ActionAuthority(c, database, Action, body, ct);
        var admission = new LifecycleNativeAdmission(Catalog, request.OperationId, Action, body, "AmendTrade", until);
        await using (var status = ScheduleStore.Command(c, "SELECT lifecycle.replay_gas_billing($1,$2::jsonb,$3)::text",
            Catalog, body.GetRawText(), until))
            if (await status.ExecuteScalarAsync(ct) is string prior) return new(JsonSerializer.Deserialize<JsonElement>(prior), admission);
        var value = await Prepare(c, request, selection, ct);
        return await Write(c, request, selection, value, until, ct);
    }

    async Task<LifecycleActionWrite> Write(NpgsqlConnection c, LifecycleActionRequest request, GasBillingActionSelection selection,
        GasCashBillingStore.Prepared value, DateTimeOffset until, CancellationToken ct)
    {
        var body = LifecycleNative.Body(request, selection);
        if (Refusal(value, request.Automatic) is {} reason)
            throw new MarketDataException(reason, "Automatic daily billing requires a reconciled original; review differences and corrections explicitly.");
        if (Hash(value, request, selection) != request.PreviewHash)
            throw new MarketDataException("lifecycle_stale_review", "Review the exact final quantity, cash and reconciliation inputs.");
        var calculation = JsonSerializer.SerializeToElement(new
        {
            sourceId = value.SourceId, amount = value.Calculation.Amount.Amount.ToString(),
            numerator = value.Calculation.ExactAmount.Numerator, denominator = value.Calculation.ExactAmount.Denominator,
            scheduledSourceId = value.Reconciliation.Scheduled?.Observation.Id
        }, LifecycleNative.Json);
        var outcome = await LifecycleNative.Read(c, "SELECT lifecycle.execute_gas_billing($1,$2::jsonb,$3,$4::jsonb)::text", ct,
            Catalog, body.GetRawText(), until, calculation.GetRawText());
        return new(outcome, new LifecycleNativeAdmission(Catalog, request.OperationId, Action, body, "AmendTrade", until));
    }

    internal static Guid Occurrence(Guid catalog, GasBillingSelection selection)
    {
        var key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"gas_daily_billing_v1/{catalog:D}/{selection.TradeId:D}/{selection.CashSlot}/{selection.Day}");
        return new Guid(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..32]);
    }

    internal async Task<(JsonElement? Proposal, JsonElement? Outcome)> RunScheduled(NpgsqlConnection c, LifecycleScheduleDefinition definition,
        LifecycleReadContext context, Guid operation, string mode, CancellationToken ct)
    {
        var automatic = mode == "automatic";
        if (mode is not ("manual" or "review" or "automatic")) throw new MarketDataException("lifecycle_schedule_source", "Retain the saved action mode.");
        definition.Validate();
        var gas = definition.Settings.GasSource ?? throw new MarketDataException("lifecycle_schedule_source", "Retain the typed gas operating source.");
        if (context.SnapshotId is not null || context.AsOf != OpenEtrm.MarketData.Trading.TradingInstant.Parse(gas.EvaluationAt, "gas_billing_context") ||
            context.BusinessDate != DateWorkspace.Date(gas.BusinessDate))
            throw new MarketDataException("gas_billing_context", "Retain the exact scheduled processing clock.");
        var selection = gas.Selection(definition);
        if (Occurrence(Catalog, selection) != definition.OccurrenceId)
            throw new MarketDataException("lifecycle_schedule_source", "Retain this exact gas payment and day.");
        var source = await LifecycleNative.Source(c, database, context, ct);
        var store = new GasBillingOperatingStore(database);
        var prepared = await store.Prepare(c, new(source, context.AsOf), selection, ct);
        if (prepared.PreviousId is not null)
            throw new MarketDataException("gas_billing_correction_required", "A source-only task cannot choose correction timing or evidence.");
        var request = new LifecycleActionRequest(operation, context, definition.Target, definition.Action, definition.PolicyId, definition.PolicyRevision, automatic, new('0', 64),
            JsonSerializer.SerializeToElement(new GasBillingActionSelection(selection, gas.ProcessingConvention, gas.ScheduledPolicy, prepared.SourceId, null, null, null,
                "Source-derived scheduled daily billing"), LifecycleNative.Json));
        if (mode == "manual") return (null, null);
        var action = Read(request);
        var until = await LifecycleNative.ActionAuthority(c, database, Action, LifecycleNative.Body(request, action), ct);
        _ = await store.Bound(c, new(source, context.AsOf), () => LifecycleNative.Read(c,
            "SELECT lifecycle.check_action_policy($1,$2,$3,$4,$5,$6)::text", ct, Catalog, request.PolicyId, request.PolicyRevision,
            request.Action, context.AsOf, automatic), ct);
        // A single attempt reviews and executes the same immutable input cut.
        // Native execution independently rechecks current heads and exact cash.
        var review = Review(request, action, prepared, until);
        database.RegisterCommitAdmission(c, review);
        request = request with { PreviewHash = review.Hash };
        if (!automatic) return (JsonSerializer.SerializeToElement(new { request, review = new { review.Hash, review.Offer, review.Details } }, LifecycleNative.Json), null);
        var written = await Write(c, request, action, prepared, until, ct);
        database.RegisterCommitAdmission(c, written);
        return (null, written.Outcome);
    }

    // Portfolio discovery is bounded by the explicit billing worklist. A target
    // alone has neither a gas day nor a selected final-quantity policy.
    public Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection c, LifecycleReadContext context, LifecycleTarget? target,
        LifecycleDiscoveryAccess access, CancellationToken ct) => Task.FromResult(ImmutableArray<LifecycleActionOffer>.Empty);
}
