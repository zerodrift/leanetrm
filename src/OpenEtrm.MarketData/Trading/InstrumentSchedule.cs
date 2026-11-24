using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

public sealed record InstrumentScheduleEntry(int Slot, SideMembership Membership)
{
    public SideSchedule Schedule => Membership.Side.GenerateSchedule();
}

public readonly record struct InstrumentScheduleRow(int Slot, ScheduleRow Row);

// Entries preserve composition. Rows are inspected on demand; valuation lets
// each Side execute its own economics in the supplied context.
public class InstrumentSchedule
{
    public SidedInstrument Instrument { get; }
    public ImmutableArray<InstrumentScheduleEntry> Entries { get; }
    public IEnumerable<InstrumentScheduleRow> Rows => Entries.SelectMany(entry =>
        entry.Schedule.Rows.Select(row => new InstrumentScheduleRow(entry.Slot, row)));

    protected internal InstrumentSchedule(SidedInstrument instrument)
    {
        Instrument = instrument;
        Entries = [..instrument.Sides.Select((membership, slot) => new InstrumentScheduleEntry(slot, membership))];
    }

    public virtual InstrumentValuation Evaluate(SideValuationContext context)
    {
        if (context is null) throw new MarketDataException("side_context", "An explicit valuation context is required.");
        Instrument.RequireSideComposition();
        return new(Instrument, context, [..Instrument.Sides.Select(m => context.Evaluate(m.Side))]);
    }
}
