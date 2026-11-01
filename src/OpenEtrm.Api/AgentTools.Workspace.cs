using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenEtrm.Api;

sealed record WorkspaceArguments(string Kind,string? Page,string? Operation,JsonElement? Input):ToolArguments;

public sealed partial class AgentTools
{
    static ImmutableArray<AgentTool> WorkspaceTools=>[
        Tool("read_workspace_page","Inspects the page the user is on, including pages for risk, trade entry, delivery prices, interval data, administration and AI settings. Returns its available operations and current directory when there is one. Unsaved terms are not calculated values. Use discover_page_operations to inspect a request contract, read_page_operation for reads and previews, and propose_page_action for every save or other effect.",
            """{"type":"object","properties":{"page":{"type":"string"}},"required":["page"],"additionalProperties":false}""",[AssistantOperations.Permission]),
        Tool("discover_page_operations","Discovers the same authorized operations used by the application pages. Name a page or all; operation null lists its operations, an exact operation id returns its handler's input contract. Inputs are JSON with body and string-valued parameters. Missing financial terms require a question. No arbitrary URLs, credentials, SQL or execution are available.",
            """{"type":"object","properties":{"page":{"type":"string"},"operation":{"type":["string","null"]}},"required":["page","operation"],"additionalProperties":false}""",[AssistantOperations.Permission]),
        Tool("read_page_operation","Runs a discovered read or calculation through the page's ordinary authorized operation. Supply its exact operation and inputJson, an object with body (the request or null) and parameters (string query/route values). Financial reads use the retained context and frame this turn names, knowledgeMode retained and that exact contextId; explicit knownAt is this turn's cutoff. Current-only pages refuse historical views. Every command is refused here, including jobs, publishing, administration, tests with external effects and exports that record a fact.",
            OperationSchema,[AssistantOperations.Permission]),
        Tool("propose_page_action","Prepares a discovered command for human review. Supply the complete explicit inputJson with body and parameters, expected versions and all financial policies. Preview financial changes first with the corresponding ordinary read operation. This tool never executes a command or saves business data, even if the question says save or approve. The application shows the exact payload and requires a separate human Authorize action. Credentials cannot be proposed. Say clearly that the proposal is awaiting authorization.",
            OperationSchema,[AssistantOperations.Permission])
    ];
    const string OperationSchema="""{"type":"object","properties":{"operation":{"type":"string","minLength":1,"maxLength":128},"inputJson":{"type":"string","minLength":2,"maxLength":6144}},"required":["operation","inputJson"],"additionalProperties":false}""";
    static ImmutableArray<ToolOperation> WorkspaceOperations=>[
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
            if(!PageReports.All.Contains(page)&&!(tool=="discover_page_operations"&&page=="all"))throw ToolInput.Invalid();
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
                    value["page"]=arguments.Page;
                    value["selection"]=JsonNode.Parse(turn.PageReport.GetProperty("selection").GetRawText());
                    var selection=turn.PageReport.GetProperty("selection");
                    if(selection.TryGetProperty("operation",out _))
                    {
                        var selected=(WorkspaceArguments)Parse("read_page_operation",selection).Arguments;
                        var selectedRead=await workspace.Read(turn,selected.Operation!,selected.Input!.Value,ct);
                        value["selectedView"]=selectedRead.Value;access=[..access.Concat(selectedRead.Access).Distinct()];
                    }
                    var directory=arguments.Page switch
                    {"risk_jobs"=>"portfolio.risk.jobs_v1","jobs"=>"jobs_v1","delivery_prices"=>"power-imports_v1","interval_series"=>"interval-series_v1",
                        "users_access"=>"admin_v1",_=>null};
                    if(directory is not null&&!selection.TryGetProperty("operation",out _))
                    {
                        var operation=AssistantOperations.RequireRead(directory);
                        access=[..access.Concat(AssistantWorkspace.Access(operation)).Distinct()];
                        var read=await workspace.Read(turn,directory,JsonSerializer.SerializeToElement(new{body=(object?)null,parameters=new{}}),ct);
                        value["directory"]=read.Value;access=[..access.Concat(read.Access).Distinct()];
                    }
                }
            }
            var content=Bounded(value,CountRows(value))??throw new ToolRefusal("result_too_large");
            return new(tool,call.Operation.Id,input,null,at,access,content,ToolStepEnd.Result);
        }
        catch(Exception e)
        {
            var (code,end)=Classify(e);
            if(e is ToolRefusal refusal){code=refusal.Code;end=ToolStepEnd.Refusal;}
            return new(tool,call.Operation.Id,input,code,at,access,end==ToolStepEnd.Refusal?Refusal(code!):null,end);
        }
    }
    static int CountRows(JsonNode? node)=>node switch
    {JsonArray array=>(array.Any(n=>n is JsonObject)?array.Count:0)+array.Sum(CountRows),JsonObject obj=>obj.Sum(p=>CountRows(p.Value)),_=>0};
}
