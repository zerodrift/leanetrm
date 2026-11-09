using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;

internal static class CurveAuthoringMarketChecks
{
    internal static void Run()
    {
        var primary=Guid.NewGuid();var other=Guid.NewGuid();var at=DateTimeOffset.Parse("2032-10-04T20:00:00Z");
        var oldPin=Guid.NewGuid();var newPin=Guid.NewGuid();var actual=Guid.NewGuid();var context=Guid.NewGuid();
        var frame=new InputFrame(Guid.NewGuid(),"Original close",at,[oldPin],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty)
            {ContextId=context,OriginCloseId=Guid.NewGuid()};
        var manifest=new PreviewManifest("local-synthetic-preview",Guid.NewGuid(),primary,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),[],null,
            FuturesConvention:"contract_lot_execution_pnl_v2",CurveMarkets:[new("Synthetic independent source",other,[new(at,[newPin],new(){["daily"]=[Guid.NewGuid()]},[actual])])]);
        using var runtime=NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        var database=new PreviewDatabase(manifest,runtime);
        Require(CurveAuthoringMarket.Revision(database,null)==primary,"An omitted selector retains the original primary market.");
        Require(ReferenceEquals(CurveAuthoringMarket.Frame(database,frame,primary),frame),"Primary selections keep the original frame.");
        var selected=CurveAuthoringMarket.Frame(database,frame,other);
        Require(selected.Id==frame.Id&&selected.At==at&&selected.ContextId==context,"Source selection preserves the exact dated knowledge view.");
        Require(selected.ObservationIds.SequenceEqual([newPin])&&selected.FrozenObservationIds.SequenceEqual([actual])&&selected.OriginCloseId is null,
            "Only declared source inputs enter a supplemental frame; the original close is not claimed to certify them.");
        Require(manifest.RevisionId==primary&&frame.ObservationIds.SequenceEqual([oldPin]),"The primary holding context remains unchanged.");
        Reject(()=>CurveAuthoringMarket.Revision(database,Guid.NewGuid()));
        Reject(()=>CurveAuthoringMarket.Revision(database,Guid.Empty));
        Reject(()=>CurveAuthoringMarket.Frame(database,frame with{At=at.AddDays(1)},other));
        var source=Saved(manifest.CatalogId,Guid.NewGuid(),other,null);
        var child=Saved(manifest.CatalogId,Guid.NewGuid(),null,source.Id);
        Require(CurveAuthoringMarket.SavedRevision(child,[source,child])==other,"A saved published dependency keeps its exact source revision.");
        var cycleA=Saved(manifest.CatalogId,Guid.NewGuid(),null,null);
        var cycleB=Saved(manifest.CatalogId,Guid.NewGuid(),null,cycleA.Id);
        cycleA=Saved(manifest.CatalogId,cycleA.Id,null,cycleB.Id);
        Reject(()=>CurveAuthoringMarket.SavedRevision(cycleA,[cycleA,cycleB]));
        Console.WriteLine("PASS curve authoring scope: explicit sources, exact frame/context, unchanged primary, saved ancestry and fail-closed ambiguity");
    }
    static CustomCurveVersion Saved(Guid catalog,Guid id,Guid? revision,Guid? parent)=>new(id,1,1,"published",
        new("synthetic_source","Synthetic source",parent is {} ancestor?[new("parent","published",ImmutableDictionary<string,string>.Empty,ImmutableDictionary<string,string>.Empty,
            PublishedSource:new(catalog,ancestor,1,"output"))]:[],"output"),ImmutableDictionary<string,CurveNodePosition>.Empty,1,null,
        revision is {} rev?ImmutableDictionary<string,CurveSourceReference>.Empty.Add("source",new(catalog,rev,"daily")):ImmutableDictionary<string,CurveSourceReference>.Empty,
        ImmutableDictionary<string,CalendarReference>.Empty,ImmutableDictionary<string,CurveScheduleReference>.Empty,new("person"));
    static void Reject(Action action){try{action();}catch(MarketDataException e)when(e.Code=="curve_market"){return;}throw new Exception("An incomplete or cyclic source scope was accepted.");}
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
}
