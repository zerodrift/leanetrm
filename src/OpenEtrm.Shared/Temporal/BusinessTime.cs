namespace OpenEtrm.Shared.Temporal;

// A business date is an independent coordinate, not the UTC calendar date of
// an instant. Recorded sessions supply it; working UTC snapshots declare theirs.
public readonly record struct BusinessTime
{
    public DateOnly Date { get; }
    public DateTimeOffset Instant { get; }
    public BusinessTime(DateOnly date,DateTimeOffset instant)
    {
        if(instant.Offset!=TimeSpan.Zero)
            throw new TemporalException("temporal_context","Valuation instants must be UTC.");
        Date=date;Instant=instant;
    }
    public static BusinessTime UtcSnapshot(DateTimeOffset instant)=>new(DateOnly.FromDateTime(instant.UtcDateTime),instant);
    public void RequireDate(DateOnly effectiveOn)
    {
        if(effectiveOn!=Date)
            throw new TemporalException("temporal_context","The effective date must equal the selected business date.");
    }
}
