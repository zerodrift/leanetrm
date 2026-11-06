using System.Collections.Immutable;

namespace OpenEtrm.MarketData.CustomCurves;

public static partial class RecipeEngine
{
    public const string WholeDeliverySplice="whole_delivery_intervals_v1";
    private static (DateOnly From,DateOnly Cutover,DateOnly To) SpliceRange(RecipeNode node)
    {
        DateOnly Date(string key)=>DateOnly.TryParseExact(Required(node,key),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,out var day)?day:throw Invalid("Splice dates require exact YYYY-MM-DD values.");
        var from=Date("from");var cutover=Date("cutover");var to=Date("to");
        if(from.Year<1901||to.Year>2199||from>=cutover||cutover>=to||Required(node,"implementation")!=WholeDeliverySplice)
            throw Invalid("Select ordered explicit start, cutover and exclusive end dates and whole-delivery splice semantics.");
        return(from,cutover,to);
    }
    // Selection only: retained cells keep their source expression, geometry and
    // evidence. No prices are averaged, shaped or independently observed here.
    private static Panel SplicePanels(RecipeNode node,Panel before,Panel after,CancellationToken cancellation)
    {
        var range=SpliceRange(node);var result=ImmutableArray.CreateBuilder<Cell>();
        void Select(Panel source,DateOnly from,DateOnly to)
        {
            if(source.Cells.IsDefault)throw Missing("A splice requires explicit source panels.");
            var next=from;
            foreach(var cell in source.Cells.Where(c=>c.DeliveryFrom<to&&c.DeliveryTo>from).OrderBy(c=>c.DeliveryFrom).ThenBy(c=>c.DeliveryTo))
            {
                cancellation.ThrowIfCancellationRequested();
                if(cell.DeliveryFrom!=next||cell.DeliveryTo>to||cell.DeliveryTo<=cell.DeliveryFrom)
                    throw Missing("Splice coverage has a gap, overlap, repeated value or a delivery interval crossing a selected boundary.");
                if(result.Count>0&&result[0].Unit!=cell.Unit)throw Missing("Spliced curves require identical currency, quantity and quote multiplier.");
                result.Add(cell with{Position=result.Count+1});next=cell.DeliveryTo;
            }
            if(next!=to)throw Missing("Every selected splice range requires complete source coverage; another source cannot fill a gap.");
        }
        Select(before,range.From,range.Cutover);Select(after,range.Cutover,range.To);
        return new(result.ToImmutable());
    }
    // Pair complete symbolic outputs by explicit delivery boundaries. Price
    // evaluation stays in the connected plan; no value buffer is constructed.
    private static Panel CombinePanels(RecipeNode node,IReadOnlyDictionary<string,Panel> panels,DateOnly contextDate,CancellationToken cancellation,ref int work)
    {
        var inputs=node.PanelWeights.OrderBy(w=>w.SourcePort,StringComparer.Ordinal).Select(w=>
        {
            var panel=panels[w.SourcePort];
            if(panel.Cells.IsDefaultOrEmpty||panel.Cells.GroupBy(c=>new RecipeDelivery(c.DeliveryFrom,c.DeliveryTo)).Any(g=>g.Count()!=1))
                throw Missing("Each combined curve needs exactly one value per delivery period. Reduce repeated historical dates explicitly first.");
            return(Weight:Weight(w.Weight),Cells:panel.Cells.ToDictionary(c=>new RecipeDelivery(c.DeliveryFrom,c.DeliveryTo)));
        }).ToArray();
        var periods=inputs[0].Cells.Keys.OrderBy(d=>d.From).ThenBy(d=>d.To).ToArray();
        if(inputs.Any(p=>!p.Cells.Keys.ToHashSet().SetEquals(periods)))
            throw Missing("Combined curves must cover identical complete delivery periods; no point may be dropped, broadcast or interpolated.");
        var result=ImmutableArray.CreateBuilder<Cell>();
        foreach(var period in periods)
        {
            cancellation.ThrowIfCancellationRequested();var cells=inputs.Select(p=>p.Cells[period]).ToArray();
            if(cells.Select(c=>c.Unit).Distinct().Count()!=1)throw Missing("Combined curves require identical currency, quantity and quote multiplier.");
            var references=cells.Sum(c=>(long)c.Inputs.Length);
            if(references>50000-work)throw Missing("The recipe exceeds its 50,000 cumulative sample-reference preparation limit.");
            work+=(int)references;
            result.Add(new Cell(result.Count+1,cells.SelectMany(c=>c.Dates).Distinct().Order().ToImmutableArray(),contextDate,cells[0].Unit,
                cells.SelectMany(c=>c.Inputs).ToImmutableArray(),new(node.Id,result.Count,null,cells.Select(c=>c.Formula).ToImmutableArray(),
                    inputs.Select(p=>p.Weight).ToImmutableArray(),node.Parameters["aggregation"]),period.From,period.To));
        }
        return new(result.ToImmutable());
    }
}
