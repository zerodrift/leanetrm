using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Crude;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Physical;
using OpenEtrm.Shared.Numerics;

internal static class DeliveryBehaviorChecks
{
    private static readonly ReferenceRevision Reference = new(Guid.Parse("da111111-1111-1111-1111-111111111111"), Guid.Parse("da222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset Start = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static Guid Id(int n) => Guid.Parse($"da000000-0000-0000-0000-{n:D12}");
    private static ReferenceKey R(string code) => new(Reference, code);
    private static ExactDecimal D(string value) => ExactDecimal.Parse(value);

    internal static void Run()
    {
        Pipeline();
        Receipt();
        Console.WriteLine("PASS delivery behaviors: shared interval/transfer contracts, native gas conservation and capacity, native crude receipt/reservation/dispatch and explicit unsupported inputs");
    }

    private static void Pipeline()
    {
        var unit = R("synthetic_energy");
        ExactQuantity Q(string value) => ExactQuantity.FromQuantity(new(D(value), unit));
        var from = R("synthetic_receipt"); var transit = R("synthetic_transit"); var to = R("synthetic_delivery");
        var route = new GasTransportRouteProfile(Id(100), GasTransportRouteProfile.Version, unit,
            GasTransportRouteProfile.GrossReceiptFractions, GasTransportRouteProfile.UniformSegmentFlow,
            GasTransportRouteProfile.FixedTransit, GasTransportRouteProfile.NoNodeStorage,
            [new(Id(1), from, GasTransportRouteNode.Source), new(Id(2), transit, GasTransportRouteNode.Transit), new(Id(3), to, GasTransportRouteNode.Sink)],
            [new(Id(11), Id(1), Id(2), new SignedRatio(1, 50), SignedRatio.Zero, 3_600_000_000), new(Id(12), Id(2), Id(3), SignedRatio.Zero, SignedRatio.Zero, 0)]);
        ImmutableArray<GasTransportRouteFlow> flows = [
            new(Id(21), Id(11), Start, Start.AddHours(1), Q("100"), Start.AddHours(1), Start.AddHours(2), Q("98")),
            new(Id(22), Id(12), Start.AddHours(1), Start.AddHours(2), Q("98"), Start.AddHours(1), Start.AddHours(2), Q("98"))];
        var limits = ImmutableArray.Create(new GasTransportCapacitySlice(Start, Start.AddHours(2), new(D("100"), unit)));
        var capacity = new GasTransportCapacityProfile(Id(200), GasTransportCapacityProfile.Version, unit, Start, Start.AddHours(2),
            GasTransportCapacityProfile.ElapsedHour, GasTransportCapacityProfile.UniformStages,
            GasTransportCapacityProfile.GrossReceipts, GasTransportCapacityProfile.GrossDeliveries,
            [new(Id(31), from, transit), new(Id(32), transit, to)], limits,
            [new(from, limits), new(transit, limits)], [new(transit, limits), new(to, limits)], [new(Id(31), limits), new(Id(32), limits)]);
        var uses = ImmutableArray.Create(
            new GasTransportCapacityUse(Id(21), Id(31), from, transit, Start, Start.AddHours(1), new(D("100"), unit), Start.AddHours(1), Start.AddHours(2), new(D("98"), unit)),
            new GasTransportCapacityUse(Id(22), Id(32), transit, to, Start.AddHours(1), Start.AddHours(2), new(D("98"), unit), Start.AddHours(1), Start.AddHours(2), new(D("98"), unit)));
        var assessedCapacity = GasTransportCapacity.Assess(capacity, uses);
        var behavior = new GasPipelineDeliveryBehavior();
        DeliveryBehavior<GasPipelineDeliveryInput> shared = behavior;
        var assessment = shared.Assess(new(route, flows, [assessedCapacity]));
        if (assessment.Profile != GasPipelineDeliveryBehavior.Implementation || assessment.QuantitySpecification != unit || assessment.Activities.Length != 4)
            throw new Exception("A shared delivery result must retain the native gas profile, quantity specification and every segment stage.");
        Equal(assessment, "external_receipt", "100"); Equal(assessment, "fuel", "2"); Equal(assessment, "external_delivery", "98");
        if (assessment.Activities.Any(a => a.Geometry.Kind != "interval" || a.Geometry.Through is null) || assessment.Constraints.Any(c => c.State != "within_limit"))
            throw new Exception("The declared delayed conserving route must expose interval stages and complete native capacity/conservation assessments.");
        var delayed = assessment.Activities.Single(a => a.SourceId == Id(21) && a.Stage == "transit_delivery");
        if (delayed.Geometry.From != Start.AddHours(1) || delayed.Quantity != "98" || delayed.Location != transit)
            throw new Exception("The delivery projection must retain the native source, point and explicit transit-shifted delivery window.");
        var encoded = JsonSerializer.Serialize(assessment, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (encoded != JsonSerializer.Serialize(shared.Assess(new(route, [..flows.Reverse()], [assessedCapacity])), new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            throw new Exception("Equivalent source enumeration must retain identical delivery projection order.");
        var changed = shared.Assess(new(route, flows.SetItem(1, flows[1] with { DeliveryQuantity = Q("97") }), [assessedCapacity]));
        Equal(changed, "external_delivery", "97");
        if (!changed.Constraints.Any(c => c.State == "breached" && c.Residual == "-1") || assessment.Activities.Single(a => a.SourceId == Id(22) && a.Stage == "external_delivery").Quantity != "98")
            throw new Exception("A selected breach must remain a breach without clipping its evidence or changing an earlier immutable result.");
        var unassessed = shared.Assess(new(route, flows, []));
        if (!unassessed.Constraints.Any(c => c.Code == "capacity" && c.State == "unassessed"))
            throw new Exception("Missing capacity evidence must remain explicitly unassessed rather than successful zero use.");
        Reject("gas_transport_route_input", () => shared.Assess(new(route with { Implementation = "unsupported" }, flows, [assessedCapacity])));
        Reject("delivery_behavior_input", () => shared.Assess(new(route, flows, default)));
        Reject("delivery_behavior_input", () => shared.Assess(new(route, flows, [assessedCapacity with { Intervals = [] }])));
        Reject("delivery_behavior_input", () => shared.Assess(null!));
    }

    private static void Receipt()
    {
        var specification = new QuantitySpecification(R("synthetic_standard_volume"), R("synthetic_crude"), R("standard_60f"), R("synthetic_barrel"), R("volume"));
        Quantity Q(string value) => new(D(value), specification.Key);
        var location = new CrudeDeliveryLocation(R("synthetic_facility"), R("synthetic_custody"));
        var due = Start.AddMonths(1).AddDays(5);
        var terms = new CrudePhysicalCashTerms(CrudeReceiptCash.Implementation, Id(400), Id(401), location, R("synthetic_grade"),
            specification, D("60"), "fahrenheit", Q("1000"), new(Start, Start.AddMonths(1)), due,
            new(new(D("78"), new PriceUnit(R("synthetic_usd"), specification.Key, D("1"))), D("-1")), null,
            new(CrudeReceiptCash.RoundingImplementation, 2, CashTieRule.ToEven));
        var receipt = new CrudeAcceptedReceipt(Id(410), Start.AddDays(1), Q("400"), terms.Grade, location, CrudeReceiptTitle.Transferred);
        var disposition = new CrudeReceiptDisposition(Id(411), receipt.Id, Q("400"), CrudeDispositionKind.OwnedInventory,
            Start.AddDays(1), null, location.CustodyPoint, terms.OwnerLegalEntityId);
        var sale = new CrudeOnwardSale(Id(420), 1, terms.OwnerLegalEntityId, terms.Grade, location, specification.Key, Q("150"), terms.Delivery);
        var allocation = new CrudeInventoryAllocation(Id(421), receipt.Id, Q("150"), Start.AddDays(2), sale);
        var dispatch = new CrudeReceiptDispatch(Id(422), allocation.Id, Q("50"), Start.AddDays(3), CrudeReceiptTitle.Transferred);
        var input = new CrudeReceiptDeliveryInput(terms, [receipt], [disposition], [allocation], [dispatch], [], [], [], due);
        DeliveryBehavior<CrudeReceiptDeliveryInput> behavior = new CrudeReceiptDeliveryBehavior();
        var assessment = behavior.Assess(input);
        Equal(assessment, "accepted", "400"); Equal(assessment, "remaining", "600"); Equal(assessment, "owned", "350");
        Equal(assessment, "reserved", "100"); Equal(assessment, "dispatched", "50"); Equal(assessment, "unreserved_inventory", "250");
        if (assessment.Profile != CrudeReceiptDeliveryBehavior.Implementation || assessment.QuantitySpecification != specification.Key || assessment.Activities.Length != 2 ||
            assessment.Activities.Any(a => a.Geometry.Kind != "transfer" || a.Geometry.Through is not null || a.Location != location.CustodyPoint))
            throw new Exception("Accepted receipt and dispatch must retain distinct point events rather than invented delivery intervals.");
        var received = assessment.Activities.Single(a => a.Stage == "accepted_receipt");
        var sent = assessment.Activities.Single(a => a.Stage == "dispatch");
        if (received.SourceId != receipt.Id || received.Geometry.From != receipt.AcceptedAt || received.Quantity != "400" || sent.SourceId != dispatch.Id || sent.Quantity != "50")
            throw new Exception("The common event projection must preserve native source identities, instants and quantities.");
        using (var wire = JsonDocument.Parse(JsonSerializer.Serialize(assessment, new JsonSerializerOptions(JsonSerializerDefaults.Web))))
        {
            var geometry = wire.RootElement.GetProperty("activities")[0].GetProperty("geometry");
            if (geometry.GetProperty("kind").GetString() != "transfer" || geometry.GetProperty("through").ValueKind != JsonValueKind.Null)
                throw new Exception("The shared wire geometry must preserve a discrete transfer and explicit absent interval end.");
        }
        if (assessment.Capabilities.Any(c => c.Code.Contains("nomination", StringComparison.Ordinal)) || assessment.Constraints.Any(c => c.State != "within_limit"))
            throw new Exception("A receipt profile must declare its own supported operations without acquiring pipeline nomination behavior.");
        Reject("crude_dispatch_quantity", () => behavior.Assess(input with { Dispatches = [dispatch with { Quantity = Q("151") }] }));
        Reject("crude_receipt_title", () => behavior.Assess(input with { Receipts = [receipt with { Title = CrudeReceiptTitle.NotTransferred }] }));
        Reject("crude_receipt_reference", () => behavior.Assess(input with { Receipts = [receipt with { Grade = R("other_grade") }] }));
    }

    private static void Equal(DeliveryAssessment result, string code, string value)
    {
        var actual = result.Measures.Single(m => m.Code == code).Value;
        if (actual != value) throw new Exception($"Expected {code}={value}, received {actual}.");
    }
    private static void Reject(string code, Action action)
    {
        try { action(); } catch (MarketDataException e) when (e.Code == code) { return; }
        throw new Exception("Expected " + code);
    }
}
