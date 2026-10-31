using System.Collections.Immutable;
using System.Reflection;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasTicketChecks
{
    static readonly Guid Book = Guid.Parse("00000000-0000-0000-0000-0000000000b1"), Setup = Guid.Parse("00000000-0000-0000-0000-0000000000a1"),
        Entity = Guid.Parse("00000000-0000-0000-0000-0000000000e1"), Counterparty = Guid.Parse("00000000-0000-0000-0000-0000000000c1"),
        Snapshot = Guid.Parse("00000000-0000-0000-0000-000000000051"), Operation = Guid.Parse("00000000-0000-0000-0000-000000000001"), Definition = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    static ImmutableDictionary<string, string> V(params (string Id, string Value)[] values) => values.ToImmutableDictionary(v => v.Id, v => v.Value);
    static TicketSideDraft Side(string kind, string code, params (string Id, string Value)[] values) => new(kind, code, V(values));
    static readonly ImmutableDictionary<string, string> Trade = V(("book", Book.ToString()), ("legal_entity", $"{Entity}@4"), ("counterparty", $"{Counterparty}@7"), ("gas_setup", Setup.ToString()));
    static TicketSideDraft Receipt => Side("physical", "RCT", ("direction", "receive"), ("delivery_from", "2027-01-01"), ("delivery_to", "2027-02-01"), ("product", "firm"), ("quantity", "10000"), ("quantity_basis", "per_operating_day"), ("pipeline", "PIPE"), ("zone", "Z1"), ("delivery_point", "P1"));
    static TicketSideDraft Linked => Side("physical", "DLV", ("direction", "deliver"), ("delivery_from", "2027-01-01"), ("delivery_to", "2027-02-01"), ("product", "firm"), ("quantity_basis", "per_operating_day"), ("pipeline", "PIPE"), ("zone", "Z1"), ("delivery_point", "P2"), ("link_source", "RCT"), ("link_formula", "gross_less_fraction"), ("link_fraction", "0.02"));
    static TicketSideDraft Cash => Side("cash", "PAY", ("cash_direction", "pay"), ("cash_source", "RCT"), ("cash_stage", "receipt"), ("cash_price", "2.5"), ("cash_payment_at", "2027-03-01T00:00:00Z"));
    static TicketSideDraft Fee => Side("fee", "FEE", ("fee_direction", "pay"), ("fee_source", "DLV"), ("fee_stage", "delivery"), ("fee_tariff", "TARIFF"), ("fee_payment_at", "2027-03-01T00:00:00Z"));
    static TicketDraft Draft(params TicketSideDraft[] sides) => new("physical_gas", 1, Trade, [..sides]);
    static GasCompositionRequest Map(TicketDraft draft) => GasTicket.Request(draft, Snapshot, "current", Operation, Definition);

    public static void Run()
    {
        var passed = 0;
        void Check(string name, Action check) { check(); passed++; Console.WriteLine($"PASS {name}"); }
        void Reject(string name, TicketDraft draft)
        {
            try { Map(draft); } catch (MarketDataException error) when (error.Code == "ticket_input") { passed++; Console.WriteLine($"PASS rejects {name}"); return; }
            throw new Exception($"Expected ticket_input for {name}.");
        }
        void Equal(object? expected, object? actual, string what) { if (!Equals(expected, actual)) throw new Exception($"{what}: expected '{expected}', got '{actual}'."); }
        var full = Draft(Receipt, Linked, Cash, Fee);

        Check("the gas description is valid", () => TicketDescriptions.Validate(GasTicket.Description));
        Check("every editable field reaches the booking request", () =>
        {
            var r = Map(full);
            Equal(Book, r.BookId, "book"); Equal(Setup, r.GasSetupId, "gas setup");
            Equal(Entity, r.LegalEntityId, "legal entity"); Equal(4, r.LegalEntityRevision, "legal entity revision");
            Equal(Counterparty, r.CounterpartyId, "counterparty"); Equal(7, r.CounterpartyRevision, "counterparty revision");
            Equal(2, r.PhysicalSides.Length, "physical count"); Equal(2, r.CashSides.Length, "cash count");
            var p = r.PhysicalSides[0];
            Equal(new GasPhysicalRequest("RCT", "PIPE", "Z1", "P1", "firm", "receive", "2027-01-01", "2027-02-01", "per_operating_day", "10000", null), p, "receipt side");
            var l = r.PhysicalSides[1];
            Equal(new GasQuantityLinkRequest("RCT", "gross_less_fraction", "0.02"), l.Link, "link"); Equal(null, l.Quantity, "linked quantity");
            Equal(new GasCashRequest("PAY", "cash", "pay", "RCT", "receipt", "2.5", null, "2027-03-01T00:00:00Z"), r.CashSides[0], "cash side");
            Equal(new GasCashRequest("FEE", "fee", "pay", "DLV", "delivery", null, "TARIFF", "2027-03-01T00:00:00Z"), r.CashSides[1], "fee side");
        });
        Check("every request property is reachable from one field", () =>
        {
            string[] structural = ["GasPhysicalRequest.Code", "GasPhysicalRequest.Link", "GasCashRequest.Code", "GasCashRequest.Role", "GasCompositionRequest.SnapshotId", "GasCompositionRequest.Convention", "GasCompositionRequest.KnowledgeMode",
                "GasCompositionRequest.Profile", "GasCompositionRequest.OperationId", "GasCompositionRequest.DefinitionRevisionId", "GasCompositionRequest.Discounting", "GasCompositionRequest.PhysicalSides", "GasCompositionRequest.CashSides"];
            var parameters = new[] { typeof(GasPhysicalRequest), typeof(GasCashRequest), typeof(GasQuantityLinkRequest), typeof(GasCompositionRequest) }
                .SelectMany(t => t.GetConstructors().Single().GetParameters().Select(x => $"{t.Name}.{x.Name}")).Where(x => !structural.Contains(x)).Order().ToArray();
            string[] reached = ["GasPhysicalRequest.Pipeline:pipeline", "GasPhysicalRequest.Zone:zone", "GasPhysicalRequest.DeliveryPoint:delivery_point", "GasPhysicalRequest.Product:product", "GasPhysicalRequest.Direction:direction",
                "GasPhysicalRequest.DeliveryFrom:delivery_from", "GasPhysicalRequest.DeliveryTo:delivery_to", "GasPhysicalRequest.QuantityBasis:quantity_basis", "GasPhysicalRequest.Quantity:quantity",
                "GasQuantityLinkRequest.Source:link_source", "GasQuantityLinkRequest.Formula:link_formula", "GasQuantityLinkRequest.Fraction:link_fraction",
                "GasCashRequest.Direction:cash_direction|fee_direction", "GasCashRequest.Source:cash_source|fee_source", "GasCashRequest.Stage:cash_stage|fee_stage", "GasCashRequest.Price:cash_price|fee_price",
                "GasCashRequest.TariffCode:fee_tariff", "GasCashRequest.PaymentAt:cash_payment_at|fee_payment_at",
                "GasCompositionRequest.BookId:book", "GasCompositionRequest.GasSetupId:gas_setup", "GasCompositionRequest.LegalEntityId:legal_entity", "GasCompositionRequest.LegalEntityRevision:legal_entity",
                "GasCompositionRequest.CounterpartyId:counterparty", "GasCompositionRequest.CounterpartyRevision:counterparty"];
            var names = reached.Select(x => x.Split(':')[0]).Order().ToArray();
            if (!parameters.SequenceEqual(names)) throw new Exception($"Request properties and the field map differ: {string.Join(", ", parameters.Except(names).Concat(names.Except(parameters)))}.");
            var editable = GasTicket.Description.TradeFields.Concat(GasTicket.Description.Sides.SelectMany(s => s.Fields)).Where(f => f.State == "editable").Select(f => f.Id).Order().ToArray();
            // Legal entity and counterparty each fill an id and a revision from one field.
            var mapped = reached.SelectMany(x => x.Split(':')[1].Split('|')).Distinct().Order().ToArray();
            if (!editable.SequenceEqual(mapped)) throw new Exception($"Editable fields and the field map differ: {string.Join(", ", editable.Except(mapped).Concat(mapped.Except(editable)))}.");
        });
        Check("profile and discounting are explicit", () => { var r = Map(full); Equal("fixed_gas_composition_v1", r.Profile, "profile"); Equal(false, r.Discounting, "discounting"); Equal(PhysicalGasStore.Convention, r.Convention, "convention"); Equal("current", r.KnowledgeMode, "knowledge"); });
        Check("cash and fee sides keep draft order", () => Equal("FEE", Map(Draft(Receipt, Linked, Fee, Cash)).CashSides[0].Code, "first cash side"));

        TicketSideDraft With(TicketSideDraft side, string id, string? value) => side with { Values = value is null ? side.Values.Remove(id) : side.Values.SetItem(id, value) };
        Reject("an unknown field id", Draft(With(Receipt, "colour", "blue")));
        Reject("a value for a derived field", Draft(Receipt, With(Cash, "cash_start", "2027-01-01")));
        Reject("a value for an info field", Draft(With(Receipt, "price", "3")));
        Reject("a value for a field of another side kind", Draft(Receipt, With(Cash, "fee_tariff", "T")));
        Reject("a missing required side value", Draft(With(Receipt, "product", null)));
        Reject("a blank value", Draft(With(Receipt, "product", " ")));
        Reject("a missing required trade value", full with { Trade = Trade.Remove("book") });
        Reject("an unknown trade field", full with { Trade = Trade.Add("note", "x") });
        Reject("a value for an info trade field", full with { Trade = Trade.Add("trade_date", "2027-01-01") });
        Reject("a missing quantity on an unlinked side", Draft(With(Receipt, "quantity", null)));
        Reject("a quantity on a linked side", Draft(Receipt, With(Linked, "quantity", "5")));
        Reject("a link formula without a source", Draft(Receipt, With(With(Linked, "link_source", null), "quantity", "5")));
        Reject("a link source without a formula", Draft(Receipt, With(Linked, "link_formula", null)));
        Reject("a link source without a fraction", Draft(Receipt, With(Linked, "link_fraction", null)));
        Reject("a link source naming no physical side", Draft(Receipt, With(Linked, "link_source", "NOPE")));
        Reject("a cash source naming no physical side", Draft(Receipt, With(Cash, "cash_source", "NOPE")));
        Reject("no physical side", Draft(Cash));
        Reject("five physical sides", Draft(Enumerable.Range(0, 5).Select(n => With(Receipt, "quantity", "1") with { Code = $"S{n}" }).ToArray()));
        Reject("five cash sides", Draft([Receipt, ..Enumerable.Range(0, 5).Select(n => Cash with { Code = $"C{n}" })]));
        Reject("five cash and fee sides together", Draft([Receipt, ..Enumerable.Range(0, 3).Select(n => Cash with { Code = $"C{n}" }), ..Enumerable.Range(0, 2).Select(n => Fee with { Code = $"F{n}", Values = Fee.Values.SetItem("fee_source", "RCT") })]));
        Reject("an unknown side kind", Draft(Receipt, Side("swap", "X")));
        Reject("a repeated side code", Draft(Receipt, Cash with { Code = "RCT" }));
        Reject("a blank side code", Draft(Receipt with { Code = "" }));
        Reject("another product", full with { Product = "power" });
        Reject("another version", full with { Version = 2 });
        Reject("a malformed entity revision", full with { Trade = Trade.SetItem("legal_entity", $"{Entity}@x") });
        Reject("an entity without a revision", full with { Trade = Trade.SetItem("counterparty", Counterparty.ToString()) });
        Reject("a malformed book", full with { Trade = Trade.SetItem("book", "not-a-guid") });
        Console.WriteLine($"{passed} gas ticket checks passed.");
    }
}
