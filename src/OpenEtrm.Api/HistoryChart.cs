using System.Collections.Immutable;
using OpenEtrm.Shared.Analytics;

namespace OpenEtrm.Api;

public sealed record HistoryChartPoint(DateTimeOffset At,string? Value,string? UnavailableReason);
public sealed record HistoryChartConflict(DateTimeOffset At,string Message);
// The series a history read is charted and overlaid from (markets-desk.md): one point per instant. History may hold several samples at
// one instant (a close beside the frame it recorded); samples that agree on value and availability are one point, and samples that
// differ are never chosen between: the read states the first instant they differ at instead, and has no points.
public sealed record HistoryChart(ImmutableArray<HistoryChartPoint>? Points,HistoryChartConflict? Conflict)
{
    public static HistoryChart Of(IEnumerable<HistorySample> samples)
    {
        var points=ImmutableArray.CreateBuilder<HistoryChartPoint>();
        foreach(var instant in samples.GroupBy(sample=>sample.At))
        {
            if(!HistoryRevisionPolicy.Agree(instant))
                return new(null,new(instant.Key,$"Several closes recorded different prices at {instant.Key:O}. Choose As known at cutoff, or one close, to chart one price per instant."));
            var first=instant.First();
            points.Add(new(instant.Key,first.Value,first.UnavailableReason));
        }
        return new(points.ToImmutable(),null);
    }

    /// <summary>The overlays' input: exactly the chart's points. A conflicting series has none.</summary>
    public ImmutableArray<OverlayPoint> OverlayInput()=>
        Points is {} points?[..points.Select(p=>new OverlayPoint(p.At,p.Value))]:throw new InvalidOperationException("A conflicting history series has no overlay input.");
}
