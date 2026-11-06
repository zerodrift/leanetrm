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
    internal static readonly string[] FutureFields=["ticket.lots","ticket.executionPrice","ticket.memo"];
    internal static readonly string[] SwapFields=["swap.quantity","swap.fixedPrice","swap.memo","swap.structure","swap.fixedDirection","swap.priceMode"];
    internal static readonly string[] Fields=[..FutureFields,..SwapFields];
    internal static readonly IReadOnlyDictionary<string,string> Narration=new Dictionary<string,string>{
        ["look_here"]="Opening the screen controls.",["selection"]="These are the selected positions.",
        ["sort"]="I am changing the displayed order.",["filter"]="I am filtering the displayed rows.",
        ["details"]="I am opening the details.",["fill"]="These terms remain unsaved for you to review.",
        ["review"]="Review the exact proposed change. Authorization is your separate action.",
        ["pause"]="Continue when you are ready.",["complete"]="The walkthrough is complete."};
    internal static string Page(string page)=>page.StartsWith("blotter",StringComparison.Ordinal)?"blotter":page;
    internal static JsonObject Capabilities(string page)=>new(){["steps"]=JsonSerializer.SerializeToNode(new[]{"go","say","pause","scroll_to","open"}.Concat(Page(page)=="blotter"?["select","sort","filter","fill"]:Array.Empty<string>())),
        ["panes"]=JsonSerializer.SerializeToNode(Page(page)=="blotter"?new[]{"details","charts","new_future","review"}:["review"]),["columns"]=JsonSerializer.SerializeToNode(Page(page)=="blotter"?Columns:[]),["fields"]=JsonSerializer.SerializeToNode(page=="blotter_new_swap"?SwapFields:Page(page)=="blotter"?FutureFields:[]),
        ["narration"]=JsonSerializer.SerializeToNode(Narration),["fillEvidence"]="A fill ask must contain only Fill unsaved futures ticket or Fill unsaved swap ticket followed by unique Set FIELD = \"literal value\" lines, or a complete imperative: Fill the swap ticket with structure fixed for floating, price mode agreed, quantity 1000 and fixed price 3.4. Supported natural terms: quantity, fixed price, memo (JSON quoted), structure (fixed for floating/floating for floating), fixed direction (pay fixed/receive fixed), price mode (agreed). Missing or ambiguous terms require clarification."};
    internal static JsonArray Validate(JsonElement steps,JsonElement report,string question,TurnTargets targets,TurnPin pin)
    {
        if(steps.ValueKind!=JsonValueKind.Array||steps.GetArrayLength() is <1 or >12)throw Invalid();
        var page=report.GetProperty("page").GetString()!;var ticket=page=="blotter_new_future"?"futures":page=="blotter_new_swap"?"swap":null;string? structure=null,priceMode=null;var sameView=true;var result=new JsonArray();
        foreach(var step in steps.EnumerateArray())
        {
            var m=new ToolInput(step);var kind=m.Choice("kind","go","select","sort","filter","open","scroll_to","fill","say","pause");
            if(kind=="go")
            {
                m.Exactly("kind","target");var target=targets.Find(m.Text("target",2,10))??throw Invalid();
                if(!targets.Read(target.Page)||!PageReports.Opens(target.Page)||target.Kind is "proposal" or "draft")throw Invalid();
                sameView&=target.Kind=="page"&&Page(page)==Page(target.Page);page=target.Page;ticket=page=="blotter_new_future"?"futures":page=="blotter_new_swap"?"swap":null;structure=null;priceMode=null;
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
                        if(pane=="new_future")ticket="futures";
                    }
                    break;
                case "fill":
                    Blotter();m.Exactly("kind","page","field","value");if(ticket is null)throw Invalid();
                    var field=m.Choice("field",[..Fields]);var value=m.Text("value",1,field.EndsWith(".memo",StringComparison.Ordinal)?200:64);
                    if(!(ticket=="swap"?SwapFields:FutureFields).Contains(field))throw Invalid();
                    ValidValue(field,value);
                    var terms=Terms(question,ticket);
                    if(!terms.TryGetValue(field,out var term)||term!=value)throw Explicit();
                    if(field=="swap.structure")structure=value;
                    if(field is "swap.fixedDirection" or "swap.priceMode" or "swap.fixedPrice")
                    {
                        if(structure!="financial_fixed_floating")throw Invalid();
                        if(field=="swap.fixedPrice"&&priceMode!="agreed")throw Invalid();
                    }
                    if(field=="swap.priceMode")priceMode=value;
                    normalized["field"]=field;normalized["value"]=value;break;
            }
            result.Add(normalized);
            void Blotter(){if(Page(page)!="blotter")throw Invalid();}
        }
        return result;
    }
    static ToolRefusal Explicit()=>new("walkthrough_fill_requires_explicit_terms");
    static void ValidValue(string field,string value)
    {
        var allowed=field switch{
            "swap.structure"=>new[]{"financial_fixed_floating","financial_floating_floating"},
            "swap.fixedDirection"=>new[]{"-1","1"},"swap.priceMode"=>new[]{"agreed"},_=>null};
        if(allowed is not null?!allowed.Contains(value):!field.EndsWith(".memo",StringComparison.Ordinal)&&!Decimal().IsMatch(value))throw Invalid();
    }
    static Dictionary<string,string> Terms(string question,string product)
    {
        var terms=new Dictionary<string,string>(StringComparer.Ordinal);
        var fields=product=="swap"?SwapFields:FutureFields;
        void Add(string field,string value){if(!fields.Contains(field)||!terms.TryAdd(field,value))throw Explicit();ValidValue(field,value);}
        var lines=question.Split('\n').Select(l=>l.TrimEnd('\r')).ToArray();
        if(lines.Length>=2&&lines[0]==$"Fill unsaved {product} ticket")
        {
            foreach(var line in lines.Skip(1))
            {
                var assignment=Assignment().Match(line);if(!assignment.Success)throw Explicit();
                string? stated;try{stated=JsonSerializer.Deserialize<string>(assignment.Groups[2].Value);}catch(JsonException){throw Explicit();}
                if(stated is null)throw Explicit();Add(assignment.Groups[1].Value,stated);
            }
            return terms;
        }
        const string header="Fill the swap ticket with ";
        if(product!="swap"||!question.StartsWith(header,StringComparison.OrdinalIgnoreCase))throw Explicit();
        var body=question[header.Length..];var offset=0;
        foreach(Match term in NaturalTerm().Matches(body))
        {
            if(term.Index!=offset)throw Explicit();offset+=term.Length;
            var name=term.Groups[1].Value.ToLowerInvariant();var raw=term.Groups[2].Value;
            var value=raw.StartsWith('"')?JsonSerializer.Deserialize<string>(raw)!:raw;
            var field=name switch{"quantity"=>"swap.quantity","fixed price"=>"swap.fixedPrice","memo"=>"swap.memo","structure"=>"swap.structure","fixed direction"=>"swap.fixedDirection","price mode"=>"swap.priceMode",_=>throw Explicit()};
            value=(field,value.ToLowerInvariant()) switch{
                ("swap.structure","fixed for floating")=>"financial_fixed_floating",("swap.structure","floating for floating")=>"financial_floating_floating",
                ("swap.fixedDirection","pay fixed")=>"-1",("swap.fixedDirection","receive fixed")=>"1",_=>value};
            Add(field,value);
        }
        if(offset!=body.Length||terms.Count==0)throw Explicit();return terms;
    }
    [GeneratedRegex("""^Set ((?:ticket|swap)[.][a-zA-Z]+) = ("(?:[^"\\]|\\.)*")$""")]private static partial Regex Assignment();
    [GeneratedRegex("""\G(?:^|, | and )(quantity|fixed price|memo|structure|fixed direction|price mode) ("(?:[^"\\]|\\.)*"|-?[0-9]+(?:\.[0-9]+)?|fixed for floating|floating for floating|pay fixed|receive fixed|agreed)(?=$|, | and )""",RegexOptions.IgnoreCase)]private static partial Regex NaturalTerm();
    static ToolRefusal Invalid()=>new("walkthrough_plan_invalid");
    [GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?\z")]private static partial Regex Decimal();
}

public sealed partial class AgentTools
{
    static AgentTool WalkthroughTool=>Tool("present_walkthrough","Publish a visible, interruptible presentation walkthrough, at most 12 steps, after reading every page it names. Use for requests to show, find, explain or do something on screen. Takes planJson, a JSON array. No commands or selectors. go: {kind,target} uses an issued handle; New trade, New futures trade and New swap page handles open their native entry controls. The controls digest reads entry metadata and issues these handles; do not repeat that read. Other steps name kind and page: say adds message (a code from presentation.narration); pause; scroll_to adds widget; select adds focus {tradeIds,pivotCell}; sort adds column,direction; filter adds field,value; open adds pane (details/charts/new_future/review), target (issued handle or null), proposalStep (existing proposal ordinal or null); fill adds field,value. Read presentation capabilities from the page digest. Fill only the fields in presentation.fields, using the complete affirmative ask syntax in presentation.fillEvidence. For swaps, set explicit structure before its side fields; fixed price also requires an explicit agreed price mode. Do not choose a direction, structure or price mode for the user. Other phrasing or terms require clarification. Never Book, Save, submit, check consent or Authorize.",
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
