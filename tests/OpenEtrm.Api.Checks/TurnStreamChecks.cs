using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Npgsql;
using OpenEtrm.Api;

// The live event stream's parts that need no database (docs/ai-assistant.md §8): its wire format, which model text
// cannot shape and which writes a pushed piece of text byte for byte as the same event read back; the request it
// serves and the point it resumes from; one stream per unfinished turn; and its delivery, driven here by a manual clock,
// scripted reads and signals: order, gaps and repeats, endings, heartbeats, the session's end, expiry, shutdown, a cut,
// a refused read and the replay recheck's hidden answer.
static class TurnStreamChecks
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    static readonly Guid TurnId=Guid.Parse("00000000-0000-4000-8000-000000000001");

    public static async Task Run()
    {
        Wire();
        Requests();
        Subscriptions();
        await Delivery();
    }

    static void Wire()
    {
        Require(Text(TurnWire.Comment("open"))==": open\n\n","a comment is one line and a blank line");
        var injected="x\r\nevent: navigation\ndata: {}\nid: 999\n\ny\u2028z\u2029\r";
        var frame=Text(TurnWire.Text(7,TurnId,null,injected,Json));
        var lines=frame.Split('\n');
        Require(lines.Length==5&&lines[0]=="id: 7"&&lines[1]=="event: text"&&lines[2].StartsWith("data: {",StringComparison.Ordinal)&&lines[3]==""&&lines[4]==""
            &&!frame.Contains('\r'),"model text is escaped inside one data line, whatever line breaks and fields it holds");
        using(var data=JsonDocument.Parse(lines[2]["data: ".Length..]))
            Require(data.RootElement.GetProperty("payload").GetProperty("text").GetString()==injected&&data.RootElement.GetProperty("turnId").GetGuid()==TurnId
                &&data.RootElement.GetProperty("hidden").ValueKind==JsonValueKind.Null&&!data.RootElement.TryGetProperty("incomplete",out _),
                "the data line names its turn and hidden marker and carries the text as sent");
        // The database prints jsonb with a space after each colon, raw UTF-8 and \u escapes for control characters; a
        // payload read back from it is written exactly as the pushed text.
        foreach(var (text,stored) in new[]{("a\r\nb",@"{""text"": ""a\r\nb""}"),("€ and 😀",@"{""text"": ""€ and 😀""}"),
            ("\u0001</script>\"'&",@"{""text"": ""\u0001</script>\""'&""}"),("line\u2028break",@"{""text"": ""line" +"\u2028"+@"break""}")})
        {
            using var read=JsonDocument.Parse(stored);
            Require(TurnWire.Text(3,TurnId,null,text,Json).SequenceEqual(TurnWire.Event(3,"text",TurnId,null,null,read.RootElement,Json)),
                $"a pushed piece of text writes byte for byte as the same event read back: {stored}");
        }
        using(var payload=JsonDocument.Parse(@"{""text"": ""Read.""}"))
        {
            var hidden=Text(TurnWire.Event(4,"text",TurnId,"access_changed",null,payload.RootElement,Json));
            Require(hidden==Text(TurnWire.Text(4,TurnId,"access_changed","Read.",Json))&&hidden.Contains(@"""payload"":{""hidden"":""access_changed""}",StringComparison.Ordinal)
                &&hidden.Contains(@"""hidden"":""access_changed""",StringComparison.Ordinal)&&!hidden.Contains("Read.",StringComparison.Ordinal),
                "a hidden turn's text is sent as its hidden marker, whether read or pushed");
        }
        using(var ended=JsonDocument.Parse(@"{""code"": ""limit_elapsed""}"))
        {
            var terminal=Text(TurnWire.Event(9,"failed",TurnId,null,true,ended.RootElement,Json));
            Require(terminal.StartsWith("id: 9\nevent: failed\ndata: ",StringComparison.Ordinal)&&terminal.Contains(@"""incomplete"":true",StringComparison.Ordinal)
                &&terminal.Contains(@"""payload"":{""code"":""limit_elapsed""}",StringComparison.Ordinal),"a terminal event says whether its turn is incomplete");
        }
        var refused=Text(TurnWire.Refused("replay_unavailable",Json));
        Require(refused.StartsWith("event: refused\ndata: {\"code\":\"replay_unavailable\",\"message\":",StringComparison.Ordinal)&&refused.EndsWith("}\n\n",StringComparison.Ordinal)
            &&!refused.Contains("id:",StringComparison.Ordinal)&&Text(TurnWire.Replaced)=="event: replaced\ndata: {}\n\n","refused and replaced carry no id");
        using(var unknown=JsonDocument.Parse("{}"))
            Require(Throws<InvalidOperationException>(()=>TurnWire.Event(1,"navigation",TurnId,null,null,unknown.RootElement,Json)),"only the fixed event names are written");
        Console.WriteLine("PASS the stream's wire format: one data line of escaped JSON per event, fixed names, no id on refused or replaced, and "+
            "a pushed piece of text byte for byte the event read back, hidden as the replay recheck hides it");
    }

    static void Requests()
    {
        var id=TurnId.ToString();
        foreach(var (method,path,matches) in new[]{("GET",$"/api/assistant/conversations/{id}/events",true),("GET",$"/api/assistant/conversations/{id}/events/",true),
            ("GET",$"/api/Assistant/Conversations/{TurnId:N}/Events",true),("POST",$"/api/assistant/conversations/{id}/events",false),
            ("HEAD",$"/api/assistant/conversations/{id}/events",false),("GET",$"/api/assistant/conversations/{id}",false),
            ("GET","/api/assistant/conversations/not-a-turn/events",false),("GET",$"/api/assistant/conversations/{id}/events/more",false),
            ("GET",$"/api/assistant/turns/{id}/events",false),("GET",$"/api/settings/assistant/conversations/{id}/events",false)})
            Require(TurnStream.Matches(method,new PathString(path))==matches,$"{method} {path} is {(matches?"":"not ")}a stream");
        Require(TurnStream.Resume(4,StringValues.Empty)==4&&TurnStream.Resume(4,"9")==9&&TurnStream.Resume(4,"0")==0,"Last-Event-ID overrides after");
        foreach(var bad in new[]{"","05","-1","+1"," 1","1 ","x","1.0","99999999999999999999"})
            Require(Throws<BadHttpRequestException>(()=>TurnStream.Resume(0,bad)),$"Last-Event-ID {bad} is refused");
        Require(Throws<BadHttpRequestException>(()=>TurnStream.Resume(0,new StringValues(["1","2"]))),"two Last-Event-ID values are refused");
        Console.WriteLine("PASS a stream is a GET of a conversation's events, exempt from the request deadline, and resumes after the Last-Event-ID a "+
            "reconnect sends, which overrides after, or refuses one that is not a sequence");
    }

    static void Subscriptions()
    {
        var streams=new TurnStreams();
        var other=Guid.NewGuid();
        var (a,b,c)=(streams.Subscribe(TurnId,"session-a","risk"),streams.Subscribe(TurnId,"session-b","risk"),streams.Subscribe(other,"session-a","risk"));
        var reopened=streams.Subscribe(other,"session-a","trader");
        streams.Published(TurnId,1,"x");
        Require(Drained(a)==1&&Drained(b)==1&&Drained(c)==0,"a turn's signals reach only its subscriptions");
        streams.Activate(a);
        streams.Activate(b);
        Require(a.Stopped==StreamStop.Replaced&&b.Stopped==StreamStop.None&&c.Stopped==StreamStop.None,"a newer stream of a turn replaces the older, alone");
        streams.EndSession("session-a","risk");
        Require(c.Stopped==StreamStop.SessionEnded&&b.Stopped==StreamStop.None&&reopened.Stopped==StreamStop.None,
            "a profile switch ends the streams its session opened under the profile it left, alone");
        streams.Remove(b);
        streams.Published(TurnId,2,"y");
        Require(Drained(b)==0,"a removed subscription hears nothing more");
        var slow=streams.Subscribe(other,"session-c","risk");
        for(var n=1;n<=RuntimeSet.SubscriberEvents+1;n++)streams.Published(other,n,"z");
        Require(slow.Stopped==StreamStop.Cut,"a subscription whose buffer is full is cut, and the writer never waits");
        var replaced=streams.Subscribe(other,"session-c","risk");
        streams.Activate(replaced);
        streams.Published(other,1,"z");
        streams.Activate(streams.Subscribe(other,"session-d","risk"));
        Require(replaced.Stopped==StreamStop.Replaced,"a stop is kept beside the buffered signals, which it preempts");
        var third=Guid.NewGuid();
        var live=streams.Subscribe(third,"session-e","risk");
        streams.Activate(live);
        var doomed=streams.Subscribe(third,"session-f","risk");
        streams.EndSession("session-f","risk");
        streams.Activate(doomed);
        Require(live.Stopped==StreamStop.None&&doomed.Stopped==StreamStop.SessionEnded,"a stream stopped while it is admitted replaces none");
        streams.Remove(doomed);
        streams.Activate(streams.Subscribe(third,"session-f","trader"));
        Require(live.Stopped==StreamStop.Replaced,"its turn's stream stays the one it was");
        Console.WriteLine("PASS one stream per unfinished turn: a newer one replaces the older unless stopped first, a profile switch ends those "+
            "its session opened under the profile it left, and a full buffer cuts its subscription without the writer waiting");
    }

    static async Task Delivery()
    {
        var at=new DateTimeOffset(2026,1,5,12,0,0,TimeSpan.Zero);
        var deadline=at.AddMinutes(2);
        StreamEvent E(long sequence,string kind,string payload)=>new(sequence,kind,JsonDocument.Parse(payload).RootElement);
        StreamRead R(string state,params StreamEvent[] events)=>new(null,new StreamTurn(TurnId,state,state!="queued",state is "failed" or "cancelled" or "interrupted",null),deadline,[..events]);
        var opening=R("running",E(1,"accepted","{}"),E(2,"text",@"{""text"":""a""}"));
        var ending=R("completed",E(4,"usage",@"{""calls"":1}"),E(5,"completed","{}"));

        // In order, without repeats; a gap or a step is read; the worker's end is read through the terminal event.
        {
            var run=Start(opening,[ending]);
            run.Streams.Published(TurnId,3,"b");
            run.Streams.Published(TurnId,3,"b");
            run.Streams.Published(TurnId,2,"a");
            run.Streams.Ended(TurnId);
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text text usage completed"&&Ids(frames)=="1 2 3 4 5"&&run.Reads.SequenceEqual([3L])
                &&frames[3].Contains(@"""payload"":{""text"":""b""}",StringComparison.Ordinal),"pushed text is sent in order once, and the run's end is read through its terminal event");
        }
        {
            var run=Start(opening,[R("running",E(3,"step",@"{""ordinal"":1,""tool"":""read_trades""}"),E(4,"text",@"{""text"":""c""}")),
                R("completed",E(5,"usage",@"{""calls"":1}"),E(6,"completed","{}"))]);
            run.Streams.Published(TurnId,4,"c");
            run.Streams.Published(TurnId,4,"c");
            run.Streams.Ended(TurnId);
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text step text usage completed"&&Ids(frames)=="1 2 3 4 5 6"&&run.Reads.SequenceEqual([2L,4L]),
                "a sequence past the next is read from the last one sent, steps included, and what was read is not repeated");
        }
        {
            var run=Start(opening,[R("running",E(3,"step",@"{""ordinal"":1,""tool"":""read_trades""}")),ending with{Events=[E(4,"usage","{}"),E(5,"completed","{}")]}]);
            run.Streams.Published(TurnId,3,null);
            run.Streams.Ended(TurnId);
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text step usage completed"&&run.Reads.SequenceEqual([2L,3L]),"a published event without text is read");
        }
        {
            var run=Start(opening,[R("running")]);
            run.Streams.Ended(TurnId);
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text refused"&&frames[^1].Contains("agent_runtime_unavailable",StringComparison.Ordinal),
                "a run that ended without ending its turn, as when its generation was lost, refuses the stream agent_runtime_unavailable");
        }
        {
            var run=Start(R("running"),[ending],after:2);
            run.Streams.Published(TurnId,3,"b");
            run.Streams.Ended(TurnId);
            var frames=await run.Finished();
            Require(Kinds(frames)==":open text usage completed"&&Ids(frames)=="3 4 5"&&run.Reads.SequenceEqual([3L]),
                "a stream resumed after a sequence that nothing yet follows sends what comes next, reading from the last event it sent");
        }

        // Heartbeats while idle, a re-read before the turn has started, the session's end, its profile and expiry.
        {
            var run=Start(opening,[]);
            await run.Time.Waiting();
            run.Time.Advance(RuntimeSet.StreamHeartbeat-TimeSpan.FromSeconds(1));
            var early=run.Sink.All().Length;
            run.Time.Advance(TimeSpan.FromSeconds(1));
            var ping=await run.Next(4);
            run.Script.Enqueue(ending);
            run.Streams.Ended(TurnId);
            var frames=await run.Finished();
            Require(early==3&&ping==": ping\n\n"&&Kinds(frames)==":open accepted text :ping usage completed"&&run.Reads.SequenceEqual([2L]),
                "an idle stream writes a ping comment once the heartbeat interval has passed since its last write, and only then");
        }
        {
            var queued=R("queued",E(1,"accepted","{}"));
            var run=Start(queued,[R("queued"),R("cancelled",E(2,"usage","{}"),E(3,"cancelled","{}"))]);
            await run.Time.Waiting();
            run.Time.Advance(RuntimeSet.StreamHeartbeat);
            await run.Next(3);
            await run.Time.Waiting();
            run.Time.Advance(RuntimeSet.StreamHeartbeat);
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted :ping usage cancelled"&&run.Reads.SequenceEqual([1L,1L])&&frames[^1].Contains(@"""incomplete"":true",StringComparison.Ordinal),
                "while its turn has not started a stream reads it again at each heartbeat, so an ending its worker never ran still arrives");
        }
        {
            var run=Start(opening,[],sessionEnds:at.AddSeconds(10));
            await run.Time.Waiting();
            run.Time.Advance(TimeSpan.FromSeconds(10));
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text refused"&&frames[^1].Contains("session_ended",StringComparison.Ordinal),"the session's end refuses the stream session_ended");
        }
        {
            var run=Start(opening,[]);
            await run.Next(3);
            run.Current=false;
            run.Streams.Published(TurnId,3,"secret");
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text refused"&&frames[^1].Contains("session_ended",StringComparison.Ordinal)&&!string.Concat(frames).Contains("secret",StringComparison.Ordinal),
                "a write once the session's profile changed or the session ended is refused session_ended instead");
        }
        {
            var run=Start(opening,[]);
            run.Streams.EndSession("session","risk");
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text refused"&&frames[^1].Contains("session_ended",StringComparison.Ordinal),"a profile switch refuses the session's streams session_ended");
        }
        {
            var run=Start(opening with{Deadline=at.AddSeconds(5)},[]);
            await run.Time.Waiting();
            run.Time.Advance(TimeSpan.FromSeconds(5+RuntimeSet.AdmissionSeconds-1));
            await run.Time.Waiting();
            run.Time.Advance(TimeSpan.FromSeconds(1));
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text :ping refused"&&frames[^1].Contains("stream_expired",StringComparison.Ordinal),
                "a stream whose turn passes its elapsed limit and the admission deadline without ending is refused stream_expired");
        }

        // A newer stream, a cut, shutdown, a refused read, a failed read.
        {
            var run=Start(opening,[]);
            run.Streams.Activate(run.Streams.Subscribe(TurnId,"another","risk"));
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text replaced","a stream replaced by a newer one says so and ends");
        }
        {
            var run=Start(opening,[R("running",E(3,"text",@"{""text"":""c""}"),E(4,"text",@"{""text"":""d""}"))],read:false);
            run.Streams.Published(TurnId,4,"d");
            Require(await run.Entered.WaitAsync(TimeSpan.FromSeconds(10)),"the delivery reads the gap");
            for(var n=5;n<=RuntimeSet.SubscriberEvents+5;n++)run.Streams.Published(TurnId,n,"z");
            run.Release();
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text text text"&&Ids(frames)=="1 2 3 4","a subscriber that overflows its buffer is cut without a final event");
        }
        {
            var run=Start(opening,[]);
            await run.Time.Waiting();
            run.Stop();
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text refused"&&frames[^1].Contains("agent_runtime_unavailable",StringComparison.Ordinal),"shutdown refuses the stream agent_runtime_unavailable");
        }
        {
            var run=Start(opening,[],failure:new PreviewDeniedException());
            run.Streams.Ended(TurnId);
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text refused"&&frames[^1].Contains("access_denied",StringComparison.Ordinal),"a read refused for want of UseAgents refuses the stream access_denied");
        }
        {
            var run=Start(opening,[],failure:new NpgsqlException("unavailable"));
            run.Streams.Ended(TurnId);
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text","a read that fails otherwise cuts the stream, which resumes through Last-Event-ID");
        }

        // The replay recheck's answer: once a read hides the answer, every later text is hidden, and the terminal event says so.
        {
            var hidden=opening with{Turn=opening.Turn! with{Hidden="access_changed"},Events=[E(1,"accepted","{}"),E(2,"text",@"{""hidden"":""access_changed""}")]};
            var run=Start(hidden,[ending]);
            run.Streams.Published(TurnId,3,"secret");
            run.Streams.Ended(TurnId);
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text text usage completed"&&!string.Concat(frames).Contains("secret",StringComparison.Ordinal)
                &&frames.Skip(1).All(f=>f.Contains(@"""hidden"":""access_changed""",StringComparison.Ordinal))&&frames[^1].Contains(@"""incomplete"":false",StringComparison.Ordinal),
                "once a read hides the turn's answer, later text is sent hidden though a read no longer hides it, and every event says so");
        }
        {
            var run=Start(opening,[R("running",E(3,"step","{}")) with{Turn=opening.Turn! with{Hidden="access_changed"}},
                R("completed",E(5,"usage","{}"),E(6,"completed","{}"))]);
            run.Streams.Published(TurnId,3,null);
            run.Streams.Published(TurnId,4,"secret");
            run.Streams.Ended(TurnId);
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted text step text usage completed"&&!string.Concat(frames).Contains("secret",StringComparison.Ordinal),
                "text pushed after a read found the answer hidden is hidden too");
        }

        // A stream of a finished turn replays and ends; a refused read is answered on the stream.
        {
            var run=Start(R("completed",E(1,"accepted","{}"),E(2,"usage","{}"),E(3,"completed","{}")),[],live:false);
            var frames=await run.Finished();
            Require(Kinds(frames)==":open accepted usage completed","a stream of a finished turn replays through its terminal event and ends");
            run=Start(new StreamRead("turn_not_running",null,default,[]),[],live:false);
            frames=await run.Finished();
            Require(Kinds(frames)==":open refused"&&frames[^1].Contains("turn_not_running",StringComparison.Ordinal),"a refused read is sent as refused after the opening comment");
        }
        Console.WriteLine("PASS the stream's delivery: from where it resumes, pushed text in order once, gaps, steps and the run's end read, heartbeats while "+
            "idle and a re-read before the turn starts, the session's end and profile, expiry, a newer stream, a cut, shutdown, refused and failed reads, "+
            "and the hidden answer");
    }

    // One delivery under a manual clock, with scripted reads, until it ends.
    sealed class DeliveryRun
    {
        public required TurnStreams Streams {get;init;}
        public required ManualTime Time {get;init;}
        public required Queue<StreamRead> Script {get;init;}
        public required FrameSink Sink {get;init;}
        public Task Task {get;set;}=Task.CompletedTask;
        public required CancellationTokenSource Stopping {get;init;}
        public required SemaphoreSlim Reading {get;init;}
        public required SemaphoreSlim Entered {get;init;}
        public List<long> Reads {get;}=[];
        public volatile bool Current=true;
        public void Stop()=>Stopping.Cancel();
        public void Release()=>Reading.Release(1000);
        public async Task<string> Next(int count)
        {
            string frame="";
            for(var n=0;n<count;n++)frame=await Sink.Next();
            return frame;
        }
        public async Task<string[]> Finished()
        {
            await Task.WaitAsync(TimeSpan.FromSeconds(10));
            return Sink.All();
        }
    }

    static DeliveryRun Start(StreamRead opened,StreamRead[] script,DateTimeOffset? sessionEnds=null,bool live=true,bool read=true,Exception? failure=null,long after=0)
    {
        var time=new ManualTime(new DateTimeOffset(2026,1,5,12,0,0,TimeSpan.Zero));
        var streams=new TurnStreams();
        var sink=new FrameSink();
        var subscription=live?streams.Subscribe(TurnId,"session","risk"):null;
        if(subscription is not null)streams.Activate(subscription);
        var (reading,entered)=(new SemaphoreSlim(read?1000:0),new SemaphoreSlim(0));
        DeliveryRun? run=null;
        var queue=new Queue<StreamRead>(script);
        async Task<StreamRead> Read(long after,CancellationToken ct)
        {
            entered.Release();
            await reading.WaitAsync(ct);
            run!.Reads.Add(after);
            if(failure is not null)throw failure;
            return queue.Dequeue();
        }
        var stopping=new CancellationTokenSource();
        run=new DeliveryRun{Streams=streams,Time=time,Script=queue,Sink=sink,Stopping=stopping,Reading=reading,Entered=entered};
        var delivery=new TurnDelivery(sink,Read,()=>run.Current,time,Json,NullLogger.Instance);
        run.Task=Task.Run(()=>delivery.Run(opened,after,subscription,sessionEnds??time.GetUtcNow().AddMinutes(30),CancellationToken.None,stopping.Token));
        return run;
    }

    static int Drained(TurnSubscription subscription)
    {
        var count=0;
        while(subscription.Signals.TryRead(out _))count++;
        return count;
    }

    // Each frame's event name, or :comment, in order.
    static string Kinds(string[] frames)=>string.Join(' ',frames.Select(f=>f.StartsWith(':')?":"+f[1..].Trim():
        f.Split('\n').Single(l=>l.StartsWith("event: ",StringComparison.Ordinal))["event: ".Length..]));
    static string Ids(string[] frames)=>string.Join(' ',frames.SelectMany(f=>f.Split('\n')).Where(l=>l.StartsWith("id: ",StringComparison.Ordinal)).Select(l=>l[4..]));
    static string Text(byte[] bytes)=>Encoding.UTF8.GetString(bytes);

    static bool Throws<T>(Action action) where T:Exception
    {
        try{action();}
        catch(T){return true;}
        return false;
    }

    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}

    // The bytes a delivery writes, split into its frames, each ending with a blank line.
    sealed class FrameSink:Stream
    {
        readonly BlockingCollection<string> frames=[];
        readonly List<string> written=[];
        readonly StringBuilder pending=new();
        public override bool CanRead=>false;
        public override bool CanSeek=>false;
        public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();
        public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}
        public override Task FlushAsync(CancellationToken ct)=>Task.CompletedTask;
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)
        {
            lock(pending)
            {
                pending.Append(Encoding.UTF8.GetString(buffer,offset,count));
                int end;
                while((end=pending.ToString().IndexOf("\n\n",StringComparison.Ordinal))>=0)
                {
                    var frame=pending.ToString(0,end+2);
                    pending.Remove(0,end+2);
                    written.Add(frame);
                    frames.Add(frame);
                }
            }
        }
        public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken ct){Write(buffer,offset,count);return Task.CompletedTask;}
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default){Write(buffer.ToArray(),0,buffer.Length);return ValueTask.CompletedTask;}
        public async Task<string> Next()=>await Task.Run(()=>frames.TryTake(out var frame,TimeSpan.FromSeconds(10))?frame:throw new TimeoutException("No frame was written."));
        public string[] All(){lock(pending)return [..written];}
    }

    // A clock the checks move by hand; each timer fires once the clock passes its due time.
    sealed class ManualTime(DateTimeOffset start):TimeProvider
    {
        readonly object gate=new();
        readonly List<ManualTimer> timers=[];
        readonly SemaphoreSlim created=new(0);
        DateTimeOffset now=start;
        public override DateTimeOffset GetUtcNow(){lock(gate)return now;}
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        {
            var timer=new ManualTimer(this,callback,state);
            timer.Change(dueTime,period);
            created.Release();
            return timer;
        }
        // Returns once a delivery waits on a new timer.
        public Task Waiting()=>created.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(t=>{if(!t.Result)throw new TimeoutException("The delivery did not wait.");});
        public void Advance(TimeSpan by)
        {
            ManualTimer[] due;
            lock(gate)
            {
                now+=by;
                due=[..timers.Where(t=>t.Due<=now)];
                foreach(var t in due)timers.Remove(t);
            }
            foreach(var t in due)t.Fire();
        }
        internal void Schedule(ManualTimer timer,DateTimeOffset? due)
        {
            lock(gate)
            {
                timers.Remove(timer);
                if(due is {} at){timer.Due=at;timers.Add(timer);}
            }
        }
        internal DateTimeOffset Now(){lock(gate)return now;}
    }

    sealed class ManualTimer(ManualTime time,TimerCallback callback,object? state):ITimer
    {
        public DateTimeOffset Due;
        public bool Change(TimeSpan dueTime,TimeSpan period)
        {
            time.Schedule(this,dueTime==Timeout.InfiniteTimeSpan?null:time.Now()+dueTime);
            return true;
        }
        public void Fire()=>callback(state);
        public void Dispose()=>time.Schedule(this,null);
        public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
    }
}
