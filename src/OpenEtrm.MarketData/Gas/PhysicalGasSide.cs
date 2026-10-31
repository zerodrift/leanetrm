using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

[EconomicDefinition("gas_valuation_assignment_v1")]
public sealed record GasValuationAssignment
{
    [IdentityField] public ReferenceKey Key { get; }
    [IdentityField] public GasDeliveryLocation Location { get; }
    public ReferenceKey Point => Location.Point;
    [IdentityField] public ReferenceKey Product { get; }
    [IdentityField] public DateOnly From { get; }
    [IdentityField] public DateOnly To { get; }
    [IdentityField] public PriceSettlementSelection Settlement { get; }
    [IdentityField] public QuoteSpecification Quote { get; }
    [IdentityField] public SwapProjection Projection { get; }
    [IdentityField] public IndexFixing? Fixing { get; }
    [IdentityField] public UnitPrice Adjustment { get; }
    public IndexPriceLookup Lookup => new(Settlement, Quote, Projection.Market, Projection.Contract, null, Fixing);

    public GasValuationAssignment(ReferenceKey key, GasDeliveryLocation location, ReferenceKey product, DateOnly from, DateOnly to,
        PriceSettlementSelection settlement, QuoteSpecification quote, SwapProjection projection, IndexFixing? fixing, UnitPrice adjustment)
    {
        if (location is null) throw new MarketDataException("gas_delivery_location", "Select a complete gas delivery location.");
        foreach (var reference in new[] { key, location.Pipeline, location.Zone, location.Point, product }) Money.RequireKey(reference);
        if (settlement is null || quote is null || projection is null || projection.Contract is null || adjustment is null ||
            from >= to || location.Point.Revision != key.Revision || product.Revision != key.Revision || quote.Key.Revision != key.Revision ||
            quote.Quantity.Revision != key.Revision || quote.Currency.Revision != key.Revision ||
            settlement.SelectedIndex.Revision != key.Revision || projection.Market.Curve.Revision != projection.Contract.Key.Revision ||
            projection.Market.Contract != projection.Contract.Key || projection.Contract.Kind != ContractKind.Forward ||
            projection.Contract.DeliveryFrom != from || projection.Contract.DeliveryTo != to || projection.AveragingQuantity is not null ||
            adjustment.Unit != PriceUnit.FromQuote(quote))
            throw new MarketDataException("gas_valuation_assignment", "Supply one exact point/product assignment, forward period, qualified quote and compatible market adjustment.");
        Key = key; Location = location; Product = product; From = from; To = to; Settlement = settlement;
        Quote = quote; Projection = projection; Fixing = fixing; Adjustment = adjustment;
    }
}

[EconomicDefinition("physical_gas_side_v1")]
public sealed record PhysicalGasSide : Side
{
    [IdentityField] public GasDeliveryLocation Location { get; }
    public ReferenceKey DeliveryPoint => Location.Point;
    [IdentityField] public ReferenceKey Product { get; }
    [IdentityField] public GasQuantityTerms Quantities { get; }
    public ImmutableArray<GasValuationAssignment> Assignments { get; }
    public ReferenceKey Currency => Assignments[0].Quote.Currency;
    public Quantity UnitQuantity => new(ExactDecimal.Parse("1"), Quantities.Receipt.Specification);
    public override DefinitionIdentity PricingBindingIdentity => DefinitionIdentity.For(new GasValuationBindings(Assignments));
    private readonly Lazy<PhysicalGasSchedule> schedule;
    public override ImmutableArray<CurveRequest> Dependencies => [..Assignments.Select(a => a.Projection.Market).Distinct()];
    public override ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot) =>
        [..Assignments.Select(a => PeriodPriceSelector.ProjectionAt(a.Lookup, snapshot)).OfType<CurveRequest>().Distinct()];

    public PhysicalGasSide(Guid id, GasDeliveryLocation location, ReferenceKey product, GasQuantityTerms quantities,
        ImmutableArray<GasValuationAssignment> assignments) : base(id)
    {
        if (location is null) throw new MarketDataException("gas_delivery_location", "Select a complete gas delivery location.");
        var deliveryPoint = location.Point; Money.RequireKey(product);
        if (quantities is null || quantities.Receipt.Amount != ExactDecimal.Parse("1") ||
            quantities.Basis is not (GasQuantityBasis.PerOperatingDay or GasQuantityBasis.PeriodTotalEqualOperatingDays) ||
            quantities.From < new DateOnly(2000, 1, 1) || quantities.To > new DateOnly(2100, 1, 1) ||
            product.Revision != deliveryPoint.Revision || quantities.Clock.Key.Revision != deliveryPoint.Revision)
            throw new MarketDataException("gas_quantity_basis", "Select a normalized one-unit gas daily or period-total profile in the supported retained horizon and one reference revision.");
        if (assignments.IsDefaultOrEmpty || assignments.Any(a => a is null || a.Location != location || a.Product != product ||
            a.Quote.Quantity != quantities.Receipt.Specification || a.Quote.Currency != assignments[0].Quote.Currency) ||
            assignments.Select(a => a.Key).Distinct().Count() != assignments.Length)
            throw new MarketDataException("gas_valuation_assignment", "Supply distinct compatible market assignments for this exact gas point, product and quantity.");
        var used = new HashSet<ReferenceKey>();
        for (var day = quantities.From; day < quantities.To; day = day.AddDays(1))
        {
            var candidates = assignments.Where(a => a.From <= day && a.To >= day.AddDays(1)).ToArray();
            if (candidates.Length != 1)
                throw new MarketDataException("gas_valuation_assignment", "Every gas day requires exactly one complete market assignment without gaps or overlaps.");
            used.Add(candidates[0].Key);
        }
        if (used.Count != assignments.Length)
            throw new MarketDataException("gas_valuation_assignment", "Every supplied assignment must value part of this delivery term.");
        Location = location; Product = product; Quantities = quantities;
        Assignments = [..assignments.OrderBy(a => a.From)];
        schedule = new(() => new(this));
    }

    public override PhysicalGasSchedule GenerateSchedule() => schedule.Value;
    public override PhysicalGasSideValuation PV(SideValuationContext context) => GenerateSchedule().Evaluate(context);
}

[EconomicDefinition("gas_valuation_bindings_v1")]
internal sealed record GasValuationBindings([property: IdentityField] ImmutableArray<GasValuationAssignment> Assignments);

[EconomicDefinition("gas_instrument_pricing_bindings_v1")]
internal sealed record GasInstrumentPricingBindings([property: IdentityField] ImmutableArray<string?> Sides);

public sealed record PhysicalGasSchedule : SideSchedule
{
    public PhysicalGasSide Side { get; }
    public GasQuantitySchedule Quantities { get; }
    public override ImmutableArray<CurveRequest> Dependencies => Side.Dependencies;
    public override ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot) => Side.DependenciesAt(snapshot);
    public override IReadOnlyList<ScheduleRow> Rows { get; }
    private readonly Lazy<BookedSideProgram> program;
    public BookedSideProgram Program => program.Value;

    internal PhysicalGasSchedule(PhysicalGasSide side) : base(side.Id, side.Currency, side.UnitQuantity, "gas_future_delivery_schedule_v1")
    {
        Side = side; Quantities = GasQuantitySchedule.Generate(side.Quantities);
        Rows = Quantities.Rows.Select((quantity, ordinal) =>
        {
            var assignment = side.Assignments.Single(a => a.From <= quantity.OperatingDay && a.To > quantity.OperatingDay);
            var next = quantity.OperatingDay.AddDays(1); var one = ExactDecimal.Parse("1");
            var row = new ScheduleRow(side.Id, quantity.OperatingDay, next, quantity.End, side.Currency, side.UnitQuantity,
                null, null, null, [new(one, null, assignment.Lookup, assignment.Adjustment)], Implementation,
                [new(quantity.OperatingDay, next, 0, one, Implementation)], Implementation);
            return new ScheduleRow(row, side.Id, ordinal, quantity.Delivery).WithPhysicalInterval(
                new(quantity.Start, quantity.End, quantity.OperatingDay, side.Quantities.Clock.StartMinute / 60, side.Quantities.Clock.StartMinute % 60, 1));
        }).ToImmutableArray();
        program = new(() => BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side)));
    }

    public override PhysicalGasSideValuation Evaluate(SideValuationContext context)
    {
        if (context?.GasExecution is not {} execution)
            throw new MarketDataException("side_market_sources", "Gas valuation requires explicit certified base/scenario inputs and qualified market assignments.");
        var value = execution.Sources.Evaluate(Program, SideId, execution.Scenario, context.Discounting);
        return new(this, context, value);
    }
}

public sealed record PhysicalGasSideValuation : SideValuation
{
    public BookedSideEvaluation Execution { get; }
    internal PhysicalGasSideValuation(PhysicalGasSchedule schedule, SideValuationContext context, BookedSideEvaluation execution)
        : base(schedule, context, execution.PresentValue) { Execution = execution; }
}

// First admitted profile: fixed contractual volumes and linear consideration /
// usage cash in the same native quantity. Operational quantity links come later.
[EconomicDefinition("physical_gas_instrument_v1")]
public sealed record PhysicalGasInstrument : SidedInstrument
{
    public PhysicalGasSide Delivery => (PhysicalGasSide)Sides[0].Side;
    public override ReferenceKey Currency => Delivery.Currency;
    public override Quantity UnitQuantity => Delivery.UnitQuantity;
    public override DefinitionIdentity PricingBindingIdentity => DefinitionIdentity.For(
        new GasInstrumentPricingBindings([..Sides.Select(m => m.Side.PricingBindingIdentity?.Canonical)]));
    internal override bool SupportsSettlement => false;
    public PhysicalGasInstrument(Guid id, PhysicalGasSide delivery, Side consideration, ImmutableArray<Side> usageFees,
        ExactDecimal direction, ExactDecimal? feeDirection) : base(id, Compose(delivery, consideration, usageFees, direction, feeDirection)) { }

    private static ImmutableArray<SideMembership> Compose(PhysicalGasSide delivery, Side consideration,
        ImmutableArray<Side> fees, ExactDecimal direction, ExactDecimal? feeDirection)
    {
        var one = ExactDecimal.Parse("1");
        if (delivery is null || consideration is null || fees.IsDefault || fees.Any(f => f is null) ||
            direction != one && direction != ExactDecimal.Parse("-1") ||
            (fees.Length == 0 ? feeDirection is not null : feeDirection is null || feeDirection != one && feeDirection != ExactDecimal.Parse("-1"))) throw Invalid();
        var expected = delivery.GenerateSchedule().Quantities.TotalDelivery;
        foreach (var side in new[] { consideration }.Concat(fees))
        {
            if (side is not (FixedSide or FloatingSide) || side.GenerateSchedule() is not SidePaymentSchedule cash ||
                cash.Currency != delivery.Currency || cash.UnitQuantity != delivery.UnitQuantity || cash.Rows.Count != 1) throw Invalid();
            var row = cash.Rows[0];
            if (row.DeliveryFrom != delivery.Quantities.From || row.DeliveryTo != delivery.Quantities.To ||
                row.AllocatedQuantity != expected) throw Invalid();
        }
        return [new(delivery, direction), new(consideration, direction * ExactDecimal.Parse("-1")), ..fees.Select(f => new SideMembership(f, feeDirection!.Value))];
    }
    private static MarketDataException Invalid() => new("gas_instrument", "Select one gas delivery, matching whole-term linear cash, explicit receive/deliver direction and an independent pay/receive direction when fees exist.");
}
