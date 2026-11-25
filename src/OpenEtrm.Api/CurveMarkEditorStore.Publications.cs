using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

internal sealed partial class CurveMarkEditorStore
{
    private sealed record Recovered(CurveMarkEditorBatch Batch, CurveMarkEditorReceipt Receipt);

    private static JsonElement Command(CurveMarkEditorBatch batch) => JsonSerializer.SerializeToElement(new
    {
        batch.Policy, batch.OwnerId, batch.OperationId, batch.Kind, batch.Source, batch.Context.SnapshotId, batch.Reason, batch.Changes
    }, Json);

    private async Task<Recovered?> Recover(NpgsqlConnection c, CurveMarkEditorStatusRequest identity, bool writing,
        DateTimeOffset until, CancellationToken ct)
    {
        if (identity.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(identity.OwnerId) ||
            identity.Kind is not (CurveMarkEditorDraft.Volatility or CurveMarkEditorDraft.Correlation))
            throw new CurveMarkEditorException("curve_mark_input", "Supply the original publication owner, operation and kind.");
        var status = await Read(c, "SELECT security.curve_mark_editor_status($1,$2,$3,$4)::text",
            ct, Catalog, identity.OwnerId, identity.OperationId, identity.Kind);
        if (!status.GetProperty("found").GetBoolean())
        {
            if (!writing) database.RegisterCommitAdmission(c, new CurveMarkEditorAdmission(Catalog, null, null, false, until));
            return null;
        }

        try
        {
            // The native owner/source check precedes registration of these private coordinates.
            var inputs = Field<ReadInputCoordinates>(status, "sourceInputs");
            var known = await inputs.Resolve(c, Catalog, ct);
            var command = status.GetProperty("command");
            var context = new CurveMarkEditorContext(status.Id("sourceContextId"), status.Instant("effectiveAt"),
                inputs.BusinessOn, command.GetProperty("snapshotId").Deserialize<Guid?>(Json));
            var source = Field<CatalogueSource>(status, "source");
            var batch = CurveMarkEditorDraft.NormalizeBatch(new(command.Text("policy"), identity.OwnerId, identity.OperationId,
                identity.Kind, source, context, command.Text("reason"), Field<ImmutableArray<CurveMarkEditorChange>>(command, "changes")));
            if (known.ContextId != context.ContextId || !JsonElement.DeepEquals(command, Command(batch)))
                throw CurveMarkEditorReceiptValidation.Unavailable();
            var receipt = CurveMarkEditorReceiptValidation.Require(batch, status.GetProperty("outcome"));
            var admission = new CurveMarkEditorAdmission(Catalog, context, source, writing, until);
            await admission.RequireCommit(c, ct);
            database.RegisterCommitAdmission(c, admission);
            return new(batch, receipt);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or CurveMarkEditorException)
        {
            throw CurveMarkEditorReceiptValidation.Unavailable();
        }
    }

    internal async Task<CurveMarkEditorStatusResponse> Status(NpgsqlConnection c, CurveMarkEditorStatusRequest request, CancellationToken ct)
    {
        var until = await Deadline(c, false, ct);
        var result = await Recover(c, request, false, until, ct);
        return result is null ? new("absent", null) : new("committed", result.Receipt);
    }

    private async Task<(CurveMarkEditorBatch Batch, bool AlreadyRecorded)> Prepare(NpgsqlConnection c, PreviewSession session, CurveMarkEditorBatch request, CancellationToken ct)
    {
        if (session.MarketContext is not null)
            throw new MarketDataException("historical_read_only", "Return to current working data to publish curve marks.");
        var batch = CurveMarkEditorDraft.NormalizeBatch(request);
        var until = await Deadline(c, true, ct);
        var prior = await Recover(c, new(batch.OwnerId, batch.OperationId, batch.Kind), true, until, ct);
        if (prior is null)
            await Resolve(c, session, batch.Context, batch.Source, true, until, ct);
        else if (prior.Batch.Context != batch.Context || !JsonElement.DeepEquals(Command(prior.Batch), Command(batch)))
            throw new MarketDataException("curve_mark_request_conflict", "This operation already has a different request. Recover its original outcome.");
        return (batch, prior is not null);
    }

    private async Task<CurveMarkEditorReceipt> Write(NpgsqlConnection c, CurveMarkEditorBatch batch, CancellationToken ct)
    {
        var outcome = await Read(c, "SELECT security.record_curve_mark_batch($1,$2,$3,$4::jsonb)::text", ct,
            Catalog, batch.Context.ContextId, batch.Context.AsOf, Command(batch).GetRawText());
        return CurveMarkEditorReceiptValidation.Require(batch, outcome);
    }

    internal async Task<CurveMarkEditorReceipt> Record(NpgsqlConnection c, PreviewSession session, CurveMarkEditorBatch request, CancellationToken ct)
        => await Write(c, (await Prepare(c, session, request, ct)).Batch, ct);

    internal async Task<CurveMarkEditorReviewResponse> Review(NpgsqlConnection c, PreviewSession session, CurveMarkEditorBatch request, CancellationToken ct)
    {
        var (batch, alreadyRecorded) = await Prepare(c, session, request, ct);
        ImmutableArray<CurveMarkEditorCellKey> keys = [..batch.Changes.Select(change =>
            new CurveMarkEditorCellKey(change.FirstCurve, change.SecondCurve, change.Month))];
        var states = await Cells(c, batch.Context, batch.Source, batch.Kind, keys, true, ct);
        await using (var save = ScheduleStore.Command(c, "SAVEPOINT curve_mark_review")) await save.ExecuteNonQueryAsync(ct);
        CurveMarkEditorReceipt receipt;
        try { receipt = await Write(c, batch, ct); }
        finally
        {
            await using var restore = ScheduleStore.Command(c, "ROLLBACK TO SAVEPOINT curve_mark_review; RELEASE SAVEPOINT curve_mark_review");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var changes = ImmutableArray.CreateBuilder<CurveMarkEditorReviewedChange>(keys.Length);
        for (var i = 0; i < keys.Length; i++)
        {
            var cell = receipt.Cells[i];
            var head = cell.HeadAtPublication;
            changes.Add(new(keys[i], states[i].Selected?.Value, cell.Value, batch.Changes[i].ExpectedRevision, cell.Changed && !alreadyRecorded,
                cell.Changed && !alreadyRecorded ? null : cell.Revision, alreadyRecorded ? null : new(head.Revision, head.Value, head.EffectiveAt)));
        }
        return new(batch, changes.MoveToImmutable(), alreadyRecorded);
    }
}
