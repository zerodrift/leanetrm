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
    }
}
