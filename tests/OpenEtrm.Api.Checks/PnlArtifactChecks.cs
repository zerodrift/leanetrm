using System.Text.Json;
using OpenEtrm.Api;

internal static class PnlArtifactChecks
{
    internal static void Run()
    {
        using var source=JsonDocument.Parse("""{"amount":"1/3","label":"Δ + <price>"}""");
        var report=source.RootElement;
        var stored=JsonSerializer.SerializeToElement(new{report},RiskJobs.Json).GetProperty("report");
        if(PnlReportArtifacts.ResultHash(report)!=PnlReportArtifacts.ResultHash(stored))
            throw new Exception("Embedding a report changed its financial content digest.");
        using var changed=JsonDocument.Parse("""{"amount":"1/4","label":"Δ + <price>"}""");
        if(PnlReportArtifacts.ResultHash(report)==PnlReportArtifacts.ResultHash(changed.RootElement))
            throw new Exception("An altered financial value retained its content digest.");
        Console.WriteLine("PASS P&L content identity survives JSON embedding and escaping, and detects changed exact values");
    }
}
