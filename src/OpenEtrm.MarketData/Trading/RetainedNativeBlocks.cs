using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

internal sealed partial class RetainedSideSchedule
{
    private ImmutableDictionary<DateOnly,ImmutableArray<NativeBlock>> nativeMonths=ImmutableDictionary<DateOnly,ImmutableArray<NativeBlock>>.Empty;
    private sealed class NativeBlock(RetainedNativeRow template,ScheduleDefinitionGrid grid)
    {
        internal RetainedNativeRow Template { get; }=template;
        internal ScheduleDefinitionGrid Grid { get; }=grid;
        internal ImmutableArray<DeliveryInterval> Intervals { get; }=[..Enumerable.Range(0,grid.CellCount).Select(grid.Interval)];
        internal RetainedNativeRow Cell(int cell)
        {
            var interval=Intervals[cell];
            return Template with{CellOrdinal=cell,Obligation=Template.Obligation with{Grid=null,Interval=interval,OccurrenceKey=null,TermsDigest=null}};
        }
    }
    // The retained graph has one operator set per block. Cells are views into
    // its immutable geometry, never copies of the operator DAG or bindings.
    private sealed class NativeCellRows(ImmutableArray<NativeBlock> blocks):IReadOnlyList<RetainedNativeRow>
    {
        public int Count { get; }=blocks.Sum(block=>block.Grid.CellCount);
        public RetainedNativeRow this[int ordinal]
        {
            get
            {
                if((uint)ordinal>=(uint)Count)throw new ArgumentOutOfRangeException(nameof(ordinal));
                var low=0;var high=blocks.Length-1;
                while(low<high){var middle=low+(high-low+1)/2;if(blocks[middle].Grid.FirstRowOrdinal<=ordinal)low=middle;else high=middle-1;}
                return blocks[low].Cell(ordinal-blocks[low].Grid.FirstRowOrdinal!.Value);
            }
        }
        public IEnumerator<RetainedNativeRow> GetEnumerator(){foreach(var block in blocks)for(var cell=0;cell<block.Grid.CellCount;cell++)yield return block.Cell(cell);}
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private IEnumerable<DeliveryInterval> GridIntervals(int ordinal)
    {
        var retained=nativeBlocks.FirstOrDefault(block=>block.Grid.Ordinal==ordinal);
        if(retained is not null)return retained.Intervals;
        var grid=Definition.Grids[ordinal];return Enumerable.Range(0,grid.CellCount).Select(grid.Interval);
    }
    internal IEnumerable<DeliveryInterval> MonthIntervals(DateOnly month)
    {
        if(NativeTerms is null||month.Day!=1||month<NativeTerms.Delivery.From||month>=NativeTerms.Delivery.To)
            throw Error("Select one original operating month within the retained native term.");
        // Only immutable contractual geometry is indexed. Quantity revisions,
        // normalization and price calibration still belong to the input context.
        if(nativeMonths.TryGetValue(month,out var blocks))
            foreach(var block in blocks)foreach(var interval in block.Intervals)yield return interval;
    }
    internal ExactQuantity ProfileQuantity(RetainedNativeRow native)
    {
        var row=Definition.Rows[native.Quantity];
        if(row.Operation=="quantity_literal")return Quantity(row.Ordinal);
        if(row.Operation!="profile_quantity")throw Error("A retained profile requires its explicit literal or duration quantity operation.");
        var interval=native.Obligation.Interval??throw Error("A profile cell must name an exact retained interval.");
        return new((interval.End-interval.Start).Ticks,TimeSpan.TicksPerHour,row.Unit!.Value);
    }
}
