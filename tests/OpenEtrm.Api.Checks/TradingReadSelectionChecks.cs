using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class TradingReadSelectionChecks
{
    internal static void Run()
    {
        var at=new DateTimeOffset(2031,11,1,0,0,0,TimeSpan.Zero);
        var manifest=new PreviewManifest("local-synthetic-preview",Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
            Guid.NewGuid(),Guid.NewGuid(),[],new(at,"synthetic_power"),FuturesConvention:"contract_lot_execution_pnl_v2");
        var session=new PreviewSession(manifest){Profile="trader"};
        var frame=new InputFrame(Guid.NewGuid(),"Synthetic read",at,[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty);
        var request=new FuturesReadRequest(frame.Id,TradingBlotter.Convention,"current");
        var selected=TradingReadSelection.Capture(session,request,frame);
        selected.RequireSession(session);
        if(!selected.Working||selected.SelectedContext is not null||!ReferenceEquals(selected.Frame,frame))
            throw new Exception("A concurrent read must capture its exact working selection before releasing the UI gate.");
        var historical=new ScheduleContext("2031-11-01",1,"2031-11-02T00:00:00Z",Guid.NewGuid());
        session.MarketContext=historical;
        if(selected.SelectedContext is not null||!selected.Working)
            throw new Exception("A later UI context selection cannot reinterpret an admitted read.");
        var retained=TradingReadSelection.Capture(session,request with {KnowledgeMode="retained",ContextId=historical.ContextId},
            frame with {ContextId=historical.ContextId});
        if(retained.Working||retained.SelectedContext!=historical)
            throw new Exception("Historical reads must preserve their selected exact context and read-only state.");
        session.Profile="viewer";Denied(()=>selected.RequireSession(session));
        session.Profile="trader";Denied(()=>(selected with {Ends=DateTimeOffset.UtcNow.AddSeconds(-1)}).RequireSession(session));
        Console.WriteLine("PASS concurrent trading read: immutable selection, historical mode, changed actor and ended UI session");
    }
    private static void Denied(Action action)
    {
        try {action();}
        catch(PreviewDeniedException){return;}
        throw new Exception("A completed background read cannot outlive its UI session or selected identity.");
    }
}
