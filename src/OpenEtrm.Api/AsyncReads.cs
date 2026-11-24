using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// The workbench multiplexes named managed reads; authorization is freshly
// checked at execution and fenced publication. Writes keep their ordinary routes.
internal sealed class AsyncReads(PreviewDatabase database,CancellationToken stopping,bool hosted,ILogger logger,TimeSpan submissionLimit):IAsyncDisposable
{
    // Both active slots may finish together and publish in sequence.
    readonly int admissionWindowMs=(int)(AsyncReadChannel.Lifetime-submissionLimit-AsyncReadChannel.ReadLimit-
        AsyncReadChannel.DeliveryLimit*AsyncReadChannel.MaximumActive).TotalMilliseconds;
    readonly object registryGate=new();
    readonly Dictionary<Guid,Owner> channels=[];
    sealed record Owner(AsyncReadChannel Channel,PreviewSession Session,Guid ClientId,string Profile,ScheduleContext? Context,LiveSelection? Live,string? HostedDigest)
    {
        readonly long generation=Session.ViewGeneration;
        internal bool Current=>Session.ViewGeneration==generation&&Session.Ends>DateTimeOffset.UtcNow&&Session.Profile==Profile&&Session.MarketContext==Context&&Session.Live==Live;
        internal void RequireCurrent()
        {
            if(!Current)
            {
                Channel.Close();throw new PreviewDeniedException();
            }
        }
    }
    internal void Map(RouteGroupBuilder api,Func<HttpContext,PreviewSession> session,Func<PreviewSession,ReadInput,ReadPlan> prepare)
    {
        PreviewSession ReadSession(HttpContext context)=>PageSession(context,session);
        api.MapPost("/async-reads/channels",async(HttpContext c)=>
        {
            var body=await Body<AsyncReadOpenRequest>(c);if(body.ClientId==Guid.Empty)throw AsyncReadContract.Invalid();
            var current=ReadSession(c);await current.Gate.WaitAsync(c.RequestAborted);
            try
            {
                var profile=current.Profile??throw new PreviewDeniedException();
                await database.WithIdentity(profile,null,async(connection,ct)=>{await database.RequireSignedIn(connection,ct);return true;},c.RequestAborted,hostedDigest:hosted?current.Digest:null);
                Owner[] ended;
                lock(registryGate)
                {
                    // An explicit open replaces only this page client's channel,
                    // including a lost opening receipt. Other tabs keep their reads.
                    ended=[..channels.Values.Where(o=>o.Channel.Closed||o.Session.Ends<=DateTimeOffset.UtcNow||
                        ReferenceEquals(o.Session,current)&&(o.ClientId==body.ClientId||!o.Current))];
                }
                foreach(var owner in ended)owner.Channel.Close();
                foreach(var owner in ended)await owner.Channel.DisposeAsync();
                lock(registryGate)
                {
                    foreach(var owner in ended)channels.Remove(owner.Channel.Id);
                    if(channels.Values.Count(o=>ReferenceEquals(o.Session.Account,current.Account))>=4)
                        throw AsyncReadChannel.Refusal(429,"async_session_capacity","This session already has four page response channels. Close an unused page before opening another.");
                    if(channels.Count>=64)throw AsyncReadChannel.Refusal(429,"async_channel_capacity","The response channels are at capacity.");
                    var channel=new AsyncReadChannel(Guid.NewGuid(),stopping,failure=>logger.LogError(
                        "Asynchronous read failed: operation={Operation} request={RequestId} type={ExceptionType} method={Method} sqlstate={SqlState}",
                        failure.Operation,failure.RequestId,failure.ExceptionType,failure.Method,failure.SqlState));
                    channels.Add(channel.Id,new(channel,current,body.ClientId,profile,current.MarketContext,current.Live,hosted?current.Digest:null));
                    return Results.Ok(new {protocol=AsyncReadChannel.Protocol,channelId=channel.Id,profile,context=current.MarketContext,admissionWindowMs});
                }
            }
            finally{current.Gate.Release();}
        });
        api.MapGet("/async-reads/events",(HttpContext c,Guid channelId)=>Serve(c,ReadSession(c),channelId));
        api.MapPost("/async-reads",async(HttpContext c)=>
        {
            var body=await Body<AsyncReadRequest>(c);AsyncReadChannel.Validate(body);
            var current=ReadSession(c);await current.Gate.WaitAsync(c.RequestAborted);
            try
            {
                var owner=Find(current,body.ChannelId);owner.RequireCurrent();
                var input=AsyncReadContract.Decode(body);
                var plan=prepare(current,input);
                owner.Channel.Submit(body,plan);
                return Results.Json(new {channelId=body.ChannelId,requestId=body.RequestId,state="accepted"},statusCode:202);
            }
            finally{current.Gate.Release();}
        });
        api.MapPost("/async-reads/cancel",async(HttpContext c)=>
        {
            var body=await Body<AsyncReadCancel>(c);var current=ReadSession(c);
            await current.Gate.WaitAsync(c.RequestAborted);
            try
            {
                var owner=Find(current,body.ChannelId);owner.RequireCurrent();var state=owner.Channel.Cancel(body.RequestId);
                return Results.Ok(new {channelId=body.ChannelId,requestId=body.RequestId,state});
            }
            finally{current.Gate.Release();}
        });
    }
    internal static PreviewSession PageSession(HttpContext context,Func<HttpContext,PreviewSession> session)
    {
        // Delegated tools use their classified ordinary operations. This page
        // transport cannot detach and discard an invocation's authority guards.
        if(context.Features.Get<AssistantInvocation>() is not null)throw new PreviewDeniedException();
        return session(context);
    }
    static async Task<T> Body<T>(HttpContext c)
    {
        if(c.Request.ContentLength>AsyncReadChannel.MaximumRequestBytes)throw AsyncReadChannel.Refusal(413,"async_request_size","The read request exceeds the request limit.");
        if(c.Features.Get<IHttpMaxRequestBodySizeFeature>() is {IsReadOnly:false} limit)limit.MaxRequestBodySize=AsyncReadChannel.MaximumRequestBytes;
        return await c.Request.ReadFromJsonAsync<T>(c.RequestAborted)??throw new BadHttpRequestException("Supply a JSON request.");
    }
    Owner Find(PreviewSession session,Guid id)
    {
        lock(registryGate)
        {
            if(!channels.TryGetValue(id,out var owner)||!ReferenceEquals(session,owner.Session)||owner.Channel.Closed)
                throw AsyncReadChannel.Refusal(410,"async_channel_unavailable","This response channel is unavailable. Open a new channel and explicitly request the read again.");
            return owner;
        }
    }
    Task Authorize(Owner owner,ReadInput? input,CancellationToken ct,
        Func<Npgsql.NpgsqlConnection,CancellationToken,Task>? recheck=null,Func<CancellationToken,Task>? publish=null)=>
        ReadAuthority.Require(database,owner.Profile,owner.HostedDigest,input?.Permissions??[],ct,recheck,publish);
    static async Task Current(Owner owner,CancellationToken ct)
    {
        await owner.Session.Gate.WaitAsync(ct);
        try{owner.RequireCurrent();}finally{owner.Session.Gate.Release();}
    }
    async Task<IResult> Serve(HttpContext c,PreviewSession session,Guid id)
    {
        if(!EventStreamAdmission.FromOwnPage(c.Request.Headers))throw new PreviewDeniedException();
        if(c.Request.Headers.ContainsKey("Last-Event-ID"))throw AsyncReadChannel.Refusal(409,"async_resume_unsupported","response channels cannot resume. Open a fresh channel.");
        var owner=Find(session,id);var channel=owner.Channel;
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted,channel.Stopping,stopping);
        var ct=lifetime.Token;
        // Claim failures must not close a different active listener's channel.
        await Current(owner,ct);channel.Connect();
        try
        {
            await Authorize(owner,null,ct);await Current(owner,ct);
            c.Response.ContentType="text/event-stream; charset=utf-8";
            c.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await c.Response.StartAsync(ct);
            await Write(c,"ready",new {protocol=AsyncReadChannel.Protocol,channelId=id},ct);
            while(!ct.IsCancellationRequested)
            {
                using var wake=CancellationTokenSource.CreateLinkedTokenSource(ct);wake.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    if(!await channel.Responses.WaitToReadAsync(wake.Token))break;
                }
                catch(OperationCanceledException)when(!ct.IsCancellationRequested)
                {
                    await Authorize(owner,null,ct);await Current(owner,ct);
                    await Write(c,null,null,ct);continue;
                }
                while(channel.Responses.TryRead(out var response))
                {
                    // Reauthorize the exact references and observations at delivery,
                    // not merely the role used when this read entered the queue.
                    using var delivery=CancellationTokenSource.CreateLinkedTokenSource(ct);
                    delivery.CancelAfter(AsyncReadChannel.DeliveryLimit);
                    // Keep the same gate-before-database order as ordinary handlers.
                    // Both fences cover only this bounded publication, never evaluation
                    // or stream idle time; a waiting revocation cannot race disclosure.
                    await session.Gate.WaitAsync(delivery.Token);
                    try
                    {
                        owner.RequireCurrent();
                        await Authorize(owner,response.Plan.Input,delivery.Token,response.State=="completed"?response.Recheck:null,publication=>
                        {
                            owner.RequireCurrent();
                            return Write(c,"response",new AsyncReadFrame(AsyncReadChannel.Protocol,id,response.Request.RequestId,
                                response.Request.Operation,response.State,response.Plan.Input.SnapshotId,response.Result,response.Error,response.ReadReferences),publication);
                        });
                    }
                    finally{session.Gate.Release();}
                }
            }
        }
        catch(Exception e)when(c.Response.HasStarted&&(LiveContexts.Ends(e)||e is AsyncReadException)){}
        finally
        {
            channel.Close();
            // Retiring owners remain discoverable so a new open must await the
            // same drain before it can admit more work for this session.
            await channel.DisposeAsync();
            lock(registryGate)channels.Remove(id);
        }
        return Results.Empty;
    }
    internal static async Task Write(HttpContext c,string? name,object? body,CancellationToken ct)
    {
        using var write=CancellationTokenSource.CreateLinkedTokenSource(ct);write.CancelAfter(TimeSpan.FromSeconds(5));
        await JsonReadTransport.WriteEvent(c.Response.Body,name,body,write.Token);
        await c.Response.Body.FlushAsync(write.Token);
    }
    internal static bool Matches(string method,PathString path)=>HttpMethods.IsGet(method)&&path.Equals("/api/async-reads/events",StringComparison.OrdinalIgnoreCase);
    public async ValueTask DisposeAsync()
    {
        Owner[] owners;lock(registryGate){owners=[..channels.Values];channels.Clear();}
        foreach(var owner in owners)owner.Channel.Close();
        foreach(var owner in owners)await owner.Channel.DisposeAsync();
    }
}
