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
        var followed = With(Member(physical, 0, "delivery_schedule_v1"), Member(payment, 0));
        var band = QuantityPart.Band(new(0, 1), new(4, 5));
        var parted = With(followed.Members[0], Member(payment, 0, part: band));
        Require(original.EconomicIdentity != followed.EconomicIdentity, "The selected quantity policy belongs to the instrument's economics.");
        Require(followed.EconomicIdentity != parted.EconomicIdentity, "The payment's part belongs to the instrument's economics.");
        var anotherPhysical = new RetainedGasMember(physical.SideId, physical.Direction, new(2, 1), physical.Program, 1);
        Require(With(followed.Members[0], anotherPhysical, Member(payment, 0)).EconomicIdentity !=
            With(followed.Members[0], anotherPhysical, Member(payment, 1)).EconomicIdentity, "A payment names its physical source in identity.");
        Reject(() => Member(payment, 0, "delivery_schedule_v1"));
        Reject(() => Member(physical, 0, part: band));
        Reject(() => Member(payment, part: band));
        Reject(() => Member(physical, 0, "unknown_policy_v1"));
        Reject(() => Member(physical, 0, "selected_delivery_quantity_v1"));
        Reject(() => With(Member(physical, 0), Member(payment, 0)));
        Reject(() => With(Member(physical, 0, "delivery_schedule_v1"), Member(payment, 1)));
        Reject(() => With(Member(physical, 0, "delivery_schedule_v1"), payment));
        Console.WriteLine("PASS gas instrument identity: legacy bytes, explicit policy, part and physical source; invalid membership terms refuse");
        WorkedExamples();
        GasSourceGeometryChecks.Run();
    }

    static void WorkedExamples()
    {
        static void Equal(ExactMoney actual, string expected) => Require(actual.ToExactString() == expected, $"Expected {expected}, received {actual.ToExactString()}.");
        static void Refused(Action action)
        {
            try { action(); } catch(MarketDataException e) when(e.Code == "physical_lifecycle_quantity") { return; }
            throw new Exception("Missing or mismatched entry quantities must refuse.");
        }
        var f = new GasPricingScheduleFixture();
        var aDays = f.Days((10000, PhysicalLifecycle.ContractQuantity), (9000, PhysicalLifecycle.ScheduledQuantity), (7000, PhysicalLifecycle.AllocatedProvisionalQuantity));
        var a = f.Value(f.State(aDays));
        string[][] amounts = [["31000", "-24000", "-6500", "0", "0"], ["27900", "-24000", "-3250", "3200", "-3000"], ["21700", "-21000", "0", "9600", "-9000"]];
        for(var day = 0; day < 3; day++) for(var slot = 0; slot < 5; slot++)
            Equal(a.Rows.Single(r => r.Slot == slot && r.DeliveryFrom == f.From.AddDays(day)).Unrealized, amounts[day][slot]);
        string[] sums = ["80600", "-69000", "-9750", "12800", "-12000"];
        for(var slot = 0; slot < 5; slot++) Equal(a.Rows.Where(r => r.Slot == slot).Select(r => r.Unrealized).Aggregate((x, y) => x.Add(y)), sums[slot]);
        Equal(a.Total, "2650");
        var evidenceContext = f.Context(f.State(aDays));
        Equal(f.Instrument.Value(evidenceContext).Scale(ExactDecimal.Parse("10000")), "2650");
        Require(evidenceContext.Entries.Select(e=>e.Value.Scale(ExactDecimal.Parse("10000")).ToExactString()).SequenceEqual(sums),
            "The unit valuation retains every signed entry from the same Side evaluations for risk reports.");
        var entry = f.Sides(f.State(aDays)).Sides[2];
        var shown = entry.Value.InspectSchedule(f.Instrument.Members[2].Program.Document.ScheduleManifest,
            ExactDecimal.Parse("10000"), new(-1, 1), f.Clock.Boundary).Read(0, 20).Rows[1];
        Require(shown.Quantity == "-1000" && shown.QuantityState == "band_v1" && shown.SourceQuantity == "9000" &&
            shown.SourceQuantityStates.SequenceEqual(["scheduled"]), "A payment part shows its signed taken quantity and unsigned source evidence.");

        var bDays = f.Days((9000, PhysicalLifecycle.AllocatedFinalQuantity), (7000, PhysicalLifecycle.AllocatedFinalQuantity), (10000, PhysicalLifecycle.ContractQuantity));
        var b = f.Value(f.State(bDays), ended: true);
        string[][] accrued = [["0", "-24000", "-3350", "3300", "-3000"], ["0", "-21000", "0", "9300", "-9000"]];
        for(var day = 0; day < 2; day++) for(var slot = 0; slot < 5; slot++)
            Equal(b.Rows.Single(r => r.Slot == slot && r.DeliveryFrom == f.From.AddDays(day)).Accrued, accrued[day][slot]);
        for(var slot = 0; slot < 5; slot++) Equal(b.Rows.Single(r => r.Slot == slot && r.DeliveryFrom == f.From.AddDays(2)).Unrealized, amounts[0][slot]);
        Equal(b.RealizedUnsettled, "-47750"); Equal(b.Unrealized, "500"); Equal(b.Total, "-47250");
        var unpublished = f.Value(f.State(bDays), ended: true, publishedMonth: false);
        Equal(unpublished.RealizedUnsettled, "9250"); Equal(unpublished.Unrealized, "-56500"); Equal(unpublished.Total, "-47250");
        Require(unpublished.Rows.Where(r => r.Slot is 1 or 4 && r.DeliveryFrom < f.From.AddDays(2)).All(r =>
            r.Settlement == SettlementState.Projected && r.Accrued.ToExactString() == "0"), "An unpublished month remains forecast after delivery ends.");
        Refused(() => f.Instrument.Value(f.Context(null)));
        Refused(() => f.Value(f.State([aDays[0], aDays[2]])));
        var contractual = new GasPricingScheduleFixture(follows: false);
        var contract = contractual.Value(contractual.State(contractual.Days((10000, PhysicalLifecycle.ContractQuantity), (10000, PhysicalLifecycle.ContractQuantity), (10000, PhysicalLifecycle.ContractQuantity))));
        foreach(var row in contract.Rows) Equal(row.Unrealized, amounts[0][row.Slot]);
        Refused(() => contractual.Value(contractual.State(contractual.Days((10000, PhysicalLifecycle.ContractQuantity), (9000, PhysicalLifecycle.ScheduledQuantity), (10000, PhysicalLifecycle.ContractQuantity)))));
        var partial = new GasPricingScheduleFixture(partial: true);
        var p1 = partial.Value(partial.State(partial.Days((500, PhysicalLifecycle.ScheduledQuantity), (9000, PhysicalLifecycle.ScheduledQuantity), (7000, PhysicalLifecycle.AllocatedProvisionalQuantity))));
        var p2 = partial.Value(partial.State(partial.Days((10000, PhysicalLifecycle.ContractQuantity), (9000, PhysicalLifecycle.ScheduledQuantity), (7000, PhysicalLifecycle.AllocatedProvisionalQuantity))));
        Require(p1.Rows.Where(r => r.Slot > 0).SequenceEqual(p2.Rows.Where(r => r.Slot > 0)), "A payment ignores records outside its own term.");
        var paid = new CashOccurrencePayment(Guid.NewGuid(), 2, 1, f.Clock.Boundary(f.From.AddDays(2)), f.Clock.Boundary(f.From.AddDays(2)), ExactMoney.Parse("-3250", f.Quote.Currency));
        var corrected = f.Value(f.State(bDays.SetItem(1, bDays[1] with { Selected = ExactQuantity.FromQuantity(new(ExactDecimal.Parse("8000"), f.Quote.Quantity)) }), [paid]), ended: true);
        var overpaid = corrected.Rows.Single(r => r.Slot == 2 && r.DeliveryFrom == f.From.AddDays(1));
        Equal(overpaid.Accrued, "0"); Equal(overpaid.Paid, "-3250"); Equal(overpaid.Outstanding, "3250");
        Require(overpaid.Settlement == SettlementState.Overpaid, "A part reduced to zero keeps its recorded overpayment.");
        var doubled = new GasPricingScheduleFixture(size: 20000);
        var twice = doubled.Value(doubled.State(doubled.Days((20000, PhysicalLifecycle.ContractQuantity), (18000, PhysicalLifecycle.ScheduledQuantity), (14000, PhysicalLifecycle.AllocatedProvisionalQuantity))));
        Require(twice.Rows.Zip(a.Rows).All(p => p.First.Unrealized.ToExactString() == p.Second.Unrealized.Scale(ExactDecimal.Parse("2")).ToExactString()), "Doubling the base and records doubles every amount exactly.");
        var share = new GasPricingScheduleFixture(sharedPayment: true);
        var sharedState = share.State(share.Days((10000, PhysicalLifecycle.ContractQuantity), (10000, PhysicalLifecycle.ContractQuantity), (10000, PhysicalLifecycle.ContractQuantity)));
        Equal(share.Value(sharedState).Rows.Single(r => r.Slot == 1).Unrealized, "33600");
        var side = share.Sides(sharedState).Sides[1];
        var inspected = side.Value.InspectSchedule(share.Instrument.Members[1].Program.Document.ScheduleManifest, ExactDecimal.Parse("10000"), new(21, 20), share.Clock.Boundary).Read(0, 20);
        Require(inspected.Rows.Single().Quantity == "10500", "The payment's displayed quantity includes its share once.");
        var presentationOnlyCode = ((RetainedProgramValuation)side.Value).ViewInput("payment.with.dots", ExactDecimal.Parse("10000"), new(21, 20), []);
        Require(ScheduleViews.TotalQuantity(presentationOnlyCode, share.Clock.Boundary) == new SignedRatio(10500, 1),
            "A presentation-only code limitation cannot refuse a valid payment's quantity total.");
        Console.WriteLine("PASS gas entry schedules: both worked examples, unpublished month, exact share, parts, source dates, missing state, overpayment and size scaling");
    }

    private static void Require(bool condition, string message) { if(!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch(MarketDataException error) when(error.Code == "gas_membership") { return; }
        throw new Exception("Expected gas_membership refusal.");
    }
    internal const string LegacyIdentity = """
        {"contract":"retained_gas_instrument_v1","Assignments":[{"contract":"gas_valuation_assignment_v1","Adjustment":["price","0",["price_unit",["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"currency"],["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"quantity"],"1"]],"Fixing":{"contract":"IndexFixing_v1","FixingAt":"2040-12-31T12:00:00.0000000\u002B00:00","From":"2041-01-01","Implementation":"published_index_observations_v1","Index":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"local_index"],"Quote":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"outright"],"Series":["series",["definition_revision","dbbd2280-812e-4421-b037-d21e5ed95b71","b1b34601-46ed-4169-9baf-6574ffea746b"],"c42f0d87-254d-4ba3-a452-000000000032"],"Source":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"source"],"To":"2041-01-02"},"From":"2041-01-01","Key":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_assignment"],"Location":{"contract":"gas_delivery_location_v1","Pipeline":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_pipeline"],"Point":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_point"],"Zone":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_zone"]},"Product":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"synthetic_product"],"Projection":["projection",["request",["curve",["definition_revision","dbbd2280-812e-4421-b037-d21e5ed95b71","b1b34601-46ed-4169-9baf-6574ffea746b"],"local"],["contract",["definition_revision","dbbd2280-812e-4421-b037-d21e5ed95b71","b1b34601-46ed-4169-9baf-6574ffea746b"],"c42f0d87-254d-4ba3-a452-000000000001"]],["market_contract",["contract",["definition_revision","dbbd2280-812e-4421-b037-d21e5ed95b71","b1b34601-46ed-4169-9baf-6574ffea746b"],"c42f0d87-254d-4ba3-a452-000000000001"],2,"2041-01-01","2041-01-02","2041-01-02T00:00:00.0000000\u002B00:00",null],null],"Quote":["quote",["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"outright"],1,["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"currency"],["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"quantity"],"1","0.001","0.001","-1000","1000"],"Settlement":{"contract":"price_settlement_selection_v1","CurveTargetIndex":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"local_index"],"Mode":1,"ProjectionPolicy":null,"SelectedIndex":["reference",["reference_revision","af839777-9f42-49f4-bf48-9a240bfe80b7","ddba8ff1-d44c-44c6-a653-42cccd1831db"],"local_index"]},"To":"2041-01-02"}],"Members":[{"contract":"retained_gas_member_v1","Direction":"1","ProgramDigest":"931dcc0aaf795d15164870b4f47357e13f6497354419228ec6f6aeb0378f4a03","Ratio":["exact_ratio","1","1"]},{"contract":"retained_gas_member_v1","Direction":"-1","ProgramDigest":"d6fe69098e2a07959612fcb6c3171aaf8bb4223ea4254313d799410afdf941ce","Ratio":["exact_ratio","1","1"]}]}
        """;

    // A compact D360 delivery/cash composition with stable reference and Side
    // identities makes the pre-change canonical bytes an independent oracle.
    internal sealed class IdentityFixture
    {
        internal readonly RetainedGasInstrument Instrument;
        internal PhysicalGasSide Physical { get; }
        internal FixedSide Cash { get; }
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
            Physical = gas; Cash = cash;
            BookedSideProgram Book(Side side) => BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side,
                lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery));
            Instrument = new(Guid.Parse("c01cb550-df60-4cd6-b601-524026b00003"),
                [new(gas.Id, ExactDecimal.Parse("1"), ExactRatio.One, Book(gas)),
                 new(cash.Id, ExactDecimal.Parse("-1"), ExactRatio.One, Book(cash))], assignments);
        }
    }
}
