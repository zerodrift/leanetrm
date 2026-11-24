using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasInstrumentScheduleChecks
{
    internal static void Run()
    {
        var f = new IdentityFixture();
        Require(f.Instrument.EconomicIdentity.Canonical == LegacyIdentity, "A legacy gas instrument preserves every canonical byte.");
        var original = f.Instrument; var physical = original.Members[0]; var payment = original.Members[1];
        RetainedGasMember Member(RetainedGasMember m, int? source = null, string? policy = null, QuantityPart? part = null) =>
            new(m.SideId, m.Direction, m.Ratio, m.Program, source, policy, part);
        RetainedGasInstrument With(params RetainedGasMember[] members) => new(original.Id, [..members], original.Assignments);
        var followed = With(Member(physical, 0, PhysicalLifecycle.SelectedQuantityPolicy), Member(payment, 0));
        var band = QuantityPart.Band(new(0, 1), new(4, 5));
        var parted = With(followed.Members[0], Member(payment, 0, part: band));
        Require(original.EconomicIdentity != followed.EconomicIdentity, "The selected quantity policy belongs to the instrument's economics.");
        Require(followed.EconomicIdentity != parted.EconomicIdentity, "The payment's part belongs to the instrument's economics.");
        var anotherPhysical = new RetainedGasMember(physical.SideId, physical.Direction, new(2, 1), physical.Program, 1);
        Require(With(followed.Members[0], anotherPhysical, Member(payment, 0)).EconomicIdentity !=
            With(followed.Members[0], anotherPhysical, Member(payment, 1)).EconomicIdentity, "A payment names its physical source in identity.");
        Reject(() => Member(payment, 0, PhysicalLifecycle.SelectedQuantityPolicy));
        Reject(() => Member(physical, 0, part: band));
        Reject(() => Member(payment, part: band));
        Reject(() => Member(physical, 0, "unknown_policy_v1"));
        Reject(() => With(Member(physical, 0), Member(payment, 0)));
        Reject(() => With(Member(physical, 0, PhysicalLifecycle.SelectedQuantityPolicy), Member(payment, 1)));
        Reject(() => With(Member(physical, 0, PhysicalLifecycle.SelectedQuantityPolicy), payment));
        Console.WriteLine("PASS gas instrument identity: legacy bytes, explicit policy, part and physical source; invalid membership terms refuse");
    }

    private static void Require(bool condition, string message) { if(!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch(MarketDataException error) when(error.Code == "gas_membership") { return; }
        throw new Exception("Expected gas_membership refusal.");
    }
    private const string LegacyIdentity = """
        {"contract":"retained_gas_instrument_v1","Assignments":[{"contract":"gas_valuation_assignment_v1","Adjustment":["price","0",["price_unit",["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"currency"],["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"quantity"],"1"]],"Fixing":{"contract":"IndexFixing_v1","FixingAt":"2040-12-31T12:00:00.0000000\u002B00:00","From":"2041-01-01","Implementation":"published_index_observations_v1","Index":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"local_index"],"Quote":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"outright"],"Series":["series",["definition_revision","dbbd2280-812e-4421-b037-d21e5ed95b71","b1b34601-46ed-4169-9baf-6574ffea746b"],"c42f0d87-254d-4ba3-a452-000000000032"],"Source":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"source"],"To":"2041-01-02"},"From":"2041-01-01","Key":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_assignment"],"Location":{"contract":"gas_delivery_location_v1","Pipeline":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_pipeline"],"Point":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_point"],"Zone":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_zone"]},"Product":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_product"],"Projection":["projection",["request",["curve",["definition_revision","dbbd2280-812e-4421-b037-d21e5ed95b71","b1b34601-46ed-4169-9baf-6574ffea746b"],"local"],["contract",["definition_revision","dbbd2280-812e-4421-b037-d21e5ed95b71","b1b34601-46ed-4169-9baf-6574ffea746b"],"c42f0d87-254d-4ba3-a452-000000000001"]],["market_contract",["contract",["definition_revision","dbbd2280-812e-4421-b037-d21e5ed95b71","b1b34601-46ed-4169-9baf-6574ffea746b"],"c42f0d87-254d-4ba3-a452-000000000001"],2,"2041-01-01","2041-01-02","2041-01-02T00:00:00.0000000\u002B00:00",null],null],"Quote":["quote",["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"outright"],1,["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"currency"],["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"quantity"],"1","0.001","0.001","-1000","1000"],"Settlement":{"contract":"price_settlement_selection_v1","CurveTargetIndex":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"local_index"],"Mode":1,"ProjectionPolicy":null,"SelectedIndex":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"local_index"]},"To":"2041-01-02"}],"Members":[{"contract":"retained_gas_member_v1","Direction":"1","ProgramDigest":"931dcc0aaf795d15164870b4f47357e13f6497354419228ec6f6aeb0378f4a03","Ratio":["exact_ratio","1","1"]},{"contract":"retained_gas_member_v1","Direction":"-1","ProgramDigest":"d6fe69098e2a07959612fcb6c3171aaf8bb4223ea4254313d799410afdf941ce","Ratio":["exact_ratio","1","1"]}]}
        """;

    // A compact D360 delivery/cash composition with stable reference and Side
    // identities makes the pre-change canonical bytes an independent oracle.
    internal sealed class IdentityFixture
    {
        internal readonly RetainedGasInstrument Instrument;
        internal IdentityFixture()
        {
            var f = new CommodityLookupFixture(31);
            var lookup = f.Swap().Floating.GenerateSchedule().Rows[0].Prices[0].Lookup!;
            var from = lookup.Contract.DeliveryFrom; var to = lookup.Contract.DeliveryTo;
            ReferenceKey R(string code) => new(f.Quote.Key.Revision, code);
            var clock = GasDayClock.Capture(R("synthetic_schedule_clock"), TimeZoneInfo.Utc, 360);
            var location = new GasDeliveryLocation(R("synthetic_pipeline"), R("synthetic_zone"), R("synthetic_point"));
            ImmutableArray<GasValuationAssignment> assignments = [new(R("synthetic_assignment"), location, R("synthetic_product"),
                from, to, lookup.Settlement, lookup.Quote, new(lookup.Market, lookup.Contract), lookup.Fixing, new(default, f.Quote))];
            var gas = new PhysicalGasSide(Guid.Parse("c01cb550-df60-4cd6-b601-524026b00001"), location, R("synthetic_product"),
                new(clock, from, to, GasQuantityBasis.PerOperatingDay, new(ExactDecimal.Parse("1"), f.Quote.Quantity),
                    GasFuelConvention.None, default), assignments, SyntheticSideCalendars.Physical());
            var cash = new FixedSide(Guid.Parse("c01cb550-df60-4cd6-b601-524026b00002"), new FlatPrice(new UnitPrice(ExactDecimal.Parse("3"), f.Quote)),
                f.Quote, gas.UnitQuantity, [new(from, to, clock.Boundary(to).AddDays(2), new(1, 1), null)
                    { DeliveryCompletionAt = clock.Boundary(to) }], SyntheticSideCalendars.Dated(false), gasDeliveryClock: clock);
            BookedSideProgram Book(Side side) => BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side,
                lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery));
            Instrument = new(Guid.Parse("c01cb550-df60-4cd6-b601-524026b00003"),
                [new(gas.Id, ExactDecimal.Parse("1"), ExactRatio.One, Book(gas)),
                 new(cash.Id, ExactDecimal.Parse("-1"), ExactRatio.One, Book(cash))], assignments);
        }
    }
}
