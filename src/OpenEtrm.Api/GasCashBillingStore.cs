using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasBillingReadRequest(FuturesReadRequest Context, Guid TradeId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasBillingPreviewRequest(FuturesReadRequest Context, Guid TradeId, int ExpectedRevision,
    int CashSlot, string Day, string SelectionPolicy, string Profile, int DecimalPlaces, string RoundingPolicy);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasBillingDetermineRequest(GasBillingPreviewRequest Preview, Guid OperationId, string PreviewHash, [property: JsonRequired] Guid SourceId,
    [property: JsonRequired] Guid? ExpectedPreviousId, [property: JsonRequired] string? CorrectionDueAt,
    [property: JsonRequired] string? Reason, string Evidence);

public sealed class GasCashBillingStore(PreviewDatabase database)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly GasDailyQuantities daily = new(database);
    static MarketDataException Error(string code, string message) => new(code, message);
    static int Int(JsonElement row, string name) => int.Parse(row.Text(name), CultureInfo.InvariantCulture);
    internal sealed record Prepared(Guid ContextId, Guid SourceId, Guid? PreviousId,
        GasBillingCalculation Calculation, string Hash, DateTimeOffset DueAt, bool AlreadyDetermined);

    public static GasBillingPreviewRequest RequirePreview(GasBillingDetermineRequest request) =>
        request.Preview is { Context: not null } preview ? preview :
            throw Error("gas_billing_input", "Supply the complete reviewed billing request and context.");

    async Task<ImmutableArray<JsonElement>> Components(NpgsqlConnection c, Guid trade, CancellationToken ct)
    {
        await using (var check = ScheduleStore.Command(c, "SELECT security.require_gas_quantity_trade($1,$2,false)", database.Manifest.CatalogId, trade))
            await check.ExecuteNonQueryAsync(ct);
        // Keep exact selected keys ahead of expensive RLS joins. Every relation
        // still uses its ordinary read policy; all visible revisions are retained.
        return await PreviewDatabase.Rows(c, """
            SELECT row_to_json(x)::text FROM(
            SELECT r.revision,r.instrument_id,m.slot,m.code,m.role,m.stage,m.direction,m.price::text,m.payment_at,o.id obligation_id,
            q.catalog_id reference_catalog_id,q.revision_id reference_revision_id,q.quantity_code,q.currency_code,q.multiplier::text,cur.display_minor_units
            FROM(SELECT * FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=($1,$2) OFFSET 0)r
            CROSS JOIN LATERAL(SELECT * FROM trading.gas_component_terms
                WHERE(catalog_id,instrument_id)=(r.catalog_id,r.instrument_id) AND role IN('consideration','fee') OFFSET 0)m
            CROSS JOIN LATERAL(SELECT * FROM trading.gas_delivery_obligations
                WHERE(catalog_id,instrument_id,slot)=(m.catalog_id,m.instrument_id,m.source_slot) OFFSET 0)o
            CROSS JOIN LATERAL(SELECT * FROM market_data.gas_valuation_assignments
                WHERE(catalog_id,revision_id,code)=(m.catalog_id,m.definition_revision_id,m.assignment_code) OFFSET 0)a
            CROSS JOIN LATERAL(SELECT * FROM market_data.curves
                WHERE(catalog_id,revision_id,code)=(a.catalog_id,a.revision_id,a.curve_code) OFFSET 0)curve
            CROSS JOIN LATERAL(SELECT * FROM ref_data.quote_specifications
                WHERE(catalog_id,revision_id,code)=(a.reference_catalog_id,a.reference_revision_id,curve.quote_code) OFFSET 0)q
            CROSS JOIN LATERAL(SELECT * FROM ref_data.currencies
                WHERE(catalog_id,revision_id,code)=(q.catalog_id,q.revision_id,q.currency_code) OFFSET 0)cur
            ORDER BY m.slot)x
            """, [database.Manifest.CatalogId, trade], ct);
    }
    async Task<ImmutableArray<JsonElement>> History(NpgsqlConnection c, Guid trade, CancellationToken ct) =>
        await PreviewDatabase.Rows(c, """
            SELECT (to_jsonb(d)||jsonb_build_object('rounded_total',trim_scale(d.rounded_total)::text,
            'contribution',trim_scale(d.contribution)::text))::text FROM trading.gas_cash_determinations d
            WHERE(catalog_id,trade_id)=($1,$2) ORDER BY cash_slot,day,ordinal
            """, [database.Manifest.CatalogId, trade], ct);

    public async Task<object> Read(NpgsqlConnection c, InputFrame frame, GasBillingReadRequest body, CancellationToken ct)
    {
        var components = await Components(c, body.TradeId, ct);
        var selection = await daily.Select(c, frame, body.TradeId, GasDailyQuantitySelection.ProjectionPolicy, false, ct);
        var claims = components.SelectMany(m => selection.Days.Where(d => d.ObligationId == m.Id("obligation_id")).Select(d => new
        {
            cashSlot = Int(m, "slot"), code = m.Text("code"), role = m.Text("role"), stage = m.Text("stage"),
            sideCode = d.SideCode, day = d.Contract.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            d.Contract.Start, d.Contract.End, price = m.Text("price"), currency = m.Text("currency_code"),
            decimalPlaces = Int(m, "display_minor_units"), direction = Int(m, "direction"),
            supported = m.Text("stage") == "delivery", completed = frame.At >= d.Contract.End
        })).ToArray();
        return new { body.TradeId, expectedRevision = Int(selection.Header, "revision"), claims,
            determinations = await History(c, body.TradeId, ct),
            profile = GasCashBilling.Implementation, roundingPolicy = GasCashBilling.RoundingImplementation,
            quantityPolicies = new[] { GasDailyQuantitySelection.BillingPolicy, GasDailyQuantitySelection.FacilityBillingPolicy },
            correctionReasons = new[] { "late_actuals", "quantity_correction", "allocation_correction" },
            canWrite = await daily.RecordRefusal(c, body.Context is { KnowledgeMode: "current", ContextId: null }, ct) is null,
            scope = "commercial_daily_cash_only", valuationEffect = "contractual_pv_unchanged" };
    }

    internal async Task<Prepared> Prepare(NpgsqlConnection c, InputFrame frame, GasBillingPreviewRequest request, CancellationToken ct)
    {
        if (request.Profile != GasCashBilling.Implementation || request.RoundingPolicy != GasCashBilling.RoundingImplementation ||
            request.SelectionPolicy is not (GasDailyQuantitySelection.BillingPolicy or GasDailyQuantitySelection.FacilityBillingPolicy))
            throw Error("gas_billing_policy", "Select explicit final quantity, daily fixed cash and currency-rounding policies.");
        var component = (await Components(c, request.TradeId, ct)).SingleOrDefault(m => Int(m, "slot") == request.CashSlot);
        if (component.ValueKind != JsonValueKind.Object || component.Text("stage") != "delivery")
            throw Error("gas_billing_profile", "This profile bills a retained fixed-price delivery component. Receipt and fuel stages require another qualified profile.");
        if (Int(component, "revision") != request.ExpectedRevision)
            throw Error("gas_billing_conflict", "Refresh the current booked revision.");
        if (request.DecimalPlaces != Int(component, "display_minor_units"))
            throw Error("gas_billing_rounding", "Use the selected exact currency revision's minor-unit precision.");
        var day = DateWorkspace.Date(request.Day);
        var selection = await daily.Select(c, frame, request.TradeId, request.SelectionPolicy, false, ct, component.Id("obligation_id"), day);
        var source = selection.Days.SingleOrDefault() ?? throw Error("gas_billing_interval", "Choose one retained native delivery day for this cash component.");
        var reference = new ReferenceRevision(component.Id("reference_catalog_id"), component.Id("reference_revision_id"));
        var unit = new PriceUnit(new(reference, component.Text("currency_code")), new(reference, component.Text("quantity_code")), ExactDecimal.Parse(component.Text("multiplier")));
        var previous = (await PreviewDatabase.Rows(c, """
            SELECT to_jsonb(d)::text FROM trading.gas_cash_determinations d
            WHERE(catalog_id,trade_id,cash_slot,day)=($1,$2,$3,$4) ORDER BY ordinal DESC LIMIT 1
            """, [database.Manifest.CatalogId, request.TradeId, request.CashSlot, day], ct)).SingleOrDefault();
        var previousId = previous.ValueKind == JsonValueKind.Object ? previous.Id("id") : (Guid?)null;
        var paymentAt = TradingInstant.Parse(component.Text("payment_at"), "gas_billing_input");
        var terms = new GasBillingTerms(source.Contract, new(ExactDecimal.Parse(component.Text("price")), unit),
            ExactDecimal.Parse(component.Text("direction")), paymentAt, request.DecimalPlaces, request.RoundingPolicy);
        var value = GasCashBilling.Calculate(terms, source.Selected, frame.At,
            previousId is null ? null : new Money(ExactDecimal.Parse(previous.Text("rounded_total")), unit.Currency));
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.TradeId, request.ExpectedRevision, request.CashSlot, request.Day, request.Profile, request.SelectionPolicy,
            request.DecimalPlaces, request.RoundingPolicy, frame.Id, frame.At, component, source.Contract,
            source.Selected.RevisionId, source.Selected.Quantity, previousId, value
        }, Json)));
        return new(frame.ContextId ?? throw Error("gas_billing_context", "Retain the certified billing context."),
            source.Selected.RevisionId!.Value, previousId, value, hash, paymentAt,
            previousId is not null && (previous.OptionalText("manual_revision_id") ?? previous.OptionalText("facility_target_id")) == source.Selected.RevisionId.Value.ToString());
    }
    public async Task<object> Preview(NpgsqlConnection c, InputFrame frame, GasBillingPreviewRequest request, CancellationToken ct)
    {
        var p = await Prepare(c, frame, request, ct);
        return new { previewHash = p.Hash, previousId = p.PreviousId, sourceId = p.SourceId,
            amount = p.Calculation.Amount.Amount.ToString(), contribution = p.Calculation.Contribution.Amount.ToString(),
            exactAmount = p.Calculation.ExactAmount.ToExactString(), currency = p.Calculation.Amount.Currency.Code,
            contractualDueAt = p.DueAt, request.Profile, request.SelectionPolicy, request.RoundingPolicy,
            alreadyDetermined = p.AlreadyDetermined, scope = "commercial_daily_cash_only" };
    }
    public async Task<object> Determine(NpgsqlConnection c, InputFrame frame, GasBillingDetermineRequest request, CancellationToken ct)
    {
        var body = RequirePreview(request);
        if (body.Context is not { Convention: PhysicalGasStore.Convention, KnowledgeMode: "current", ContextId: null })
            throw Error("historical_read_only", "Return to working data before determining billing.");
        var payload = JsonSerializer.Serialize(request, Json);
        await using (var replay = ScheduleStore.Command(c, "SELECT security.replay_gas_billing($1,$2,$3::jsonb)::text", database.Manifest.CatalogId, request.OperationId, payload))
            if (await replay.ExecuteScalarAsync(ct) is string old) return JsonSerializer.Deserialize<JsonElement>(old);
        var p = await TradingBookingRead.Run(database, c, frame, (_, pinned) => Prepare(c, pinned, body, ct), ct);
        if (p.SourceId != request.SourceId || p.Hash != request.PreviewHash) throw Error("gas_billing_stale_preview", "Billing inputs or the accepted determination changed. Preview and review the current result.");
        if (request.ExpectedPreviousId != p.PreviousId) throw Error("gas_billing_conflict", "Retain the reviewed predecessor.");
        if (request.CorrectionDueAt is {} due) _ = TradingInstant.Parse(due, "gas_billing_correction");
        var calculation = JsonSerializer.Serialize(new { sourceId = p.SourceId, amount = p.Calculation.Amount.Amount.ToString(),
            numerator = p.Calculation.ExactAmount.Numerator, denominator = p.Calculation.ExactAmount.Denominator }, Json);
        await using var command = ScheduleStore.Command(c, "SELECT security.record_gas_billing($1,$2::jsonb,$3,$4,$5::jsonb)::text",
            database.Manifest.CatalogId, payload, frame.At, p.ContextId, calculation);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    public async Task<object> Status(NpgsqlConnection c, Guid operation, CancellationToken ct)
    {
        if (operation == Guid.Empty) throw Error("gas_billing_input", "Supply the retained operation identity.");
        await using var command = ScheduleStore.Command(c, "SELECT security.gas_billing_status($1,$2,NULL)::text", database.Manifest.CatalogId, operation);
        return await command.ExecuteScalarAsync(ct) is string value ? JsonSerializer.Deserialize<JsonElement>(value) : new { found = false };
    }
}
