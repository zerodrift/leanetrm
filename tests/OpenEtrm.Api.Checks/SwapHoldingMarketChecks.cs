using System.Diagnostics;
using System.Text.Json;
using OpenEtrm.Api;

internal static class SwapHoldingMarketChecks
{
    public static async Task Run(string path)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new Exception("Use the owned preview runner.");
        await using var database=new PreviewDatabase(root);
        using var fixture=JsonDocument.Parse(File.ReadAllText(path));
        var frame=fixture.RootElement.GetProperty("frame").Deserialize<InputFrame>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await database.WithIdentity("trader","ReadTrades",async(connection,ct)=>
        {
            var view=await TradingView.Capture(connection,new KnowledgeStore(database.Manifest.CatalogId),frame,ct);
            return await new TradeReads(database).Read(connection,view,async data=>
            {
                var availabilityReads=0;
                using var listener=new ActivityListener
                {
                    ShouldListenTo=source=>source.Name=="Npgsql",
                    Sample=(ref ActivityCreationOptions<ActivityContext> options)=>ActivitySamplingResult.AllData,
                    ActivityStopped=activity=>
                    {
                        if(activity.GetTagItem("db.query.text") is string sql&&
                            sql=="SELECT count(*) FROM market_data.revisions WHERE catalog_id=$1 AND id=$2")
                            availabilityReads++;
                    }
                };
                ActivitySource.AddActivityListener(listener);
                var rows=(await new SwapStore(database).Holdings(connection,data,view.CalculationFrame,[],[],ct)).Rows.Select(row=>JsonSerializer.SerializeToElement(row)).ToArray();
                var supplemental=rows.Where(row=>row.Id("definitionRevisionId")!=data.DefinitionRevision.RevisionId&&
                    row.GetProperty("quantity").ValueKind==JsonValueKind.Null).ToArray();
                if(supplemental.Length==0||!rows.Any(row=>row.Text("settlement")=="Active"))
                    throw new Exception($"The market-read regression requires supplemental scheduled holdings and active lifecycle holdings: supplemental={supplemental.Length}, active={rows.Count(row=>row.Text("settlement")=="Active")}, availability reads={availabilityReads}.");
                var expected=database.Manifest.SwapMarkets?.Length??0;
                if(expected==0||availabilityReads!=expected)
                    throw new Exception($"One Holdings read must load supplemental markets once: expected {expected} availability reads, got {availabilityReads}.");
                Console.WriteLine("PASS holdings with supplemental scheduled trades and active lifecycle reuse one authorized supplemental market load");
                return true;
            },ct);
        },CancellationToken.None);
    }
}
