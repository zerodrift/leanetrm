using System.Globalization;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record SchedulePreviewRequest(string? Name, string? Symbol, string? Curve, int Position, string? DateRole, string? At);
public sealed record ShortcutResolveRequest(string? Symbol, string? At);

public static class SchedulePreview
{
    public static DateTimeOffset RequiredMilestoneInstant(System.Text.Json.JsonElement milestone)
    {
        if (!milestone.TryGetProperty("event_at", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String)
            throw new MarketDataException("sequence_definition", "The selected trading milestone needs an explicit UTC instant.");
        try
        {
            var at = ParseAt(value.GetString());
            if (at <= DateTimeOffset.MinValue || at.Year >= 9999) throw new MarketDataException("sequence_context", "Invalid instant.");
            return at;
        }
        catch (MarketDataException) { throw new MarketDataException("sequence_definition", "The selected trading milestone needs a finite explicit UTC instant."); }
    }
    public static ContractSequence Compile(PreviewData data, PreviewSequenceCoverage coverage)
    {
        var revision = new DefinitionRevision(data.Definitions["revisions"][0].Id("catalog_id"), data.Definitions["revisions"][0].Id("id"));
        var references = new ReferenceRevision(data.Definitions["revisions"][0].Id("reference_catalog_id"), data.Definitions["revisions"][0].Id("reference_revision_id"));
        var rows = data.Definitions["curve_members"].Where(r => r.Text("curve_code") == coverage.Curve)
            .OrderBy(r => r.GetProperty("ordinal").GetInt32()).ToArray();
        var entries = rows.Select((row, i) => {
            if (row.GetProperty("ordinal").GetInt32() != i) throw new MarketDataException("sequence_definition", "The retained curve order has a gap.");
            var contract = data.Contracts.Single(c => c.Key.Id == row.Id("contract_id"));
            if (contract.FuturesSeries is not { } series) throw new MarketDataException("sequence_source", "This source needs explicit supported futures terms.");
            System.Text.Json.JsonElement Milestone(string role)
            {
                var matches = data.References["futures_milestones"].Where(m => m.Text("series_code") == series.Code && m.Text("milestone") == role).ToArray();
                if (matches.Length != 1 || matches[0].Text("value_state") != "known")
                    throw new MarketDataException("sequence_definition", "A selected first/last-trading milestone is missing or unknown.");
                return matches[0];
            }
            var first = Milestone("first_trading"); var last = Milestone("last_trading");
            if (RequiredMilestoneInstant(last) != contract.Expiry) throw new MarketDataException("sequence_definition", "The contract expiry differs from its selected last-trading milestone.");
            return new ContractSequenceEntry(i + 1, contract, FuturesStore.Binding(data,coverage.Curve,contract.Key.Id,contract.Key.Id,data.FuturesConvention).EligibleFrom,
                DateOnly.ParseExact(last.Text("business_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        });
        return new(new(revision, coverage.Curve), references, coverage.From, coverage.To, entries);
    }
    public static ScheduleShortcut Create(ContractSequence sequence, SchedulePreviewRequest request)
    {
        var role = DateRoles.Where(r => r.Code == request.DateRole).Select(r => (ScheduleDateRole?)r.Value).SingleOrDefault()
            ?? throw new MarketDataException("shortcut_definition", "Choose the date that this shortcut returns.");
        return new(request.Symbol!, request.Name!, sequence, request.Position, role);
    }
    // The dates a shortcut can return: one table drives parsing, display codes and the editor's choices.
    private static readonly (string Code, string Name, ScheduleDateRole Value)[] DateRoles = [
        ("delivery_start", "Delivery start", ScheduleDateRole.DeliveryStart),
        ("delivery_end", "Delivery end · not included", ScheduleDateRole.DeliveryEnd),
        ("last_trading", "Last trading date", ScheduleDateRole.LastTradingDate)];
    public static object DateRoleChoices => DateRoles.Select(r => new { code = r.Code, name = r.Name });
    public static DateTimeOffset ParseAt(string? value)
    {
        if (!OpenEtrm.Shared.Temporal.UtcInstant.TryParse(value,out var at))
            throw new MarketDataException("sequence_context", "Supply an explicit UTC valuation instant.");
        return at;
    }
    public static object Describe(ScheduleShortcut shortcut) => new { shortcut.Name, shortcut.Symbol,
        curve = shortcut.Sequence.Key.Code, shortcut.Position, dateRole = Role(shortcut.DateRole) };
    public static object Result(ScheduleShortcut shortcut, DateTimeOffset at)
    {
        var result = shortcut.Resolve(at);
        return new { result.Symbol, result.Sequence, result.References, result.RuleVersion, result.Position,
            dateRole = Role(result.DateRole), result.ValuationAt, contractId = result.Contract.Id,
            contractKey = result.Contract, result.Date };
    }
    public static string Role(ScheduleDateRole role) => DateRoles.Where(r => r.Value == role).Select(r => r.Code).SingleOrDefault()
        ?? throw new MarketDataException("shortcut_definition", "Unsupported date role.");
}

// Private preview artifacts, not published reference data. Caller holds its session gate.
public sealed class PreviewShortcutLibrary
{
    private readonly Dictionary<(string Owner, string Symbol), ScheduleShortcut> values = [];
    public IEnumerable<ScheduleShortcut> For(string owner) => values.Where(p => p.Key.Owner == owner).Select(p => p.Value);
    public ScheduleShortcut Find(string owner, string? symbol) => symbol is not null && values.TryGetValue((owner, symbol), out var found)
        ? found : throw new MarketDataException("shortcut_missing", "Choose a shortcut kept in this persona's preview session.");
    public ScheduleShortcut Keep(string owner, ScheduleShortcut shortcut)
    {
        var key = (owner, shortcut.Symbol);
        if (values.TryGetValue(key, out var old))
        {
            if (old.Name != shortcut.Name || old.Position != shortcut.Position || old.DateRole != shortcut.DateRole ||
                old.Sequence.Key != shortcut.Sequence.Key || old.Sequence.References != shortcut.Sequence.References ||
                old.Sequence.From != shortcut.Sequence.From || old.Sequence.To != shortcut.Sequence.To)
                throw new MarketDataException("shortcut_conflict", "This shortcut already has different terms. Choose a new shortcut name.");
            return old;
        }
        if (For(owner).Count() >= 32) throw new MarketDataException("preview_limit", "This persona already has 32 preview shortcuts.");
        var shared = For(owner).Select(s => s.Sequence).FirstOrDefault(s => s.Key == shortcut.Sequence.Key && s.References == shortcut.Sequence.References &&
            s.From == shortcut.Sequence.From && s.To == shortcut.Sequence.To);
        var saved = shared is null ? shortcut : new ScheduleShortcut(shortcut.Symbol, shortcut.Name, shared, shortcut.Position, shortcut.DateRole);
        values.Add(key, saved);
        return saved;
    }
}
