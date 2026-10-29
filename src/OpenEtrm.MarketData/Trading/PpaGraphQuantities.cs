using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData.Power;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.Trading;

internal sealed record PpaGraphQuantities(ScheduleQuantityColumn Total,
    ImmutableDictionary<AssetKey, ScheduleQuantityColumn> Assets);

// Prepared rows establish source eligibility and lineage once. Compilation reads
// the selected raw series cells, then represents generation, adjustments and
// normalization as dependencies of the same graph that prices the schedule.
internal sealed class PpaQuantityGraphCompiler(IntervalValuationInputs inputs, PpaInstrument instrument,
    DeliveryCoverage coverage, ScheduleGraphBuilder graph, CancellationToken cancellation)
{
    private readonly Dictionary<(Guid, DateOnly), PpaGraphQuantities> retained = [];
    private readonly HashSet<(Guid, DateOnly)> active = [];
    private readonly Dictionary<(Guid, DateOnly), ImmutableArray<PreparedRowInputs>> prepared = instrument.Memberships.SelectMany(member =>
        inputs.Rows(instrument, member.Side, coverage).GroupBy(row => Month(row.Quantity.Interval))
            .Select(group => new KeyValuePair<(Guid, DateOnly), ImmutableArray<PreparedRowInputs>>((member.Side.Id, group.Key), group.ToImmutableArray()))).ToDictionary();
    private readonly Dictionary<(string Definition, string Grid), PpaGraphQuantities> volumes = [];
    private readonly Dictionary<Guid, Dictionary<(DateTimeOffset, int), QuantityCell>> series = [];
    private readonly Dictionary<(AssetVersionKey, ScheduleAssetMeasure), ScheduleQuantityNode> capacities = [];
    internal PpaGraphQuantities Compile(Side side, DateOnly month)
    {
        cancellation.ThrowIfCancellationRequested();
        var key = (side.Id, month);
        if (retained.TryGetValue(key, out var prior)) return prior;
        if (!active.Add(key)) throw PpaErrors.Error("ppa_quantity_source", "slot", "Quantity slot references cannot form a cycle.");
        var rows = prepared.TryGetValue(key, out var selectedRows) ? selectedRows : [];
        if (rows.IsEmpty) throw PpaErrors.Error("ppa_quantity_source", "month", "The selected side has no prepared rows in this month.");
        var grid = Grid(rows.Select(r => r.Quantity.Interval));
        var terms=RetainedSideSchedules.Terms(side);
        var result = terms.Quantity switch
        {
            VolumeQuantity volume => Volume(terms,volume, rows, grid),
            ProfileQuantity => Profile(side, month, rows, grid),
            SlotQuantity slots => Slots(slots, month, grid),
            AccreditedQuantity when side is CapacityDeliverySide capacity => Accredited(capacity, rows, grid),
            _ => throw PpaErrors.Error("ppa_quantity_source", "quantity", "Select a supported explicit quantity contract.")
        };
        if (side is CertificateDeliverySide certificate)
        {
            var conversion=side.RetainedPlan?.CertificateConversion??certificate.Conversion;
            var scale = Enumerable.Repeat((conversion.ScaledValue, ExactDecimal.WorkingScale), grid.Count).ToImmutableArray();
            result = new(graph.Transform(result.Total, scale, certificate.CertificateQuantity),
                result.Assets.ToImmutableDictionary(p => p.Key, p => graph.Transform(p.Value, scale, certificate.CertificateQuantity)));
        }
        retained.Add(key, result); active.Remove(key); return result;
    }
    private PpaGraphQuantities Profile(Side side, DateOnly month, ImmutableArray<PreparedRowInputs> rows, UtcGrid grid)
    {
        // A contract profile is a frozen function of the side's clock and TOU
        // parameters. Preserve its explicit 1 MW denominator as a graph input.
        var definition = side.RetainedPlan?.SourceIdentity(RetainedScheduleSourceRole.ProfileQuantity)??DefinitionIdentity.For(side.Terms);
        var absolute = graph.QuantityColumn(new ScheduleSourceKey.Contract(definition, month.Year * 12 + month.Month), grid,
            [..rows.Select(r => r.Quantity.Quantity)], ScheduleInputUse.Frozen);
        var basisDefinition=side.RetainedPlan?.SourceIdentity(RetainedScheduleSourceRole.ProfileBasis)??definition;
        var basis = graph.QuantityInput(new ScheduleSourceKey.Contract(basisDefinition, 0), ExactQuantity.FromQuantity(side.Terms.UnitQuantity), ScheduleInputUse.Frozen);
        return new(graph.Normalize(absolute, graph.Broadcast(basis, grid)), ImmutableDictionary<AssetKey, ScheduleQuantityColumn>.Empty);
    }
    private PpaGraphQuantities Slots(SlotQuantity slots, DateOnly month, UtcGrid grid)
    {
        var selected = slots.Slots.Select(slot => Compile(instrument.Memberships.Single(m => m.Slot == slot).Side, month)).ToArray();
        if (selected.Any(s => !s.Total.Grid.Equals(grid))) throw PpaErrors.Error("ppa_cadence", "slots", "Slot quantities must retain the exact row grid.");
        var components = selected.SelectMany(s => s.Assets).GroupBy(p => p.Key).ToImmutableDictionary(g => g.Key, g => graph.Add(g.Select(p => p.Value).ToImmutableArray()));
        return new(graph.Add([..selected.Select(s => s.Total)]), components);
    }
    private PpaGraphQuantities Volume(PpaSideTerms terms,VolumeQuantity definition, ImmutableArray<PreparedRowInputs> rows, UtcGrid grid)
    {
        var scope = (DefinitionIdentity.For(definition).Digest+"/"+DefinitionIdentity.For(terms.Normalization).Digest, ScheduleGraph.Hash(ScheduleGraph.GridEvidence(grid)!));
        if (volumes.TryGetValue(scope, out var previous)) return previous;
        var assets = terms.Normalization.Assets.Select(k => inputs.Reference.Assets[k]).ToArray();
        var denominator = graph.Add([..assets.Select(a => Capacity(a, grid, ScheduleAssetMeasure.CapacityBasis))]);
        var components = ImmutableDictionary.CreateBuilder<AssetKey, ScheduleQuantityColumn>();
        foreach (var asset in assets)
        {
            cancellation.ThrowIfCancellationRequested();
            var raw = rows.Select(r => r.Quantity.Lineage.Single(v => v.Asset == asset.Key)).ToArray();
            var links = new ScheduleQuantityCell[rows.Length];
            // A source group shares selected revision, knowledge, state and asset
            // version. Sparse UTC axes preserve omissions without zero filling.
            foreach (var group in raw.Select((volume, row) => (volume, row)).GroupBy(p =>
                (p.volume.Source, p.volume.Series, p.volume.Revision, p.volume.KnownAt, p.volume.ContextId, p.volume.AssetVersion, p.volume.State, p.volume.SourceStage, p.volume.Stage)))
            {
                var selected = group.ToArray(); var source = inputs.Reference.Sources[group.Key.Source];
                var block = Grid(selected.Select(p => p.volume.Interval));
                var cells = selected.Select(p => SelectedCell(p.volume)).ToArray();
                var sourceKey = new ScheduleSourceKey.Series(inputs.Reference.Definitions, group.Key.Series, group.Key.Revision,
                    block.Start(0), block.End(block.Count - 1), group.Key.KnownAt, group.Key.ContextId);
                ScheduleQuantityColumn quantity;
                var energy = inputs.Reference.Quantities[selected[0].volume.Energy.Specification];
                if (group.Key.State == VolumeState.Metered)
                    quantity = graph.QuantityColumn(sourceKey, block, [..cells.Select(c => ExactQuantity.FromQuantity(new(c.Value, energy.Key)))], ScheduleInputUse.Frozen);
                else
                {
                    var factors = graph.CapacityFactors(sourceKey, block, [..cells.Select(c => c.Value)],
                        group.Key.State == VolumeState.Forecast ? ScheduleInputUse.ModelInput : ScheduleInputUse.Frozen);
                    quantity = graph.GenerateEnergy(factors, Capacity(asset, block, ScheduleAssetMeasure.CapacityBasis), asset.CapacityQuantity, energy);
                }
                quantity = graph.AdjustVolumes(quantity, source, group.Key.State, group.Key.SourceStage, group.Key.Stage,
                    [..selected.Select(p => p.volume.Interval.OperatingDate)]);
                for (var n = 0; n < selected.Length; n++) links[selected[n].row] = quantity.Cell(n);
            }
            components.Add(asset.Key, graph.Normalize(graph.Gather(grid, [..links]), denominator));
        }
        var componentMap = components.ToImmutable();
        var result = new PpaGraphQuantities(graph.Add(componentMap.Values.ToImmutableArray()), componentMap);
        volumes.Add(scope, result); return result;
    }
    private QuantityCell SelectedCell(AssetVolume volume)
    {
        if (!series.TryGetValue(volume.Series, out var values))
        {
            if (!inputs.Read.Series.TryGetValue(volume.Series, out var source)) throw PpaErrors.Error("volume_source", "series", "The exact selected quantity series is absent.");
            series.Add(volume.Series, values = source.Values.ToDictionary(c => (c.Start, c.Revision)));
        }
        if (!values.TryGetValue((volume.Interval.Start, volume.Revision), out var cell) || cell.End != volume.Interval.End || cell.Revision != volume.Revision ||
            cell.KnownAt != volume.KnownAt || cell.ContextId != volume.ContextId)
            throw PpaErrors.Error("volume_source", "selection", "Graph inputs must retain the exact eligible raw quantity cell and its certified knowledge.");
        return cell;
    }
    private ScheduleQuantityColumn Capacity(GenerationAsset asset, UtcGrid grid, ScheduleAssetMeasure measure)
    {
        var groups = Enumerable.Range(0, grid.Count).Select(n => (Cell: n, Version: AssetDefinitions.At(asset, grid.Start(n), grid.End(n)))).GroupBy(p => p.Version.From);
        var links = new ScheduleQuantityCell[grid.Count];
        foreach (var group in groups)
        {
            cancellation.ThrowIfCancellationRequested(); var selected = group.ToArray(); var version = selected[0].Version;
            var key = (new AssetVersionKey(asset.Key, version.From), measure);
            if (!capacities.TryGetValue(key, out var input))
            {
                var amount = measure == ScheduleAssetMeasure.CapacityBasis ? version.CapacityBasis : version.AccreditedCapacity
                    ?? throw PpaErrors.Error("asset_version", "accreditation", "Every asset requires explicit accredited capacity.");
                input = graph.QuantityInput(new ScheduleSourceKey.Asset(key.Item1, version.To, measure),
                    ExactQuantity.FromQuantity(new(amount, asset.CapacityQuantity.Key)), ScheduleInputUse.Frozen);
                capacities.Add(key, input);
            }
            var block = Subgrid(grid, selected.Select(p => p.Cell).ToArray());
            var column = graph.Broadcast(input, block);
            for (var n = 0; n < selected.Length; n++) links[selected[n].Cell] = column.Cell(n);
        }
        return graph.Gather(grid, [..links]);
    }
    private PpaGraphQuantities Accredited(CapacityDeliverySide side, ImmutableArray<PreparedRowInputs> rows, UtcGrid grid)
    {
        if (rows.Length != 1) throw PpaErrors.Error("ppa_cadence", "capacity", "Accredited capacity has one whole operating-month row.");
        var month = Month(rows[0].Quantity.Interval); var clock = inputs.Reference.Clocks[side.Terms.Delivery.Clock];
        var retainedDays=side.RetainedPlan?.CapacityDays.Where(day=>day.OperatingDate.Year==month.Year&&day.OperatingDate.Month==month.Month).ToArray();
        var days = retainedDays is null?UtcGrid.Create(Enumerable.Range(0, DateTime.DaysInMonth(month.Year, month.Month) + 1)
            .Select(n => clock.StartOfDay(month.AddDays(n)).ToUniversalTime()).ToArray()):Grid(retainedDays);
        var assets = RetainedSideSchedules.Terms(side).Normalization.Assets.Select(k => inputs.Reference.Assets[k]).ToArray();
        var denominator = graph.Add([..assets.Select(a => Capacity(a, days, ScheduleAssetMeasure.CapacityBasis))]);
        var components = assets.ToImmutableDictionary(a => a.Key, a =>
        {
            var ratios = graph.Normalize(Capacity(a, days, ScheduleAssetMeasure.AccreditedCapacity), denominator);
            var factor=side.RetainedPlan?.CapacityEntitlementFactor??PpaErrors.One;
            var entitlement = graph.Transform(ratios, Enumerable.Repeat((factor.ScaledValue, ExactDecimal.WorkingScale), days.Count).ToImmutableArray(), side.CapacityQuantity);
            return graph.Broadcast(graph.Sum(entitlement, days.Select([..Enumerable.Range(0, days.Count)])), grid);
        });
        return new(graph.Add(components.Values.ToImmutableArray()), components);
    }
    internal static DateOnly Month(DeliveryInterval interval) => new(interval.OperatingDate.Year, interval.OperatingDate.Month, 1);
    internal static UtcGrid Grid(IEnumerable<DeliveryInterval> intervals)
    {
        var selected = intervals.ToArray();
        if (selected.Length == 0) throw PpaErrors.Error("ppa_cadence", "grid", "Select a nonempty native schedule block.");
        var duration = selected[0].End - selected[0].Start;
        if (selected.All(i => i.End - i.Start == duration)) return UtcGrid.Sparse([..selected.Select(i => i.Start.UtcTicks)], duration);
        if (selected.Zip(selected.Skip(1)).Any(p => p.First.End != p.Second.Start)) throw PpaErrors.Error("ppa_cadence", "grid", "Irregular intervals require an exact contiguous grid.");
        return UtcGrid.Create([..selected.Select(i => i.Start), selected[^1].End]);
    }
    internal static UtcGrid Subgrid(UtcGrid grid, int[] selected) => Grid(selected.Select(n =>
        new DeliveryInterval(grid.Start(n), grid.End(n), DateOnly.FromDateTime(grid.Start(n).UtcDateTime), 0, 0, 1)));
}
