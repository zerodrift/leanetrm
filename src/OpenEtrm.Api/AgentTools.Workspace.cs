using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenEtrm.Api;

sealed record WorkspaceArguments(string Kind,string? Page,string? Operation,JsonElement? Input):ToolArguments;

public sealed partial class AgentTools
{
    static ImmutableArray<AgentTool> WorkspaceTools=>[
        WalkthroughTool,
        Tool("read_page_digest",
            """{"type":"object","properties":{"mode":{"type":"string","enum":["overview","controls"]}},"required":["mode"],"additionalProperties":false}""",[AssistantOperations.Permission]),
        Tool("read_workspace_page",
            """{"type":"object","properties":{"page":{"type":"string"}},"required":["page"],"additionalProperties":false}""",[AssistantOperations.Permission]),
        Tool("discover_page_operations",
            """{"type":"object","properties":{"page":{"type":"string"},"operation":{"type":["string","null"]}},"required":["page","operation"],"additionalProperties":false}""",[AssistantOperations.Permission]),
        Tool("read_page_operation",
            OperationSchema,[AssistantOperations.Permission]),
        Tool("propose_page_action",
            OperationSchema,[AssistantOperations.Permission])
    ];
    const string OperationSchema="""{"type":"object","properties":{"operation":{"type":"string","minLength":1,"maxLength":128},"inputJson":{"type":"string","minLength":2,"maxLength":6144}},"required":["operation","inputJson"],"additionalProperties":false}""";
    static ImmutableArray<ToolOperation> WorkspaceOperations=>[
        new("workspace.walkthrough_v1","present_walkthrough",ToolNeeds.View,WorkspaceAccess,[]),
        new("workspace.digest_v1","read_page_digest",ToolNeeds.View,WorkspaceAccess,[]),
        new("workspace.page_v1","read_workspace_page",ToolNeeds.View,WorkspaceAccess,[]),
        new("workspace.discover_v1","discover_page_operations",ToolNeeds.View,WorkspaceAccess,[]),
        new("workspace.read_v1","read_page_operation",ToolNeeds.View,WorkspaceAccess,[]),
        new("workspace.propose_v1","propose_page_action",ToolNeeds.CurrentOnly,WorkspaceAccess,[])
    ];
    static ImmutableArray<ReadAccess> WorkspaceAccess(PreviewManifest _)=>[new("workspace_access",null,AssistantOperations.Permission)];
    static ToolCall ParseWorkspace(string tool,ToolInput members)
    {
        if(tool is "read_workspace_page" or "discover_page_operations")
        {
            if(tool=="read_workspace_page")members.Exactly("page");else members.Exactly("page","operation");
            var page=members.Text("page",1,64);
            if(!PageReports.All.Contains(page)&&!(tool=="discover_page_operations"&&page is "all" or IssueReports.Page))throw ToolInput.Invalid();
            var operation=tool=="discover_page_operations"&&members.Has("operation")?members.Text("operation",1,128):null;
            return new(Operations[tool=="read_workspace_page"?"workspace.page_v1":"workspace.discover_v1"],new WorkspaceArguments(tool,page,operation,null));
        }
        members.Exactly("operation","inputJson");
        var id=members.Text("operation",1,128);
        if(AssistantOperations.Find(id) is null)throw ToolInput.Invalid();
        var input=AssistantWorkspace.ParseInput(members.Formula("inputJson"));
        return new(Operations[tool=="read_page_operation"?"workspace.read_v1":"workspace.propose_v1"],new WorkspaceArguments(tool,null,id,input));
    }
    async Task<ToolStep> RunWorkspace(TurnReads turn,TurnTargets targets,string tool,JsonElement input,ToolCall call,WorkspaceArguments arguments,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(turn.ReadLimit);ct=deadline.Token;
        var access=WorkspaceAccess(database.Manifest);var at=DateTimeOffset.UtcNow;
        try
        {
            var workspace=Workspace??throw new ToolRefusal("operation_unavailable");JsonObject value;
            await database.WithAgent(turn.Agent,turn.ReadLimit,[AssistantOperations.Permission],instant=>at=instant,(_,_)=>Task.FromResult(true),ct);
            access=[..access.Concat(await workspace.SourceAccess(turn,ct)).Distinct()];
            if(arguments.Kind=="propose_page_action")value=await workspace.Propose(turn,arguments.Operation!,arguments.Input!.Value,targets.Steps+1,ct);
            else if(arguments.Kind=="read_page_operation")
            {
                // Keep the actual operation permissions in the source set before execution, including a refusal.
                access=[..access.Concat(AssistantWorkspace.Access(AssistantOperations.RequireRead(arguments.Operation!))).Distinct()];
                var read=await workspace.Read(turn,arguments.Operation!,arguments.Input!.Value,ct);value=read.Value;
                access=[..access.Concat(read.Access).Distinct()];
            }
            else
            {
                value=await workspace.Discover(turn,arguments.Page!,arguments.Operation,ct);
                if(arguments.Kind=="read_workspace_page")
                {
                    value["page"]=arguments.Page;value["presentation"]=WalkthroughPlans.Capabilities(arguments.Page!);
                    var observed=arguments.Page==turn.PageReport.GetProperty("page").GetString();
                    var selection=observed?turn.PageReport.GetProperty("selection"):JsonSerializer.SerializeToElement(new{});
                    value["selection"]=JsonNode.Parse(selection.GetRawText());
                    value["pageStateSource"]=observed?"question":"unobserved";
                    if(PageReports.SelectedRead(arguments.Page!,selection) is {} selected)
                    {
                        var selectedRead=await workspace.Read(turn,selected.Operation!,selected.Input!.Value,ct);
                        value["selectedView"]=selectedRead.Value;access=[..access.Concat(selectedRead.Access).Distinct()];
                    }
                    else if(arguments.Page=="physical_operations"&&SelectedGasDay(selection,turn.Pin) is {} gasDay)
                    {
                        // The selected gas day is read as its page reads it, under the turn's pin. A refusal is reported
                        // in place of the day, so the rest of the page stays readable.
                        try
                        {
                            var financial=await workspace.Discover(turn,arguments.Page!,"gas.operations.read_v1",ct);
                            value["context"]=financial["context"]?.DeepClone();
                            gasDay=SelectedGasDay(selection,turn.Pin)!;
                            var dayRead=await workspace.Read(turn,"gas.operations.read_v1",JsonSerializer.SerializeToElement(gasDay),ct);
                            dayRead.Value["inputJson"]=gasDay.ToJsonString();
                            value["selectedView"]=dayRead.Value;access=[..access.Concat(dayRead.Access).Distinct()];
                        }
                        catch(Exception e) when(Classify(e) is (var code,ToolStepEnd.Refusal))
                        {
                            value["selectedView"]=JsonNode.Parse(Refusal(code!));
                        }
                    }
                    var directory=arguments.Page switch
                    {"risk_jobs"=>"portfolio.risk.jobs_v1","jobs"=>"jobs_v1","delivery_prices"=>"power-imports_v1","interval_series"=>"interval-series_v1",
                        "users_access"=>"admin_v1","gas_setup"=>"gas-setup.catalog_v1","crude_setup"=>"crude-setup.catalog_v1",
                        "facility_quantities"=>selection.TryGetProperty("ledgerId",out var ledger)&&ledger.ValueKind==JsonValueKind.String?"facilities.quantities.read_v1":"facilities.quantities.directory_v1",_=>null};
                    if(directory is not null&&!selection.TryGetProperty("operation",out _))
                    {
                        var operation=AssistantOperations.RequireRead(directory);
                        access=[..access.Concat(AssistantWorkspace.Access(operation)).Distinct()];
                        var native=new JsonObject{["body"]=null,["parameters"]=new JsonObject()};
                        if(directory is "gas-setup.catalog_v1" or "crude-setup.catalog_v1")
                            native["parameters"]!["snapshotId"]=turn.Pin.RequireFrame().Id.ToString();
                        if(arguments.Page=="facility_quantities")
                        {
                            var financial=await workspace.Discover(turn,arguments.Page!,directory,ct);
                            var certificate=financial["context"]??throw new ToolRefusal("context_required");
                            value["context"]=certificate.DeepClone();
                            native["body"]=new JsonObject{["context"]=new JsonObject{["effectiveOn"]=certificate["effectiveOn"]!.DeepClone(),
                                ["knowledgeMode"]="retained",["retained"]=certificate.DeepClone(),["convention"]=FacilityQuantityStore.Convention}};
                            if(selection.TryGetProperty("ledgerId",out var selectedLedger)&&selectedLedger.ValueKind==JsonValueKind.String)native["body"]!["ledgerId"]=selectedLedger.GetString();
                        }
                        var read=await workspace.Read(turn,directory,JsonSerializer.SerializeToElement(native),ct);
                        value["directory"]=read.Value;access=[..access.Concat(read.Access).Distinct()];
                    }
                }
            }
            var (content,issued)=WorkspaceResult(value,arguments.Kind=="read_workspace_page"&&turn.Pin.BusinessDate is null?arguments.Page:null,targets.Next,turn.OutputLimits);
            return new(tool,call.Operation.Id,input,null,at,access,content,ToolStepEnd.Result){Issued=issued,ProposalPage=arguments.Kind=="propose_page_action"?AssistantOperations.Find(arguments.Operation!)!.Page:null,ReadPages=arguments.Kind=="read_workspace_page"?[arguments.Page!]:arguments.Kind=="read_page_operation"?[AssistantOperations.RequireRead(arguments.Operation!).Page]:[]};
        }
        catch(Exception e)
        {
            var (code,end)=Classify(e);
            if(e is ToolRefusal refusal){code=refusal.Code;end=ToolStepEnd.Refusal;}
            return new(tool,call.Operation.Id,input,code,at,access,end==ToolStepEnd.Refusal?Refusal(code!):null,end);
        }
    }
    // The operations read of a selected gas day, with the exact input it is read with: the turn's frame and retained
    // context and the physical gas valuation convention, so the assistant can repeat it for that day.
    internal static JsonObject? SelectedGasDay(JsonElement selection,TurnPin pin)=>
        !pin.Operational&&pin.Frame is {} frame&&selection.TryGetProperty("instrumentType",out var type)&&type.ValueKind==JsonValueKind.String&&type.GetString()==GasPositions.Type&&
        selection.TryGetProperty("tradeId",out var trade)&&trade.ValueKind==JsonValueKind.String&&selection.TryGetProperty("day",out var day)&&day.ValueKind==JsonValueKind.String
            ?new(){["body"]=new JsonObject{["context"]=new JsonObject{["snapshotId"]=frame.Id.ToString(),["convention"]=PhysicalGasStore.Convention,
                ["knowledgeMode"]="retained",["contextId"]=pin.ContextId.ToString()},["tradeId"]=trade.GetString(),["day"]=day.GetString()},["parameters"]=new JsonObject()}
            :null;

    internal static (string Content,ImmutableArray<IssuedTarget> Issued) WorkspaceResult(JsonObject value,string? page,int next,ToolOutputLimits limits)
    {
        ImmutableArray<IssuedTarget> issued=[];
        if(page is not null&&PageReports.Opens(page)&&value["operations"]!.AsArray().Count>0)
        {
            var target=NavigationTargets.Page(page);var handle=NavigationTargets.Handle(next);
            value["target"]=handle;value["targetLabel"]=target.Label;issued=[new(handle,target)];
        }
        var content=Bounded(value,CountRows(value),limits)??throw new ToolRefusal("result_too_large");
        return(content,issued);
    }
    static int CountRows(JsonNode? node)=>node switch
    {JsonArray array=>(array.Any(n=>n is JsonObject)?array.Count:0)+array.Sum(CountRows),JsonObject obj=>obj.Sum(p=>CountRows(p.Value)),_=>0};
}
