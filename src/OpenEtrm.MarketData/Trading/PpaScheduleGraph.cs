using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.Trading;

internal sealed record PpaGraphGroup(SidePeriodKey Key, long Rows, ScheduleQuantityNode Quantity,
    ScheduleCashNode Amount, ScheduleCashNode? PresentValue, bool Modeled);
internal sealed record PpaGraphComponent(ScheduleQuantityColumn Quantity, SchedulePriceColumn Price,
    SchedulePriceColumn? RawPrice, CurveKey? Curve, ExactDecimal Multiplier, UnitPrice? Adder,
    ImmutableArray<RowPriceState> States, ImmutableArray<IntervalPriceResolution?> Resolutions);
internal sealed record PpaGraphRowState(ComponentKind Kind, EconomicClass Class, PaymentLifecycle? Lifecycle,
    ValueCoverage Coverage, bool Modeled, string? UnavailableReason);
internal sealed record PpaGraphBlock(ImmutableArray<PreparedRowInputs> Rows, ScheduleQuantityColumn Quantity,
    ImmutableArray<PpaGraphComponent> Components, ScheduleCashColumn Amount, ScheduleCashColumn? PresentValue,
    SidePaymentFactor? Factor,ScheduleDiscountNode? Discount, ImmutableArray<PpaGraphRowState> States);

internal sealed class PpaGraphSide(Side side, SideSlot slot, ImmutableArray<PpaGraphGroup> groups, ImmutableArray<PpaGraphBlock> blocks)
{
    internal ImmutableArray<PpaGraphGroup> Groups => groups;
    internal long Rows => blocks.Sum(b => (long)b.Rows.Length);
    internal ImmutableArray<ScheduleRowValue> MaterializeRows(ScheduleGraphSession session, ImmutableHashSet<DateOnly> detailMonths,
        CancellationToken cancellation = default)
    {
        var result = ImmutableArray.CreateBuilder<ScheduleRowValue>();
        foreach (var block in blocks)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!detailMonths.Contains(PpaQuantityGraphCompiler.Month(block.Rows[0].Quantity.Interval))) continue;
            for (var n = 0; n < block.Rows.Length; n++)
            {
                cancellation.ThrowIfCancellationRequested(); var prepared = block.Rows[n]; var q = prepared.Quantity; var state = block.States[n];
                var components = block.Components.Select(component =>
                {
                    var resolution = component.Resolutions[n];
                    if (resolution is not null && component.RawPrice is not null)
                    {
                        var raw = session.Price(component.RawPrice.Cell(n), cancellation);
                        resolution = resolution.State == IntervalPriceState.Unresolved ? resolution with { Estimate = raw } : resolution with { Price = raw };
                    }
                    return new PriceComponent(session.Quantity(component.Quantity.Cell(n), cancellation), session.Price(component.Price.Cell(n), cancellation),
                        component.States[n], component.Curve, resolution, component.Multiplier, component.Adder);
                }).ToImmutableArray();
                var factor=block.Factor is {} original&&block.Discount is {} node?original with{DiscountFactor=session.DiscountFactor(node,cancellation)}:block.Factor;
                result.Add(new(side.Id, slot, state.Kind, q.Interval, session.Quantity(block.Quantity.Cell(n), cancellation), q.State, q.Stage,
                    q.Lineage, q.CapacityVersions, components, session.Cash(block.Amount.Cell(n), cancellation), PpaErrors.One,
                    side is FixedSide or FloatingSide ? PaymentRule.Implementation : "monthly_physical_value_date_v1", prepared.ValueAt, factor,
                    block.PresentValue is null ? null : session.Cash(block.PresentValue.Cell(n), cancellation), state.Class, state.Lifecycle, state.Coverage, state.UnavailableReason)
                { Row = prepared.Schedule, CapacityDays = q.CapacityDays });
            }
        }
        return [..result.OrderBy(row => row.Row.Ordinal)];
    }
}

internal sealed class PpaScheduleGraph
{
    internal ScheduleGraphPlan Plan { get; }
    internal ImmutableDictionary<Guid, PpaGraphSide> Sides { get; }
    private readonly CurveBuild authority;
    private readonly ScheduleGraphSession basis;
    private PpaScheduleGraph(ScheduleGraphPlan plan, ImmutableDictionary<Guid, PpaGraphSide> sides, CurveBuild authority, CancellationToken cancellation)
    { Plan = plan; Sides = sides; this.authority = authority; basis = plan.Base(cancellation); }
    internal ScheduleGraphSession SessionFor(CurveBuild scenario, ScheduleGraphSession? parent = null, CancellationToken cancellation = default)
    {
        if (scenario is null || !scenario.Snapshot.HasSameInputs(authority.Snapshot) || !ReferenceEquals(scenario.Model, authority.Model) ||
            !ReferenceEquals(scenario.PricingBindings, authority.PricingBindings) && !scenario.PricingBindings.OrderBy(p => p.Key.Curve.Code).ThenBy(p => p.Key.Contract.Id)
                .SequenceEqual(authority.PricingBindings.OrderBy(p => p.Key.Curve.Code).ThenBy(p => p.Key.Contract.Id)))
            throw PpaErrors.Error("ppa_context", "scenario", "Schedule reuse requires the same admitted authority and exact connected price bindings.");
        if (parent is not null && !ReferenceEquals(parent.Plan, Plan)) throw PpaErrors.Error("ppa_context", "scenario", "A reused schedule session belongs to this exact plan.");
        return (parent ?? basis).WithMarketScenario(scenario.Graph, cancellation);
    }
    internal static PpaScheduleGraph Compile(IntervalValuationInputs inputs, PpaInstrument instrument, DeliveryCoverage coverage,
        CurveBuild scenario, ImmutableArray<SidePaymentFactor> factors, IntervalPriceResolver admission, CancellationToken cancellation = default)
    {
        inputs.ValidateCoverage(instrument, coverage); cancellation.ThrowIfCancellationRequested();
        if (scenario is null || !scenario.Snapshot.HasSameInputs(inputs.Snapshot) || !ReferenceEquals(scenario.Model, inputs.BaseMarket.Model) || factors.IsDefault)
            throw PpaErrors.Error("ppa_context", "graph", "Supply the exact admitted price authority, prepared schedule and resolved factor list.");
        if (admission is null) throw PpaErrors.Error("ppa_context", "price_admission", "Supply the exact session price admission resolver.");
        admission.RequireContext(scenario, inputs.Read.Curves);
        var sourceContext = scenario.Connected.Scenario.Plan.Context;
        if(sourceContext.ContextId!=inputs.KnowledgeContext||sourceContext.KnownAt!=inputs.Snapshot.Knowledge!.KnownAt)
            throw PpaErrors.Error("ppa_context","knowledge","The price graph and quantity inputs must share the admitted resolved context.");
        var graph = new ScheduleGraphBuilder(scenario.Connected, new(2_000_000, 8_000_000, 35_136, 32_000_000), sourceContext);
        var quantities = new PpaQuantityGraphCompiler(inputs, instrument, coverage, graph, cancellation);
        var prices = new PpaPriceGraphCompiler(inputs, scenario, graph, admission, cancellation);
        var factorsByKey = factors.ToImmutableDictionary(f => f.Payment);
        var sides = ImmutableDictionary.CreateBuilder<Guid, PpaGraphSide>();
        foreach (var member in instrument.Memberships)
        {
            cancellation.ThrowIfCancellationRequested(); var side = member.Side;
            var groups = ImmutableArray.CreateBuilder<PpaGraphGroup>(); var blocks = ImmutableArray.CreateBuilder<PpaGraphBlock>();
            foreach (var monthRows in inputs.Rows(instrument, side, coverage).GroupBy(r => PpaQuantityGraphCompiler.Month(r.Quantity.Interval)))
            {
                var month = monthRows.Key; var quantity = quantities.Compile(side, month);
                // Partition only at immutable structural boundaries. Quantity and
                // price states are summary selections within a block, never
                // scenario branches or per-row shock adjustments.
                foreach (var partition in monthRows.GroupBy(row => (row.Schedule.Key,
                    Frozen: !IntervalValuationInputs.InMeasure(side, row, scenario.Snapshot.ValuationAt),
                    Holding: side is CertificateDeliverySide && row.Quantity.Interval.End <= scenario.Snapshot.ValuationAt,
                    Bindings: new RowBindings(row))))
                {
                    cancellation.ThrowIfCancellationRequested(); var rows = partition.ToImmutableArray();
                    var grid = PpaQuantityGraphCompiler.Grid(rows.Select(r => r.Quantity.Interval));
                    var native = Select(graph, quantity.Total, grid);
                    var components = ImmutableArray.CreateBuilder<PpaGraphComponent>();
                    if (side is FixedSide fixedSide)
                    {
                        var fixedPrices = graph.FrozenPrices(new ScheduleSourceKey.Contract(side.RetainedPlan?.SourceIdentity(RetainedScheduleSourceRole.FixedPrice)??DefinitionIdentity.For(fixedSide.Price), rows[0].Schedule.Ordinal), grid,
                            [..rows.Select(r => side.RetainedPlan is {} retained?retained.NativeRows[r.Schedule.Ordinal].FixedPrice
                                ??throw RetainedSideSchedules.Error("A retained fixed obligation requires its exact contractual price expression."):
                                fixedSide.Price.At(r.Quantity.Interval.OperatingDate))]);
                        components.Add(new(native, fixedPrices, null, null, PpaErrors.One, null,
                            [..rows.Select(_ => RowPriceState.Fixed)], [..rows.Select(_ => (IntervalPriceResolution?)null)]));
                    }
                    else
                    {
                        for (var component = 0; component < rows[0].Prices.Length; component++)
                        {
                            var binding = rows[0].Prices[component];
                            var resolved = prices.Compile(binding.Curve, month, partition.Key.Frozen,binding.Settlement);
                            var curve = inputs.Read.Curves[binding.Curve]; var period = curve.Curve is PeriodContractCurve;
                            var selected = rows.Select(row => ResolutionAt(resolved, row.Quantity.Interval, period)).ToArray();
                            var references = selected.Select(n => resolved.Values.Cell(n)).ToImmutableArray();
                            var raw = period ? graph.ObservePeriods(grid, references, [..rows.Select(r => r.Quantity.Interval.OperatingDate)]) : graph.Gather(grid, references);
                            var priced = graph.Affine(raw, binding.Multiplier, binding.Adder);
                            var componentQuantity = native;
                            if (side is not (FixedSide or FloatingSide) && binding.Asset is {} asset)
                            {
                                if (rows.Any(r => r.Prices[component].Asset != asset)) throw PpaErrors.Error("ppa_quantity_source", "component", "A component block must retain one exact asset link.");
                                componentQuantity = Select(graph, quantity.Assets[asset], grid);
                            }
                            var resolutions = selected.Select(n => (IntervalPriceResolution?)resolved.Resolutions[n]).ToImmutableArray();
                            var states = rows.Select((row, n) => State(inputs, side, row, resolved.Resolutions[selected[n]], period)).ToImmutableArray();
                            components.Add(new(componentQuantity, priced, raw, binding.Curve, binding.Multiplier, binding.Adder, states, resolutions));
                        }
                    }
                    var boundComponents = components.ToImmutable();
                    var amount = graph.Add([..boundComponents.Select(c => graph.Amount(c.Price, c.Quantity))]);
                    graph.CountRowAmounts(amount);
                    SidePaymentFactor? factor = null; ScheduleCashColumn? pv = null;ScheduleDiscountNode? discount=null;
                    if (partition.Key.Holding) pv = amount;
                    else if (rows[0].ValueAt > scenario.Snapshot.ValuationAt)
                    {
                        if (!factorsByKey.TryGetValue(rows[0].Schedule.Key, out factor)) throw PpaErrors.Error("side_payment_factors", "discounting", "A future row requires its exact resolved discount input.");
                        discount=graph.DiscountInput(factor);pv = graph.Discount(amount, discount);
                    }
                    var statesForRows = rows.Select((row, n) => Classify(side, member.Slot, row, boundComponents.Select(c => c.States[n]), scenario.Snapshot.ValuationAt)).ToImmutableArray();
                    foreach (var selected in statesForRows.Select((state, n) => (state, n)).GroupBy(p =>
                        (Key: new SidePeriodKey(side.Id, member.Slot, month, p.state.Kind, rows[p.n].Quantity.State, p.state.Class, p.state.Lifecycle, p.state.Coverage, rows[p.n].ValueAt), p.state.Modeled)))
                    {
                        var indices = selected.Select(p => p.n).ToArray(); var selection = grid.Select(indices);
                        groups.Add(new(selected.Key.Key, indices.Length, graph.Sum(native, selection), graph.Sum(amount, selection), pv is null ? null : graph.Sum(pv, selection), selected.Key.Modeled));
                    }
                    blocks.Add(new(rows, native, boundComponents, amount, pv, factor,discount, statesForRows));
                }
            }
            sides.Add(side.Id, new(side, member.Slot, groups.ToImmutable(), blocks.ToImmutable()));
        }
        return new(graph.Compile(cancellation), sides.ToImmutable(), scenario, cancellation);
    }
    private static ScheduleQuantityColumn Select(ScheduleGraphBuilder graph, ScheduleQuantityColumn quantity, UtcGrid grid) => quantity.Grid.Equals(grid) ? quantity :
        graph.Gather(grid, [..Enumerable.Range(0, grid.Count).Select(n => quantity.Cell(PpaPriceGraphCompiler.Find(quantity.Grid, grid.Start(n), grid.End(n))))]);
    // This is an in-memory partition key, not an economic identity. Exact
    // equality resolves hash collisions and excludes already-resolved quantities.
    private readonly struct RowBindings(PreparedRowInputs row) : IEquatable<RowBindings>
    {
        private readonly ImmutableArray<PreparedPriceBinding> prices = row.Prices;
        private readonly ImmutableArray<QuantityComponent> components = row.Quantity.Components;
        public bool Equals(RowBindings other)
        {
            if (prices.Length != other.prices.Length || components.Length != other.components.Length) return false;
            for (var n = 0; n < prices.Length; n++)
                if (prices[n].Curve != other.prices[n].Curve || prices[n].Multiplier != other.prices[n].Multiplier || prices[n].Adder != other.prices[n].Adder || prices[n].Settlement != other.prices[n].Settlement||prices[n].Asset!=other.prices[n].Asset) return false;
            for (var n = 0; n < components.Length; n++)
                if (components[n].Location != other.components[n].Location || components[n].Asset != other.components[n].Asset) return false;
            return true;
        }
        public override bool Equals(object? other) => other is RowBindings value && Equals(value);
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var price in prices) { hash.Add(price.Curve); hash.Add(price.Multiplier); hash.Add(price.Adder); hash.Add(price.Settlement);hash.Add(price.Asset); }
            foreach (var component in components) { hash.Add(component.Location); hash.Add(component.Asset); }
            return hash.ToHashCode();
        }
    }
    private static int ResolutionAt(PpaGraphPrices prices, DeliveryInterval interval, bool period)
    {
        if (!period) return PpaPriceGraphCompiler.Find(prices.Values.Grid, interval.Start, interval.End);
        for (var n = 0; n < prices.Resolutions.Length; n++)
        {
            var value = prices.Resolutions[n].Interval;
            if (DateOnly.FromDateTime(value.Start.UtcDateTime) <= interval.OperatingDate && DateOnly.FromDateTime(value.End.UtcDateTime) > interval.OperatingDate) return n;
        }
        throw PpaErrors.Error("ppa_cadence", "vintage", "The row requires its exact generation-vintage price.");
    }
    private static RowPriceState State(IntervalValuationInputs inputs, Side side, PreparedRowInputs row, IntervalPriceResolution resolved, bool period)
    {
        if (resolved.State == IntervalPriceState.Unresolved)
        {
            if (inputs.Policy != PendingActualsPolicy.EstimateUntilActual) throw new MarketDataException("actuals_required", "Delivered prices require eligible actuals under the selected policy.");
            _ = PpaPriceGraphCompiler.Required(resolved); return RowPriceState.Estimated;
        }
        var state = resolved.State switch { IntervalPriceState.Actual => RowPriceState.Actual, IntervalPriceState.PublishedFixing => RowPriceState.PublishedFixing,
            IntervalPriceState.Modeled => RowPriceState.Modeled, _ => RowPriceState.Projected };
        if (period && side is not CertificateDeliverySide && row.Quantity.Interval.End <= inputs.Snapshot.ValuationAt && state == RowPriceState.Projected)
        {
            if (inputs.Policy != PendingActualsPolicy.EstimateUntilActual) throw new MarketDataException("actuals_required", "The delivered period requires its eligible fixing.");
            state = RowPriceState.Estimated;
        }
        return state;
    }
    private static PpaGraphRowState Classify(Side side, SideSlot slot, PreparedRowInputs row, IEnumerable<RowPriceState> prices, DateTimeOffset valuation)
    {
        var selected = prices.ToArray(); var delivered = row.Quantity.Interval.End <= valuation;
        var holding = side is CertificateDeliverySide && delivered; var payment = side is FixedSide or FloatingSide;
        var knownQuantity = row.Quantity.State is VolumeState.Metered or VolumeState.Contractual;
        var known = knownQuantity && selected.All(p => p is RowPriceState.Fixed or RowPriceState.Actual or RowPriceState.PublishedFixing);
        var classification = holding && knownQuantity ? EconomicClass.Held : !delivered ? EconomicClass.Unrealised : known ? EconomicClass.Realised : EconomicClass.Estimated;
        ValueCoverage coverage; string? unavailable = null;
        if (holding) coverage = ValueCoverage.HoldingValue;
        else if (row.ValueAt > valuation) coverage = ValueCoverage.Discounted;
        else if (payment)
        {
            if (selected.Any(p => p is RowPriceState.Projected or RowPriceState.Modeled)) throw PpaErrors.Error("ppa_due_leg_unresolved", slot.ToString(), "A due payment cannot consume an unsupported projected or modeled amount.");
            coverage = known ? ValueCoverage.OverdueBalance : ValueCoverage.OverdueEstimate; unavailable = "due_cash_policy_required";
        }
        else coverage = ValueCoverage.DeliveredValue;
        return new(holding ? ComponentKind.Holding : payment ? ComponentKind.Payment : ComponentKind.Delivery, classification,
            payment ? delivered ? PaymentLifecycle.DeliveredUnsettled : PaymentLifecycle.RemainingProjected : null, coverage,
            selected.Any(p => p == RowPriceState.Modeled), unavailable);
    }
}
