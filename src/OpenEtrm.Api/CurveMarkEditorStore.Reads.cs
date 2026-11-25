using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;

namespace OpenEtrm.Api;

internal sealed partial class CurveMarkEditorStore
{
    private sealed record ReadScope(CurveMarkEditorReadContext Context, DateTimeOffset? ImportUntil);
    private sealed record ChoicePage(ImmutableArray<CurveMarkEditorCurveChoice> Curves, bool HasMoreCurves, bool CanPublish);

    private async Task<ReadScope> ReadScopeFor(NpgsqlConnection c, PreviewSession session, CurveMarkEditorContext context,
        CatalogueSource source, bool editing, CancellationToken ct)
    {
        // Preserve the original optional import grant as well as mandatory read authority.
        // A later grant cannot turn a waiting reader into a publisher.
        await using var q = ScheduleStore.Command(c, """
            WITH original AS MATERIALIZED (
              SELECT CASE WHEN $2 THEN nullif(security.authority_until('ImportObservations'),'-infinity'::timestamptz) END AS import_until
            ) SELECT security.curve_mark_editor_authority($1,false),import_until FROM original
            """, Catalog, editing && session.MarketContext is null);
        await using var row = await q.ExecuteReaderAsync(ct);
        await row.ReadAsync(ct);
        var until = row.GetFieldValue<DateTimeOffset>(0);
        DateTimeOffset? importUntil = row.IsDBNull(1) ? null : row.GetFieldValue<DateTimeOffset>(1);
        await row.DisposeAsync();
        var known = await Resolve(c, session, context, source, false, until, ct);
        return new(new(context, known.ReadCut!.KnownAt, session.MarketContext is null ? "working" : "historical"), importUntil);
    }

    private void AdmitEditability(NpgsqlConnection c, ReadScope scope, CatalogueSource source)
    {
        if (scope.ImportUntil is {} until)
            database.RegisterCommitAdmission(c, new CurveMarkEditorAdmission(Catalog, scope.Context.Selected, source, true, until));
    }

    private async Task<ChoicePage> Choices(NpgsqlConnection c, CurveMarkEditorContext context, CatalogueSource source,
        string search, int offset, int limit, CancellationToken ct)
    {
        var page = await Read(c, "SELECT security.curve_mark_editor_setup($1,$2,$3,$4::jsonb,$5,$6,$7)::text", ct,
            Catalog, context.ContextId, context.AsOf, Body(source), search, offset, limit);
        return new(Field<ImmutableArray<CurveMarkEditorCurveChoice>>(page, "curves"), page.GetProperty("hasMoreCurves").GetBoolean(),
            page.GetProperty("canPublish").GetBoolean());
    }

    private async Task<ImmutableArray<CurveMarkEditorCellState>> Cells(NpgsqlConnection c, CurveMarkEditorContext context,
        CatalogueSource source, string kind, ImmutableArray<CurveMarkEditorCellKey> keys, bool working, CancellationToken ct)
    {
        var rows = await Read(c, "SELECT security.curve_mark_editor_cells($1,$2,$3,$4::jsonb,$5,$6::jsonb,$7)::text", ct,
            Catalog, context.ContextId, context.AsOf, Body(source), kind, Body(keys), working);
        var states = rows.Deserialize<ImmutableArray<CurveMarkEditorCellState>>(Json);
        if (!states.Select(s => s.Key).SequenceEqual(keys)) throw ContextError();
        return states;
    }

    private async Task<ImmutableArray<CurveMarkEditorCellState>> ViewStates(NpgsqlConnection c, ReadScope scope,
        CatalogueSource source, CurveMarkEditorView view, CurveMarkEditorLayout layout, CancellationToken ct)
    {
        var context = scope.Context.Selected;
        ImmutableArray<CurveMarkEditorCellKey> keys = [..layout.Cells.Where(cell => cell.Editable).Select(cell => cell.Key!)];
        if (keys.Length > 0)
            return await Cells(c, context, source, view.Kind, keys, scope.ImportUntil is not null, ct);

        // A one-curve matrix contains just its diagonal. Verify the selection using the
        // authorized, code-ordered catalogue without constructing a financial mark key.
        var code = view.Curves.Single();
        var limit = CurveMarkEditorDraft.Limits.PageSize;
        for (var offset = 0; ; offset += limit)
        {
            var page = await Choices(c, context, source, code, offset, limit, ct);
            if (page.Curves.Any(choice => choice.Code == code)) return [];
            if (!page.HasMoreCurves || string.CompareOrdinal(page.Curves[^1].Code, code) > 0)
                throw new CurveMarkEditorException("curve_mark_source", "Select an available curve from this exact source.");
        }
    }

    internal async Task<CurveMarkEditorSetupResponse> Setup(NpgsqlConnection c, PreviewSession session, CurveMarkEditorSetupRequest request, CancellationToken ct)
    {
        var scope = await ReadScopeFor(c, session, request.Context, request.Source, true, ct);
        var choices = await Choices(c, request.Context, request.Source, request.Search, request.Offset, request.Limit, ct);
        var canPublish = choices.CanPublish && scope.ImportUntil.HasValue;
        if (canPublish) AdmitEditability(c, scope, request.Source);
        var layout = request.View is null ? null : CurveMarkEditorDraft.ViewCells(request.View);
        var draft = layout is null ? null : CurveMarkEditorDraft.Validate(request.View!,
            await ViewStates(c, scope, request.Source, request.View!, layout, ct), []);
        return new(request.Source, scope.Context, await Owner(c, ct), canPublish, CurveMarkEditorDraft.Convention, CurveMarkEditorDraft.Limits,
            choices.Curves, choices.HasMoreCurves, request.View, layout, draft);
    }

    internal async Task<CurveMarkEditorValidateResponse> Validate(NpgsqlConnection c, PreviewSession session, CurveMarkEditorValidateRequest request, CancellationToken ct)
    {
        var scope = await ReadScopeFor(c, session, request.Context, request.Source, true, ct);
        var layout = CurveMarkEditorDraft.ViewCells(request.View);
        var states = await ViewStates(c, scope, request.Source, request.View, layout, ct);
        if (states.Any(state => state.EditBlock?.Code != "curve_mark_read_only")) AdmitEditability(c, scope, request.Source);
        if (request.Paste is {} paste)
        {
            var result = CurveMarkEditorDraft.PreviewPaste(request.View, states, request.Edits, paste);
            return new(request.Source, scope.Context, request.View, result.Draft, result.Addressed);
        }
        return new(request.Source, scope.Context, request.View, CurveMarkEditorDraft.Validate(request.View, states, request.Edits), []);
    }

    internal async Task<CurveMarkEditorHistoryResponse> History(NpgsqlConnection c, PreviewSession session, CurveMarkEditorHistoryRequest request, CancellationToken ct)
    {
        var scope = await ReadScopeFor(c, session, request.Context, request.Source, false, ct);
        var key = CurveMarkEditorDraft.NormalizeKey(request.Kind, request.Key);
        var page = await Read(c, "SELECT security.curve_mark_editor_history($1,$2,$3,$4::jsonb,$5,$6::jsonb,$7,$8)::text", ct,
            Catalog, request.Context.ContextId, request.Context.AsOf, Body(request.Source), request.Kind, Body(key), request.Offset, request.Limit);
        return new(request.Source, scope.Context, request.Kind, key, Field<ImmutableArray<CurveMarkEditorHistoryEntry>>(page, "revisions"),
            page.GetProperty("hasMore").GetBoolean());
    }
}
