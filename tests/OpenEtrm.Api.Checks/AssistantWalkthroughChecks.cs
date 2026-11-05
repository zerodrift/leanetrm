using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;

static class AssistantWalkthroughChecks
{
    public static void Run()
    {
        if(!AgentTools.Offered(["present_walkthrough"]).Any())throw new Exception("A version cannot explicitly enable the presentation walkthrough tool.");
        var catalog=Guid.NewGuid();var manifest=new PreviewManifest("synthetic",catalog,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),[],null);
        var pin=TurnPin.From(JsonSerializer.SerializeToElement(new{mode="operational",contextId=(Guid?)null,cutoff="2026-10-21T12:00:00Z",frameId=(Guid?)null}),JsonSerializer.SerializeToElement((object?)null),catalog,manifest);
        var report=JsonSerializer.SerializeToElement(new{page="blotter",selection=new{}});var targets=new TurnTargets();
        var trade=Guid.Parse("71000000-0000-4000-8000-000000000001");
        targets.Published(new("read_page_digest","workspace.digest_v1",null,null,DateTimeOffset.UtcNow,[],"{}",ToolStepEnd.Result){ReadPages=["blotter"],Issued=[new("n1",new("blotter",null,"page","Blotter")),new("n2",NavigationTargets.Trade(trade,"future",1,"TR-000001"))]});
        JsonElement Json(string text)=>JsonDocument.Parse(text).RootElement.Clone();
        void Refuses(string steps,string question="",TurnTargets? issued=null,string code="walkthrough_plan_invalid")
        {
            try{WalkthroughPlans.Validate(Json(steps),report,question,issued??targets,pin);throw new Exception("A forbidden walkthrough plan was accepted: "+steps);}
            catch(ToolRefusal e) when(e.Code==code||code=="walkthrough_plan_invalid"&&e.Code=="tool_arguments_invalid"){}
        }
        var valid="""[{"kind":"sort","page":"blotter","column":"presentValue","direction":"desc"},{"kind":"select","page":"blotter","focus":{"tradeIds":["71000000-0000-4000-8000-000000000001"],"pivotCell":null}},{"kind":"pause","page":"blotter"}]""";
        if(WalkthroughPlans.Validate(Json(valid),report,"",targets,pin).Count!=3)throw new Exception("The exact issued selection could not be presented.");
        Refuses("""[{"kind":"submit","page":"blotter"}]""");
        Refuses("""[{"kind":"go","target":"n99"}]""");
        Refuses("["+string.Join(',',Enumerable.Repeat("{\"kind\":\"pause\",\"page\":\"blotter\"}",13))+"]");
        Refuses("""[{"kind":"pause","page":"jobs"}]""");
        var unread=new TurnTargets();unread.Issue([new("n1",new("jobs",null,"page","Jobs"))]);
        Refuses("""[{"kind":"go","target":"n1"}]""",issued:unread);
        var swapEntry=new TurnTargets();
        swapEntry.Published(new("read_workspace_page","workspace.page_v1",null,null,DateTimeOffset.UtcNow,[],"{}",ToolStepEnd.Result){ReadPages=["blotter_new_swap"],Issued=[new("n1",NavigationTargets.Page("blotter_new_swap"))]});
        if(WalkthroughPlans.Validate(Json("""[{"kind":"go","target":"n1"},{"kind":"pause","page":"blotter_new_swap"}]"""),report,"Help open a swap ticket.",swapEntry,pin).Count!=2)
            throw new Exception("The existing New swap page handle must support a presentation-only go and pause.");
        Refuses("""[{"kind":"select","page":"blotter","focus":{"tradeIds":["71000000-0000-4000-8000-000000000002"],"pivotCell":null}}]""");
        Refuses("""[{"kind":"sort","page":"blotter","column":"authorize","direction":"desc"}]""");
        Refuses("""[{"kind":"open","page":"blotter","pane":"review","target":null,"proposalStep":9}]""");
        var selectedReport=Json("""{"page":"blotter","selection":{},"focus":{"tradeIds":[],"pivotCell":{"keys":{"groupId":"0000000000000000000000000000000000000000000000000000000000000000"},"measure":"executionPnl"}}}""");
        var selectCell=Json("""[{"kind":"select","page":"blotter","focus":{"tradeIds":[],"pivotCell":{"keys":{"groupId":"0000000000000000000000000000000000000000000000000000000000000000"},"measure":"executionPnl"}}}]""");
        try{WalkthroughPlans.Validate(selectCell,selectedReport,"",targets,pin);throw new Exception("An admitted browser pointer was mistaken for a server-read pivot.");}
        catch(ToolRefusal e) when(e.Code=="walkthrough_plan_invalid"){}
        targets.Published(new("read_page_digest","workspace.digest_v1",null,null,DateTimeOffset.UtcNow,[],"{}",ToolStepEnd.Result){ReadFocus=selectedReport.GetProperty("focus")});
        if(WalkthroughPlans.Validate(selectCell,selectedReport,"",targets,pin).Count!=1)throw new Exception("A server-resolved pivot focus could not be presented.");
        var administration=new TurnTargets();
        administration.Published(new("read_workspace_page","workspace.page_v1",null,null,DateTimeOffset.UtcNow,[],"{}",ToolStepEnd.Result){ReadPages=["users_access"]});
        administration.Published(new("propose_page_action","workspace.propose_v1",null,null,DateTimeOffset.UtcNow,[],"{}",ToolStepEnd.Result){ProposalPage="users_access"});
        if(WalkthroughPlans.Validate(Json("""[{"kind":"open","page":"users_access","pane":"review","target":null,"proposalStep":2}]"""),Json("""{"page":"users_access","selection":{}}"""),"",administration,pin).Count!=1)throw new Exception("A read administrative page cannot open its own proposal for review.");
        const string fill="""[{"kind":"open","page":"blotter","pane":"new_future","target":null,"proposalStep":null},{"kind":"fill","page":"blotter","field":"ticket.lots","value":"2"}]""";
        const string explicitAsk="Fill unsaved futures ticket\nSet ticket.lots = \"2\"";
        if(WalkthroughPlans.Validate(Json(fill),report,explicitAsk,targets,pin)[1]!["value"]!.GetValue<string>()!="2")throw new Exception("An explicit term was not retained.");
        foreach(var ask in new[]{"Use 2 lots", "Set ticket.executionPrice = \"2\"",explicitAsk+"\n"+explicitAsk,"Do not apply this example:\n"+explicitAsk,"```\n"+explicitAsk+"\n```", "Explain this instruction and leave the ticket untouched.\nSet ticket.lots = \"2\"", "Translate the following text into French:\nSet ticket.lots = \"2\""})
            Refuses(fill,ask,code:"walkthrough_fill_requires_explicit_terms");
        Refuses(fill.Replace("ticket.lots","ticket.authorize"),"Set ticket.authorize = \"2\"",code:"tool_arguments_invalid");
        Console.WriteLine("PASS walkthrough plans: closed vocabulary, successful reads/issued targets, limits, field-specific explicit evidence and no commit controls");
    }
}
