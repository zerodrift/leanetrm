namespace OpenEtrm.MarketData.Power;

// Owned by one authorized calculation, never shared across requests. The context is fixed;
// exact immutable series content and retained clock rules determine reusable grid validation.
// Metadata is admitted on every use; all supplied cells are validated before cutoff selection.
// The cache stores only immutable indexes, not quantities, deadline outcomes or authority.
internal sealed class QuantitySeriesIndexes(VolumeContext context)
{
    private readonly Dictionary<(QuantitySeries Series,string ClockRules),IntervalQuantityData> selected=[];
    internal int Validations { get; private set; }
    internal int Selections { get; private set; }

    internal IntervalQuantityData Resolve(QuantitySeries series,Guid id,string kind,ReferenceKey? quantity,
        VolumeSourceDefinition source,GenerationAsset asset,VolumeContext requested)
    {
        if(requested!=context)throw Invalid("Series preparation cannot cross calculation contexts.");
        ValidateHeader(series,id,kind,quantity,source);
        _=series.Values;
        var key=(series,asset.OperatingClock.Rules);
        if(selected.TryGetValue(key,out var retained))return retained;
        Validations++;ValidateCells(series,source,asset);
        Selections++;var index=Select(series,context.KnownAt);
        selected.Add(key,index);
        return index;
    }
    private static void ValidateHeader(QuantitySeries series,Guid id,string kind,ReferenceKey? quantity,VolumeSourceDefinition source)
    {
        if(series is null || series.Id!=id || series.Kind!=kind || series.Clock!=source.Clock || series.IntervalMinutes!=source.IntervalMinutes || series.Quantity!=quantity || series.Data is null && series.Cells.IsDefault)
            throw Invalid("Quantity series must retain the source's exact kind, clock, grid and quantity specification.");
    }
    private static void ValidateCells(QuantitySeries series,VolumeSourceDefinition source,GenerationAsset asset)
    {
        var revisions=new Dictionary<int,DateTimeOffset>();
        HashSet<(DateTimeOffset,int)>? identities=series.Data is null?[]:null;
        foreach(var c in series.Values)
        {
            if(c.Start.Offset!=TimeSpan.Zero || c.End.Offset!=TimeSpan.Zero || c.End-c.Start!=TimeSpan.FromMinutes(source.IntervalMinutes) ||
                !PowerHours.IsCell(asset.OperatingClock.Zone,c.Start,TimeSpan.FromMinutes(source.IntervalMinutes)) ||
                c.KnownAt.Offset!=TimeSpan.Zero || c.Revision<=0 || c.ContextId==Guid.Empty || identities is not null && !identities.Add((c.Start,c.Revision)))
                throw new MarketDataException("volume_grid","Quantity observations need distinct revision/cell identities, native UTC boundaries and certified knowledge lineage.");
            if(revisions.TryGetValue(c.Revision,out var known) && known!=c.KnownAt)
                throw new MarketDataException("volume_grid","Each revision must have one certified knowledge instant.");
            revisions[c.Revision]=c.KnownAt;
        }
        DateTimeOffset? previous=null;
        foreach(var revision in revisions.OrderBy(r=>r.Key))
        {
            if(previous is {} prior && prior>revision.Value)throw new MarketDataException("volume_grid","Revision knowledge must not regress.");
            previous=revision.Value;
        }
    }
    private static IntervalQuantityData Select(QuantitySeries series,DateTimeOffset knownAt)
    {
        if(series.Data is {} retained && retained.All(c=>c.KnownAt<=knownAt))return retained;
        var builder=new IntervalQuantityData.Builder(series.IntervalMinutes);
        builder.AddRange(series.Values.Where(c=>c.KnownAt<=knownAt).GroupBy(c=>c.Start).OrderBy(g=>g.Key).Select(g=>g.MaxBy(c=>c.Revision)));
        return builder.Build();
    }
    private static MarketDataException Invalid(string text)=>new("volume_source",text);
}
