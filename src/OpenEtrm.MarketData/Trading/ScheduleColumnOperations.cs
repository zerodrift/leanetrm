using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.Shared.Calculation;
using System.Numerics;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.Trading;

internal sealed record ScheduleShapeColumn : ScheduleColumn
{
    internal ScheduleShapeColumn(Guid owner, int index, UtcGrid grid) : base(owner, index, grid) { }
}
internal abstract partial record ScheduleGraphValue
{
    internal sealed record ShapeCells(IntervalSelection Values) : ScheduleGraphValue;
}
internal abstract partial record ScheduleGraphOperation
{
    internal sealed record Gather(ImmutableArray<(int Node, int Cell)> Links, UtcGrid Grid, bool Price) : ScheduleGraphOperation([..Links.Select(l => l.Node).Distinct()])
    {
        internal override int Cells => Grid.Count;
        internal override object Evidence
        {
            get
            {
                var sources = Dependencies.Select((node, ordinal) => (node, ordinal)).ToDictionary(p => p.node, p => p.ordinal);
                return new { Kind = Price ? "gather_prices" : "gather_quantities", Grid = ScheduleGraph.GridEvidence(Grid),
                    Cells = ScheduleGraph.HashArray(writer => { foreach (var link in Links) { writer.WriteNumberValue(sources[link.Node]); writer.WriteNumberValue(link.Cell); } }) };
            }
        }
        internal override ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs, CancellationToken ct)
        {
            // Each dependency is fetched once. The cell map stores two integers,
            // not a graph node, delegate or financial object for each output row.
            var columns = new Dictionary<int, ScheduleGraphValue>();
            foreach (var dependency in Dependencies) { ct.ThrowIfCancellationRequested(); columns.Add(dependency, inputs.Get(dependency)); }
            if (Price)
            {
                var result = ImmutableArray.CreateBuilder<ExactUnitPrice>(Links.Length);
                foreach (var link in Links) { ct.ThrowIfCancellationRequested(); result.Add(((ScheduleGraphValue.Prices)columns[link.Node]).Values[link.Cell]); }
                return new ScheduleGraphValue.Prices(result.MoveToImmutable());
            }
            var quantities = ImmutableArray.CreateBuilder<ExactQuantity>(Links.Length);
            foreach (var link in Links) { ct.ThrowIfCancellationRequested(); quantities.Add(((ScheduleGraphValue.Quantities)columns[link.Node]).Values[link.Cell]); }
            return new ScheduleGraphValue.Quantities(quantities.MoveToImmutable());
        }
    }
    internal sealed record QuantityFold(int Input, UtcSelection Selection, ReferenceKey Unit) : ScheduleGraphOperation([Input])
    {
        internal override object Evidence => new { Kind = "quantity_sum", Selection = ScheduleGraph.Selected(Selection), Unit };
        internal override ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs, CancellationToken ct)
        {
            var values = ((ScheduleGraphValue.Quantities)inputs.Get(Input)).Values; ExactQuantity? sum = null;
            foreach (var cell in Selection) { ct.ThrowIfCancellationRequested(); sum = sum is null ? values[cell] : sum.Add(values[cell]); }
            return new ScheduleGraphValue.Quantity(sum ?? ExactQuantity.FromQuantity(new(default, Unit)));
        }
    }
    internal sealed record ColumnAdd(ImmutableArray<int> Inputs, UtcGrid Grid, bool Cash) : ScheduleGraphOperation(Inputs)
    {
        internal override int Cells => Grid.Count;
        internal override object Evidence => new { Kind = Cash ? "cash_column_add" : "quantity_column_add", Grid = ScheduleGraph.GridEvidence(Grid) };
        internal override ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs, CancellationToken ct)
        {
            if (Cash)
            {
                var columns = new List<ImmutableArray<ExactMoney>>();
                foreach (var dependency in Inputs) { ct.ThrowIfCancellationRequested(); columns.Add(((ScheduleGraphValue.Cashflows)inputs.Get(dependency)).Values); }
                var values = ImmutableArray.CreateBuilder<ExactMoney>(Grid.Count);
                for (var n = 0; n < Grid.Count; n++) { ct.ThrowIfCancellationRequested(); var value = columns[0][n]; for (var c = 1; c < columns.Count; c++) value = value.Add(columns[c][n]); values.Add(value); }
                return new ScheduleGraphValue.Cashflows(values.MoveToImmutable());
            }
            var quantities = new List<ImmutableArray<ExactQuantity>>();
            foreach (var dependency in Inputs) { ct.ThrowIfCancellationRequested(); quantities.Add(((ScheduleGraphValue.Quantities)inputs.Get(dependency)).Values); }
            var result = ImmutableArray.CreateBuilder<ExactQuantity>(Grid.Count);
            for (var n = 0; n < Grid.Count; n++) { ct.ThrowIfCancellationRequested(); var value = quantities[0][n]; for (var c = 1; c < quantities.Count; c++) value = value.Add(quantities[c][n]); result.Add(value); }
            return new ScheduleGraphValue.Quantities(result.MoveToImmutable());
        }
    }
    internal sealed record QuantityTransform(int Input, UtcGrid Grid, ImmutableArray<(BigInteger Numerator, BigInteger Denominator)> Factors, ReferenceKey Target) : ScheduleGraphOperation([Input])
    {
        internal override int Cells => Grid.Count;
        internal override object Evidence => new { Kind = "contract_quantity_transform", Grid = ScheduleGraph.GridEvidence(Grid), Target,
            Factors = ScheduleGraph.HashArray(writer => { foreach (var factor in Factors) { writer.WriteStringValue(factor.Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture)); writer.WriteStringValue(factor.Denominator.ToString(System.Globalization.CultureInfo.InvariantCulture)); } }) };
        internal override ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs, CancellationToken ct)
        {
            var values = ((ScheduleGraphValue.Quantities)inputs.Get(Input)).Values;
            var result = ImmutableArray.CreateBuilder<ExactQuantity>(values.Length);
            for (var n = 0; n < values.Length; n++) { ct.ThrowIfCancellationRequested(); result.Add(new(values[n].NumeratorValue * Factors[n].Numerator, values[n].DenominatorValue * Factors[n].Denominator, Target)); }
            return new ScheduleGraphValue.Quantities(result.MoveToImmutable());
        }
    }
    internal sealed record VolumeAdjustments(int Input, UtcGrid Grid, VolumeSourceDefinition Definition, VolumeState State,
        QuantityStage InputStage, QuantityStage TargetStage, ImmutableArray<DateOnly> OperatingDates) : ScheduleGraphOperation([Input])
    {
        internal override int Cells => Grid.Count;
        internal override object Evidence => new { Kind = AssetVolumes.Implementation, Definition.Key, Definition.Asset, State, InputStage, TargetStage,
            Loss = Definition.Loss.ToString(), Availability = new { Definition.Availability.ReflectedInForecast, Factor = Definition.Availability.Factor?.ToString() },
            Curtailment = new { Definition.Curtailment.ReflectedInForecast, Factor = Definition.Curtailment.Factor?.ToString() },
            Degradation = Definition.Degradation is {} d ? new { Rate = d.Rate.ToString(), d.BaseDate, d.Basis } : null,
            Dates = ScheduleGraph.HashArray(writer => { foreach (var date in OperatingDates) writer.WriteNumberValue(date.DayNumber); }) };
        internal override ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs, CancellationToken ct)
        {
            var values = ((ScheduleGraphValue.Quantities)inputs.Get(Input)).Values;
            var result = ImmutableArray.CreateBuilder<ExactQuantity>(values.Length);
            for (var n = 0; n < values.Length; n++)
            {
                ct.ThrowIfCancellationRequested();
                result.Add(AssetVolumes.ApplyAdjustments(values[n], Definition, State, InputStage, TargetStage, OperatingDates[n]));
            }
            return new ScheduleGraphValue.Quantities(result.MoveToImmutable());
        }
    }
    internal sealed record AffinePrices(int Input, UtcGrid Grid, ExactDecimal Multiplier, UnitPrice Adder) : ScheduleGraphOperation([Input])
    {
        internal override int Cells => Grid.Count;
        internal override object Evidence => new { Kind = "index_price_affine", Grid = ScheduleGraph.GridEvidence(Grid), Multiplier = Multiplier.ToString(), Adder = Adder.Value.ToString(), Unit = ScheduleGraph.UnitEvidence(Adder.Unit) };
        internal override ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs, CancellationToken ct)
        {
            var values = ((ScheduleGraphValue.Prices)inputs.Get(Input)).Values;
            var result = ImmutableArray.CreateBuilder<ExactUnitPrice>(values.Length);
            var add = ExactUnitPrice.FromPrice(Adder);
            foreach (var value in values) { ct.ThrowIfCancellationRequested(); result.Add(value.ScaleFraction(Multiplier.ScaledValue, ExactDecimal.WorkingScale).Add(add)); }
            return new ScheduleGraphValue.Prices(result.MoveToImmutable());
        }
    }
    internal sealed record ShapePrices(int Mark, int Shape, ShapeMethod Method, UtcGrid Grid) : ScheduleGraphOperation([Mark, Shape])
    {
        internal override int Cells => Grid.Count;
        internal override object Evidence => new { Kind = BlockShaping.OperatorVersion, Policy = BlockShaping.CalibrationPolicy, Method, Grid = ScheduleGraph.GridEvidence(Grid) };
        internal override ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var mark = ((ScheduleGraphValue.Price)inputs.Get(Mark)).Value.ToUnitPriceExact();
            var shape = ((ScheduleGraphValue.ShapeCells)inputs.Get(Shape)).Values;
            var values = BlockShaping.Calibrate(Method, mark, shape);
            ct.ThrowIfCancellationRequested();
            return new ScheduleGraphValue.Prices(values);
        }
    }
}
public sealed partial class ScheduleGraphBuilder
{
    public SchedulePriceColumn FrozenPrices(ScheduleSourceKey key, UtcGrid grid, ImmutableArray<ExactUnitPrice> values)
    {
        Grid(grid);
        if (values.IsDefault || values.Length != grid.Count || values.Any(v => v is null) || values.Select(v => v.Unit).Distinct().Count() != 1)
            throw ScheduleGraph.Error("unit_mismatch", "A frozen column requires one exact price per interval in one unit.");
        return Source(key, ScheduleInputUse.Frozen, new ScheduleGraphValue.Prices(values), grid, n => new SchedulePriceColumn(owner, n, grid, values[0].Unit));
    }
    public SchedulePriceColumn Gather(UtcGrid grid, ImmutableArray<SchedulePriceCell> cells)
    {
        Grid(grid); if (cells.IsDefault || cells.Length != grid.Count) throw ScheduleGraph.Error("interval_alignment", "Declare one exact source cell per output interval.");
        foreach (var cell in cells) { Require(cell.Column); cell.Column.RequireCell(cell.Index); }
        if (cells.Select(c => c.Column.Unit).Distinct().Count() != 1) throw ScheduleGraph.Error("unit_mismatch", "Gathered prices require one exact unit.");
        for (var n = 0; n < cells.Length; n++) MatchCell(grid, n, cells[n].Column.Grid, cells[n].Index);
        return new(owner, Append(new ScheduleGraphOperation.Gather([..cells.Select(c => (c.Column.Index, c.Index))], grid, true)), grid, cells[0].Column.Unit);
    }
    internal void CountRowAmounts(ScheduleCashColumn column) { Require(column); rowAmounts.Add(column.Index); }
    internal SchedulePriceColumn ObservePeriods(UtcGrid grid, ImmutableArray<SchedulePriceCell> cells, ImmutableArray<DateOnly> operatingDates)
    {
        Grid(grid);
        if (cells.IsDefault || cells.Length != grid.Count || operatingDates.IsDefault || operatingDates.Length != grid.Count)
            throw ScheduleGraph.Error("interval_alignment", "Period prices require one exact operating date and vintage reference per row.");
        for (var n = 0; n < cells.Length; n++)
        {
            var cell = cells[n]; Require(cell.Column); cell.Column.RequireCell(cell.Index);
            var from = DateOnly.FromDateTime(cell.Column.Grid.Start(cell.Index).UtcDateTime);
            var to = DateOnly.FromDateTime(cell.Column.Grid.End(cell.Index).UtcDateTime);
            if (operatingDates[n] < from || operatingDates[n] >= to) throw ScheduleGraph.Error("interval_alignment", "The explicit generation vintage must contain the row's operating date.");
        }
        if (cells.Select(c => c.Column.Unit).Distinct().Count() != 1) throw ScheduleGraph.Error("unit_mismatch", "Vintage prices require one exact unit.");
        return new(owner, Append(new ScheduleGraphOperation.Gather([..cells.Select(c => (c.Column.Index, c.Index))], grid, true)), grid, cells[0].Column.Unit);
    }
    public ScheduleQuantityColumn Gather(UtcGrid grid, ImmutableArray<ScheduleQuantityCell> cells)
    {
        Grid(grid); if (cells.IsDefault || cells.Length != grid.Count) throw ScheduleGraph.Error("interval_alignment", "Declare one exact source cell per output interval.");
        foreach (var cell in cells) { Require(cell.Column); cell.Column.RequireCell(cell.Index); }
        if (cells.Select(c => (c.Column.Unit, c.Column.Basis)).Distinct().Count() != 1) throw ScheduleGraph.Error("quantity_mismatch", "Gathered quantities require one exact unit and external basis.");
        for (var n = 0; n < cells.Length; n++) MatchCell(grid, n, cells[n].Column.Grid, cells[n].Index);
        return new(owner, Append(new ScheduleGraphOperation.Gather([..cells.Select(c => (c.Column.Index, c.Index))], grid, false)), grid, cells[0].Column.Unit, cells[0].Column.Basis);
    }
    private static void MatchCell(UtcGrid a, int x, UtcGrid b, int y)
    { if (a.Start(x) != b.Start(y) || a.End(x) != b.End(y)) throw ScheduleGraph.Error("interval_alignment", "A cell reference must retain its exact interval; no resampling is inferred."); }
    public ScheduleQuantityNode Sum(ScheduleQuantityColumn column, UtcSelection selection)
    {
        Require(column); if (selection is null || !column.Grid.Equals(selection.Grid)) throw ScheduleGraph.Error("interval_alignment", "A sum selects cells from its exact column grid.");
        return new(owner, Append(new ScheduleGraphOperation.QuantityFold(column.Index, selection, column.Unit)), column.Unit, column.Basis);
    }
    public ScheduleQuantityColumn Add(ImmutableArray<ScheduleQuantityColumn> columns)
    {
        if (columns.IsDefaultOrEmpty) throw ScheduleGraph.Error("schedule_graph_input", "Supply explicit quantity contributions.");
        foreach (var column in columns) Align(columns[0], column);
        if (columns.Select(c => (c.Unit, c.Basis)).Distinct().Count() != 1) throw ScheduleGraph.Error("quantity_mismatch", "Quantity contributions require one exact unit and external basis.");
        return columns.Length == 1 ? columns[0] : new(owner, Append(new ScheduleGraphOperation.ColumnAdd([..columns.Select(c => c.Index)], columns[0].Grid, false)), columns[0].Grid, columns[0].Unit, columns[0].Basis);
    }
    public ScheduleCashColumn Add(ImmutableArray<ScheduleCashColumn> columns)
    {
        if (columns.IsDefaultOrEmpty) throw ScheduleGraph.Error("schedule_graph_input", "Supply explicit cash contributions.");
        foreach (var column in columns) Align(columns[0], column);
        if (columns.Select(c => (c.Currency, c.Basis)).Distinct().Count() != 1) throw ScheduleGraph.Error("quantity_mismatch", "Cash contributions require one exact currency and external basis.");
        return columns.Length == 1 ? columns[0] : new(owner, Append(new ScheduleGraphOperation.ColumnAdd([..columns.Select(c => c.Index)], columns[0].Grid, true)), columns[0].Grid, columns[0].Currency, columns[0].Basis);
    }
    internal ScheduleQuantityColumn Transform(ScheduleQuantityColumn quantity, ImmutableArray<(BigInteger Numerator, BigInteger Denominator)> factors, ReferenceKey target)
    {
        Require(quantity); Money.RequireKey(target);
        if (factors.IsDefault || factors.Length != quantity.Grid.Count || factors.Any(f => f.Denominator.Sign <= 0))
            throw ScheduleGraph.Error("schedule_graph_input", "A declared contract transform supplies one exact factor per interval.");
        return new(owner, Append(new ScheduleGraphOperation.QuantityTransform(quantity.Index, quantity.Grid, factors, target)), quantity.Grid, target, quantity.Basis);
    }
    internal ScheduleQuantityColumn AdjustVolumes(ScheduleQuantityColumn quantity, VolumeSourceDefinition source, VolumeState state,
        QuantityStage inputStage, QuantityStage targetStage, ImmutableArray<DateOnly> operatingDates)
    {
        Require(quantity);
        if (source is null || state is not (VolumeState.Forecast or VolumeState.Metered or VolumeState.Estimated) || !Enum.IsDefined(inputStage) ||
            !Enum.IsDefined(targetStage) || operatingDates.IsDefault || operatingDates.Length != quantity.Grid.Count)
            throw ScheduleGraph.Error("volume_source", "Volume adjustments require the selected source, native dates and declared quantity stages.");
        return new(owner, Append(new ScheduleGraphOperation.VolumeAdjustments(quantity.Index, quantity.Grid, source, state, inputStage, targetStage, operatingDates)),
            quantity.Grid, quantity.Unit, quantity.Basis);
    }
    public SchedulePriceColumn Affine(SchedulePriceColumn price, ExactDecimal multiplier, UnitPrice adder)
    {
        Require(price); if (adder is null || adder.Unit != price.Unit) throw ScheduleGraph.Error("unit_mismatch", "Index adders retain the exact price unit.");
        return new(owner, Append(new ScheduleGraphOperation.AffinePrices(price.Index, price.Grid, multiplier, adder)), price.Grid, price.Unit);
    }
    internal SchedulePriceColumn Shape(ScheduleSourceKey.Shape key, SchedulePriceNode mark, ShapeMethod method, IntervalSelection values)
    {
        Require(mark); if (values is null || values.Count == 0 || !Enum.IsDefined(method)) throw ScheduleGraph.Error("interval_shape", "Supply the exact prepared shape and its admitted method.");
        var duration = values[0].End - values[0].Start;
        if (values.Any(v => v.End - v.Start != duration)) throw ScheduleGraph.Error("interval_shape", "Shape cells must retain their exact native resolution.");
        var grid = UtcGrid.Sparse([..values.Select(v => v.Start.UtcTicks)], duration); Grid(grid);
        var source = Source(key, ScheduleInputUse.Frozen, new ScheduleGraphValue.ShapeCells(values), grid, n => new ScheduleShapeColumn(owner, n, grid));
        return new(owner, Append(new ScheduleGraphOperation.ShapePrices(mark.Index, source.Index, method, grid)), grid, mark.Unit);
    }
}
public sealed partial class ScheduleGraphSession
{
    // Import only descendant market layers into this same mixed graph. The caller
    // selects the appropriate retained base for siblings; model-input edges and
    // completed unchanged month roots remain in this session's graph.
    public ScheduleGraphSession WithMarketScenario(ConnectedScenario scenario, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (scenario is null || !ReferenceEquals(scenario.Plan, Plan.Prices) || scenario.Layers.Length < marketLayers.Length ||
            marketLayers.Where((layer, n) => !layer.SequenceEqual(scenario.Layers[n])).Any())
            throw ScheduleGraph.Error("schedule_graph_context", "A descendant market scenario must retain this exact admitted plan and every existing layer.");
        var result = this;
        for (var n = marketLayers.Length; n < scenario.Layers.Length; n++)
        {
            cancellation.ThrowIfCancellationRequested(); var layer = scenario.Layers[n];
            result = result.Child(layer.ToDictionary(s => s.Node, s => (ScheduleGraphValue)new ScheduleGraphValue.Price(ExactUnitPrice.FromPrice(new(s.Size, s.Unit)))),
                "market_shift", cancellation, result.marketLayers.Add(layer));
        }
        return result;
    }
}
