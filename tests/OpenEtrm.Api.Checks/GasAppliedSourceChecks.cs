using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class GasAppliedSourceChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Applied-source checks require owned disposable PostgreSQL.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        async Task Read(Guid context,Guid book,bool refuse)
        {
            var operationalRead=false;
            try
            {
                await database.WithIdentity("trader",null,async(connection,ct)=>
                {
                    var source=(await new KnowledgeStore(manifest.CatalogId).Read(connection,context,ct)).Context;
                    await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);
                    var at=fixture.Instant("at");await TradingContext.Bind(connection,manifest.CatalogId,source,at,ct);
                    var frame=new InputFrame(Guid.NewGuid(),"Synthetic applied source correction",at,DateWorkspace.Date(source.EffectiveOn),[fixture.Id("markId")],
                        ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[fixture.Id("contractId")]),context);
                    var data=await MarketInputSelection.Root(database,connection,frame,ct);var snapshot=data.Snapshot(frame).WithKnowledge(context,SchedulePreview.ParseAt(source.KnownAt));
                    var known=await GasOwnedKnownHistoryStore.Read(connection,database,frame,snapshot,[book],ct);
                    operationalRead=true;
                    if(refuse)
                    {
                        if(known.SourceConflicts.IsEmpty)throw new Exception("The operational source hid corrected accepted effects needed for repair.");
                        try
                        {
                            await GasOwnedProjection.Prepare(connection,database,known,null,snapshot,at.AddHours(1),[],ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty,ct);
                            throw new Exception("Economic projection accepted an unresolved accepted source correction.");
                        }
                        catch(MarketDataException error)when(error.Code=="gas_owned_reconciliation"){}
                        var later=known.SourceConflicts[0] with{Time=known.SourceConflicts[0].Time with{EffectiveAt=at.AddHours(2)}};
                        GasOwnedAppliedSources.RequireClear([later],at.AddHours(1));
                        try{GasOwnedAppliedSources.RequireClear([later],at.AddHours(2));throw new Exception("A correction effective at the horizon was omitted.");}
                        catch(MarketDataException error)when(error.Code=="gas_owned_reconciliation"){}
                    }
                    else if(!known.SourceConflicts.IsEmpty)throw new Exception("A later source correction leaked into an older certified view.");
                    _=await GasOwnedHoldings.Read(connection,database,frame,snapshot,[book],ct);return true;
                },default);
                if(refuse)throw new Exception("Complete valuation accepted corrected applied source evidence after its original quantity disappeared.");
            }
            catch(MarketDataException error)when(refuse&&operationalRead&&error.Code=="gas_owned_reconciliation"){}
        }
        var before=fixture.Id("beforeContext");
        await Read(before,fixture.Id("consumptionBook"),false);
        await Read(before,fixture.Id("cashoutBook"),false);
        var missing=new List<string>();
        foreach(var check in new[]{(Context:fixture.Id("afterEvidence"),Book:fixture.Id("consumptionBook"),Name:"fully consumed dispatch evidence"),
            (Context:fixture.Id("afterObservation"),Book:fixture.Id("cashoutBook"),Name:"extinguished cashout observation")})
        {
            try{await Read(check.Context,check.Book,true);}
            catch(Exception error)when(error.Message.StartsWith("Complete valuation accepted",StringComparison.Ordinal)){missing.Add(check.Name);}
        }
        if(missing.Count>0)throw new Exception("Accepted unresolved applied source correction: "+string.Join(", ",missing));
        Console.WriteLine("PASS applied gas source corrections: old certified views retained; fully consumed dispatch and extinguished operator cashout block complete valuation");
    }
}
