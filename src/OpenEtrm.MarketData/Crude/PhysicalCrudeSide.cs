using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Crude;

[EconomicDefinition("crude_delivery_location_v1")]
public sealed record CrudeDeliveryLocation
{
    [IdentityField] public ReferenceKey Facility { get; }
    [IdentityField] public ReferenceKey CustodyPoint { get; }
    public CrudeDeliveryLocation(ReferenceKey facility, ReferenceKey custodyPoint)
    {
        Money.RequireKey(facility); Money.RequireKey(custodyPoint);
        if (facility.Revision != custodyPoint.Revision || facility == custodyPoint)
            throw new MarketDataException("crude_delivery_location", "Select distinct exact facility and custody-point references in one revision.");
        Facility = facility; CustodyPoint = custodyPoint;
    }
}

[EconomicDefinition("crude_valuation_assignment_v1")]
public sealed record CrudeValuationAssignment
{
    [IdentityField] public ReferenceKey Key { get; }
    [IdentityField] public ReferenceKey Commodity { get; }
    [IdentityField] public ReferenceKey Grade { get; }
    [IdentityField] public CrudeDeliveryLocation Location { get; }
    [IdentityField] public QuantitySpecification Quantity { get; }
    [IdentityField] public ReferenceKey Clock { get; }
    [IdentityField] public ExactDecimal StandardTemperature { get; }
    [IdentityField] public string TemperatureScale { get; }
    [IdentityField] public PriceSettlementSelection Settlement { get; }
    [IdentityField] public QuoteSpecification Quote { get; }
    [IdentityField] public SwapProjection Projection { get; }
    [IdentityField] public IndexFixing Fixing { get; }
    public IndexPriceLookup Lookup => new(Settlement, Quote, Projection.Market, Projection.Contract, null, Fixing);

    public CrudeValuationAssignment(ReferenceKey key, ReferenceKey commodity, ReferenceKey grade,
        CrudeDeliveryLocation location, QuantitySpecification quantity, ReferenceKey clock,
        ExactDecimal standardTemperature, string temperatureScale, PriceSettlementSelection settlement,
        QuoteSpecification quote, SwapProjection projection, IndexFixing fixing)
    {
        if (location is null || quantity is null || settlement is null || quote is null || projection?.Contract is null || fixing is null)
            throw Invalid();
        foreach (var reference in new[] { key, commodity, grade, location.Facility, location.CustodyPoint, quantity.Key,
            quantity.Commodity, quantity.MeasurementBasis, quantity.Unit, quantity.Dimension, clock, quote.Key, quote.Quantity,
            quote.Currency, settlement.SelectedIndex, fixing.Index, fixing.Quote, fixing.Source })
        {
            Money.RequireKey(reference);
            if (reference.Revision != key.Revision) throw Invalid();
        }
        var contract = projection.Contract;
        if (quantity.Commodity != commodity || quantity.Dimension.Code != "volume" || quote.Quantity != quantity.Key ||
            standardTemperature != ExactDecimal.Parse("60") || temperatureScale != "fahrenheit" ||
            quote.Kind != QuoteKind.Outright || !CrudeDeliveryWindow.IsMonthlyForward(contract) ||
            projection.Market.Curve.Revision != contract.Key.Revision || projection.Market.Contract != contract.Key ||
            projection.AveragingQuantity is not null || fixing.Index != settlement.SelectedIndex || fixing.Quote != quote.Key ||
            fixing.From != contract.DeliveryFrom || fixing.To != contract.DeliveryTo ||
            fixing.Series.Revision != contract.Key.Revision || fixing.Implementation != IndexFixing.SupportedImplementation ||
            !TradingInstant.IsSupported(fixing.FixingAt)) throw Invalid();
        Key = key; Commodity = commodity; Grade = grade; Location = location; Quantity = quantity; Clock = clock;
        StandardTemperature = standardTemperature; TemperatureScale = temperatureScale;
        Settlement = settlement; Quote = quote; Projection = projection; Fixing = fixing;
    }
    private static MarketDataException Invalid() => new("crude_valuation_assignment",
        "Select an exact grade, facility/custody point, 60 Fahrenheit standard-volume quantity and qualified monthly forward/index assignment.");
}

[EconomicDefinition("physical_crude_side_v1")]
public sealed record PhysicalCrudeSide : Side
{
    [IdentityField] public CrudeDeliveryLocation Location => Assignment.Location;
    [IdentityField] public ReferenceKey Commodity => Assignment.Commodity;
    [IdentityField] public ReferenceKey Grade => Assignment.Grade;
    [IdentityField] public ReferenceKey Clock => Assignment.Clock;
    [IdentityField] public QuantitySpecification Quantity => Assignment.Quantity;
    [IdentityField] public ExactDecimal StandardTemperature => Assignment.StandardTemperature;
    [IdentityField] public string TemperatureScale => Assignment.TemperatureScale;
    [IdentityField] public DateTimeOffset DeliveryFrom { get; }
    [IdentityField] public DateTimeOffset DeliveryTo { get; }
    [IdentityField] public ExactDecimal Tolerance { get; }
    public CrudeValuationAssignment Assignment { get; }
    public DateOnly DeliveryDateFrom => window.DateFrom;
    public DateOnly DeliveryDateTo => window.DateTo;
    public ReferenceKey Currency => Assignment.Quote.Currency;
    public Quantity UnitQuantity => new(ExactDecimal.Parse("1"), Quantity.Key);
    public override DefinitionIdentity PricingBindingIdentity => DefinitionIdentity.For(Assignment);
    public override ImmutableArray<CurveRequest> Dependencies => [Assignment.Projection.Market];
    public override ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot) =>
        PeriodPriceSelector.ProjectionAt(Assignment.Lookup, snapshot) is {} market ? [market] : [];
    private readonly CrudeDeliveryWindow window;
    private readonly Lazy<PhysicalCrudeSchedule> schedule;

    public PhysicalCrudeSide(Guid id, CrudeValuationAssignment assignment, DateTimeOffset deliveryFrom,
        DateTimeOffset deliveryTo, ExactDecimal tolerance) : base(id)
    {
        if (assignment is null) throw new MarketDataException("crude_valuation_assignment", "Select an exact crude valuation assignment.");
        if (tolerance != default) throw new MarketDataException("crude_tolerance", "This fixed-total crude profile requires explicit zero tolerance.");
        window = new(deliveryFrom, deliveryTo);
        window.RequireCoveredBy(assignment.Projection.Contract);
        Assignment = assignment; DeliveryFrom = deliveryFrom; DeliveryTo = deliveryTo; Tolerance = tolerance;
        schedule = new(() => new(this));
    }
    public override PhysicalCrudeSchedule GenerateSchedule() => schedule.Value;
    public override PhysicalCrudeSideValuation PV(SideValuationContext context) => GenerateSchedule().Evaluate(context);
}

public sealed record PhysicalCrudeSchedule : SideSchedule
{
    public PhysicalCrudeSide Side { get; }
    public override ImmutableArray<CurveRequest> Dependencies => Side.Dependencies;
    public override ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot) => Side.DependenciesAt(snapshot);
    public override IReadOnlyList<ScheduleRow> Rows { get; }
    private readonly Lazy<BookedSideProgram> program;
    public BookedSideProgram Program => program.Value;
    internal PhysicalCrudeSchedule(PhysicalCrudeSide side) : base(side.Id, side.Currency, side.UnitQuantity, "crude_future_delivery_schedule_v1")
    {
        Side = side;
        var from = side.DeliveryDateFrom; var to = side.DeliveryDateTo; var one = ExactDecimal.Parse("1");
        var row = new ScheduleRow(side.Id, from, to, side.DeliveryTo, side.Currency, side.UnitQuantity, null, null, null,
            [new(one,null,side.Assignment.Lookup,null)], Implementation, [new(from,to,0,one,Implementation)], Implementation);
        Rows = [new ScheduleRow(row,side.Id,0,new(1,1,side.Quantity.Key)).WithPhysicalInterval(
            new(side.DeliveryFrom,side.DeliveryTo,from,side.DeliveryFrom.Hour,side.DeliveryFrom.Minute,1))];
        program = new(() => BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side)));
    }
    public override PhysicalCrudeSideValuation Evaluate(SideValuationContext context)
    {
        if (context?.CrudeExecution is not {} execution)
            throw new MarketDataException("side_market_sources", "Crude delivery requires explicit certified market inputs and its qualified grade/custody assignment.");
        return new(this,context,execution.Sources.Evaluate(Program,SideId,execution.Scenario,context.Discounting));
    }
}
public sealed record PhysicalCrudeSideValuation : SideValuation
{
    public BookedSideEvaluation Execution { get; }
    internal PhysicalCrudeSideValuation(PhysicalCrudeSchedule schedule,SideValuationContext context,BookedSideEvaluation execution)
        :base(schedule,context,execution.PresentValue) { Execution=execution; }
}

[EconomicDefinition("physical_crude_instrument_v1")]
public sealed record PhysicalCrudeInstrument : SidedInstrument
{
    public PhysicalCrudeSide Delivery => (PhysicalCrudeSide)Sides[0].Side;
    public override ReferenceKey Currency => Delivery.Currency;
    public override Quantity UnitQuantity => Delivery.UnitQuantity;
    public override DefinitionIdentity PricingBindingIdentity => Delivery.PricingBindingIdentity;
    internal override bool SupportsSettlement => false;
    public PhysicalCrudeInstrument(Guid id,PhysicalCrudeSide delivery,FixedSide consideration,FixedSide? handlingFee,
        ExactDecimal direction,ExactDecimal considerationDirection,ExactDecimal? feeDirection)
        :base(id,Compose(delivery,consideration,handlingFee,direction,considerationDirection,feeDirection)) { }
    public override void ValidateBinding(Quantity quantity, OpenEtrm.MarketData.Power.DeliveryCoverage? coverage)
    {
        base.ValidateBinding(quantity, coverage);
        if (quantity is null || quantity.Specification != UnitQuantity.Specification || quantity.Amount.CompareTo(default) <= 0)
            throw new MarketDataException("crude_quantity", "Select a positive fixed total in the exact admitted standard-volume quantity specification.");
    }
    private static ImmutableArray<SideMembership> Compose(PhysicalCrudeSide delivery,FixedSide consideration,FixedSide? fee,
        ExactDecimal direction,ExactDecimal considerationDirection,ExactDecimal? feeDirection)
    {
        static bool Sign(ExactDecimal value) => value==ExactDecimal.Parse("1") || value==ExactDecimal.Parse("-1");
        if(delivery is null || consideration is null || !Sign(direction) || !Sign(considerationDirection) ||
            (fee is null ? feeDirection is not null : feeDirection is null || !Sign(feeDirection.Value))) throw Invalid();
        foreach(var side in fee is null ? new[]{consideration} : new[]{consideration,fee})
        {
            if(side.Price is not FlatPrice || side.GenerateSchedule() is not SidePaymentSchedule cash || cash.Rows.Count!=1 ||
                cash.Currency!=delivery.Currency || cash.UnitQuantity!=delivery.UnitQuantity) throw Invalid();
            var row=cash.Rows[0];
            if(row.DeliveryFrom!=delivery.DeliveryDateFrom || row.DeliveryTo!=delivery.DeliveryDateTo ||
                row.AllocatedQuantity!=new ExactQuantity(1,1,delivery.Quantity.Key) || row.PaymentAt<delivery.DeliveryTo) throw Invalid();
        }
        return fee is null ? [new(delivery,direction),new(consideration,considerationDirection)] :
            [new(delivery,direction),new(consideration,considerationDirection),new(fee,feeDirection!.Value)];
    }
    private static MarketDataException Invalid() => new("crude_instrument", "Select matching one-unit fixed cash consideration and optional handling fee with independent explicit signs.");
}
