using System.Text.Json;
using OpenEtrm.Api;

internal static class SwapHoldingMarketChecks
{
    public static async Task Run(string path)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new Exception("Use the owned preview runner.");
        await using var database=new PreviewDatabase(root);
        using var fixture=JsonDocument.Parse(File.ReadAllText(path));
        var requested=fixture.RootElement.GetProperty("frame").Deserialize<InputFrame>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await database.WithIdentity("trader","ReadTrades",async(connection,ct)=>
        {
            var frame=requested.OriginCloseId is {} close
                ?await MarketCloses.Frame(connection,database.Manifest.CatalogId,close,null,ct)
                :requested;
            var view=await TradingView.Capture(connection,new KnowledgeStore(database.Manifest.CatalogId),frame,ct);
            return await new TradeReads(database).Read(connection,view,async data=>
            {
                var result=await new SwapStore(database).Holdings(connection,data,view.CalculationFrame,[],[],ct);
                var rows=result.Rows.Select(row=>JsonSerializer.SerializeToElement(row)).ToArray();
                var supplemental=rows.Where(row=>row.Id("definitionRevisionId")!=data.DefinitionRevision.RevisionId&&
                    row.GetProperty("quantity").ValueKind==JsonValueKind.Null).ToArray();
                if(supplemental.Length==0||!rows.Any(row=>row.Text("settlement")=="Active"))
                    throw new Exception($"The market-read regression requires supplemental scheduled holdings and active lifecycle holdings: supplemental={supplemental.Length}, active={rows.Count(row=>row.Text("settlement")=="Active")}.");
                if(result.SupplementalMarketLoads!=1)
                    throw new Exception($"One Holdings read must load supplemental markets once, got {result.SupplementalMarketLoads}.");
                Console.WriteLine("PASS holdings with supplemental scheduled trades and active lifecycle reuse one authorized supplemental market load");
                return true;
            },ct);
        },CancellationToken.None);
    }
}
