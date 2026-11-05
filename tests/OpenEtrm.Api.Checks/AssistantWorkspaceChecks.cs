using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;

static class AssistantWorkspaceChecks
{
    public static void Run()
    {
        var pages=new[]{"risk_jobs","delivery_prices","interval_series","interval_curves","users_access","settings_ai",
            "blotter_risk","blotter_risk_lens","blotter_new_trade","blotter_new_future","blotter_new_swap"};
        foreach(var page in pages)
        {
            PageReports.Require(JsonSerializer.SerializeToElement(new{page,selection=new{}}));
            if(PageReports.Dispatch(JsonSerializer.SerializeToElement(new{page,selection=new{}}))?.Call is null)
                throw new Exception($"The assistant cannot inspect {page}.");
        }
        foreach(var operation in new[]{"swaps.book_v1","futures.amend_v1","admin.update_v1","jobs.control_v1","settings.ai.agents.save_v1"})
        {
            var command=AssistantOperations.Find(operation);
            if(command is null||!command.RequiresApproval)throw new Exception($"{operation} must require human approval.");
            try{AssistantOperations.RequireRead(operation);throw new Exception("A command entered the read path.");}
            catch(ToolRefusal e) when(e.Code=="approval_required"){}
        }
        foreach(var excluded in new[]{"futures.amend-preview_v1","swaps.preview_v1","swaps.scheduled-price_v1"})
            if(AssistantOperations.Find(excluded) is not null)
                throw new Exception($"{excluded} cannot preserve this turn's financial context and must stay outside the catalog.");
        foreach(var (page,product) in new[]{("blotter_new_future","futures"),("blotter_new_swap","swaps")})
        {
            var entry=AssistantOperations.ForPage(page).ToArray();
            foreach(var suffix in new[]{"catalog","price","book"})
            {
                var operation=entry.Single(o=>o.Id==$"{product}.{suffix}_v1");
                if(!ReferenceEquals(operation,AssistantOperations.Find(operation.Id)))throw new Exception("An entry alias changed its authorized operation.");
            }
            if(entry.Any(o=>o.RequiresApproval&&o.Id!=$"{product}.book_v1"))throw new Exception("New trade discovery exposed an unrelated command.");
        }
        var handles=new TurnTargets();
        JsonObject PageValue()=>new(){["operations"]=new JsonArray(new JsonObject{["id"]="swaps.catalog_v1"},new JsonObject{["id"]="swaps.book_v1"})};
        foreach(var limit in new[]{new ToolOutputLimits(1,65536),new ToolOutputLimits(500,32)})
        {
            try{AgentTools.WorkspaceResult(PageValue(),"blotter_new_swap",handles.Next,limit);throw new Exception("An oversized page read issued a successful handle.");}
            catch(ToolRefusal e) when(e.Code=="result_too_large"){}
        }
        if(handles.Next!=1)throw new Exception("A refused page read consumed a handle.");
        var empty=AgentTools.WorkspaceResult(new(){["operations"]=new JsonArray()},"blotter_new_swap",handles.Next,new(500,65536));
        if(!empty.Issued.IsEmpty||JsonDocument.Parse(empty.Content).RootElement.TryGetProperty("target",out _))throw new Exception("A page without authorized operations issued a target.");
        var pageResult=AgentTools.WorkspaceResult(PageValue(),"blotter_new_swap",handles.Next,new(500,65536));
        if(pageResult.Issued is not [{Handle:"n1",Target:{Page:"blotter_new_swap",Selection:null,Kind:"page",Label:"New swap"}}])throw new Exception("A workspace page target must be its exact fixed page, without a selection.");
        handles.Published(new("read_workspace_page","workspace.page_v1",null,null,DateTimeOffset.UtcNow,[],pageResult.Content,ToolStepEnd.Result){Issued=pageResult.Issued});
        var next=AgentTools.WorkspaceResult(PageValue(),"blotter_new_future",handles.Next,new(500,65536));
        if(next.Issued[0].Handle!="n2")throw new Exception("Workspace handles must follow the turn's published targets.");
        foreach(var read in new[]{"portfolio.risk.statistical.setup_v1","portfolio.risk.parametric.summary_v1","portfolio.risk.historical.summary_v1","portfolio.risk.historical.sample_v1"})
        {
            var operation=AssistantOperations.Find(read);
            if(operation is not {Page:"risk_jobs",Method:"POST",RequiresApproval:false,CurrentOnly:false}||
                !operation.Permissions.Order().SequenceEqual(new[]{"ReadMarketData","ReadTrades","RunScenario"}))
                throw new Exception($"{read} must be a pinned Risk-page read under the scenario, trade and market permissions.");
            AssistantOperations.RequireRead(read);
        }
        if(AssistantOperations.Find("settings.ai.providers.key_v1") is not null||AssistantOperations.Find("arbitrary.sql_v1") is not null)
            throw new Exception("Credentials and arbitrary operations must stay outside the model catalog.");
        if(AssistantOperations.Catalog.Any(o=>o.Id.Length>64)||AssistantOperations.Catalog.Select(o=>o.Id).Distinct().Count()!=AssistantOperations.Catalog.Count)
            throw new Exception("Operation identities must be bounded and unique.");
        foreach(var invalid in new[]{"{\"body\":{\"knownAt\":\"a\",\"KnownAt\":\"b\"},\"parameters\":{}}",
            "{\"body\":null,\"parameters\":{\"id\":\"a\",\"Id\":\"b\"}}"})
        {
            try{AssistantWorkspace.ParseInput(invalid);throw new Exception("Ambiguous request members were accepted.");}
            catch(ToolRefusal e) when(e.Code=="tool_arguments_invalid"){}
        }
        Console.WriteLine("PASS assistant workspace: page coverage, explicit operation catalog and mandatory write approval");
    }
}
