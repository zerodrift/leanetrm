using System.Text.Json;
using OpenEtrm.Api;

internal static class UnifiedCatalogueChecks
{
    internal static void Run()
    {
        // A caller selects a curve/contract, not a hidden population containing it.
        // Unknown population selectors must refuse rather than silently changing pricing inputs.
        foreach(var type in new[]{typeof(CustomCurveSaveRequest),typeof(CustomCurvePreviewRequest),
            typeof(CustomCurveCompileRequest),typeof(CustomCurveCompositionRequest),typeof(MarketDatesRequest),
            typeof(ContractPriceTermsSaveRequest),typeof(ContractPriceTermsPreviewRequest),
            typeof(PricePublicationSaveRequest),typeof(PricePublicationPreviewRequest),
            typeof(PricePublicationOccurrenceSaveRequest),typeof(PricePublicationFinalIngestRequest),typeof(ComponentHistoryRequest)})
        {
            try { JsonSerializer.Deserialize("{\"marketRevisionId\":\"11111111-1111-1111-1111-111111111111\"}",type,new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch(JsonException error)when(error.Message.Contains("marketRevisionId",StringComparison.Ordinal)){continue;}
            throw new Exception($"{type.Name} still accepts a separate source-market selector.");
        }
        if(typeof(PreviewManifest).GetProperty("CurveMarkets") is not null)
            throw new Exception("The workspace still partitions ordinary curves into named source markets.");
        Console.WriteLine("PASS unified catalogue: curve, contract, publication and calendar operations refuse population selectors");
    }
}
