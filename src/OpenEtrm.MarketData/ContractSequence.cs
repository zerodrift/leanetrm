using System.Collections.Immutable;

namespace OpenEtrm.MarketData;

public enum ScheduleDateRole { DeliveryStart = 1, DeliveryEnd, LastTradingDate }
public sealed record ContractSequenceEntry(int Ordinal, MarketContract Contract, DateTimeOffset EligibleFrom, DateOnly? LastTradingDate);
public sealed record ResolvedScheduleDate(string Symbol, CurveKey Sequence, ReferenceRevision References,
    string RuleVersion, int Position, ScheduleDateRole DateRole, DateTimeOffset ValuationAt, ContractKey Contract, DateOnly Date);

// Compiled from a caller-qualified pinned futures sequence; no clock, database or prices.
public sealed class ContractSequence
{
    public CurveKey Key { get; }
    public ReferenceRevision References { get; }
    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }
    public ImmutableArray<ContractSequenceEntry> Entries { get; }
    public const string RuleVersion = "expiry_ordinal_v1";

    public ContractSequence(CurveKey key, ReferenceRevision references, DateTimeOffset from,
        DateTimeOffset to, IEnumerable<ContractSequenceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (key.Revision.CatalogId == Guid.Empty || key.Revision.RevisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(key.Code) || references.CatalogId == Guid.Empty || references.RevisionId == Guid.Empty ||
            !FiniteUtc(from) || !FiniteUtc(to) || from >= to)
            Fail("Supply exact revision pins and finite half-open UTC coverage.");
        Entries = entries.Take(10_001).ToImmutableArray();
        if (Entries.Length is < 1 or > 10_000) Fail("Supply one to 10,000 ordered contracts.");
        var seen = new HashSet<ContractKey>();
        var seriesSeen = new HashSet<ReferenceKey>();
        for (var i = 0; i < Entries.Length; i++)
        {
            var e = Entries[i];
            if (e is null || e.Ordinal != i + 1 || e.Contract is not { } c || c.Key.Revision != key.Revision ||
                c.Key.Id == Guid.Empty || !seen.Add(c.Key) || c.Kind != ContractKind.Future ||
                c.FuturesSeries is not { } series || series.Revision != references || string.IsNullOrWhiteSpace(series.Code) || !seriesSeen.Add(series) ||
                c.DeliveryFrom >= c.DeliveryTo || !FiniteUtc(c.Expiry) || !FiniteUtc(e.EligibleFrom) ||
                e.EligibleFrom >= c.Expiry || i > 0 && Entries[i - 1].Contract.Expiry >= c.Expiry)
                Fail("Contract order, exact pins, first-trading eligibility and increasing last-trading instants are required.");
        }
        if (to > Entries[^1].Contract.Expiry) Fail("Coverage cannot extend past the supplied live horizon.");
        Key = key; References = references; From = from; To = to;
    }
    internal ContractSequenceEntry Resolve(int position, DateTimeOffset at)
    {
        if(position<=0||position>Entries.Length)throw new MarketDataException("sequence_horizon", "Select a positive sequence position.");
        return ObservationContractSelector.Prompt(position-1,ContractRollRule.AtExpiry()).Select(this,at);
    }
    private static bool FiniteUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value > DateTimeOffset.MinValue && value.Year < 9999;
    private static void Fail(string message) => throw new MarketDataException("sequence_definition", message);
}

// Symbol spelling never determines position, product or date semantics.
public sealed class ScheduleShortcut
{
    public string Symbol { get; }
    public string Name { get; }
    public ContractSequence Sequence { get; }
    public int Position { get; }
    public ScheduleDateRole DateRole { get; }
    public ScheduleShortcut(string symbol, string name, ContractSequence sequence, int position, ScheduleDateRole dateRole)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (symbol is null || !System.Text.RegularExpressions.Regex.IsMatch(symbol,@"\Aoe\.[a-z][a-z0-9_]{0,31}\z"))
            throw new MarketDataException("shortcut_symbol", "Use oe. followed by a lowercase name.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || position <= 0 || position > sequence.Entries.Length || !Enum.IsDefined(dateRole))
            throw new MarketDataException("shortcut_definition", "Supply a name, available positive position and explicit date role.");
        Symbol = symbol; Name = name; Sequence = sequence; Position = position; DateRole = dateRole;
    }
    public ResolvedScheduleDate Resolve(DateTimeOffset at)
    {
        var entry = Sequence.Resolve(Position, at);
        var date = DateRole switch {
            ScheduleDateRole.DeliveryStart => entry.Contract.DeliveryFrom,
            ScheduleDateRole.DeliveryEnd => entry.Contract.DeliveryTo,
            ScheduleDateRole.LastTradingDate => entry.LastTradingDate ?? throw new MarketDataException("sequence_date", "The selected last-trading business date is missing."),
            _ => throw new MarketDataException("shortcut_definition", "Select a supported date role.")
        };
        return new(Symbol, Sequence.Key, Sequence.References, ContractSequence.RuleVersion, Position, DateRole, at, entry.Contract.Key, date);
    }
}
