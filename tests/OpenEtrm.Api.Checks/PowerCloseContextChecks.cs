using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class PowerCloseContextChecks
{
    internal static async Task Run(string path)
    {
        using var json=JsonDocument.Parse(File.ReadAllText(path));var f=json.RootElement;
        var manifest=f.GetProperty("manifest").Deserialize<PreviewManifest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await using var runtime=NpgsqlDataSource.Create(f.GetProperty("connection").GetString()!);
        await using var database=new PreviewDatabase(manifest,runtime);
        await using var connection=await runtime.OpenConnectionAsync();
        await using var transaction=await connection.BeginTransactionAsync();
        await using(var use=ScheduleStore.Command(connection,"SELECT security.use_context($1)",f.GetProperty("token").GetGuid()))await use.ExecuteNonQueryAsync();
        var store=new PpaStore(database,new TouProductStore(manifest.CatalogId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId));
        var first=await MarketCloses.Frame(connection,manifest.CatalogId,f.GetProperty("first").GetGuid(),null,default);
        var later=await MarketCloses.Frame(connection,manifest.CatalogId,f.GetProperty("later").GetGuid(),null,default);
        var known=f.GetProperty("known").GetString()!;var latest=f.GetProperty("latest").GetString()!;
        await store.RequireContractClose(connection,first,known,default);
        try
        {
            await store.RequireContractClose(connection,later,known,default);
            throw new Exception("An after-cutoff close was admitted merely because its observations were old.");
        }
        catch(PreviewDeniedException){}
        catch(MarketDataException error)when(error.Code is "close_replaced" or "close_not_visible" or "close_missing"){}
        try
        {
            await store.RequireContractClose(connection,first,latest,default);
            throw new Exception("A close replaced within the selected cutoff was admitted.");
        }
        catch(MarketDataException error)when(error.Code=="close_replaced"){}
        await store.RequireContractClose(connection,later,latest,default);
        Console.WriteLine("PASS power close context: historical head retained, after-cutoff old-pin close refused, visible replacement required");
    }
}
