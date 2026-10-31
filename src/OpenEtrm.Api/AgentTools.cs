using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// A tool the worker offers (docs/ai-assistant.md §7.3): its definition, sent to the model, and the permissions its reads
// need, which the turn's grant lists.
public sealed record AgentTool(string Name,string Description,JsonElement InputSchema,ImmutableArray<string> Permissions)
{
    public ModelTool Definition=>new(Name,Description,InputSchema);
}

// What a read needs of the turn's pin before it runs (§7.2): its pinned context, with or without the As of frame,
// current-only definitions, with or without the frame, or current operational records such as jobs, which have no
// market context and which a historical view does not read (docs/devops-assistant.md §3).
enum ToolNeeds {View,Frame,CurrentOnly,CurrentOnlyFrame,Operational}

// A versioned read operation (architecture §10): its tool, what it needs of the pin, the accesses its read runs under, and
// which of its read's refusals mean that a page's selection is unavailable.
sealed record ToolOperation(string Id,string Tool,ToolNeeds Needs,Func<PreviewManifest,ImmutableArray<ReadAccess>> Access,ImmutableHashSet<string> SelectionRefusals);

// One access a read ran under, as record_tool_step takes it (§7.1): in a catalog, or for job reads none.
public sealed record ReadAccess(string Access,Guid? CatalogId,string Permission);

// A request's operation and its arguments.
sealed record ToolCall(ToolOperation Operation,ToolArguments Arguments);
abstract record ToolArguments;
sealed record NoArguments:ToolArguments;
sealed record PrepareCurveArguments(string Code,string Name,string Formula,string Engine):ToolArguments;
// Save curve draft's request: the curve as it was prepared, and the assistant's own draft it revises, or none for a new curve.
sealed record SaveCurveArguments(PrepareCurveArguments Curve,Guid? CurveId):ToolArguments;
sealed record CurveArguments(string Curve):ToolArguments;
sealed record ReferenceArguments(string Table,string Search):ToolArguments;
sealed record PartyArguments(string Kind):ToolArguments;
sealed record BlotterArguments(FuturesBlotterView View):ToolArguments;
sealed record TradeArguments(Guid TradeId,string InstrumentType,int? Revision):ToolArguments;
sealed record PpaBookArguments(Guid Book):ToolArguments;
sealed record JobListArguments(int Page):ToolArguments;
sealed record JobHistoryArguments(Guid Job,long? Before):ToolArguments;
// Navigate's request names exactly one of a page the assistant reads and a handle a read issued.
sealed record NavigateArguments(string? Page,string? Handle):ToolArguments;

// A refusal of a request before its read runs.
sealed class ToolRefusal(string code):Exception(code)
{
    public string Code {get;}=code;
}

// The turn's agent, in whose request context every read runs: its principal and the turn's grant, fixed at ask (§7.1).
public sealed record AgentIdentity(string Principal,Guid Delegation)
{
    public override string ToString()=>nameof(AgentIdentity);
}

// What a turn's reads run with: its pin, its agent, the page report stored at ask, the version's switched-on tools and its
// time per read (D245).
public sealed record TurnReads(TurnPin Pin,AgentIdentity Agent,JsonElement PageReport,ImmutableHashSet<string> Tools,Guid TurnId,TimeSpan ReadLimit);

// How a step ended: with a result or a refusal the model receives, a failure that ends the turn, or a read the worker
// stopped, whose cause it names.
public enum ToolStepEnd {Result,Refusal,Failure,Interrupted}

// A request's step (§7.3): what record_tool_step records, and the content the model receives for a result or refusal.
// A read names its operation, the instant it read and every access it ran under; a refusal before any read names none.
// A Navigate step names the target it issued, which its navigation event publishes; a read's step names the targets its
// result issued, which name targets once the step is published.
public sealed record ToolStep(string Tool,string? Operation,JsonElement? Input,string? Code,DateTimeOffset? ReadAt,ImmutableArray<ReadAccess> Access,
    string? Content,ToolStepEnd End)
{
    public NavigationTarget? Target {get;init;}
    public ImmutableArray<IssuedTarget> Issued {get;init;}=[];
    // The record_tool_step answer of a step recorded in its own write's transaction (D260), which the worker publishes as
    // it is; none for a step the worker records.
    public JsonElement? Recorded {get;init;}
    public override string ToString()=>nameof(ToolStep);
}

// What a turn's steps write as the turn (§7.3, D260): each step's record, and the claim of a curve draft a save makes, both
// with the turn's fencing token, which nothing else reads and no result, record, log or exception text carries.
public sealed class TurnWrites
{
    readonly Guid turn;
    readonly byte[] token;
    public TurnWrites(Guid turn,byte[] token)=>(this.turn,this.token)=(turn,token.ToArray());

    internal CurveDraftSaver Saver(int step)=>CurveDraftSaver.Assistant(turn,step,token);

    // Records a step on `connection`, in whatever transaction it holds: its read's operation, instant and accesses, and a
    // Navigate step's target with the turn's pinned context.
    internal Task<JsonElement> Record(NpgsqlConnection connection,ToolStep step,TurnPin pin,CancellationToken ct)=>ModelProviders.Read(connection,
        "SELECT assistant.record_tool_step($1,$2,$3,$4::text,$5::jsonb,$6::text,$7::timestamptz,$8::jsonb,$9::jsonb)::text",
        [turn,token,step.Tool,(object?)step.Operation??DBNull.Value,step.Input is {} input?input.GetRawText():DBNull.Value,(object?)step.Code??DBNull.Value,
            (object?)step.ReadAt??DBNull.Value,step.ReadAt is null?DBNull.Value:JsonSerializer.Serialize(step.Access,JsonSerializerOptions.Web),
            step.Target is {} target?NavigationTargets.Json(target,pin).ToJsonString():DBNull.Value],ct);

    public override string ToString()=>nameof(TurnWrites);
}

// The fixed tools (§7.3, §12 step 2): reads over the session-independent page reads, so pages and tools share each read
// and its authorization path, and Navigate. Every read runs in a request context issued to the turn's agent under its
// grant, at the pin the turn's record rebuilds, and returns the page's own values, bounded by the runtime set and never
// truncated, with the navigation targets it issues; Navigate checks its target's page in such a context.
public sealed partial class AgentTools(PreviewDatabase database,MarketReads market,ScheduleReads schedules,PartyReads parties,TradeReads trades,ILogger logger)
{
    const string Uuid="^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";
    static readonly ImmutableArray<string> MarketData=["ReadMarketData"];

    // The fixed tools, in the order every request offers them. Show card comes later.
    internal static readonly ImmutableArray<AgentTool> Tools=
    [
        Tool("read_current_page","Reads the page the user asked from, with what it had selected then, at the knowledge and As of frame pinned when "+
            "the question was asked. Navigating while you answer changes nothing it reads. On the Blotter, layout says whether its details and "+
            "charts panes were open and whether the Blotter was in a window of its own. Takes no input.",
            """
            {"type":"object","properties":{},"additionalProperties":false}
            """,[]),
        Tool("read_market_data","Reads market data at the knowledge and As of frame pinned when the question was asked, never another date. View "+
            "curves lists the curve directory. View curve returns one curve's dated contracts with their exact prices at the frame. View observations "+
            "returns the input observations that curve's prices were built from. View contract_definitions lists the published shared contract date "+
            "definitions. Name the curve for the curve and observations views, and send it as null for the others.",
            """
            {"type":"object","properties":{"view":{"type":"string","enum":["curves","curve","observations","contract_definitions"]},
             "curve":{"type":["string","null"],"minLength":1,"maxLength":64,"pattern":"^[A-Za-z0-9][A-Za-z0-9_.-]*$",
              "description":"A curve code from the curves view for the curve and observations views; null for the others."}},
             "required":["view","curve"],"additionalProperties":false}
            """,MarketData),
        Tool("read_trades","Reads trades at the pinned knowledge and As of frame. View blotter returns Blotter rows with their execution P&L or "+
            "present value (mode trades), or totals grouped by its dimensions (mode pivot), for the listed books and portfolios; empty lists read "+
            "every book you can read. View trade returns one trade's revision, or its latest under the pinned knowledge when revision is null, with "+
            "its history. View ppa_book returns the PPA trades booked in the one book bookIds names, with their bindings. Send every member: those "+
            "the view does not take are null.",
            $$$"""
            {"type":"object","properties":{"view":{"type":"string","enum":["blotter","trade","ppa_book"]},
             "mode":{"type":["string","null"],"enum":["trades","pivot",null],"description":"Blotter: trade rows, or totals grouped by groupBy. Null for a trade."},
             "bookIds":{"type":["array","null"],"items":{"type":"string","pattern":"{{{Uuid}}}"},"maxItems":64,"uniqueItems":true,
              "description":"Blotter: the books to read, empty for every book. PPA book: exactly one book. Null for a trade."},
             "portfolioIds":{"type":["array","null"],"items":{"type":"string","pattern":"{{{Uuid}}}"},"maxItems":64,"uniqueItems":true,
              "description":"Blotter: the portfolios to read, empty for every portfolio. Null for a trade."},
             "groupBy":{"type":["array","null"],"items":{"type":"string","enum":["portfolio","book","market","contract","instrumentType","commodity"]},"uniqueItems":true,
              "description":"Blotter: empty for trade rows, at least one dimension for totals. Null for a trade."},
             "measures":{"type":["array","null"],"items":{"type":"string","enum":["tradeCount","lots","quantity","executionPnl","presentValue"]},"uniqueItems":true,
              "description":"Blotter: empty for trade rows, at least one measure for totals; lots need the contract dimension. Null for a trade."},
             "tradeId":{"type":["string","null"],"pattern":"{{{Uuid}}}","description":"Trade: the trade to read. Null for the blotter."},
             "instrumentType":{"type":["string","null"],"enum":["future","financial_swap","heat_rate","ppa",null],"description":"Trade: its instrument type. Null for the blotter."},
             "revision":{"type":["integer","null"],"minimum":1,
              "description":"Trade: a revision, or null for its latest under the pinned knowledge. Null for the blotter."}},
             "required":["view","mode","bookIds","portfolioIds","groupBy","measures","tradeId","instrumentType","revision"],"additionalProperties":false}
            """,["ReadMarketData","ReadTrades"]),
        Tool("read_trade_parties","Reads the legal entity or counterparty versions in force at the pinned As of frame, as known at the pinned knowledge.",
            """
            {"type":"object","properties":{"kind":{"type":"string","enum":["legal_entity","counterparty"]}},"required":["kind"],"additionalProperties":false}
            """,["ReadMarketData","ReadTradeParties"]),
        Tool("read_schedules","View library returns published date schedules and their calendars at the pinned knowledge. View tou_products returns "+
            "the current published TOU products, and view power_hours their hourly masks with the As of frame's single-index power contracts; both "+
            "are current definitions, read now and labeled with that instant, and a historical view does not read them.",
            """
            {"type":"object","properties":{"view":{"type":"string","enum":["library","tou_products","power_hours"]}},"required":["view"],"additionalProperties":false}
            """,MarketData),
        Tool("read_reference_data","Reads one reference table at the pinned knowledge, at most 200 rows: narrow a larger table with search, a literal "+
            "case-insensitive match anywhere in a row; an empty search matches every row.",
            $$$"""
            {"type":"object","properties":{"table":{"type":"string","enum":[{{{string.Join(",",PreviewDatabase.ReferenceTables.Select(t=>$"\"{t}\""))}}}]},
             "search":{"type":"string","maxLength":160}},"required":["table","search"],"additionalProperties":false}
            """,MarketData),
        ..CurveTools,
        JobTool,
        Tool("navigate","Requests a move of the user to a page, or to what a read in this turn showed. Every read result names a target handle for its "+
            "own view, and each curve and trade it lists names one too. Send page, with target null, to open a page as it opens; or send a handle as "+
            "target, with page null. Ordinary moves keep the knowledge and As of frame pinned when the question was asked. Curve proposals instead request explicit review of an unpriced draft under the user's current header, retaining the proposal's origin dates. Say where it leads.",
            $$$"""
            {"type":"object","properties":{"page":{"type":["string","null"],"enum":[{{{string.Join(",",PageReports.Navigable.Order(StringComparer.Ordinal).Select(p=>$"\"{p}\""))}}},null],
              "description":"A page to open, or null to open a target."},
             "target":{"type":["string","null"],"pattern":"^n[1-9][0-9]{0,8}$","description":"A target handle a read in this turn returned, or null to open a page."}},
             "required":["page","target"],"additionalProperties":false}
            """,[])
    ];

    static AgentTool Tool(string name,string description,string schema,ImmutableArray<string> permissions)
    {
        using var parsed=JsonDocument.Parse(schema);
        return new(name,description,parsed.RootElement.Clone(),permissions);
    }

    // What a started turn's reads run with (§7.1, §7.2), from start_turn's answer: its pin, rebuilt from its record for
    // this scope's catalog, the agent and grant it was asked under, its stored page report and its version's tools.
    public TurnReads Reads(JsonElement turn,JsonElement started)=>new(
        TurnPin.From(turn.GetProperty("context"),started.GetProperty("pins"),started.GetProperty("catalogId").GetGuid(),database.Manifest),
        new(started.GetProperty("agentPrincipal").GetString()!,started.GetProperty("delegationId").GetGuid()),turn.GetProperty("pageReport").Clone(),
        [..started.GetProperty("tools").EnumerateArray().Select(t=>t.GetString()!)],turn.GetProperty("id").GetGuid(),
        TimeSpan.FromSeconds(started.GetProperty("limits").GetProperty("readSeconds").GetInt32()));

    // The definitions a version's request offers: its switched-on tools that this release implements, in the fixed order.
    public static ImmutableArray<ModelTool> Offered(IEnumerable<string> switchedOn)
    {
        var on=switchedOn.ToHashSet(StringComparer.Ordinal);
        return [..Tools.Where(t=>on.Contains(t.Name)).Select(t=>t.Definition)];
    }

    static readonly ImmutableDictionary<string,ToolOperation> Operations=new ToolOperation[]
    {
        new("market_data.curves_v1","read_market_data",ToolNeeds.View,MarketAccess,[]),
        new("market_data.curve_v1","read_market_data",ToolNeeds.Frame,MarketAccess,["missing_curve"]),
        new("market_data.observations_v1","read_market_data",ToolNeeds.Frame,MarketAccess,["missing_curve"]),
        new("market_data.contract_definitions_v1","read_market_data",ToolNeeds.View,MarketAccess,[]),
        new("trades.blotter_v1","read_trades",ToolNeeds.Frame,TradeAccess,["blotter_view"]),
        new("trades.trade_v1","read_trades",ToolNeeds.Frame,TradeAccess,["futures_trade","swap_trade","ppa_trade","ppa_revision"]),
        new("trades.ppa_book_v1","read_trades",ToolNeeds.Frame,TradeAccess,["ppa_scope"]),
        new("trade_parties.versions_v1","read_trade_parties",ToolNeeds.Frame,PartyAccess,[]),
        new("schedules.library_v1","read_schedules",ToolNeeds.View,MarketAccess,[]),
        new("schedules.tou_products_v1","read_schedules",ToolNeeds.CurrentOnly,MarketAccess,[]),
        new("schedules.power_hours_v1","read_schedules",ToolNeeds.CurrentOnlyFrame,MarketAccess,[]),
        new("reference_data.table_v1","read_reference_data",ToolNeeds.View,MarketAccess,[]),
        new("custom_curves.catalog_v1","read_custom_curves",ToolNeeds.View,MarketAccess,[]),
        new("custom_curves.prepare_v1","prepare_custom_curve",ToolNeeds.Frame,MarketAccess,[]),
        new("custom_curves.save_draft_v1","save_custom_curve_draft",ToolNeeds.CurrentOnlyFrame,MarketAccess,[]),
        new("jobs.list_v1","read_jobs",ToolNeeds.Operational,JobAccess,[]),
        new("jobs.history_v1","read_jobs",ToolNeeds.Operational,JobAccess,["job_not_found"])
    }.ToImmutableDictionary(o=>o.Id);
    internal static ToolOperation Operation(string id)=>Operations[id];
    // Navigate reads no page: the accesses its check runs under are those of its target's page.
    static readonly ToolOperation Navigation=new(NavigationTargets.Operation,"navigate",ToolNeeds.View,_=>[],[]);
    // Market and reference reads check both catalogs; trades and parties read a market catalog's trading data, with its
    // market data (§7.1). An access recorded and not checked only makes later rechecks stricter.
    static ImmutableArray<ReadAccess> MarketAccess(PreviewManifest m)=>[new("market_access",m.CatalogId,"ReadMarketData"),new("reference_access",m.ReferenceCatalogId,"ReadMarketData")];
    static ImmutableArray<ReadAccess> TradeAccess(PreviewManifest m)=>[new("trading_access",m.CatalogId,"ReadTrades"),..MarketAccess(m)];
    static ImmutableArray<ReadAccess> PartyAccess(PreviewManifest m)=>[new("trading_access",m.CatalogId,"ReadTradeParties"),new("market_access",m.CatalogId,"ReadMarketData")];

    // A request's input, read against its tool's schema: every member it states, those its view takes each of its stated
    // type and range and the others null; anything else is tool_arguments_invalid. Read current page takes none and
    // dispatches the stored page report.
    internal static ToolCall Parse(string tool,JsonElement input)
    {
        var members=new ToolInput(input);
        switch(tool)
        {
            case "read_custom_curves":members.Exactly();return new(Operations["custom_curves.catalog_v1"],new NoArguments());
            case "prepare_custom_curve":
                members.Exactly("code","name","engine","formula");
                return new(Operations["custom_curves.prepare_v1"],PreparedCurve(members,input));
            case "save_custom_curve_draft":
                members.Exactly("code","name","engine","formula","curveId");
                return new(Operations["custom_curves.save_draft_v1"],new SaveCurveArguments(PreparedCurve(members,input),members.Has("curveId")?members.Id("curveId"):null));
            case "read_jobs":return ParseJobs(members);
            case "read_market_data":
                members.Exactly("view","curve");
                var market=members.Choice("view","curves","curve","observations","contract_definitions");
                if(market is "curve" or "observations")
                    return new(Operations[$"market_data.{market}_v1"],new CurveArguments(members.Text("curve",1,64,CurveCode())));
                members.Null("curve");
                return new(Operations[$"market_data.{market}_v1"],new NoArguments());
            case "read_trades":
                members.Exactly("view","mode","bookIds","portfolioIds","groupBy","measures","tradeId","instrumentType","revision");
                var view=members.Choice("view","blotter","trade","ppa_book");
                if(view=="ppa_book")
                {
                    members.Null("mode","portfolioIds","groupBy","measures","tradeId","instrumentType","revision");
                    return members.Ids("bookIds") is [var book]?new(Operations["trades.ppa_book_v1"],new PpaBookArguments(book)):throw ToolInput.Invalid();
                }
                if(view=="trade")
                {
                    members.Null("mode","bookIds","portfolioIds","groupBy","measures");
                    return new(Operations["trades.trade_v1"],new TradeArguments(members.Id("tradeId"),members.Choice("instrumentType","future","financial_swap","heat_rate","ppa"),
                        members.Revision("revision")));
                }
                members.Null("tradeId","instrumentType","revision");
                var mode=members.Choice("mode","trades","pivot");
                var groupBy=members.Choices("groupBy","portfolio","book","market","contract","instrumentType","commodity");
                var measures=members.Choices("measures","tradeCount","lots","quantity","executionPnl","presentValue");
                if(mode=="trades"?!groupBy.IsEmpty||!measures.IsEmpty:groupBy.IsEmpty||measures.IsEmpty)throw ToolInput.Invalid();
                return new(Operations["trades.blotter_v1"],new BlotterArguments(new(mode,members.Ids("bookIds"),members.Ids("portfolioIds"),groupBy,measures,
                    mode=="pivot"?TradingBlotter.PivotConvention:null)));
            case "read_trade_parties":
                members.Exactly("kind");
                return new(Operations["trade_parties.versions_v1"],new PartyArguments(members.Choice("kind","legal_entity","counterparty")));
            case "read_schedules":
                members.Exactly("view");
                return new(Operations[$"schedules.{members.Choice("view","library","tou_products","power_hours")}_v1"],new NoArguments());
            case "read_reference_data":
                members.Exactly("table","search");
                return new(Operations["reference_data.table_v1"],new ReferenceArguments(members.Choice("table",[..PreviewDatabase.ReferenceTables]),members.Text("search",0,160)));
            case "navigate":
                members.Exactly("page","target");
                if(members.Has("page"))
                {
                    members.Null("target");
                    return new(Navigation,new NavigateArguments(members.Choice("page",[..PageReports.Navigable]),null));
                }
                members.Null("page");
                return new(Navigation,new NavigateArguments(null,members.Text("target",2,10,Handle())));
            default:
                throw ToolInput.Invalid();
        }
    }

    // A curve as preparation takes it, within what a step's stored input keeps.
    static PrepareCurveArguments PreparedCurve(ToolInput members,JsonElement input)
    {
        var serialized=input.GetRawText();
        if(Encoding.UTF8.GetByteCount(serialized)+serialized.Count(c=>c is ',' or ':')>8192)throw ToolInput.Invalid();
        return new(members.Text("code",1,32,CustomCode()),members.Text("name",1,80),members.Formula("formula"),
            members.Choice("engine",OpenEtrm.MarketData.CustomCurves.RecipeEngine.Version));
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*\z")] private static partial Regex CurveCode();
    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,31}\z")] private static partial Regex CustomCode();
    [GeneratedRegex(@"^n[1-9][0-9]{0,8}\z")] private static partial Regex Handle();

    // The fixed text each refusal carries, never a read's own message, which may name business data (§7.3, D177). A
    // structured refusal this table does not name carries the generic sentence.
    static readonly ImmutableDictionary<string,string> Texts=new Dictionary<string,string>
    {
        ["tool_arguments_invalid"]="The request's arguments are not ones this tool accepts: they do not match its input schema, name a target handle no "+
            "read in this turn returned, or name a page this agent cannot open for the user. Correct them and ask again.",
        ["page_not_supported"]="The page this question was asked from is not one the assistant reads, so nothing was read from it.",
        ["tool_not_enabled"]="This agent version does not have the tool that reads the page this question was asked from switched on.",
        ["page_selection_unavailable"]="What the page had selected when the question was asked is not available at the pinned knowledge and As of frame.",
        ["snapshot_missing"]="The question was asked without an As of frame, so this read is unavailable.",
        ["power_historical"]="TOU products and power hours are current definitions, which a historical view does not read.",
        ["result_too_large"]="The result is larger than one read returns, so nothing was returned rather than a truncated result. Narrow the request and ask again.",
        ["access_denied"]="The read was denied under the agent's current access.",
        ["missing_curve"]="No curve with this code exists in the pinned market data.",
        ["missing_curve_view"]="The pinned As of frame selects no contracts for this curve.",
        ["empty_curve_view"]="This curve has no remaining contracts at the pinned As of frame.",
        ["missing_observation"]="Market data this curve needs is missing at the pinned As of frame.",
        ["source_expired"]="An input this curve needs has expired at the pinned As of frame.",
        ["market_date"]="The As of frame's date differs from the pinned context's date.",
        ["temporal_context"]="The pinned context cannot be read as it was recorded.",
        ["unit_catalog"]="The configured unit catalog is unavailable at the pinned knowledge.",
        ["reference_catalog"]="The reference catalog is unavailable at the pinned knowledge.",
        ["reference_page"]="Choose a supported reference table and search text of at most 160 characters.",
        ["ambiguous_reference"]="A required exact reference is missing or ambiguous at the pinned knowledge.",
        ["market_navigation"]="The curve directory has missing or conflicting classifications.",
        ["revision_conflict"]="An observation has conflicting corrections.",
        ["snapshot_inputs"]="The pinned As of frame does not name each of its inputs exactly once.",
        ["blotter_view"]="Choose available books and portfolios, with supported groupings and measures; lots need contract grouping.",
        ["futures_trade"]="This futures trade is unavailable at the pinned knowledge.",
        ["swap_trade"]="This swap is unavailable at the pinned knowledge.",
        ["futures_definition"]="A held futures contract cannot be resolved at the pinned knowledge.",
        ["swap_context"]="A swap market this read needs is unavailable at the pinned As of frame.",
        ["clock_rule"]="A market clock this read needs is incomplete or unreadable.",
        ["clock_tzdb"]="A market clock this read needs declares a time-zone release other than the installed one.",
        ["power_calendar"]="A settlement index calendar does not cover a whole delivery period.",
        ["knowledge_coverage"]="The pinned knowledge cutoff cannot be certified for this read.",
        ["knowledge_clock"]="The pinned knowledge cutoff cannot be certified for this read.",
        ["knowledge_tracking"]="The pinned knowledge cutoff cannot be certified for this read.",
        ["knowledge_retention"]="The pinned knowledge cutoff cannot be certified for this read.",
        ["knowledge_timeline"]="The pinned knowledge cutoff cannot be certified for this read.",
        ["precision_loss"]="An exact result needs more precision than this read keeps.",
        ["jobs_current_only"]="Jobs are current operational records, which a historical view does not read.",
        ["job_not_found"]="No job of the user's with this id exists. The assistant reads only the user's own jobs.",
        ["historical_read_only"]="A historical view is read-only: curve drafts are saved only over working data. Nothing was saved.",
        ["custom_curve_open_unsaved"]="The user has this curve open with unsaved edits. Ask them to save or discard their edits first. Nothing was saved.",
        ["custom_curve_agent_scope"]="The assistant saves only a new curve, or a draft it saved in this conversation that no one has saved since; never a "+
            "published curve or a person's draft. Nothing was saved.",
        ["custom_curve_agent_claimed"]="The assistant saves one curve draft per step. Nothing was saved.",
        ["custom_curve_catalog_limit"]="This catalog already holds 64 custom curves, the most it can. Nothing was saved.",
        ["custom_curve_conflict"]="The curve or the exact references it was prepared with changed, or its code is already used. Nothing was saved: "+
            "prepare it again, or choose another code.",
        ["delegation_denied"]="The assistant saves a curve draft only while the user may author market data and the grant allows saving drafts. Nothing was saved.",
        ["agent_runtime_unavailable"]="The assistant's turn is no longer running here. Nothing was saved."
    }.ToImmutableDictionary();
    internal const string GenericText="The read refused this request; its code states why.";
    internal static string Text(string code)=>Texts.GetValueOrDefault(code,GenericText);

    // How a read that returned nothing ended (§7.3): a structured refusal of its input or a denial is a refused step, whose
    // publication the database rechecks; a cancellation is the worker's to name; anything else fails the turn tool_failed.
    internal static (string? Code,ToolStepEnd End) Classify(Exception e)=>e switch
    {
        // The API's cancellation, or the database's own copy of the read's bound (D245).
        OperationCanceledException or PostgresException{SqlState:"57014"}=>(null,ToolStepEnd.Interrupted),
        PreviewDeniedException or PostgresException{SqlState:"42501"}=>("access_denied",ToolStepEnd.Refusal),
        MarketDataException m=>(m.Code,ToolStepEnd.Refusal),
        TemporalException t=>(t.Code,ToolStepEnd.Refusal),
        PostgresException{SqlState:"P0001"} p when RefusalCode().IsMatch(p.MessageText)=>(p.MessageText,ToolStepEnd.Refusal),
        _=>("tool_failed",ToolStepEnd.Failure)
    };

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,63}\z")] private static partial Regex RefusalCode();

    // A refusal's content: only its code and fixed text.
    internal static string Refusal(string code)=>JsonSerializer.Serialize(new{code,message=Text(code)});

    // A result within the runtime set's row and byte bounds, whole; otherwise none, never a truncated one (§7.3).
    internal static string? Bounded(JsonObject result,int rows)
    {
        if(rows>RuntimeSet.ToolResultRows)return null;
        var content=result.ToJsonString();
        return Encoding.UTF8.GetByteCount(content)<=RuntimeSet.ToolResultBytes?content:null;
    }

    // Runs one request as a step (§7.3): checks its arguments and what it needs of the pin before any read, then reads in
    // the agent's request context, issuing the result's targets from the turn's next handle. A read that bound its context
    // joins the source set with every access it runs under, even when it is then denied or fails; a refusal before that
    // adds none. Navigate resolves its target before its check, refusing an unissued handle with the input kept. Save curve
    // draft writes, and records its successful step itself, in the one transaction its write commits in (D260).
    public async Task<ToolStep> Run(TurnReads turn,TurnTargets targets,ModelToolRequest request,TurnWrites writes,CancellationToken ct)
    {
        var (tool,input)=(request.Name,(JsonElement?)request.Input);
        ToolStep Before(string code,JsonElement? kept)=>new(tool,null,kept,code,null,[],Refusal(code),ToolStepEnd.Refusal);
        ToolCall call;
        PageRead? dispatched;
        try
        {
            (call,dispatched)=Requested(turn,request);
            if(call.Arguments is SaveCurveArguments draft&&DraftRefusal(turn,draft) is {} refusal)throw new ToolRefusal(refusal);
            Require(turn.Pin,call.Operation.Needs);
        }
        catch(ToolRefusal refused){return Before(refused.Code,refused.Code=="tool_arguments_invalid"?null:input);}
        catch(MarketDataException refused){return Before(refused.Code,input);}
        if(call.Arguments is NavigateArguments navigate)
        {
            if(Target(targets,navigate) is not {} target)return Before("tool_arguments_invalid",input);
            // A historical view does not move to a page of current operational records, as it does not read them.
            if(!turn.Pin.Working&&PageReports.Operations(target.Page).Any(o=>Operations[o].Needs==ToolNeeds.Operational))return Before("jobs_current_only",input);
            return await Navigate(turn,tool,input!.Value,target,ct);
        }
        if(call.Arguments is SaveCurveArguments save)return await SaveStep(turn,targets,tool,input!.Value,call,save,writes,ct);
        var operation=call.Operation;
        var access=operation.Access(database.Manifest);
        DateTimeOffset? readAt=null;
        ToolStep Step(string? code,string? content,ToolStepEnd end)=>new(tool,readAt is null?null:operation.Id,input,code,readAt,readAt is null?[]:access,content,end);
        try
        {
            var result=await database.WithAgent(turn.Agent,turn.ReadLimit,Tools.Single(t=>t.Name==operation.Tool).Permissions,at=>readAt=at,
                (connection,read)=>Read(connection,turn.Pin,call,read),ct);
            if(!result.Access.IsDefaultOrEmpty)access=[..access.Concat(result.Access).Distinct()];
            if(operation.Id=="custom_curves.prepare_v1")result.Value["proposal"]=new JsonObject{["turnId"]=turn.TurnId.ToString(),["step"]=targets.Steps+1};
            return Returned(tool,request.Input,call,turn.Pin,readAt!.Value,access,result,targets.Next,dispatched?.Layout);
        }
        catch(Exception e)
        {
            var (code,end)=Classify(e);
            if(end==ToolStepEnd.Failure)logger.LogError("A tool read failed: {ErrorType}",e.GetType().Name);
            if(end!=ToolStepEnd.Refusal)return Step(code,null,end);
            var shown=Shown(operation,dispatched is not null,code!);
            return Step(shown,Refusal(shown),end);
        }
    }

    // A read's step once it returned (§7.3): its result within the runtime set's row and byte bounds, with the targets it
    // issues numbered from the turn's next handle and, for Read current page on the Blotter, the layout its report states;
    // otherwise result_too_large, never a truncated result, issuing none. Rows are counted before any target is issued, and
    // bytes after, since handles count toward them.
    internal static ToolStep Returned(string tool,JsonElement input,ToolCall call,TurnPin pin,DateTimeOffset readAt,ImmutableArray<ReadAccess> access,
        ToolResult result,int next,BlotterLayout? layout=null)
    {
        ToolStep Step(string? code,string content,ToolStepEnd end)=>new(tool,call.Operation.Id,input,code,readAt,access,content,end);
        var tooLarge=Step("result_too_large",Refusal("result_too_large"),ToolStepEnd.Refusal);
        if(result.Rows>RuntimeSet.ToolResultRows)return tooLarge;
        var issued=NavigationTargets.Issue(call,result.Value,next);
        return Bounded(Envelope(pin,call.Operation,readAt,result,issued[0].Handle,layout),result.Rows) is {} content?Step(null,content,ToolStepEnd.Result) with{Issued=issued}
            :tooLarge;
    }

    // The target a Navigate request names (§7.3): a page Navigate opens, or a handle a published step of this turn issued
    // on such a page; none for any other handle (D250).
    internal static NavigationTarget? Target(TurnTargets targets,NavigateArguments navigate)=>
        navigate.Page is {} page?NavigationTargets.Page(page):targets.Find(navigate.Handle!) is {} target&&PageReports.Opens(target.Page)?target:null;

    // Navigate's step (§7.3): its target, once its page is checked in a request context issued to the agent, under the
    // turn's grant against the delegator's permissions, by the accesses and permissions that page's reads run under, which
    // then join the source set. A page the agent cannot open for its delegator is tool_arguments_invalid, keeping the
    // input and adding nothing to the source set (D190); a check stopped or failing once bound ends as a read does,
    // recording the instant and accesses it ran under (§7.5).
    async Task<ToolStep> Navigate(TurnReads turn,string tool,JsonElement input,NavigationTarget target,CancellationToken ct)
    {
        ToolStep Unopened()=>new(tool,null,input,"tool_arguments_invalid",null,[],Refusal("tool_arguments_invalid"),ToolStepEnd.Refusal);
        var access=NavigationTargets.Access(target.Page,database.Manifest);
        DateTimeOffset? checkedAt=null;
        bool opens;
        try{opens=await database.WithAgent(turn.Agent,turn.ReadLimit,NavigationTargets.Permissions(target.Page),at=>checkedAt=at,(connection,c)=>Opens(connection,access,c),ct);}
        catch(Exception e)
        {
            var (code,end)=Classify(e);
            if(end==ToolStepEnd.Refusal)return Unopened();
            if(end==ToolStepEnd.Failure)logger.LogError("A navigation target's check failed: {ErrorType}",e.GetType().Name);
            return new(tool,checkedAt is null?null:NavigationTargets.Operation,input,code,checkedAt,checkedAt is null?[]:access,null,end);
        }
        return opens?new(tool,NavigationTargets.Operation,input,null,checkedAt,access,NavigationTargets.Echo(target),ToolStepEnd.Result){Target=target}:Unopened();
    }

    // Whether the bound request context opens a page: each access its reads run under holds.
    static async Task<bool> Opens(NpgsqlConnection connection,ImmutableArray<ReadAccess> access,CancellationToken ct)
    {
        foreach(var held in access)
        {
            await using var command=held.CatalogId is {} catalog?ScheduleStore.Command(connection,held.Access switch
            {
                "market_access"=>"SELECT security.market_access($1,$2)",
                "reference_access"=>"SELECT security.reference_access($1,$2)",
                "trading_access"=>"SELECT security.trading_access($1,$2)",
                _=>throw new InvalidOperationException("An access has no check.")
            },catalog,held.Permission)
                :held.Access=="job_access"?ScheduleStore.Command(connection,"SELECT security.has_permission($1)",held.Permission)
                :throw new InvalidOperationException("An access has no check.");
            if(!Equals(await command.ExecuteScalarAsync(ct),true))return false;
        }
        return true;
    }

    // A read's refusal as its step names it: for a page's selection, the refusals that mean the selection is unavailable
    // at the pin are page_selection_unavailable; every other refusal keeps its code.
    internal static string Shown(ToolOperation operation,bool dispatched,string code)=>
        dispatched&&operation.SelectionRefusals.Contains(code)?"page_selection_unavailable":code;

    // The call a request names: its own read, or for Read current page the read its stored page report dispatches, while
    // the version has that read's tool switched on, with that report.
    internal static (ToolCall Call,PageRead? Dispatched) Requested(TurnReads turn,ModelToolRequest request)
    {
        // The adapter fails a call that requests a tool it did not offer, so this is defense in depth: such a request
        // reads nothing, and the database records no step for a tool the version has not switched on (turn_input), a
        // write it does not take, which fails the turn agent_runtime_unavailable.
        if(!turn.Tools.Contains(request.Name))throw new ToolRefusal("tool_not_enabled");
        if(request.Name!="read_current_page")return(Parse(request.Name,request.Input),null);
        new ToolInput(request.Input).Exactly();
        return PageReports.Dispatch(turn.PageReport) switch
        {
            null=>throw new ToolRefusal("page_not_supported"),
            {Call:null}=>throw new ToolRefusal("page_selection_unavailable"),
            {Call:{} call} when!turn.Tools.Contains(call.Operation.Tool)=>throw new ToolRefusal("tool_not_enabled"),
            {Call:{} call} read=>(call,read)
        };
    }

    // What a read needs of the pin, checked before it runs (§7.2): working data for current-only definitions
    // (power_historical) and for operational records (jobs_current_only), and a frame where it consumes one
    // (snapshot_missing).
    static void Require(TurnPin pin,ToolNeeds needs)
    {
        if(needs is ToolNeeds.CurrentOnly or ToolNeeds.CurrentOnlyFrame)pin.CurrentOnly();
        if(needs is ToolNeeds.Operational&&!pin.Working)throw new MarketDataException("jobs_current_only","Jobs are not read under a historical view.");
        if(Consumes(needs))pin.RequireFrame();
    }

    static bool Consumes(ToolNeeds needs)=>needs is ToolNeeds.Frame or ToolNeeds.CurrentOnlyFrame;

    // One read at the turn's pin (§7.2): page views and trades as of the turn's context, read exactly as its record names
    // it, and current-only definitions with no context bound.
    async Task<ToolResult> Read(NpgsqlConnection connection,TurnPin pin,ToolCall call,CancellationToken ct)
    {
        async Task<ScheduleContext> Context()=>(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,pin.ContextId,ct)).Context;
        return call switch
        {
            {Operation.Id:"custom_curves.catalog_v1"}=>await CurveCatalog(connection,pin.Page(await Context()),ct),
            {Operation.Id:"custom_curves.prepare_v1",Arguments:PrepareCurveArguments curve}=>await PrepareCurve(connection,pin.Page(await Context()),curve,ct),
            {Operation.Id:"market_data.curves_v1"}=>ToolResults.Curves(await market.Navigation(connection,pin.Page(await Context()),ct)),
            {Operation.Id:"market_data.curve_v1",Arguments:CurveArguments curve}=>ToolResults.Curve(await market.Evaluate(connection,pin.Page(await Context()),curve.Curve,ct)),
            {Operation.Id:"market_data.observations_v1",Arguments:CurveArguments curve}=>
                ToolResults.Observations(await market.Evaluate(connection,pin.Page(await Context()),curve.Curve,ct)),
            {Operation.Id:"market_data.contract_definitions_v1"}=>ToolResults.ContractDefinitions(await schedules.Library(connection,pin.Page(await Context()),ct)),
            {Operation.Id:"reference_data.table_v1",Arguments:ReferenceArguments table}=>
                ToolResults.Reference(await market.References(connection,pin.Page(await Context()),table.Table,0,table.Search,ct)),
            {Operation.Id:"schedules.library_v1"}=>ToolResults.Library(await schedules.Library(connection,pin.Page(await Context()),ct)),
            {Operation.Id:"schedules.tou_products_v1"}=>ToolResults.TouProducts(await schedules.TouProducts(connection,pin.CurrentOnly(),ct)),
            {Operation.Id:"schedules.power_hours_v1"}=>ToolResults.PowerHours(await schedules.PowerCatalog(connection,pin.CurrentOnly(),ct)),
            {Operation.Id:"trade_parties.versions_v1",Arguments:PartyArguments party}=>
                ToolResults.Parties(await parties.Catalog(connection,pin.Trading(await Context()),party.Kind,ct),party.Kind),
            {Operation.Id:"trades.blotter_v1",Arguments:BlotterArguments blotter}=>ToolResults.Blotter(await trades.Blotter(connection,pin.Trading(await Context()),blotter.View,ct)),
            {Operation.Id:"trades.trade_v1",Arguments:TradeArguments trade}=>await Trade(connection,pin.Trading(await Context()),trade,ct),
            {Operation.Id:"trades.ppa_book_v1",Arguments:PpaBookArguments book}=>ToolResults.PpaBook(await trades.PpaBook(connection,pin.Trading(await Context()),book.Book,ct)),
            {Arguments:JobListArguments list}=>await Jobs(connection,list,ct),
            {Arguments:JobHistoryArguments job}=>await JobHistory(connection,job,ct),
            _=>throw new InvalidOperationException("An operation has no read.")
        };
    }

    // A trade's editor read, then the deal's saved display formats it is shown by, in the same transaction under the
    // trading context it bound. Formats are current shared presentation, which the page reads the same way.
    async Task<ToolResult> Trade(NpgsqlConnection connection,TradingView view,TradeArguments trade,CancellationToken ct)
    {
        var editor=trade.InstrumentType=="future"?await trades.FuturesTrade(connection,view,trade.TradeId,trade.Revision,ct)
            :trade.InstrumentType=="ppa"?await trades.PpaTrade(connection,view,trade.TradeId,trade.Revision,ct)
            :await trades.SwapTrade(connection,view,trade.TradeId,trade.Revision,ct);
        var formats=(await DealAmountFormats.All(connection,database.Manifest.CatalogId,ct)).GetValueOrDefault(trade.TradeId);
        return ToolResults.Trade(editor,trade.InstrumentType,formats);
    }

    // A result with the knowledge it was read at (§7.2): the turn's pinned context and cutoff, or for current-only
    // definitions and operational records the instant they were read, never the cutoff; the As of frame where the read
    // consumed one; the handle of its own target (§7.3); and, from turns_v10, the Blotter's layout its page report states (D263).
    internal static JsonObject Envelope(TurnPin pin,ToolOperation operation,DateTimeOffset readAt,ToolResult result,string target,BlotterLayout? layout=null)
    {
        var context=operation.Needs is ToolNeeds.CurrentOnly or ToolNeeds.CurrentOnlyFrame or ToolNeeds.Operational
            ?new JsonObject{["knowledge"]=operation.Needs==ToolNeeds.Operational?"current_operations":"current_definitions",["readAt"]=JsonSerializer.SerializeToNode(readAt)}
            :new JsonObject{["knowledge"]=pin.Working?"working_data":"retained",["contextId"]=pin.ContextId.ToString(),["cutoff"]=JsonSerializer.SerializeToNode(pin.Cutoff)};
        if(Consumes(operation.Needs)&&pin.Frame is {} frame)
            context["frame"]=new JsonObject{["id"]=frame.Id.ToString(),["name"]=frame.Name,["at"]=JsonSerializer.SerializeToNode(frame.At)};
        var envelope=new JsonObject{["operation"]=operation.Id,["context"]=context};
        if(result.Displayed)envelope["amountDisplay"]=ToolAmountDisplay.Implementation;
        envelope["target"]=target;
        if(layout is {} shown)envelope["layout"]=new JsonObject{["pane"]=new JsonObject{["details"]=shown.Details,["charts"]=shown.Charts},["poppedOut"]=shown.PoppedOut};
        envelope["result"]=result.Value;
        return envelope;
    }
}

// A tool input's members, each read with its exact JSON type; any other member, type or value refuses the input as
// tool_arguments_invalid. Text holds no control character, which a step's stored input could not keep.
sealed class ToolInput
{
    const int MaximumItems=64;
    readonly Dictionary<string,JsonElement> members=new(StringComparer.Ordinal);

    public ToolInput(JsonElement input)
    {
        if(input.ValueKind!=JsonValueKind.Object)throw Invalid();
        foreach(var member in input.EnumerateObject())if(!members.TryAdd(member.Name,member.Value))throw Invalid();
    }

    public void Exactly(params string[] names)
    {
        if(members.Count!=names.Length||!names.All(members.ContainsKey))throw Invalid();
    }

    // Whether a member holds a value rather than null.
    public bool Has(string name)=>members.TryGetValue(name,out var value)&&value.ValueKind!=JsonValueKind.Null;

    // Members another view takes, named as null.
    public void Null(params string[] names)
    {
        if(!names.All(n=>members.TryGetValue(n,out var value)&&value.ValueKind==JsonValueKind.Null))throw Invalid();
    }

    public string Choice(string name,params ImmutableArray<string> values)=>
        String(Member(name,JsonValueKind.String)) is var value&&values.Contains(value)?value:throw Invalid();

    public string Text(string name,int minimum,int maximum,Regex? pattern=null)
    {
        var value=String(Member(name,JsonValueKind.String));
        return value.Length>=minimum&&value.Length<=maximum&&Plain(value)&&(pattern is null||pattern.IsMatch(value))?value:throw Invalid();
    }

    public string Formula(string name)
    {
        var value=String(Member(name,JsonValueKind.String));
        return value.Length is >0 and <=6144&&Plain(value.Replace("\n","").Replace("\r","").Replace("\t",""))?value:throw Invalid();
    }
    public Guid Id(string name)=>Canonical(Member(name,JsonValueKind.String));
    public bool Flag(string name)=>members.TryGetValue(name,out var value)&&value.ValueKind is JsonValueKind.True or JsonValueKind.False?value.GetBoolean():throw Invalid();
    // A member that is an object, read as members of its own.
    public ToolInput Members(string name)=>new(Member(name,JsonValueKind.Object));

    public ImmutableArray<Guid> Ids(string name)
    {
        var ids=Items(name).Select(Canonical).ToImmutableArray();
        return ids.Distinct().Count()==ids.Length?ids:throw Invalid();
    }

    public ImmutableArray<string> Choices(string name,params ImmutableArray<string> values)
    {
        var chosen=Items(name).Select(i=>i.ValueKind==JsonValueKind.String&&String(i) is var value&&values.Contains(value)?value:throw Invalid()).ToImmutableArray();
        return chosen.Distinct().Count()==chosen.Length?chosen:throw Invalid();
    }

    // A whole number in a range, or for an optional member null: always named, never left to a default.
    public long Whole(string name,long minimum,long maximum)=>
        Member(name,JsonValueKind.Number).TryGetInt64(out var value)&&value>=minimum&&value<=maximum?value:throw Invalid();
    public long? OptionalWhole(string name,long minimum,long maximum)=>
        members.TryGetValue(name,out var value)&&value.ValueKind==JsonValueKind.Null?null:Whole(name,minimum,maximum);

    // A revision, or null for the latest: always named, never left to a default.
    public int? Revision(string name)
    {
        if(!members.TryGetValue(name,out var value))throw Invalid();
        if(value.ValueKind==JsonValueKind.Null)return null;
        return value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out var revision)&&revision>=1?revision:throw Invalid();
    }

    IEnumerable<JsonElement> Items(string name)
    {
        var items=Member(name,JsonValueKind.Array);
        return items.GetArrayLength()<=MaximumItems?items.EnumerateArray():throw Invalid();
    }

    JsonElement Member(string name,JsonValueKind kind)=>members.TryGetValue(name,out var value)&&value.ValueKind==kind?value:throw Invalid();

    static Guid Canonical(JsonElement id)=>id.ValueKind==JsonValueKind.String&&String(id) is {Length:36} text&&Guid.TryParseExact(text,"D",out var parsed)
        &&parsed.ToString()==text?parsed:throw Invalid();

    // A string's text; an escape that names no character, such as an unpaired surrogate, refuses the input, so text is
    // always whole characters.
    static string String(JsonElement value)
    {
        try{return value.GetString()!;}
        catch(InvalidOperationException){throw Invalid();}
    }

    static bool Plain(string text)=>!text.Any(char.IsControl);

    public static ToolRefusal Invalid()=>new("tool_arguments_invalid");
}
