using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class CurveMarkEditorReceiptValidation
{
    internal static MarketDataException Unavailable() => new("curve_mark_receipt",
        "The retained publication evidence could not be verified. Keep the original request for reconciliation.");

    internal static CurveMarkEditorReceipt Require(CurveMarkEditorBatch batch, JsonElement outcome)
    {
        try
        {
            var receipt = outcome.Deserialize<CurveMarkEditorReceipt>(CurveMarkEditorEndpoints.Json) ?? throw Unavailable();
            if (receipt.Policy != batch.Policy || receipt.OwnerId != batch.OwnerId || receipt.OperationId != batch.OperationId ||
                receipt.Kind != batch.Kind || receipt.Source != batch.Source || receipt.Reason != batch.Reason ||
                receipt.EffectiveAt != batch.Context.AsOf || receipt.Cells.Length != batch.Changes.Length)
                throw Unavailable();

            var ids = new HashSet<Guid>();
            foreach (var (cell, change) in receipt.Cells.Zip(batch.Changes))
            {
                if (cell is null || cell.Key != new CurveMarkEditorCellKey(change.FirstCurve, change.SecondCurve, change.Month) ||
                    cell.MarkId == Guid.Empty || !ids.Add(cell.MarkId) || cell.Revision < 1 || cell.Value != change.Value ||
                    cell.HeadAtPublication is not {} head || head.MarkId == Guid.Empty || head.Revision < cell.Revision ||
                    !TradingInstant.IsSupported(head.EffectiveAt) || head.EffectiveAt < receipt.EffectiveAt ||
                    CurveMarkEditorDraft.NormalizeValue(batch.Kind, head.Value) != head.Value)
                    throw Unavailable();
                var acceptedIsHead = cell.Revision == head.Revision;
                if (acceptedIsHead != (head.MarkId == cell.MarkId) ||
                    acceptedIsHead && (head.Value != cell.Value || head.EffectiveAt != receipt.EffectiveAt) ||
                    cell.Changed && (!acceptedIsHead || (long)change.ExpectedRevision + 1 != cell.Revision))
                    throw Unavailable();
            }
            return receipt;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or CurveMarkEditorException)
        {
            throw Unavailable();
        }
    }
}
