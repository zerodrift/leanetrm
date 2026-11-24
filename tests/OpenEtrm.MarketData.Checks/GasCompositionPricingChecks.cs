using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasCompositionPricingChecks
{
    static ExactDecimal N(string value) => ExactDecimal.Parse(value);
    static void Require(bool condition, string message) { if(!condition) throw new Exception(message); }
    static void Refused(Action action)
    {
        try { action(); } catch(MarketDataException e) when(e.Code == "gas_composition_input") { return; }
        throw new Exception("Expected gas_composition_input.");
    }
    internal static void Run()
    {
        var old = new GasInstrumentScheduleChecks.IdentityFixture();
        var legacy = new GasCompositionInstrument(Guid.Parse("c01cb550-df60-4cd6-b601-524026b00004"),
            [new("delivery", old.Physical, N("1"), N("10000"), null)],
            [new("payment", old.Cash, "consideration", "delivery", GasQuantityStage.Delivery, N("-1"), N("1"))]);
        Require(legacy.EconomicIdentity.Canonical == LegacyIdentity, "A share of one without new entry terms preserves every canonical byte.");
        var f = new PricingFixture();
        var physical = new GasPhysicalComponent("delivery", f.Physical(), N("1"), N("10000"), null);
        var payment = new GasCashComponent("payment", f.Cash(f.From, f.To), "consideration", "delivery", GasQuantityStage.Delivery, N("-1"), N("1"));
        GasCompositionInstrument Compose(GasPhysicalComponent source, params GasCashComponent[] payments) => new(Guid.NewGuid(), [source], [..payments]);
        var plain = Compose(physical, payment);
        Require(plain.PV(f.Context).PresentValue.Scale(plain.BaseQuantity).ToExactString() == "6000", "Whole quantity preserves the independent fixed purchase value.");
        var followed = Compose(physical with { QuantityPolicy = "delivery_schedule_v1" }, payment);
        Refused(() => followed.PV(f.Context));
        var band = QuantityPart.Band(new(0, 1), new(4, 5));
        var parted = Compose(physical, payment with { Part = band });
        Refused(() => parted.PV(f.Context));
        Require(followed.EconomicIdentity != plain.EconomicIdentity && parted.EconomicIdentity != plain.EconomicIdentity,
            "Policy and part are explicit booking economics.");
        var shared = Compose(physical, payment with { Share = N("1.05") });
        Require(shared.Sides[1].QuantityRatio == new ExactRatio(21, 20), "A 1.05 share scales the stage ratio once.");
        var halves = Compose(physical, payment with { Code = "first", Share = N("0.5") }, payment with { Code = "second", Share = N("0.5") });
        Require(halves.Sides.Skip(1).All(m => m.QuantityRatio == new ExactRatio(1, 2)), "Two half shares retain independent exact membership ratios.");
        Require(halves.PV(f.Context).PresentValue == plain.PV(f.Context).PresentValue, "Two independently priced halves equal one whole.");
        var partial = Compose(physical, payment with { Side = f.Cash(f.From.AddDays(1), f.To) });
        Require(partial.Sides[1].Side.GenerateSchedule().Rows.Count == 1 && partial.PV(f.Context).PresentValue.Scale(partial.BaseQuantity).ToExactString() == "36000",
            "A fixed payment can cover just the last two gas days.");
        Refused(() => Compose(physical, payment with { Side = f.Cash(f.From.AddDays(-1), f.To) }));
        Refused(() => Compose(physical, payment with { Side = f.Cash(f.From, f.To.AddDays(1)) }));
        Refused(() => Compose(physical, payment with { Side = f.Cash(f.From, f.To, allocation: 2) }));
        var offClock = GasDayClock.Capture(f.Clock.Key, TimeZoneInfo.Utc, 361);
        Refused(() => Compose(physical, payment with { Side = f.Cash(f.From, f.To, clock: offClock) }));
        ImmutableArray<GasPhysicalComponent> Sources(int count) => [..Enumerable.Range(0, count).Select(n =>
            new GasPhysicalComponent("physical" + n, f.Physical(), N("1"), N("10000"), null))];
        ImmutableArray<GasCashComponent> Payments(int count) => [..Enumerable.Range(0, count).Select(n => payment with { Code = "cash" + n, Source = "physical0" })];
        Require(new GasCompositionInstrument(Guid.NewGuid(), Sources(4), Payments(4)).Sides.Length == 8, "Four physical plus four payments are admitted.");
        Require(new GasCompositionInstrument(Guid.NewGuid(), Sources(1), Payments(7)).Sides.Length == 8, "One physical plus seven payments are admitted.");
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), Sources(1), Payments(8)));
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), Sources(2), Payments(7)));
        var linked = new GasPhysicalComponent("linked", f.Physical(), N("-1"), null,
            new("delivery", GasFuelConvention.NetEqualsGrossTimesOneMinusFuel, N("0.02")));
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [physical, linked], [payment with { Part = band }]));
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [physical, linked], [payment with { Source = "linked", Part = band }]));
        var linkedShare = new GasCompositionInstrument(Guid.NewGuid(), [physical, linked], [payment with { Source = "linked", Share = N("1.05") }]);
        Require(linkedShare.Sides[2].QuantityRatio == new ExactRatio(1029, 1000), "A share multiplies the selected stage, including its exact fuel factor.");
        Refused(() => Compose(physical, payment with { Share = N("0") }));
        Refused(() => Compose(physical, payment with { Share = N("-1") }));
        Refused(() => Compose(physical, payment with { Share = null }));
        Refused(() => Compose(physical with { QuantityPolicy = "unknown_v1" }, payment));
        Refused(() => new GasCompositionInstrument(Guid.NewGuid(), [physical with { QuantityPolicy = "delivery_schedule_v1" }, linked], [payment]));
        Console.WriteLine("PASS gas composition pricing: legacy identity, explicit share, partial dates, exact stage scaling, policy/part valuation refusal and side limits");
    }
    sealed class PricingFixture
    {
        readonly CommodityLookupFixture f = new(31);
        readonly ImmutableArray<GasValuationAssignment> assignments;
        readonly GasDeliveryLocation location;
        internal DateOnly From { get; }
        internal DateOnly To => From.AddDays(3);
        internal GasDayClock Clock { get; }
        internal SideValuationContext Context { get; }
        internal PricingFixture()
        {
            var lookups = f.Swap().Floating.GenerateSchedule().Rows[0].Prices.Take(3).Select(p => p.Lookup!).ToArray();
            From = lookups[0].Contract.DeliveryFrom;
            ReferenceKey R(string code) => new(f.Quote.Key.Revision, code);
            Clock = GasDayClock.Capture(R("synthetic_pricing_clock"), TimeZoneInfo.Utc, 360);
            location = new(R("pipeline"), R("zone"), R("point"));
            assignments = [..lookups.Select((p, n) => new GasValuationAssignment(R("assignment_" + n), location, R("product"),
                p.Contract.DeliveryFrom, p.Contract.DeliveryTo, p.Settlement, p.Quote, new(p.Market, p.Contract), p.Fixing, new(default, f.Quote)))];
            var snapshot = (f.Snapshot with { Selections = [..f.Snapshot.Selections.Select((s, n) => s with {
                Observation = s.Observation with { Value = N(n % 2 == 0 ? "3" : "0.20") } })] }).WithKnowledge(Guid.NewGuid(), f.Snapshot.ValuationAt);
            var market = f.Model.Build(snapshot, Physical().DependenciesAt(snapshot));
            Context = SideValuationContext.ForPhysicalGas(market, market, new ExplicitDiscounting(N("1"), DiscountFactorSource.Undiscounted), assignments);
        }
        internal PhysicalGasSide Physical() => new(Guid.NewGuid(), location, assignments[0].Product,
            new(Clock, From, To, GasQuantityBasis.PerOperatingDay, new(N("1"), f.Quote.Quantity), GasFuelConvention.None, default),
            assignments, SyntheticSideCalendars.Physical());
        internal FixedSide Cash(DateOnly from, DateOnly to, int? allocation = null, GasDayClock? clock = null)
        {
            clock ??= Clock;
            return new(Guid.NewGuid(), new FlatPrice(new UnitPrice(N("3"), f.Quote)), f.Quote, Physical().UnitQuantity,
                [new(from, to, clock.Boundary(to).AddDays(2), new(allocation ?? to.DayNumber - from.DayNumber, 1), null)
                    { DeliveryCompletionAt = clock.Boundary(to) }], SyntheticSideCalendars.Dated(false), gasDeliveryClock: clock);
        }
    }
    private const string LegacyIdentity = """
        {"contract":"gas_composition_instrument_v1","Roles":[{"contract":"gas_composition_role_v1","Code":"delivery","Link":null,"Role":"physical","Source":null,"Stage":null},{"contract":"gas_composition_role_v1","Code":"payment","Link":null,"Role":"consideration","Source":"delivery","Stage":2}],"Sides":[[{"contract":"physical_gas_side_v2","CalendarPolicy":{"contract":"side_calendar_policy_v1","Delivery":{"contract":"side_calendar_binding_v1","Applicable":true,"Calendar":{"contract":"captured_calendar_definition_v1","Exceptions":[],"From":"2000-01-01","Key":["reference",["reference_revision","2e6f3c90-54e8-4c00-8356-1e2a7d950001","2e6f3c90-54e8-4c00-8356-1e2a7d950002"],"synthetic_all_dates"],"To":"2100-01-01","WeekendMask":0},"DefinitionDigest":"0f46650be48da3d709148d8b48d4d1beb3b8b56aa89e47366691177ab97776eb","Publication":null},"Observation":{"contract":"side_calendar_binding_v1","Applicable":false,"Calendar":null,"DefinitionDigest":null,"Publication":null},"Payment":{"contract":"side_calendar_binding_v1","Applicable":false,"Calendar":null,"DefinitionDigest":null,"Publication":null},"PaymentTiming":null},"Location":{"contract":"gas_delivery_location_v1","Pipeline":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_pipeline"],"Point":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_point"],"Zone":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_zone"]},"Product":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_product"],"Quantities":{"contract":"gas_quantity_terms_v1","Basis":1,"Clock":{"contract":"gas_day_clock_v1","Key":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_schedule_clock"],"Rules":"UTC;0;(UTC) Coordinated Universal Time;Coordinated Universal Time;Coordinated Universal Time;;","StartMinute":360},"From":"2041-01-01","FuelConvention":1,"FuelFraction":"0","Receipt":["quantity","1",["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"quantity"]],"To":"2041-01-02"}},"1",["exact_ratio","1","1"]],[{"contract":"FixedSide_v5","CalendarPolicy":{"contract":"side_calendar_policy_v1","Delivery":{"contract":"side_calendar_binding_v1","Applicable":true,"Calendar":{"contract":"captured_calendar_definition_v1","Exceptions":[],"From":"2000-01-01","Key":["reference",["reference_revision","2e6f3c90-54e8-4c00-8356-1e2a7d950001","2e6f3c90-54e8-4c00-8356-1e2a7d950002"],"synthetic_all_dates"],"To":"2100-01-01","WeekendMask":0},"DefinitionDigest":"0f46650be48da3d709148d8b48d4d1beb3b8b56aa89e47366691177ab97776eb","Publication":null},"Observation":{"contract":"side_calendar_binding_v1","Applicable":false,"Calendar":null,"DefinitionDigest":null,"Publication":null},"Payment":{"contract":"side_calendar_binding_v1","Applicable":true,"Calendar":{"contract":"captured_calendar_definition_v1","Exceptions":[],"From":"2000-01-01","Key":["reference",["reference_revision","2e6f3c90-54e8-4c00-8356-1e2a7d950001","2e6f3c90-54e8-4c00-8356-1e2a7d950002"],"synthetic_all_dates"],"To":"2100-01-01","WeekendMask":0},"DefinitionDigest":"0f46650be48da3d709148d8b48d4d1beb3b8b56aa89e47366691177ab97776eb","Publication":null},"PaymentTiming":"utc_payment_day_v1"},"DateQuoteKey":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"outright"],"GasDeliveryClock":{"contract":"gas_day_clock_v1","Key":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_schedule_clock"],"Rules":"UTC;0;(UTC) Coordinated Universal Time;Coordinated Universal Time;Coordinated Universal Time;;","StartMinute":360},"IntervalDefinition":null,"Periods":[{"contract":"fixed_side_period_v2","Allocation":["exact_ratio","1","1"],"DeliveryCompletionAt":"2041-01-02T06:00:00.0000000\u002B00:00","DeliveryFrom":"2041-01-01","DeliveryTo":"2041-01-02","PaymentAt":"2041-01-04T06:00:00.0000000\u002B00:00","PaymentSchedule":null}],"PowerQuantity":null,"Price":{"contract":"ppa_flat_price_v1","Price":["price","3",["price_unit",["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"currency"],["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"quantity"],"1"]]},"QuantityBasis":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"quantity"]},"-1",["exact_ratio","1","1"]]]}
        """;
}
