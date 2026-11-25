using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed record GasSwingExpectedHead(string Day, Guid? RevisionId);
public sealed record GasSwingElectionInput(string Day, string? Quantity, string ExercisingParty,
    string SubmittedAt, string EffectiveAt, string Evidence);
public sealed record GasSwingRecordRequest(FuturesReadRequest Context, Guid TradeId, Guid InstrumentId, int ExpectedRevision,
    Guid OperationId, int Right, ImmutableArray<GasSwingExpectedHead> ExpectedHeads, ImmutableArray<GasSwingElectionInput> Elections);
public sealed record GasSwingReadRequest(FuturesReadRequest Context, Guid TradeId, Guid InstrumentId);

internal static class GasSwingStore
{
    // A nullable parameter still has to be supplied. This distinguishes a
    // withdrawal from an omitted quantity and an empty chain from a missing head.
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    internal static T Request<T>(JsonElement input) where T : class =>
        input.Deserialize<T>(Wire) ?? throw new JsonException();

    private sealed record WriteAdmission(Guid Catalog, Guid Operation, DateTimeOffset Until) : ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection, CancellationToken ct)
        {
            await using var check = ScheduleStore.Command(connection, "SELECT security.require_gas_swing_receipt($1,$2,$3)", Catalog, Operation, Until);
            await check.ExecuteNonQueryAsync(ct);
        }
    }

    // Both runtime and a leased worker can capture current financial authority.
    // A restored temporary view also retains its earlier, potentially shorter deadline.
    private sealed record ReadAuthority(Guid Catalog, DateTimeOffset Until) : ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection, CancellationToken ct)
        {
            await using var check = ScheduleStore.Command(connection,
                "SELECT clock_timestamp()<least($2,deadline) FROM settlement_data.futures_margin_preview_authority($1) AS deadline", Catalog, Until);
            if (await check.ExecuteScalarAsync(ct) is not true) throw new PreviewDeniedException();
        }
    }

    private sealed record ReadAdmission(Guid Catalog, Guid Trade, Guid Binding, DateTimeOffset Until) : IViewAdmission
    {
        public ICommitAdmission Retained => new ReadAuthority(Catalog, Until);

        public async Task RequireCommit(NpgsqlConnection connection, CancellationToken ct)
        {
            await RequireScope(connection, Catalog, Trade, Binding, ct);
            await Retained.RequireCommit(connection, ct);
        }
    }

    internal static async Task<JsonElement> Record(NpgsqlConnection connection, PreviewDatabase database, GasSwingRecordRequest request,
        InputFrame frame, CancellationToken ct)
    {
        var catalog = database.Manifest.CatalogId;
        await using var capture = ScheduleStore.Command(connection, "SELECT security.gas_swing_write_until($1)", catalog);
        var until = new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        var body = new { request.OperationId, request.TradeId, request.InstrumentId, request.ExpectedRevision,
            request.Right, request.ExpectedHeads, request.Elections };
        JsonElement result;
        try
        {
            result=await Result(connection,"SELECT security.record_gas_swing_elections($1,$2::jsonb,$3)::text",
                ct,catalog,JsonSerializer.Serialize(body,Wire),frame.At);
        }
        catch(PostgresException error) when(error is {SqlState:"P0001",MessageText:"swing_feasibility"})
        {
            // Feasibility belongs to this atomic selection of days. Identify its first
            // row for focus; the native validator considered every effective boundary.
            var selected=request.Elections.Select(e=>e.Day).Order(StringComparer.Ordinal).ToArray();
            var row=request.ExpectedHeads.Count(h=>string.CompareOrdinal(h.Day,selected[0])<0);
            throw new TicketFieldException("swing_feasibility",$"The elections for {selected[0]} through {selected[^1]} leave no feasible completion of the term quantity.",
                null,"quantity"){TermSection="swingElections",TermRow=row};
        }
        database.RegisterCommitAdmission(connection, new WriteAdmission(catalog, request.OperationId, until));
        return result;
    }

    internal static async Task<JsonElement> Read(NpgsqlConnection connection, PreviewDatabase database, Guid trade, Guid binding, CancellationToken ct)
    {
        var catalog = database.Manifest.CatalogId;
        var result = await Result(connection, "SELECT security.read_gas_swing_elections($1,$2,$3)::text", ct, catalog, trade, binding);
        database.RegisterCommitAdmission(connection, new ReadAdmission(catalog, trade, binding, result.Instant("authorityUntil")));
        return result;
    }

    internal static async Task RequireScope(NpgsqlConnection connection, Guid catalog, Guid trade, Guid binding, CancellationToken ct)
    {
        await using var query = ScheduleStore.Command(connection, "SELECT security.require_gas_swing_binding($1,$2,$3)", catalog, trade, binding);
        await query.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<(ImmutableArray<SwingElection> Elections,DateTimeOffset? Next)> Facts(
        NpgsqlConnection connection, PreviewDatabase database, GasCompositionExecution held, CancellationToken ct)
    {
        if (held.SwingRights.IsEmpty) return ([], null);
        var result = await Read(connection, database, held.TradeId, held.InstrumentId, ct);
        return Facts(held,result);
    }

    internal static (ImmutableArray<SwingElection> Elections, DateTimeOffset? Next) Facts(GasCompositionExecution held,JsonElement result)
    {
        var elections = ImmutableArray.CreateBuilder<SwingElection>();
        foreach (var row in result.GetProperty("elections").EnumerateArray())
        {
            if (row.OptionalText("quantity") is not {} amount) continue;
            var ordinal = row.GetProperty("right").GetInt32();
            var quantity = ExactQuantity.FromQuantity(new(ExactDecimal.Parse(amount), held.SwingQuantityUnit(ordinal)));
            elections.Add(new(ordinal, DateWorkspace.Date(row.Text("day")), quantity, GasSwingBooking.Holder(row.Text("exercisingParty")),
                row.GetProperty("submittedAt").GetDateTimeOffset(), row.GetProperty("effectiveAt").GetDateTimeOffset(),
                row.Id("revisionId"), row.Text("evidence")));
        }
        var next = result.GetProperty("nextElectionEffectiveAt");
        return (elections.ToImmutable(), next.ValueKind == JsonValueKind.Null ? null : next.GetDateTimeOffset());
    }

    private static async Task<JsonElement> Result(NpgsqlConnection connection, string sql, CancellationToken ct, params object[] values)
    {
        await using var query = ScheduleStore.Command(connection, sql, values);
        return JsonSerializer.Deserialize<JsonElement>((string)(await query.ExecuteScalarAsync(ct))!);
    }
}
