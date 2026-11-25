using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;

internal static class CurveMarkEditorChecks
{
    private const string Volatility = "volatility";
    private const string Correlation = "correlation";
    private const string January = "2027-01";
    private const string February = "2027-02";
    private const string March = "2027-03";
    private const string First = "synthetic_first";
    private const string Second = "synthetic_second";
    private const string Third = "synthetic_third";
    private static readonly CurveMarkEditorView Grid = new(Volatility, "grid", [First, Second], January, March);
    private static readonly CurveMarkEditorView Matrix = new(Correlation, "grid", [Third, First, Second], January, January);
    private static readonly DateTimeOffset Effective = new(2026, 11, 24, 14, 0, 0, TimeSpan.Zero);
    private static readonly CurveMarkEditorContext Context = new(Guid.NewGuid(), Effective, new(2026, 11, 24));
    private static readonly CatalogueSource Source = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    internal static void Run()
    {
        Commands();
        Views();
        Analytics();
        Rectangles();
        Matrices();
        Console.WriteLine("PASS curve mark editor: exact commands, bounded views, draft analytics, addressed TSV and atomic matrix paste");
    }

    private static void Commands()
    {
        var command = new CurveMarkEditorBatch("curve_mark_batch_v1", "synthetic_owner", Guid.NewGuid(), Volatility,
            Source, Context, "  Synthetic mark correction  ",
            [new(Second, null, February, 0, "0.000000000001"), new(First, null, January, 2, "0.500000000000")]);
        var normalized = CurveMarkEditorDraft.NormalizeBatch(command);
        Require(normalized.Reason == "Synthetic mark correction" &&
            normalized.Changes.SequenceEqual([
                new(First, null, January, 2, "0.5"), new(Second, null, February, 0, "0.000000000001")]),
            "A batch must preserve exact values and predecessors while ordering the normalized keys.");
        Require(CurveMarkEditorDraft.NormalizeBatch(command with {Changes = [..command.Changes.Reverse()]})
            .Changes.SequenceEqual(normalized.Changes), "An equivalent ordering must have the same canonical changes.");

        var correlation = command with { Kind = Correlation, Changes = [new(Third, First, January, 0, "-0.125000000000")] };
        Require(CurveMarkEditorDraft.NormalizeBatch(correlation).Changes.Single() == new CurveMarkEditorChange(First, Third, January, 0, "-0.125"),
            "A reversed pair must normalize to one ordinal key while retaining its negative sign.");
        Reject("curve_mark_duplicate", () => CurveMarkEditorDraft.NormalizeBatch(correlation with
        {
            Changes = correlation.Changes.Add(new(First, Third, January, 1, "-0.25"))
        }));
        Reject("curve_mark_pair", () => CurveMarkEditorDraft.NormalizeBatch(correlation with { Changes = [new(First, First, January, 0, "1")] }));
        Reject("curve_mark_input", () => CurveMarkEditorDraft.NormalizeBatch(command with { Changes = [new(First, Second, January, 0, "1")] }));
        Reject("curve_mark_input", () => CurveMarkEditorDraft.NormalizeBatch(command with { Changes = [new(First, null, January, -1, "1")] }));
        Reject("curve_mark_input", () => CurveMarkEditorDraft.NormalizeBatch(command with { Policy = "unknown" }));
        Reject("curve_mark_input", () => CurveMarkEditorDraft.NormalizeBatch(command with { OperationId = Guid.Empty }));
        Reject("curve_mark_input", () => CurveMarkEditorDraft.NormalizeBatch(command with { OwnerId = " " }));
        Reject("curve_mark_input", () => CurveMarkEditorDraft.NormalizeBatch(command with { Source = Source with { ReferenceRevisionId = Guid.Empty } }));
        Reject("curve_mark_input", () => CurveMarkEditorDraft.NormalizeBatch(command with { Context = Context with { SnapshotId = Guid.Empty } }));
        Reject("curve_mark_input", () => CurveMarkEditorDraft.NormalizeBatch(command with { Context = Context with { AsOf = Effective.AddTicks(1) } }));
        Reject("curve_mark_input", () => CurveMarkEditorDraft.NormalizeBatch(command with { Reason = " \t\n" }));
        Reject("curve_mark_input", () => CurveMarkEditorDraft.NormalizeBatch(command with { Reason = new string('x', 501) }));
        Require(CurveMarkEditorDraft.NormalizeBatch(command with { Reason = string.Concat(Enumerable.Repeat("🙂", 500)) }).Reason.Length == 1000,
            "The reason limit must count Unicode scalars, as PostgreSQL does.");
        Reject("curve_mark_input", () => CurveMarkEditorDraft.NormalizeBatch(command with { Reason = "\uD800" }));
        Reject("curve_mark_bounds", () => CurveMarkEditorDraft.NormalizeBatch(command with { Changes = [] }));
        var changes = Enumerable.Range(1, 241).Select(i => new CurveMarkEditorChange($"synthetic_{i}", null, January, 0, "0")).ToImmutableArray();
        Require(CurveMarkEditorDraft.NormalizeBatch(command with { Changes = changes[..240] }).Changes.Length == 240,
            "A batch must accept 240 unique changes.");
        Reject("curve_mark_bounds", () => CurveMarkEditorDraft.NormalizeBatch(command with { Changes = changes }));

        foreach (var value in new[] { "-0.01", "10.000000000001", "0.1234567890120", "0.0000000000000", "1e-2", "", ".5", "0.5 ", "=1+1" })
            Reject("curve_mark_value", () => CurveMarkEditorDraft.NormalizeValue(Volatility, value));
        foreach (var value in new[] { "-1.000000000001", "1.000000000001", "2" })
            Reject("curve_mark_value", () => CurveMarkEditorDraft.NormalizeValue(Correlation, value));
        Require(CurveMarkEditorDraft.NormalizeValue(Volatility, "10.000000000000") == "10" &&
            CurveMarkEditorDraft.NormalizeValue(Correlation, "-1") == "-1" &&
            CurveMarkEditorDraft.NormalizeValue(Correlation, "-0.000000000001") == "-0.000000000001" &&
            CurveMarkEditorDraft.NormalizeValue(Correlation, "-0.000") == "0",
            "Boundary, signed fraction and explicit zero values must retain their exact meanings.");

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var payload = JsonSerializer.SerializeToNode(command, json)!.AsObject();
        payload["changes"]![0]!["value"] = 0.5;
        RejectJson(() => payload.Deserialize<CurveMarkEditorBatch>(json));
        payload["changes"]![0]!["value"] = "0.5";
        payload["changes"]![0]!["expectedRevision"] = "0";
        RejectJson(() => payload.Deserialize<CurveMarkEditorBatch>(json));
        payload["changes"]![0]!["expectedRevision"] = 0;
        foreach (var instant in new[] { "2026-11-24T14:00:00", "2026-11-24T14:00:00.0000001Z", "2026-11-24T14:00:00.00000000001Z" })
        {
            payload["context"]!["asOf"] = instant;
            RejectJson(() => payload.Deserialize<CurveMarkEditorBatch>(json));
        }
        payload["context"]!["asOf"] = "2026-11-24T14:00:00+00:00";
        Require(payload.Deserialize<CurveMarkEditorBatch>(json)?.Context.AsOf == Effective, "An explicit UTC context must survive JSON ingress.");
        payload["changes"]![0]!.AsObject().Remove("expectedRevision");
        RejectJson(() => payload.Deserialize<CurveMarkEditorBatch>(json));
    }

    private static void Views()
    {
        var curves = Enumerable.Range(1, 21).Select(i => $"synthetic_{i:00}").Reverse().ToImmutableArray();
        var grid = CurveMarkEditorDraft.ViewCells(new(Volatility, "grid", curves[..20], "1901-01", "1901-12"));
        Require(grid.RowCount == 20 && grid.ColumnCount == 12 && grid.Cells.Length == 240 &&
            grid.Cells[0] == new CurveMarkEditorViewCell(new(0, 0), new(curves[0], null, "1901-01"), true) &&
            grid.Cells[^1].Key == new CurveMarkEditorCellKey(curves[19], null, "1901-12"),
            "A maximum volatility grid must preserve explicit curve order and inclusive months.");
        Reject("curve_mark_bounds", () => CurveMarkEditorDraft.ViewCells(Grid with { Curves = curves }));
        Reject("curve_mark_bounds", () => CurveMarkEditorDraft.ViewCells(Grid with { ToMonth = "2028-01" }));
        Reject("curve_mark_bounds", () => CurveMarkEditorDraft.ViewCells(Grid with { FromMonth = "1900-12" }));
        Reject("curve_mark_bounds", () => CurveMarkEditorDraft.ViewCells(Grid with { ToMonth = "2200-01" }));
        Reject("curve_mark_bounds", () => CurveMarkEditorDraft.ViewCells(Grid with { FromMonth = March, ToMonth = January }));
        Reject("curve_mark_input", () => CurveMarkEditorDraft.ViewCells(Grid with { FromMonth = "2027-1" }));
        Reject("curve_mark_duplicate", () => CurveMarkEditorDraft.ViewCells(Grid with { Curves = [First, First] }));
        Reject("curve_mark_input", () => CurveMarkEditorDraft.ViewCells(Grid with { Curves = [First, " Synthetic"] }));
        Reject("curve_mark_bounds", () => CurveMarkEditorDraft.ViewCells(Matrix with { ToMonth = February }));
        Reject("curve_mark_bounds", () => CurveMarkEditorDraft.ViewCells(Grid with { Mode = "strip" }));

        var matrix = CurveMarkEditorDraft.ViewCells(Matrix);
        Require(matrix.RowCount == 3 && matrix.ColumnCount == 3 && matrix.Cells.Count(cell => cell.Editable) == 3 &&
            matrix.Cells[1] == new CurveMarkEditorViewCell(new(0, 1), new(First, Third, January), true) &&
            matrix.Cells[3] == new CurveMarkEditorViewCell(new(1, 0), new(First, Third, January), false) &&
            matrix.Cells[0] == new CurveMarkEditorViewCell(new(0, 0), null, false),
            "Only the upper triangle may address a new correlation, with the lower triangle mirroring it and no diagonal fact.");
        var largestMatrix = CurveMarkEditorDraft.ViewCells(Matrix with { Curves = curves[..20] });
        Require(largestMatrix.Cells.Length == 400 && largestMatrix.Cells.Count(cell => cell.Editable) == 190,
            "A 20-curve correlation matrix has precisely 190 independent mark cells.");

        var strip = new CurveMarkEditorView(Correlation, "strip", [Third, First], "2180-01", "2199-12");
        var stripCells = CurveMarkEditorDraft.ViewCells(strip);
        Require(stripCells.RowCount == 240 && stripCells.ColumnCount == 1 && stripCells.Cells.All(cell => cell.Editable) &&
            stripCells.Cells[^1] == new CurveMarkEditorViewCell(new(239, 0), new(First, Third, "2199-12"), true),
            "A 240-month paired strip must keep one canonical cell per month through the last admitted month.");
        Require(CurveMarkEditorDraft.ViewCells(Grid with { Mode = "strip", Curves = [First] }).Cells.Length == 3,
            "A volatility strip must contain the single selected curve.");
        Reject("curve_mark_bounds", () => CurveMarkEditorDraft.ViewCells(strip with { FromMonth = "2179-12" }));
    }

    private static void Analytics()
    {
        var selected = new CurveMarkEditorValue(Guid.NewGuid(), 1, "0.500000000000", Effective, Effective);
        var head = selected with { Id = Guid.NewGuid(), Revision = 2, Value = "0.7", EffectiveAt = Effective.AddDays(1) };
        ImmutableArray<CurveMarkEditorCellState> states =
        [
            new(Key(First, January), selected, selected, 1, null),
            new(Key(First, February), null, null, 0, null),
            new(Key(First, March), selected with { Value = "0" }, selected with { Value = "0" }, 1, null),
            new(Key(Second, January), selected, head, null, new("curve_mark_time", "A later-effective correction is already the head."))
        ];
        var result = CurveMarkEditorDraft.Validate(Grid, states,
            [new(Key(First, January), "0.6"), new(Key(First, February), "0"), new(Key(First, March), ""), new(Key(Second, January), "0.8")]);
        Require(result.Coverage == new CurveMarkEditorCoverage(3, 3, 2, 2),
            "Coverage must use selected marks, count selected zero, and count changed and invalid edits independently.");
        var changed = result.Cells[0];
        Require(changed.State.Selected?.Value == "0.5" && changed.EnteredValue == "0.6" && changed.ProposedValue == "0.6" &&
            changed.Difference == "0.1" && changed.IsChanged && !changed.IsNewMark && changed.Error is null,
            "The exact difference 0.6 - 0.5 must be 0.1.");
        var added = result.Cells[1];
        Require(added.State.Selected is null && added.State.ExpectedRevision == 0 && added.ProposedValue == "0" &&
            added.Difference is null && added.IsChanged && added.IsNewMark,
            "A new explicit zero must remain distinct from a missing mark and have no numerical predecessor.");
        Require(result.Cells[2].State.Selected?.Value == "0" && result.Cells[2].EnteredValue == "" &&
            result.Cells[2].ProposedValue is null && result.Cells[2].Error?.Code == "curve_mark_value",
            "Clearing a selected zero must preserve its raw invalid edit and omit a chartable value.");
        Require(result.Cells[3].State.WriteHead == head && result.Cells[3].Error?.Code == "curve_mark_time" && result.Cells[3].ProposedValue is null,
            "An explicitly blocked correction must keep the selected value and write head distinct.");
        Require(result.Cells[4].State.Selected is null && result.Cells[4].State.ExpectedRevision is null && result.Cells[4].EnteredValue is null,
            "Omitted state must represent missing selected data without asserting a write predecessor.");

        var same = CurveMarkEditorDraft.Validate(Grid, states, [new(Key(First, January), "0.500000000000")]).Cells[0];
        Require(same.ProposedValue == "0.5" && same.Difference == "0" && !same.IsChanged, "An equal normalized edit must have an exact zero difference.");
        var pair = new CurveMarkEditorCellKey(Third, First, January);
        var corr = CurveMarkEditorDraft.Validate(Matrix, [new(pair, selected, selected, 1, null)], [new(pair, "-0.25")]);
        Require(corr.Coverage == new CurveMarkEditorCoverage(1, 2, 1, 0) && corr.Cells[0].Difference == "-0.75",
            "A mirrored pair must affect one coverage cell and show its signed exact difference.");
        Reject("curve_mark_duplicate", () => CurveMarkEditorDraft.Validate(Matrix, [],
            [new(pair, "0.1"), new(new(First, Third, January), "0.2")]));
        Reject("curve_mark_bounds", () => CurveMarkEditorDraft.Validate(Grid, [], [new(Key(First, "2027-04"), "0")]));
        Reject("curve_mark_read_only", () => CurveMarkEditorDraft.PreviewPaste(Grid,
            [new(Key(First, January), selected, null, null, null)], [], new("rectangle", new(0, 0), "0")));
    }

    private static void Rectangles()
    {
        var strip = Grid with { Mode = "strip", Curves = [First] };
        var stripStates = EditableStates(strip);
        var stripPaste = CurveMarkEditorDraft.PreviewPaste(strip, stripStates, [], new("rectangle", new(1, 0), "0.4\n0"));
        Require(stripPaste.Addressed.SequenceEqual([
            new(new(1, 0), Key(First, February), "0.4"), new(new(2, 0), Key(First, March), "0")]),
            "A vertical strip rectangle must address successive months down one value column.");
        Reject("curve_mark_paste_bounds", () => CurveMarkEditorDraft.PreviewPaste(strip, stripStates, [],
            new("rectangle", new(1, 0), "0.4\t0")));

        var states = EditableStates(Grid);
        ImmutableArray<CurveMarkEditorEdit> original = [new(Key(First, February), "0.2"), new(Key(Second, March), "bad")];
        var paste = CurveMarkEditorDraft.PreviewPaste(Grid, states, original, new("rectangle", new(0, 0), "0.600000000000\t\r\n\t0\r\n"));
        Require(paste.Addressed.SequenceEqual([
            new(new(0, 0), Key(First, January), "0.6"), new(new(1, 1), Key(Second, February), "0")]),
            "The rectangle must preview only addressed nonblank replacements, including a zero.");
        Require(paste.Draft.Cells[1].EnteredValue == "0.2" && paste.Draft.Cells[5].EnteredValue == "bad",
            "A blank in the rectangle and cells outside it must preserve their existing raw edits.");
        Require(CurveMarkEditorDraft.PreviewPaste(Grid, states, original, new("rectangle", new(0, 0), "")).Addressed.IsEmpty,
            "An empty selected clipboard cell may leave a draft unchanged.");
        Reject("curve_mark_paste_shape", () => CurveMarkEditorDraft.PreviewPaste(Grid, states, original, new("rectangle", new(0, 0), "0\t0\n0")));
        Reject("curve_mark_paste_bounds", () => CurveMarkEditorDraft.PreviewPaste(Grid, states, original, new("rectangle", new(0, 2), "0\t")));
        Reject("curve_mark_paste_bounds", () => CurveMarkEditorDraft.PreviewPaste(Grid, states, original, new("rectangle", new(1, 0), "0\n0")));
        var error = Reject("curve_mark_value", () => CurveMarkEditorDraft.PreviewPaste(Grid, states, original, new("rectangle", new(0, 0), "0.9\t=1+1")));
        Require(error.Address == new CurveMarkEditorAddress(0, 1) && error.Key == Key(First, February),
            "An invalid paste value must identify its destination on refusal.");
        Require(original.SequenceEqual([new(Key(First, February), "0.2"), new(Key(Second, March), "bad")]),
            "Successful preview and every refused paste must preserve the entire original draft.");
    }

    private static void Matrices()
    {
        var states = EditableStates(Matrix);
        ImmutableArray<CurveMarkEditorEdit> original = [new(new(First, Second, January), "0.25")];
        var paste = CurveMarkEditorDraft.PreviewPaste(Matrix, states, original,
            new("full_matrix", null, "1.000000000000\t-0.5\t\n-0.500000000000\t\t\n0\t\t1\n"));
        Require(paste.Addressed.SequenceEqual([
            new(new(0, 1), new(First, Third, January), "-0.5"),
            new(new(0, 2), new(Second, Third, January), "0")]),
            "Full matrix paste must canonicalize mirrors, accept a lower-only explicit zero, and ignore valid diagonals.");
        Require(paste.Draft.Cells[2].EnteredValue == "0.25" && paste.Draft.Coverage == new CurveMarkEditorCoverage(0, 3, 3, 0),
            "An entirely blank mirror pair must preserve the existing edit and diagonal cells must never count.");
        Reject("curve_mark_paste_mirror", () => CurveMarkEditorDraft.PreviewPaste(Matrix, states, original,
            new("full_matrix", null, "1\t0.5\t\n0\t1\t\n\t\t1")));
        Reject("curve_mark_paste_diagonal", () => CurveMarkEditorDraft.PreviewPaste(Matrix, states, original,
            new("full_matrix", null, "0\t\t\n\t\t\n\t\t")));
        Reject("curve_mark_paste_shape", () => CurveMarkEditorDraft.PreviewPaste(Matrix, states, original,
            new("full_matrix", null, "1\t\n\t1")));
        Reject("curve_mark_paste_shape", () => CurveMarkEditorDraft.PreviewPaste(Matrix, states, original,
            new("full_matrix", null, "1\t\t\n\t1\t\n\t\t1\n\n")));
        Reject("curve_mark_paste_bounds", () => CurveMarkEditorDraft.PreviewPaste(Matrix, states, original,
            new("rectangle", new(2, 0), "0.5")));
        Reject("curve_mark_paste_bounds", () => CurveMarkEditorDraft.PreviewPaste(Matrix, states, original,
            new("rectangle", new(0, 1), "0.1\t\n0\t0.4")));
        Require(original.SequenceEqual([new(new(First, Second, January), "0.25")]),
            "Conflicting mirrors and invalid diagonals must preserve the whole old draft.");
    }

    private static CurveMarkEditorCellKey Key(string curve, string month) => new(curve, null, month);

    private static ImmutableArray<CurveMarkEditorCellState> EditableStates(CurveMarkEditorView view) =>
        [..CurveMarkEditorDraft.ViewCells(view).Cells.Where(cell => cell.Editable)
            .Select(cell => new CurveMarkEditorCellState(cell.Key!, null, null, 0, null))];

    private static CurveMarkEditorException Reject(string code, Action action)
    {
        try { action(); }
        catch (CurveMarkEditorException error) when (error.Code == code) { return error; }
        throw new Exception($"An operation accepted input requiring {code}.");
    }

    private static void RejectJson(Action action)
    {
        try { action(); }
        catch (JsonException) { return; }
        throw new Exception("An incomplete or non-exact batch JSON payload was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
