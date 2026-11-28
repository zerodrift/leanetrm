using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasBillingOperatingContext(string Convention, string BusinessDate, string EvaluationAt,
    string KnowledgeMode, [property: JsonRequired] ScheduleContext? Retained);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasBillingOperatingPreviewRequest(GasBillingOperatingContext Context, GasBillingSelection Selection);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasBillingWorkCursor(Guid TradeId, int CashSlot, string Day);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasBillingWorklistRequest(GasBillingOperatingContext Context, ImmutableArray<Guid> BookIds,
    string From, string To, string SelectionPolicy, string Profile, [property: JsonRequired] GasBillingWorkCursor? After, int Limit);
public sealed record GasBillingWorkItem(GasBillingSelection Selection, Guid BindingId, Guid BookId, Guid OccurrenceId, string State, string? Reason,
    string? Amount, string? Currency, Guid? SourceId, Guid? PreviousId, object? Reconciliation, DateTimeOffset NativeEnd, Guid? ScheduledSourceId);
public sealed record GasBillingWorklist(ScheduleContext Context, DateTimeOffset EvaluationAt, IReadOnlyList<GasBillingWorkItem> Items,
    bool HasMore, GasBillingWorkCursor? Next);
internal sealed record GasBillingReadScope(ScheduleContext Context, DateTimeOffset At);

public sealed class GasBillingOperatingStore(PreviewDatabase database)
{
    public const string Clock = "gas_processing_instant_utc_v1";
    internal static (DateOnly Day, DateTimeOffset At) ValidateContext(GasBillingOperatingContext context)
    {
        if (context is null || context.Convention != Clock ||
            !(context.KnowledgeMode == "current" && context.Retained is null || context.KnowledgeMode == "retained" && context.Retained is not null)) throw Invalid();
        var day = DateWorkspace.Date(context.BusinessDate);
        var at = TradingInstant.Parse(context.EvaluationAt, "gas_billing_context");
        if (day != DateOnly.FromDateTime(at.UtcDateTime) || context.Retained is {} retained && retained.EffectiveOn != context.BusinessDate) throw Invalid();
        return (day, at);
    }
    internal async Task<GasBillingReadScope> Resolve(NpgsqlConnection c, GasBillingOperatingContext request, ScheduleContext? selected, CancellationToken ct)
    {
        var (_, at) = ValidateContext(request);
        await using var authority = ScheduleStore.Command(c, "SELECT settlement_data.futures_margin_preview_authority($1)", database.Manifest.CatalogId);
        var until = new DateTimeOffset((DateTime)(await authority.ExecuteScalarAsync(ct))!);
        database.RegisterCommitAdmission(c, new LifecycleReadAdmission(database.Manifest.CatalogId, until));
        if (selected is not null && (request.KnowledgeMode != "retained" || request.Retained?.ContextId != selected.ContextId)) throw Invalid();
        var resolver = new KnowledgeResolver(database.Manifest.CatalogId);
        var context = request.KnowledgeMode == "current" ? (await resolver.Capture(c, new(request.BusinessDate, null, "current"), ct)).Context
            : await resolver.Retained(c, request.Retained!, ct);
        await TradingContext.AdmitSource(database, c, context, at, ct);
        return new(context, at);
    }
    internal async Task<T> Bound<T>(NpgsqlConnection c, GasBillingReadScope scope, Func<Task<T>> read, CancellationToken ct)
    {
        await using (var save = ScheduleStore.Command(c, "SAVEPOINT gas_billing_operating_read")) await save.ExecuteNonQueryAsync(ct);
        try
        {
            await PinnedContext.BindExact(c, database.Manifest.CatalogId, scope.Context, ct);
            await TradingContext.Bind(c, database.Manifest.CatalogId, scope.Context, scope.At, ct);
            return await read();
        }
        finally
        {
            await using var restore = ScheduleStore.Command(c, "ROLLBACK TO SAVEPOINT gas_billing_operating_read;RELEASE SAVEPOINT gas_billing_operating_read");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
    internal Task<GasCashBillingStore.Prepared> Prepare(NpgsqlConnection c, GasBillingReadScope scope, GasBillingSelection selection, CancellationToken ct) =>
        Bound(c, scope, () => new GasCashBillingStore(database).Prepare(c, new GasBillingView(null, scope.Context.ContextId!.Value, scope.At), selection, ct), ct);

    public async Task<object> Preview(NpgsqlConnection c, GasBillingOperatingPreviewRequest request, ScheduleContext? selected, CancellationToken ct)
    {
        if (request.Selection is null) throw Invalid();
        // The processing clock reviews no fixing, so an indexed payment is previewed in Billing & corrections only.
        if (request.Selection.Profile == GasCashBilling.IndexedImplementation)
            throw new MarketDataException("gas_billing_profile", "The processing preview does not bill an indexed payment. Review it in Billing & corrections.");
        var scope = await Resolve(c, request.Context, selected, ct);
        var p = await Prepare(c, scope, request.Selection, ct);
        return new { context = scope.Context, evaluationAt = scope.At, previewHash = p.Hash, sourceId = p.SourceId, previousId = p.PreviousId,
            amount = p.Calculation.Amount.Amount.ToString(), contribution = p.Calculation.Contribution.Amount.ToString(),
            currency = p.Calculation.Amount.Currency.Code, contractualDueAt = p.DueAt, alreadyDetermined = p.AlreadyDetermined,
            reconciliation = GasCashBillingStore.ReconciliationView(p.Reconciliation) };
    }

    public async Task<GasBillingWorklist> Worklist(NpgsqlConnection c, GasBillingWorklistRequest request, ScheduleContext? selected, CancellationToken ct)
    {
        var from = DateWorkspace.Date(request.From); var to = DateWorkspace.Date(request.To);
        if (request.BookIds.IsDefaultOrEmpty || request.BookIds.Length > 64 || request.BookIds.Any(id => id == Guid.Empty) ||
            request.BookIds.Distinct().Count() != request.BookIds.Length || from >= to || to.DayNumber - from.DayNumber > 366 ||
            request.Limit is < 1 or > 8 || request.Profile is not (GasCashBilling.Implementation or GasCashBilling.EntryImplementation) ||
            request.SelectionPolicy is not (GasDailyQuantitySelection.BillingPolicy or GasDailyQuantitySelection.FacilityBillingPolicy) ||
            request.After is {} after && (after.TradeId == Guid.Empty || after.CashSlot < 0 || DateWorkspace.Date(after.Day) < from || DateWorkspace.Date(after.Day) >= to))
            throw new MarketDataException("gas_billing_scope", "Choose explicit books, a window of one to 366 gas dates, supported policies and a page of one to eight occurrences.");
        var scope = await Resolve(c, request.Context, selected, ct);
        return await Bound(c, scope, async () =>
        {
            var page = await LifecycleNative.Read(c, "SELECT security.read_gas_billing_page($1,$2,$3,$4,$5,$6,$7::jsonb,$8)::text", ct,
                database.Manifest.CatalogId, request.BookIds.ToArray(), scope.Context.ContextId!.Value, scope.At, from, to,
                request.After is null ? DBNull.Value : JsonSerializer.Serialize(request.After, LifecycleNative.Json), request.Limit);
            var items = new List<GasBillingWorkItem>();
            var metadata = new Dictionary<Guid, ImmutableArray<System.Text.Json.JsonElement>>();
            var quantities = new Dictionary<Guid, GasCashBillingStore.BillingInputs>();
            foreach (var row in page.GetProperty("rows").EnumerateArray())
            {
                var trade = row.Id("trade_id"); var slot = int.Parse(row.Text("slot"), CultureInfo.InvariantCulture);
                if (!metadata.TryGetValue(trade, out var components))
                    metadata.Add(trade, components = await new GasCashBillingStore(database).Components(c, trade, new(null, scope.Context.ContextId!.Value, scope.At), ct));
                var component = components.Single(m => int.Parse(m.Text("slot"), CultureInfo.InvariantCulture) == slot);
                var selection = new GasBillingSelection(trade, int.Parse(row.Text("revision"), CultureInfo.InvariantCulture), slot, row.Text("native_day"),
                    request.SelectionPolicy, request.Profile, int.Parse(component.Text("display_minor_units"), CultureInfo.InvariantCulture), GasCashBilling.RoundingImplementation);
                string state; string? reason = null; GasCashBillingStore.Prepared? prepared = null;
                if (row.Instant("finish") > scope.At) state = "not_due";
                else
                {
                    try
                    {
                        if (!quantities.TryGetValue(trade, out var inputs))
                            quantities.Add(trade, inputs = await new GasCashBillingStore(database).LoadInputs(c, new(null, scope.Context.ContextId!.Value, scope.At),
                                trade, from, to, request.SelectionPolicy, ct));
                        prepared = await new GasCashBillingStore(database).Prepare(c, new(null, scope.Context.ContextId!.Value, scope.At), selection, ct, components, inputs);
                        state = prepared.AlreadyDetermined ? "already_determined" : prepared.PreviousId is not null ? "needs_correction" :
                            prepared.Reconciliation.Status == "matched" ? "ready" : "review_required";
                    }
                    catch (MarketDataException error) when (error.Code is "gas_billing_profile" or "gas_billing_rights" or "gas_billing_input")
                    { state = "unsupported_terms"; reason = error.Code; }
                    catch (MarketDataException error) when (error.Code == "gas_quantity_selection")
                    {
                        var heads = quantities[trade].Quantities.Heads;
                        var hasFinal = heads.Any(h => h.Id("obligation_id") == component.Id("obligation_id") && h.Text("day") == selection.Day && h.Text("kind") == "allocated" && h.Text("status") == "final");
                        state = hasFinal ? "input_refused" : "waiting_for_final"; reason = error.Code;
                    }
                }
                items.Add(new(selection, row.Id("instrument_id"), row.Id("book_id"), GasBillingLifecycleActions.Occurrence(database.Manifest.CatalogId, selection), state, reason,
                    prepared?.Calculation.Amount.Amount.ToString(), prepared?.Calculation.Amount.Currency.Code, prepared?.SourceId, prepared?.PreviousId,
                    prepared is null ? null : GasCashBillingStore.ReconciliationView(prepared.Reconciliation), row.Instant("finish"),
                    prepared?.Reconciliation.Scheduled?.Observation.Id));
            }
            return new GasBillingWorklist(scope.Context, scope.At, items, page.GetProperty("hasMore").GetBoolean(),
                page.GetProperty("next").Deserialize<GasBillingWorkCursor>(LifecycleNative.Json));
        }, ct);
    }
    static MarketDataException Invalid() => new("gas_billing_context", "Select an exact UTC processing instant on its explicit business date and a working or certified historical knowledge view.");
}
