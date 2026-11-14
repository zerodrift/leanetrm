using System.Text.Json;
using OpenEtrm.Api;

static class AssistantSelectionChecks
{
    public static void Run()
    {
        var ids=Enumerable.Range(1,3).Select(n=>$"71000000-0000-4000-8000-{n:000000000000}").ToArray();
        var report=JsonSerializer.SerializeToElement(new{page="blotter",selection=new{view="blotter",mode="trades",bookIds=Array.Empty<Guid>(),portfolioIds=Array.Empty<Guid>(),groupBy=Array.Empty<string>(),measures=Array.Empty<string>(),tradeId=(Guid?)null,instrumentType=(string?)null,revision=(int?)null},focus=new{tradeIds=ids,pivotCell=(object?)null}});
        PageReports.Require(report);
        var extra=System.Text.Json.Nodes.JsonNode.Parse(report.GetRawText())!.AsObject();extra["focus"]!["values"]=new System.Text.Json.Nodes.JsonArray("12","13","14");
        try{PageReports.Require(JsonSerializer.SerializeToElement(extra));throw new Exception("Browser table values were accepted as selection evidence.");}
        catch(OpenEtrm.MarketData.MarketDataException e) when(e.Code=="page_report_invalid"){}
        Console.WriteLine("PASS selection asks: exact selected identities and rejection of table values");
        RiskViews();
    }

    // Risk reports which of its five views it shows (D392): exactly its view, and on Reports a selected retained report as well.
    static void RiskViews()
    {
        static JsonElement Risk(object selection)=>JsonSerializer.SerializeToElement(new{page="risk_jobs",selection});
        static string? Code(Action action){try{action();return null;}catch(OpenEtrm.MarketData.MarketDataException e){return e.Code;}}
        var job=JsonSerializer.Serialize(new{body=(object?)null,parameters=new{id="71000000-0000-4000-8000-000000000009"}});
        foreach(var (view,name) in new[]{("reports","Reports"),("pnl_explain","P&L explain"),("scenarios_greeks","Scenarios & Greeks"),("var_es","VaR / ES"),("run_history","Run history")})
        {
            var shown=Risk(new{view});
            PageReports.Require(shown);
            if(PageReports.Dispatch(shown) is not {Page:"risk_jobs",Call.Operation.Id:"workspace.page_v1"}||PageReports.SelectedRead("risk_jobs",shown.GetProperty("selection")) is not null||
                PageReports.RiskViewName(shown.GetProperty("selection"))!=name)
                throw new Exception($"Risk's {view} view was not reported as {name} with nothing selected.");
        }
        var opened=Risk(new{view="reports",operation="portfolio.risk.jobs.id_v1",inputJson=job});
        PageReports.Require(opened);
        if(PageReports.SelectedRead("risk_jobs",opened.GetProperty("selection")) is not {Operation:"portfolio.risk.jobs.id_v1"})
            throw new Exception("A retained report opened in Reports was not the selected read.");
        foreach(var selection in new object[]{new{},new{view="risk"},new{view="VaR / ES"},new{view=(string?)null},new{view=1},new{view="reports",extra=1},
            new{operation="portfolio.risk.jobs.id_v1",inputJson=job},new{view="run_history",operation="portfolio.risk.jobs.id_v1",inputJson=job},
            new{view="reports",operation="portfolio.risk.jobs_command_v1",inputJson=job},new{view="reports",operation="portfolio.risk.jobs.id_v1"},
            new{view="reports",operation="portfolio.risk.jobs_v1",inputJson=job},new{view="reports",operation="portfolio.risk.historical.summary_v1",inputJson=job},
            new{view="reports",operation="portfolio.risk.jobs.id_v1",inputJson=JsonSerializer.Serialize(new{body=(object?)null,parameters=new{id="synthetic"}})},
            new{view="reports",operation="portfolio.risk.jobs.id_v1",inputJson=JsonSerializer.Serialize(new{body=new{},parameters=new{id="71000000-0000-4000-8000-000000000009"}})}})
            if(Code(()=>PageReports.Require(Risk(selection)))!="page_report_invalid")
                throw new Exception($"Risk admitted the report selection {JsonSerializer.Serialize(selection)}.");
        foreach(var raw in new[]{"""{"page":"risk_jobs","selection":{"view":"reports","view":"var_es"}}""",
            """{"page":"risk_jobs","selection":{"view":"reports","operation":"portfolio.risk.jobs.id_v1","operation":"portfolio.risk.jobs_v1","inputJson":"{}"}}"""})
            if(Code(()=>PageReports.Require(JsonDocument.Parse(raw).RootElement))!="page_report_invalid")throw new Exception($"Risk admitted the repeated members of {raw}.");
        Console.WriteLine("PASS Risk reports exactly the view it shows, and a selected retained report only in Reports");
    }
}
