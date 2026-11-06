using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Lng;

[EconomicDefinition("lng_valuation_assignment_v1")]
public sealed record LngValuationAssignment
{
    [IdentityField] public ReferenceKey Key { get; }
    [IdentityField] public ReferenceKey Product { get; }
    [IdentityField] public QuantitySpecification Energy { get; }
    [IdentityField] public ReferenceKey DischargeFacility { get; }
    [IdentityField] public ReferenceKey DischargeCustody { get; }
    [IdentityField] public ReferenceKey Clock { get; }
    [IdentityField] public PriceSettlementSelection Settlement { get; }
    [IdentityField] public QuoteSpecification Quote { get; }
    [IdentityField] public SwapProjection Projection { get; }
    [IdentityField] public IndexFixing Fixing { get; }
    public IndexPriceLookup Lookup => new(Settlement, Quote, Projection.Market, Projection.Contract, null, Fixing);

    public LngValuationAssignment(ReferenceKey key, ReferenceKey product, QuantitySpecification energy,
        ReferenceKey dischargeFacility, ReferenceKey dischargeCustody, ReferenceKey clock, IndexPriceLookup lookup)
    {
        foreach (var reference in new[] { key, product, dischargeFacility, dischargeCustody, clock }) Money.RequireKey(reference);
        if (energy is null || lookup?.Fixing is not {} fixing || lookup.Quote is null || lookup.Contract is null || lookup.Settlement is null)
            throw Invalid();
        if (energy.Key.Revision != key.Revision ||
            new[] { product, dischargeFacility, dischargeCustody, clock }.Any(r => r.Revision != key.Revision) ||
            dischargeFacility == dischargeCustody || !LngQuantityRules.Matches(energy, "gross_energy", "energy") ||
            lookup.Quote.Quantity != energy.Key || lookup.Quote.Kind != QuoteKind.Outright ||
            lookup.Quote.Key.Revision != key.Revision || lookup.CustomProjection is not null || lookup.AveragingQuantity is not null ||
            lookup.Contract.Kind != ContractKind.Forward || lookup.Contract.Interval is not null ||
            lookup.Contract.DeliveryFrom < new DateOnly(2000, 1, 1) || lookup.Contract.DeliveryFrom >= new DateOnly(2100, 1, 1) ||
            lookup.Contract.DeliveryFrom.Day != 1 || lookup.Contract.DeliveryTo != lookup.Contract.DeliveryFrom.AddMonths(1) ||
            lookup.Market.Contract != lookup.Contract.Key || lookup.Market.Curve.Revision != lookup.Contract.Key.Revision ||
            fixing.Index != lookup.Settlement.SelectedIndex || fixing.Quote != lookup.Quote.Key ||
            fixing.From != lookup.Contract.DeliveryFrom || fixing.To != lookup.Contract.DeliveryTo ||
            fixing.Series.Revision != lookup.Contract.Key.Revision || fixing.Source.Revision != key.Revision ||
            fixing.Implementation != IndexFixing.SupportedImplementation || !TradingInstant.IsSupported(fixing.FixingAt))
            throw Invalid();
        Key = key; Product = product; Energy = energy; DischargeFacility = dischargeFacility;
        DischargeCustody = dischargeCustody; Clock = clock; Settlement = lookup.Settlement;
        Quote = lookup.Quote; Projection = new(lookup.Market, lookup.Contract); Fixing = fixing;
    }
    private static MarketDataException Invalid() => new("lng_valuation_assignment",
        "Select an exact LNG product/discharge custody mapping to one outright monthly energy mark and its explicit fixing.");
}

[EconomicDefinition("physical_lng_cargo_side_v1")]
public sealed record PhysicalLngCargoSide : Side
{
    [IdentityField] public LngValuationAssignment Assignment { get; }
    [IdentityField] public ReferenceKey LoadFacility { get; }
    [IdentityField] public ReferenceKey LoadCustody { get; }
    [IdentityField] public DateTimeOffset LoadFrom { get; }
    [IdentityField] public DateTimeOffset LoadTo { get; }
    [IdentityField] public DateTimeOffset DischargeFrom { get; }
    [IdentityField] public DateTimeOffset DischargeTo { get; }
    public DateOnly DateFrom => DateOnly.FromDateTime(LoadFrom.UtcDateTime);
    public DateOnly DateTo => DateOnly.FromDateTime(DischargeTo.UtcDateTime).AddDays(DischargeTo.TimeOfDay == TimeSpan.Zero ? 0 : 1);
    public Quantity UnitQuantity => new(ExactDecimal.Parse("1"), Assignment.Energy.Key);
    public ReferenceKey Currency => Assignment.Lookup.Quote.Currency;
    public override DefinitionIdentity PricingBindingIdentity => DefinitionIdentity.For(Assignment);
    public override ImmutableArray<CurveRequest> Dependencies => [Assignment.Lookup.Market];
    public override ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot) =>
        PeriodPriceSelector.ProjectionAt(Assignment.Lookup, snapshot) is {} market ? [market] : [];
    private readonly Lazy<PhysicalLngCargoSchedule> schedule;

    public PhysicalLngCargoSide(Guid id, LngValuationAssignment assignment, ReferenceKey loadFacility, ReferenceKey loadCustody,
        DateTimeOffset loadFrom, DateTimeOffset loadTo, DateTimeOffset dischargeFrom, DateTimeOffset dischargeTo) : base(id)
    {
        if (assignment is null) throw new MarketDataException("lng_valuation_assignment", "Select an exact LNG discharge valuation mapping.");
        Money.RequireKey(loadFacility); Money.RequireKey(loadCustody);
        if (loadFacility.Revision != assignment.Key.Revision || loadCustody.Revision != assignment.Key.Revision || loadFacility == loadCustody)
            throw new MarketDataException("lng_load_location", "Select separate exact loading facility and custody-point references.");
        var dates = new[] { loadFrom, loadTo, dischargeFrom, dischargeTo };
        var first = new DateTimeOffset(assignment.Lookup.Contract.DeliveryFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var last = new DateTimeOffset(assignment.Lookup.Contract.DeliveryTo.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        if (dates.Any(d => !TradingInstant.IsSupported(d) || d.Year < 2000 || d > new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero)) ||
            loadFrom >= loadTo || loadTo > dischargeFrom || dischargeFrom >= dischargeTo || dischargeFrom < first || dischargeTo > last)
            throw new MarketDataException("lng_delivery_window", "Supply distinct supported UTC loading and discharge windows, in order, with discharge covered by one monthly mark.");
        Assignment = assignment; LoadFacility = loadFacility; LoadCustody = loadCustody;
        LoadFrom = loadFrom; LoadTo = loadTo; DischargeFrom = dischargeFrom; DischargeTo = dischargeTo;
        schedule = new(() => new(this));
    }
    public override PhysicalLngCargoSchedule GenerateSchedule() => schedule.Value;
    public override SideValuation PV(SideValuationContext context) => GenerateSchedule().Evaluate(context);
}

public sealed record PhysicalLngCargoSchedule : SideSchedule
{
    public PhysicalLngCargoSide Side { get; }
    public override ImmutableArray<CurveRequest> Dependencies => Side.Dependencies;
    public override ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot) => Side.DependenciesAt(snapshot);
    public override IReadOnlyList<ScheduleRow> Rows { get; }
    private readonly Lazy<BookedSideProgram> program;
    public BookedSideProgram Program => program.Value;
    internal PhysicalLngCargoSchedule(PhysicalLngCargoSide side) : base(side.Id, side.Currency, side.UnitQuantity, "lng_future_cargo_schedule_v1")
    {
        Side = side;
        var one = ExactDecimal.Parse("1");
        var row = new ScheduleRow(side.Id, side.DateFrom, side.DateTo, side.DischargeTo, side.Currency, side.UnitQuantity,
            null, null, null, [new(one, null, side.Assignment.Lookup, null)], Implementation,
            [new(side.DateFrom, side.DateTo, 0, one, Implementation)], Implementation);
        Rows = [new ScheduleRow(row, side.Id, 0, new(1, 1, side.Assignment.Energy.Key)).WithPhysicalInterval(
            new(side.LoadFrom, side.DischargeTo, side.DateFrom, side.LoadFrom.Hour, side.LoadFrom.Minute, 1))];
        program = new(() => BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side)));
    }
    public override SideValuation Evaluate(SideValuationContext context)
    {
        if (context?.LngExecution is not {} execution)
            throw new MarketDataException("side_market_sources", "LNG cargo valuation requires exact certified LNG discharge mapping inputs.");
        return new PhysicalLngCargoValuation(this, context, execution.Sources.Evaluate(Program, SideId, execution.Scenario, context.Discounting));
    }
}

public sealed record PhysicalLngCargoValuation : SideValuation
{
    public BookedSideEvaluation Execution { get; }
    public override SideScheduleSnapshot InspectSchedule(string manifest,ExactDecimal scale,OpenEtrm.Shared.Numerics.SignedRatio coefficient,Func<DateOnly,DateTimeOffset> boundary)=>SideScheduleSnapshot.FromProgramValue(this,Execution,((PhysicalLngCargoSchedule)Schedule).Program,manifest,scale,coefficient,boundary);
    internal PhysicalLngCargoValuation(PhysicalLngCargoSchedule schedule, SideValuationContext context, BookedSideEvaluation execution)
        : base(schedule, context, execution.PresentValue) { Execution = execution; }
}

[EconomicDefinition("physical_lng_instrument_v1")]
public sealed record PhysicalLngInstrument : SidedInstrument
{
    public PhysicalLngCargoSide Cargo => (PhysicalLngCargoSide)Sides[0].Side;
    [IdentityField] public LngIncoterms Incoterms { get; }
    public override ReferenceKey Currency => Cargo.Currency;
    public override Quantity UnitQuantity => Cargo.UnitQuantity;
    public override DefinitionIdentity PricingBindingIdentity => Cargo.PricingBindingIdentity;
    internal override bool SupportsSettlement => false;
    public PhysicalLngInstrument(Guid id, PhysicalLngCargoSide cargo, FixedSide consideration, LngIncoterms incoterms,
        ExactDecimal physicalDirection, ExactDecimal cashDirection) : base(id, Compose(cargo, consideration, physicalDirection, cashDirection))
    {
        if (incoterms is null || incoterms.Edition.Revision != cargo.Assignment.Key.Revision)
            throw new MarketDataException("lng_incoterms", "Save the exact LNG trade's Incoterms edition, rule and named place explicitly.");
        Incoterms = incoterms;
    }
    public override void ValidateBinding(Quantity quantity, OpenEtrm.MarketData.Power.DeliveryCoverage? coverage)
    {
        base.ValidateBinding(quantity, coverage);
        if (quantity is null || quantity.Specification != UnitQuantity.Specification || quantity.Amount.CompareTo(default) <= 0)
            throw new MarketDataException("lng_quantity", "Bind one positive exact total discharge-energy quantity.");
    }
    private static ImmutableArray<SideMembership> Compose(PhysicalLngCargoSide cargo, FixedSide cash, ExactDecimal physicalDirection, ExactDecimal cashDirection)
    {
        static bool Sign(ExactDecimal value) => value == ExactDecimal.Parse("1") || value == ExactDecimal.Parse("-1");
        if (cargo is null || cash is null || !Sign(physicalDirection) || !Sign(cashDirection) || cash.Price is not FlatPrice ||
            cash.GenerateSchedule() is not SidePaymentSchedule schedule || schedule.Rows.Count != 1 ||
            schedule.Currency != cargo.Currency || schedule.UnitQuantity != cargo.UnitQuantity || cash.Price.Unit.Multiplier != cargo.Assignment.Quote.Multiplier ||
            schedule.Rows[0].DeliveryFrom != cargo.DateFrom || schedule.Rows[0].DeliveryTo != cargo.DateTo ||
            schedule.Rows[0].PaymentAt < cargo.DischargeTo || schedule.Rows[0].AllocatedQuantity != new ExactQuantity(1, 1, cargo.Assignment.Energy.Key))
            throw new MarketDataException("lng_instrument", "Compose one normalized LNG cargo value and independently directed fixed consideration with an explicit future payment.");
        return [new(cargo, physicalDirection), new(cash, cashDirection)];
    }
}
