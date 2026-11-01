using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class TicketDescriptionChecks
{
    public static void Run()
    {
        var passed = 0;
        void Check(string name, Action check) { check(); passed++; Console.WriteLine($"PASS {name}"); }
        void Reject(string name, TicketDescription description)
        {
            try { TicketDescriptions.Validate(description); } catch (MarketDataException error) when (error.Code == "ticket_description") { passed++; Console.WriteLine($"PASS rejects {name}"); return; }
            throw new Exception($"Expected ticket_description for {name}.");
        }
        TicketField Field(string id, string state = "editable", string? band = null, string? choices = null, string? lookup = null, string? soon = null, string? rule = null, params string[] dependsOn) =>
            new(id, id, id, "text", null, state, false, band, choices, [..dependsOn], lookup, soon, rule);
        TicketSideKind Kind(string kind, params TicketField[] fields) => new(kind, kind, "blue", 1, 1, 2, [..fields], []);
        var valid = new TicketDescription("fixture", 1, "flow",
            [Field("book"), Field("note", "info")],
            [Kind("buy", Field("buy_start", band: "start"), Field("buy_price", band: "price", dependsOn: "buy_start")), Kind("sell", Field("sell_start", band: "start"))],
            [new(["buy", "sell"], 3)],
            [new("pv", "PV", null, "money", false)], ["month", "day"], [new("terms", "Terms")], ["new", "booked"]);
        TicketDescription WithTrade(params TicketField[] fields) => valid with { TradeFields = [..fields] };
        TicketDescription WithBuy(params TicketField[] fields) => valid with { Sides = [Kind("buy", fields), valid.Sides[1]] };
        Check("a valid description passes unchanged", () => { if (!ReferenceEquals(TicketDescriptions.Validate(valid), valid)) throw new Exception("Instance changed"); });
        Check("side columns map to schedule view measures", () =>
        {
            var kind = valid.Sides[0] with { Columns = [new("q", "Q", null, "quantity", false), new("p", "P", null, "price", false), new("a", "A", null, "money", false)] };
            if (!TicketDescriptions.ViewColumns(kind).SequenceEqual([("q", ScheduleViewMeasure.Quantity), ("p", ScheduleViewMeasure.Price), ("a", ScheduleViewMeasure.Amount)])) throw new Exception("Measures differ");
        });
        Reject("a field naming a unit symbol instead of a resolved unit", WithTrade(Field("book") with { Unit = "MMBtu" }));
        Reject("a column naming a unit symbol instead of a resolved unit", valid with { TradeColumns = [new("pv", "PV", "USD", "money", false)] });
        Reject("a side column the schedule view cannot show", valid with { Sides = [valid.Sides[0] with { Columns = [new("at", "At", null, "time", false)] }, valid.Sides[1]] });
        Reject("a field id repeated across trade and sides", WithTrade(Field("book"), Field("buy_start")));
        Reject("a state outside the four", WithTrade(Field("book", "hidden")));
        Reject("a soon field without hover text", WithTrade(Field("book", "soon")));
        Reject("a soon field with choices", WithTrade(Field("book", "soon", choices: "books", soon: "later")));
        Reject("a soon field with a lookup", WithTrade(Field("book", "soon", lookup: "books", soon: "later")));
        Reject("a derived field without a rule", WithTrade(Field("book", "derived")));
        Reject("a band outside the ticket band", WithTrade(Field("book", band: "footer")));
        Reject("two fields of one side kind on a band row", WithBuy(Field("a", band: "price"), Field("b", band: "price")));
        Reject("a dependency on an unknown field", WithTrade(Field("book", dependsOn: "missing")));
        Reject("a row shape other than flow", valid with { RowShape = "cargo" });
        Reject("empty levels", valid with { Levels = [] });
        Reject("levels out of order", valid with { Levels = ["day", "month"] });
        Reject("a level outside month, day, hour", valid with { Levels = ["week"] });
        Reject("a minimum above the maximum", valid with { Sides = [valid.Sides[0] with { Min = 3, Initial = 2, Max = 2 }] });
        Reject("a maximum below one", valid with { Sides = [valid.Sides[0] with { Min = 0, Initial = 0, Max = 0 }] });
        Reject("a side limit naming an unknown kind", valid with { SideLimits = [new(["buy", "swap"], 3)] });
        Reject("a side limit naming a kind twice", valid with { SideLimits = [new(["buy", "buy"], 3)] });
        Reject("a side limit below its kinds' minimum sides", valid with { SideLimits = [new(["buy", "sell"], 1)] });
        Reject("a negative minimum", valid with { Sides = [valid.Sides[0] with { Min = -1 }] });
        Reject("initial sides below the minimum", valid with { Sides = [valid.Sides[0] with { Initial = 0 }, valid.Sides[1]] });
        Reject("initial sides above the maximum", valid with { Sides = [valid.Sides[0] with { Initial = 3 }, valid.Sides[1]] });
        Reject("initial sides above a side limit", valid with { Sides = [valid.Sides[0] with { Initial = 2 }, valid.Sides[1] with { Initial = 2 }] });
        foreach (var state in new[] { "info", "derived", "soon" })
            Reject($"a required {state} field", WithTrade(Field("book", state, soon: "later", rule: "copied") with { Required = true }));
        Reject("a mode outside new, booked, amend and close-out", valid with { Modes = ["new", "draft"] });
        Console.WriteLine($"{passed} ticket description checks passed.");
    }
}
