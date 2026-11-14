using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class LifecycleOptionExpiryChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var f=document.RootElement;
        var manifest=f.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(f.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Option expiry checks require an owned disposable cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var source=(await new KnowledgeStore(manifest.CatalogId).Read(connection,f.Id("contextId"),ct)).Context;
            var at=f.GetProperty("at").GetDateTimeOffset();
            await TradingContext.AdmitSource(database,connection,source,at,ct);
            await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);
            await TradingContext.Bind(connection,manifest.CatalogId,source,at,ct);
            var seed=manifest.Frames.Single(p=>p.At==DateTimeOffset.Parse("2026-10-16T16:00:00Z"));
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic unprocessed historical expiry",at,DateWorkspace.Date(source.EffectiveOn??throw new Exception("The expiry fixture requires its exact source business date.")),[..seed.ObservationIds],
                seed.CurveContracts.ToImmutableDictionary(p=>p.Key,p=>p.Value.ToImmutableArray()),source.ContextId);
            var data=await database.Read(connection,ct,observationIds:frame.ObservationIds,observationScope:ObservationReadScope.ExactPins);
            var selected=ImmutableHashSet.Create(f.Id("tradeId"));
            var positions=await FuturesOptionPositions.Read(connection,manifest,[f.Id("bookId")],[],ct,trades:selected);
            if(positions.Length!=1||positions[0].OpenLots!=ExactDecimal.Parse("1"))throw new Exception("The unprocessed native right was silently closed.");
            var (market,values)=await FuturesOptionPortfolio.Values(connection,database,data,frame,positions,ct);
            var excluded=market.Unvalued(positions);
            if(excluded.Length!=1||excluded[0].TradeId!=f.Id("tradeId")||excluded[0].BindingId!=positions[0].OptionBinding||excluded[0].Reason!="option_expired"||
                values.ContainsKey((f.Id("tradeId"),positions[0].OptionBinding))||values[(f.Id("tradeId"),positions[0].PremiumBinding)].ToExactString()!="-1000")
                throw new Exception("Risk must identify the unprocessed expired right while retaining its exact outstanding premium.");
            var run=await FuturesOptionBlotter.Run(connection,database,data,frame,[f.Id("bookId")],[],ct,
                new(f.Id("tradeId"),positions[0].OptionBinding,1,FuturesOptionPositions.Type));
            var rows=ImmutableArray.CreateBuilder<System.Text.Json.Nodes.JsonObject>();
            await foreach(var unit in run.Units(ct))rows.AddRange(unit);
            var option=rows.Single(p=>p["component"]!.ToString()=="option");var premium=rows.Single(p=>p["component"]!.ToString()=="premium");
            if(option["unavailableReason"]?.ToString()!="option_expired"||option["presentValue"] is not null||option["openLots"]?.ToString()!="1"||
                premium["unavailableReason"] is not null||premium["presentValue"]?.ToString()!="-1000")
                throw new Exception("The ordinary Blotter fabricated expiry processing or discarded premium cash.");
            return 0;
        },default);
        Console.WriteLine("PASS actual unprocessed historical expiry: Blotter retains the open native right with option_expired, risk excludes only that right and preserves its outstanding premium");
    }
}
