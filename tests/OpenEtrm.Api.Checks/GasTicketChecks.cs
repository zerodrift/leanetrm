using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

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
            Equal(new GasCashRequest("PAY", "consideration", "pay", "RCT", "receipt", "2.5", null, "2027-03-01T00:00:00Z"), r.CashSides[0], "cash side");
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
        Check("each side kind declares every column its view side reports", () =>
        {
            string Ids(string role) => string.Join(",", GasTicket.SideKind(role).Columns.Select(c => c.Id));
            Equal("quantity,market_price,amount", Ids("physical"), "physical columns");
            Equal("quantity,price,amount", Ids("consideration"), "cash columns"); Equal("quantity,price,amount", Ids("fee"), "fee columns");
            Equal("cash", GasTicket.SideKind("consideration").Kind, "consideration kind");
            Equal($"{ScheduleViews.NetCash},{ScheduleViews.PaymentAt}", string.Join(",", GasTicket.Description.TradeColumns.Select(c => "trade." + c.Id)), "trade columns");
        });
        Check("the side caps are the composition contract's", () =>
        {
            Equal((GasCompositionInstrument.MinPhysicalSides, GasCompositionInstrument.MaxPhysicalSides), (GasTicket.SideKind("physical").Min, GasTicket.SideKind("physical").Max), "physical sides");
            Equal(GasCompositionInstrument.MaxCashSides, GasTicket.Description.SideLimits.Single(l => l.Kinds.SequenceEqual(["cash", "fee"])).Max, "cash and fee sides");
            foreach (var role in new[] { "consideration", "fee" }) Equal(GasCompositionInstrument.MaxCashSides, GasTicket.SideKind(role).Max, role + " sides");
        });
        Check("a new gas ticket opens with one physical and one payment side", () =>
            Equal("physical:1,cash:1,fee:0", string.Join(",", GasTicket.Description.Sides.Select(k => $"{k.Kind}:{k.Initial}")), "initial sides"));
        Check("quantities are in the trade's resolved quantity unit, never a unit symbol", () =>
        {
            var fields = GasTicket.Description.TradeFields.Concat(GasTicket.Description.Sides.SelectMany(k => k.Fields)).Where(f => f.Unit is not null).Select(f => $"{f.Id}:{f.Unit}");
            Equal("quantity:quantity", string.Join(",", fields), "fields with a unit");
            var columns = GasTicket.Description.Sides.SelectMany(k => k.Columns).Concat(GasTicket.Description.TradeColumns);
            if (columns.Any(c => c.Unit != (c.Measure == "quantity" ? "quantity" : null))) throw new Exception("Only quantity columns are in the resolved quantity unit.");
        });
        Check("only a booked physical side's quantities are entered in its tab", () =>
        {
            var inputs = GasTicket.Description.Sides.SelectMany(k => k.Columns.Where(c => !c.Input.IsEmpty).Select(c => $"{k.Kind}.{c.Id}:{string.Join("+", c.Input)}"));
            Equal("physical.quantity:booked", string.Join(",", inputs), "input columns");
        });
        Check("a role without a side kind refuses", () =>
        {
            try { GasTicket.SideKind("cash"); } catch (MarketDataException error) when (error.Code == "ticket_description") { return; }
            throw new Exception("Expected ticket_description for an unknown role.");
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
        // A refusal of one value names its side and field, so the ticket shows the reason there without reading the message.
        void Names(string name, TicketDraft draft, string code, string? side, string field)
        {
            try { Map(draft); } catch (TicketFieldException error) when (error.Code == code && error.Side == side && error.Field == field) { passed++; Console.WriteLine($"PASS names the field of {name}"); return; }
            throw new Exception($"Expected {code} on {side}.{field} for {name}.");
        }
        Names("a quantity that is not a plain decimal", Draft(With(Receipt, "quantity", "10,000")), "numeric_format", "RCT", "quantity");
        Names("a fee price that is not a plain decimal", Draft(Receipt, Linked, With(With(Fee, "fee_tariff", null), "fee_price", "0.1x")), "numeric_format", "FEE", "fee_price");
        Names("a missing required side value", Draft(With(Receipt, "product", null)), "ticket_input", "RCT", "product");
        Names("a blank value", Draft(With(Receipt, "product", " ")), "ticket_input", "RCT", "product");
        Names("a missing required trade value", full with { Trade = Trade.Remove("book") }, "ticket_input", null, "book");
        Names("a missing quantity on an unlinked side", Draft(With(Receipt, "quantity", null)), "ticket_input", "RCT", "quantity");
        Names("a quantity on a linked side", Draft(Receipt, With(Linked, "quantity", "5")), "ticket_input", "DLV", "quantity");
        Names("a cash source naming no physical side", Draft(Receipt, With(Cash, "cash_source", "NOPE")), "ticket_input", "PAY", "cash_source");
        Reject("another product", full with { Product = "power" });
        Reject("another version", full with { Version = 2 });
        Reject("a malformed entity revision", full with { Trade = Trade.SetItem("legal_entity", $"{Entity}@x") });
        Reject("an entity without a revision", full with { Trade = Trade.SetItem("counterparty", Counterparty.ToString()) });
        Reject("a malformed book", full with { Trade = Trade.SetItem("book", "not-a-guid") });
        Reject("a draft without trade values", full with { Trade = null! });
        Reject("a draft without sides", full with { Sides = default });
        Reject("a missing side", full with { Sides = [Receipt, null!] });
        Reject("a side without values", Draft(Receipt with { Values = null! }));
        Reject("a side without a kind", Draft(Receipt, Cash with { Kind = null! }));
        Reject("a draft without a product", full with { Product = null! });
        Reject("no draft", null!);

        string Json(GasCompositionRequest request) => JsonSerializer.Serialize(request);
        string Values(TicketDraft draft) => JsonSerializer.Serialize(new { draft.Product, draft.Version, Trade = draft.Trade.OrderBy(v => v.Key), Sides = draft.Sides.Select(s => new { s.Kind, s.Code, Values = s.Values.OrderBy(v => v.Key) }) });
        Check("a booked request rebuilds the draft that booked it", () =>
        {
            var terms = Map(full);
            Equal(Values(full), Values(GasTicket.Draft(terms)), "rebuilt draft");
            Equal(Json(terms), Json(GasTicket.Request(GasTicket.Draft(terms), terms.SnapshotId, terms.KnowledgeMode, terms.OperationId, terms.DefinitionRevisionId)), "round trip");
            var bare = Map(full with { Trade = Trade.Remove("legal_entity").Remove("counterparty") });
            Equal(Json(bare), Json(Map(GasTicket.Draft(bare))), "round trip without parties");
        });
        Check("a request with half a party reference refuses", () =>
        {
            try { GasTicket.Draft(Map(full) with { LegalEntityRevision = null }); } catch (MarketDataException error) when (error.Code == "ticket_input") { return; }
            throw new Exception("Expected ticket_input for an entity without its revision.");
        });

        var catalog = JsonDocument.Parse($$$"""
            {"gasSetupId":"{{{Setup}}}","gasSetupPublication":3,"locations":[
             {"pipeline":"PA","pipelineName":"Pipeline A","zone":"ZA1","zoneName":"Zone A1","deliveryPoint":"P1","deliveryPointName":"Point 1","clock":"fixed_utc","dayStartMinute":540,"product":"firm","productName":"Firm","valuation":{"quote":"Q"}},
             {"pipeline":"PA","pipelineName":"Pipeline A","zone":"ZA1","zoneName":"Zone A1","deliveryPoint":"P2","deliveryPointName":"Point 2","clock":"fixed_utc","dayStartMinute":540,"product":"firm","productName":"Firm","valuation":{"quote":"Q"}},
             {"pipeline":"PA","pipelineName":"Pipeline A","zone":"ZA2","zoneName":"Zone A2","deliveryPoint":"P3","deliveryPointName":"Point 3","clock":"fixed_utc","dayStartMinute":0,"product":"firm","productName":"Firm","valuation":{"quote":"Q"}},
             {"pipeline":"PB","pipelineName":"Pipeline B","zone":"ZB1","zoneName":"Zone B1","deliveryPoint":"P4","deliveryPointName":"Point 4","clock":"fixed_utc","dayStartMinute":60,"product":"firm","productName":"Firm","valuation":{"quote":"Q"}},
             {"pipeline":"PC","pipelineName":"Pipeline C","zone":"ZC1","zoneName":"Zone C1","deliveryPoint":"P5","deliveryPointName":"Point 5","clock":"fixed_utc","dayStartMinute":0,"product":"interruptible","productName":"Interruptible","valuation":null}],
             "tariffs":[{"code":"T1","name":"Usage A","pipeline":"PA","product":"firm","quote":"Q","rate":"0.045","validFrom":"2027-01-01","validTo":"2027-12-31"},
              {"code":"T2","name":"Usage B","pipeline":"PB","product":"firm","quote":"Q","rate":"0.05","validFrom":"2027-01-01","validTo":"2027-12-31"},
              {"code":"T3","name":"Usage A later","pipeline":"PA","product":"firm","quote":"Q","rate":"0.06","validFrom":"2027-01-15","validTo":"2027-12-31"}],
             "books":[{"id":"{{{Book}}}","name":"Synthetic book"}],
             "legalEntities":[{"id":"{{{Entity}}}","revision":4,"code":"E","name":"Synthetic entity"}],
             "counterparties":[{"id":"{{{Counterparty}}}","revision":7,"code":"C","name":"Synthetic counterparty"}]}
            """).RootElement;
        TicketSideDraft A(TicketSideDraft side) => With(With(With(side, "pipeline", "PA"), "zone", "ZA1"), "delivery_point", "P1");
        TicketDraft Open(params TicketSideDraft[] sides) => Draft(sides);
        string Listed(TicketDraft draft, string? side, string field) => string.Join(",", GasTicket.Choices(draft, side, field, catalog).Select(c => c.Value));
        void Refuses(string code, string name, Action action)
        {
            try { action(); } catch (MarketDataException error) when (error.Code == code) { passed++; Console.WriteLine($"PASS {name}"); return; }
            throw new Exception($"Expected {code} for {name}.");
        }
        var fee = With(With(Fee, "fee_source", "RCT"), "fee_tariff", null);
        Check("zones list only the chosen pipeline's zones", () => Equal("ZA1,ZA2", Listed(Open(A(Receipt)), "RCT", "zone"), "zones"));
        Check("points list only the chosen zone's points", () => Equal("P1,P2", Listed(Open(A(Receipt)), "RCT", "delivery_point"), "points"));
        Check("pipelines and products list only valued paths", () => { Equal("PA,PB", Listed(Open(A(Receipt)), "RCT", "pipeline"), "pipelines"); Equal("firm", Listed(Open(A(Receipt)), "RCT", "product"), "products"); });
        Refuses("ticket_input", "zones without a pipeline refuse", () => GasTicket.Choices(Open(With(Receipt, "pipeline", null)), "RCT", "zone", catalog));
        Refuses("ticket_input", "choices for an unknown side refuse", () => GasTicket.Choices(Open(A(Receipt)), "NOPE", "zone", catalog));
        Refuses("ticket_input", "choices for a field of another kind refuse", () => GasTicket.Choices(Open(A(Receipt), Cash), "PAY", "zone", catalog));
        Refuses("ticket_input", "choices under another gas setup refuse", () => GasTicket.Choices(Open(A(Receipt)) with { Trade = Trade.SetItem("gas_setup", Guid.Empty.ToString()) }, "RCT", "zone", catalog));
        Refuses("ticket_input", "side choices without the context's gas setup refuse", () => GasTicket.Choices(Open(A(Receipt)) with { Trade = Trade.Remove("gas_setup") }, "RCT", "zone", catalog));
        Check("side choices without a gas setup read the reference conventions of a context that resolves none", () =>
        {
            var reference = JsonDocument.Parse(catalog.GetRawText().Replace($"\"gasSetupId\":\"{Setup}\",\"gasSetupPublication\":3", "\"gasSetupId\":null,\"gasSetupPublication\":null")).RootElement;
            Equal("ZA1,ZA2", string.Join(",", GasTicket.Choices(Open(A(Receipt)) with { Trade = Trade.Remove("gas_setup") }, "RCT", "zone", reference).Select(c => c.Value)), "reference zones");
            Equal(null, Map(full with { Trade = Trade.Remove("gas_setup") }).GasSetupId, "booked without a setup");
        });
        Refuses("ticket_input", "choices for a field without a list refuse", () => GasTicket.Choices(Open(A(Receipt)), "RCT", "quantity", catalog));
        Check("party choices are exact '<id>@<revision>' references the booking accepts", () =>
        {
            var entity = GasTicket.Choices(full, null, "legal_entity", catalog).Single(); var party = GasTicket.Choices(full, null, "counterparty", catalog).Single();
            Equal($"{Entity}@4", entity.Value, "entity choice"); Equal("Synthetic entity", entity.Label, "entity label");
            var r = Map(full with { Trade = Trade.SetItem("legal_entity", entity.Value).SetItem("counterparty", party.Value) });
            Equal(Entity, r.LegalEntityId, "booked entity"); Equal(7, r.CounterpartyRevision, "booked counterparty revision");
            Equal(Book.ToString(), Listed(full, null, "book"), "books");
            Equal(new TicketChoice(Setup.ToString(), "Published setup 3"), GasTicket.Choices(full, null, "gas_setup", catalog).Single(), "gas setup labelled by its publication");
        });
        Check("side sources list physical codes, never the side itself for a link", () =>
        {
            Equal("DLV", Listed(Open(A(Receipt), A(Linked), Cash), "RCT", "link_source"), "link sources");
            Equal("RCT,DLV", Listed(Open(A(Receipt), A(Linked), Cash), "PAY", "cash_source"), "cash sources");
        });
        Check("usage tariffs list those covering the source side's pipeline, product and term", () => Equal("T1", Listed(Open(A(Receipt), fee), "FEE", "fee_tariff"), "tariffs"));
        Refuses("ticket_input", "usage tariffs on a receipt-stage fee refuse", () => GasTicket.Choices(Open(A(Receipt), With(fee, "fee_stage", "receipt")), "FEE", "fee_tariff", catalog));
        Check("every choice field of the description is answered", () =>
        {
            var draft = Open(A(Receipt), A(Linked), Cash, fee);
            foreach (var kind in GasTicket.Description.Sides)
                foreach (var field in kind.Fields.Where(f => f.Choices is not null))
                    if (GasTicket.Choices(draft, draft.Sides.First(s => s.Kind == kind.Kind).Code, field.Id, catalog).IsEmpty) throw new Exception($"No choices for {field.Id}.");
            foreach (var field in GasTicket.Description.TradeFields.Where(f => f.Choices is not null))
                if (GasTicket.Choices(draft, null, field.Id, catalog).IsEmpty) throw new Exception($"No choices for {field.Id}.");
        });

        Check("a delivery-stage fee looks up its one eligible tariff as the exact reference the booking takes", () =>
        {
            var found = GasTicket.Lookup(Open(A(Receipt), fee), "FEE", "fee_tariff", catalog);
            Equal("T1", found.Value, "tariff code"); Equal(new TicketSource("tariff", "Tariff Usage A"), found.Source, "tariff source");
            var booked = Map(Open(A(Receipt), With(fee, "fee_tariff", found.Value))).CashSides[0];
            Equal(("T1", (string?)null), (booked.TariffCode, booked.Price), "booked tariff without a price");
        });
        Check("no lookup copies a value into a price field", () =>
        {
            var priced = GasTicket.Description.Sides.SelectMany(k => k.Fields).Where(f => f.Id is "cash_price" or "fee_price").ToArray();
            Equal(2, priced.Length, "price fields"); if (priced.Any(f => f.Lookup is not null)) throw new Exception("A price field declares a lookup.");
        });
        Refuses("ticket_lookup", "a tariff lookup on a receipt-stage fee refuses", () => GasTicket.Lookup(Open(A(Receipt), With(fee, "fee_stage", "receipt")), "FEE", "fee_tariff", catalog));
        Refuses("ticket_lookup", "a tariff lookup beside a manual fee price refuses", () => GasTicket.Lookup(Open(A(Receipt), With(fee, "fee_price", "0.045")), "FEE", "fee_tariff", catalog));
        Refuses("ticket_lookup", "a tariff lookup with no eligible tariff refuses", () => GasTicket.Lookup(Open(With(A(Receipt), "delivery_from", "2026-12-01"), fee), "FEE", "fee_tariff", catalog));
        Refuses("ticket_lookup", "a tariff lookup with several eligible tariffs refuses", () => GasTicket.Lookup(Open(With(A(Receipt), "delivery_from", "2027-01-15"), fee), "FEE", "fee_tariff", catalog));
        Check("a booked request reopens as its draft only when the draft maps back to it exactly", () =>
        {
            Equal(Values(full), Values(GasTicket.Reopen(Map(full))), "reopened draft");
        });
        Refuses("ticket_input", "booked terms on another convention do not reopen", () => GasTicket.Reopen(Map(full) with { Convention = "physical_gas_fixed_quantity_v3" }));
        Refuses("ticket_input", "booked terms without an explicit discounting policy do not reopen", () => GasTicket.Reopen(Map(full) with { Discounting = null }));
        Check("the gas day start comes from the side's point", () =>
        {
            var found = GasTicket.Lookup(Open(A(Receipt)), "RCT", "gas_day_start", catalog);
            Equal("09:00", found.Value, "gas day start"); Equal(new TicketSource("reference", "Point 1 · fixed_utc"), found.Source, "gas day source");
        });
        Refuses("ticket_lookup", "a gas day start without a point refuses", () => GasTicket.Lookup(Open(With(A(Receipt), "delivery_point", null)), "RCT", "gas_day_start", catalog));
        Check("payment lists the sides charged on the gas", () => Equal("PAY,FEE", GasTicket.Lookup(Open(A(Receipt), Cash, fee), "RCT", "payment", catalog).Value, "paying sides"));
        Refuses("ticket_lookup", "payment with no charged side refuses", () => GasTicket.Lookup(Open(A(Receipt)), "RCT", "payment", catalog));
        Refuses("ticket_input", "a lookup on a field without one refuses", () => GasTicket.Lookup(Open(A(Receipt)), "RCT", "zone", catalog));
        Check("a market price probe prices the side's own path and term", () =>
        {
            var probe = GasTicket.PriceProbe(Open(A(Receipt), Cash), "RCT", catalog, Snapshot, Definition);
            Equal(Setup, probe.GasSetupId, "probe setup"); Equal(0, probe.CashSides.Length, "probe cash");
            var side = probe.PhysicalSides.Single();
            Equal(("RCT", "PA", "ZA1", "P1", "firm", "2027-01-01", "2027-02-01"), (side.Code, side.Pipeline, side.Zone, side.DeliveryPoint, side.Product, side.DeliveryFrom, side.DeliveryTo), "probe path");
            Equal(new TicketSource("market", "Point 1 · close 2020-12-18"), GasTicket.MarketPrice(Open(A(Receipt)), "RCT", catalog, "3.1", new(2020, 12, 18)).Source, "market source");
        });
        Refuses("ticket_lookup", "a market price on a path missing from the context refuses", () => GasTicket.PriceProbe(Open(With(A(Receipt), "delivery_point", "P9")), "RCT", catalog, Snapshot, Definition));
        Refuses("ticket_lookup", "a market price without a delivery term refuses", () => GasTicket.PriceProbe(Open(With(A(Receipt), "delivery_to", null)), "RCT", catalog, Snapshot, Definition));
        Console.WriteLine($"{passed} gas ticket checks passed.");
    }
}
