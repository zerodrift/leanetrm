using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;

// What the page assistant's read tools decide without a database (docs/ai-assistant.md §7.2, §7.3, §8): the frame and
// knowledge a turn's reads rebuild from its record, the tools a version offers and the inputs they take, the page
// reports ask accepts and Read current page dispatches, how a read's refusal or failure is named, a result's bounds,
// envelope and projection, and the amounts a result shows as the page does.
static class AgentToolChecks
{
    public static void Run()
    {
        TurnPins();
        Definitions();
        Inputs();
        Reports();
        Refusals();
        Results();
        Display();
    }

    static readonly Guid Catalog=Guid.Parse("c1000000-0000-0000-0000-000000000001");
    static readonly PreviewManifest Manifest=new("local-synthetic-preview",Catalog,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),[],null);
    static readonly DateTimeOffset Cutoff=DateTimeOffset.Parse("2026-10-21T12:00:00.123456Z");
    static readonly InputFrame Working=new(Guid.NewGuid(),"Initial synthetic inputs",DateTimeOffset.Parse("2020-12-18T16:00:00Z"),[Guid.NewGuid()],
        ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[Guid.NewGuid()]));

    static TurnPin Pin(string mode,Guid context,InputFrame? frame,Guid? catalog=null,Guid? frameId=null)=>TurnPin.From(Record(mode,context,Cutoff,frameId??frame?.Id),
        frame is null?Null():Json(TurnPin.Record(frame)),catalog??Catalog,Manifest);

    static void TurnPins()
    {
        var (a,b,c)=(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        var working=new InputFrame(Guid.NewGuid(),"Initial synthetic inputs",DateTimeOffset.Parse("2020-12-18T16:00:00Z"),[a,b],
            ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[c]));
        var retainedId=Guid.NewGuid();
        var retained=working with{Id=Guid.NewGuid(),Name="Certified market inputs",ContextId=retainedId,FrozenObservationIds=[b],ResolvesLatestCorrection=true,
            SwapMarketSelections=ImmutableDictionary<Guid,ImmutableArray<Guid>>.Empty.Add(Guid.NewGuid(),[a])};
        var captured=Guid.NewGuid();

        var rebuilt=Pin("current",captured,working);
        var historical=Pin("retained",retainedId,retained);
        Require(Same(rebuilt.Frame!,working)&&Same(historical.Frame!,retained),
            "a turn's record rebuilds the page's exact As of frame: its instant, name, observations, frozen observations, curve contracts, swap market "+
            "selections and latest-correction resolution, from the same pins the ask records");
        Console.WriteLine("PASS a turn's record rebuilds the page's exact As of frame, for working data and a retained context");

        foreach(var (name,build) in new (string,Func<TurnPin>)[]{
            ("working data without a frame",()=>Pin("current",captured,null)),
            ("another catalog",()=>Pin("current",captured,working,Guid.NewGuid())),
            ("a frame id the pins do not name",()=>Pin("current",captured,working,frameId:Guid.NewGuid())),
            ("an unknown knowledge mode",()=>Pin("elsewhere",captured,working))})
            Require(Refuses<InvalidOperationException>(()=>build()),"a record the worker cannot read is refused: "+name);
        Console.WriteLine("PASS a record without its frame, of another catalog, naming another frame or an unknown mode is refused");

        var capturedContext=new ScheduleContext("2020-12-18",7,Cutoff.ToString("O"),captured);
        var retainedContext=new ScheduleContext("2020-12-18",5,Cutoff.ToString("O"),retainedId);
        Require(rebuilt.Exact(capturedContext)==capturedContext&&Refuses<InvalidOperationException>(()=>rebuilt.Exact(capturedContext with{KnownAt=Cutoff.AddTicks(10).ToString("O")}))
            &&Refuses<InvalidOperationException>(()=>rebuilt.Exact(retainedContext)),"a read binds only the context the record names, at its recorded cutoff");
        var page=rebuilt.Page(capturedContext);
        var currentOnly=rebuilt.CurrentOnly();
        var trading=rebuilt.Trading(capturedContext);
        Require(page.Context==capturedContext&&page.Frame==rebuilt.Frame&&page.Frame!.ContextId is null&&currentOnly.Context is null&&currentOnly.Frame==rebuilt.Frame
            &&(trading.Context,trading.Frame,trading.Working)==(capturedContext,rebuilt.Frame,false),
            "working data reads its page views as of the context the ask captured at the working frame, current-only definitions with no context, and trades as a reread");
        var view=historical.Page(retainedContext);
        Require(view.Context==retainedContext&&view.Frame==historical.Frame&&(historical.Trading(retainedContext).Context,historical.Trading(retainedContext).Frame)==(retainedContext,historical.Frame)
            &&Code(()=>historical.CurrentOnly())=="power_historical","a retained context reads at its own frame and refuses current-only definitions power_historical");
        var unframed=Pin("retained",retainedId,null);
        Require(unframed.Page(retainedContext).Frame is null&&Code(()=>unframed.Trading(retainedContext))=="snapshot_missing"&&Code(()=>unframed.RequireFrame())=="snapshot_missing",
            "a turn that pinned no frame reads what needs none and refuses a frame-consuming read snapshot_missing");
        Require(Code(()=>PinnedContext.Rereading(capturedContext,retained))=="snapshot_missing","only a working frame is read as of a captured context");
        Console.WriteLine("PASS working data reads as of its ask's capture and current-only definitions with none; a retained context refuses current-only "+
            "definitions power_historical; a frame-consuming read without a frame is snapshot_missing");
    }

    static void Definitions()
    {
        Require(AgentTools.Offered(["read_trades","navigate","read_current_page","read_market_data","show_card"]).Select(t=>t.Name)
                .SequenceEqual(["read_current_page","read_market_data","read_trades"])&&AgentTools.Offered([]).IsEmpty,
            "a version's switched-on read tools are offered in the fixed order, a version without tools offers none, and Navigate is not offered");
        var permissions=AgentTools.Tools.ToDictionary(t=>t.Name,t=>string.Join(",",t.Permissions));
        Require(permissions.Count==6&&permissions["read_current_page"]==""&&permissions["read_market_data"]=="ReadMarketData"
            &&permissions["read_trades"]=="ReadMarketData,ReadTrades"&&permissions["read_trade_parties"]=="ReadMarketData,ReadTradeParties"
            &&permissions["read_schedules"]=="ReadMarketData"&&permissions["read_reference_data"]=="ReadMarketData",
            "each tool reads under exactly the permissions the database lists for it (assistant.agent_tools)");
        foreach(var tool in AgentTools.Tools)
        {
            Require(tool.InputSchema.GetProperty("type").GetString()=="object"&&tool.InputSchema.GetProperty("additionalProperties").ValueKind==JsonValueKind.False
                &&tool.Definition==new ModelTool(tool.Name,tool.Description,tool.InputSchema)&&tool.Description.Length>0,$"{tool.Name} states a closed object input schema");
            // A member a view does not take is null rather than absent, so an input the schema accepts names every member,
            // and the schema needs no top-level composition, which a provider's input schema need not accept.
            var stated=tool.InputSchema.GetProperty("properties").EnumerateObject().Select(p=>p.Name).Order(StringComparer.Ordinal);
            var required=tool.InputSchema.TryGetProperty("required",out var names)?names.EnumerateArray().Select(n=>n.GetString()!).Order(StringComparer.Ordinal):Enumerable.Empty<string>();
            Require(stated.SequenceEqual(required)&&!new[]{"anyOf","oneOf","allOf","if","then","else","not"}.Any(k=>tool.InputSchema.TryGetProperty(k,out _)),
                $"{tool.Name} requires every member it states, with no top-level composition");
        }
        Require(RuntimeSet.Turns=="turns_v3"&&RuntimeSet.ToolResultRows==500&&RuntimeSet.ToolResultBytes==65536&&RuntimeSet.WorkspaceTurns<RuntimeSet.AgentPool
            &&RuntimeSet.ToolRead==TimeSpan.FromSeconds(5),"turns_v3 bounds tool results and keeps a workspace's turns below its agent pools");
        Console.WriteLine("PASS a version's switched-on read tools are offered in the fixed order with closed schemas that require every member they "+
            "state, and their permissions; Navigate is not offered; turns_v3 bounds results and keeps workspace turns below the agent pools");
    }

    const string Book="d1000000-0000-0000-0000-000000000001",Trade="71000000-0000-0000-0000-000000000009";
    // Every member of Read trades is named; those the other view takes are null.
    const string NoTrade=""" "tradeId":null,"instrumentType":null,"revision":null""",NoBlotter=""" "mode":null,"bookIds":null,"portfolioIds":null,"groupBy":null,"measures":null""";
    const string Blotter=$$"""{"view":"blotter","mode":"trades","bookIds":[],"portfolioIds":[],"groupBy":[],"measures":[],{{NoTrade}}}""";

    static void Inputs()
    {
        var blotter=Blotter;
        foreach(var (tool,input,operation) in new[]{
            ("read_market_data","""{"view":"curves","curve":null}""","market_data.curves_v1"),
            ("read_market_data","""{"view":"curve","curve":"future"}""","market_data.curve_v1"),
            ("read_market_data","""{"view":"observations","curve":"power.de-base_1"}""","market_data.observations_v1"),
            ("read_market_data","""{"view":"contract_definitions","curve":null}""","market_data.contract_definitions_v1"),
            ("read_trades",blotter,"trades.blotter_v1"),
            ("read_trades",$$"""{"view":"blotter","mode":"pivot","bookIds":["{{Book}}"],"portfolioIds":[],"groupBy":["contract"],"measures":["lots","presentValue"],{{NoTrade}}}""","trades.blotter_v1"),
            ("read_trades",$$"""{"view":"trade",{{NoBlotter}},"tradeId":"{{Trade}}","instrumentType":"future","revision":null}""","trades.trade_v1"),
            ("read_trades",$$"""{"view":"trade",{{NoBlotter}},"tradeId":"{{Trade}}","instrumentType":"financial_swap","revision":2}""","trades.trade_v1"),
            ("read_trade_parties","""{"kind":"legal_entity"}""","trade_parties.versions_v1"),
            ("read_schedules","""{"view":"library"}""","schedules.library_v1"),
            ("read_schedules","""{"view":"tou_products"}""","schedules.tou_products_v1"),
            ("read_schedules","""{"view":"power_hours"}""","schedules.power_hours_v1"),
            ("read_reference_data","""{"table":"units","search":""}""","reference_data.table_v1"),
            ("read_reference_data",$$"""{"table":"units","search":"{{new string('€',160)}}"}""","reference_data.table_v1")})
            Require(AgentTools.Parse(tool,Json(input)).Operation.Id==operation,$"{tool} accepts {input}");
        var pivot=AgentTools.Parse("read_trades",Json($$"""{"view":"blotter","mode":"pivot","bookIds":["{{Book}}"],"portfolioIds":[],"groupBy":["contract"],"measures":["lots"],{{NoTrade}}}"""));
        var latest=AgentTools.Parse("read_trades",Json($$"""{"view":"trade",{{NoBlotter}},"tradeId":"{{Trade}}","instrumentType":"future","revision":null}"""));
        Require(pivot.Arguments is BlotterArguments{View:{Mode:"pivot",PivotConvention:TradingBlotter.PivotConvention}} totals&&totals.View.BookIds.Single()==Guid.Parse(Book)
            &&AgentTools.Parse("read_trades",Json(blotter)).Arguments is BlotterArguments{View:{Mode:"trades",PivotConvention:null}}
            &&latest.Arguments==new TradeArguments(Guid.Parse(Trade),"future",null),
            "a pivot reads under the operation's fixed totals convention, and a trade's latest revision is named explicitly as null");
        Console.WriteLine("PASS each tool accepts every member its schema states, its view's own as stated and the others null, as its operation's arguments");

        var ids=string.Join(",",Enumerable.Range(0,65).Select(_=>$"\"{Guid.NewGuid()}\""));
        foreach(var (tool,input) in new[]{
            ("read_market_data","{}"),("read_market_data","""{"view":"curve"}"""),("read_market_data","""{"view":"curves"}"""),
            ("read_market_data","""{"view":"curve","curve":null}"""),("read_market_data","""{"view":"curves","curve":"future"}"""),
            ("read_market_data","""{"view":"curve","curve":""}"""),("read_market_data","""{"view":"curve","curve":"-future"}"""),
            ("read_market_data",$$"""{"view":"curve","curve":"{{new string('a',65)}}"}"""),("read_market_data","""{"view":"Curves","curve":null}"""),
            ("read_market_data","""{"view":1,"curve":null}"""),("read_market_data","""{"view":"curves","curve":null,"extra":null}"""),("read_market_data","[]"),
            ("read_market_data","\"curves\""),("read_market_data","""{"view":"curve","curve":"fut\u0000ure"}"""),
            ("read_market_data","""{"view":"curves","view":"curves","curve":null}"""),
            ("read_trades","""{"view":"blotter"}"""),
            ("read_trades","""{"view":"blotter","mode":"trades","bookIds":[],"portfolioIds":[],"groupBy":[],"measures":[]}"""),
            ("read_trades","""{"view":"blotter","mode":"trades","bookIds":[],"portfolioIds":[],"groupBy":[],"measures":[],"tradeId":null,"instrumentType":null}"""),
            ("read_trades",$$"""{"view":"blotter","mode":"trades","bookIds":[],"portfolioIds":[],"groupBy":[],"measures":[],"tradeId":"{{Trade}}","instrumentType":null,"revision":null}"""),
            ("read_trades","""{"view":"blotter","mode":"trades","bookIds":[],"portfolioIds":[],"groupBy":[],"measures":[],"tradeId":null,"instrumentType":null,"revision":1}"""),
            ("read_trades",$$"""{"view":"blotter","mode":null,"bookIds":[],"portfolioIds":[],"groupBy":[],"measures":[],{{NoTrade}}}"""),
            ("read_trades",$$"""{"view":"blotter","mode":"trades","bookIds":null,"portfolioIds":[],"groupBy":[],"measures":[],{{NoTrade}}}"""),
            ("read_trades",$$"""{"view":"blotter","mode":"trades","bookIds":[],"portfolioIds":[],"groupBy":["book"],"measures":[],{{NoTrade}}}"""),
            ("read_trades",$$"""{"view":"blotter","mode":"pivot","bookIds":[],"portfolioIds":[],"groupBy":["book"],"measures":[],{{NoTrade}}}"""),
            ("read_trades",$$"""{"view":"blotter","mode":"pivot","bookIds":[],"portfolioIds":[],"groupBy":[],"measures":["tradeCount"],{{NoTrade}}}"""),
            ("read_trades",$$"""{"view":"blotter","mode":"trades","bookIds":["{{Book.ToUpperInvariant()}}"],"portfolioIds":[],"groupBy":[],"measures":[],{{NoTrade}}}"""),
            ("read_trades",$$"""{"view":"blotter","mode":"trades","bookIds":["{{Book}}","{{Book}}"],"portfolioIds":[],"groupBy":[],"measures":[],{{NoTrade}}}"""),
            ("read_trades",$$"""{"view":"blotter","mode":"trades","bookIds":[{{ids}}],"portfolioIds":[],"groupBy":[],"measures":[],{{NoTrade}}}"""),
            ("read_trades",$$"""{"view":"blotter","mode":"trades","bookIds":[1],"portfolioIds":[],"groupBy":[],"measures":[],{{NoTrade}}}"""),
            ("read_trades",$$"""{"view":"blotter","mode":"pivot","bookIds":[],"portfolioIds":[],"groupBy":["book","book"],"measures":["tradeCount"],{{NoTrade}}}"""),
            ("read_trades",$$"""{"view":"blotter","mode":"pivot","bookIds":[],"portfolioIds":[],"groupBy":["book"],"measures":["notional"],{{NoTrade}}}"""),
            ("read_trades",$$"""{"view":"trade","tradeId":"{{Trade}}","instrumentType":"future","revision":null}"""),
            ("read_trades",$$"""{"view":"trade",{{NoBlotter}},"tradeId":"{{Trade}}","instrumentType":"future"}"""),
            ("read_trades",$$"""{"view":"trade","mode":"trades","bookIds":null,"portfolioIds":null,"groupBy":null,"measures":null,"tradeId":"{{Trade}}","instrumentType":"future","revision":null}"""),
            ("read_trades",$$"""{"view":"trade","mode":null,"bookIds":[],"portfolioIds":null,"groupBy":null,"measures":null,"tradeId":"{{Trade}}","instrumentType":"future","revision":null}"""),
            ("read_trades",$$"""{"view":"trade",{{NoBlotter}},"tradeId":null,"instrumentType":"future","revision":null}"""),
            ("read_trades",$$"""{"view":"trade",{{NoBlotter}},"tradeId":"{{Trade}}","instrumentType":null,"revision":null}"""),
            ("read_trades",$$"""{"view":"trade",{{NoBlotter}},"tradeId":"{{Trade}}","instrumentType":"future","revision":0}"""),
            ("read_trades",$$"""{"view":"trade",{{NoBlotter}},"tradeId":"{{Trade}}","instrumentType":"future","revision":1.5}"""),
            ("read_trades",$$"""{"view":"trade",{{NoBlotter}},"tradeId":"{{Trade}}","instrumentType":"future","revision":"1"}"""),
            ("read_trades",$$"""{"view":"trade",{{NoBlotter}},"tradeId":"{{Trade}}","instrumentType":"forward","revision":null}"""),
            ("read_trades",$$"""{"view":"trade",{{NoBlotter}},"tradeId":"{{{Trade}}}","instrumentType":"future","revision":null}"""),
            ("read_trades",$$"""{"view":"trade",{{NoBlotter}},"tradeId":"{{Trade}}","instrumentType":"future","revision":null,"extra":null}"""),
            ("read_trade_parties","""{"kind":"broker"}"""),("read_trade_parties","""{"kind":"legal_entity","extra":null}"""),
            ("read_schedules","""{"view":"calendar"}"""),("read_schedules","{}"),
            ("read_reference_data","""{"table":"units"}"""),("read_reference_data","""{"table":"secrets","search":""}"""),
            ("read_reference_data",$$"""{"table":"units","search":"{{new string('a',161)}}"}"""),("read_reference_data","""{"table":"units","search":"a\nb"}"""),
            ("read_reference_data","""{"table":"units","search":"\ud800"}"""),("read_reference_data","""{"table":"units","search":"\udc00\ud800"}"""),
            ("read_reference_data","""{"table":"units","search":null}"""),("read_market_data","""{"view":"curve","curve":"\ud800"}"""),
            ("read_current_page","{}"),("navigate","{}")})
            Require(Code(()=>AgentTools.Parse(tool,Json(input)))=="tool_arguments_invalid",$"{tool} refuses {input}");
        Console.WriteLine("PASS each tool refuses missing, unknown or repeated members, wrong types, values outside its schema, control characters, "+
            "unpaired surrogates, a view's member as null and another view's member named as tool_arguments_invalid");

        // Reading a string refuses an escaped unpaired surrogate, so text only ever holds whole characters, and a pair, which
        // holds no control character, is accepted.
        Require(Refuses<InvalidOperationException>(()=>Json("""["\ud800"]""")[0].GetString())&&Refuses<InvalidOperationException>(()=>Json("""["\udc00"]""")[0].GetString())
            &&AgentTools.Parse("read_reference_data",Json("""{"table":"units","search":"😀"}""")).Arguments==new ReferenceArguments("units","\U0001F600"),
            "reading a string refuses an escaped unpaired surrogate before its text is checked, and a surrogate pair is one accepted character");
        Console.WriteLine("PASS reading a string refuses an escaped unpaired surrogate before its text is checked; a surrogate pair is accepted");

        // The largest inputs the schemas accept, which a step stores as jsonb: its text adds a space after each separator.
        string Ids()=>string.Join(",",Enumerable.Range(0,64).Select(_=>$"\"{Guid.NewGuid()}\""));
        foreach(var (tool,input) in new[]{
            ("read_trades",$$"""{"view":"blotter","mode":"pivot","bookIds":[{{Ids()}}],"portfolioIds":[{{Ids()}}],"groupBy":["portfolio","book","market","contract","instrumentType"],"measures":["tradeCount","lots","quantity","executionPnl","presentValue"],{{NoTrade}}}"""),
            ("read_reference_data",$$"""{"table":"{{PreviewDatabase.ReferenceTables.MaxBy(t=>t.Length)}}","search":"{{new string('€',160)}}"}""")})
        {
            var parsed=Json(input);
            _=AgentTools.Parse(tool,parsed);
            var text=JsonSerializer.Serialize(parsed);
            Require(Encoding.UTF8.GetByteCount(text)+text.Count(c=>c is ',' or ':')<=8192,$"the largest input {tool} accepts is a step's input of at most 8 KiB");
        }
        Console.WriteLine("PASS every input a tool accepts is stored as a step's input of at most 8 KiB");
    }

    static void Reports()
    {
        var blotter=Blotter;
        foreach(var (report,page,operation) in new[]{
            ("""{"page":"curves","selection":{"view":"curves","curve":null}}""","curves","market_data.curves_v1"),
            ("""{"page":"curves","selection":{"view":"curve","curve":"future"}}""","curves","market_data.curve_v1"),
            ("""{"page":"curves","selection":{"view":"observations","curve":"future"}}""","curves","market_data.observations_v1"),
            ("""{"page":"contract_definitions","selection":{"view":"contract_definitions","curve":null}}""","contract_definitions","market_data.contract_definitions_v1"),
            ("""{"page":"schedules","selection":{"view":"library"}}""","schedules","schedules.library_v1"),
            ("""{"page":"power_tous","selection":{"view":"tou_products"}}""","power_tous","schedules.tou_products_v1"),
            ("""{"page":"power_tous","selection":{"view":"power_hours"}}""","power_tous","schedules.power_hours_v1"),
            ("""{"page":"reference_data","selection":{"table":"units","search":"mega"}}""","reference_data","reference_data.table_v1"),
            ("""{"page":"parties","selection":{"kind":"counterparty"}}""","parties","trade_parties.versions_v1"),
            (Report("blotter",blotter),"blotter","trades.blotter_v1"),
            (Report("blotter",$$"""{"view":"trade",{{NoBlotter}},"tradeId":"{{Trade}}","instrumentType":"future","revision":3}"""),"blotter","trades.trade_v1")})
        {
            PageReports.Require(Json(report));
            Require(PageReports.Dispatch(Json(report)) is {Call:{} dispatchedCall} read&&read.Page==page&&dispatchedCall.Operation.Id==operation,$"{report} dispatches {operation}");
        }
        var unsupported=new[]{"blotter_risk","delivery_prices","custom_curves","interval_series","interval_curves","jobs","users_access","settings_ai"};
        Require(PageReports.NotSupported.Order().SequenceEqual(unsupported.Order())&&PageReports.Supported.Order()
                .SequenceEqual(new[]{"curves","contract_definitions","schedules","power_tous","reference_data","parties","blotter"}.Order()),
            "the closed set names one id per navigable view, with each coverage row the assistant reads and each it does not");
        foreach(var page in unsupported)
        {
            PageReports.Require(Json(Report(page,"{}")));
            Require(PageReports.Dispatch(Json(Report(page,"{}"))) is null,$"{page} is reported with no selection and read as unsupported");
        }
        Console.WriteLine("PASS a page report names one page of the closed set, a supported page's selection in its read tool's input for a view it "+
            "shows and an unsupported page's none, and dispatches that read");

        foreach(var report in new[]{"""{"page":"curves"}""","""{"page":"curves","selection":{"view":"curves","curve":null},"extra":1}""","""{"page":"pricing","selection":{}}""",
            """{"page":1,"selection":{}}""","""{"page":"curves","selection":[]}""","""{"page":"curves","selection":{"view":"contract_definitions","curve":null}}""",
            """{"page":"schedules","selection":{"view":"tou_products"}}""","""{"page":"jobs","selection":{"view":"curves","curve":null}}""",
            """{"page":"curves","selection":{"view":"curve"}}""","""{"page":"curves","selection":{"view":"curves"}}""",
            """{"page":"Curves","selection":{"view":"curves","curve":null}}""","[]","null","\"curves\""})
            Require(Code(()=>PageReports.Require(Json(report)))=="page_report_invalid",$"ask refuses the page report {report}");
        Require(Code(()=>PageReports.Require(default))=="page_report_invalid","ask refuses a missing page report");
        Console.WriteLine("PASS ask refuses any other page report page_report_invalid, before admission");

        // Read current page (§7.3): its own input is empty, then the stored report's page, then the version's tools.
        var working=Pin("current",Guid.NewGuid(),Working);
        TurnReads Turn(string report,params string[] tools)=>new(working,new("agent",Guid.NewGuid()),Json(report),[..tools]);
        var curve="""{"page":"curves","selection":{"view":"curve","curve":"future"}}""";
        ModelToolRequest Current(string input="{}")=>new("toolu_synthetic_01","read_current_page",Json(input));
        var (call,dispatched)=AgentTools.Requested(Turn(curve,"read_current_page","read_market_data"),Current());
        var (own,ownDispatched)=AgentTools.Requested(Turn(curve,"read_market_data"),new("toolu_synthetic_02","read_market_data",Json("""{"view":"curves","curve":null}""")));
        Require(call.Operation.Id=="market_data.curve_v1"&&call.Arguments==new CurveArguments("future")&&dispatched&&own.Operation.Id=="market_data.curves_v1"&&!ownDispatched,
            "Read current page dispatches the stored report's read with its selection; another tool reads its own input");
        Require(Code(()=>AgentTools.Requested(Turn(curve,"read_current_page"),Current()))=="tool_not_enabled"
            &&Code(()=>AgentTools.Requested(Turn("""{"page":"jobs","selection":{}}""","read_current_page","read_market_data"),Current()))=="page_not_supported"
            &&Code(()=>AgentTools.Requested(Turn("""{"page":"curves","selection":{"view":"curve"}}""","read_current_page","read_market_data"),Current()))=="page_selection_unavailable"
            &&Code(()=>AgentTools.Requested(Turn(curve,"read_current_page","read_market_data"),Current("""{"page":"curves"}""")))=="tool_arguments_invalid",
            "Read current page takes no input, refuses an unsupported page page_not_supported, a stored selection it cannot read page_selection_unavailable "+
            "and a page whose tool is off tool_not_enabled");
        Console.WriteLine("PASS Read current page takes no input and dispatches the stored report, refusing page_not_supported, tool_not_enabled and "+
            "page_selection_unavailable");

        // The adapter fails a call requesting a tool it did not offer (§6.1), so this is defense in depth.
        Require(Code(()=>AgentTools.Requested(Turn(curve,"read_market_data"),new("toolu_synthetic_03","read_trades",Json(blotter))))=="tool_not_enabled"
            &&Code(()=>AgentTools.Requested(Turn(curve,"read_market_data"),Current()))=="tool_not_enabled"
            &&Code(()=>AgentTools.Requested(Turn(curve,"read_market_data"),new("toolu_synthetic_04","navigate",Json("{}"))))=="tool_not_enabled",
            "a request for a tool the version has not switched on is tool_not_enabled before its input is read");
        Console.WriteLine("PASS a request for a tool the version has not switched on is refused tool_not_enabled before any read");
    }

    static void Refusals()
    {
        foreach(var (error,code,end) in new (Exception,string?,ToolStepEnd)[]{
            (new OperationCanceledException(),null,ToolStepEnd.Interrupted),
            (new PreviewDeniedException(),"access_denied",ToolStepEnd.Refusal),
            (new PostgresException("No current market read authority","ERROR","ERROR","42501"),"access_denied",ToolStepEnd.Refusal),
            (new MarketDataException("missing_curve","Select an exact curve definition."),"missing_curve",ToolStepEnd.Refusal),
            (new TemporalException("temporal_context","Pin an exact retained context."),"temporal_context",ToolStepEnd.Refusal),
            (new PostgresException("knowledge_coverage","ERROR","ERROR","P0001"),"knowledge_coverage",ToolStepEnd.Refusal),
            (new PostgresException("The selected curve is unavailable.","ERROR","ERROR","P0001"),"tool_failed",ToolStepEnd.Failure),
            (new PostgresException("canceling statement due to statement timeout","ERROR","ERROR","57014"),"tool_failed",ToolStepEnd.Failure),
            (new NpgsqlException("Exception while reading from stream",new TimeoutException()),"tool_failed",ToolStepEnd.Failure),
            (new InvalidOperationException("The turn's pinned-context record cannot be read."),"tool_failed",ToolStepEnd.Failure)})
            Require(AgentTools.Classify(error)==(code,end),$"a read ending {error.GetType().Name} is classified {code} {end}");
        Console.WriteLine("PASS a read's structured refusal or denial is a refused step; a stop is the worker's to name; anything else fails the turn tool_failed");

        var shown=JsonDocument.Parse(AgentTools.Refusal("missing_curve")).RootElement;
        var other=JsonDocument.Parse(AgentTools.Refusal("tou_conflict")).RootElement;
        Require(shown.EnumerateObject().Select(m=>m.Name).SequenceEqual(["code","message"])&&shown.GetProperty("code").GetString()=="missing_curve"
            &&shown.GetProperty("message").GetString()=="No curve with this code exists in the pinned market data."
            &&other.GetProperty("message").GetString()==AgentTools.GenericText,
            "a refusal carries only its code and the fixed text for it, or the fixed generic sentence for a code without one, never the read's own message");
        var curve=AgentTools.Operation("market_data.curve_v1");
        var blotter=AgentTools.Operation("trades.blotter_v1");
        var trade=AgentTools.Operation("trades.trade_v1");
        Require(AgentTools.Shown(curve,true,"missing_curve")=="page_selection_unavailable"&&AgentTools.Shown(curve,false,"missing_curve")=="missing_curve"
            &&AgentTools.Shown(curve,true,"missing_observation")=="missing_observation"&&AgentTools.Shown(blotter,true,"blotter_view")=="page_selection_unavailable"
            &&AgentTools.Shown(trade,true,"futures_trade")=="page_selection_unavailable"&&AgentTools.Shown(trade,true,"swap_trade")=="page_selection_unavailable"
            &&AgentTools.Shown(trade,true,"knowledge_coverage")=="knowledge_coverage",
            "a dispatched read's refusal of the page's selection is page_selection_unavailable, and every other refusal keeps its code");
        Console.WriteLine("PASS a refusal carries only its code and fixed text; a read refusing the page's selection is page_selection_unavailable");
    }

    static void Results()
    {
        JsonObject Sized(int bytes)
        {
            var result=new JsonObject{["p"]=""};
            result["p"]=new string('x',bytes-result.ToJsonString().Length);
            return result;
        }
        Require(AgentTools.Bounded(Sized(65536),500) is {Length:65536}&&AgentTools.Bounded(Sized(65537),500) is null&&AgentTools.Bounded(Sized(64),501) is null,
            "a result of 500 rows and 64 KiB is returned whole, and one more row or byte returns none, never a truncated result");
        Console.WriteLine("PASS a result within 500 rows and 64 KiB is returned whole; one more row or byte is result_too_large, never truncated");

        var context=Guid.NewGuid();
        var working=Pin("current",context,Working);
        var retained=Pin("retained",context,Working with{ContextId=context});
        JsonNode Context(TurnPin pin,string operation,bool displayed=false)
        {
            var envelope=AgentTools.Envelope(pin,AgentTools.Operation(operation),Cutoff.AddDays(1),new(new JsonObject{["rows"]=new JsonArray()},0,displayed));
            Require(envelope["operation"]!.GetValue<string>()==operation&&envelope["result"]!["rows"] is JsonArray&&(envelope["amountDisplay"]?.GetValue<string>()==(displayed?"tool_amount_display_v1":null)),
                "a result names its operation, the display its amounts carry, if any, and the result");
            return envelope["context"]!;
        }
        var frame=new JsonObject{["id"]=Working.Id.ToString(),["name"]=Working.Name,["at"]=JsonSerializer.SerializeToNode(Working.At)};
        Require(JsonNode.DeepEquals(Context(working,"schedules.tou_products_v1"),new JsonObject{["knowledge"]="current_definitions",["readAt"]=JsonSerializer.SerializeToNode(Cutoff.AddDays(1))})
            &&JsonNode.DeepEquals(Context(working,"schedules.power_hours_v1"),new JsonObject{["knowledge"]="current_definitions",["readAt"]=JsonSerializer.SerializeToNode(Cutoff.AddDays(1)),["frame"]=frame.DeepClone()})
            &&JsonNode.DeepEquals(Context(working,"market_data.curves_v1"),new JsonObject{["knowledge"]="working_data",["contextId"]=context.ToString(),["cutoff"]=JsonSerializer.SerializeToNode(Cutoff)})
            &&JsonNode.DeepEquals(Context(working,"market_data.curve_v1",true),new JsonObject{["knowledge"]="working_data",["contextId"]=context.ToString(),["cutoff"]=JsonSerializer.SerializeToNode(Cutoff),["frame"]=frame.DeepClone()})
            &&Context(retained,"trades.blotter_v1")["knowledge"]!.GetValue<string>()=="retained",
            "current-only definitions are labeled with the instant they were read, never the cutoff; other reads with the pinned knowledge and cutoff; "+
            "and a read that consumed the As of frame names it");
        Console.WriteLine("PASS a result is labeled with its pinned knowledge and cutoff, or for current-only definitions its read instant, and the frame it consumed");

        var (used,unused)=(Guid.NewGuid(),Guid.NewGuid());
        var priced=new PriceRead(new{snapshotId=Working.Id,name=Working.Name,at=Working.At,curve="future",evaluated=1,numericalPolicy="synthetic_policy_v1",
            trace=new[]{new{id="step_0"}},points=new[]{new{contractId=Guid.NewGuid(),priceTick="0.025",value="3.1",quote="synthetic_quote",lineage=new[]{used}}},
            observationIds=Working.ObservationIds},[Json($$"""{"id":"{{used}}","value":"3.1"}"""),Json($$"""{"id":"{{unused}}","value":"9"}""")]);
        var curve=ToolResults.Curve(priced);
        var observations=ToolResults.Observations(priced);
        Require(curve.Value.Select(m=>m.Key).SequenceEqual(["curve","numericalPolicy","points"])&&curve.Rows==1&&curve.Displayed
            &&JsonNode.DeepEquals(curve.Value["points"]![0]!["value"],new JsonObject{["value"]="3.1",["display"]="3.100"})
            &&observations.Rows==1&&observations.Value["observations"]![0]!["id"]!.GetValue<string>()==used.ToString()&&!observations.Displayed,
            "a curve returns its prices with the page's display and without the pricing trace or the frame the envelope names, and its observations are "+
            "exactly those its prices were built from, as stored");
        var revision=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferencePage Page(bool more)=>new("units",[Json("""{"catalog_id":"x","revision_id":"y","code":"megawatt","dimension_code":"power"}""")],
            ImmutableDictionary<string,ImmutableArray<JsonElement>>.Empty.Add("units",[Json("""{"code":"megawatt"}""")]).Add("dimensions",[Json("""{"code":"power","name":"Power"}""")]),
            0,200,100000,more,"mega",revision,Guid.NewGuid());
        var reference=ToolResults.Reference(Page(false));
        Require(Code(()=>ToolResults.Reference(Page(true)))=="result_too_large"&&reference.Rows==2
            &&JsonNode.DeepEquals(reference.Value,JsonNode.Parse("""{"table":"units","search":"mega","rows":[{"code":"megawatt","dimension_code":"power"}],"linked":{"dimensions":[{"code":"power","name":"Power"}]}}""")),
            "a reference table returns one whole page of rows without their catalog and revision, with the rows they link; more matching rows are result_too_large");
        var report=new{view=new{mode="pivot"},pivotNumericalPolicy="typed_partitioned_trade_totals_v1",groups=new[]{new{contractId="k",presentValue="12.5"}},
            holdings=new[]{new{tradeId="t",presentValue="12.5"}},evaluatedContracts=1,work=new{swapTrades=0},hasSwaps=false,convention="trading_workspace_v1"};
        var pivot=ToolResults.Blotter(report);
        var rows=ToolResults.Blotter(report with{view=new{mode="trades"}});
        Require(pivot.Value.Select(m=>m.Key).SequenceEqual(["view","pivotNumericalPolicy","groups","hasSwaps","convention"])&&pivot.Rows==1
            &&JsonNode.DeepEquals(pivot.Value["groups"]![0]!["presentValue"],new JsonObject{["value"]="12.5",["display"]="12.500"})
            &&rows.Value.ContainsKey("holdings")&&!rows.Value.ContainsKey("groups")&&rows.Rows==1,
            "the Blotter returns trade rows, or a pivot's totals alone, with the page's display");
        // A trade shows its amounts as its editor does (trade-editor.ts, swap-editor.ts), from the deal's saved formats.
        var (held,listed)=(Guid.NewGuid(),Guid.NewGuid());
        object Futures(Guid contract)=>new{lifecycleEligible=true,state=new{revision=2,contractId=contract,lots="2",executionPrice="3.1"},
            history=new[]{new{revision=1,contractId=contract,lots="1",executionPrice="3.14159"},new{revision=2,contractId=contract,lots="2",executionPrice="3.1"}},
            catalog=new{books=Array.Empty<object>(),contracts=new[]{new{id=listed,clearingIncrement="0.5"},new{id=held,clearingIncrement="0.001"}}},
            amendmentConvention="futures_amendment_v1"};
        var saved=Formats(("trade",new(2,2,1,4)),("fixed",new(0,0,0,0)));
        var futures=ToolResults.Trade(Futures(held),"future",saved);
        var unlisted=ToolResults.Trade(Futures(Guid.NewGuid()),"future",saved);
        var unsaved=ToolResults.Trade(Futures(Guid.NewGuid()),"future",null);
        Require(futures.Value.Select(m=>m.Key).SequenceEqual(["instrumentType","state","history"])&&futures.Rows==2&&futures.Displayed
            &&JsonNode.DeepEquals(futures.Value["state"]!["executionPrice"],new JsonObject{["value"]="3.1",["display"]="3.100"})
            &&JsonNode.DeepEquals(futures.Value["history"]![0]!["executionPrice"],new JsonObject{["value"]="3.14159",["display"]="3.142"})
            &&futures.Value["state"]!["lots"]!.GetValue<string>()=="2"
            &&JsonNode.DeepEquals(unlisted.Value["history"]![0]!["executionPrice"],new JsonObject{["value"]="3.14159",["display"]="3.14"})
            &&JsonNode.DeepEquals(unsaved.Value["history"]![0]!["executionPrice"],new JsonObject{["value"]="3.14159",["display"]="3.1416"}),
            "a futures trade returns its selected revision and history without the booking catalog or lifecycle flag, its prices shown by its "+
            "contract's settlement-price increment where the catalog lists the contract, else by the deal's trade format or the default");
        var swap=ToolResults.Trade(new{state=new{revision=1,quantity="1000",terms=new{@fixed=new{price="3.14159"}}},history=new[]{new{revision=1,quantity="1000"}},
            valuation=new{presentValue="1234.5678",snapshotId=Guid.NewGuid(),asOf=Working.At,marketCutoff=Cutoff,
                sides=new object[]{new{kind="fixed",presentValue="1234.5678",payments=new[]{new{cashAmount="-3.14159",discountFactor="0.987654"}}},
                    new{kind="floating",presentValue="1234.5678",payments=Array.Empty<object>()}}},
            catalog=new{indices=Array.Empty<object>()},lifecycleEligible=true},"financial_swap",Formats(("trade",new(1,4,3,6)),("fixed",new(2,4,3,2))));
        var valuation=swap.Value["valuation"]!;
        Require(swap.Value.Select(m=>m.Key).SequenceEqual(["instrumentType","state","history","valuation"])&&swap.Rows==1&&swap.Displayed
            &&valuation.AsObject().Select(m=>m.Key).SequenceEqual(["presentValue","marketCutoff","sides"])
            &&JsonNode.DeepEquals(swap.Value["state"]!["quantity"],new JsonObject{["value"]="1000",["display"]="1,000"})
            &&JsonNode.DeepEquals(swap.Value["state"]!["terms"]!["fixed"]!["price"],new JsonObject{["value"]="3.14159",["display"]="3.1416"})
            &&JsonNode.DeepEquals(valuation["presentValue"],new JsonObject{["value"]="1234.5678",["display"]="1,234.6"})
            &&JsonNode.DeepEquals(valuation["sides"]![0]!["presentValue"],new JsonObject{["value"]="1234.5678",["display"]="1,234.57"})
            &&JsonNode.DeepEquals(valuation["sides"]![0]!["payments"]![0]!["cashAmount"],new JsonObject{["value"]="-3.14159",["display"]="-3.14"})
            &&JsonNode.DeepEquals(valuation["sides"]![0]!["payments"]![0]!["discountFactor"],new JsonObject{["value"]="0.987654",["display"]="0.99"})
            &&JsonNode.DeepEquals(valuation["sides"]![1]!["presentValue"],new JsonObject{["value"]="1234.5678",["display"]="1,234.568"}),
            "a swap returns its revisions and valuation without the booking catalog, lifecycle flag or the frame the envelope names, its valuation "+
            "shown by the deal's trade format and each side by its own, or the default where none is saved, and its terms by the default, as the "+
            "page shows what it formats by the viewer's precision");
        Console.WriteLine("PASS each operation returns its page read's own fields and exact values without page-only material, with the page's display "+
            "where the page shows one: a trade's from its contract's increment and the deal's saved formats");
    }

    // The corpus the web checks assert too (web/tests/amount-display.test.mjs): the requests the page's walk sends for a
    // payload, and the display each returns.
    static void Display()
    {
        using var corpus=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","amount-display-cases.json")));
        var cases=0;
        foreach(var example in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name=example.GetProperty("name").GetString()!;
            var amounts=example.GetProperty("amounts").EnumerateArray().Select(a=>(Path:a.GetProperty("path").EnumerateArray().ToArray(),
                Request:new DisplayNumberRequest(a.GetProperty("request").GetProperty("value").GetString()!,a.GetProperty("request").GetProperty("decimalPlaces").GetInt32(),
                    a.GetProperty("request").GetProperty("tick").GetString(),a.GetProperty("request").GetProperty("trimTrailingZeros").GetBoolean()),
                Display:a.GetProperty("display").GetString()!)).ToArray();
            var payload=JsonNode.Parse(example.GetProperty("payload").GetRawText());
            var tick=example.TryGetProperty("tick",out var seed)?seed.GetString():null;
            var format=example.TryGetProperty("format",out var saved)?saved.Deserialize<DealAmountFormat>(JsonSerializerOptions.Web)!:DealAmountFormat.Default;
            Require(ToolAmountDisplay.Amounts(payload,tick,format).Select(a=>a.Request).ToHashSet().SetEquals(amounts.Select(a=>a.Request)),$"{name}: the walk asks for the page's displays");
            foreach(var amount in amounts)Require(UnitOperations.Display([amount.Request])[0]==amount.Display,$"{name}: {amount.Request.Value} displays {amount.Display}");
            var expected=payload!.DeepClone();
            foreach(var amount in amounts)
            {
                var parent=amount.Path[..^1].Aggregate(expected,(node,step)=>step.ValueKind==JsonValueKind.Number?node[step.GetInt32()]!:node[step.GetString()!]!);
                parent[amount.Path[^1].GetString()!]=new JsonObject{["value"]=amount.Request.Value,["display"]=amount.Display};
            }
            Require(ToolAmountDisplay.Annotate(new DisplayWalk(payload!,tick,format))==amounts.Length>0&&JsonNode.DeepEquals(payload,expected),
                $"{name}: each amount carries its exact value and display");
            cases++;
        }
        Require(cases>=15,"the display corpus covers its cases");
        Console.WriteLine($"PASS tool_amount_display_v1 shows amounts as the page does: ticks, deal formats, the tick and format a walk starts from, the "+
            $"default and trimming, over the shared corpus of {cases} cases");

        // Walks of one payload, as an editor makes them: a later walk's display of an amount replaces an earlier one's.
        var swap=JsonNode.Parse("""{"quantity":"1000","valuation":{"presentValue":"1234.5678","sides":[{"kind":"fixed","presentValue":"1234.5678"}]}}""")!;
        Require(ToolAmountDisplay.Annotate(new DisplayWalk(swap,null,DealAmountFormat.Default),new DisplayWalk(swap["valuation"]!,null,new(1,4,3,6)),
                new DisplayWalk(swap["valuation"]!["sides"]![0]!,null,new(2,4,3,6)))
            &&JsonNode.DeepEquals(swap,JsonNode.Parse("""
                {"quantity":{"value":"1000","display":"1,000"},"valuation":{"presentValue":{"value":"1234.5678","display":"1,234.6"},
                 "sides":[{"kind":"fixed","presentValue":{"value":"1234.5678","display":"1,234.57"}}]}}
                """)),"each amount shows by the last walk that reaches it, and once");
        Console.WriteLine("PASS walks of one payload show each amount once, by the last walk that reaches it");
    }

    static JsonElement Record(string mode,Guid context,DateTimeOffset cutoff,Guid? frame)=>JsonSerializer.SerializeToElement(new{mode,requestedContextId=mode=="retained"?context:(Guid?)null,
        frameId=frame,contextId=context,cutoff,recordedAt=cutoff});
    static JsonElement Null()=>Json("null");
    static Dictionary<string,DealAmountFormats.Selection> Formats(params (string Role,DealAmountFormat Format)[] saved)=>
        saved.ToDictionary(s=>s.Role,s=>new DealAmountFormats.Selection(s.Role,1,s.Format));
    static string Report(string page,string selection)=>$"{{\"page\":\"{page}\",\"selection\":{selection}}}";
    static JsonElement Json(string text)=>JsonDocument.Parse(text).RootElement.Clone();

    // Two frames read the same pins.
    static bool Same(InputFrame x,InputFrame y)=>(x.Id,x.Name,x.At,x.ContextId,x.ResolvesLatestCorrection,x.OriginCloseId)==(y.Id,y.Name,y.At,y.ContextId,y.ResolvesLatestCorrection,y.OriginCloseId)
        &&x.ObservationIds.SequenceEqual(y.ObservationIds)&&Frozen(x).SequenceEqual(Frozen(y))
        &&x.CurveContracts.Count==y.CurveContracts.Count&&x.CurveContracts.All(p=>y.CurveContracts.TryGetValue(p.Key,out var v)&&v.SequenceEqual(p.Value))
        &&(x.SwapMarketSelections??ImmutableDictionary<Guid,ImmutableArray<Guid>>.Empty).Count==(y.SwapMarketSelections??ImmutableDictionary<Guid,ImmutableArray<Guid>>.Empty).Count
        &&(x.SwapMarketSelections??ImmutableDictionary<Guid,ImmutableArray<Guid>>.Empty).All(p=>y.SwapMarketSelections!.TryGetValue(p.Key,out var v)&&v.SequenceEqual(p.Value));
    static ImmutableArray<Guid> Frozen(InputFrame frame)=>frame.FrozenObservationIds.IsDefault?[]:frame.FrozenObservationIds;

    static string? Code(Action action)
    {
        try{action();}
        catch(MarketDataException e){return e.Code;}
        catch(TemporalException e){return e.Code;}
        catch(ToolRefusal e){return e.Code;}
        return null;
    }

    static bool Refuses<T>(Action action) where T:Exception
    {
        try{action();}
        catch(T){return true;}
        return false;
    }

    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
}
