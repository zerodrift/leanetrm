using System.Collections;

namespace OpenEtrm.MarketData.Power;

// Quantity lineage is metadata beside the same compact interval values used for shapes.
// Equal revisions may be read under different certified contexts, so context is not inferred
// from revision number. Only the row-to-lineage index is retained per point.
public sealed class IntervalQuantityData : IReadOnlyList<QuantityCell>
{
    public IntervalData Values { get; }
    private readonly int[] lineage;
    private readonly (DateTimeOffset KnownAt,Guid ContextId)[] sources;
    public int Count=>Values.Count;
    private IntervalQuantityData(IntervalData values,int[] lineage,(DateTimeOffset,Guid)[] sources)
    {Values=values;this.lineage=lineage;this.sources=sources;}
    public QuantityCell this[int index]
    {
        get{var value=Values[index];var source=sources[lineage[index]];return new(value.Start,value.End,value.Value,value.Revision,source.KnownAt,source.ContextId);}
    }
    public bool TryGetValue(DateTimeOffset start,out QuantityCell value)
    {int index=Values.Grid.FindStart(start);value=index>=0?this[index]:default;return index>=0;}
    public IEnumerator<QuantityCell> GetEnumerator(){for(int i=0;i<Count;i++)yield return this[i];}
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    public sealed class Builder(int minutes)
    {
        private readonly IntervalData.Builder values=new(minutes);
        private readonly List<int> lineage=[];
        private readonly Dictionary<(DateTimeOffset,Guid),int> sourceIds=[];
        private readonly List<(DateTimeOffset,Guid)> sources=[];
        public void Add(QuantityCell cell)
        {
            if(cell.KnownAt.Offset!=TimeSpan.Zero || cell.ContextId==Guid.Empty)
                throw new MarketDataException("volume_grid","Quantity observations require certified UTC knowledge and context.");
            values.Add(cell.Start,cell.End,cell.Value,cell.Revision);
            var key=(cell.KnownAt,cell.ContextId);
            if(!sourceIds.TryGetValue(key,out int id)){id=sources.Count;sourceIds.Add(key,id);sources.Add(key);}
            lineage.Add(id);
        }
        public void AddRange(IEnumerable<QuantityCell> cells){foreach(var cell in cells)Add(cell);}
        public IntervalQuantityData Build()=>new(values.Build(),[..lineage],[..sources]);
    }
}
