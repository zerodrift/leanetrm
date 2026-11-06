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
public sealed record GasOperationsReadRequest(FuturesReadRequest Context, Guid TradeId, string Day);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOperationPreviewRequest(FuturesReadRequest Context, Guid TradeId, int ExpectedRevision, string Day, string Kind, JsonElement Details);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOperationCommand(GasOperationPreviewRequest Preview, Guid OperationId, string PreviewHash);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasServiceDetails(Guid ObligationId,string HolderRole,string ExternalReference,[property:JsonRequired]Guid? ExpectedPreviousId,string ValidFrom,string ValidTo,bool Active,string StatementPolicy,string TermsReference,string Evidence);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasNominationDetails(Guid AccountId,Guid ServiceVersionId,string ExternalReference,[property:JsonRequired]Guid? ExpectedPreviousId,string Quantity,string SubmittedAt,string Channel,string Reason,string Evidence);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasResponseDetails([property:JsonRequired]Guid? NominationId,[property:JsonRequired]Guid? ImbalanceId,string Side,string Decision,[property:JsonRequired]string? Quantity,string ExternalReference,string RespondedAt,[property:JsonRequired]Guid? ExpectedPreviousId,string Evidence);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasNominationApplyDetails(Guid NominationId,Guid CounterpartyResponseId,Guid PipelineResponseId,[property:JsonRequired]Guid? ExpectedPreviousId,string Profile,string Evidence);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasStatementDetails(Guid AccountId,Guid ServiceVersionId,string ExternalReference,[property:JsonRequired]Guid? ExpectedPreviousId,[property:JsonRequired]string? Balance,string Status,string ReportedAt,[property:JsonRequired]Guid? IncludedMovementId,string Evidence);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasImbalanceDetails(Guid BuyerAccountId,Guid BuyerVersionId,Guid SellerAccountId,Guid SellerVersionId,string ExternalReference,string Quantity,string Price,string AgreedAt,string DueAt,int DecimalPlaces,string RoundingPolicy,string Profile,string Evidence);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasImbalanceApplyDetails(Guid ImbalanceId,string Evidence);

// Native operations own all interpretation. Browser and approved assistant proposals
// submit the same closed manual contracts and review the same deterministic result.
public sealed class GasDeliveryOperationsStore(PreviewDatabase database)
{
    internal static readonly IReadOnlyDictionary<string,Type> DetailTypes = new Dictionary<string,Type>{
        ["service"]=typeof(GasServiceDetails),["nomination"]=typeof(GasNominationDetails),["response"]=typeof(GasResponseDetails),
        ["nomination_apply"]=typeof(GasNominationApplyDetails),["statement"]=typeof(GasStatementDetails),["imbalance"]=typeof(GasImbalanceDetails),["imbalance_apply"]=typeof(GasImbalanceApplyDetails)};
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly GasDailyQuantities daily = new(database);
    static MarketDataException Error(string code, string message) => new("gas_operations_" + code, message);
    static int Int(JsonElement row, string name) => int.Parse(row.Text(name), CultureInfo.InvariantCulture);
    static Guid? Id(JsonElement row, string name) => row.OptionalText(name) is {} value ? Guid.Parse(value) : null;
    static ExactDecimal Number(JsonElement row, string name) => ExactDecimal.Parse(row.Text(name));
    static DateTimeOffset Instant(JsonElement row, string name) => TradingInstant.Parse(row.Text(name), "gas_operations_input");
    static JsonElement[] Rows(JsonElement state, string name) => [.. state.GetProperty(name).EnumerateArray()];
    static JsonElement Find(IEnumerable<JsonElement> rows, Guid id) => rows.SingleOrDefault(r => r.Id("id") == id) is var row && row.ValueKind == JsonValueKind.Object ? row : throw Error("scope", "The exact selected record is unavailable in this context.");
    static JsonElement? Last(IEnumerable<JsonElement> rows, string ordinal = "revision") => rows.OrderByDescending(r => Int(r, ordinal)).Select(r => (JsonElement?)r).FirstOrDefault();
    internal sealed record Prepared(Guid ContextId, string Hash, object Calculation, object Review);

    async Task<JsonElement> State(NpgsqlConnection c, Guid trade, DateOnly day, CancellationToken ct)
    {
        await using var q = ScheduleStore.Command(c, "SELECT security.read_gas_operations($1,$2,$3)::text", database.Manifest.CatalogId, trade, day);
        return JsonSerializer.Deserialize<JsonElement>((string)(await q.ExecuteScalarAsync(ct))!);
    }
    async Task<JsonElement> Source(NpgsqlConnection c, Guid obligation, DateOnly day, DateTimeOffset at, CancellationToken ct)
    {
        await using var q = ScheduleStore.Command(c, "SELECT security.gas_service_source($1,$2,$3,$4)::text", database.Manifest.CatalogId, obligation, day, at);
        return JsonSerializer.Deserialize<JsonElement>((string)(await q.ExecuteScalarAsync(ct))!);
    }
    static ReferenceRevision Reference(JsonElement source) => new(source.Id("reference_catalog_id"), source.Id("reference_revision_id"));
    static ExactQuantity Quantity(JsonElement source, string value) => ExactQuantity.FromQuantity(new(ExactDecimal.Parse(value), new(Reference(source), source.Text("quantity_code"))));
    static GasDailyContract Contract(JsonElement source, DateOnly day) => new("retained/gas/quantity", 0, 0, day, Instant(source, "native_from"), Instant(source, "native_to"), Quantity(source, "0"));
    static GasBalanceScope Scope(JsonElement source) => new(new(Reference(source), source.Text("pipeline_code")), new(Reference(source), source.Text("zone_code")), new(Reference(source), source.Text("product_code")), new(Reference(source), source.Text("quantity_code")), Instant(source, "native_from"), Instant(source, "native_to"));
    static JsonElement Account(JsonElement state, Guid id, Guid? version, DateOnly day)
    {
        var a = Find(Rows(state, "accounts"), id);
        if (version is {} v && a.Id("version_id") != v || !a.GetProperty("active").GetBoolean() || day < DateWorkspace.Date(a.Text("valid_from")) || day >= DateWorkspace.Date(a.Text("valid_to")))
            throw Error("service", "Choose the latest active service terms covering this native day under the header context.");
        return a;
    }
    static (ExactQuantity Value, Guid Statement, Guid? Movement) Balance(JsonElement state, JsonElement account, JsonElement source)
    {
        var id = account.Id("id");
        var s = Last(Rows(state, "statements").Where(s => s.Id("account_id") == id)) ?? throw Error("balance", "A missing balance statement is unassessed, never zero.");
        var moves = Rows(state, "movements").Where(m => m.Id("account_id") == id).OrderBy(m => Int(m, "ordinal")).ToArray();
        var value = GasImbalanceTrading.Balance(new(s.Id("id"), s.OptionalText("balance") is {} b ? Quantity(source, b) : null, s.Text("status"), Id(s, "included_movement_id")),
            moves.Select(m => new GasBalanceMovement(m.Id("id"), Int(m, "ordinal"), Quantity(source, m.Text("quantity")))).ToImmutableArray(), account.Text("statement_policy"));
        return (value, s.Id("id"), moves.LastOrDefault().ValueKind == JsonValueKind.Object ? moves[^1].Id("id") : null);
    }
    public async Task<object> Read(NpgsqlConnection c, InputFrame frame, GasOperationsReadRequest body, CancellationToken ct)
    {
        var day = DateWorkspace.Date(body.Day); var state = await State(c, body.TradeId, day, ct);
        var balances = new List<object>();
        foreach (var a in Rows(state, "accounts"))
        {
            try
            {
                _ = Account(state, a.Id("id"), null, day);
                var source = await Source(c, a.Id("origin_obligation_id"), day, frame.At, ct);
                var b = Balance(state, a, source);
                balances.Add(new { accountId = a.Id("id"), amount = b.Value.ToExactString(), statementId = (Guid?)b.Statement, movementId = b.Movement, refusal = (string?)null });
            }
            catch (MarketDataException e) { balances.Add(new { accountId = a.Id("id"), amount = (string?)null, statementId = (Guid?)null, movementId = (Guid?)null, refusal = e.Message }); }
        }
        await using var permissions = ScheduleStore.Command(c, "SELECT jsonb_build_object('canConfigure',security.trading_access($1,'ConfigureGasServices'),'canRecord',security.trading_access($1,'RecordGasOperations') AND security.trading_access($1,'AmendTrade'))::text", database.Manifest.CatalogId);
        return new { body.TradeId, body.Day, state, balances, permissions = JsonSerializer.Deserialize<JsonElement>((string)(await permissions.ExecuteScalarAsync(ct))!), nominationProfile = GasNominationWorkflow.Profile, imbalanceProfile = GasImbalanceTrading.Profile, statementPolicies = new[] { GasImbalanceTrading.FinalPolicy, GasImbalanceTrading.ReportedPolicy }, roundingPolicy = GasCashBilling.RoundingImplementation, scope = "manual_nonportfolio_operations", liveConnection = "TBD" };
    }
    internal async Task<Prepared> Prepare(NpgsqlConnection c, InputFrame frame, GasOperationPreviewRequest request, CancellationToken ct)
    {
        if (request.Details.ValueKind != JsonValueKind.Object || request.Kind is null || !DetailTypes.TryGetValue(request.Kind,out var contract)) throw Error("input","Choose a supported manual operation.");
        try { _ = JsonSerializer.Deserialize(request.Details.GetRawText(),contract,Json); }
        catch(JsonException) { throw Error("input","Supply the complete closed manual contract with explicit nullable choices and exact typed values."); }
        // One shared canonical ingress check also protects direct runtime SQL calls.
        await using (var shape = ScheduleStore.Command(c, "SELECT security.validate_gas_operations($1::jsonb)", JsonSerializer.Serialize(new GasOperationCommand(request, Guid.NewGuid(), new string('0', 64)), Json)))
            await shape.ExecuteNonQueryAsync(ct);
        var day = DateWorkspace.Date(request.Day); var state = await State(c, request.TradeId, day, ct); var d = request.Details;
        var selection = await daily.Select(c, frame, request.TradeId, GasDailyQuantitySelection.ProjectionPolicy, false, ct, day: day);
        if (Int(selection.Header, "revision") != request.ExpectedRevision) throw Error("conflict", "Refresh the selected booked revision.");
        object calculation = new { }; object review;
        JsonElement? Response(Guid parent, bool nomination, string side) => Last(Rows(state, "responses").Where(r => Id(r, nomination ? "nomination_id" : "imbalance_id") == parent && r.Text("side") == side));
        Guid? Head(Guid obligation, string kind) => selection.Days.Single(r => r.ObligationId == obligation).Heads.SingleOrDefault(r => r.Text("kind") == kind) is var h && h.ValueKind == JsonValueKind.Object ? h.Id("id") : null;
        async Task<(JsonElement Buyer, JsonElement Seller, JsonElement Source, GasImbalanceTransfer Transfer, object Basis)> Transfer(JsonElement terms, bool agreement)
        {
            var buyer = Account(state, terms.Id(agreement ? "buyer_account_id" : "buyerAccountId"), terms.Id(agreement ? "buyer_version_id" : "buyerVersionId"), day);
            var seller = Account(state, terms.Id(agreement ? "seller_account_id" : "sellerAccountId"), terms.Id(agreement ? "seller_version_id" : "sellerVersionId"), day);
            if (buyer.Id("id") == seller.Id("id") || buyer.Text("holder_role") == seller.Text("holder_role") || buyer.Id("legal_entity_id") != seller.Id("legal_entity_id")) throw Error("scope", "Select one owned and one counterparty account for the same legal entity.");
            var owned = buyer.Text("holder_role") == "legal_entity" ? buyer : seller;
            if (owned.Id("trade_id") != request.TradeId) throw Error("scope", "The owned account must belong to this selected trade.");
            var bs = await Source(c, buyer.Id("origin_obligation_id"), day, frame.At, ct); var ss = await Source(c, seller.Id("origin_obligation_id"), day, frame.At, ct);
            var source = buyer.Text("holder_role") == "legal_entity" ? bs : ss;
            var bb = Balance(state, buyer, bs); var sb = Balance(state, seller, ss);
            string Key(string camel, string snake) => agreement ? snake : camel;
            var places = Int(terms, Key("decimalPlaces", "decimal_places"));
            if (places != Int(source, "decimal_places")) throw Error("rounding", "Use the exact retained currency's minor-unit precision.");
            var quote = new PriceUnit(new(Reference(source), source.Text("currency_code")), new(Reference(source), source.Text("quantity_code")), Number(source, "multiplier"));
            var result = GasImbalanceTrading.Transfer(Scope(bs), Scope(ss), bb.Value, sb.Value, Quantity(bs, terms.Text("quantity")), new(new(Number(terms, "price"), quote), buyer.Text("holder_role") == "legal_entity" ? "buyer" : "seller", places, terms.Text(Key("roundingPolicy", "rounding_policy")), Instant(terms, Key("agreedAt", "agreed_at")), Instant(terms, Key("dueAt", "due_at"))), frame.At, terms.Text("profile"));
            var basis = new { buyerStatementId = bb.Statement, sellerStatementId = sb.Statement, buyerMovementId = bb.Movement, sellerMovementId = sb.Movement, cash = result.Cash.Amount.ToString(), numerator = result.ExactCash.Numerator, denominator = result.ExactCash.Denominator };
            return (buyer, seller, source, result, basis);
        }
        switch (request.Kind)
        {
            case "service":
                var source = await Source(c, d.Id("obligationId"), day, frame.At, ct);
                if (source.Id("trade_id") != request.TradeId || d.Text("holderRole") is not ("legal_entity" or "counterparty") || DateWorkspace.Date(d.Text("validFrom")) >= DateWorkspace.Date(d.Text("validTo"))) throw Error("service", "Supply an exact booked anchor, holder and nonempty supported day range.");
                review = new { action = "Save service terms", d, source };
                break;
            case "nomination":
                var account = Account(state, d.Id("accountId"), d.Id("serviceVersionId"), day);
                if (account.Id("trade_id") != request.TradeId) throw Error("scope", "The account belongs to another trade.");
                source = await Source(c, account.Id("origin_obligation_id"), day, frame.At, ct);
                var qty = Number(d, "quantity"); if (qty.CompareTo(default) < 0) throw Error("nomination", "Requested total cannot be negative.");
                var phase = GasNominationWorkflow.Phase(Contract(source, day), Instant(d, "submittedAt"), frame.At);
                calculation = new { nominatedHead = Head(account.Id("origin_obligation_id"), "nominated"), confirmedHead = Head(account.Id("origin_obligation_id"), "confirmed"), scheduledHead = Head(account.Id("origin_obligation_id"), "scheduled") };
                review = new { action = "Record submitted request; update nominated quantity only", phase, quantity = qty.ToString(), unit = source.Text("quantity_code"), account = account.Text("external_reference"), d };
                break;
            case "response":
                var nomination = Id(d, "nominationId"); var imbalance = Id(d, "imbalanceId");
                if ((nomination is null) == (imbalance is null)) throw Error("response", "Select exactly one submitted request or imbalance agreement.");
                var parent = Find(Rows(state, nomination is null ? "agreements" : "nominations"), (nomination ?? imbalance)!.Value);
                var since = Instant(parent, nomination is null ? "agreed_at" : "submitted_at");
                if (Instant(d, "respondedAt") > frame.At || Instant(d, "respondedAt") < since || d.Text("side") is not ("counterparty" or "pipeline" or "request") || d.Text("decision") is not ("approved" or "rejected" or "withdrawn") || (d.Text("side") == "request") != (d.Text("decision") == "withdrawn") || (d.Text("decision") == "approved") != (d.OptionalText("quantity") is not null) || d.OptionalText("quantity") is {} q && ExactDecimal.Parse(q).CompareTo(default) < 0) throw Error("response", "Supply the actual response time, decision and explicit approved quantity; withdrawal belongs to the request.");
                review = new { action = "Record external response only; no schedule or balance change", d };
                break;
            case "nomination_apply":
                var n = Find(Rows(state, "nominations"), d.Id("nominationId")); account = Account(state, n.Id("account_id"), n.Id("service_version_id"), day);
                source = await Source(c, account.Id("origin_obligation_id"), day, frame.At, ct);
                var cp = Response(n.Id("id"), true, "counterparty") ?? throw Error("acknowledgment", "Record the counterparty response first.");
                var pipe = Response(n.Id("id"), true, "pipeline") ?? throw Error("acknowledgment", "Record the pipeline response first.");
                GasSchedulingDecision Decision(JsonElement r) => new(r.Text("decision") == "approved", r.OptionalText("quantity") is {} q ? Quantity(source, q) : null, Instant(r, "responded_at"), r.Text("evidence"));
                if (cp.Id("id") != d.Id("counterpartyResponseId") || pipe.Id("id") != d.Id("pipelineResponseId")) throw Error("conflict", "Review the latest external response pair.");
                var accepted = GasNominationWorkflow.Accept(Contract(source, day), Quantity(source, n.Text("quantity")), Decision(cp), Decision(pipe), Response(n.Id("id"), true, "request") is not null, frame.At, d.Text("profile"));
                var app = Last(Rows(state, "nominationApplications").Where(a => a.Id("nomination_id") == n.Id("id")), "ordinal");
                if (Head(account.Id("origin_obligation_id"), "nominated") != n.Id("nomination_id") || Head(account.Id("origin_obligation_id"), "confirmed") != (app is {} ca ? ca.Id("confirmed_id") : Id(n, "base_confirmed_id")) || Head(account.Id("origin_obligation_id"), "scheduled") != (app is {} sa ? sa.Id("scheduled_id") : Id(n, "base_scheduled_id"))) throw Error("conflict", "A newer independent quantity exists. Reconcile it before applying this request.");
                if ((app is {} prior ? (Guid?)prior.Id("id") : null) != Id(d, "expectedPreviousId")) throw Error("conflict", "Review the latest application predecessor.");
                calculation = new { counterpartyId = cp.Id("id"), pipelineId = pipe.Id("id"), previousApplicationId = app is {} ap ? (Guid?)ap.Id("id") : null };
                review = new { action = "Apply accepted schedule", confirmed = accepted.Confirmed.ToExactString(), scheduled = accepted.Scheduled.ToExactString(), unit = source.Text("quantity_code"), d };
                break;
            case "statement":
                account = Account(state, d.Id("accountId"), d.Id("serviceVersionId"), day); source = await Source(c, account.Id("origin_obligation_id"), day, frame.At, ct);
                if (account.Id("trade_id") != request.TradeId || frame.At < Instant(source, "native_to") || Instant(d, "reportedAt") > frame.At || d.Text("status") is not ("provisional" or "final" or "withdrawn") || (d.Text("status") == "withdrawn") != (d.OptionalText("balance") is null)) throw Error("statement", "Record after-flow evidence with an explicit signed balance, or withdraw the source.");
                if (d.OptionalText("balance") is {} b) _ = ExactDecimal.Parse(b);
                if (Id(d, "includedMovementId") is {} prefix)
                {
                    var m = Find(Rows(state, "movements"), prefix);
                    if (m.Id("account_id") != account.Id("id") || Instant(m, "external_effective_at") > Instant(d, "reportedAt")) throw Error("statement", "The included movement must belong to this account/day and precede the reported statement.");
                }
                review = new { action = "Record operator statement; retain accepted transfers", d };
                break;
            case "imbalance":
                var proposed = await Transfer(d, false);
                calculation = proposed.Basis;
                review = new { action = "Record agreement only; acknowledgments and application still required", buyerBalanceAfter = proposed.Transfer.BuyerBalance.ToExactString(), sellerBalanceAfter = proposed.Transfer.SellerBalance.ToExactString(), cash = proposed.Transfer.Cash.Amount.ToString(), currency = proposed.Source.Text("currency_code"), unit = proposed.Source.Text("quantity_code"), priceMultiplier = proposed.Source.Text("multiplier"), scope = "nonportfolio_unposted_commercial_cash", d };
                break;
            case "imbalance_apply":
                var t = Find(Rows(state, "agreements"), d.Id("imbalanceId"));
                if (Rows(state, "imbalanceApplications").Any(a => a.Id("agreement_id") == t.Id("id"))) throw Error("imbalance", "This agreement has already been applied. Later responses are reconciliation evidence; partial fill/reversal is TBD.");
                cp = Response(t.Id("id"), false, "counterparty") ?? throw Error("acknowledgment", "Record the counterparty response first."); pipe = Response(t.Id("id"), false, "pipeline") ?? throw Error("acknowledgment", "Record the pipeline response first.");
                if (cp.Text("decision") != "approved" || pipe.Text("decision") != "approved" || Number(cp, "quantity") != Number(t, "quantity") || Number(pipe, "quantity") != Number(t, "quantity") || Response(t.Id("id"), false, "request") is not null) throw Error("acknowledgment", "The full agreed quantity requires both approvals and no withdrawal.");
                var applied = await Transfer(t, true);
                calculation = JsonSerializer.SerializeToElement(applied.Basis, Json).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
                ((Dictionary<string, object>)calculation).Add("counterpartyId", cp.Id("id")); ((Dictionary<string, object>)calculation).Add("pipelineId", pipe.Id("id"));
                review = new { action = "Apply paired accepted movements and signed nominal cash once", buyerBalanceAfter = applied.Transfer.BuyerBalance.ToExactString(), sellerBalanceAfter = applied.Transfer.SellerBalance.ToExactString(), cash = applied.Transfer.Cash.Amount.ToString(), currency = applied.Source.Text("currency_code"), unit = applied.Source.Text("quantity_code"), priceMultiplier = applied.Source.Text("multiplier"), scope = "nonportfolio_unposted_commercial_cash", d };
                break;
            default: throw Error("input", "Choose a supported manual operation.");
        }
        // Include exact pinned dependencies, not an unrelated account's UI state.
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { request.TradeId, request.ExpectedRevision, request.Day, request.Kind, request.Details, frame.Id, frame.At, calculation, review }, Json)));
        return new(frame.ContextId ?? throw Error("context", "Retain the certified context."), hash, calculation, review);
    }
    public async Task<object> Preview(NpgsqlConnection c, InputFrame frame, GasOperationPreviewRequest body, CancellationToken ct)
    {
        var p = await Prepare(c, frame, body, ct); return new { previewHash = p.Hash, review = p.Review };
    }
    public async Task<object> Record(NpgsqlConnection c, InputFrame frame, GasOperationCommand body, bool configure, CancellationToken ct)
    {
        var request = body.Preview ?? throw Error("input", "Supply the complete reviewed operation.");
        if (request.Context is not { Convention: PhysicalGasStore.Convention, KnowledgeMode: "current", ContextId: null }) throw Error("context", "Return to working data before recording operations.");
        if (configure != (request.Kind == "service")) throw Error("input", "Use the dedicated service configuration or recording operation.");
        var payload = JsonSerializer.Serialize(body, Json);
        await using (var replay = ScheduleStore.Command(c, "SELECT security.replay_gas_operations($1,$2::jsonb,$3)::text", database.Manifest.CatalogId, payload, frame.At))
            if (await replay.ExecuteScalarAsync(ct) is string old) return JsonSerializer.Deserialize<JsonElement>(old);
        var p = await TradingBookingRead.Run(database, c, frame, (_, pinned) => Prepare(c, pinned, request, ct), ct);
        if (p.Hash != body.PreviewHash) throw Error("stale_preview", "Inputs changed. Preview and review the current operation again.");
        await using var command = ScheduleStore.Command(c, "SELECT security.record_gas_operations($1,$2::jsonb,$3,$4,$5::jsonb)::text", database.Manifest.CatalogId, payload, frame.At, p.ContextId, JsonSerializer.Serialize(p.Calculation, Json));
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    public async Task<object> Status(NpgsqlConnection c, Guid operation, CancellationToken ct)
    {
        if (operation == Guid.Empty) throw Error("input", "Supply the retained operation identity.");
        await using var q = ScheduleStore.Command(c, "SELECT security.gas_operations_status($1,$2,NULL)::text", database.Manifest.CatalogId, operation);
        return await q.ExecuteScalarAsync(ct) is string s ? JsonSerializer.Deserialize<JsonElement>(s) : new { found = false };
    }
}
