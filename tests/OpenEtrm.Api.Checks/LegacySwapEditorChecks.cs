using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;

internal static class LegacySwapEditorChecks
{
    public static async Task Run()
    {
        using var input=JsonDocument.Parse(await Console.In.ReadToEndAsync());
        var request=input.RootElement;
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest=request.GetProperty("manifest").Deserialize<PreviewManifest>(json)!;
        await using var source=NpgsqlDataSource.Create(request.GetProperty("connection").GetString()!);
        await using var database=new PreviewDatabase(manifest,source);
        await using var connection=await source.OpenConnectionAsync();
        await using var transaction=await connection.BeginTransactionAsync();
        await using(var auth=ScheduleStore.Command(connection,"SELECT security.use_context($1)",request.GetProperty("token").GetGuid()))
            await auth.ExecuteNonQueryAsync();
        var context=(await new KnowledgeStore(manifest.CatalogId).Capture(connection,new("2020-12-18",null,"current"),default)).Context;
        await MarketContextStore.Bind(connection,manifest.CatalogId,context,default);
        var seed=manifest.Frames.Single(frame=>frame.At==DateTimeOffset.Parse("2020-12-18T16:00:00Z"));
        var frame=new InputFrame(Guid.NewGuid(),"Synthetic legacy editor",seed.At,seed.ObservationIds.ToImmutableArray(),seed.CurveContracts.ToImmutableDictionary(p=>p.Key,p=>p.Value.ToImmutableArray()),context.ContextId);
        await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",manifest.CatalogId,frame.At))await bind.ExecuteNonQueryAsync();
        var data=await database.Read(connection,default,observationIds:frame.ObservationIds);
        var store=new SwapStore(database);
        var entry=JsonSerializer.SerializeToElement(await store.Catalog(connection,data,frame,default),json);
        if(entry.GetProperty("indices").GetArrayLength()!=0)throw new Exception("Unqualified legacy market became available for new entry.");
        var detail=JsonSerializer.SerializeToElement(await store.Editor(connection,data,frame,request.GetProperty("tradeId").GetGuid(),null,default),json);
        var terms=detail.GetProperty("state").GetProperty("terms");
        var indices=detail.GetProperty("catalog").GetProperty("indices").EnumerateArray().ToArray();
        if(indices.Length!=1||indices[0].GetProperty("definitionRevisionId").GetGuid()!=terms.GetProperty("definitionRevisionId").GetGuid()||
           indices[0].GetProperty("curve").GetString()!=terms.GetProperty("floating").GetProperty("curve").GetString()||
           indices[0].GetProperty("index").GetString()!=terms.GetProperty("floating").GetProperty("index").GetString()||indices[0].GetProperty("samplings").GetArrayLength()!=0)
            throw new Exception("The editor lost its exact legacy index or invented new pricing eligibility.");
        if(detail.GetProperty("valuation").GetProperty("presentValue").GetString()!="10"||detail.GetProperty("history").GetArrayLength()!=1)
            throw new Exception("Legacy valuation/history changed while restoring editor metadata.");
        Console.WriteLine("PASS migrated legacy swap editor: exact retained index, unchanged PV/history, no new-entry eligibility");
    }
}
