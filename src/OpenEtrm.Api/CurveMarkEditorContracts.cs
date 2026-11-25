using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveMarkEditorContext(
    [property: JsonRequired] Guid ContextId,
    [property: JsonRequired, JsonConverter(typeof(CurveMarkEditorInstantConverter))] DateTimeOffset AsOf,
    [property: JsonRequired] DateOnly BusinessDate,
    Guid? SnapshotId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveMarkEditorView(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] string Mode,
    [property: JsonRequired] ImmutableArray<string> Curves,
    [property: JsonRequired] string FromMonth,
    [property: JsonRequired] string ToMonth);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveMarkEditorCellKey(
    [property: JsonRequired] string FirstCurve,
    [property: JsonRequired] string? SecondCurve,
    [property: JsonRequired] string Month);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveMarkEditorChange(
    [property: JsonRequired] string FirstCurve,
    [property: JsonRequired] string? SecondCurve,
    [property: JsonRequired] string Month,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int ExpectedRevision,
    [property: JsonRequired] string Value);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveMarkEditorBatch(
    [property: JsonRequired] string Policy,
    [property: JsonRequired] string OwnerId,
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] string Kind,
    [property: JsonRequired] CatalogueSource Source,
    [property: JsonRequired] CurveMarkEditorContext Context,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] ImmutableArray<CurveMarkEditorChange> Changes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveMarkEditorAddress(
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Row,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Column);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveMarkEditorEdit(
    [property: JsonRequired] CurveMarkEditorCellKey Key,
    [property: JsonRequired] string Value);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveMarkEditorPaste(
    [property: JsonRequired] string Mode,
    [property: JsonRequired] CurveMarkEditorAddress? Start,
    [property: JsonRequired] string Text);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveMarkEditorSetupRequest(
    [property: JsonRequired] CurveMarkEditorContext Context,
    [property: JsonRequired] CatalogueSource Source,
    [property: JsonRequired] CurveMarkEditorView? View,
    [property: JsonRequired] string Search,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Offset,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Limit);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveMarkEditorValidateRequest(
    [property: JsonRequired] CurveMarkEditorContext Context,
    [property: JsonRequired] CatalogueSource Source,
    [property: JsonRequired] CurveMarkEditorView View,
    [property: JsonRequired] ImmutableArray<CurveMarkEditorEdit> Edits,
    CurveMarkEditorPaste? Paste = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveMarkEditorHistoryRequest(
    [property: JsonRequired] CurveMarkEditorContext Context,
    [property: JsonRequired] CatalogueSource Source,
    [property: JsonRequired] string Kind,
    [property: JsonRequired] CurveMarkEditorCellKey Key,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Offset,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Limit);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveMarkEditorStatusRequest(
    [property: JsonRequired] string OwnerId,
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] string Kind);

public sealed record CurveMarkEditorLimits(int GridCurves, int VolatilityGridMonths, int StripMonths, int BatchCells, int PageSize, int ReasonCharacters);

// These are zero-based, row-major display addresses. A mirrored cell shares its canonical key with the editable cell.
public sealed record CurveMarkEditorViewCell(CurveMarkEditorAddress Address, CurveMarkEditorCellKey? Key, bool Editable);
public sealed record CurveMarkEditorLayout(int RowCount, int ColumnCount, ImmutableArray<CurveMarkEditorViewCell> Cells);

public sealed record CurveMarkEditorValue(Guid Id, int Revision, string Value, DateTimeOffset EffectiveAt, DateTimeOffset KnownAt);
public sealed record CurveMarkEditorError(string Code, string Message);
// The server supplies write admission. Zero is an admitted empty head, null means no admitted write revision.
public sealed record CurveMarkEditorCellState(CurveMarkEditorCellKey Key, CurveMarkEditorValue? Selected, CurveMarkEditorValue? WriteHead,
    int? ExpectedRevision, CurveMarkEditorError? EditBlock);
public sealed record CurveMarkEditorDraftCell(CurveMarkEditorCellState State, string? EnteredValue, string? ProposedValue,
    string? Difference, bool IsChanged, bool IsNewMark, CurveMarkEditorError? Error);
public sealed record CurveMarkEditorCoverage(int Marked, int Missing, int Changed, int Invalid);
public sealed record CurveMarkEditorDraftResult(ImmutableArray<CurveMarkEditorDraftCell> Cells, CurveMarkEditorCoverage Coverage);
public sealed record CurveMarkEditorPasteChange(CurveMarkEditorAddress Address, CurveMarkEditorCellKey Key, string Value);
public sealed record CurveMarkEditorPasteResult(ImmutableArray<CurveMarkEditorPasteChange> Addressed, CurveMarkEditorDraftResult Draft);

public sealed record CurveMarkEditorReadContext(CurveMarkEditorContext Selected, DateTimeOffset KnownAt, string Mode);
public sealed record CurveMarkEditorCurveChoice(string Code, string Name, string Currency);
public sealed record CurveMarkEditorSetupResponse(CatalogueSource Source, CurveMarkEditorReadContext Context, string OwnerId,
    bool CanPublish, string Convention, CurveMarkEditorLimits Limits, ImmutableArray<CurveMarkEditorCurveChoice> Curves,
    bool HasMoreCurves, CurveMarkEditorView? View, CurveMarkEditorLayout? Layout, CurveMarkEditorDraftResult? Draft);
public sealed record CurveMarkEditorValidateResponse(CatalogueSource Source, CurveMarkEditorReadContext Context, CurveMarkEditorView View,
    CurveMarkEditorDraftResult Draft, ImmutableArray<CurveMarkEditorPasteChange> Addressed);
public sealed record CurveMarkEditorHistoryEntry(CurveMarkEditorValue Mark, Guid? PreviousId, Guid ActorId, string Reason, bool EffectiveEligible);
public sealed record CurveMarkEditorHistoryResponse(CatalogueSource Source, CurveMarkEditorReadContext Context, string Kind,
    CurveMarkEditorCellKey Key, ImmutableArray<CurveMarkEditorHistoryEntry> Revisions, bool HasMore);
public sealed record CurveMarkEditorReviewedChange(CurveMarkEditorCellKey Key, string? SelectedValue, string ProposedValue,
    int ExpectedRevision, bool WouldChange);
public sealed record CurveMarkEditorReviewResponse(CurveMarkEditorBatch Batch, ImmutableArray<CurveMarkEditorReviewedChange> Changes);
public sealed record CurveMarkEditorReceiptCell(CurveMarkEditorCellKey Key, Guid MarkId, int Revision, string Value, bool Changed);
public sealed record CurveMarkEditorReceipt(string Policy, string OwnerId, Guid OperationId, string Kind, CatalogueSource Source,
    DateTimeOffset EffectiveAt, string Reason, ImmutableArray<CurveMarkEditorReceiptCell> Cells);
public sealed record CurveMarkEditorStatusResponse(string Status, CurveMarkEditorReceipt? Receipt);

public sealed class CurveMarkEditorException(string code, string message, CurveMarkEditorCellKey? key = null,
    CurveMarkEditorAddress? address = null) : MarketDataException(code, message)
{
    public CurveMarkEditorCellKey? Key { get; } = key;
    public CurveMarkEditorAddress? Address { get; } = address;
}

// Inspect the wire text before CLR parsing could assume a zone or discard sub-tick digits.
public sealed class CurveMarkEditorInstantConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Supply an explicit UTC curve mark context instant.");
        try { return TradingInstant.Parse(reader.GetString(), "curve_mark_input"); }
        catch (MarketDataException error) { throw new JsonException(error.Message, error); }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
