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
    static TicketSideDraft Indexed => Side("floating", "IDX", ("floating_direction", "pay"), ("floating_source", "RCT"), ("floating_stage", "delivery"),
        ("floating_index", "I"), ("floating_forecast_curve", "assigned"), ("floating_spread", "0"), ("floating_payment_day", "10"));
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

        Check("entry choices suggest sole required values and explicit gas defaults without inventing optional links", () =>
        {
            Equal("PIPE", GasTicket.ChoiceSet(full, "RCT", "pipeline", [new("PIPE", "Pipeline")]).StartingValue?.Value, "sole pipeline");
            Equal("per_operating_day", GasTicket.ChoiceSet(full, "RCT", "quantity_basis", [new("per_operating_day", "Daily"),new("period_total_equal_days", "Period")]).StartingValue?.Value, "quantity basis");
            Equal("pay", GasTicket.ChoiceSet(full, "PAY", "cash_direction", [new("pay", "Pay"),new("receive", "Receive")]).StartingValue?.Value, "purchase payment direction");
            Equal(null, GasTicket.ChoiceSet(full, "DLV", "link_source", [new("RCT", "Receipt")]).StartingValue, "optional quantity link");
            Equal(null, GasTicket.ChoiceSet(full, "RCT", "pipeline", [new("A", "A"),new("B", "B")]).StartingValue, "ambiguous pipeline");
        });

        Check("the gas description is valid", () => TicketDescriptions.Validate(GasTicket.Description));
        Check("payment direction can be chosen before its source and refreshes its suggestion afterwards",()=>
        {
            foreach(var kind in new[]{"cash","fee","floating"})
            {
                var field=GasTicket.Description.Sides.Single(s=>s.Kind==kind).Fields.Single(f=>f.Id==$"{kind}_direction");
                Equal(0,field.DependsOn.Length,"independent direction choices");
                Equal($"{kind}_source,direction",string.Join(",",field.RefreshOn),"source-sensitive suggestion");
            }
        });
        Check("the trade date is assigned when booked, never looked up", () =>
        {
            var tradeDate = GasTicket.Description.TradeFields.Single(f => f.Id == "trade_date");
            Equal(null, tradeDate.Lookup, "lookup"); Equal("Assigned when booked: the UTC date of execution.", tradeDate.Rule, "rule");
        });
        Check("a delivery end reads as exclusive", () => Equal("To · exclusive", GasTicket.Description.Sides.Single(s => s.Kind == "physical").Fields.Single(f => f.Id == "delivery_to").Label, "label"));
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
        Check("an exact forecast source survives ticket mapping and reopening",()=>
        {
            var source=new CustomCurveProjectionInput(Guid.NewGuid(),Guid.NewGuid(),2,"output",null,
                CustomCurveProjectionBinding.ContainingDeliverySelection,CustomCurveProjectionBinding.SupportedImplementation);
            var selected=Receipt with{Values=Receipt.Values.Add("forecast_curve",GasTicket.ForecastValue(source))};
            var mapped=Map(Draft(selected,Cash));
            Equal(source,mapped.PhysicalSides[0].ForecastCurve,"forecast source");
            Equal(GasTicket.ForecastValue(source),GasTicket.Reopen(mapped).Sides[0].Values["forecast_curve"],"reopened source");
        });
        Check("every request property is reachable from one field", () =>
        {
            string[] structural = ["GasPhysicalRequest.Code", "GasPhysicalRequest.Link", "GasCashRequest.Code", "GasCashRequest.Role", "GasCompositionRequest.SnapshotId", "GasCompositionRequest.Convention", "GasCompositionRequest.KnowledgeMode",
                "GasCompositionRequest.Profile", "GasCompositionRequest.OperationId", "GasCompositionRequest.DefinitionRevisionId", "GasCompositionRequest.Discounting", "GasCompositionRequest.PhysicalSides", "GasCompositionRequest.CashSides",
                "GasCompositionRequest.TradeId", "GasCompositionRequest.BindingId", "GasCompositionRequest.ExpectedRevision", "GasCompositionRequest.Reason", "GasCompositionRequest.AmendmentPolicy",
                "GasCashRequest.Indexed", "GasIndexedCashRequest.ObservationConvention", "GasIndexedCashRequest.PaymentConvention"];
            var parameters = new[] { typeof(GasPhysicalRequest), typeof(GasCashRequest), typeof(GasQuantityLinkRequest), typeof(GasCompositionRequest), typeof(GasIndexedCashRequest) }
                .SelectMany(t => t.GetConstructors().Single().GetParameters().Select(x => $"{t.Name}.{x.Name}")).Where(x => !structural.Contains(x)).Order().ToArray();
            string[] reached = ["GasPhysicalRequest.Pipeline:pipeline", "GasPhysicalRequest.Zone:zone", "GasPhysicalRequest.DeliveryPoint:delivery_point", "GasPhysicalRequest.Product:product", "GasPhysicalRequest.Direction:direction",
                "GasPhysicalRequest.DeliveryFrom:delivery_from", "GasPhysicalRequest.DeliveryTo:delivery_to", "GasPhysicalRequest.QuantityBasis:quantity_basis", "GasPhysicalRequest.Quantity:quantity",
                "GasQuantityLinkRequest.Source:link_source", "GasQuantityLinkRequest.Formula:link_formula", "GasQuantityLinkRequest.Fraction:link_fraction",
                "GasCashRequest.Direction:cash_direction|fee_direction|floating_direction", "GasCashRequest.Source:cash_source|fee_source|floating_source", "GasCashRequest.Stage:cash_stage|fee_stage|floating_stage", "GasCashRequest.Price:cash_price|fee_price",
                "GasCashRequest.TariffCode:fee_tariff", "GasCashRequest.PaymentAt:cash_payment_at|fee_payment_at",
                "GasCompositionRequest.BookId:book", "GasCompositionRequest.GasSetupId:gas_setup", "GasCompositionRequest.LegalEntityId:legal_entity", "GasCompositionRequest.LegalEntityRevision:legal_entity",
                "GasPhysicalRequest.ForecastCurve:forecast_curve", "GasCompositionRequest.CounterpartyId:counterparty", "GasCompositionRequest.CounterpartyRevision:counterparty",
                "GasIndexedCashRequest.Index:floating_index", "GasIndexedCashRequest.ForecastCurve:floating_forecast_curve", "GasIndexedCashRequest.Spread:floating_spread", "GasIndexedCashRequest.PaymentDay:floating_payment_day"];
            var names = reached.Select(x => x.Split(':')[0]).Order().ToArray();
            if (!parameters.SequenceEqual(names)) throw new Exception($"Request properties and the field map differ: {string.Join(", ", parameters.Except(names).Concat(names.Except(parameters)))}.");
            var editable = GasTicket.Description.TradeFields.Concat(GasTicket.Description.Sides.SelectMany(s => s.Fields)).Where(f => f.State == "editable").Select(f => f.Id).Order().ToArray();
            // Legal entity and counterparty each fill an id and a revision from one field.
            var mapped = reached.SelectMany(x => x.Split(':')[1].Split('|')).Distinct().Order().ToArray();
            if (!editable.SequenceEqual(mapped)) throw new Exception($"Editable fields and the field map differ: {string.Join(", ", editable.Except(mapped).Concat(mapped.Except(editable)))}.");
        });
        Check("each side kind declares every column its view side reports", () =>
        {
            string Ids(string role) => string.Join(",", TicketDescriptions.ViewColumns(GasTicket.SideKind(role)).Select(c => c.Item1));
            Equal("quantity,market_price,amount", Ids("physical"), "physical columns");
            Equal("quantity,price,amount", Ids("consideration"), "cash columns"); Equal("quantity,price,amount", Ids("fee"), "fee columns");
            Equal("cash", GasTicket.SideKind("consideration").Kind, "consideration kind");
            Equal($"{ScheduleViews.NetCash},{ScheduleViews.PaymentAt}", string.Join(",", GasTicket.Description.TradeColumns.Select(c => "trade." + c.Id)), "trade columns");
        });
        Check("the side caps are the composition contract's", () =>
        {
            Equal((GasCompositionInstrument.MinPhysicalSides, GasCompositionInstrument.MaxPhysicalSides), (GasTicket.SideKind("physical").Min, GasTicket.SideKind("physical").Max), "physical sides");
            Equal(GasCompositionInstrument.MaxCashSides, GasTicket.Description.SideLimits.Single(l => l.Kinds.SequenceEqual(["cash", "fee", "floating"])).Max, "cash and fee sides");
            foreach (var role in new[] { "consideration", "fee", "indexed_consideration" }) Equal(GasCompositionInstrument.MaxCashSides, GasTicket.SideKind(role).Max, role + " sides");
        });
        Check("a new gas ticket opens with one physical and one payment side", () =>
            Equal("physical:1,cash:1,fee:0,floating:0", string.Join(",", GasTicket.Description.Sides.Select(k => $"{k.Kind}:{k.Initial}")), "initial sides"));
        Check("quantities are in the trade's resolved quantity unit, never a unit symbol", () =>
        {
            var fields = GasTicket.Description.TradeFields.Concat(GasTicket.Description.Sides.SelectMany(k => k.Fields)).Where(f => f.Unit is not null).Select(f => $"{f.Id}:{f.Unit}");
            Equal("quantity:quantity", string.Join(",", fields), "fields with a unit");
            var columns = GasTicket.Description.Sides.SelectMany(k => k.Columns).Concat(GasTicket.Description.TradeColumns).Where(c => c.Soon is null);
            if (columns.Any(c => c.Unit != (c.Measure == "quantity" ? "quantity" : null))) throw new Exception("Only quantity columns are in the resolved quantity unit.");
        });
        Check("only a booked physical side's quantities are entered in its tab", () =>
        {
            var inputs = GasTicket.Description.Sides.SelectMany(k => k.Columns.Where(c => !c.Input.IsEmpty).Select(c => $"{k.Kind}.{c.Id}:{string.Join("+", c.Input)}"));
            Equal("physical.quantity:booked", string.Join(",", inputs), "input columns");
        });
        Check("the gas description draws the owner's coming-soon set, and none of it is booked", () =>
        {
            string Soon(IEnumerable<TicketField> fields) => string.Join(",", fields.Where(f => f.State == "soon").Select(f => f.Id));
            Equal("force_majeure,credit_terms", Soon(GasTicket.Description.TradeFields), "soon trade fields");
            Equal("swing_daily_min,swing_daily_max,swing_range,swing_notice_deadline,nomination_cycle,nominating_party,heat_content,quality_spec", Soon(GasTicket.SideKind("physical").Fields), "soon physical fields");
            Equal("", string.Join(",", GasTicket.Description.Sides.Where(k => k.Soon is not null).Select(k => k.Kind)), "soon side kinds");
            Equal("volume", string.Join(",", GasTicket.Description.Sides.SelectMany(k => k.Columns).Where(c => c.Soon is not null).Select(c => c.Id)), "soon columns");
            Equal("imbalance", string.Join(",", GasTicket.Description.Tabs.Where(t => t.Soon is not null).Select(t => t.Id)), "soon tabs");
            if (TicketDescriptions.ViewColumns(GasTicket.SideKind("physical")).Any(c => c.Item1 == "volume")) throw new Exception("The soon volume column reached the schedule view.");
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
        Reject("a value for a coming-soon field", Draft(With(Receipt, "heat_content", "1037")));
        Reject("a value for a coming-soon trade field", full with { Trade = Trade.Add("credit_terms", "net 30") });
        Reject("a coming-soon side kind", Draft(Receipt, Cash, Side("floating", "FLT")));
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
        Check("indexed payment terms and their independent forecast round trip",()=>
        {
            var forecast=new CustomCurveProjectionInput(Guid.NewGuid(),Guid.NewGuid(),3,"output",null,CustomCurveProjectionBinding.ContainingDeliverySelection,CustomCurveProjectionBinding.SupportedImplementation);
            var draft=Draft(Receipt,Indexed with{Values=Indexed.Values.SetItem("floating_forecast_curve",GasTicket.ForecastValue(forecast))});
            var request=Map(draft);var cash=request.CashSides.Single();
            Equal(GasCompositionStore.IndexedProfile,request.Profile,"profile");
            Equal(new GasIndexedCashRequest("I",GasIndexedCash.Observation,GasIndexedCash.Payment,10,"0",forecast),cash.Indexed,"indexed terms");
            Equal(null,cash.Price,"no fixed price");Equal(null,cash.PaymentAt,"no scalar payment date");
            Equal(Values(draft),Values(GasTicket.Reopen(request)),"reopened indexed draft");
        });
        Check("indexed JSON requires explicit nullable forecast and refuses undeclared policies",()=>
        {
            var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var text=JsonSerializer.Serialize(new GasIndexedCashRequest("I",GasIndexedCash.Observation,GasIndexedCash.Payment,10,"0",null),options);
            Equal(null,JsonSerializer.Deserialize<GasIndexedCashRequest>(text,options)!.ForecastCurve,"explicit null");
            foreach(var bad in new[]{text.Replace(",\"forecastCurve\":null",""),text.Replace("}",",\"fallback\":\"current\"}")})
            {
                try{JsonSerializer.Deserialize<GasIndexedCashRequest>(bad,options);}catch(JsonException){continue;}
                throw new Exception("An incomplete or unknown indexed pricing policy was admitted.");
            }
        });
        Check("suggested market price uses exact quote precision with ties to even",()=>
        {
            Equal("3.14",GasTicketStartingValues.Price("629/200",ExactDecimal.Parse("0.01")),"tie down");
            Equal("3.16",GasTicketStartingValues.Price("631/200",ExactDecimal.Parse("0.01")),"tie up");
            Equal("-3.14",GasTicketStartingValues.Price("-629/200",ExactDecimal.Parse("0.01")),"negative tie");
        });
        Reject("invalid indexed payment day",Draft(Receipt,Indexed with{Values=Indexed.Values.SetItem("floating_payment_day","29")}));
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
             {"pipeline":"PA","pipelineName":"Pipeline A","zone":"ZA1","zoneName":"Zone A1","deliveryPoint":"P1","deliveryPointName":"Point 1","clock":"fixed_utc","dayStartMinute":540,"product":"firm","productName":"Firm","valuation":{"quote":"Q","index":"I"}},
             {"pipeline":"PA","pipelineName":"Pipeline A","zone":"ZA1","zoneName":"Zone A1","deliveryPoint":"P2","deliveryPointName":"Point 2","clock":"fixed_utc","dayStartMinute":540,"product":"firm","productName":"Firm","valuation":{"quote":"Q","index":"I"}},
             {"pipeline":"PA","pipelineName":"Pipeline A","zone":"ZA2","zoneName":"Zone A2","deliveryPoint":"P3","deliveryPointName":"Point 3","clock":"fixed_utc","dayStartMinute":0,"product":"firm","productName":"Firm","valuation":{"quote":"Q","index":"I"}},
             {"pipeline":"PB","pipelineName":"Pipeline B","zone":"ZB1","zoneName":"Zone B1","deliveryPoint":"P4","deliveryPointName":"Point 4","clock":"fixed_utc","dayStartMinute":60,"product":"firm","productName":"Firm","valuation":{"quote":"Q","index":"I"}},
             {"pipeline":"PC","pipelineName":"Pipeline C","zone":"ZC1","zoneName":"Zone C1","deliveryPoint":"P5","deliveryPointName":"Point 5","clock":"fixed_utc","dayStartMinute":0,"product":"interruptible","productName":"Interruptible","valuation":null}],
             "tariffs":[{"code":"T1","name":"Usage A","pipeline":"PA","product":"firm","quote":"Q","rate":"0.045","validFrom":"2027-01-01","validTo":"2027-12-31"},
              {"code":"T2","name":"Usage B","pipeline":"PB","product":"firm","quote":"Q","rate":"0.05","validFrom":"2027-01-01","validTo":"2027-12-31"},
              {"code":"T3","name":"Usage A later","pipeline":"PA","product":"firm","quote":"Q","rate":"0.06","validFrom":"2027-01-15","validTo":"2027-12-31"}],
             "books":[{"id":"{{{Book}}}","name":"Synthetic book"}],
             "legalEntities":[{"id":"{{{Entity}}}","revision":4,"code":"E","name":"Synthetic entity"}],
             "counterparties":[{"id":"{{{Counterparty}}}","revision":7,"code":"C","name":"Synthetic counterparty"}]}
            """).RootElement;
        var referenceCatalog=System.Text.Json.Nodes.JsonNode.Parse(catalog.GetRawText())!;
        referenceCatalog["physicalReferences"]=referenceCatalog["locations"]!.DeepClone();
        catalog=JsonSerializer.SerializeToElement(referenceCatalog);
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
        Check("physical choices retain unpriced reference paths", () => { Equal("PA,PB,PC", Listed(Open(A(Receipt)), "RCT", "pipeline"), "pipelines"); Equal("firm,interruptible", Listed(Open(A(Receipt)), "RCT", "product"), "products"); });
        Refuses("ticket_input", "zones without a pipeline refuse", () => GasTicket.Choices(Open(With(Receipt, "pipeline", null)), "RCT", "zone", catalog));
        Refuses("ticket_input", "choices for an unknown side refuse", () => GasTicket.Choices(Open(A(Receipt)), "NOPE", "zone", catalog));
        Refuses("ticket_input", "choices for a field of another kind refuse", () => GasTicket.Choices(Open(A(Receipt), Cash), "PAY", "zone", catalog));
        Check("physical reference choices do not select a pricing setup", () => Equal("ZA1,ZA2", string.Join(",",GasTicket.Choices(Open(A(Receipt)) with { Trade = Trade.SetItem("gas_setup", Guid.Empty.ToString()) }, "RCT", "zone", catalog).Select(c=>c.Value)), "reference zones"));
        Check("physical references are available before pricing setup is chosen", () => Equal("ZA1,ZA2", string.Join(",",GasTicket.Choices(Open(A(Receipt)) with { Trade = Trade.Remove("gas_setup") }, "RCT", "zone", catalog).Select(c=>c.Value)), "reference zones"));
        Check("side choices without a gas setup read the reference conventions of a context that resolves none", () =>
        {
            var reference = JsonDocument.Parse(catalog.GetRawText().Replace($"\"gasSetupId\":\"{Setup}\",\"gasSetupPublication\":3", "\"gasSetupId\":null,\"gasSetupPublication\":null")).RootElement;
            Equal("ZA1,ZA2", string.Join(",", GasTicket.Choices(Open(A(Receipt)) with { Trade = Trade.Remove("gas_setup") }, "RCT", "zone", reference).Select(c => c.Value)), "reference zones");
            Equal(null, Map(full with { Trade = Trade.Remove("gas_setup") }).GasSetupId, "booked without a setup");
        });
        // Preview and booking hold a draft to the setup its context resolves, as choices and lookups do; the refusal names the trade's setup field.
        void Pins(string name, Guid? chosen, Guid? resolved)
        {
            try { GasTicket.RequireSetup(chosen, resolved); } catch (TicketFieldException error) when (error.Code == "ticket_input" && error.Side is null && error.Field == "gas_setup") { passed++; Console.WriteLine($"PASS {name}"); return; }
            throw new Exception($"Expected ticket_input on gas_setup for {name}.");
        }
        Pins("a draft naming a superseded gas setup refuses on the setup field", Setup, Guid.Empty);
        Pins("a draft without the setup its context resolves refuses on the setup field", null, Setup);
        Pins("a draft naming a setup where the context resolves none refuses on the setup field", Setup, null);
        Check("a draft naming the resolved setup, or none where none resolves, is held to nothing more", () => { GasTicket.RequireSetup(Setup, Setup); GasTicket.RequireSetup(null, null); });
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
            var draft = Open(A(Receipt), A(Linked), Cash, fee, Indexed);
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
        Check("fixed price starting values are visible suggestions while fee prices stay explicit", () =>
        {
            var priced = GasTicket.Description.Sides.SelectMany(k => k.Fields).Where(f => f.Id is "cash_price" or "fee_price").ToArray();
            Equal(2, priced.Length, "price fields");Equal("entry_price",priced.Single(f=>f.Id=="cash_price").Lookup,"fixed price suggestion");Equal(null,priced.Single(f=>f.Id=="fee_price").Lookup,"fee price");
        });
        Refuses("ticket_lookup", "a tariff lookup on a receipt-stage fee refuses", () => GasTicket.Lookup(Open(A(Receipt), With(fee, "fee_stage", "receipt")), "FEE", "fee_tariff", catalog));
        Refuses("ticket_lookup", "a tariff lookup beside a manual fee price refuses", () => GasTicket.Lookup(Open(A(Receipt), With(fee, "fee_price", "0.045")), "FEE", "fee_tariff", catalog));
        Refuses("ticket_lookup", "a tariff lookup with no eligible tariff refuses", () => GasTicket.Lookup(Open(With(A(Receipt), "delivery_from", "2026-12-01"), fee), "FEE", "fee_tariff", catalog));
        Refuses("ticket_lookup", "a tariff lookup with several eligible tariffs refuses", () => GasTicket.Lookup(Open(With(A(Receipt), "delivery_from", "2027-01-15"), fee), "FEE", "fee_tariff", catalog));
        Check("a booked request reopens as its draft only when the draft maps back to it exactly", () =>
        {
            Equal(Values(full), Values(GasTicket.Reopen(Map(full))), "reopened draft");
        });
        Check("amended terms reopen without putting command metadata into the economic draft", () =>
        {
            var terms = Map(full) with { TradeId = Guid.NewGuid(), BindingId = Guid.NewGuid(), ExpectedRevision = 1,
                Reason = "Synthetic correction", AmendmentPolicy = "future_gas_composition_replace_v1" };
            Equal(Values(full), Values(GasTicket.Reopen(terms)), "amended economic draft");
        });
        Refuses("ticket_input", "booked terms on another convention do not reopen", () => GasTicket.Reopen(Map(full) with { Convention = "physical_gas_fixed_quantity_v3" }));
        Refuses("ticket_input", "booked terms without an explicit discounting policy do not reopen", () => GasTicket.Reopen(Map(full) with { Discounting = null }));
        // A single-point trade's retained terms: a receipt priced at 2.5 with a 0.045 fee paid, both paid at one instant.
        var point = new PhysicalGasTerms(Snapshot, PhysicalGasStore.Convention, "current", Operation, Book, Definition, "PIPE", "Z1", "P1", "firm", "2027-01-01", "2027-02-01", "10000", "per_operating_day",
            "receive", "2.5", "0.045", null, "pay", "2027-03-01T00:00:00+00:00", false, Entity, 4, Counterparty, 7, Setup);
        Check("a single-point trade opens as one physical side with its payment and fee, each charged on its delivered quantity", () =>
        {
            TicketSideDraft Charge(string kind, string direction, (string, string) price) => Side(kind, $"{kind}-1", ($"{kind}_direction", direction), ($"{kind}_source", "physical-1"), ($"{kind}_stage", "delivery"), price, ($"{kind}_payment_at", "2027-03-01T00:00:00+00:00"));
            var expected = Draft(With(Receipt with { Code = "physical-1" }, "quantity", "10000"), Charge("cash", "pay", ("cash_price", "2.5")), Charge("fee", "pay", ("fee_price", "0.045")));
            Equal(Values(expected), Values(GasTicket.Reopen(GasTicket.SinglePoint(point))), "single-point draft");
            Equal(Values(Draft(Receipt with { Code = "physical-1", Values = Receipt.Values.SetItem("direction", "deliver") }, Charge("cash", "receive", ("cash_price", "2.5")))),
                Values(GasTicket.Reopen(GasTicket.SinglePoint(point with { Direction = "deliver", UsageFee = null, UsageFeeDirection = null }))), "delivered gas is paid for by the counterparty");
            var tariffed = GasTicket.Reopen(GasTicket.SinglePoint(point with { UsageFee = null, UsageTariffCode = "TARIFF", UsageFeeDirection = "receive" })).Sides.Single(s => s.Kind == "fee").Values;
            Equal(("TARIFF", (string?)null, "receive"), (tariffed["fee_tariff"], tariffed.GetValueOrDefault("fee_price"), tariffed["fee_direction"]), "tariffed fee");
        });
        Refuses("ticket_input", "a single-point trade with a fee direction and no fee does not open", () => GasTicket.Reopen(GasTicket.SinglePoint(point with { UsageFee = null })));
        Refuses("ticket_input", "a single-point trade with both a fee price and a tariff does not open", () => GasTicket.Reopen(GasTicket.SinglePoint(point with { UsageTariffCode = "TARIFF" })));
        Refuses("ticket_input", "a single-point trade with an unknown direction does not open", () => GasTicket.Reopen(GasTicket.SinglePoint(point with { Direction = "hold" })));
        Refuses("ticket_input", "a single-point trade without its payment instant does not open", () => GasTicket.Reopen(GasTicket.SinglePoint(point with { PaymentAt = null })));
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
