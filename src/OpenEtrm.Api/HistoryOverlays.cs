using System.Collections.Immutable;
using OpenEtrm.Shared.Analytics;

namespace OpenEtrm.Api;

// History may hold several samples at one instant (a close beside the frame it recorded). Overlays take one sample per
// instant: identical samples are the same observation and count once; different ones are refused, never chosen between.
public static class HistoryOverlays
{
    public static ImmutableArray<OverlayPoint> Points(ImmutableArray<HistorySample> samples)
    {
        var points=ImmutableArray.CreateBuilder<OverlayPoint>();
        foreach(var instant in samples.GroupBy(sample=>sample.At))
        {
            var first=instant.First();
            if(instant.Any(sample=>sample.Value!=first.Value||sample.UnavailableReason!=first.UnavailableReason))
                throw new OverlayException("overlay_input",$"Several different prices are recorded at {instant.Key:O}. Choose one version before adding overlays.");
            points.Add(new(instant.Key,first.Value));
        }
        return points.ToImmutable();
    }
}
