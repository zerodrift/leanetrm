using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Only immutable request selection is retained after the UI session gate is
// released. Database reads and result publication still authorize the actor.
internal sealed record TradingReadSelection(string Profile,InputFrame Frame,bool Working,
    ScheduleContext? SelectedContext,bool Live,DateTimeOffset Ends)
{
    internal string ReadOnlyLabel=>Live?"Live view":"Historical view";
    internal static bool IsWorking(PreviewSession session,FuturesReadRequest request)=>
        session.MarketContext is null&&request is {KnowledgeMode:"current",ContextId:null};
    internal static TradingReadSelection Capture(PreviewSession session,FuturesReadRequest request,InputFrame frame)=>
        new(session.Profile??throw new PreviewDeniedException(),frame,IsWorking(session,request),session.MarketContext,
            session.Live is not null,session.Ends);
    internal void RequireSession(PreviewSession session)
    {
        if(DateTimeOffset.UtcNow>=Ends||session.Profile!=Profile)throw new PreviewDeniedException();
    }
    internal async Task<TradingView> Resolve(NpgsqlConnection connection,KnowledgeStore store,FuturesReadRequest request,CancellationToken ct)
    {
        if(DateTimeOffset.UtcNow>=Ends)throw new PreviewDeniedException();
        if(Working)return await TradingView.Capture(connection,store,Frame,ct);
        if(request is not {KnowledgeMode:"retained",ContextId:{} id})
            throw new MarketDataException("futures_context","Choose current knowledge explicitly or supply the exact retained context.");
        var context=(await store.Read(connection,id,ct)).Context;
        if(SelectedContext is null)return TradingView.Reread(Frame,context);
        if(Live&&Frame.ContextId==context.ContextId||SelectedContext.ContextId==context.ContextId)
            return TradingView.Retained(Frame,context);
        throw new MarketDataException("futures_context","Trading must use the shared selected historical context.");
    }
}
