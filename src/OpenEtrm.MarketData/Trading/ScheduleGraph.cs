using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.Shared.Calculation;
using OpenEtrm.Shared.Temporal;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenEtrm.MarketData.Checks")]

namespace OpenEtrm.MarketData.Trading;

// These are admitted input identities, not permission grants. Series cells retain
// their revision and certified knowledge; asset and contract terms are frozen.
public abstract record ScheduleSourceKey
{
    public sealed record Series(DefinitionRevision Definitions, Guid Id, int Revision,
        DateTimeOffset From, DateTimeOffset To, DateTimeOffset KnownAt, Guid KnowledgeContext) : ScheduleSourceKey;
    public sealed record Asset(AssetVersionKey Version, DateTimeOffset To, ScheduleAssetMeasure Measure) : ScheduleSourceKey;
    public sealed record Contract(DefinitionIdentity Definition, int Ordinal) : ScheduleSourceKey;
    internal sealed record Shape(DefinitionRevision Definitions, Guid SeriesId, CurveRequest Anchor,
        DateTimeOffset KnownAt, Guid KnowledgeContext, ImmutableArray<int> Revisions) : ScheduleSourceKey;
    internal sealed record FrozenPrice(CurveKey Curve, DateTimeOffset From, DateTimeOffset To,
        string Scenario, DateTimeOffset KnownAt, ImmutableArray<ObservationKey> Observations,
        Guid? ActualBatch, int? ActualRevision) : ScheduleSourceKey;
    internal sealed record Rate(ReferenceKey Specification, SeriesKey SeriesId, ReferenceKey Source, ObservationKey Observation, DateTimeOffset ObservedAt) : ScheduleSourceKey;
}
public enum ScheduleAssetMeasure { CapacityBasis = 1, AccreditedCapacity }
public enum ScheduleInputUse { Frozen = 1, ModelInput }
public sealed record ScheduleGraphLimits(int Nodes, int Edges, int ColumnCells, long TotalColumnCells)
{
    internal void Require()
    {
        if (Nodes <= 0 || Nodes == int.MaxValue || Edges <= 0 || ColumnCells <= 0 || TotalColumnCells < ColumnCells)
            throw ScheduleGraph.Error("schedule_graph_size", "Supply explicit positive graph and column work bounds.");
    }
}
public abstract record ScheduleNode
{
    internal Guid Owner { get; }
    internal int Index { get; }
    private protected ScheduleNode(Guid owner, int index) { Owner = owner; Index = index; }
}
public sealed record SchedulePriceNode : ScheduleNode
{
    public PriceUnit Unit { get; }
    internal SchedulePriceNode(Guid owner, int index, PriceUnit unit) : base(owner, index) { Unit = unit; }
}
public sealed record ScheduleQuantityNode : ScheduleNode
{
    public ReferenceKey Unit { get; }
    public ReferenceKey? Basis { get; }
    internal ScheduleQuantityNode(Guid owner, int index, ReferenceKey unit, ReferenceKey? basis = null) : base(owner, index) { Unit = unit; Basis = basis; }
}
public sealed record ScheduleCashNode : ScheduleNode
{
    public ReferenceKey Currency { get; }
    public ReferenceKey? Basis { get; }
    internal ScheduleCashNode(Guid owner, int index, ReferenceKey currency, ReferenceKey? basis = null) : base(owner, index) { Currency = currency; Basis = basis; }
}
public sealed record ScheduleDiscountNode : ScheduleNode
{
    public SidePaymentKey Payment { get; }
    internal ScheduleDiscountNode(Guid owner, int index, SidePaymentKey payment) : base(owner, index) { Payment = payment; }
}
public abstract record ScheduleColumn : ScheduleNode
{
    public UtcGrid Grid { get; }
    private protected ScheduleColumn(Guid owner, int index, UtcGrid grid) : base(owner, index) { Grid = grid; }
    internal void RequireCell(int cell)
    { if ((uint)cell >= (uint)Grid.Count) throw ScheduleGraph.Error("schedule_graph_cell", "The cell is outside its exact column grid."); }
}
public sealed record SchedulePriceColumn : ScheduleColumn
{
    public PriceUnit Unit { get; }
    internal SchedulePriceColumn(Guid owner, int index, UtcGrid grid, PriceUnit unit) : base(owner, index, grid) { Unit = unit; }
    public SchedulePriceCell Cell(int cell) { RequireCell(cell); return new(this, cell); }
}
public sealed record ScheduleQuantityColumn : ScheduleColumn
{
    public ReferenceKey Unit { get; }
    public ReferenceKey? Basis { get; }
    internal ScheduleQuantityColumn(Guid owner, int index, UtcGrid grid, ReferenceKey unit, ReferenceKey? basis = null) : base(owner, index, grid) { Unit = unit; Basis = basis; }
    public ScheduleQuantityCell Cell(int cell) { RequireCell(cell); return new(this, cell); }
}
public sealed record ScheduleCashColumn : ScheduleColumn
{
    public ReferenceKey Currency { get; }
    public ReferenceKey? Basis { get; }
    internal ScheduleCashColumn(Guid owner, int index, UtcGrid grid, ReferenceKey currency, ReferenceKey? basis) : base(owner, index, grid) { Currency = currency; Basis = basis; }
    public ScheduleCashCell Cell(int cell) { RequireCell(cell); return new(this, cell); }
}
public sealed record ScheduleCapacityFactors : ScheduleColumn
{
    internal ScheduleCapacityFactors(Guid owner, int index, UtcGrid grid) : base(owner, index, grid) { }
}
// Cell references have no financial values and allocate no object per row.
public readonly record struct SchedulePriceCell(SchedulePriceColumn Column, int Index);
public readonly record struct ScheduleQuantityCell(ScheduleQuantityColumn Column, int Index);
public readonly record struct ScheduleCashCell(ScheduleCashColumn Column, int Index);

internal abstract partial record ScheduleGraphValue
{
    internal sealed record Price(ExactUnitPrice Value) : ScheduleGraphValue;
    internal sealed record Quantity(ExactQuantity Value) : ScheduleGraphValue;
    internal sealed record Cash(ExactMoney Value) : ScheduleGraphValue;
    internal sealed record Factor(ExactDecimal Value) : ScheduleGraphValue;
    internal sealed record Prices(ImmutableArray<ExactUnitPrice> Values) : ScheduleGraphValue;
    internal sealed record Quantities(ImmutableArray<ExactQuantity> Values) : ScheduleGraphValue;
    internal sealed record Cashflows(ImmutableArray<ExactMoney> Values) : ScheduleGraphValue;
    internal sealed record CapacityFactors(ImmutableArray<ExactDecimal> Values) : ScheduleGraphValue;
    internal static ScheduleGraphValue Add(ScheduleGraphValue left, ScheduleGraphValue right) => (left, right) switch
    {
        (Price a, Price b) => new Price(a.Value.Add(b.Value)),
        (Quantity a, Quantity b) => new Quantity(a.Value.Add(b.Value)),
        (AnnualRate a, AnnualRate b) => new AnnualRate(a.Value+b.Value),
        (Quantities a, Quantities b) when a.Values.Length == b.Values.Length => new Quantities([..a.Values.Zip(b.Values, (x, y) => x.Add(y))]),
        (CapacityFactors a, CapacityFactors b) when a.Values.Length == b.Values.Length => new CapacityFactors([..a.Values.Zip(b.Values, (x, y) => x + y)]),
        _ => throw ScheduleGraph.Error("schedule_graph_type", "An input offset must retain its exact supported type, unit and grid.")
    };
}
internal abstract partial record ScheduleGraphOperation(ImmutableArray<int> Dependencies)
{
    internal abstract ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs, CancellationToken ct);
    internal abstract object Evidence { get; }
    internal virtual int Cells => 0;
    internal sealed record Source(ScheduleSourceKey Key, ScheduleInputUse Use, ScheduleGraphValue Value, UtcGrid? Grid = null) : ScheduleGraphOperation([])
    {
        internal override int Cells => Grid?.Count ?? 0;
        internal override object Evidence => new { Kind = "source", Key = ScheduleGraph.SourceEvidence(Key), Use, Grid = ScheduleGraph.GridEvidence(Grid), Value = ScheduleGraph.ValueEvidence(Value) };
        internal override ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs, CancellationToken ct) => Value;
    }
    internal sealed record DiscountSource(SidePaymentFactor Factor) : ScheduleGraphOperation([])
    {
        internal override object Evidence => new { Kind = "discount_input", Factor.Payment, Factor.Source, Value = Factor.DiscountFactor.ToString(), Snapshot = Factor.Snapshot.Id };
        internal override ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs, CancellationToken ct) => new ScheduleGraphValue.Factor(Factor.DiscountFactor);
    }
    internal enum Kind { Normalize, Amount, Discount, Scale, Sum, Broadcast, Prices, GenerateEnergy }
    internal sealed record Compute(Kind Operator, ImmutableArray<int> Inputs, UtcGrid? Grid = null,
        ExactDecimal? Scale = null, ReferenceKey? OutputUnit = null, UtcSelection? Selection = null) : ScheduleGraphOperation(Inputs)
    {
        internal override int Cells => Operator == Kind.Sum ? 0 : Grid?.Count ?? 0;
        internal override object Evidence => new { Kind = Operator.ToString(), Grid = ScheduleGraph.GridEvidence(Grid), Scale = Scale?.ToString(), OutputUnit,
            Selection = Selection is null ? null : ScheduleGraph.Selected(Selection) };
        internal override ScheduleGraphValue Evaluate(GraphInputs<ScheduleGraphValue> inputs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var first = inputs.Get(Inputs[0]);
            if (Operator == Kind.Sum)
            {
                ExactMoney? sum = null;
                if (first is ScheduleGraphValue.Cashflows column)
                {
                    foreach (var cell in Selection!) { ct.ThrowIfCancellationRequested(); sum = sum is null ? column.Values[cell] : sum.Add(column.Values[cell]); }
                    return new ScheduleGraphValue.Cash(sum ?? ExactMoney.FromMoney(new(default, OutputUnit!.Value)));
                }
                foreach (var node in Inputs) { ct.ThrowIfCancellationRequested(); var cash = ((ScheduleGraphValue.Cash)inputs.Get(node)).Value; sum = sum is null ? cash : sum.Add(cash); }
                return new ScheduleGraphValue.Cash(sum!);
            }
            if (Operator == Kind.Broadcast)
            {
                if (first is ScheduleGraphValue.Price p) return new ScheduleGraphValue.Prices(Repeat(p.Value, Grid!.Count, ct));
                return new ScheduleGraphValue.Quantities(Repeat(((ScheduleGraphValue.Quantity)first).Value, Grid!.Count, ct));
            }
            if (Operator == Kind.Prices)
            {
                var result = ImmutableArray.CreateBuilder<ExactUnitPrice>(Inputs.Length);
                foreach (var node in Inputs) { ct.ThrowIfCancellationRequested(); result.Add(((ScheduleGraphValue.Price)inputs.Get(node)).Value); }
                return new ScheduleGraphValue.Prices(result.MoveToImmutable());
            }
            if (Operator == Kind.Scale)
            {
                if (first is ScheduleGraphValue.Quantity q) return new ScheduleGraphValue.Quantity(q.Value.Scale(Scale!.Value));
                if (first is ScheduleGraphValue.Cash cash) return new ScheduleGraphValue.Cash(cash.Value.Scale(Scale!.Value));
                if (first is ScheduleGraphValue.Quantities qs) return new ScheduleGraphValue.Quantities(Map(qs.Values.Length, n => qs.Values[n].Scale(Scale!.Value), ct));
                var cs = (ScheduleGraphValue.Cashflows)first;
                return new ScheduleGraphValue.Cashflows(Map(cs.Values.Length, n => cs.Values[n].Scale(Scale!.Value), ct));
            }
            var second = inputs.Get(Inputs[1]);
            if (Operator == Kind.GenerateEnergy)
            {
                var factors = ((ScheduleGraphValue.CapacityFactors)first).Values;
                var capacity = ((ScheduleGraphValue.Quantities)second).Values;
                return new ScheduleGraphValue.Quantities(Map(factors.Length, n => new ExactQuantity(
                    capacity[n].NumeratorValue * factors[n].ScaledValue * Grid!.DurationTicks(n),
                    capacity[n].DenominatorValue * ExactDecimal.WorkingScale * TimeSpan.TicksPerHour, OutputUnit!.Value), ct));
            }
            if (Operator == Kind.Normalize)
            {
                static ExactQuantity Divide(ExactQuantity energy, ExactQuantity capacity)
                {
                    if (capacity.NumeratorValue.Sign <= 0) throw ScheduleGraph.Error("ppa_normalization", "The declared normalization capacity must be positive.");
                    return energy.ScaleFraction(capacity.DenominatorValue, capacity.NumeratorValue);
                }
                if (first is ScheduleGraphValue.Quantity q) return new ScheduleGraphValue.Quantity(Divide(q.Value, ((ScheduleGraphValue.Quantity)second).Value));
                var energy = ((ScheduleGraphValue.Quantities)first).Values; var capacity = ((ScheduleGraphValue.Quantities)second).Values;
                return new ScheduleGraphValue.Quantities(Map(energy.Length, n => Divide(energy[n], capacity[n]), ct));
            }
            if (Operator == Kind.Amount)
            {
                if (first is ScheduleGraphValue.Price p) return new ScheduleGraphValue.Cash(((ScheduleGraphValue.Quantity)second).Value.Times(p.Value));
                var prices = ((ScheduleGraphValue.Prices)first).Values; var quantities = ((ScheduleGraphValue.Quantities)second).Values;
                return new ScheduleGraphValue.Cashflows(Map(prices.Length, n => quantities[n].Times(prices[n]), ct));
            }
            var factor = ((ScheduleGraphValue.Factor)second).Value;
            if (first is ScheduleGraphValue.Cash value) return new ScheduleGraphValue.Cash(value.Value.Scale(factor));
            var cashflows = ((ScheduleGraphValue.Cashflows)first).Values;
            return new ScheduleGraphValue.Cashflows(Map(cashflows.Length, n => cashflows[n].Scale(factor), ct));
        }
        private static ImmutableArray<T> Repeat<T>(T value, int count, CancellationToken ct) => Map(count, _ => value, ct);
        private static ImmutableArray<T> Map<T>(int count, Func<int, T> value, CancellationToken ct)
        {
            var result = ImmutableArray.CreateBuilder<T>(count);
            for (var n = 0; n < count; n++) { ct.ThrowIfCancellationRequested(); result.Add(value(n)); }
            return result.MoveToImmutable();
        }
    }
}

// Each node is a scalar or one explicitly bounded block/month. Columns retain
// exact values; the graph does not allocate a node or edge for every output cell.
public sealed partial class ScheduleGraphBuilder
{
    private readonly Guid owner = Guid.NewGuid();
    private readonly ConnectedCurvePlan prices;
    private readonly ConnectedScenario.TargetIndex targets;
    private readonly ImmutableArray<ImmutableArray<ResolvedCurveShift>> layers;
    private readonly string seed;
    private readonly MarketSnapshot authority;
    private readonly ResolvedCalculationContext sourceContext;
    private readonly ScheduleGraphLimits limits;
    private readonly List<ScheduleGraphOperation> operations = [];
    private readonly Dictionary<ScheduleSourceKey, (ScheduleNode Node, ScheduleGraphOperation.Source Source)> sources = [];
    private readonly Dictionary<SidePaymentKey, (ScheduleDiscountNode Node, SidePaymentFactor Factor)> discounts = [];
    private readonly HashSet<int> rowAmounts = [];
    private bool compiled;
    private long cells;
    public ScheduleGraphBuilder(ConnectedScenario scenario, MarketSnapshot authority, ScheduleGraphLimits limits,
        ImmutableArray<MarketSnapshot> sourceSnapshots = default)
        : this(ConnectedValuation.Prepare(scenario, authority, sourceSnapshots.IsDefault ? [] : sourceSnapshots), limits) { }
    public ScheduleGraphBuilder(ConnectedValuation admitted, ScheduleGraphLimits limits)
        : this(admitted, limits, admitted?.Scenario.Plan.Context ?? throw ScheduleGraph.Error("schedule_graph_context", "Supply an admitted market context.")) { }
    internal ScheduleGraphBuilder(ConnectedValuation admitted, ScheduleGraphLimits limits, ResolvedCalculationContext sourceContext)
    {
        if (admitted is null || limits is null) throw ScheduleGraph.Error("schedule_graph_context", "Supply the admitted scenario and explicit work bounds.");
        var scenario = admitted.Scenario;
        if (sourceContext is null || sourceContext.ValuationAt != admitted.Snapshot.ValuationAt || sourceContext.MarketAsOf != admitted.Snapshot.MarketCutoff)
            throw ScheduleGraph.Error("schedule_graph_context", "Source admission must retain the exact valuation and market coordinates with its certified knowledge context.");
        this.sourceContext = sourceContext;
        limits.Require();
        if (scenario.Plan.DependencyPlan.NodeCount > limits.Nodes || scenario.Plan.DependencyPlan.EdgeCount > limits.Edges)
            throw ScheduleGraph.Error("schedule_graph_size", "The imported price graph exceeds the admitted graph bounds.");
        prices = scenario.Plan; targets = scenario.Targets; layers = scenario.Layers; seed = scenario.Id;
        authority = admitted.Snapshot; this.limits = limits;
    }
    public SchedulePriceNode Price(CurvePointKey point) => new(owner, prices.IndexOf(point), prices.GetPoint(point).Unit);
    public SchedulePriceNode FixedPrice(ScheduleSourceKey key, ExactUnitPrice value)
    {
        if (value is null) throw ScheduleGraph.Error("schedule_graph_input", "Supply an exact fixed price.");
        return Source(key, ScheduleInputUse.Frozen, new ScheduleGraphValue.Price(value), null, n => new SchedulePriceNode(owner, n, value.Unit));
    }
    public ScheduleQuantityNode QuantityInput(ScheduleSourceKey key, ExactQuantity value, ScheduleInputUse use)
    {
        if (value is null) throw ScheduleGraph.Error("schedule_graph_input", "Supply an exact quantity input.");
        return Source(key, use, new ScheduleGraphValue.Quantity(value), null, n => new ScheduleQuantityNode(owner, n, value.Specification));
    }
    public ScheduleQuantityColumn QuantityColumn(ScheduleSourceKey key, UtcGrid grid, ImmutableArray<ExactQuantity> values, ScheduleInputUse use)
    {
        Grid(grid); if (values.IsDefault || values.Length != grid.Count || values.Any(v => v is null) || values.Select(v => v.Specification).Distinct().Count() != 1)
            throw ScheduleGraph.Error("quantity_mismatch", "Every exact grid cell requires one quantity in the same specification.");
        return Source(key, use, new ScheduleGraphValue.Quantities(values), grid, n => new ScheduleQuantityColumn(owner, n, grid, values[0].Specification));
    }
    public ScheduleCapacityFactors CapacityFactors(ScheduleSourceKey key, UtcGrid grid, ImmutableArray<ExactDecimal> values, ScheduleInputUse use)
    {
        Grid(grid); if (values.IsDefault || values.Length != grid.Count) throw ScheduleGraph.Error("schedule_graph_input", "Supply one capacity factor for every exact interval.");
        ScheduleGraph.RequireFactors(values);
        return Source(key, use, new ScheduleGraphValue.CapacityFactors(values), grid, n => new ScheduleCapacityFactors(owner, n, grid));
    }
    public ScheduleDiscountNode DiscountInput(SidePaymentFactor factor)
    {
        Open();
        if (factor is null || !authority.HasSameInputs(factor.Snapshot) || factor.DiscountFactor.CompareTo(default) <= 0 || !Enum.IsDefined(factor.Source) ||
            factor.Source == DiscountFactorSource.Undiscounted && factor.DiscountFactor != ExactDecimal.Parse("1"))
            throw ScheduleGraph.Error("side_payment_factors", "The factor requires this exact snapshot, value key and explicit supported source.");
        factor.Payment.RequireUndue(authority);
        if((factor.Source==DiscountFactorSource.CurrencyDefault)!=(factor.RateDefinition is not null))
            throw ScheduleGraph.Error("discount_curve","Currency-default discounting requires its rate derivation; explicit and undiscounted factors take none.");
        if (discounts.TryGetValue(factor.Payment, out var prior))
        {
            if (prior.Factor.DiscountFactor != factor.DiscountFactor || prior.Factor.Source != factor.Source || prior.Factor.RateDefinition?.Identity != factor.RateDefinition?.Identity)
                throw ScheduleGraph.Error("side_payment_factors", "One exact value key cannot carry conflicting discount inputs.");
            return prior.Node;
        }
        var node = new ScheduleDiscountNode(owner, factor.RateDefinition is null?Append(new ScheduleGraphOperation.DiscountSource(factor)):RateDiscount(factor), factor.Payment);
        discounts.Add(factor.Payment, (node, factor)); return node;
    }
    public ScheduleQuantityNode Normalize(ScheduleQuantityNode quantity, ScheduleQuantityNode capacity)
    {
        Require(quantity); Require(capacity); Basis(quantity.Basis, capacity.Basis);
        return new(owner, Compute(ScheduleGraphOperation.Kind.Normalize, [quantity.Index, capacity.Index]), quantity.Unit, capacity.Unit);
    }
    public ScheduleQuantityColumn Normalize(ScheduleQuantityColumn quantity, ScheduleQuantityColumn capacity)
    {
        Align(quantity, capacity); Basis(quantity.Basis, capacity.Basis);
        return new(owner, Compute(ScheduleGraphOperation.Kind.Normalize, [quantity.Index, capacity.Index], quantity.Grid), quantity.Grid, quantity.Unit, capacity.Unit);
    }
    public ScheduleQuantityColumn GenerateEnergy(ScheduleCapacityFactors factors, ScheduleQuantityColumn capacity,
        QuantitySpecification capacitySpecification, QuantitySpecification energySpecification)
    {
        Align(factors, capacity);
        if (capacity.Basis is not null || capacitySpecification is null || energySpecification is null || capacity.Unit != capacitySpecification.Key ||
            capacitySpecification.Unit.Code != "megawatt" || capacitySpecification.Dimension.Code != "power")
            throw ScheduleGraph.Error("volume_source", "Generation requires an absolute MW capacity column and exact MW/MWh specifications.");
        AssetDefinitions.Energy(capacitySpecification, energySpecification, capacitySpecification.Key.Revision);
        return new(owner, Compute(ScheduleGraphOperation.Kind.GenerateEnergy, [factors.Index, capacity.Index], factors.Grid, output: energySpecification.Key), factors.Grid, energySpecification.Key);
    }
    public ScheduleCashNode Amount(SchedulePriceNode price, ScheduleQuantityNode quantity)
    {
        Require(price); Require(quantity); PriceQuantity(price.Unit, quantity.Unit);
        return new(owner, Compute(ScheduleGraphOperation.Kind.Amount, [price.Index, quantity.Index]), price.Unit.Currency, quantity.Basis);
    }
    public ScheduleCashColumn Amount(SchedulePriceColumn price, ScheduleQuantityColumn quantity)
    {
        Align(price, quantity); PriceQuantity(price.Unit, quantity.Unit);
        return new(owner, Compute(ScheduleGraphOperation.Kind.Amount, [price.Index, quantity.Index], price.Grid), price.Grid, price.Unit.Currency, quantity.Basis);
    }
    public ScheduleCashNode Discount(ScheduleCashNode amount, ScheduleDiscountNode discount)
    {
        Require(amount); Require(discount); Currency(amount.Currency, discount.Payment.Currency);
        return new(owner, Compute(ScheduleGraphOperation.Kind.Discount, [amount.Index, discount.Index]), amount.Currency, amount.Basis);
    }
    public ScheduleCashColumn Discount(ScheduleCashColumn amount, ScheduleDiscountNode discount)
    {
        Require(amount); Require(discount); Currency(amount.Currency, discount.Payment.Currency);
        return new(owner, Compute(ScheduleGraphOperation.Kind.Discount, [amount.Index, discount.Index], amount.Grid), amount.Grid, amount.Currency, amount.Basis);
    }
    public ScheduleQuantityNode Scale(ScheduleQuantityNode value, ExactDecimal factor)
    { Require(value); return new(owner, Compute(ScheduleGraphOperation.Kind.Scale, [value.Index], scale: factor), value.Unit, value.Basis); }
    public ScheduleQuantityColumn Scale(ScheduleQuantityColumn value, ExactDecimal factor)
    { Require(value); return new(owner, Compute(ScheduleGraphOperation.Kind.Scale, [value.Index], value.Grid, factor), value.Grid, value.Unit, value.Basis); }
    public ScheduleCashNode Scale(ScheduleCashNode value, ExactDecimal factor)
    { Require(value); return new(owner, Compute(ScheduleGraphOperation.Kind.Scale, [value.Index], scale: factor), value.Currency, value.Basis); }
    public ScheduleCashColumn Scale(ScheduleCashColumn value, ExactDecimal factor)
    { Require(value); return new(owner, Compute(ScheduleGraphOperation.Kind.Scale, [value.Index], value.Grid, factor), value.Grid, value.Currency, value.Basis); }
    public SchedulePriceColumn Broadcast(SchedulePriceNode value, UtcGrid grid)
    { Require(value); Grid(grid); return new(owner, Compute(ScheduleGraphOperation.Kind.Broadcast, [value.Index], grid), grid, value.Unit); }
    public ScheduleQuantityColumn Broadcast(ScheduleQuantityNode value, UtcGrid grid)
    { Require(value); Grid(grid); return new(owner, Compute(ScheduleGraphOperation.Kind.Broadcast, [value.Index], grid), grid, value.Unit, value.Basis); }
    public SchedulePriceColumn Prices(UtcGrid grid, ImmutableArray<SchedulePriceNode> values)
    {
        Grid(grid); if (values.IsDefault || values.Length != grid.Count) throw ScheduleGraph.Error("schedule_graph_input", "Declare one price reference for every grid cell.");
        foreach (var node in values) Require(node);
        if (values.Select(p => p.Unit).Distinct().Count() != 1) throw ScheduleGraph.Error("unit_mismatch", "A price column requires one exact unit.");
        return new(owner, Compute(ScheduleGraphOperation.Kind.Prices, [..values.Select(v => v.Index)], grid), grid, values[0].Unit);
    }
    public ScheduleCashNode Sum(ScheduleCashColumn column, UtcSelection selection)
    {
        Require(column); if (selection is null || !column.Grid.Equals(selection.Grid)) throw ScheduleGraph.Error("interval_alignment", "A sum selects cells from its exact column grid.");
        return new(owner, Compute(ScheduleGraphOperation.Kind.Sum, [column.Index], output: column.Currency, selection: selection), column.Currency, column.Basis);
    }
    public ScheduleCashNode Sum(ImmutableArray<ScheduleCashNode> values)
    {
        if (values.IsDefaultOrEmpty) throw ScheduleGraph.Error("schedule_graph_input", "Declare nonempty cash contributions.");
        foreach (var node in values) Require(node);
        if (values.Select(v => (v.Currency, v.Basis)).Distinct().Count() != 1) throw ScheduleGraph.Error("quantity_mismatch", "Cash aggregates retain one currency and one external quantity basis.");
        return new(owner, Compute(ScheduleGraphOperation.Kind.Sum, [..values.Select(v => v.Index)]), values[0].Currency, values[0].Basis);
    }
    public ScheduleGraphPlan Compile(CancellationToken cancellation = default)
    {
        Open(); cancellation.ThrowIfCancellationRequested();
        var edges = new List<DependencyEdge>();
        void Add(int node, int dependency) { if (edges.Count >= limits.Edges) throw ScheduleGraph.Error("schedule_graph_size", "The admitted graph edge bound was exceeded."); edges.Add(new(node, dependency)); }
        for (var node = 0; node < prices.DependencyPlan.NodeCount; node++)
        { cancellation.ThrowIfCancellationRequested(); foreach (var dependency in prices.DependencyPlan.Dependencies(node)) Add(node, dependency); }
        for (var n = 0; n < operations.Count; n++)
        { cancellation.ThrowIfCancellationRequested(); foreach (var dependency in operations[n].Dependencies.Distinct()) Add(prices.DependencyPlan.NodeCount + n, dependency); }
        var plan = ScheduleGraph.Translate(() => DependencyPlan.Compile(prices.DependencyPlan.NodeCount + operations.Count, edges, cancellation));
        var result = new ScheduleGraphPlan(owner, prices, targets, layers, seed, authority, sourceContext, [..operations], plan, rowAmounts.ToImmutableHashSet(), cancellation);
        compiled = true; return result;
    }
    private T Source<T>(ScheduleSourceKey key, ScheduleInputUse use, ScheduleGraphValue value, UtcGrid? grid, Func<int, T> create) where T : ScheduleNode
    {
        Open(); ScheduleGraph.RequireSource(key, use, sourceContext, grid);
        var op = new ScheduleGraphOperation.Source(key, use, value, grid);
        if (sources.TryGetValue(key, out var prior))
        {
            if (prior.Node is not T retained || ScheduleGraph.Hash(prior.Source.Evidence) != ScheduleGraph.Hash(op.Evidence))
                throw ScheduleGraph.Error("schedule_graph_input", "One exact source identity cannot carry conflicting values, use, units or grids.");
            return retained;
        }
        var node = create(Append(op)); sources.Add(key, (node, op)); return node;
    }
    private int Compute(ScheduleGraphOperation.Kind kind, ImmutableArray<int> inputs, UtcGrid? grid = null, ExactDecimal? scale = null, ReferenceKey? output = null, UtcSelection? selection = null)
        => Append(new ScheduleGraphOperation.Compute(kind, inputs, grid, scale, output, selection));
    private int Append(ScheduleGraphOperation op)
    {
        Open(); var node = prices.DependencyPlan.NodeCount + operations.Count;
        if (node >= limits.Nodes || cells + op.Cells > limits.TotalColumnCells) throw ScheduleGraph.Error("schedule_graph_size", "The admitted graph node or column-cell bound was exceeded.");
        cells += op.Cells; operations.Add(op); return node;
    }
    private void Open() { if (compiled) throw ScheduleGraph.Error("schedule_graph_context", "A compiled schedule plan is immutable."); }
    private void Require(ScheduleNode node) { if (node is null || node.Owner != owner) throw ScheduleGraph.Error("schedule_graph_context", "A schedule link must belong to this exact plan."); }
    private void Grid(UtcGrid grid) { if (grid is null || grid.Count == 0 || grid.Count > limits.ColumnCells) throw ScheduleGraph.Error("schedule_graph_size", "A column requires a nonempty exact grid within its admitted cell bound."); }
    private void Align(ScheduleColumn a, ScheduleColumn b) { Require(a); Require(b); if (!a.Grid.Equals(b.Grid)) throw ScheduleGraph.Error("interval_alignment", "Column operators require the same exact UTC grid; no resampling is inferred."); }
    private static void PriceQuantity(PriceUnit price, ReferenceKey quantity) { if (price.Quantity != quantity) throw ScheduleGraph.Error("quantity_mismatch", "The price denominator must match the exact row quantity."); }
    private static void Currency(ReferenceKey a, ReferenceKey b) { if (a != b) throw ScheduleGraph.Error("unit_mismatch", "Discount inputs require the cashflow's exact currency."); }
    private static void Basis(ReferenceKey? a, ReferenceKey? b) { if (a is not null || b is not null) throw ScheduleGraph.Error("ppa_normalization", "Normalize absolute quantities once; retain the resulting external unit basis."); }
}

public sealed class ScheduleGraphPlan
{
    internal Guid Owner { get; }
    internal ConnectedCurvePlan Prices { get; }
    internal ConnectedScenario.TargetIndex Targets { get; }
    internal ImmutableArray<ScheduleGraphOperation> Operations { get; }
    internal ImmutableArray<ImmutableArray<ResolvedCurveShift>> Layers { get; }
    internal ImmutableArray<string> NodeIdentities { get; }
    public DependencyPlan Dependencies { get; }
    public ResolvedCalculationContext Context => Prices.Context;
    public ResolvedCalculationContext SourceContext { get; }
    public string ManifestId { get; }
    public string SeedScenarioId { get; }
    public MarketSnapshot Snapshot { get; }
    private readonly ImmutableArray<long> workWeights;
    internal ScheduleGraphPlan(Guid owner, ConnectedCurvePlan prices, ConnectedScenario.TargetIndex targets,
        ImmutableArray<ImmutableArray<ResolvedCurveShift>> layers, string seed, MarketSnapshot snapshot, ResolvedCalculationContext sourceContext,
        ImmutableArray<ScheduleGraphOperation> operations, DependencyPlan dependencies, ImmutableHashSet<int> rowAmounts, CancellationToken ct)
    {
        Owner = owner; Prices = prices; Targets = targets; Layers = layers; SeedScenarioId = seed; Snapshot = snapshot; SourceContext = sourceContext; Operations = operations; Dependencies = dependencies;
        workWeights = [..Enumerable.Range(0, dependencies.NodeCount).Select(n => rowAmounts.Contains(n) ? (long)operations[n - prices.DependencyPlan.NodeCount].Cells : 0)];
        var identities = ImmutableArray.CreateBuilder<string>(dependencies.NodeCount);
        for (var n = 0; n < prices.DependencyPlan.NodeCount; n++) { ct.ThrowIfCancellationRequested(); identities.Add(ScheduleGraph.Hash(new { prices.ManifestId, Node = n })); }
        foreach (var operation in operations)
        { ct.ThrowIfCancellationRequested(); identities.Add(ScheduleGraph.Hash(new { operation.Evidence, Inputs = operation.Dependencies.Select(n => identities[n]).ToArray() })); }
        NodeIdentities = identities.MoveToImmutable();
        ManifestId = ScheduleGraph.Hash(new { Version = "schedule_graph_v1", prices.ManifestId, Snapshot = snapshot.Id, SourceContext, Nodes = NodeIdentities.Order(StringComparer.Ordinal).ToArray() });
    }
    public ScheduleGraphSession Base(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var values = new GraphSession<ScheduleGraphValue>(Dependencies, Evaluate, ScheduleGraphValue.Add, Validate, workWeights);
        foreach (var layer in Layers)
        {
            cancellation.ThrowIfCancellationRequested();
            values = ScheduleGraph.Translate(() => values.WithOffsets(layer.ToDictionary(s => s.Node,
                s => (ScheduleGraphValue)new ScheduleGraphValue.Price(ExactUnitPrice.FromPrice(new(s.Size, s.Unit)))), cancellation));
        }
        return new(this, values, ScheduleGraph.Hash(new { ManifestId, SeedScenarioId }));
    }
    private ScheduleGraphValue Evaluate(int node, GraphInputs<ScheduleGraphValue> inputs, CancellationToken ct)
        => node < Prices.DependencyPlan.NodeCount ? new ScheduleGraphValue.Price(Prices.EvaluateNode(node, inputs, static value => ((ScheduleGraphValue.Price)value).Value, ct))
            : Operations[node - Prices.DependencyPlan.NodeCount].Evaluate(inputs, ct);
    private void Validate(int node, ScheduleGraphValue value)
    {
        if (node < Prices.DependencyPlan.NodeCount) Prices.ValidateNode(node, ((ScheduleGraphValue.Price)value).Value);
        if (value is ScheduleGraphValue.CapacityFactors factors) ScheduleGraph.RequireFactors(factors.Values);
        if (value is ScheduleGraphValue.AnnualRate rate && (rate.Value.CompareTo(ExactDecimal.Parse("-0.1"))<0 || rate.Value.CompareTo(ExactDecimal.Parse("1"))>0))
            throw ScheduleGraph.Error("rate_domain","The supported annual decimal rate range is -0.1 to 1.");
    }
    internal void Require(ScheduleNode node) { if (node is null || node.Owner != Owner) throw ScheduleGraph.Error("schedule_graph_context", "A schedule link must belong to this exact plan."); }
}
public sealed partial class ScheduleGraphSession
{
    public ScheduleGraphPlan Plan { get; }
    public GraphWork Work => values.Work;
    public string Id { get; }
    private readonly GraphSession<ScheduleGraphValue> values;
    private readonly ImmutableArray<ImmutableArray<ResolvedCurveShift>> marketLayers;
    internal ScheduleGraphSession(ScheduleGraphPlan plan, GraphSession<ScheduleGraphValue> values, string id,
        ImmutableArray<ImmutableArray<ResolvedCurveShift>>? marketLayers = null)
    { Plan = plan; this.values = values; Id = id; this.marketLayers = marketLayers ?? plan.Layers; }
    private ScheduleGraphValue Get(ScheduleNode node, CancellationToken ct) { Plan.Require(node); return ScheduleGraph.Translate(() => values.Get(node.Index, ct)); }
    public ExactQuantity Quantity(ScheduleQuantityNode node, CancellationToken cancellation = default) => ((ScheduleGraphValue.Quantity)Get(node, cancellation)).Value;
    public ExactMoney Cash(ScheduleCashNode node, CancellationToken cancellation = default) => ((ScheduleGraphValue.Cash)Get(node, cancellation)).Value;
    public ExactUnitPrice Price(SchedulePriceNode node, CancellationToken cancellation = default) => ((ScheduleGraphValue.Price)Get(node, cancellation)).Value;
    public ExactDecimal DiscountFactor(ScheduleDiscountNode node,CancellationToken cancellation=default)=>((ScheduleGraphValue.Factor)Get(node,cancellation)).Value;
    public ExactQuantity Quantity(ScheduleQuantityCell cell, CancellationToken cancellation = default)
    { Plan.Require(cell.Column); cell.Column.RequireCell(cell.Index); return ((ScheduleGraphValue.Quantities)Get(cell.Column, cancellation)).Values[cell.Index]; }
    public ExactUnitPrice Price(SchedulePriceCell cell, CancellationToken cancellation = default)
    { Plan.Require(cell.Column); cell.Column.RequireCell(cell.Index); return ((ScheduleGraphValue.Prices)Get(cell.Column, cancellation)).Values[cell.Index]; }
    public ExactMoney Cash(ScheduleCashCell cell, CancellationToken cancellation = default)
    { Plan.Require(cell.Column); cell.Column.RequireCell(cell.Index); return ((ScheduleGraphValue.Cashflows)Get(cell.Column, cancellation)).Values[cell.Index]; }
    // Model-only API: the authorized model must admit its source policies first.
    // Contract and asset terms and sources marked Frozen cannot be replaced.
    internal ScheduleGraphSession WithQuantityInputs(IReadOnlyDictionary<ScheduleQuantityNode, ExactQuantity> changes, CancellationToken cancellation = default)
        => Replace(Changes(changes, value => new ScheduleGraphValue.Quantity(value), cancellation), cancellation);
    internal ScheduleGraphSession WithQuantityColumns(IReadOnlyDictionary<ScheduleQuantityColumn, ImmutableArray<ExactQuantity>> changes, CancellationToken cancellation = default)
        => Replace(Changes(changes, value => new ScheduleGraphValue.Quantities(value), cancellation), cancellation);
    internal ScheduleGraphSession WithCapacityFactors(IReadOnlyDictionary<ScheduleCapacityFactors, ImmutableArray<ExactDecimal>> changes, CancellationToken cancellation = default)
        => Replace(Changes(changes, value => new ScheduleGraphValue.CapacityFactors(value), cancellation), cancellation);
    private static Dictionary<ScheduleNode, ScheduleGraphValue> Changes<TNode, TValue>(IReadOnlyDictionary<TNode, TValue> changes,
        Func<TValue, ScheduleGraphValue> capture, CancellationToken cancellation) where TNode : ScheduleNode
    {
        cancellation.ThrowIfCancellationRequested();
        if (changes is null) throw ScheduleGraph.Error("schedule_graph_input", "Supply explicit admitted source changes.");
        var result = new Dictionary<ScheduleNode, ScheduleGraphValue>();
        foreach (var (node, value) in changes) { cancellation.ThrowIfCancellationRequested(); if (node is null) throw ScheduleGraph.Error("schedule_graph_input", "A source link is required."); result.Add(node, capture(value)); }
        return result;
    }
    private ScheduleGraphSession Replace(IReadOnlyDictionary<ScheduleNode, ScheduleGraphValue> changes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (changes.Count == 0) throw ScheduleGraph.Error("schedule_graph_input", "Supply explicit admitted source changes.");
        var offsets = new Dictionary<int, ScheduleGraphValue>();
        foreach (var (node, value) in changes)
        {
            ct.ThrowIfCancellationRequested(); Plan.Require(node);
            if (node.Index < Plan.Prices.DependencyPlan.NodeCount || Plan.Operations[node.Index - Plan.Prices.DependencyPlan.NodeCount] is not ScheduleGraphOperation.Source { Use: ScheduleInputUse.ModelInput } source)
                throw ScheduleGraph.Error("schedule_graph_input", "Only explicitly variable model sources may change; frozen and derived nodes retain their inputs.");
            var current = Get(node, ct);
            offsets.Add(node.Index, ScheduleGraph.Difference(source.Value, value, current));
        }
        return Child(offsets, "model_input_replacement", ct);
    }
    public ScheduleGraphSession WithMarketShifts(ImmutableArray<CurveShift> shifts, CancellationToken cancellation = default)
    {
        var resolved = ConnectedScenario.ResolveShifts(Plan.Prices, Plan.Targets, shifts,
            point => ((ScheduleGraphValue.Price)ScheduleGraph.Translate(() => values.Get(Plan.Prices.IndexOf(point), cancellation))).Value, cancellation);
        return Child(resolved.ToDictionary(s => s.Node, s => (ScheduleGraphValue)new ScheduleGraphValue.Price(ExactUnitPrice.FromPrice(new(s.Size, s.Unit)))), "market_shift", cancellation, marketLayers.Add(resolved));
    }
    private ScheduleGraphSession Child(Dictionary<int, ScheduleGraphValue> offsets, string kind, CancellationToken ct, ImmutableArray<ImmutableArray<ResolvedCurveShift>>? nextLayers = null)
    {
        var child = ScheduleGraph.Translate(() => values.WithOffsets(offsets, ct));
        var identity = ScheduleGraph.Hash(new { Parent = Id, Kind = kind, Changes = offsets.OrderBy(p => Plan.NodeIdentities[p.Key], StringComparer.Ordinal)
            .Select(p => new { Node = Plan.NodeIdentities[p.Key], Value = ScheduleGraph.ValueEvidence(p.Value) }).ToArray() });
        return new(Plan, child, identity, nextLayers ?? marketLayers);
    }
}
internal static class ScheduleGraph
{
    // Geometry is immutable and carries no authorization or market data. Keep
    // this derived digest attached weakly to its grid, never beyond that grid's
    // lifetime. Equal-but-distinct grids still produce identical receipts.
    private sealed record GridReceipt(int Count, string Digest);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UtcGrid, GridReceipt> gridReceipts = new();
    internal static MarketDataException Error(string code, string message) => new(code, message);
    internal static T Translate<T>(Func<T> run) { try { return run(); } catch (CalculationException error) { throw Error(error.Code, error.Message); } }
    internal static string Hash(object value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    internal static int[] Selected(UtcSelection selection) { var result = new List<int>(); foreach (var cell in selection) result.Add(cell); return [..result]; }
    internal static object UnitEvidence(PriceUnit unit) => new { unit.Currency, unit.Quantity, Multiplier = unit.Multiplier.ToString() };
    internal static string HashArray(Action<Utf8JsonWriter> write)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var buffer = new DigestBuffer(digest);
        using (var writer = new Utf8JsonWriter(buffer)) { writer.WriteStartArray(); write(writer); writer.WriteEndArray(); }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }
    private sealed class DigestBuffer(IncrementalHash digest) : System.Buffers.IBufferWriter<byte>, IDisposable
    {
        private byte[] bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
        public void Advance(int count) => digest.AppendData(bytes.AsSpan(0, count));
        public Memory<byte> GetMemory(int sizeHint = 0) { Require(sizeHint); return bytes; }
        public Span<byte> GetSpan(int sizeHint = 0) { Require(sizeHint); return bytes; }
        private void Require(int sizeHint)
        {
            if (sizeHint <= bytes.Length) return;
            System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
            bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(sizeHint);
        }
        public void Dispose() => System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
    }
    internal static object? GridEvidence(UtcGrid? grid) => grid is null ? null : gridReceipts.GetValue(grid, static value =>
        new(value.Count, HashArray(writer => { for (var n = 0; n < value.Count; n++) { writer.WriteNumberValue(value.Start(n).UtcTicks); writer.WriteNumberValue(value.End(n).UtcTicks); } })));
    internal static object SourceEvidence(ScheduleSourceKey key) => key switch
    {
        ScheduleSourceKey.Series s => new { Kind = "series", s.Definitions, s.Id, s.Revision, s.From, s.To, s.KnownAt, s.KnowledgeContext },
        ScheduleSourceKey.Asset a => new { Kind = "asset", a.Version, a.To, a.Measure },
        ScheduleSourceKey.Contract c => new { Kind = "contract", c.Definition.Version, c.Definition.Digest, c.Ordinal },
        ScheduleSourceKey.Shape s => new { Kind = "shape", s.Definitions, s.SeriesId, s.Anchor, s.KnownAt, s.KnowledgeContext, s.Revisions },
        ScheduleSourceKey.FrozenPrice p => new { Kind = "frozen_price", p.Curve, p.From, p.To, p.Scenario, p.KnownAt, p.Observations, p.ActualBatch, p.ActualRevision },
        ScheduleSourceKey.Rate r => new { Kind="annual_rate", r.Specification, r.SeriesId, r.Source, r.Observation, r.ObservedAt },
        _ => throw Error("schedule_graph_input", "Select an exact supported source identity.")
    };
    internal static object ValueEvidence(ScheduleGraphValue value) => value switch
    {
        ScheduleGraphValue.Price p => new { Kind = "price", Unit = UnitEvidence(p.Value.Unit), p.Value.Numerator, p.Value.Denominator },
        ScheduleGraphValue.Quantity q => new { Kind = "quantity", q.Value.Specification, q.Value.Numerator, q.Value.Denominator },
        ScheduleGraphValue.Factor f => new { Kind = "factor", Value = f.Value.ToString() },
        ScheduleGraphValue.AnnualRate r => new { Kind="annual_rate", Value=r.Value.ToString() },
        ScheduleGraphValue.Prices p => new { Kind = "prices", Unit = UnitEvidence(p.Values[0].Unit), Count = p.Values.Length,
            Digest = HashArray(writer => { foreach (var value in p.Values) { writer.WriteStringValue(value.Numerator); writer.WriteStringValue(value.Denominator); } }) },
        ScheduleGraphValue.Quantities q => new { Kind = "quantities", q.Values[0].Specification, Count = q.Values.Length,
            Digest = HashArray(writer => { foreach (var value in q.Values) { writer.WriteStringValue(value.Numerator); writer.WriteStringValue(value.Denominator); } }) },
        ScheduleGraphValue.CapacityFactors f => new { Kind = "capacity_factors", Count = f.Values.Length,
            Digest = HashArray(writer => { foreach (var value in f.Values) writer.WriteStringValue(value.ToString()); }) },
        ScheduleGraphValue.ShapeCells s => new { Kind = "shape_cells", s.Values.Count,
            Digest = HashArray(writer => { foreach (var cell in s.Values) { writer.WriteNumberValue(cell.Start.UtcTicks); writer.WriteNumberValue(cell.End.UtcTicks); writer.WriteStringValue(cell.Value.ToString()); writer.WriteNumberValue(cell.Revision); } }) },
        _ => throw Error("schedule_graph_input", "Only admitted price and quantity sources enter input evidence.")
    };
    internal static void RequireSource(ScheduleSourceKey key, ScheduleInputUse use, ResolvedCalculationContext context, UtcGrid? grid)
    {
        if (!Enum.IsDefined(use)) throw Error("schedule_graph_input", "Select frozen terms or an explicitly variable model input.");
        switch (key)
        {
            case ScheduleSourceKey.Rate r when use==ScheduleInputUse.ModelInput && grid is null && r.SeriesId.Revision.CatalogId!=Guid.Empty &&
                r.SeriesId.Revision.RevisionId!=Guid.Empty && r.SeriesId.Id!=Guid.Empty && r.Observation.CatalogId==r.SeriesId.Revision.CatalogId &&
                r.Observation.Id!=Guid.Empty && r.Specification.Revision==r.Source.Revision && !string.IsNullOrWhiteSpace(r.Specification.Code) &&
                !string.IsNullOrWhiteSpace(r.Source.Code) && TradingInstant.IsSupported(r.ObservedAt) && r.ObservedAt<=context.MarketAsOf: return;
            case ScheduleSourceKey.Series s when s.Definitions.CatalogId != Guid.Empty && s.Definitions.RevisionId != Guid.Empty && s.Id != Guid.Empty && s.Revision > 0 &&
                TradingInstant.IsSupported(s.From) && TradingInstant.IsSupported(s.To) && s.From < s.To && TradingInstant.IsSupported(s.KnownAt) && s.KnownAt <= context.KnownAt && s.KnowledgeContext != Guid.Empty &&
                (grid is null || s.From <= grid.Start(0) && s.To >= grid.End(grid.Count - 1)): return;
            case ScheduleSourceKey.Asset a when use == ScheduleInputUse.Frozen && a.Version.Asset.Revision.CatalogId != Guid.Empty && a.Version.Asset.Revision.RevisionId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(a.Version.Asset.Code) && TradingInstant.IsSupported(a.Version.From) && TradingInstant.IsSupported(a.To) && a.Version.From < a.To && Enum.IsDefined(a.Measure) &&
                (grid is null || a.Version.From <= grid.Start(0) && a.To >= grid.End(grid.Count - 1)): return;
            case ScheduleSourceKey.Contract c when use == ScheduleInputUse.Frozen && c.Definition is not null && !string.IsNullOrWhiteSpace(c.Definition.Version) && !string.IsNullOrWhiteSpace(c.Definition.Canonical) &&
                c.Definition.Digest == Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(c.Definition.Canonical))) && c.Ordinal >= 0: return;
            case ScheduleSourceKey.Shape s when use == ScheduleInputUse.Frozen && s.Definitions.CatalogId != Guid.Empty && s.Definitions.RevisionId != Guid.Empty &&
                s.SeriesId != Guid.Empty && s.KnowledgeContext != Guid.Empty && TradingInstant.IsSupported(s.KnownAt) && s.KnownAt <= context.KnownAt &&
                grid is not null && !s.Revisions.IsDefault && s.Revisions.Length == grid.Count && s.Revisions.All(r => r > 0): return;
            case ScheduleSourceKey.FrozenPrice p when use == ScheduleInputUse.Frozen && p.Curve.Revision.CatalogId != Guid.Empty && p.Curve.Revision.RevisionId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(p.Curve.Code) && TradingInstant.IsSupported(p.From) && TradingInstant.IsSupported(p.To) && p.From < p.To &&
                !string.IsNullOrWhiteSpace(p.Scenario) && TradingInstant.IsSupported(p.KnownAt) && p.KnownAt <= context.KnownAt && !p.Observations.IsDefault &&
                (p.ActualRevision is null || p.ActualRevision > 0) && p.Observations.All(o => o.CatalogId != Guid.Empty && o.Id != Guid.Empty): return;
            default: throw Error("schedule_graph_input", "Supply exact immutable source versions, eligible knowledge, complete coverage and explicit source use.");
        }
    }
    internal static void RequireFactors(ImmutableArray<ExactDecimal> values)
    {
        if (values.IsDefault || values.Any(v => v.CompareTo(default) < 0 || v.CompareTo(ExactDecimal.Parse("1")) > 0))
            throw Error("volume_range", "Forecast capacity factors must remain in [0,1].");
    }
    internal static ScheduleGraphValue Difference(ScheduleGraphValue original, ScheduleGraphValue replacement, ScheduleGraphValue current)
    {
        var minus = ExactDecimal.Parse("-1");
        if(original is ScheduleGraphValue.AnnualRate && replacement is ScheduleGraphValue.AnnualRate rate && current is ScheduleGraphValue.AnnualRate currentRate)
        {
            if(rate.Value.CompareTo(ExactDecimal.Parse("-0.1"))<0 || rate.Value.CompareTo(ExactDecimal.Parse("1"))>0)
                throw Error("rate_domain","The supported annual decimal rate range is -0.1 to 1.");
            return new ScheduleGraphValue.AnnualRate(rate.Value-currentRate.Value);
        }
        if (original is ScheduleGraphValue.Quantity o && replacement is ScheduleGraphValue.Quantity r && current is ScheduleGraphValue.Quantity c && r.Value is not null && r.Value.Specification == o.Value.Specification)
            return new ScheduleGraphValue.Quantity(r.Value.Add(c.Value.Scale(minus)));
        if (original is ScheduleGraphValue.Quantities os && replacement is ScheduleGraphValue.Quantities rs && current is ScheduleGraphValue.Quantities cs &&
            !rs.Values.IsDefault && rs.Values.Length == os.Values.Length && rs.Values.Select((v, n) => v is not null && v.Specification == os.Values[n].Specification).All(v => v))
            return new ScheduleGraphValue.Quantities([..rs.Values.Zip(cs.Values, (a, b) => a.Add(b.Scale(minus)))]);
        if (original is ScheduleGraphValue.CapacityFactors of && replacement is ScheduleGraphValue.CapacityFactors rf && current is ScheduleGraphValue.CapacityFactors cf && !rf.Values.IsDefault && rf.Values.Length == of.Values.Length)
        { RequireFactors(rf.Values); return new ScheduleGraphValue.CapacityFactors([..rf.Values.Zip(cf.Values, (a, b) => a - b)]); }
        throw Error("quantity_mismatch", "An input replacement must retain its exact quantity type, specification and grid.");
    }
}
