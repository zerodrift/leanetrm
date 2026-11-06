using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using OpenEtrm.Api;

static class AssistantWorkspaceChecks
{
    public static void Run()
    {
        DiscoveryWork();
        foreach(var page in new[]{"dashboard","crude_setup","vol_surfaces","facility_quantities"})
        {
            PageReports.Require(JsonSerializer.SerializeToElement(new{page,selection=new{}}));
            if((page is "dashboard" or "vol_surfaces")&&(PageReports.Dispatch(JsonSerializer.SerializeToElement(new{page,selection=new{}})) is not null||PageReports.Opens(page)))
                throw new Exception("An identity-only page gained a reader or navigation target.");
            foreach(var invalid in new[]{"{\"value\":\"918273\"}","{}"})
            foreach(var extra in invalid=="{}"?new[]{",\"layout\":{}",",\"view\":{}",",\"focus\":{}"}:new[]{""})
            {
                try{PageReports.Require(JsonDocument.Parse("{\"page\":\""+page+"\",\"selection\":"+invalid+extra+"}").RootElement);throw new Exception("Browser values or Blotter fields entered an identity-only report.");}
                catch(OpenEtrm.MarketData.MarketDataException e) when(e.Code=="page_report_invalid"){}
            }
        }
        BusinessDatePins();
        var pages=new[]{"risk_jobs","delivery_prices","interval_series","interval_curves","users_access","settings_ai",
            "blotter_risk","blotter_risk_lens","blotter_new_trade","blotter_new_future","blotter_new_swap","gas_setup","crude_setup","facility_quantities"};
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
        foreach(var id in new[]{"gas-setup.catalog_v1","crude-setup.catalog_v1","facilities.quantities.directory_v1","facilities.quantities.read_v1","gas.quantities.read_v1"})
            if(AssistantOperations.RequireRead(id).CurrentOnly)throw new Exception("Physical reads must retain the question's exact context.");
        foreach(var id in new[]{"gas-setup.save_v1","gas-setup.publish_v1","crude-setup.save_v1","crude-setup.publish_v1","facilities.quantities.record_v1","gas.quantities.record_v1"})
            if(AssistantOperations.Find(id) is not {RequiresApproval:true,CurrentOnly:true})throw new Exception("Physical changes require working context and human approval.");
        if(AssistantWorkspace.CanOpen("facility_quantities",["ReadMarketData","ReadTrades"]))throw new Exception("Facility navigation requires facility read authority.");
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

    static void BusinessDatePins()
    {
        var catalog=Guid.NewGuid();var id=Guid.NewGuid();
        var manifest=new PreviewManifest("local-synthetic-preview",catalog,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),[],null);
        var report=JsonSerializer.SerializeToElement(new{page="facility_quantities",businessDate="2021-01-15",selection=new{ledgerId=(Guid?)null}});
        PageReports.Require(report);
        var context=JsonSerializer.SerializeToElement(new{mode="current",contextId=id,frameId=(Guid?)null,cutoff="2026-11-05T12:00:00Z"});
        var pin=TurnPin.From(context,JsonSerializer.SerializeToElement((object?)null),catalog,manifest,report);
        var certificate=new ScheduleContext("2021-01-15",null,"2026-11-05T12:00:00Z",id);
        if(!pin.Working||pin.Frame is not null||pin.Exact(certificate)!=certificate)throw new Exception("Facility questions require their explicit date and certified knowledge without a price close.");
        try{pin.RequireFrame();throw new Exception("A facility date became a pricing frame.");}catch(OpenEtrm.MarketData.MarketDataException e)when(e.Code=="snapshot_missing"){}
        try{pin.Exact(certificate with{EffectiveOn="2021-01-16"});throw new Exception("A different business date was accepted.");}catch(InvalidOperationException){}
        foreach(var page in new[]{"gas_setup","crude_setup","curves"})
        {
            try{PageReports.Require(JsonSerializer.SerializeToElement(new{page,businessDate="2021-01-15",selection=new{}}));throw new Exception("Another page accepted a facility date.");}
            catch(OpenEtrm.MarketData.MarketDataException e)when(e.Code=="page_report_invalid"){}
        }
        var operation=AssistantOperations.RequireRead("facilities.quantities.directory_v1");
        foreach(var day in new[]{"2021-01-15","2021-01-16"})
        {
            var input=AssistantWorkspace.ParseInput(JsonSerializer.Serialize(new{body=new{context=new{effectiveOn=day,knowledgeMode="retained",retained=certificate,convention=FacilityQuantityStore.Convention}},parameters=new{}}));
            var request=new AssistantOperationInput(JsonNode.Parse(input.GetProperty("body").GetRawText()),new JsonObject());
            if(day==certificate.EffectiveOn)AssistantWorkspace.RequireFacilityContext(operation,request,certificate,false);
            else try{AssistantWorkspace.RequireFacilityContext(operation,request,certificate,false);throw new Exception("A facility read changed the asked date.");}catch(ToolRefusal e)when(e.Code=="operation_context_mismatch"){}
        }
        Console.WriteLine("PASS facility questions: explicit operating date, exact certified cutoff and no inferred pricing frame");
    }

    static void DiscoveryWork()
    {
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();
        using var app=builder.Build();var group=app.MapGroup("/api");
        var built=0;var enumerations=0;
        foreach(var operation in AssistantOperations.Catalog)
            ((IEndpointConventionBuilder)group.MapMethods(operation.Path,[operation.Method],(HttpContext _)=>Task.CompletedTask)).Add(_=>built++);
        // Only endpoint metadata is used here; authenticated discovery still obtains its own current permissions.
        var workspace=new AssistantWorkspace(null!,"api","/api",app.Services,()=>
        {
            enumerations++;return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source=>source.Endpoints);
        },new HttpContextAccessor());
        var permissions=AssistantOperations.Catalog.SelectMany(o=>o.Permissions).Distinct().ToArray();
        var result=workspace.DiscoverOperations("all",null,permissions,CancellationToken.None);
        if(result.Count!=AssistantOperations.Catalog.Count||!result.Select(o=>o!["id"]!.GetValue<string>()).SequenceEqual(AssistantOperations.Catalog.Select(o=>o.Id)))
            throw new Exception("Full discovery lost or changed an authorized operation.");
        if(enumerations!=1||built!=AssistantOperations.Catalog.Count)
            throw new Exception($"Full operation discovery rebuilt route metadata: {enumerations} source reads and {built} route builds for {AssistantOperations.Catalog.Count} operations.");
        var denied=workspace.DiscoverOperations("all",null,[],CancellationToken.None);
        if(denied.Count!=0)throw new Exception("A prior permission set survived a later discovery.");
        ((IEndpointRouteBuilder)app).DataSources.Clear();
        if(workspace.DiscoverOperations("all",null,permissions,CancellationToken.None).Count!=0)
            throw new Exception("Discovery retained endpoints that are no longer available.");
        var before=enumerations;using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try{workspace.DiscoverOperations("all",null,permissions,cancelled.Token);throw new Exception("Cancelled discovery continued to resolve routes.");}
        catch(OperationCanceledException){}
        if(enumerations!=before)throw new Exception("Cancelled discovery read route metadata.");
        Console.WriteLine("PASS assistant discovery: one route snapshot, fresh permissions/endpoints and cancellation before metadata work");
    }
}
