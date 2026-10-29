namespace OpenEtrm.MarketData.Trading;

public sealed record ScheduleDefinitionWork(long Rows,long Edges,long Bindings,long Roots,long Ports)
{
    public long Grids { get; init; }
    public long GridCells { get; init; }
}

public static partial class ScheduleDefinitionCompiler
{
    // The retained artifact now consists of bounded operator blocks and integer
    // geometry columns. Count that actual artifact; there is no interval-DAG
    // construction, canonical row expansion or speculative byte estimate here.
    public static ScheduleDefinitionWork Estimate(Side side,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(side);cancellationToken.ThrowIfCancellationRequested();
        var definition=Generate(side,cancellationToken);
        return new(definition.Rows.Count,definition.Edges.Count,definition.Bindings.Count,definition.Roots.Count,definition.Ports.Count)
        {Grids=definition.Grids.Count,GridCells=definition.Grids.Sum(grid=>(long)grid.CellCount)};
    }
}
