using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

public enum GasBillDirection { Pay, Receive }
// One gas day a billable payment covers in the month.
public sealed record GasBillExpectedDay(int CashSlot, DateOnly Day);
// One final cash determination of the trade and month, with its contribution to its day's amount and whether a
// document already carries it.
public sealed record GasBillDetermination(Guid Id, int CashSlot, DateOnly Day, int Ordinal, Money Contribution,
    GasBillDirection Direction, bool Documented);
public sealed record GasBillLine(Guid DeterminationId, int CashSlot, DateOnly Day, int Ordinal, Money Amount);
public sealed record GasBillDocument(string Kind, ImmutableArray<GasBillLine> Lines, Money PayTotal, Money ReceiveTotal);
public sealed record GasBillAssessment(string State, ImmutableArray<GasBillExpectedDay> Missing, GasBillDocument? Document);

// A monthly document over one trade's existing daily determinations. It creates no payable and rounds nothing: every
// line is a determination's own contribution, and pay and receive payments are totalled separately, never netted.
public static class GasMonthlyBill
{
    public const string ScopePolicy = "gas_trade_month_gross_v1";
    public const string AmountPolicy = "sum_of_daily_determinations_v1";
    public const string Bill = "bill", AdjustmentNote = "adjustment_note";

    public static GasBillAssessment Assess(IEnumerable<GasBillExpectedDay> expectedDays,
        IEnumerable<GasBillDetermination> determinations, bool billIssued)
    {
        var expected = expectedDays.ToImmutableSortedSet(Comparer<GasBillExpectedDay>.Create(
            (a, b) => (a.CashSlot, a.Day).CompareTo((b.CashSlot, b.Day))));
        var facts = determinations.OrderBy(d => d.CashSlot).ThenBy(d => d.Day).ThenBy(d => d.Ordinal).ToImmutableArray();
        if (expected.IsEmpty) throw Error("gas_bill_scope", "This trade has no billable payment day in the month.");
        if (facts.Any(d => !expected.Contains(new(d.CashSlot, d.Day))))
            throw Error("gas_bill_revision", "A determination of this month is outside the trade revision's billable payment days.");
        foreach (var day in facts.GroupBy(d => (d.CashSlot, d.Day)))
            if (!day.Select(d => d.Ordinal).SequenceEqual(Enumerable.Range(1, day.Count())) || day.Select(d => d.Direction).Distinct().Count() != 1)
                throw Error("gas_bill_input", "Each payment day needs its complete determination chain in one direction.");
        if (facts.Select(d => d.Contribution.Currency).Distinct().Count() > 1)
            throw Error("gas_bill_input", "A document has one currency.");
        var missing = expected.Where(e => !facts.Any(d => (d.CashSlot, d.Day) == (e.CashSlot, e.Day))).ToImmutableArray();
        var open = facts.Where(d => !d.Documented).ToImmutableArray();
        if (!billIssued)
        {
            if (open.Length != facts.Length) throw Error("gas_bill_input", "A determination is on a document, but the month has no bill.");
            return missing.IsEmpty ? new("ready", missing, Document(Bill, open)) : new("incomplete", missing, null);
        }
        if (!missing.IsEmpty || open.Any(d => d.Ordinal == 1))
            throw Error("gas_bill_input", "An issued bill covers every payment day's original determination.");
        return open.IsEmpty ? new("issued", missing, null) : new("adjustment_ready", missing, Document(AdjustmentNote, open));
    }

    static GasBillDocument Document(string kind, ImmutableArray<GasBillDetermination> open)
    {
        var zero = new Money(ExactDecimal.Parse("0"), open[0].Contribution.Currency);
        Money Total(GasBillDirection direction) => open.Where(d => d.Direction == direction).Aggregate(zero, (sum, d) => sum.Add(d.Contribution));
        return new(kind, [.. open.Select(d => new GasBillLine(d.Id, d.CashSlot, d.Day, d.Ordinal, d.Contribution))],
            Total(GasBillDirection.Pay), Total(GasBillDirection.Receive));
    }
    static MarketDataException Error(string code, string message) => new(code, message);
}
