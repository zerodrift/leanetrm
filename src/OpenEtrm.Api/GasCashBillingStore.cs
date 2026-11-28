using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using OpenEtrm.Shared.Numerics;
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
    [property: JsonRequired] string? Reason, string Evidence,
    // Named only by an indexed determination, so every fixed request keeps its retained wire form.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? FixingObservationId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasBillingSelection(Guid TradeId, int ExpectedRevision, [property: JsonRequired] int CashSlot, string Day,
    string SelectionPolicy, string Profile, [property: JsonRequired] int DecimalPlaces, string RoundingPolicy)
{
    internal static GasBillingSelection From(GasBillingPreviewRequest r) =>
        new(r.TradeId, r.ExpectedRevision, r.CashSlot, r.Day, r.SelectionPolicy, r.Profile, r.DecimalPlaces, r.RoundingPolicy);
}
internal sealed record GasBillingView(Guid? Id, Guid ContextId, DateTimeOffset At);

public sealed class GasCashBillingStore(PreviewDatabase database)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly GasDailyQuantities daily = new(database);
    static MarketDataException Error(string code, string message) => new(code, message);
    static int Int(JsonElement row, string name) => int.Parse(row.Text(name), CultureInfo.InvariantCulture);
    internal sealed record Prepared(Guid ContextId, Guid SourceId, Guid? PreviousId,
        GasBillingCalculation Calculation, string Hash, DateTimeOffset DueAt, bool AlreadyDetermined,
        GasBillingReconciliation Reconciliation, IndexedPricing? Indexed = null);
    // The fixing an indexed determination reviews, with the booked spread and the unit price they give.
    internal sealed record IndexedPricing(GasBillingFixing Fixing, Guid? CorrectsId, string IndexCode, UnitPrice Spread, UnitPrice Price);

    public static GasBillingPreviewRequest RequirePreview(GasBillingDetermineRequest request) =>
        request.Preview is { Context: not null } preview ? preview :
            throw Error("gas_billing_input", "Supply the complete reviewed billing request and context.");

    internal async Task<ImmutableArray<JsonElement>> Components(NpgsqlConnection c, Guid trade, GasBillingView view, CancellationToken ct)
    {
        var rows = await LifecycleNative.Read(c, "SELECT security.read_gas_billing_components($1,$2,$3,$4)::text", ct,
            database.Manifest.CatalogId, trade, view.ContextId, view.At);
        return [.. rows.EnumerateArray()];
    }
    internal sealed record BillingInputs(GasDailyQuantities.Inputs Quantities, ImmutableArray<JsonElement> ManualHeads, ImmutableArray<JsonElement> History);
    internal async Task<BillingInputs> LoadInputs(NpgsqlConnection c, GasBillingView view, Guid trade, DateOnly from, DateOnly to, string policy, CancellationToken ct)
    {
        var data = await LifecycleNative.Read(c, "SELECT security.read_gas_billing_inputs($1,$2,$3,$4,$5,$6,$7)::text", ct,
            database.Manifest.CatalogId, trade, view.ContextId, view.At, from, to, policy);
        ImmutableArray<JsonElement> Rows(string name) => [.. data.GetProperty(name).EnumerateArray()];
        var header = data.GetProperty("header");
        var programs = await new RetainedScheduleStore(database).LoadPrograms(c, header.Id("instrument_id"), view.ContextId, view.At, ct);
        return new(new(header, Rows("obligations"), Rows("heads"), [], null, programs, []), Rows("manualHeads"), Rows("history"));
    }
    static bool SupportedEntry(JsonElement row)=>GasCashBilling.SupportsEntry(
        new ExactRatio(BigInteger.Parse(row.Text("share_n")),BigInteger.Parse(row.Text("share_d"))),row.OptionalText("part_kind") is not null,
        DateWorkspace.Date(row.Text("delivery_from")),DateWorkspace.Date(row.Text("delivery_to")),DateWorkspace.Date(row.Text("source_from")),DateWorkspace.Date(row.Text("source_to")));
    static bool RightDependent(JsonElement row) => row.GetProperty("right_dependent").GetBoolean();
    static bool Indexed(JsonElement row) => row.Text("role") == "indexed_consideration";
    // These conventions retain one published fixing for each payment day: a named daily or monthly index, or the index
    // its location's dated periods select. The retired strict daily convention needs its own profile.
    static bool RetainedFixing(JsonElement row) => row.OptionalText("observation_convention") is "named_index_daily_v1" or "named_index_monthly_v1" or "point_publications_v1";
    static bool Billable(JsonElement row) => row.Text("stage") == "delivery" && !RightDependent(row) && (!Indexed(row) || RetainedFixing(row));
    static string[] Profiles(JsonElement row) => Indexed(row) ? [GasCashBilling.IndexedImplementation]
        : SupportedEntry(row) ? [GasCashBilling.Implementation, GasCashBilling.EntryImplementation] : [GasCashBilling.EntryImplementation];
    static bool InTerm(JsonElement row, DateOnly day) =>
        day >= DateWorkspace.Date(row.Text("delivery_from")) && day < DateWorkspace.Date(row.Text("delivery_to"));
    static GasBillingEntryTerms EntryTerms(JsonElement row)
    {
        SignedRatio Level(string prefix) => new(BigInteger.Parse(row.Text(prefix + "_n")), BigInteger.Parse(row.Text(prefix + "_d")));
        var part = row.OptionalText("part_kind") switch
        {
            null => null,
            "band" => QuantityPart.Band(Level("part_lower"), row.OptionalText("part_upper_n") is null ? null : Level("part_upper")),
            "shortfall" => QuantityPart.Shortfall(Level("part_lower")),
            _ => throw Error("gas_billing_profile", "This quantity part requires another qualified billing profile.")
        };
        var held = ScheduleNumber.Parse(row.Text("held_base"));
        return new(DateWorkspace.Date(row.Text("delivery_from")), DateWorkspace.Date(row.Text("delivery_to")),
            new(BigInteger.Parse(row.Text("share_n")), BigInteger.Parse(row.Text("share_d"))), part,
            new(held.Numerator, held.Denominator));
    }
    static string EntryDescription(JsonElement row)
    {
        var entry = EntryTerms(row);
        static string Exact(SignedRatio value) => value.Denominator.IsOne ? value.Numerator.ToString(CultureInfo.InvariantCulture)
            : value.Numerator.ToString(CultureInfo.InvariantCulture) + "/" + value.Denominator.ToString(CultureInfo.InvariantCulture);
        string Level(SignedRatio level) => Exact(level * entry.HeldBaseQuantity) + " " + row.Text("quantity_code");
        var quantity = entry.Part is not {} part ? "whole final quantity" : part.Kind == QuantityPartKind.Shortfall
            ? "shortfall below " + Level(part.Lower)
            : "band from " + Level(part.Lower) + (part.Upper is {} upper ? " to " + Level(upper) : " without an upper limit");
        return $"Share {Exact(new(entry.Share.Numerator, entry.Share.Denominator))}; {quantity}; delivery {entry.From:yyyy-MM-dd} through {entry.To.AddDays(-1):yyyy-MM-dd}.";
    }

    public async Task<object> Read(NpgsqlConnection c, InputFrame frame, GasBillingReadRequest body, CancellationToken ct)
    {
        var components = await Components(c, body.TradeId, new(frame.Id, frame.ContextId!.Value, frame.At), ct);
        var data = await LifecycleNative.Read(c, "SELECT security.read_gas_billing_contracts($1,$2,$3,$4)::text", ct,
            database.Manifest.CatalogId, body.TradeId, frame.ContextId.Value, frame.At);
        var header = data.GetProperty("header");
        var programs = await new RetainedScheduleStore(database).LoadPrograms(c, header.Id("instrument_id"), frame.ContextId, frame.At, ct);
        var booked = ScheduleNumber.Parse(header.Text("quantity"));
        var contracts = data.GetProperty("obligations").EnumerateArray().SelectMany(o =>
        {
            var program = programs.Single(p => p.Slot == Int(o, "slot"));
            var scale = new ExactRatio(BigInteger.Parse(o.Text("ratio_n")) * booked.Numerator, BigInteger.Parse(o.Text("ratio_d")) * booked.Denominator);
            return GasDailyQuantitySelection.ReadContract(program.Program, scale).Select(contract => new { ObligationId = o.Id("id"), SideCode = o.Text("code"), Contract = contract });
        }).ToArray();
        var claims = components.SelectMany(m => contracts.Where(d => d.ObligationId == m.Id("obligation_id") && InTerm(m, d.Contract.Day)).Select(d => new
        {
            cashSlot = Int(m, "slot"), code = m.Text("code"), role = m.Text("role"), stage = m.Text("stage"),
            sideCode = d.SideCode, day = d.Contract.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            d.Contract.Start, d.Contract.End, price = m.OptionalText("price"), currency = m.Text("currency_code"),
            decimalPlaces = Int(m, "display_minor_units"), direction = Int(m, "direction"),
            supported = Billable(m), completed = frame.At >= d.Contract.End,
            refusal = RightDependent(m) ? "gas_billing_rights" : Billable(m) ? null : "gas_billing_profile",
            profiles = Profiles(m), entryDescription = EntryDescription(m),
            index = Indexed(m) ? new { code = m.OptionalText("index_code"), convention = m.Text("observation_convention"), spread = m.Text("spread") } : null
        })).ToArray();
        return new { body.TradeId, expectedRevision = Int(header, "revision"), claims,
            determinations = data.GetProperty("history"),
            profile = GasCashBilling.Implementation,
            profiles = new[] { GasCashBilling.Implementation, GasCashBilling.EntryImplementation, GasCashBilling.IndexedImplementation },
            roundingPolicy = GasCashBilling.RoundingImplementation, fixingPolicy = GasCashBilling.FixingPolicy,
            quantityPolicies = new[] { GasDailyQuantitySelection.BillingPolicy, GasDailyQuantitySelection.FacilityBillingPolicy },
            correctionReasons = new[] { "late_actuals", "quantity_correction", "allocation_correction", "index_fixing_correction" },
            canWrite = await daily.RecordRefusal(c, body.Context is { KnowledgeMode: "current", ContextId: null }, ct) is null,
            scope = "commercial_daily_cash_only", valuationEffect = "contractual_pv_unchanged" };
    }

    internal Task<Prepared> Prepare(NpgsqlConnection c, InputFrame frame, GasBillingPreviewRequest request, CancellationToken ct) =>
        Prepare(c, new GasBillingView(frame.Id, frame.ContextId ?? throw Error("gas_billing_context", "Retain the certified billing context."), frame.At), GasBillingSelection.From(request), ct);

    internal async Task<Prepared> Prepare(NpgsqlConnection c, GasBillingView view, GasBillingSelection request, CancellationToken ct,
        ImmutableArray<JsonElement> components = default, BillingInputs? inputs = null)
    {
        if (request.Profile is not (GasCashBilling.Implementation or GasCashBilling.EntryImplementation or GasCashBilling.IndexedImplementation) || request.RoundingPolicy != GasCashBilling.RoundingImplementation ||
            request.SelectionPolicy is not (GasDailyQuantitySelection.BillingPolicy or GasDailyQuantitySelection.FacilityBillingPolicy))
            throw Error("gas_billing_policy", "Select explicit final quantity, daily fixed cash and currency-rounding policies.");
        var component = (components.IsDefault ? await Components(c, request.TradeId, view, ct) : components).SingleOrDefault(m => Int(m, "slot") == request.CashSlot);
        if(component.ValueKind==JsonValueKind.Object && RightDependent(component))
            throw Error("gas_billing_rights", "Exercise-dependent payments and quantity sources require a separately qualified billing profile.");
        if(component.ValueKind==JsonValueKind.Object && request.Profile == GasCashBilling.Implementation && !SupportedEntry(component))
            throw Error("gas_billing_input","This billing profile requires a whole, full-term payment with share one.");
        var indexed = request.Profile == GasCashBilling.IndexedImplementation;
        if (component.ValueKind != JsonValueKind.Object || component.Text("stage") != "delivery" || Indexed(component) != indexed || indexed && !RetainedFixing(component))
            throw Error("gas_billing_profile", "Fixed profiles bill a retained fixed-price delivery component, and the indexed profile a payment on a named daily or monthly index or on point publications. Other stages and index conventions require another qualified profile.");
        if (Int(component, "revision") != request.ExpectedRevision)
            throw Error("gas_billing_conflict", "Refresh the current booked revision.");
        if (request.DecimalPlaces != Int(component, "display_minor_units"))
            throw Error("gas_billing_rounding", "Use the selected exact currency revision's minor-unit precision.");
        var day = DateWorkspace.Date(request.Day);
        if (!InTerm(component, day)) throw Error("gas_billing_interval", "Choose a native delivery day inside this payment's own term.");
        inputs ??= await LoadInputs(c, view, request.TradeId, day, day.AddDays(1), request.SelectionPolicy, ct);
        var selection = GasDailyQuantities.Select(inputs.Quantities, view.At, request.SelectionPolicy, component.Id("obligation_id"), day);
        var source = selection.Days.SingleOrDefault() ?? throw Error("gas_billing_interval", "Choose one retained native delivery day for this cash component.");
        var reference = new ReferenceRevision(component.Id("reference_catalog_id"), component.Id("reference_revision_id"));
        var unit = new PriceUnit(new(reference, component.Text("currency_code")), new(reference, component.Text("quantity_code")), ExactDecimal.Parse(component.Text("multiplier")));
        var previous = inputs.History.SingleOrDefault(row => Int(row, "cash_slot") == request.CashSlot && DateWorkspace.Date(row.Text("day")) == day);
        var previousId = previous.ValueKind == JsonValueKind.Object ? previous.Id("id") : (Guid?)null;
        (IndexedPricing Indexed, DateTimeOffset PaymentAt)? pricing = indexed ? await ReadIndexed(c, view, request, component, unit, ct) : null;
        var paymentAt = pricing?.PaymentAt ?? TradingInstant.Parse(component.Text("payment_at"), "gas_billing_input");
        var terms = new GasBillingTerms(source.Contract, pricing?.Indexed.Price ?? new(ExactDecimal.Parse(component.Text("price")), unit),
            ExactDecimal.Parse(component.Text("direction")), paymentAt, request.DecimalPlaces, request.RoundingPolicy);
        var previousTotal = previousId is null ? null : new Money(ExactDecimal.Parse(previous.Text("rounded_total")), unit.Currency);
        var value = request.Profile != GasCashBilling.Implementation
            ? GasCashBilling.CalculateEntry(terms, EntryTerms(component), source.Selected, view.At, previousTotal)
            : GasCashBilling.Calculate(terms, source.Selected, view.At, previousTotal);
        var manualHeads = inputs.ManualHeads.Where(h => h.Id("obligation_id") == source.ObligationId && DateWorkspace.Date(h.Text("day")) == day);
        var scheduled = manualHeads.SingleOrDefault(h => h.Text("kind") == "scheduled");
        GasBillingQuantityEvidence? scheduleEvidence = scheduled.ValueKind != JsonValueKind.Object || scheduled.Text("status") == "withdrawn" ? null :
            new(source.ObligationId, day, source.Contract.Start, source.Contract.End,
                new(scheduled.Id("id"), "scheduled", scheduled.Text("status"),
                    ExactQuantity.FromQuantity(new(ExactDecimal.Parse(scheduled.Text("quantity")), unit.Quantity)),
                    scheduled.Text("evidence"), scheduled.Text("source")));
        var finalEvidence = new GasBillingQuantityEvidence(source.ObligationId, day, source.Contract.Start, source.Contract.End,
            new(source.Selected.RevisionId!.Value, "allocated", "final", source.Selected.Quantity, source.Selected.Evidence!,
                request.SelectionPolicy == GasDailyQuantitySelection.BillingPolicy ? "manual_daily_v1" : "facility_allocation_adoption_v1"));
        var reconciliation = GasBillingReconciliation.Assess(source.ObligationId, source.Contract, scheduleEvidence, finalEvidence, GasBillingReconciliation.ManualScheduled);
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.TradeId, request.ExpectedRevision, request.CashSlot, request.Day, request.Profile, request.SelectionPolicy,
            request.DecimalPlaces, request.RoundingPolicy, view.Id, view.At, component, source.Contract,
            source.Selected.RevisionId, source.Selected.Quantity, previousId, value, reconciliation,
            fixingObservationId = pricing?.Indexed.Fixing.ObservationId
        }, Json)));
        return new(view.ContextId,
            source.Selected.RevisionId!.Value, previousId, value, hash, paymentAt,
            previousId is not null && (previous.OptionalText("manual_revision_id") ?? previous.OptionalText("facility_target_id")) == source.Selected.RevisionId.Value.ToString()
                && previous.OptionalText("fixing_observation_id") == pricing?.Indexed.Fixing.ObservationId.ToString(), reconciliation, pricing?.Indexed);
    }
    // The database selects the payment day's fixing head under the view's knowledge; a day without exactly one final head refuses there.
    async Task<(IndexedPricing Indexed, DateTimeOffset PaymentAt)> ReadIndexed(NpgsqlConnection c, GasBillingView view, GasBillingSelection request,
        JsonElement component, PriceUnit unit, CancellationToken ct)
    {
        var row = await LifecycleNative.Read(c, "SELECT security.read_gas_billing_fixing($1,$2,$3,$4,$5,$6)::text", ct,
            database.Manifest.CatalogId, request.TradeId, request.CashSlot, DateWorkspace.Date(request.Day), view.ContextId, view.At);
        var fixing = new GasBillingFixing(row.Id("observationId"), new(ExactDecimal.Parse(row.Text("value")), unit),
            TradingInstant.Parse(row.Text("observedAt"), "gas_billing_fixing"), row.Text("publicationState") == "final");
        var spread = new UnitPrice(ExactDecimal.Parse(component.Text("spread")), unit);
        var price = GasCashBilling.IndexedPrice(fixing, spread, TradingInstant.Parse(row.Text("fixingAt"), "gas_billing_fixing"), view.At);
        return (new(fixing, row.OptionalId("correctsId"), row.Text("indexCode"), spread, price), TradingInstant.Parse(row.Text("paymentAt"), "gas_billing_fixing"));
    }
    public async Task<object> Preview(NpgsqlConnection c, InputFrame frame, GasBillingPreviewRequest request, CancellationToken ct)
    {
        var p = await Prepare(c, frame, request, ct);
        return new { previewHash = p.Hash, previousId = p.PreviousId, sourceId = p.SourceId,
            amount = p.Calculation.Amount.Amount.ToString(), contribution = p.Calculation.Contribution.Amount.ToString(),
            exactAmount = p.Calculation.ExactAmount.ToExactString(), currency = p.Calculation.Amount.Currency.Code,
            contractualDueAt = p.DueAt, request.Profile, request.SelectionPolicy, request.RoundingPolicy,
            alreadyDetermined = p.AlreadyDetermined, reconciliation = ReconciliationView(p.Reconciliation), scope = "commercial_daily_cash_only",
            fixing = p.Indexed is not {} i ? null : new { observationId = i.Fixing.ObservationId, value = i.Fixing.Value.Value.ToString(), observedAt = i.Fixing.ObservedAt,
                correctsId = i.CorrectsId, index = i.IndexCode, spread = i.Spread.Value.ToString(), unitPrice = i.Price.Value.ToString(), policy = GasCashBilling.FixingPolicy } };
    }
    internal static object ReconciliationView(GasBillingReconciliation r) => new
    {
        r.Status, r.ScheduledPolicy, contractual = r.Contractual.ToExactString(), scheduled = r.Scheduled?.Observation.Quantity.ToExactString(),
        final = r.Final?.Observation.Quantity.ToExactString(), scheduledLessContract = r.ScheduledLessContract?.ToExactString(),
        finalLessScheduled = r.FinalLessScheduled?.ToExactString(), scheduledSource = r.Scheduled, finalSource = r.Final,
        automaticOriginalEligible = r.Status == "matched"
    };
    public async Task<object> Determine(NpgsqlConnection c, InputFrame frame, GasBillingDetermineRequest request, CancellationToken ct)
    {
        var body = RequirePreview(request);
        if (body.Context is not { Convention: PhysicalGasStore.Convention, KnowledgeMode: "current", ContextId: null })
            throw Error("historical_read_only", "Return to working data before determining billing.");
        var payload = JsonSerializer.Serialize(request, Json);
        await using (var replay = ScheduleStore.Command(c, "SELECT security.replay_gas_billing($1,$2,$3::jsonb)::text", database.Manifest.CatalogId, request.OperationId, payload))
            if (await replay.ExecuteScalarAsync(ct) is string old) return JsonSerializer.Deserialize<JsonElement>(old);
        var p = await TradingBookingRead.Run(database, c, frame, (_, pinned) => Prepare(c, pinned, body, ct), ct);
        if (p.SourceId != request.SourceId || p.Indexed?.Fixing.ObservationId != request.FixingObservationId || p.Hash != request.PreviewHash) throw Error("gas_billing_stale_preview", "Billing inputs or the accepted determination changed. Preview and review the current result.");
        if (request.ExpectedPreviousId != p.PreviousId) throw Error("gas_billing_conflict", "Retain the reviewed predecessor.");
        if (request.CorrectionDueAt is {} due) _ = TradingInstant.Parse(due, "gas_billing_correction");
        var calculation = JsonSerializer.Serialize(new { sourceId = p.SourceId, amount = p.Calculation.Amount.Amount.ToString(),
            numerator = p.Calculation.ExactAmount.Numerator, denominator = p.Calculation.ExactAmount.Denominator,
            fixingObservationId = p.Indexed?.Fixing.ObservationId }, Json);
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
