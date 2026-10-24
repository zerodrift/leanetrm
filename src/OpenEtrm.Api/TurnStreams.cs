using System.Threading.Channels;

namespace OpenEtrm.Api;

// The live event stream's subscriptions in this process (docs/ai-assistant.md §8). The worker that runs a turn tells
// them of each event it published once that has committed, a piece of text with its content and any other event by its
// sequence alone, which a stream then reads, and of the end of its run of the turn; a profile switch ends the streams
// its session opened under the profile it left. Nothing here waits for a stream or fails a writer: a subscription whose
// buffer is full is cut, and the stored events, which a stream reads again, are the truth.
public sealed class TurnStreams
{
    readonly object gate=new();
    readonly Dictionary<Guid,List<TurnSubscription>> subscribers=[];
    // The one stream an unfinished turn has (§8).
    readonly Dictionary<Guid,TurnSubscription> streaming=[];

    // A subscription that hears its turn from now on without yet replacing the turn's stream.
    public TurnSubscription Subscribe(Guid turn,string session,string profile)
    {
        var subscription=new TurnSubscription(turn,session,profile);
        lock(gate)
        {
            if(!subscribers.TryGetValue(turn,out var heard))subscribers[turn]=heard=[];
            heard.Add(subscription);
        }
        return subscription;
    }

    // Makes the subscription its turn's stream, telling the one it replaces; one already stopped replaces none.
    public void Activate(TurnSubscription subscription)
    {
        TurnSubscription? older;
        lock(gate)
        {
            if(subscription.Stopped!=StreamStop.None)return;
            streaming.TryGetValue(subscription.Turn,out older);
            streaming[subscription.Turn]=subscription;
        }
        if(older is not null&&older!=subscription)older.Stop(StreamStop.Replaced);
    }

    public void Remove(TurnSubscription subscription)
    {
        lock(gate)
        {
            if(subscribers.TryGetValue(subscription.Turn,out var heard)&&heard.Remove(subscription)&&heard.Count==0)subscribers.Remove(subscription.Turn);
            if(streaming.TryGetValue(subscription.Turn,out var current)&&current==subscription)streaming.Remove(subscription.Turn);
        }
    }

    // An event the worker published once it has committed: a piece of text with its content, any other event without.
    internal void Published(Guid turn,long sequence,string? text)=>Offer(turn,new(sequence,text));

    // The worker's run of the turn has ended, whether or not the turn has.
    internal void Ended(Guid turn)=>Offer(turn,TurnSignal.End);

    // A profile switch, once the session has its new profile, ends every stream the session opened under the one it left
    // (§7.2, §8); when the profile changes, not one opened under the new profile meanwhile. It stops them under the lock
    // Activate takes, so a stream it stops replaces none, and one that subscribes after it finds the new profile.
    public void EndSession(string session,string? left)
    {
        lock(gate)
            foreach(var subscription in subscribers.Values.SelectMany(s=>s).Where(s=>s.Session==session&&s.Profile==left))
                subscription.Stop(StreamStop.SessionEnded);
    }

    void Offer(Guid turn,TurnSignal signal)
    {
        TurnSubscription[] heard;
        lock(gate)heard=subscribers.TryGetValue(turn,out var listening)?[..listening]:[];
        foreach(var subscription in heard)subscription.Offer(signal);
    }
}

// What stops a stream before its turn ends it: a full buffer, a newer stream of its turn or the end of its session.
public enum StreamStop{None,Cut,Replaced,SessionEnded}

// A published event's sequence, with its text when it is a piece of the answer; or, as End, the end of the worker's run.
internal readonly record struct TurnSignal(long Sequence,string? Text)
{
    public static readonly TurnSignal End=new(0,null);
    public bool Ended=>Sequence==0;
}

// One stream's subscription to its turn: the signals it buffers, at most the runtime set's, and what stopped it. A stop
// is kept beside the buffer, so it is never lost to a full one and is seen before the signals still buffered.
public sealed class TurnSubscription
{
    // Its waiters resume asynchronously, so a stop under the subscriptions' lock runs none of them there.
    readonly Channel<TurnSignal> signals=Channel.CreateBounded<TurnSignal>(new BoundedChannelOptions(RuntimeSet.SubscriberEvents)
        {SingleReader=true,FullMode=BoundedChannelFullMode.Wait,AllowSynchronousContinuations=false});
    int stopped;

    internal TurnSubscription(Guid turn,string session,string profile)=>(Turn,Session,Profile)=(turn,session,profile);

    public Guid Turn {get;}
    public string Session {get;}
    // The profile the stream was opened under.
    public string Profile {get;}
    public StreamStop Stopped=>(StreamStop)Volatile.Read(ref stopped);
    internal ChannelReader<TurnSignal> Signals=>signals.Reader;

    internal void Offer(TurnSignal signal)
    {
        if(!signals.Writer.TryWrite(signal))Stop(StreamStop.Cut);
    }

    // The first stop stands.
    internal void Stop(StreamStop reason)
    {
        if(Interlocked.CompareExchange(ref stopped,(int)reason,(int)StreamStop.None)==(int)StreamStop.None)signals.Writer.TryComplete();
    }
}
