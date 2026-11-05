using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenEtrm.Api;

sealed record WalkthroughArguments(JsonElement Steps):ToolArguments;

// Presentation capabilities, never business commands. Field evidence is deliberately a small
// literal language: a complete standalone `Set ticket.lots = "2"` line in this ask.
// Natural-language, negated, duplicated and conflicting assignments require clarification.
internal static partial class WalkthroughPlans
{
    internal static readonly string[] Columns=["tradeReference","book","portfolio","instrumentType","quantity","executionPrice","mark","presentValue","executionPnl","contractId","currency","unit"];
    internal static readonly string[] Fields=["ticket.lots","ticket.executionPrice","ticket.memo"];
    internal static readonly IReadOnlyDictionary<string,string> Narration=new Dictionary<string,string>{
        ["look_here"]="Look here with me.",["selection"]="These are the selected positions.",
        ["sort"]="I am changing the displayed order.",["filter"]="I am filtering the displayed rows.",
        ["details"]="I am opening the details.",["fill"]="These terms remain unsaved for you to review.",
        ["review"]="Review the exact proposed change. Authorization is your separate action.",
        ["pause"]="Continue when you are ready.",["complete"]="The walkthrough is complete."};
    internal static string Page(string page)=>page.StartsWith("blotter",StringComparison.Ordinal)?"blotter":page;
    internal static JsonObject Capabilities(string page)=>new(){["steps"]=JsonSerializer.SerializeToNode(new[]{"go","say","pause","scroll_to","open"}.Concat(Page(page)=="blotter"?["select","sort","filter","fill"]:Array.Empty<string>())),
        ["panes"]=JsonSerializer.SerializeToNode(Page(page)=="blotter"?new[]{"details","charts","new_future","review"}:["review"]),["columns"]=JsonSerializer.SerializeToNode(Page(page)=="blotter"?Columns:[]),["fields"]=JsonSerializer.SerializeToNode(Page(page)=="blotter"?Fields:[]),
        ["narration"]=JsonSerializer.SerializeToNode(Narration),["fillEvidence"]="A fill ask must contain only the header Fill unsaved futures ticket followed by unique Set ticket.FIELD = \"literal value\" lines. Other prose requires clarification."};
    internal static JsonArray Validate(JsonElement steps,JsonElement report,string question,TurnTargets targets,TurnPin pin)
    {
        if(steps.ValueKind!=JsonValueKind.Array||steps.GetArrayLength() is <1 or >12)throw Invalid();
        var page=report.GetProperty("page").GetString()!;var ticket=page=="blotter_new_future";var sameView=true;var result=new JsonArray();
        foreach(var step in steps.EnumerateArray())
        {
            var m=new ToolInput(step);var kind=m.Choice("kind","go","select","sort","filter","open","scroll_to","fill","say","pause");
            if(kind=="go")
            {
                m.Exactly("kind","target");var target=targets.Find(m.Text("target",2,10))??throw Invalid();
                if(!targets.Read(target.Page)||!PageReports.Opens(target.Page)||target.Kind is "proposal" or "draft")throw Invalid();
                sameView&=target.Kind=="page"&&Page(page)==Page(target.Page);page=target.Page;ticket=page=="blotter_new_future";
                result.Add(new JsonObject{["kind"]=kind,["target"]=NavigationTargets.Json(target,pin)});continue;
            }
            var named=m.Text("page",1,64);
            if(named!=page||!targets.Read(page))throw Invalid();
            var normalized=new JsonObject{["kind"]=kind,["page"]=page};
            switch(kind)
            {
                case "say":m.Exactly("kind","page","message");normalized["text"]=Narration[m.Choice("message",[..Narration.Keys])];break;
                case "pause":m.Exactly("kind","page");break;
                case "scroll_to":m.Exactly("kind","page","widget");normalized["widget"]=m.Choice("widget",Page(page)=="blotter"?["page","positions","details","charts"]:["page"]);break;
                case "sort":
                    Blotter();m.Exactly("kind","page","column","direction");normalized["column"]=m.Choice("column",[..Columns]);normalized["direction"]=m.Choice("direction","asc","desc");break;
                case "filter":
                    Blotter();m.Exactly("kind","page","field","value");normalized["field"]=m.Choice("field",["query",..Columns]);normalized["value"]=m.Text("value",0,200);break;
                case "select":
                    Blotter();m.Exactly("kind","page","focus");var focus=PageFocus.Parse(step.GetProperty("focus"));
                    if(focus.PivotCell is {} cell)
                    {
                        if(!sameView||!targets.ReadFocus(step.GetProperty("focus"))||!report.TryGetProperty("focus",out var admitted)||!JsonNode.DeepEquals(JsonNode.Parse(admitted.GetRawText()),JsonNode.Parse(step.GetProperty("focus").GetRawText())))throw Invalid();
                    }
                    else if(focus.TradeIds.IsEmpty||focus.TradeIds.Any(id=>!targets.All.Any(t=>t.Page=="blotter"&&t.Selection?["tradeId"]?.GetValue<string>()==id.ToString())))throw Invalid();
                    normalized["focus"]=JsonNode.Parse(step.GetProperty("focus").GetRawText());break;
                case "open":
                    m.Exactly("kind","page","pane","target","proposalStep");var pane=m.Choice("pane","details","charts","new_future","review");normalized["pane"]=pane;
                    normalized["target"]=null;normalized["proposalStep"]=null;
                    if(pane=="review")
                    {
                        m.Null("target");var number=(int)m.Whole("proposalStep",1,1000);if(!targets.Proposed(number,page))throw Invalid();normalized["proposalStep"]=number;
                    }
                    else
                    {
                        Blotter();m.Null("proposalStep");
                        if(m.Has("target"))
                        {
                            if(pane!="details")throw Invalid();var target=targets.Find(m.Text("target",2,10));
                            if(target is not{Page:"blotter",Kind:"record"}||!targets.Read("blotter"))throw Invalid();
                            normalized["target"]=NavigationTargets.Json(target,pin);
                        }
                        if(pane=="new_future")ticket=true;
                    }
                    break;
                case "fill":
                    Blotter();m.Exactly("kind","page","field","value");if(!ticket)throw Invalid();
                    var field=m.Choice("field",[..Fields]);var value=m.Text("value",1,field=="ticket.memo"?200:64);
                    if(field!="ticket.memo"&&!Decimal().IsMatch(value))throw Invalid();
                    var lines=question.Split('\n').Select(l=>l.TrimEnd('\r')).ToArray();
                    if(lines.Length<2||lines[0]!="Fill unsaved futures ticket")throw Explicit();
                    var terms=new Dictionary<string,string>(StringComparer.Ordinal);
                    foreach(var line in lines.Skip(1))
                    {
                        var assignment=Assignment().Match(line);if(!assignment.Success)throw Explicit();
                        string? stated;try{stated=JsonSerializer.Deserialize<string>(assignment.Groups[2].Value);}catch(JsonException){throw Explicit();}
                        if(stated is null||!terms.TryAdd(assignment.Groups[1].Value,stated))throw Explicit();
                    }
                    if(!terms.TryGetValue(field,out var term)||term!=value)throw Explicit();
                    normalized["field"]=field;normalized["value"]=value;break;
            }
            result.Add(normalized);
            void Blotter(){if(Page(page)!="blotter")throw Invalid();}
        }
        return result;
    }
    static ToolRefusal Explicit()=>new("walkthrough_fill_requires_explicit_terms");
    [GeneratedRegex("^Set (ticket[.](?:lots|executionPrice|memo)) = (\"(?:[^\"\\\\]|\\\\.)*\")$" )]private static partial Regex Assignment();
    static ToolRefusal Invalid()=>new("walkthrough_plan_invalid");
    [GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?\z")]private static partial Regex Decimal();
}

public sealed partial class AgentTools
{
    static AgentTool WalkthroughTool=>Tool("present_walkthrough","Publish a visible, interruptible presentation walkthrough, at most 12 steps, after reading every page it names. Use for requests to show, find, explain or do something on screen. Takes planJson, a JSON array. No commands or selectors. go: {kind,target} uses an issued handle. Other steps name kind and page: say adds message (a code from presentation.narration); pause; scroll_to adds widget; select adds focus {tradeIds,pivotCell}; sort adds column,direction; filter adds field,value; open adds pane (details/charts/new_future/review), target (issued handle or null), proposalStep (existing proposal ordinal or null); fill adds field,value. Read presentation capabilities from the page digest. Fill only unsaved ticket fields, only when the entire current ask is the header Fill unsaved futures ticket followed by unique Set ticket.FIELD = \"value\" lines; otherwise ask for terms. Never Book, Save, submit, check consent or Authorize.",
        """{"type":"object","properties":{"planJson":{"type":"string","minLength":2,"maxLength":6144}},"required":["planJson"],"additionalProperties":false}""",[AssistantOperations.Permission]);
    static ToolCall ParseWalkthrough(ToolInput m)
    {
        m.Exactly("planJson");try{var steps=JsonSerializer.Deserialize<JsonElement>(m.Formula("planJson"));return new(Operations["workspace.walkthrough_v1"],new WalkthroughArguments(steps));}
        catch(JsonException){throw ToolInput.Invalid();}
    }
    async Task<ToolStep> RunWalkthrough(TurnReads turn,TurnTargets targets,ModelToolRequest request,WalkthroughArguments args,CancellationToken ct)
    {
        var at=DateTimeOffset.UtcNow;var access=WorkspaceAccess(database.Manifest);
        try
        {
            await database.WithAgent(turn.Agent,turn.ReadLimit,[AssistantOperations.Permission],instant=>at=instant,(_,_)=>Task.FromResult(true),ct);
            access=[..access.Concat(await (Workspace??throw new ToolRefusal("operation_unavailable")).SourceAccess(turn,ct)).Distinct()];
            var steps=WalkthroughPlans.Validate(args.Steps,turn.PageReport,turn.Question,targets,turn.Pin);
            var plan=new JsonObject{["pageReport"]=JsonNode.Parse(turn.PageReport.GetRawText()),["context"]=NavigationTargets.Json(NavigationTargets.Page(turn.PageReport.GetProperty("page").GetString()!),turn.Pin)["context"]!.DeepClone(),["steps"]=steps};
            if(System.Text.Encoding.UTF8.GetByteCount(plan.ToJsonString())>16384||Bounded(plan,steps.Count,turn.OutputLimits) is null)throw new ToolRefusal("result_too_large");
            return new(request.Name,"workspace.walkthrough_v1",request.Input,null,at,access,"{\"status\":\"presented\",\"businessEffect\":\"none\"}",ToolStepEnd.Result){Walkthrough=plan};
        }
        catch(Exception e)
        {
            var(code,end)=e is ToolRefusal r?(r.Code,ToolStepEnd.Refusal):Classify(e);
            return new(request.Name,"workspace.walkthrough_v1",request.Input,code,at,access,end==ToolStepEnd.Refusal?Refusal(code!):null,end);
        }
    }
}
