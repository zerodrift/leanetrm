using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenEtrm.Api;

internal static class AsyncReadChecks
{
    internal static async Task Run()
    {
        var channelId=Guid.NewGuid();var snapshot=Guid.NewGuid();
        var request=new AsyncReadRequest(channelId,Guid.NewGuid(),"curve.evaluate.v1",JsonSerializer.SerializeToElement(new{snapshotId=snapshot,curve="synthetic",includeWorkspace=true}));
        ReadPlan Plan(AsyncReadRequest selected,Func<CancellationToken,Task<object>> work)=>new(AsyncReadContract.Decode(selected),async ct=>new(await work(ct),(_,_)=>Task.CompletedTask));
        await using var channel=new AsyncReadChannel(channelId,CancellationToken.None);
        Refused("async_channel_not_ready",()=>channel.Submit(request,Plan(request,_=>Task.FromResult<object>(new {}))));
        channel.Connect();
        var both=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        async Task<object> Work(CancellationToken ct){if(Interlocked.Increment(ref calls)==2)both.SetResult();await release.Task.WaitAsync(ct);return new{value="synthetic"};}
        channel.Submit(request,Plan(request,Work));
        var reordered=request with{Body=JsonSerializer.SerializeToElement(new{curve="synthetic",includeWorkspace=true,snapshotId=snapshot})};
        channel.Submit(reordered,Plan(reordered,Work));
        var conflict=request with{Body=JsonSerializer.SerializeToElement(new{snapshotId=snapshot,curve="different",includeWorkspace=true})};
        Refused("async_request_conflict",()=>channel.Submit(conflict,Plan(conflict,Work)));
        var second=request with{RequestId=Guid.NewGuid()};channel.Submit(second,Plan(second,Work));
        await both.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var third=request with{RequestId=Guid.NewGuid()};Refused("async_busy",()=>channel.Submit(third,Plan(third,Work)));
        release.SetResult();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var a=await channel.Read(timeout.Token);var b=await channel.Read(timeout.Token);
        if(calls!=2||a.Request.RequestId==b.Request.RequestId||a.State!="completed"||b.State!="completed")throw new Exception("Independent reads must complete once with exact request identities.");
        channel.Submit(request,Plan(request,Work));if(calls!=2)throw new Exception("A completed retry must not repeat work.");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var drain=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Submit(third,Plan(third,async ct=>{entered.SetResult();try{await Task.Delay(Timeout.Infinite,ct);}catch(OperationCanceledException){cancelled.SetResult();await drain.Task;throw;}return new {};}));
        await entered.Task;var firstDisposal=channel.DisposeAsync().AsTask();await cancelled.Task;
        var secondDisposal=channel.DisposeAsync().AsTask();if(secondDisposal.IsCompleted){drain.SetResult();await firstDisposal;throw new Exception("All replacement and disconnect callers must await the same worker drain.");}
        drain.SetResult();await Task.WhenAll(firstDisposal,secondDisposal);
        if(!channel.Stopping.IsCancellationRequested)throw new Exception("Retired channels retain a safe cancelled lifetime token.");
        Refused("async_channel_unavailable",()=>channel.Submit(request,Plan(request,Work)));
        await AsyncReadBudgetChecks.Run();
        await CancellationAdmission();
        await DetachedRequestContext();
        DelegatedTransport();
        await FailureDiagnostics();
        await AsyncReadVisibilityChecks.Run();
        Console.WriteLine("PASS async reads: concurrency, normalized retries, bounds, correlation, cancellation admission/drain, detached request context, delegated refusal, safe diagnostics and visibility isolation");
    }
    static async Task FailureDiagnostics()
    {
        var failures=new List<AsyncReadFailure>();
        await using var channel=new AsyncReadChannel(Guid.NewGuid(),CancellationToken.None,failures.Add);channel.Connect();
        var request=new AsyncReadRequest(channel.Id,Guid.NewGuid(),"curve.evaluate.v1",JsonSerializer.SerializeToElement(new{snapshotId=Guid.NewGuid(),curve="synthetic"}));
        const string detail="synthetic confidential payload";
        channel.Submit(request,new(AsyncReadContract.Decode(request),_=>throw new InvalidOperationException(detail)));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));var failed=await channel.Read(timeout.Token);
        if(failed.State!="failed"||failed.Error?.Code!="async_read_failed"||failed.Error.Message.Contains(detail,StringComparison.Ordinal))
            throw new Exception("Unexpected worker failures must preserve the generic client refusal.");
        if(failures.Count!=1||failures[0].Operation!=request.Operation||failures[0].RequestId!=request.RequestId||
            failures[0].ExceptionType!=typeof(InvalidOperationException).FullName||string.IsNullOrEmpty(failures[0].Method)||
            JsonSerializer.Serialize(failures).Contains(detail,StringComparison.Ordinal))
            throw new Exception("Unexpected worker diagnostics must correlate the exact read using type and method without exception text or request data.");
        var refused=request with{RequestId=Guid.NewGuid()};channel.Submit(refused,new(AsyncReadContract.Decode(refused),
            _=>throw new OpenEtrm.MarketData.MarketDataException("synthetic_refusal","Explicit synthetic input refusal.")));
        var known=await channel.Read(timeout.Token);
        if(known.Error?.Code!="synthetic_refusal"||failures.Count!=1)
            throw new Exception("Expected business refusals must keep their structured result without being logged as unexpected failures.");
        await using var unavailableLog=new AsyncReadChannel(Guid.NewGuid(),CancellationToken.None,_=>throw new InvalidOperationException("Synthetic log failure."));unavailableLog.Connect();
        var unlogged=request with{ChannelId=unavailableLog.Id,RequestId=Guid.NewGuid()};
        unavailableLog.Submit(unlogged,new(AsyncReadContract.Decode(unlogged),_=>throw new InvalidOperationException(detail)));
        if((await unavailableLog.Read(timeout.Token)).Error?.Code!="async_read_failed")
            throw new Exception("A diagnostic sink failure must not prevent the original generic terminal response.");
    }
    static void DelegatedTransport()
    {
        var manifest=new PreviewManifest("local-synthetic-preview",Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
            Guid.NewGuid(),Guid.NewGuid(),[],null,FuturesConvention:"contract_lot_execution_pnl_v2");
        var session=new PreviewSession(manifest){Profile="trader"};var context=new DefaultHttpContext();var resolved=false;
        PreviewSession Resolve(HttpContext c){resolved=true;return session;}
        if(!ReferenceEquals(AsyncReads.PageSession(context,Resolve),session)||!resolved)
            throw new Exception("The page read transport must resolve its ordinary authenticated session.");
        context.Features.Set(new AssistantInvocation(session));resolved=false;
        try{AsyncReads.PageSession(context,Resolve);}
        catch(PreviewDeniedException)when(!resolved){return;}
        throw new Exception("Delegated invocations must be refused before resolving a page session, never lose their authority constraints in detached work.");
    }
    static async Task DetachedRequestContext()
    {
        var accessor=new HttpContextAccessor();var submitting=new DefaultHttpContext();accessor.HttpContext=submitting;
        var ambient=new AsyncLocal<string?>{Value="synthetic submission"};
        try
        {
            await using var channel=new AsyncReadChannel(Guid.NewGuid(),CancellationToken.None);channel.Connect();
            var request=new AsyncReadRequest(channel.Id,Guid.NewGuid(),"curve.evaluate.v1",JsonSerializer.SerializeToElement(new{snapshotId=Guid.NewGuid(),curve="synthetic"}));
            var plan=new ReadPlan(AsyncReadContract.Decode(request),_=>Task.FromResult(new ReadOutcome(
                new{requestContext=accessor.HttpContext is not null,ambient=ambient.Value},(_,_)=>Task.CompletedTask)));
            channel.Submit(request,plan);
            if(!ReferenceEquals(accessor.HttpContext,submitting)||ambient.Value!="synthetic submission")
                throw new Exception("Scheduling a detached read must preserve the submitting request's own context.");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));var completed=await channel.Read(timeout.Token);
            using var result=JsonDocument.Parse(completed.Result??throw new Exception("The completed result is missing."));
            if(completed.State!="completed"||result.RootElement.GetProperty("requestContext").GetBoolean()||result.RootElement.GetProperty("ambient").ValueKind!=JsonValueKind.Null)
                throw new Exception("A detached read must not inherit the submitting HTTP context or its ambient execution state.");
            var alreadyDetached=request with{RequestId=Guid.NewGuid()};
            using(ExecutionContext.SuppressFlow())channel.Submit(alreadyDetached,plan);
            var repeated=await channel.Read(timeout.Token);
            using var repeatedResult=JsonDocument.Parse(repeated.Result??throw new Exception("The completed retry is missing."));
            if(repeated.State!="completed"||repeatedResult.RootElement.GetProperty("requestContext").GetBoolean())
                throw new Exception("An already detached caller must still admit and complete its explicit read.");
        }
        finally{accessor.HttpContext=null;}
    }
    static async Task CancellationAdmission()
    {
        await using var channel=new AsyncReadChannel(Guid.NewGuid(),CancellationToken.None);channel.Connect();
        var request=new AsyncReadRequest(channel.Id,Guid.NewGuid(),"curve.evaluate.v1",JsonSerializer.SerializeToElement(new{snapshotId=Guid.NewGuid(),curve="synthetic"}));
        var calls=0;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ReadPlan Plan(AsyncReadRequest selected)=>new(AsyncReadContract.Decode(selected),async ct=>
        {Interlocked.Increment(ref calls);entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);return new(new{},(_,_)=>Task.CompletedTask);});
        if(channel.Cancel(request.RequestId)!="not_admitted"||channel.Cancel(request.RequestId)!="not_admitted")
            throw new Exception("Cancellation before HTTP admission must return an idempotent promise that the request cannot execute later.");
        Refused("async_request_cancelled",()=>channel.Submit(request,Plan(request)));
        if(calls!=0)throw new Exception("A delayed submit must never execute after confirmed non-admission cancellation.");
        var admitted=request with{RequestId=Guid.NewGuid()};channel.Submit(admitted,Plan(admitted));await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if(channel.Cancel(admitted.RequestId)!="cancellation_requested")throw new Exception("An admitted worker must retain its slot until its terminal response.");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));var terminal=await channel.Read(timeout.Token);
        if(terminal.State!="cancelled"||terminal.Request.RequestId!=admitted.RequestId||calls!=1)throw new Exception("Admitted cancellation must produce one exact terminal response.");
        for(var n=2;n<AsyncReadChannel.MaximumRequests;n++)channel.Cancel(Guid.NewGuid());
        Refused("async_channel_limit",()=>channel.Cancel(Guid.NewGuid()));
        Refused("async_channel_limit",()=>channel.Submit(request with{RequestId=Guid.NewGuid()},Plan(request)));
    }
    static void Refused(string code,Action action){try{action();}catch(AsyncReadException error)when(error.Code==code){return;}throw new Exception("Expected async refusal "+code);}
}
