using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace OpenEtrm.Api;

// Advances the SAME header context used by market, trade, side schedule and risk
// reads. No pricing or secondary live valuation implementation lives here.
public sealed class LiveContexts(PreviewDatabase database,CalculationChanges changes,JsonSerializerOptions json)
{
    public async Task<LiveSelection> Resolve(string profile,LiveSelection selection,CancellationToken ct)=>
        await database.WithIdentity(profile,"ReadMarketData",async(connection,cancellation)=>
        {
            var head=await Head(connection,cancellation);
            if(head==selection.Head)return selection;
            var captured=await new KnowledgeStore(database.Manifest.CatalogId).Capture(connection,
                new(selection.Seed.At.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),null,"current"),cancellation);
            await MarketContextStore.Bind(connection,database.Manifest.CatalogId,captured.Context,cancellation);
            var data=await database.Read(connection,cancellation,observationIds:selection.Seed.ObservationIds);
            var frame=MarketContextStore.Select(selection.Seed,data,captured.Context) with{OriginCloseId=null,Name="Live · selected-fact corrections"};
            // Capture holds the common publication gate through this transaction.
            // This head and the new cutoff therefore describe one committed view.
            return selection.Resolve(captured.Context,frame,await Head(connection,cancellation));
        },ct);

    async Task<string> Head(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.calculation_head($1)",database.Manifest.CatalogId);
        return (string)(await command.ExecuteScalarAsync(ct)??throw new PreviewDeniedException());
    }
    public async Task<IResult> Serve(HttpContext c,Func<PreviewSession> bound,Guid id,CancellationToken stopping)
    {
        if(!EventStreamAdmission.FromOwnPage(c.Request.Headers))throw new PreviewDeniedException();
        var session=bound();var profile=session.Profile??throw new PreviewDeniedException();
        bool Current()=>DateTimeOffset.UtcNow<session.Ends&&session.Profile==profile&&session.Live?.Id==id;
        if(!Current())throw new PreviewDeniedException();
        if(c.Request.Headers["Last-Event-ID"] is {Count:>0} resume&&
            (resume.Count!=1||resume[0] is not {} last||!last.StartsWith(id.ToString("D")+":",StringComparison.Ordinal)||
                !long.TryParse(last.AsSpan(37),NumberStyles.None,CultureInfo.InvariantCulture,out var sequence)||sequence<1))
            throw new BadHttpRequestException("The live stream resume point belongs to another selection.");
        var subscription=changes.Subscribe();
        if(!session.ClaimLiveStream()){changes.Remove(subscription.Id);throw new PreviewDeniedException();}
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted,stopping,session.LiveStopping);
        var ct=lifetime.Token;
        try
        {
            long delivered=0;var heartbeat=DateTimeOffset.UtcNow+TimeSpan.FromSeconds(10);
            while(Current())
            {
                // No connection or session semaphore is held while waiting for a
                // notification, heartbeat or client output. Heartbeats reauthorize.
                LiveSelection resolved;
                await session.Gate.WaitAsync(ct);
                try
                {
                    if(!Current())break;
                    var prior=session.Live!;
                    resolved=await Resolve(profile,prior,ct);
                    if(!Current())break;
                    if(!ReferenceEquals(prior,resolved))session.SelectLive(resolved);
                }
                finally{session.Gate.Release();}
                if(!Current())break;
                if(!c.Response.HasStarted)
                {
                    c.Response.ContentType="text/event-stream; charset=utf-8";
                    c.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                    await c.Response.StartAsync(ct);
                }
                if(resolved.Generation!=delivered||DateTimeOffset.UtcNow>=heartbeat)
                {
                    using var write=CancellationTokenSource.CreateLinkedTokenSource(ct);
                    write.CancelAfter(TimeSpan.FromSeconds(5));
                    var payload=resolved.Generation==delivered?": ping\n\n":
                        $"id: {id:D}:{resolved.Generation}\nevent: context\ndata: "+JsonSerializer.Serialize(new
                        {live=resolved.Summary(),context=resolved.Context,frame=new NavigationFrame(resolved.Frame!.Id,resolved.Frame.Name,resolved.Frame.At)},json)+"\n\n";
                    if(!Current())break;
                    await c.Response.WriteAsync(payload,write.Token);await c.Response.Body.FlushAsync(write.Token);
                    if(DateTimeOffset.UtcNow>=heartbeat)heartbeat=DateTimeOffset.UtcNow+TimeSpan.FromSeconds(10);
                }
                delivered=resolved.Generation;
                using var wake=CancellationTokenSource.CreateLinkedTokenSource(ct);
                wake.CancelAfter(heartbeat>DateTimeOffset.UtcNow?heartbeat-DateTimeOffset.UtcNow:TimeSpan.Zero);
                try{await subscription.Reader.ReadAsync(wake.Token);}
                catch(OperationCanceledException)when(!ct.IsCancellationRequested){}
                // Coalesce bursts into one new frame, with a bounded refresh rate.
                while(subscription.Reader.TryRead(out _)){}
                await Task.Delay(TimeSpan.FromMilliseconds(250),ct);
            }
        }
        catch(Exception e)when(c.Response.HasStarted&&Ends(e)){}
        finally{changes.Remove(subscription.Id);session.ReleaseLiveStream();}
        return Results.Empty;
    }
    // The failures that end a started stream quietly: the client left or the process is stopping, the session ended, a read
    // was refused, or the selection no longer resolves (docs/ai-assistant.md §11 for a hosted session revoked elsewhere).
    internal static bool Ends(Exception e)=>e is OperationCanceledException or IOException or PreviewDeniedException or PreviewSessionEnded
        or PostgresException or OpenEtrm.MarketData.MarketDataException or OpenEtrm.Shared.Temporal.TemporalException;
    internal static bool Matches(string method,PathString path)=>HttpMethods.IsGet(method)&&path.Equals("/api/market/live/events",StringComparison.OrdinalIgnoreCase);
}
