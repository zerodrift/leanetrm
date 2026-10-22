using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// A page report's page and the read its selection names, once it parses.
sealed record PageRead(string Page,ToolCall? Call);

// The page a question was asked from (docs/ai-assistant.md §7.3, §8): exactly {"page","selection"}, the page one of a
// closed set with one id per navigable view. A supported page's selection is an input of its read tool for one of the
// views the page shows; an unsupported page's selection is empty. Ask refuses any other report before admission, and
// Read current page dispatches the stored report with the same parser.
static class PageReports
{
    // A page: the tool that reads it, the operations behind the views it shows and, for a page the assistant reads, the
    // name navigation targets label it with.
    sealed record Page(string? Tool,ImmutableHashSet<string> Operations,string? Name);

    static readonly ImmutableDictionary<string,Page> Pages=new Dictionary<string,Page>
    {
        ["curves"]=new("read_market_data",["market_data.curves_v1","market_data.curve_v1","market_data.observations_v1"],"Curves & prices"),
        ["contract_definitions"]=new("read_market_data",["market_data.contract_definitions_v1"],"Contract definitions"),
        ["schedules"]=new("read_schedules",["schedules.library_v1"],"Schedules"),
        ["power_tous"]=new("read_schedules",["schedules.tou_products_v1","schedules.power_hours_v1"],"Power TOUs"),
        ["reference_data"]=new("read_reference_data",["reference_data.table_v1"],"Reference data"),
        ["parties"]=new("read_trade_parties",["trade_parties.versions_v1"],"Entities & counterparties"),
        ["blotter"]=new("read_trades",["trades.blotter_v1","trades.trade_v1"],"Blotter"),
        ["blotter_risk"]=Unsupported(),["delivery_prices"]=Unsupported(),["custom_curves"]=Unsupported(),["interval_series"]=Unsupported(),
        ["interval_curves"]=Unsupported(),["jobs"]=Unsupported(),["users_access"]=Unsupported(),["settings_ai"]=Unsupported()
    }.ToImmutableDictionary(StringComparer.Ordinal);

    static Page Unsupported()=>new(null,[],null);

    // The pages the assistant reads, and those it does not.
    internal static IEnumerable<string> Supported=>Pages.Where(p=>p.Value.Tool is not null).Select(p=>p.Key);
    internal static IEnumerable<string> NotSupported=>Pages.Where(p=>p.Value.Tool is null).Select(p=>p.Key);

    // A page the assistant reads: its read tool, the operations behind the views it shows and its name; and the page an
    // operation reads.
    internal static string Tool(string page)=>Pages[page].Tool??throw new InvalidOperationException("The assistant does not read this page.");
    internal static ImmutableHashSet<string> Operations(string page)=>Pages[page].Operations;
    internal static string Name(string page)=>Pages[page].Name??throw new InvalidOperationException("The assistant does not read this page.");
    internal static string PageOf(string operation)=>Pages.Single(p=>p.Value.Operations.Contains(operation)).Key;

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
        if(report.ValueKind!=JsonValueKind.Object||!report.EnumerateObject().Select(m=>m.Name).Order(StringComparer.Ordinal).SequenceEqual(["page","selection"])
            ||report.GetProperty("page") is not {ValueKind:JsonValueKind.String} named||!Pages.TryGetValue(named.GetString()!,out var page))
            return null;
        var (id,selection)=(named.GetString()!,report.GetProperty("selection"));
        if(page.Tool is null)return selection.ValueKind==JsonValueKind.Object&&!selection.EnumerateObject().Any()?new(id,null):null;
        try
        {
            var call=AgentTools.Parse(page.Tool,selection);
            return new(id,page.Operations.Contains(call.Operation.Id)?call:null);
        }
        catch(ToolRefusal){return new(id,null);}
    }
}
