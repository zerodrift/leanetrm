using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Npgsql;

namespace OpenEtrm.Api;

// The live event stream (docs/ai-assistant.md §8), in one scope: GET …/assistant/conversations/{id}/events?after=, read
// by native EventSource, serves the conversation's latest turn. Admission refuses before the stream begins: a request
// that is not from the application's own page, a missing or ended session, a malformed resume point, a reader without
// UseAgents or a conversation not theirs, and, while the turn is unfinished, a process without the worker. Every stored
// event it sends comes through the database's read in the owner's own context, with the replay recheck: at admission,
// to fill a gap, for a step, after the worker's run ends and, before the turn starts, at each heartbeat. Text arrives
// pushed from the worker once its publication recheck has committed. The stream holds no database connection while it
// waits and is exempt from the request deadline and the session's semaphore, so Stop never waits for it.
public sealed class TurnStream(PreviewDatabase database,string scope,AgentWorker worker,TurnStreams streams,JsonSerializerOptions json,TimeProvider time,
    CancellationToken stopping,ILogger logger)
{
    public async Task<IResult> Serve(HttpContext context,Func<PreviewSession> bound,Guid conversation,long after)
    {
        // EventSource sends no anti-forgery header, so a stream is admitted only from the application's own page: a page
        // on another loopback port shares its cookie but not its origin (§8).
        if(!EventStreamAdmission.FromOwnPage(context.Request.Headers))throw new PreviewDeniedException();
        var session=bound();
        var profile=session.Profile??throw new PreviewDeniedException();
        var from=Resume(after,context.Request.Headers["Last-Event-ID"]);
        var aborted=context.RequestAborted;
        var opened=await Read(profile,conversation,null,from,aborted);
        TurnSubscription? subscription=null;
        try
        {
            if(opened is {Refused:null,Turn:{Ended:false} turn})
            {
                // The subscription hears what commits from now on. Live events come from this process's worker: a
                // generation lost before this restarts here, as for an ask, interrupting the turn (§7.4), and one lost
                // after it ends the worker's run of the turn, which the subscription hears.
                subscription=streams.Subscribe(turn.Id,session.Digest,profile);
                if(await worker.Generation(aborted) is null)
                    return Results.Json(new{code="agent_runtime_unavailable",message=Refusal("agent_runtime_unavailable")},statusCode:StatusCodes.Status503ServiceUnavailable);
                // This read sends what committed before the subscription, and any ending the restart wrote.
                var heard=await Read(profile,conversation,turn.Id,opened.Events.IsEmpty?from:opened.Events[^1].Sequence,aborted);
                opened=heard.Refused is null?heard.After(opened):opened with{Refused=heard.Refused};
            }
            // A profile switch changes the profile, then stops the streams opened under the one it left: a stream opened
            // under another profile that subscribes after the stops is refused here, and one they stop replaces none (§8).
            if(!Bound(session,profile))throw new PreviewSessionEnded();
            if(subscription is not null)streams.Activate(subscription);
            context.Response.StatusCode=StatusCodes.Status200OK;
            context.Response.ContentType="text/event-stream; charset=utf-8";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await context.Response.StartAsync(aborted);
            var named=opened.Turn?.Id;
            var delivery=new TurnDelivery(context.Response.Body,(sequence,ct)=>Read(profile,conversation,named,sequence,ct),()=>Bound(session,profile),time,json,logger);
            try{await delivery.Run(opened,from,subscription,session.Ends,aborted,stopping);}
            // The client left.
            catch(Exception e) when(aborted.IsCancellationRequested&&e is OperationCanceledException or IOException){}
            return Results.Empty;
        }
        finally{if(subscription is not null)streams.Remove(subscription);}
    }

    // The stream's read of the turn (§8), in the owner's own request context while they hold UseAgents.
    Task<StreamRead> Read(string profile,Guid conversation,Guid? turn,long after,CancellationToken ct)=>
        database.WithIdentity(profile,"UseAgents",async(connection,c)=>StreamRead.Parse(await ModelProviders.Read(connection,
            "SELECT assistant.turn_stream($1,$2,$3::uuid,$4,$5)::text",[scope,conversation,(object?)turn??DBNull.Value,after,RuntimeSet.ReplayEvents],c)),ct);

    // Whether the session still binds the stream: unexpired, under the profile it was opened with (§8).
    bool Bound(PreviewSession session,string profile)=>time.GetUtcNow()<=session.Ends&&session.Profile==profile;

    // Whether a request is a stream, which the first middleware exempts from the request deadline (§8); the route's
    // conversation is a guid, as its constraint reads one.
    internal static bool Matches(string method,PathString apiPath)=>HttpMethods.IsGet(method)
        &&apiPath.StartsWithSegments("/api/assistant/conversations",StringComparison.OrdinalIgnoreCase,out var rest)
        &&rest.Value?.TrimEnd('/').Split('/') is ["",var conversation,var events]&&Guid.TryParse(conversation,out _)
        &&events.Equals("events",StringComparison.OrdinalIgnoreCase);

    // Where a stream resumes (§8): after the Last-Event-ID a reconnect sends, which overrides after, else after. An id
    // that is not one of the stream's sequences is refused, as is a body that does not bind.
    internal static long Resume(long after,StringValues lastEventId)
    {
        if(lastEventId.Count==0)return after;
        if(lastEventId is [string id]&&id.Length is >0 and <=19&&id.All(char.IsAsciiDigit)&&(id=="0"||id[0]!='0')
           &&long.TryParse(id,NumberStyles.None,CultureInfo.InvariantCulture,out var sequence))return sequence;
        throw new BadHttpRequestException("Last-Event-ID is not a sequence.");
    }

    // The documented message for each code a stream is refused with.
    internal static string Refusal(string code)=>code switch
    {
        "turn_not_running"=>"This conversation has no unfinished answer to stream. Read the conversation for its answers.",
        "replay_unavailable"=>"The stream cannot resume from there. Read the conversation again, then open a new stream.",
        "stream_expired"=>"This answer passed its time limit without ending. Read the conversation for its state.",
        "session_ended"=>"Your session has ended. Reload the page to continue.",
        "access_denied"=>"You can no longer use this agent. Read the conversation for its state.",
        "agent_runtime_unavailable"=>ModelCatalog.Refusal(code),
        _=>throw new ArgumentOutOfRangeException(nameof(code),code,"A stream is refused only with a documented code.")
    };
}

// One stream once its admission read has answered (§8): the frames it writes, in order, until the stream ends with its
// turn's terminal event, replaced, refused or, the one deliberate case besides a read that fails, cut without a final
// event. Before each write the session must still bind it. Every stored event is sent once, in sequence: text the
// worker pushes when it is the next, anything else read from the last one sent. Once a read hides the turn's answer,
// every later text is hidden too, and every event says so.
internal sealed class TurnDelivery(Stream output,Func<long,CancellationToken,Task<StreamRead>> read,Func<bool> current,TimeProvider time,
    JsonSerializerOptions json,ILogger logger)
{
    Guid turn;
    long last;
    string? hidden;
    bool started,ended;
    DateTimeOffset heartbeat;

    // Sends what the opening read answered after the sequence the stream resumes from and then, with a subscription, the
    // turn's events as they come.
    internal async Task Run(StreamRead opened,long after,TurnSubscription? subscription,DateTimeOffset sessionEnds,CancellationToken aborted,CancellationToken stopping)
    {
        last=after;
        if(!await Write(TurnWire.Comment("open"),aborted)||!await Send(opened,aborted)||ended||subscription is null)return;
        // A turn's worker ends it within its elapsed limit; one still unfinished past the admission deadline is stuck (§7.4).
        var expiry=opened.Deadline+TimeSpan.FromSeconds(RuntimeSet.AdmissionSeconds);
        while(true)
        {
            switch(subscription.Stopped)
            {
                case StreamStop.Cut:return;
                case StreamStop.Replaced:await Write(TurnWire.Replaced,aborted);return;
                case StreamStop.SessionEnded:await Refuse("session_ended",aborted);return;
            }
            var now=time.GetUtcNow();
            if(now>=sessionEnds){await Refuse("session_ended",aborted);return;}
            if(now>=expiry){await Refuse("stream_expired",aborted);return;}
            if(now>=heartbeat)
            {
                // Until its turn starts a stream reads it again at each heartbeat, so an ending its worker never ran,
                // such as the ask's own, still arrives.
                if(!started&&(!await CatchUp(aborted)||ended))return;
                if(!await Write(TurnWire.Comment("ping"),aborted))return;
                continue;
            }
            TurnSignal? signal;
            try{signal=await Next(subscription.Signals,Min(heartbeat,Min(sessionEnds,expiry))-now,aborted,stopping);}
            catch(OperationCanceledException) when(stopping.IsCancellationRequested&&!aborted.IsCancellationRequested)
            {
                await Refuse("agent_runtime_unavailable",aborted);
                return;
            }
            if(signal is not {} heard||subscription.Stopped!=StreamStop.None)continue;
            if(heard.Ended)
            {
                // The worker's run has ended; a turn it left unfinished, as a lost generation does, is not this
                // process's to finish.
                if(await CatchUp(aborted)&&!ended)await Refuse("agent_runtime_unavailable",aborted);
                return;
            }
            if(heard.Sequence<=last)continue;
            if(heard.Sequence==last+1&&heard.Text is {} text)
            {
                if(!await Write(TurnWire.Text(heard.Sequence,turn,hidden,text,json),aborted))return;
                last=heard.Sequence;
                continue;
            }
            if(!await CatchUp(aborted)||ended)return;
        }
    }

    // Sends a read's events after the last one sent, then its refusal if it has one; false once the stream has ended.
    async Task<bool> Send(StreamRead answer,CancellationToken aborted)
    {
        if(answer.Turn is {} shown)
        {
            turn=shown.Id;
            hidden??=shown.Hidden;
            started|=shown.Started;
            foreach(var e in answer.Events.Where(e=>e.Sequence>last))
            {
                var terminal=TurnWire.Terminal(e.Kind);
                if(!await Write(TurnWire.Event(e.Sequence,e.Kind,turn,hidden,terminal?shown.Incomplete:null,e.Payload,json),aborted))return false;
                last=e.Sequence;
                ended|=terminal;
            }
        }
        if(answer.Refused is not {} code)return true;
        await Refuse(code,aborted);
        return false;
    }

    // Reads the turn after the last event sent and sends what the read answers; false once the stream has ended.
    async Task<bool> CatchUp(CancellationToken aborted)
    {
        StreamRead answer;
        try{answer=await read(last,aborted);}
        // The session was revoked elsewhere (§11).
        catch(PreviewSessionEnded)
        {
            await Refuse("session_ended",aborted);
            return false;
        }
        catch(Exception e) when(e is PreviewDeniedException or PostgresException{SqlState:"42501"})
        {
            await Refuse("access_denied",aborted);
            return false;
        }
        // A read that fails otherwise cuts the stream; its reconnect is admitted again.
        catch(Exception e) when(!aborted.IsCancellationRequested&&e is NpgsqlException or OperationCanceledException or InvalidOperationException or JsonException
            or KeyNotFoundException or FormatException)
        {
            logger.LogWarning("A stream's read failed: {ErrorType}",e.GetType().Name);
            return false;
        }
        return await Send(answer,aborted);
    }

    // The next signal, or none once the wait is over or the subscription stopped.
    async Task<TurnSignal?> Next(ChannelReader<TurnSignal> signals,TimeSpan wait,CancellationToken aborted,CancellationToken stopping)
    {
        if(signals.TryRead(out var ready))return ready;
        using var due=new CancellationTokenSource(wait,time);
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(due.Token,aborted,stopping);
        try{return await signals.WaitToReadAsync(stop.Token)&&signals.TryRead(out var signal)?signal:null;}
        catch(OperationCanceledException) when(due.IsCancellationRequested&&!aborted.IsCancellationRequested&&!stopping.IsCancellationRequested){return null;}
    }

    Task Refuse(string code,CancellationToken aborted)=>Write(TurnWire.Refused(code,json),aborted);

    // Writes a frame while the session binds the stream, else refuses it session_ended (§8); false once the stream ended so.
    async Task<bool> Write(byte[] frame,CancellationToken aborted)
    {
        var binding=current();
        await output.WriteAsync(binding?frame:TurnWire.Refused("session_ended",json),aborted);
        await output.FlushAsync(aborted);
        heartbeat=time.GetUtcNow()+RuntimeSet.StreamHeartbeat;
        return binding;
    }

    static DateTimeOffset Min(DateTimeOffset a,DateTimeOffset b)=>a<b?a:b;
}

// The stream's wire format (§8): an id line with the sequence, an event line with a fixed name, one data line of
// single-line JSON from the API serializer, and a blank line; comments are one line. JSON escapes CR and LF inside its
// strings, so no text adds a line or a field, and a pushed piece of text writes exactly as the same event read back.
internal static class TurnWire
{
    static readonly FrozenSet<string> Stored=FrozenSet.Create(StringComparer.Ordinal,"accepted","step","navigation","text","usage","completed","failed","cancelled",
        "interrupted");

    internal static bool Terminal(string kind)=>kind is "completed" or "failed" or "cancelled" or "interrupted";

    internal static byte[] Comment(string text)=>Encoding.UTF8.GetBytes($": {text}\n\n");

    internal static readonly byte[] Replaced=Frame(null,"replaced",new(),_=>{});

    internal static byte[] Refused(string code,JsonSerializerOptions json)=>Frame(null,"refused",json,w=>
    {
        w.WriteString("code",code);
        w.WriteString("message",TurnStream.Refusal(code));
    });

    // A stored event: its turn, whether the replay recheck hides its answer, for a terminal event whether the turn is
    // incomplete, and its payload, a text's or a navigation target's hidden once the answer is.
    internal static byte[] Event(long sequence,string kind,Guid turn,string? hidden,bool? incomplete,JsonElement payload,JsonSerializerOptions json)=>
        Stored.Contains(kind)?Frame(sequence,kind,json,w=>Data(w,turn,hidden,incomplete,kind is "text" or "navigation"&&hidden is not null?p=>Masked(p,hidden):payload.WriteTo))
            :throw new InvalidOperationException($"No stored event is named {kind}.");

    // A piece of text the worker pushed, as the event that stores it reads back.
    internal static byte[] Text(long sequence,Guid turn,string? hidden,string text,JsonSerializerOptions json)=>Frame(sequence,"text",json,w=>Data(w,turn,hidden,null,
        hidden is not null?p=>Masked(p,hidden):p=>{p.WriteStartObject();p.WriteString("text",text);p.WriteEndObject();}));

    static void Data(Utf8JsonWriter w,Guid turn,string? hidden,bool? incomplete,Action<Utf8JsonWriter> payload)
    {
        w.WriteString("turnId",turn);
        if(hidden is null)w.WriteNull("hidden");else w.WriteString("hidden",hidden);
        if(incomplete is {} answerIncomplete)w.WriteBoolean("incomplete",answerIncomplete);
        w.WritePropertyName("payload");
        payload(w);
    }

    static void Masked(Utf8JsonWriter w,string hidden)
    {
        w.WriteStartObject();
        w.WriteString("hidden",hidden);
        w.WriteEndObject();
    }

    static byte[] Frame(long? sequence,string name,JsonSerializerOptions json,Action<Utf8JsonWriter> properties)
    {
        var frame=new ArrayBufferWriter<byte>();
        if(sequence is {} id)Append(frame,"id: "+id.ToString(CultureInfo.InvariantCulture)+"\n");
        Append(frame,$"event: {name}\ndata: ");
        using(var writer=new Utf8JsonWriter(frame,new JsonWriterOptions{Encoder=json.Encoder}))
        {
            writer.WriteStartObject();
            properties(writer);
            writer.WriteEndObject();
        }
        Append(frame,"\n\n");
        return frame.WrittenSpan.ToArray();
    }

    static void Append(ArrayBufferWriter<byte> frame,string text)=>frame.Write(Encoding.UTF8.GetBytes(text));
}

// What one stream read answers (§8): a refusal, or the turn as its owner reads it, the instant its elapsed limit passes
// and its events after the sequence read from, as the replay recheck shows them.
internal sealed record StreamRead(string? Refused,StreamTurn? Turn,DateTimeOffset Deadline,ImmutableArray<StreamEvent> Events)
{
    internal static StreamRead Parse(JsonElement read)
    {
        if(read.TryGetProperty("refused",out var refused))return new(refused.GetString(),null,default,[]);
        var turn=read.GetProperty("turn");
        return new(null,new(turn.GetProperty("id").GetGuid(),turn.GetProperty("state").GetString()!,turn.GetProperty("startedAt").ValueKind!=JsonValueKind.Null,
                turn.GetProperty("incomplete").GetBoolean(),turn.GetProperty("hidden").GetString()),read.GetProperty("deadline").GetDateTimeOffset(),
            [..read.GetProperty("events").EnumerateArray().Select(e=>new StreamEvent(e.GetProperty("sequence").GetInt64(),e.GetProperty("kind").GetString()!,e.GetProperty("payload")))]);
    }

    // This read's events after an earlier read's, with this read's turn.
    internal StreamRead After(StreamRead earlier)=>this with
    {
        Events=[..earlier.Events,..Events.Where(e=>earlier.Events.IsEmpty||e.Sequence>earlier.Events[^1].Sequence)]
    };
}

internal sealed record StreamTurn(Guid Id,string State,bool Started,bool Incomplete,string? Hidden)
{
    internal bool Ended=>TurnWire.Terminal(State);
}

internal sealed record StreamEvent(long Sequence,string Kind,JsonElement Payload);
