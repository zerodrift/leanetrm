using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasBillReadRequest(FuturesReadRequest Context, Guid TradeId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasBillPreviewRequest(FuturesReadRequest Context, Guid TradeId, int ExpectedRevision, string Month, string ScopePolicy);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasBillIssueRequest(GasBillPreviewRequest Preview, Guid OperationId, string PreviewHash, string ContentDigest,
    [property: JsonRequired] Guid? ExpectedPreviousId, string Evidence);

// Monthly bills and adjustment notes of one gas trade. A document is a record over existing daily determinations:
// the database supplies the scope and facts and assembles the issued document itself; this store presents them.
public sealed class GasBillStore(PreviewDatabase database)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static MarketDataException Error(string code, string message) => new(code, message);
    static int Int(JsonElement row, string name) => int.Parse(row.Text(name), CultureInfo.InvariantCulture);
    static DateOnly Month(DateOnly day) => new(day.Year, day.Month, 1);
    static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    sealed record Inputs(int Revision, Guid InstrumentId, ImmutableArray<JsonElement> Payments, ImmutableArray<GasBillExpectedDay> Expected,
        ImmutableArray<JsonElement> Determinations, ImmutableArray<JsonElement> Documents, ImmutableHashSet<DateOnly> LaterMonths);
    sealed record Assessed(GasBillAssessment Assessment, Guid? PreviousId, string? Hash, string? Digest, Guid ContextId);
    // What a request reviewed, whatever session reviewed it: the document's determination identities in line order.
    static string Digest(GasBillDocument document) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(',', document.Lines.Select(l => l.DeterminationId.ToString("D"))))));

    async Task<Inputs> Load(NpgsqlConnection c, Guid trade, Guid context, DateTimeOffset at, CancellationToken ct)
    {
        var data = await LifecycleNative.Read(c, "SELECT security.read_gas_bill_inputs($1,$2,$3,$4)::text", ct, database.Manifest.CatalogId, trade, context, at);
        ImmutableArray<JsonElement> Rows(string name) => [.. data.GetProperty(name).EnumerateArray()];
        var header = data.GetProperty("header");
        return new(Int(header, "revision"), header.Id("instrument_id"), Rows("payments"),
            [.. Rows("expected").Select(e => new GasBillExpectedDay(Int(e, "slot"), DateWorkspace.Date(e.Text("day"))))], Rows("determinations"), Rows("documents"),
            [.. data.GetProperty("later_months").EnumerateArray().Select(m => DateWorkspace.Date(m.GetString()!))]);
    }

    static GasBillAssessment Assess(Inputs inputs, DateOnly month)
    {
        if (inputs.LaterMonths.Contains(month))
            throw Error("gas_bill_later_document", "A document of this month takes effect after the selected business time. Select a business time at or after it.");
        var facts = inputs.Determinations.Where(d => Month(DateWorkspace.Date(d.Text("day"))) == month).ToArray();
        if (facts.Any(d => d.Id("instrument_id") != inputs.InstrumentId))
            throw Error("gas_bill_revision", "This month holds determinations of an earlier trade revision's instrument. Billing an amended trade needs its own contract.");
        return GasMonthlyBill.Assess(inputs.Expected.Where(e => Month(e.Day) == month),
            facts.Select(d => new GasBillDetermination(d.Id("id"), Int(d, "cash_slot"), DateWorkspace.Date(d.Text("day")), Int(d, "ordinal"),
                new Money(ExactDecimal.Parse(d.Text("contribution")), new(new(d.Id("reference_catalog_id"), d.Id("reference_revision_id")), d.Text("currency_code"))),
                Int(d, "direction") == 1 ? GasBillDirection.Receive : GasBillDirection.Pay, d.GetProperty("documented").GetBoolean())),
            inputs.Documents.Any(b => DateWorkspace.Date(b.Text("month")) == month));
    }

    static object DocumentView(GasBillDocument document, Inputs inputs) => new
    {
        kind = document.Kind,
        lines = document.Lines.Select(l => new
        {
            determinationId = l.DeterminationId, cashSlot = l.CashSlot, day = Iso(l.Day), ordinal = l.Ordinal, amount = l.Amount.Amount.ToString(),
            dueAt = inputs.Determinations.Single(d => d.Id("id") == l.DeterminationId).GetProperty("due_at")
        }),
        payTotal = document.PayTotal.Amount.ToString(), receiveTotal = document.ReceiveTotal.Amount.ToString(), currency = document.PayTotal.Currency.Code
    };

    public async Task<object> Read(NpgsqlConnection c, InputFrame frame, GasBillReadRequest body, CancellationToken ct)
    {
        var inputs = await Load(c, body.TradeId, frame.ContextId ?? throw Error("gas_bill_context", "Retain the certified bill context."), frame.At, ct);
        // Every month with an expected day, a determination or a document is shown, whatever the current revision delivers.
        var months = inputs.Expected.Select(e => Month(e.Day)).Concat(inputs.Determinations.Select(d => Month(DateWorkspace.Date(d.Text("day")))))
            .Concat(inputs.Documents.Select(b => DateWorkspace.Date(b.Text("month")))).Concat(inputs.LaterMonths).Distinct().Order().Select(month =>
        {
            string state; string? refusal = null; ImmutableArray<GasBillExpectedDay> missing = [];
            try { var assessed = Assess(inputs, month); state = assessed.State; missing = assessed.Missing; }
            catch (MarketDataException error) when (error.Code.StartsWith("gas_bill_", StringComparison.Ordinal)) { state = "refused"; refusal = error.Code; }
            return new
            {
                month = Iso(month), state, refusal, missing = missing.Select(m => new { cashSlot = m.CashSlot, day = Iso(m.Day) }),
                documents = inputs.Documents.Where(b => DateWorkspace.Date(b.Text("month")) == month).Select(b => new
                {
                    id = b.Id("id"), ordinal = Int(b, "ordinal"), kind = Int(b, "ordinal") == 1 ? GasMonthlyBill.Bill : GasMonthlyBill.AdjustmentNote,
                    previousId = b.OptionalId("previous_id"), payTotal = b.Text("pay_total"), receiveTotal = b.Text("receive_total"), currency = b.Text("currency_code"),
                    scopePolicy = b.Text("scope_policy"), contentDigest = b.Text("content_digest"), effectiveAt = b.GetProperty("effective_at"), evidence = b.Text("evidence"),
                    lines = b.GetProperty("lines").EnumerateArray().Select(l => new
                    { determinationId = l.Id("determination_id"), cashSlot = Int(l, "cash_slot"), day = l.Text("day"), amount = l.Text("amount") })
                })
            };
        }).ToArray();
        return new
        {
            body.TradeId, expectedRevision = inputs.Revision, scopePolicy = GasMonthlyBill.ScopePolicy, amountPolicy = GasMonthlyBill.AmountPolicy,
            payments = inputs.Payments.Select(p => new
            {
                cashSlot = Int(p, "slot"), code = p.Text("code"), role = p.Text("role"), direction = Int(p, "direction"),
                covered = p.OptionalText("reason") is null, reason = p.OptionalText("reason")
            }),
            months, scope = "commercial_document_only",
            canWrite = await new GasDailyQuantities(database).RecordRefusal(c, body.Context is { KnowledgeMode: "current", ContextId: null }, ct) is null
        };
    }

    async Task<(Assessed Value, Inputs Inputs)> Prepare(NpgsqlConnection c, InputFrame frame, GasBillPreviewRequest request, CancellationToken ct)
    {
        if (request.ScopePolicy != GasMonthlyBill.ScopePolicy)
            throw Error("gas_bill_policy", "Select the explicit bill scope policy.");
        var month = DateWorkspace.Date(request.Month);
        if (month.Day != 1) throw Error("gas_bill_input", "Name the delivery month by its first day.");
        var context = frame.ContextId ?? throw Error("gas_bill_context", "Retain the certified bill context.");
        var inputs = await Load(c, request.TradeId, context, frame.At, ct);
        if (inputs.Revision != request.ExpectedRevision) throw Error("gas_bill_conflict", "Refresh the current booked revision.");
        var assessed = Assess(inputs, month);
        var previous = inputs.Documents.Where(b => DateWorkspace.Date(b.Text("month")) == month).OrderBy(b => Int(b, "ordinal")).LastOrDefault();
        var previousId = previous.ValueKind == JsonValueKind.Object ? previous.Id("id") : (Guid?)null;
        var hash = assessed.Document is not { } document ? null : Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.TradeId, request.ExpectedRevision, request.Month, request.ScopePolicy, frame.Id, frame.At, previousId, document.Kind,
            lines = document.Lines.Select(l => new { l.DeterminationId, amount = l.Amount.Amount.ToString() }),
            pay = document.PayTotal.Amount.ToString(), receive = document.ReceiveTotal.Amount.ToString()
        }, Json)));
        return (new(assessed, previousId, hash, assessed.Document is null ? null : Digest(assessed.Document), context), inputs);
    }

    public async Task<object> Preview(NpgsqlConnection c, InputFrame frame, GasBillPreviewRequest request, CancellationToken ct)
    {
        var (p, inputs) = await Prepare(c, frame, request, ct);
        return new
        {
            state = p.Assessment.State, missing = p.Assessment.Missing.Select(m => new { cashSlot = m.CashSlot, day = Iso(m.Day) }),
            document = p.Assessment.Document is { } document ? DocumentView(document, inputs) : null,
            previousId = p.PreviousId, previewHash = p.Hash, contentDigest = p.Digest, request.ScopePolicy, amountPolicy = GasMonthlyBill.AmountPolicy, scope = "commercial_document_only"
        };
    }

    public async Task<object> Issue(NpgsqlConnection c, InputFrame frame, GasBillIssueRequest request, CancellationToken ct)
    {
        var body = request.Preview is { Context: not null } preview ? preview : throw Error("gas_bill_input", "Supply the complete reviewed bill request and context.");
        if (body.Context is not { Convention: PhysicalGasStore.Convention, KnowledgeMode: "current", ContextId: null })
            throw Error("historical_read_only", "Return to working data before issuing a bill.");
        var payload = JsonSerializer.Serialize(request, Json);
        await using (var replay = ScheduleStore.Command(c, "SELECT security.replay_gas_bill($1,$2,$3::jsonb)::text", database.Manifest.CatalogId, request.OperationId, payload))
            if (await replay.ExecuteScalarAsync(ct) is string old) return JsonSerializer.Deserialize<JsonElement>(old);
        var (p, _) = await TradingBookingRead.Run(database, c, frame, (_, pinned) => Prepare(c, pinned, body, ct), ct);
        if (p.Assessment.Document is not { } document)
            throw p.Assessment.State == "incomplete" ? Error("gas_bill_incomplete", "Every billable payment day of the month needs a determination before its bill is issued.")
                : Error("gas_bill_empty", "Every determination of this month is already on a document.");
        if (p.Hash != request.PreviewHash || p.Digest != request.ContentDigest) throw Error("gas_bill_stale_preview", "The month's determinations or documents changed. Preview and review the current document.");
        if (request.ExpectedPreviousId != p.PreviousId) throw Error("gas_bill_conflict", "Retain the reviewed predecessor.");
        var calculation = JsonSerializer.Serialize(new { payTotal = document.PayTotal.Amount.ToString(), receiveTotal = document.ReceiveTotal.Amount.ToString() }, Json);
        await using var command = ScheduleStore.Command(c, "SELECT security.issue_gas_bill($1,$2::jsonb,$3,$4,$5::jsonb)::text",
            database.Manifest.CatalogId, payload, frame.At, p.ContextId, calculation);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }

    public async Task<object> Status(NpgsqlConnection c, Guid operation, CancellationToken ct)
    {
        if (operation == Guid.Empty) throw Error("gas_bill_input", "Supply the retained operation identity.");
        await using var command = ScheduleStore.Command(c, "SELECT security.gas_bill_status($1,$2,NULL)::text", database.Manifest.CatalogId, operation);
        return await command.ExecuteScalarAsync(ct) is string value ? JsonSerializer.Deserialize<JsonElement>(value) : new { found = false };
    }
}
