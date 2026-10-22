using OpenEtrm.Api;

public sealed partial class PreviewSession
{
    private readonly Dictionary<Guid,(InputFrame Frame,ScheduleContext Context)> liveFrames=[];
    private readonly Queue<Guid> liveFrameOrder=[];
    private CancellationTokenSource? liveStopping;
    private int liveStream;
    private CancellationToken liveToken=new(true);
    public LiveSelection? Live {get;private set;}
    internal CancellationToken LiveStopping=>liveToken;
    internal bool ClaimLiveStream()=>Interlocked.CompareExchange(ref liveStream,1,0)==0;
    internal void ReleaseLiveStream()=>Interlocked.Exchange(ref liveStream,0);
    internal bool IsLiveFrame(Guid id)=>liveFrames.ContainsKey(id);
    internal PinnedContext? LivePin(Guid id)=>liveFrames.TryGetValue(id,out var retained)?PinnedContext.Retained(retained.Context,retained.Frame):null;
    // The session gate protects selection changes. A bounded window of ephemeral
    // frames lives here; jobs and receipts retain their own admitted inputs.
    public void SelectLive(LiveSelection live)
    {
        if(live.Frame is not {} frame||live.Context is not {} context)throw PinnedContext.Missing();
        if(Live?.Id!=live.Id){StopLive();liveStopping=new();liveToken=liveStopping.Token;}
        ForgetDiscoveredFrames();
        Live=live;MarketContext=context;Frames.Add(frame.Id,frame);
        liveFrames.Add(frame.Id,(frame,context));liveFrameOrder.Enqueue(frame.Id);
        // A small admission window lets an in-flight multi-read screen finish
        // under its exact old frame while newer generations arrive. No values or
        // authorization are cached. Older requests fail explicitly and retry latest.
        while(liveFrameOrder.Count>4){var old=liveFrameOrder.Dequeue();liveFrames.Remove(old);Frames.Remove(old);}
    }
    public void StopLive()
    {
        liveStopping?.Cancel();liveStopping?.Dispose();liveStopping=null;liveToken=new(true);
        foreach(var id in liveFrameOrder)Frames.Remove(id);
        liveFrames.Clear();liveFrameOrder.Clear();
        Live=null;
    }
    public void PauseLive(PinnedContext pin)
    {
        var frame=pin.RequireFrame();var context=pin.Context??throw PinnedContext.Missing();
        if(!IsLiveFrame(frame.Id)||PersistentFrames.Length>=MaximumFrames)throw PinnedContext.Missing();
        StopLive();ForgetDiscoveredFrames();MarketContext=context;
        ApplyFrames(new([frame],FrameCatalog));
    }
}
