using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.Api;

internal static class GasBillingReadChecks
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    const string Work="SELECT coalesce(jsonb_object_agg(schemaname||'.'||relname,seq_tup_read+idx_tup_fetch),'{}')::text FROM pg_stat_xact_user_tables WHERE schemaname IN('security','trading','market_data','ref_data')";

    public static async Task Run(string fixturePath)
    {
        var root=Path.GetFullPath(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")!);
        var runtime=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_RUNTIME")!);
        if(!File.Exists(Path.Combine(root,".openetrm-preview"))||Path.GetFullPath(runtime.Host!)!=root||runtime.Username!="preview_runtime"||runtime.Database!="postgres"||runtime.CommandTimeout!=5||Path.GetDirectoryName(Path.GetFullPath(fixturePath))!=root)
            throw new Exception("Billing work checks require an owned preview and its ordinary five-second read deadline.");
        using var fixture=JsonDocument.Parse(File.ReadAllText(fixturePath));
        var input=fixture.RootElement;
        var frame=input.GetProperty("frame").Deserialize<InputFrame>(Json)!;
        var request=input.GetProperty("request").Deserialize<GasBillingReadRequest>(Json)!;
        var mode=input.GetProperty("planMode").GetString();
        if(mode is not("force_custom_plan" or "force_generic_plan"))throw new Exception("Choose an explicit plan mode.");
        await using var database=new PreviewDatabase(root);
        var captured=input.TryGetProperty("context",out var held)?held.Deserialize<ScheduleContext>(Json):null;
        var result=await database.WithIdentity(input.GetProperty("profile").GetString()!,"ReadTrades",async(connection,ct)=>
        {
            await using(var plan=ScheduleStore.Command(connection,$"SET LOCAL plan_cache_mode={mode}"))await plan.ExecuteNonQueryAsync(ct);
            // Hold one certified view across maintenance and unrelated-row growth.
            var view=captured is null?await TradingView.Capture(connection,new(database.Manifest.CatalogId),frame,ct):
                new TradingView(frame,captured,request.Context is {KnowledgeMode:"current",ContextId:null});
            async Task<Dictionary<string,long>> Tuples()
            {
                await using var count=ScheduleStore.Command(connection,Work);
                return JsonSerializer.Deserialize<Dictionary<string,long>>((string)(await count.ExecuteScalarAsync(ct))!)!;
            }
            var before=await Tuples();var clock=Stopwatch.StartNew();
            var store=new GasCashBillingStore(database);
            var data=await new TradeReads(database).ReadBound(connection,view,
                ()=>store.Read(connection,view.CalculationFrame,request,ct),ct);
            var elapsed=clock.Elapsed.TotalMilliseconds;
            var tables=(await Tuples()).ToDictionary(pair=>pair.Key,pair=>pair.Value-before.GetValueOrDefault(pair.Key)).Where(pair=>pair.Value!=0).ToDictionary();
            return new {view.Context,data,tuples=tables.Values.Sum(),tables,elapsed};
        },CancellationToken.None);
        File.WriteAllText(fixturePath+".result.json",JsonSerializer.Serialize(result,Json));
        if(input.TryGetProperty("expected",out var expected)&&!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(result.data,Json),JsonNode.Parse(expected.GetRawText())))
            throw new Exception("Billing metadata maintenance changed the native pinned result or row order.");
        Console.WriteLine($"PASS managed billing read: {mode}, native pinned result retained, metadata tuples={result.tuples}");
    }
}
