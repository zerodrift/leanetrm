using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.Trading;

internal sealed record PpaGraphPrices(SchedulePriceColumn Values, ImmutableArray<IntervalPriceResolution> Resolutions);

internal sealed class PpaPriceGraphCompiler(IntervalValuationInputs inputs, CurveBuild scenario,
    ScheduleGraphBuilder graph, IntervalPriceResolver admission, CancellationToken cancellation)
{

    private readonly Dictionary<(CurveKey, DateOnly, bool,PriceSettlementSelection?), PpaGraphPrices> months = [];
    private readonly Dictionary<(CurveKey, CurveRequest), SchedulePriceColumn> shapes = [];
    internal PpaGraphPrices Compile(CurveKey key, DateOnly month, bool frozen,PriceSettlementSelection? selection)
    {
        cancellation.ThrowIfCancellationRequested();
        if (months.TryGetValue((key, month, frozen,selection), out var prior)) return prior;
        var curve = inputs.Read.Curves[key]; var prepared = curve.Period(month);
        // Existing admission owns expiry, actual eligibility, frozen knowledge and
        // selected-anchor validation. Its numeric result is used only for frozen
        // facts; live results are compiled from their actual graph dependencies.
        var resolutions = (frozen ? inputs.BaseResolver : admission).Prices(key, month,selection);
        var grid = PpaQuantityGraphCompiler.Grid(resolutions.Select(p => p.Interval));
        var links = new SchedulePriceCell[resolutions.Length];
        foreach (var group in resolutions.Select((value, index) => (value, index)).GroupBy(p =>
            (Frozen: frozen || p.value.State is IntervalPriceState.Actual or IntervalPriceState.PublishedFixing,
             p.value.Actual?.BatchId, p.value.Actual?.Revision, KnownAt: p.value.Actual?.KnownAt ?? inputs.KnownAt)))
        {
            var selected = group.ToArray();
            if (group.Key.Frozen)
            {
                var block = PpaQuantityGraphCompiler.Grid(selected.Select(p => p.value.Interval));
                var observations = selected.SelectMany(p => p.value.Anchor?.Lineage ?? p.value.ForwardLineage).Distinct().OrderBy(o => o.CatalogId).ThenBy(o => o.Id).ToImmutableArray();
                var source = new ScheduleSourceKey.FrozenPrice(key, block.Start(0), block.End(block.Count - 1),
                    frozen ? inputs.BaseMarket.Graph.Id : scenario.Graph.Id, group.Key.KnownAt, observations, group.Key.BatchId, group.Key.Revision);
                var column = graph.FrozenPrices(source, block, [..selected.Select(p => Required(p.value))]);
                if(selected[0].value.PeriodSelection is not null)
                    column=graph.Prices(block,[..selected.Select((p,n)=>graph.PeriodPrice(column.Cell(n),p.value.PeriodSelection!))]);
                for (var n = 0; n < selected.Length; n++) links[selected[n].index] = column.Cell(n);
                continue;
            }
            if (prepared.Shape is null)
            {
                var block = PpaQuantityGraphCompiler.Grid(selected.Select(p => p.value.Interval));
                var points = selected.Select(p =>
                {
                    if(p.value.Anchor?.Request is not {} request || !scenario.PricingBindings.TryGetValue(request,out var point))
                        throw PpaErrors.Error("risk_dependency", "price", "The exact admitted connected price binding is unavailable.");
                    var raw=graph.Price(point);return p.value.PeriodSelection is {} selector?graph.PeriodPrice(raw,selector):raw;
                }).ToImmutableArray();
                var column = graph.Prices(block, points);
                for (var n = 0; n < selected.Length; n++) links[selected[n].index] = column.Cell(n);
                continue;
            }
            foreach (var selectedCell in selected)
            {
                cancellation.ThrowIfCancellationRequested();
                var resolved = selectedCell.value;
                var request = resolved.Anchor?.Request ?? throw PpaErrors.Error("interval_estimate_unavailable", "price", "The bound price has no eligible source anchor.");
                if (!scenario.PricingBindings.TryGetValue(request, out var point))
                    throw PpaErrors.Error("risk_dependency", "price", "The exact admitted connected price binding is unavailable.");
                if (prepared.Shape is {} shape)
                {
                    if (!shapes.TryGetValue((key, request), out var column))
                    {
                        var selectedBlock = shape.Blocks.Single(b => b.Request == request);
                        var cells = selectedBlock.Cells;
                        var source = new ScheduleSourceKey.Shape(inputs.Reference.Definitions, shape.Definition.ShapeSeries, request,
                            inputs.KnownAt, inputs.KnowledgeContext, [..cells.Select(c => c.Revision)]);
                        column = graph.Shape(source, graph.Price(point), shape.Definition.Method, cells);
                        shapes.Add((key, request), column);
                    }
                    var cell = Find(column.Grid, resolved.Start, resolved.End);
                    links[selectedCell.index] = column.Cell(cell);
                }
            }
        }
        var result = new PpaGraphPrices(graph.Gather(grid, [..links]), resolutions);
        months.Add((key, month, frozen,selection), result); return result;
    }
    internal static ExactUnitPrice Required(IntervalPriceResolution value) => value.Price ?? value.Estimate
        ?? throw PpaErrors.Error("interval_estimate_unavailable", "price", "The bound resolver supplies no eligible price or estimate.");
    internal static int Find(UtcGrid grid, DateTimeOffset from, DateTimeOffset to)
    {
        var lo = 0; var hi = grid.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2; var at = grid.Start(mid);
            if (at < from) lo = mid + 1;
            else if (at > from) hi = mid - 1;
            else return grid.End(mid) == to ? mid : throw PpaErrors.Error("ppa_cadence", "price", "The selected cell must retain its entire exact interval.");
        }
        throw PpaErrors.Error("ppa_cadence", "price", "The exact source interval is absent from its prepared column.");
    }
}
