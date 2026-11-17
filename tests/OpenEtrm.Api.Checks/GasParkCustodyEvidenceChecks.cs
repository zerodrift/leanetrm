using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class GasParkCustodyEvidenceChecks
{
    internal static void Run()
    {
        var at=TradingInstant.Parse("2032-01-15T10:00:00Z","synthetic");
        var request=new GasParkCustodyEvidenceRequest(Guid.NewGuid(),new(Guid.NewGuid(),at,new(2032,1,15)),new("gas_park_custody",Guid.NewGuid(),Guid.NewGuid()),
            JsonSerializer.SerializeToElement(new{sourceKey=" observed_receipt ",expectedRevision=0,kind="custody_receipt",quantity="720.000",observedAt="2032-01-15T10:00:00+00:00",evidence=" Synthetic custody observation "}));
        var normal=GasParkCustodyEvidenceStore.Normalize(request);
        if(normal.Selection.Text("sourceKey")!="observed_receipt"||normal.Selection.Text("quantity")!="720"||normal.Selection.Text("evidence")!="Synthetic custody observation"||
            normal.Selection.Instant("observedAt")!=at)throw new Exception("Reviewed raw custody observations must retain native-style canonical text, exact quantity and UTC microseconds.");
        void Reject(Action<JsonObject> mutate)
        {
            var selection=JsonNode.Parse(request.Selection.GetRawText())!.AsObject();mutate(selection);
            try{GasParkCustodyEvidenceStore.Normalize(request with{Selection=JsonSerializer.SerializeToElement(selection)});}
            catch(MarketDataException error)when(error.Code=="gas_custody_input"){return;}
            throw new Exception("Raw custody evidence requires its closed explicit observation fields and structured refusal.");
        }
        Reject(x=>x["quantity"]="-1");Reject(x=>x["quantity"]="1000000000000000000000000");
        Reject(x=>x["kind"]="title_receipt");Reject(x=>x["sourceKey"]="x\n");Reject(x=>x["evidence"]="\0");
        Reject(x=>x["observedAt"]="2032-01-15T10:00:00.0000001Z");Reject(x=>x["observedAt"]="2032-01-15T11:00:00+01:00");
        Reject(x=>x["observedAt"]="2032-01-15T10:00:00.00000001Z");
        Reject(x=>x["accountId"]=Guid.NewGuid());Reject(x=>x.Remove("expectedRevision"));
        var zero=JsonNode.Parse(request.Selection.GetRawText())!.AsObject();zero["quantity"]="0";
        if(GasParkCustodyEvidenceStore.Normalize(request with{Selection=JsonSerializer.SerializeToElement(zero)}).Selection.Text("quantity")!="0")
            throw new Exception("An explicit zero raw observation remains recordable; it is not accepted performance.");
        var supplementary=string.Concat(Enumerable.Repeat("\U0001F30C",65));
        var unicode=JsonNode.Parse(request.Selection.GetRawText())!.AsObject();unicode["sourceKey"]=supplementary;
        unicode["evidence"]=string.Concat(Enumerable.Repeat("\U0001F30C",2000));
        if(GasParkCustodyEvidenceStore.Normalize(request with{Selection=JsonSerializer.SerializeToElement(unicode)}).Selection.Text("sourceKey")!=supplementary)
            throw new Exception("Custody key/evidence bounds must count Unicode scalars, preserving exact opaque identity.");
        Reject(x=>x["sourceKey"]=string.Concat(Enumerable.Repeat("\U0001F30C",129)));
        Reject(x=>x["evidence"]=string.Concat(Enumerable.Repeat("\U0001F30C",2001)));
        unicode["sourceKey"]="\u00a0";unicode["evidence"]="\nSynthetic free evidence\n";
        var opaque=GasParkCustodyEvidenceStore.Normalize(request with{Selection=JsonSerializer.SerializeToElement(unicode)});
        if(opaque.Selection.Text("sourceKey")!="\u00a0"||opaque.Selection.Text("evidence")!="\nSynthetic free evidence\n")
            throw new Exception("Opaque keys use ASCII trimming only; free multiline evidence must not inherit key restrictions.");
        Console.WriteLine("PASS custody evidence ingress: closed observation-only fields, canonical identity, explicit zero, native bounds and structured malformed refusal");
    }
}
