using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;

internal static class CurveVersionInputChecks
{
    internal static void Run()
    {
        var primary=Guid.NewGuid();var other=Guid.NewGuid();var at=DateTimeOffset.Parse("2032-10-04T20:00:00Z");
        var oldPin=Guid.NewGuid();var newPin=Guid.NewGuid();var actual=Guid.NewGuid();var context=Guid.NewGuid();
        var frame=new InputFrame(Guid.NewGuid(),"Original close",at,[oldPin],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty)
            {ContextId=context,OriginCloseId=Guid.NewGuid()};
        var manifest=new PreviewManifest("local-synthetic-preview",Guid.NewGuid(),primary,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),[],null,
            FuturesConvention:"contract_lot_execution_pnl_v2",GasMarket:new(other,[new(at,[newPin],new(){["daily"]=[Guid.NewGuid()]},[actual])]));
        using var runtime=NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        var database=new PreviewDatabase(manifest,runtime);
        Require(CurveVersionInputs.Revision(database,null)==primary,"New curves use the active catalogue.");
        Require(ReferenceEquals(CurveVersionInputs.Frame(database,frame,primary),frame),"Primary selections keep the original frame.");
        Reject(()=>CurveVersionInputs.Frame(database,frame,other));
        Reject(()=>CurveVersionInputs.Revision(database,Guid.NewGuid()));
        Reject(()=>CurveVersionInputs.Revision(database,Guid.Empty));
        Require(frame.ObservationIds.SequenceEqual([oldPin])&&frame.OriginCloseId is not null,
            "A rejected version must not replace the selected close's prices or official identity.");
        var source=Saved(manifest.CatalogId,Guid.NewGuid(),other,null);
        var child=Saved(manifest.CatalogId,Guid.NewGuid(),null,source.Id);
        Require(CurveVersionInputs.SavedRevision(child,[source,child])==other,"A saved published dependency keeps its exact source revision.");
        var cycleA=Saved(manifest.CatalogId,Guid.NewGuid(),null,null);
        var cycleB=Saved(manifest.CatalogId,Guid.NewGuid(),null,cycleA.Id);
        cycleA=Saved(manifest.CatalogId,cycleA.Id,null,cycleB.Id);
        Reject(()=>CurveVersionInputs.SavedRevision(cycleA,[cycleA,cycleB]));
        Console.WriteLine("PASS retained curve inputs: exact frame/context, immutable pins, saved ancestry and fail-closed ambiguity");
    }
    static CustomCurveVersion Saved(Guid catalog,Guid id,Guid? revision,Guid? parent)=>new(id,1,1,"published",
        new("synthetic_source","Synthetic source",parent is {} ancestor?[new("parent","published",ImmutableDictionary<string,string>.Empty,ImmutableDictionary<string,string>.Empty,
            PublishedSource:new(catalog,ancestor,1,"output"))]:[],"output"),ImmutableDictionary<string,CurveNodePosition>.Empty,1,null,
        revision is {} rev?ImmutableDictionary<string,CurveSourceReference>.Empty.Add("source",new(catalog,rev,"daily")):ImmutableDictionary<string,CurveSourceReference>.Empty,
        ImmutableDictionary<string,CalendarReference>.Empty,ImmutableDictionary<string,CurveScheduleReference>.Empty,new("person"));
    static void Reject(Action action){try{action();}catch(MarketDataException e)when(e.Code=="curve_inputs"){return;}throw new Exception("An incomplete or cyclic source scope was accepted.");}
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
}
