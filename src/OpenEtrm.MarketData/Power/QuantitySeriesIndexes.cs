using System.Collections.Frozen;

namespace OpenEtrm.MarketData.Power;

// Owned by one authorized calculation, never shared across requests. The context is fixed;
// exact immutable series content and retained clock rules determine reusable grid validation.
// Metadata is admitted on every use; all supplied cells are validated before cutoff selection.
// The cache stores only immutable indexes, not quantities, deadline outcomes or authority.
internal sealed class QuantitySeriesIndexes(VolumeContext context)
{
    private readonly Dictionary<(QuantitySeries Series,string ClockRules),FrozenDictionary<DateTimeOffset,QuantityCell>> selected=[];
    internal int Validations { get; private set; }
    internal int Selections { get; private set; }

    internal FrozenDictionary<DateTimeOffset,QuantityCell> Resolve(QuantitySeries series,Guid id,string kind,ReferenceKey? quantity,
        VolumeSourceDefinition source,GenerationAsset asset,VolumeContext requested)
    {
        if(requested!=context)throw Invalid("Series preparation cannot cross calculation contexts.");
        ValidateHeader(series,id,kind,quantity,source);
        var key=(series,asset.OperatingClock.Rules);
        if(selected.TryGetValue(key,out var retained))return retained;
        Validations++;ValidateCells(series,source,asset);
        Selections++;var index=Select(series,context.KnownAt);
        selected.Add(key,index);
        return index;
    }
    private static void ValidateHeader(QuantitySeries series,Guid id,string kind,ReferenceKey? quantity,VolumeSourceDefinition source)
    {
        if(series is null || series.Id!=id || series.Kind!=kind || series.Clock!=source.Clock || series.IntervalMinutes!=source.IntervalMinutes || series.Quantity!=quantity || series.Cells.IsDefault)
            throw Invalid("Quantity series must retain the source's exact kind, clock, grid and quantity specification.");
    }
    private static void ValidateCells(QuantitySeries series,VolumeSourceDefinition source,GenerationAsset asset)
    {
        if(series.Cells.Any(c=>c.Start.Offset!=TimeSpan.Zero || c.End-c.Start!=TimeSpan.FromMinutes(source.IntervalMinutes) ||
            !PowerHours.IsCell(asset.OperatingClock.Zone,c.Start,TimeSpan.FromMinutes(source.IntervalMinutes)) || c.KnownAt.Offset!=TimeSpan.Zero || c.Revision<=0 || c.ContextId==Guid.Empty) ||
            series.Cells.GroupBy(c=>(c.Start,c.Revision)).Any(g=>g.Count()!=1))
            throw new MarketDataException("volume_grid","Quantity observations need distinct revision/cell identities, native UTC boundaries and certified knowledge lineage.");
        var revisions=series.Cells.GroupBy(c=>c.Revision).OrderBy(g=>g.Key).ToArray();
        if(revisions.Any(g=>g.Select(c=>c.KnownAt).Distinct().Count()!=1) ||
            revisions.Zip(revisions.Skip(1)).Any(p=>p.First.First().KnownAt>p.Second.First().KnownAt))
            throw new MarketDataException("volume_grid","Each series revision needs one certified knowledge instant, ordered without regression.");
    }
    private static FrozenDictionary<DateTimeOffset,QuantityCell> Select(QuantitySeries series,DateTimeOffset knownAt)=>series.Cells.Where(c=>c.KnownAt<=knownAt)
        .GroupBy(c=>c.Start).ToFrozenDictionary(g=>g.Key,g=>g.MaxBy(c=>c.Revision)!);
    private static MarketDataException Invalid(string text)=>new("volume_source",text);
}
