using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record CurveMarkEditorAdmission(Guid Catalog, CurveMarkEditorContext? Context, CatalogueSource? Source,
    bool Writing, DateTimeOffset Until) : ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection c, CancellationToken ct)
    {
        await using var q = ScheduleStore.Command(c, """
            SELECT security.require_curve_mark_editor_commit($1,$2::uuid,$3::timestamptz,$4::jsonb,$5,$6)
            """, Catalog, (object?)Context?.ContextId ?? DBNull.Value, (object?)Context?.AsOf ?? DBNull.Value,
            Source is null ? DBNull.Value : JsonSerializer.Serialize(Source, CurveMarkEditorEndpoints.Json), Writing, Until);
        await q.ExecuteNonQueryAsync(ct);
    }
}

internal sealed partial class CurveMarkEditorStore(PreviewDatabase database)
{
    private Guid Catalog => database.Manifest.CatalogId;
    private static JsonSerializerOptions Json => CurveMarkEditorEndpoints.Json;
    private static string Body<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static Task<JsonElement> Read(NpgsqlConnection c, string sql, CancellationToken ct, params object[] values) =>
        LifecycleNative.Read(c, sql, ct, values);
    private static T Field<T>(JsonElement value, string name) => value.GetProperty(name).Deserialize<T>(Json)
        ?? throw new JsonException("Missing curve mark field.");
    private static CurveMarkEditorException ContextError() => new("curve_mark_context",
        "Select the certified context, business date and exact instant of the displayed market inputs.");

    private async Task<DateTimeOffset> Deadline(NpgsqlConnection c, bool writing, CancellationToken ct)
    {
        await using var q = ScheduleStore.Command(c, "SELECT security.curve_mark_editor_authority($1,$2)", Catalog, writing);
        return new((DateTime)(await q.ExecuteScalarAsync(ct))!);
    }

    private async Task<string> Owner(NpgsqlConnection c, CancellationToken ct)
    {
        await using var q = ScheduleStore.Command(c, "SELECT security.curve_mark_editor_owner($1)", Catalog);
        return (string)(await q.ExecuteScalarAsync(ct))!;
    }

    private async Task<ScheduleContext> Resolve(NpgsqlConnection c, PreviewSession session, CurveMarkEditorContext selected,
        CatalogueSource source, bool writing, DateTimeOffset until, CancellationToken ct)
    {
        if (selected.ContextId == Guid.Empty || selected.BusinessDate == default || !TradingInstant.IsSupported(selected.AsOf) ||
            source.DefinitionRevisionId == Guid.Empty || source.ReferenceCatalogId == Guid.Empty || source.ReferenceRevisionId == Guid.Empty ||
            session.MarketContext is {} historical && historical.ContextId != selected.ContextId)
            throw ContextError();
        if (selected.SnapshotId is {} id && (!session.Frames.TryGetValue(id, out var frame) || frame.At != selected.AsOf ||
            frame.BusinessDate != selected.BusinessDate || frame.ContextId != session.MarketContext?.ContextId))
            throw ContextError();

        var admission = new CurveMarkEditorAdmission(Catalog, null, source, writing, until);
        await admission.RequireCommit(c, ct);
        var known = (await new KnowledgeResolver(Catalog).Read(c, selected.ContextId, ct)).Context;
        if (known.ReadCut is not {} cut || PublishedSchedules.ContextDate(known) != selected.BusinessDate)
            throw ContextError();
        if (session.MarketContext is not null) await cut.Bind(c, Catalog, known, ct);
        else if (writing) await cut.RegisterWriteSource(c, known, ct);
        // KnowledgeResolver.Read registers the exact working read inputs.
        admission = admission with { Context = selected };
        await admission.RequireCommit(c, ct);
        database.RegisterCommitAdmission(c, admission);
        return known;
    }
}
