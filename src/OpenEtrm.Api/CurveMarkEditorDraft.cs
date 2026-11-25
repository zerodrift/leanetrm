using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public static partial class CurveMarkEditorDraft
{
    public const string Policy = "curve_mark_batch_v1";
    public const string Convention = ExerciseMarks.HeldLogReturn;
    public const string Volatility = "volatility";
    public const string Correlation = "correlation";
    private const int FractionPlaces = 12;

    public static CurveMarkEditorLimits Limits { get; } = new(20, 12, 240, 240, 50, 500);
    private static readonly ExactDecimal One = ExactDecimal.Parse("1");
    private static readonly ExactDecimal Ten = ExactDecimal.Parse("10");
    private static readonly CurveMarkEditorError ReadOnly = new("curve_mark_read_only", "No write revision is available for this mark.");

    [GeneratedRegex(@"\A[a-z][a-z0-9_.-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CurveCode();

    public static CurveMarkEditorBatch NormalizeBatch(CurveMarkEditorBatch batch)
    {
        if (batch is null || batch.Policy != Policy || string.IsNullOrWhiteSpace(batch.OwnerId) || batch.OperationId == Guid.Empty ||
            batch.Source is not { } source || source.DefinitionRevisionId == Guid.Empty || source.ReferenceCatalogId == Guid.Empty ||
            source.ReferenceRevisionId == Guid.Empty || batch.Context is not { } context || context.ContextId == Guid.Empty ||
            context.BusinessDate == default || context.SnapshotId == Guid.Empty || !TradingInstant.IsSupported(context.AsOf))
            throw Error("curve_mark_input", "Supply the exact source, context, account, operation and batch policy.");
        RequireKind(batch.Kind);
        var reason = NormalizeReason(batch.Reason);
        if (batch.Changes.IsDefaultOrEmpty || batch.Changes.Length > Limits.BatchCells)
            throw Error("curve_mark_bounds", $"A batch must contain 1 to {Limits.BatchCells} changes.");

        var seen = new HashSet<CurveMarkEditorCellKey>();
        var changes = ImmutableArray.CreateBuilder<CurveMarkEditorChange>(batch.Changes.Length);
        foreach (var change in batch.Changes)
        {
            if (change is null)
                throw Error("curve_mark_input", "Each change requires an exact key, expected revision and value.");
            var key = NormalizeKey(batch.Kind, new(change.FirstCurve, change.SecondCurve, change.Month));
            if (!seen.Add(key))
                throw Error("curve_mark_duplicate", "A batch can contain each mark key only once.", key);
            if (change.ExpectedRevision < 0)
                throw Error("curve_mark_input", "The expected revision must be a nonnegative integer.", key);
            var value = ParseValue(batch.Kind, change.Value, key).ToString();
            changes.Add(new(key.FirstCurve, key.SecondCurve, key.Month, change.ExpectedRevision, value));
        }
        return batch with
        {
            Reason = reason,
            Changes = [..changes.OrderBy(change => change.FirstCurve, StringComparer.Ordinal)
                .ThenBy(change => change.SecondCurve, StringComparer.Ordinal).ThenBy(change => change.Month, StringComparer.Ordinal)]
        };
    }

    public static CurveMarkEditorCellKey NormalizeKey(string kind, CurveMarkEditorCellKey key)
    {
        RequireKind(kind);
        if (key is null || !IsCurveCode(key.FirstCurve) ||
            (kind == Volatility ? key.SecondCurve is not null : !IsCurveCode(key.SecondCurve)))
            throw Error("curve_mark_input", "Supply the exact curve code for volatility or both exact curve codes for correlation.", key);
        _ = ParseMonth(key.Month, key);
        if (key.SecondCurve is null) return key;
        var order = string.CompareOrdinal(key.FirstCurve, key.SecondCurve);
        if (order == 0)
            throw Error("curve_mark_pair", "A same-curve pair has no correlation mark.", key);
        return order < 0 ? key : new(key.SecondCurve, key.FirstCurve, key.Month);
    }

    public static string NormalizeValue(string kind, string? value)
    {
        RequireKind(kind);
        return ParseValue(kind, value).ToString();
    }

    public static CurveMarkEditorLayout ViewCells(CurveMarkEditorView view)
    {
        if (view is null || view.Mode is not ("grid" or "strip"))
            throw Error("curve_mark_input", "Select a curve mark grid or strip.");
        RequireKind(view.Kind);
        var from = ParseMonth(view.FromMonth);
        var to = ParseMonth(view.ToMonth);
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month + 1;
        var curveCount = view.Curves.IsDefault ? 0 : view.Curves.Length;
        var matrix = view.Kind == Correlation && view.Mode == "grid";
        var strip = view.Mode == "strip";
        var maxMonths = strip ? Limits.StripMonths : matrix ? 1 : Limits.VolatilityGridMonths;
        if (curveCount < 1 || curveCount > Limits.GridCurves || months < 1 || months > maxMonths ||
            strip && curveCount != (view.Kind == Volatility ? 1 : 2))
            throw Error("curve_mark_bounds", "Select curves and an inclusive month range within the limits for this view.");
        if (!view.Curves.All(IsCurveCode))
            throw Error("curve_mark_input", "Select exact curve codes.");
        if (view.Curves.Distinct(StringComparer.Ordinal).Count() != curveCount)
            throw Error("curve_mark_duplicate", "Select each curve only once.");

        var rows = strip ? months : curveCount;
        var columns = matrix ? curveCount : strip ? 1 : months;
        var cells = ImmutableArray.CreateBuilder<CurveMarkEditorViewCell>(rows * columns);
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var address = new CurveMarkEditorAddress(row, column);
            if (matrix && row == column)
            {
                cells.Add(new(address, null, false));
                continue;
            }
            var month = matrix ? view.FromMonth : from.AddMonths(strip ? row : column).ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var first = view.Curves[strip ? 0 : row];
            var second = view.Kind == Volatility ? null : view.Curves[matrix ? column : 1];
            var key = NormalizeKey(view.Kind, new(first, second, month));
            cells.Add(new(address, key, !matrix || row < column));
        }
        return new(rows, columns, cells.MoveToImmutable());
    }

    public static CurveMarkEditorDraftResult Validate(CurveMarkEditorView view, ImmutableArray<CurveMarkEditorCellState> states,
        ImmutableArray<CurveMarkEditorEdit> edits)
    {
        var keys = EditableKeys(ViewCells(view));
        var scope = keys.ToHashSet();
        return Evaluate(view.Kind, keys, Index(view.Kind, scope, states, state => state.Key), Index(view.Kind, scope, edits, edit => edit.Key));
    }

    public static CurveMarkEditorPasteResult PreviewPaste(CurveMarkEditorView view, ImmutableArray<CurveMarkEditorCellState> states,
        ImmutableArray<CurveMarkEditorEdit> edits, CurveMarkEditorPaste paste)
    {
        var layout = ViewCells(view);
        var keys = EditableKeys(layout);
        var scope = keys.ToHashSet();
        var stateIndex = Index(view.Kind, scope, states, state => state.Key);
        var merged = Index(view.Kind, scope, edits, edit => edit.Key);
        if (paste is null || paste.Text is null)
            throw Error("curve_mark_paste_shape", "Supply plain-text TSV and an explicit paste mode.");

        var addressed = paste.Mode switch
        {
            "rectangle" => Rectangle(view.Kind, layout, paste),
            "full_matrix" when view.Kind == Correlation && view.Mode == "grid" && paste.Start is null => FullMatrix(layout, paste.Text),
            _ => throw Error("curve_mark_paste_shape", "Select a rectangle start or paste a complete correlation matrix.")
        };
        foreach (var change in addressed)
        {
            if (WriteError(stateIndex.GetValueOrDefault(change.Key)) is { } blocked)
                throw Error(blocked.Code, blocked.Message, change.Key, change.Address);
            merged[change.Key] = new(change.Key, change.Value);
        }
        return new(addressed, Evaluate(view.Kind, keys, stateIndex, merged));
    }

    private static ImmutableArray<CurveMarkEditorCellKey> EditableKeys(CurveMarkEditorLayout layout) =>
        [..layout.Cells.Where(cell => cell.Editable).Select(cell => cell.Key!)];

    private static Dictionary<CurveMarkEditorCellKey, T> Index<T>(string kind, HashSet<CurveMarkEditorCellKey> scope,
        ImmutableArray<T> inputs, Func<T, CurveMarkEditorCellKey> keyOf) where T : class
    {
        if (inputs.IsDefault)
            throw Error("curve_mark_input", "Supply explicit editor states and edits.");
        if (inputs.Length > Limits.BatchCells)
            throw Error("curve_mark_bounds", $"Supply at most {Limits.BatchCells} states or edits.");
        var indexed = new Dictionary<CurveMarkEditorCellKey, T>();
        foreach (var input in inputs)
        {
            if (input is null) throw Error("curve_mark_input", "Each cell entry requires an exact key.");
            var key = NormalizeKey(kind, keyOf(input));
            if (!scope.Contains(key)) throw Error("curve_mark_bounds", "The mark is outside the selected view.", key);
            if (!indexed.TryAdd(key, input)) throw Error("curve_mark_duplicate", "A mark key occurs more than once.", key);
        }
        return indexed;
    }

    private static CurveMarkEditorDraftResult Evaluate(string kind, ImmutableArray<CurveMarkEditorCellKey> keys,
        Dictionary<CurveMarkEditorCellKey, CurveMarkEditorCellState> states, Dictionary<CurveMarkEditorCellKey, CurveMarkEditorEdit> edits)
    {
        var cells = ImmutableArray.CreateBuilder<CurveMarkEditorDraftCell>(keys.Length);
        var marked = 0;
        var changed = 0;
        var invalid = 0;
        foreach (var key in keys)
        {
            var state = states.GetValueOrDefault(key) ?? new(key, null, null, null, null);
            var selected = state.Selected is { } mark ? ParseValue(kind, mark.Value, key) : (ExactDecimal?)null;
            state = state with
            {
                Key = key,
                Selected = state.Selected is { } previous ? previous with { Value = selected!.Value.ToString() } : null,
                WriteHead = state.WriteHead is { } head ? head with { Value = ParseValue(kind, head.Value, key).ToString() } : null
            };
            if (selected.HasValue) marked++;
            if (!edits.TryGetValue(key, out var edit))
            {
                cells.Add(new(state, null, null, null, false, false, null));
                continue;
            }

            var error = WriteError(state);
            if (error is null)
            {
                try
                {
                    var proposed = ParseValue(kind, edit.Value, key);
                    var isChanged = proposed != selected;
                    var difference = (proposed - selected)?.ToString();
                    if (isChanged) changed++;
                    cells.Add(new(state, edit.Value, proposed.ToString(), difference, isChanged, !selected.HasValue, null));
                    continue;
                }
                catch (CurveMarkEditorException invalidValue) { error = new(invalidValue.Code, invalidValue.Message); }
            }
            invalid++;
            cells.Add(new(state, edit.Value, null, null, false, false, error));
        }
        return new(cells.MoveToImmutable(), new(marked, keys.Length - marked, changed, invalid));
    }

    private static CurveMarkEditorError? WriteError(CurveMarkEditorCellState? state) =>
        state?.EditBlock ?? (state?.ExpectedRevision is >= 0 ? null : ReadOnly);

    private static ImmutableArray<CurveMarkEditorPasteChange> Rectangle(string kind, CurveMarkEditorLayout layout, CurveMarkEditorPaste paste)
    {
        if (paste.Start is not { } start || start.Row < 0 || start.Row >= layout.RowCount || start.Column < 0 || start.Column >= layout.ColumnCount ||
            !CellAt(layout, start.Row, start.Column).Editable)
            throw Error("curve_mark_paste_bounds", "Start the rectangle at an editable cell.", address: paste.Start);
        var rows = ClipboardRows(paste.Text, layout.RowCount - start.Row, layout.ColumnCount - start.Column, "curve_mark_paste_bounds");
        var changes = ImmutableArray.CreateBuilder<CurveMarkEditorPasteChange>();
        for (var row = 0; row < rows.Count; row++)
        for (var column = 0; column < rows[row].Length; column++)
        {
            var input = rows[row][column];
            if (input.Length == 0) continue;
            var cell = CellAt(layout, start.Row + row, start.Column + column);
            if (!cell.Editable)
                throw Error("curve_mark_paste_bounds", "The rectangle addresses a display-only cell.", cell.Key, cell.Address);
            changes.Add(new(cell.Address, cell.Key!, ParseValue(kind, input, cell.Key, cell.Address).ToString()));
        }
        return changes.ToImmutable();
    }

    private static ImmutableArray<CurveMarkEditorPasteChange> FullMatrix(CurveMarkEditorLayout layout, string text)
    {
        const string shape = "curve_mark_paste_shape";
        const string diagonal = "curve_mark_paste_diagonal";
        const string diagonalMessage = "A matrix diagonal accepts only blank or exact one.";
        var size = layout.RowCount;
        var rows = ClipboardRows(text, size, size, shape);
        if (rows.Count != size || rows[0].Length != size)
            throw Error(shape, "Supply exactly one row and column per selected curve.");

        var changes = ImmutableArray.CreateBuilder<CurveMarkEditorPasteChange>();
        for (var row = 0; row < size; row++)
        {
            if (rows[row][row] is { Length: > 0 } sameCurve)
            {
                var address = new CurveMarkEditorAddress(row, row);
                try
                {
                    if (ParseValue(Correlation, sameCurve, address: address) != One)
                        throw Error(diagonal, diagonalMessage, address: address);
                }
                catch (CurveMarkEditorException error) when (error.Code == "curve_mark_value")
                {
                    throw Error(diagonal, diagonalMessage, address: address);
                }
            }
            for (var column = row + 1; column < size; column++)
            {
                var cell = CellAt(layout, row, column);
                var upper = rows[row][column].Length == 0 ? (ExactDecimal?)null :
                    ParseValue(Correlation, rows[row][column], cell.Key, cell.Address);
                var lower = rows[column][row].Length == 0 ? (ExactDecimal?)null :
                    ParseValue(Correlation, rows[column][row], cell.Key, new(column, row));
                if (upper.HasValue && lower.HasValue && upper != lower)
                    throw Error("curve_mark_paste_mirror", "The supplied mirrors must have equal exact values.", cell.Key, cell.Address);
                if ((upper ?? lower) is { } proposed)
                    changes.Add(new(cell.Address, cell.Key!, proposed.ToString()));
            }
        }
        return changes.ToImmutable();
    }

    private static CurveMarkEditorViewCell CellAt(CurveMarkEditorLayout layout, int row, int column) =>
        layout.Cells[row * layout.ColumnCount + column];

    private static List<string[]> ClipboardRows(string text, int maxRows, int maxColumns, string sizeError)
    {
        // ReadLine consumes one terminal line ending while preserving internal empty rows.
        using var reader = new StringReader(text);
        var rows = new List<string[]>(maxRows);
        var line = reader.ReadLine() ?? "";
        do
        {
            if (rows.Count == maxRows) throw Error(sizeError, "The pasted rows exceed the selected area.");
            var cells = new List<string>(maxColumns);
            var span = line.AsSpan();
            foreach (var range in span.Split('\t'))
            {
                if (cells.Count == maxColumns) throw Error(sizeError, "The pasted columns exceed the selected area.");
                var value = span[range];
                cells.Add(value.Trim().IsEmpty ? "" : value.ToString());
            }
            if (rows.Count > 0 && cells.Count != rows[0].Length)
                throw Error("curve_mark_paste_shape", "Every pasted row must have the same number of columns.");
            rows.Add([..cells]);
        } while ((line = reader.ReadLine()) is not null);
        return rows;
    }

    private static ExactDecimal ParseValue(string kind, string? text, CurveMarkEditorCellKey? key = null, CurveMarkEditorAddress? address = null)
    {
        var dot = text?.IndexOf('.') ?? -1;
        if (text is null || (dot >= 0 && text.Length - dot - 1 > FractionPlaces))
            throw Error("curve_mark_value", $"Supply a plain decimal with at most {FractionPlaces} fractional places.", key, address);
        ExactDecimal value;
        try { value = ExactDecimal.Parse(text); }
        catch (MarketDataException)
        {
            throw Error("curve_mark_value", $"Supply a plain decimal with at most {FractionPlaces} fractional places.", key, address);
        }
        if (kind == Volatility ? value.CompareTo(default) < 0 || value.CompareTo(Ten) > 0 :
            value.CompareTo(-One) < 0 || value.CompareTo(One) > 0)
            throw Error("curve_mark_value", kind == Volatility ? "Volatility must be from zero to ten." : "Correlation must be from minus one to one.", key, address);
        return value;
    }

    private static DateOnly ParseMonth(string? month, CurveMarkEditorCellKey? key = null)
    {
        if (month is not { Length: 7 } || !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw Error("curve_mark_input", "Supply a delivery month in yyyy-MM format.", key);
        if (date.Year is < 1901 or > 2199)
            throw Error("curve_mark_bounds", "Delivery months must be within 1901–2199.", key);
        return date;
    }

    private static string NormalizeReason(string? reason)
    {
        static CurveMarkEditorException Invalid() =>
            Error("curve_mark_input", $"Supply a nonblank reason of at most {Limits.ReasonCharacters} characters.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Contains('\0')) throw Invalid();
        var remaining = reason.AsSpan();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            if (++count > Limits.ReasonCharacters || Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done)
                throw Invalid();
            remaining = remaining[consumed..];
        }
        return reason.Trim();
    }

    private static bool IsCurveCode(string? code) => code is { Length: > 0 and <= 64 } && CurveCode().IsMatch(code);

    private static void RequireKind(string kind)
    {
        if (kind is not (Volatility or Correlation)) throw Error("curve_mark_input", "Select volatility or correlation.");
    }

    private static CurveMarkEditorException Error(string code, string message,
        CurveMarkEditorCellKey? key = null, CurveMarkEditorAddress? address = null) => new(code, message, key, address);
}
