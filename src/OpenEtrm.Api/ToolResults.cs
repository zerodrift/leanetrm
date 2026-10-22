using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// A read's result as a tool returns it: its value, the records it lists, which the runtime set bounds, and whether its
// amounts carry the page's display.
sealed record ToolResult(JsonObject Value,int Rows,bool Displayed);

// What each operation returns of its page read (docs/ai-assistant.md §7.3): the page's own fields and exact values,
// without what only the page uses (booking catalogs, rights, workspace payloads, the pricing trace and the frame the
// envelope already names). Where the page shows formatted amounts, so does the result.
static class ToolResults
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new ExactDecimalJsonConverter()}};
    static JsonNode Node<T>(T value)=>JsonSerializer.SerializeToNode(value,Json)!;

    public static ToolResult Curves(NavigationRead read)=>new(new(){["curves"]=Node(read.Curves)},read.Curves.Length,false);

    // A curve's prices at the frame, shown as the page shows them.
    public static ToolResult Curve(PriceRead read)
    {
        var curve=Node(read.Result).AsObject();
        foreach(var page in new[]{"snapshotId","name","at","evaluated","trace","observationIds"})curve.Remove(page);
        var points=curve["points"]!.AsArray();
        return new(curve,points.Count,ToolAmountDisplay.Annotate(DisplayWalk.Viewer(points)));
    }

    // The observations the curve's prices were built from, exactly as stored.
    public static ToolResult Observations(PriceRead read)
    {
        var curve=Node(read.Result).AsObject();
        var used=curve["points"]!.AsArray().SelectMany(p=>p!["lineage"]!.AsArray().Select(id=>Guid.Parse(id!.GetValue<string>()))).ToHashSet();
        var observations=read.Observations.Where(o=>used.Contains(o.Id("id"))).ToImmutableArray();
        return new(new(){["curve"]=curve["curve"]!.DeepClone(),["observations"]=Node(observations)},observations.Length,false);
    }

    public static ToolResult ContractDefinitions(ScheduleLibrary library)=>
        new(new(){["contractDefinitions"]=Node(library.ContractDefinitions)},library.ContractDefinitions.Length,false);

    // One page of a reference table: a table with more matching rows than a page holds would be truncated, so it is
    // result_too_large. Rows name no catalog or revision; linked rows name the codes they label.
    public static ToolResult Reference(ReferencePage page)
    {
        if(page.HasMore)throw new MarketDataException("result_too_large","More rows match than one read returns.");
        var linked=page.References.Where(r=>r.Key!=page.Table).OrderBy(r=>r.Key,StringComparer.Ordinal).ToArray();
        return new(new(){["table"]=page.Table,["search"]=page.Search,["rows"]=Rows(page.Rows),
            ["linked"]=new JsonObject(linked.Select(r=>KeyValuePair.Create(r.Key,(JsonNode?)Rows(r.Value))))},page.Rows.Length+linked.Sum(r=>r.Value.Length),false);
    }

    static JsonArray Rows(ImmutableArray<JsonElement> rows)=>new(rows.Select(r=>
    {
        var row=Node(r).AsObject();
        row.Remove("catalog_id");row.Remove("revision_id");
        return (JsonNode?)row;
    }).ToArray());

    public static ToolResult Library(ScheduleLibrary library)=>new(new(){["knownThrough"]=library.KnownThrough,["versions"]=Node(library.Versions),
        ["calendars"]=Node(library.Calendars)},library.Versions.Length+library.Calendars.Length,false);

    public static ToolResult TouProducts(TouProductLibrary library)=>
        new(new(){["versions"]=Node(library.Versions),["clocks"]=Node(library.Clocks)},library.Versions.Length+library.Clocks.Length,false);

    // The published products, their hourly masks and the frame's single-index contracts.
    public static ToolResult PowerHours(object catalog)
    {
        var read=Node(catalog).AsObject();
        var hourly=new JsonArray(read["presets"]!.AsArray().Where(p=>p!["minutes"]!.GetValue<int>()==60).Select(p=>
        {
            var mask=p!.DeepClone().AsObject();
            mask.Remove("columns");
            return (JsonNode?)mask;
        }).ToArray());
        var result=new JsonObject{["products"]=read["products"]!.DeepClone(),["unavailableProducts"]=read["unavailableProducts"]!.DeepClone(),
            ["blocks"]=read["blocks"]!.DeepClone(),["masks"]=hourly,["curves"]=read["curves"]!.DeepClone()};
        return new(result,new[]{"products","unavailableProducts","masks"}.Sum(n=>result[n]!.AsArray().Count)
            +result["curves"]!.AsArray().Sum(c=>c!["contracts"]!.AsArray().Count),false);
    }

    public static ToolResult Parties(PartyRead read,string kind)=>new(new(){["kind"]=kind,["versions"]=Node(read.Versions)},read.Versions.GetArrayLength(),false);

    // Trade rows, each shown by its deal's formats, or the totals of a pivot, with the page's display.
    public static ToolResult Blotter(object report)
    {
        var read=Node(report).AsObject();
        var listed=read["view"]!["mode"]!.GetValue<string>()=="pivot"?"groups":"holdings";
        foreach(var page in new[]{"work","evaluatedContracts",listed=="groups"?"holdings":"groups"})read.Remove(page);
        return new(read,read[listed]!.AsArray().Count,ToolAmountDisplay.Annotate(DisplayWalk.Viewer(read)));
    }

    // A trade's selected revision and every revision the pinned knowledge holds, shown as its editor shows them
    // (trade-editor.ts, swap-editor.ts), from the deal's saved formats: a futures trade by the deal's trade format with
    // its prices at its contract's settlement-price increment, where the catalog lists the contract; a swap's valuation
    // by the deal's trade format and each side by its own, and the rest as the page shows it from the viewer's precision.
    public static ToolResult Trade(object editor,string instrumentType,Dictionary<string,DealAmountFormats.Selection>? formats)
    {
        var read=Node(editor).AsObject();
        var increment=instrumentType=="future"?Increment(read):null;
        foreach(var page in new[]{"catalog","amendmentConvention","lifecycleEligible"})read.Remove(page);
        var result=new JsonObject{["instrumentType"]=instrumentType};
        foreach(var (name,value) in read.ToArray())
        {
            read.Remove(name);
            result[name]=value;
        }
        DealAmountFormat Format(string role)=>DealAmountFormats.Resolve(formats,role);
        ImmutableArray<DisplayWalk> walks;
        if(instrumentType=="future")walks=[new(result,increment,Format("trade"))];
        else
        {
            var valuation=result["valuation"]!.AsObject();
            valuation.Remove("snapshotId");valuation.Remove("asOf");
            walks=[DisplayWalk.Viewer(result),new(valuation,null,Format("trade")),
                ..valuation["sides"]!.AsArray().Select(side=>new DisplayWalk(side!,null,Format(side!["kind"]!.GetValue<string>())))];
        }
        return new(result,result["history"]!.AsArray().Count,ToolAmountDisplay.Annotate(walks));
    }

    // The futures editor's tick for a trade's prices: its contract's settlement-price increment, where the catalog
    // lists the contract.
    static string? Increment(JsonObject editor)
    {
        var contract=editor["state"]!["contractId"]!.GetValue<string>();
        return editor["catalog"]!["contracts"]!.AsArray().FirstOrDefault(c=>c!["id"]!.GetValue<string>()==contract)?["clearingIncrement"]!.GetValue<string>();
    }
}

// A walk of the page's amount display over part of a payload, from the tick and deal format the page starts it with.
sealed record DisplayWalk(JsonNode Part,string? Tick,DealAmountFormat Format)
{
    // A walk the page starts from the viewer's own display precision, a preference outside the turn's pins, which a
    // result starts from the default format instead.
    public static DisplayWalk Viewer(JsonNode part)=>new(part,null,DealAmountFormat.Default);
}

// The page's amount display (web/src/amount-display.ts), applied on the server so a result carries the strings the
// page shows beside the exact values (D114): each amount the page formats becomes {value, display}. A field's name
// gives its kind. A price shows by its nearest priceTick, a mark by its row's markTick, else by the tick its walk
// started from; other amounts by the nearest deal format (fixed prices by the fixed format), else by the format its
// walk started from, ties to even, thousands grouped and trailing zeros trimmed except for money. Anything else is
// left as it is.
static partial class ToolAmountDisplay
{
    public const string Implementation="tool_amount_display_v1";
    static readonly FrozenSet<string> Money=FrozenSet.ToFrozenSet(["presentValue","instrumentPresentValue","cashAmount","contribution","cashContribution",
        "executionPnl","upPnl","downPnl","basePv","upPv","downPv","change"]);
    static readonly FrozenSet<string> Prices=FrozenSet.ToFrozenSet(["price","fixedPrice","executionPrice","mark","value","annualRate"]);
    static readonly FrozenSet<string> Quantities=FrozenSet.ToFrozenSet(["quantity","physicalQuantity","totalQuantity","weight"]);
    static readonly FrozenSet<string> Factors=FrozenSet.ToFrozenSet(["discountFactor","factor","multiplier","delta","gamma"]);

    // Replaces each amount the page's walks show with its value and display, one that two walks reach by the later
    // walk's, as an editor shows a part by the walk it made for that part; whether there was any.
    public static bool Annotate(params ImmutableArray<DisplayWalk> walks)
    {
        var shown=new Dictionary<(JsonObject Row,string Field),DisplayNumberRequest>();
        foreach(var walk in walks)
            foreach(var (row,field,request) in Amounts(walk.Part,walk.Tick,walk.Format))shown[(row,field)]=request;
        foreach(var ((row,field),request) in shown)row[field]=new JsonObject{["value"]=request.Value,["display"]=UnitOperations.Display([request])[0]};
        return shown.Count>0;
    }

    // Each amount a payload shows, where it is and the display it asks for, as the page's walk from a tick and a deal
    // format finds them.
    internal static List<(JsonObject Row,string Field,DisplayNumberRequest Request)> Amounts(JsonNode? payload,string? tick,DealAmountFormat format)
    {
        var amounts=new List<(JsonObject,string,DisplayNumberRequest)>();
        void Visit(JsonNode? node,string? tick,DealAmountFormat inherited)
        {
            if(node is JsonArray array){foreach(var item in array)Visit(item,tick,inherited);return;}
            if(node is not JsonObject row)return;
            var priceTick=Text(row["priceTick"])??tick;
            var policy=Policy(row["amountFormat"])??inherited;
            foreach(var (field,raw) in row)
            {
                if(Kind(field) is {} kind&&Text(raw) is {} value&&Amount().IsMatch(value))
                {
                    var selectedTick=kind=="price"?(field=="mark"?Text(row["markTick"]):null)??priceTick:null;
                    var selected=field=="fixedPrice"?Policy(row["fixedAmountFormat"])??policy:policy;
                    var places=kind switch{"money"=>selected.Money,"price"=>selected.Price,"quantity"=>selected.Quantity,_=>selected.Factor};
                    amounts.Add((row,field,new(value,places,selectedTick,kind!="money")));
                }
                else Visit(raw,priceTick,policy);
            }
        }
        Visit(payload,tick,format);
        return amounts;
    }

    static string? Kind(string field)=>Money.Contains(field)?"money":Prices.Contains(field)?"price":Quantities.Contains(field)?"quantity":Factors.Contains(field)?"factor":null;

    static string? Text(JsonNode? node)=>node is JsonValue value&&value.TryGetValue<string>(out var text)?text:null;

    // A deal's format as the page reads it: each kind's places, a whole number from 0 to 18.
    static DealAmountFormat? Policy(JsonNode? node)
    {
        if(node is null)return null;
        int Places(string kind)=>node[kind] is JsonValue value&&value.TryGetValue<int>(out var places)&&places is >=0 and <=18
            ?places:throw new InvalidOperationException("A deal's amount format is unreadable.");
        return node is JsonObject?new(Places("money"),Places("price"),Places("quantity"),Places("factor")):throw new InvalidOperationException("A deal's amount format is unreadable.");
    }

    [GeneratedRegex(@"^-?[0-9]+(?:\.[0-9]+|/[0-9]+)?\z")] private static partial Regex Amount();
}
