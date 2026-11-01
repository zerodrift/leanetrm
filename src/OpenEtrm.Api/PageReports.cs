using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// A page report's page, the read its selection names, once it parses, and the Blotter's layout where it states one.
sealed record PageRead(string Page,ToolCall? Call,BlotterLayout? Layout);

// What the Blotter showed of its panes and window when the question was asked (D271): whether its details and charts were
// open, each the shown tab of its group in the panes as drawn and not put away, and whether it was in a window of its own.
sealed record BlotterLayout(bool Details,bool Charts,bool PoppedOut);

// The page a question was asked from (docs/ai-assistant.md §7.3, §8): exactly {"page","selection"}, the page one of a
// closed set with one id per navigable view, and on a page the Blotter reports optionally its "layout". A supported page's
// selection is an input of its read tool for one of the views the page shows; an unsupported page's selection is empty.
// Ask refuses any other report before admission, and Read current page dispatches the stored report with the same parser.
static class PageReports
{
    // A page: the tool that reads it, the operations behind the views it shows and, for a page the assistant reads, the
    // name navigation targets label it with and whether Navigate opens it (D250).
    sealed record Page(string? Tool,ImmutableHashSet<string> Operations,string? Name,bool Navigable=true);

    static readonly ImmutableDictionary<string,Page> Pages=new Dictionary<string,Page>
    {
        ["curves"]=new("read_market_data",["market_data.curves_v1","market_data.curve_v1","market_data.observations_v1"],"Curves & prices"),
        ["contract_definitions"]=new("read_market_data",["market_data.contract_definitions_v1"],"Contract definitions"),
        ["schedules"]=new("read_schedules",["schedules.library_v1"],"Schedules"),
        ["power_tous"]=new("read_schedules",["schedules.tou_products_v1","schedules.power_hours_v1"],"Power TOUs"),
        ["reference_data"]=new("read_reference_data",["reference_data.table_v1"],"Reference data"),
        ["parties"]=new("read_trade_parties",["trade_parties.versions_v1"],"Entities & counterparties"),
        ["blotter"]=new("read_trades",["trades.blotter_v1","trades.trade_v1"],"Blotter"),
        ["custom_curves"]=new("read_custom_curves",["custom_curves.catalog_v1","custom_curves.prepare_v1","custom_curves.save_draft_v1"],"Curve builder"),
        ["jobs"]=new("read_jobs",["jobs.list_v1","jobs.history_v1"],"Jobs & Infrastructure"),
        // The inspected PPA is the Blotter's trade view of it; its target opens it in the Blotter.
        ["ppa"]=new("read_trades",["trades.trade_v1","trades.ppa_book_v1"],"PPA bookings",false),
        ["blotter_risk"]=Unsupported(),["blotter_risk_lens"]=Unsupported(),["blotter_new_trade"]=Unsupported(),["blotter_new_future"]=Unsupported(),["blotter_new_swap"]=Unsupported(),["blotter_new_ppa"]=Unsupported(),["blotter_new_gas"]=Unsupported(),["blotter_new_crude"]=Unsupported(),
        ["gas_setup"]=Unsupported(),["physical_gas"]=Unsupported(),["physical_crude"]=Unsupported(),["delivery_prices"]=Unsupported(),["interval_series"]=Unsupported(),
        ["interval_curves"]=Unsupported(),["users_access"]=Unsupported(),["settings_ai"]=Unsupported()
    }.ToImmutableDictionary(StringComparer.Ordinal);

    static Page Unsupported()=>new(null,[],null);

    // The pages the Blotter reports, which alone may state its layout.
    static readonly ImmutableHashSet<string> BlotterPages=["blotter","blotter_risk","blotter_risk_lens","blotter_new_trade","blotter_new_future","blotter_new_swap","blotter_new_ppa","blotter_new_gas"];

    // The pages the assistant reads, those Navigate opens, and those it does not read.
    internal static IEnumerable<string> Supported=>Pages.Where(p=>p.Value.Tool is not null).Select(p=>p.Key);
    internal static IEnumerable<string> Navigable=>Pages.Where(p=>p.Value is {Tool:not null,Navigable:true}).Select(p=>p.Key);
    internal static IEnumerable<string> NotSupported=>Pages.Where(p=>p.Value.Tool is null).Select(p=>p.Key);
    internal static bool Opens(string page)=>Pages[page] is {Tool:not null,Navigable:true};

    // A page the assistant reads: its read tool, the operations behind the views it shows and its name; and the page an
    // operation reads.
    internal static string Tool(string page)=>Pages[page].Tool??throw new InvalidOperationException("The assistant does not read this page.");
    internal static ImmutableHashSet<string> Operations(string page)=>Pages[page].Operations;
    internal static string Name(string page)=>Pages[page].Name??throw new InvalidOperationException("The assistant does not read this page.");
    // An operation two pages show belongs to the one Navigate opens.
    internal static string PageOf(string operation)
    {
        var pages=Pages.Where(p=>p.Value.Operations.Contains(operation)).ToArray();
        return (pages.Length==1?pages[0]:pages.Single(p=>p.Value.Navigable)).Key;
    }

    // A report ask accepts, or page_report_invalid.
    public static void Require(JsonElement report)
    {
        if(Read(report) is not {} read||read.Call is null&&Pages[read.Page].Tool is not null)
            throw new MarketDataException("page_report_invalid","Name the page you asked from, from the supported set, with its selection in the documented form.");
    }

    // The stored report's page and read: none for a page the assistant does not read, and no read for a selection that
    // does not parse, which ask never stores.
    public static PageRead? Dispatch(JsonElement report)=>Read(report) is {} read&&Pages[read.Page].Tool is not null?read:null;

    static PageRead? Read(JsonElement report)
    {
        if(report.ValueKind!=JsonValueKind.Object)return null;
        var members=report.EnumerateObject().Select(m=>m.Name).Order(StringComparer.Ordinal).ToArray();
        var laid=members.SequenceEqual(["layout","page","selection"]);
        if(!laid&&!members.SequenceEqual(["page","selection"])||report.GetProperty("page") is not {ValueKind:JsonValueKind.String} named||!Pages.TryGetValue(named.GetString()!,out var page))
            return null;
        var id=named.GetString()!;
        BlotterLayout? layout=null;
        if(laid&&(!BlotterPages.Contains(id)||(layout=Layout(report.GetProperty("layout"))) is null))return null;
        var selection=report.GetProperty("selection");
        if(page.Tool is null)return selection.ValueKind==JsonValueKind.Object&&!selection.EnumerateObject().Any()?new(id,null,layout):null;
        try
        {
            // Curve builder reports the curve it has open, if any, and whether it holds unsaved edits, and reads its catalog (D260).
            if(id=="custom_curves"){_=OpenCurve(selection);return new(id,AgentTools.Parse(page.Tool,NoInput),layout);}
            var call=AgentTools.Parse(page.Tool,selection);
            return new(id,page.Operations.Contains(call.Operation.Id)&&Shows(id,call)?call:null,layout);
        }
        catch(ToolRefusal){return new(id,null,layout);}
    }

    // The Blotter's layout: exactly {"pane":{"details","charts"},"poppedOut"}, each a flag; anything else is none.
    static BlotterLayout? Layout(JsonElement layout)
    {
        try
        {
            var members=new ToolInput(layout);
            members.Exactly("pane","poppedOut");
            var pane=members.Members("pane");
            pane.Exactly("details","charts");
            return new(pane.Flag("details"),pane.Flag("charts"),members.Flag("poppedOut"));
        }
        catch(ToolRefusal){return null;}
    }

    static readonly JsonElement NoInput=JsonDocument.Parse("{}").RootElement.Clone();

    // Curve builder's selection: exactly the curve it has open, or null, and whether it holds unsaved edits.
    static (Guid? Curve,bool Unsaved) OpenCurve(JsonElement selection)
    {
        var members=new ToolInput(selection);
        members.Exactly("curve","unsaved");
        return(members.Has("curve")?members.Id("curve"):null,members.Flag("unsaved"));
    }

    // Whether a stored report shows this curve open in Curve builder with unsaved edits (D260).
    internal static bool HoldsUnsaved(JsonElement report,Guid curve)=>
        Read(report) is {Page:"custom_curves",Call:not null}&&OpenCurve(report.GetProperty("selection")) is {Unsaved:true} open&&open.Curve==curve;

    // PPA bookings shows only PPAs, each at the revision it inspected.
    static bool Shows(string page,ToolCall call)=>page!="ppa"||call.Arguments is not TradeArguments{InstrumentType:not "ppa"} and not TradeArguments{Revision:null};
}
