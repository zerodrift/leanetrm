using System.Collections.Immutable;

namespace OpenEtrm.MarketData.CustomCurves;

public static partial class RecipeEngine
{
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
