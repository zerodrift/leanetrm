using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class PpaPortfolioChecks
{
    internal static async Task Run(string path,bool denied=false)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new InvalidOperationException("Use the owned preview runner.");
        await using var database=new PreviewDatabase(root);
        using var fixture=JsonDocument.Parse(File.ReadAllText(path));var request=fixture.RootElement;
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var frame=request.GetProperty("frame").Deserialize<InputFrame>(json)!;
        var policy=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesRisk.Implementation,
            SwapStore.Convention,ZeroRateCurve.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1);
        foreach(var name in denied?new[]{"after"}:new[]{"before","after"})
        {
            var context=request.GetProperty(name).Deserialize<ScheduleContext>(json)!;
            try
            {
                await database.WithIdentity("risk","ReadTrades",async(connection,ct)=>
                {
                    await MarketContextStore.Bind(connection,database.Manifest.CatalogId,context,ct);
                    await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",database.Manifest.CatalogId,frame.At))await bind.ExecuteNonQueryAsync(ct);
                    var data=await database.Read(connection,ct,observationIds:frame.ObservationIds,observationScope:ObservationReadScope.ExactPins);
                    try
                    {
                        var result=await new PortfolioValuation(database).Calculate(connection,data,data.Snapshot(frame),context,[request.Id("bookId")],policy,ct);
                        if(name=="after")throw new Exception("A complete portfolio silently omitted its retained PPA positions.");
                        if(result.Positions.Length!=1||result.Positions[0].TradeId!=request.Id("futureTradeId")||result.Totals.IsEmpty)
                            throw new Exception("The independent pre-PPA control did not value its supported futures position.");
                        Console.WriteLine("PASS PPA portfolio: earlier certified complete scope values its supported position");
                    }
                    catch(MarketDataException error)when(name=="after"&&error.Code=="portfolio_ppa_unsupported")
                    {
                        if(denied)throw new Exception("Unsupported-product diagnostics preceded complete PPA reference authority.");
                        Console.WriteLine("PASS PPA portfolio: whole-book consumer refuses retained PPA before partial valuation evidence");
                    }
                    return true;
                },CancellationToken.None);
            }
            catch(PostgresException error)when(denied&&error.SqlState=="42501")
            {Console.WriteLine("PASS PPA portfolio: inaccessible PPA reference refuses authority before product diagnostics");}
        }
    }
}
