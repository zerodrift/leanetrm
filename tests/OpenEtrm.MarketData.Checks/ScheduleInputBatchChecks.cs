using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Calculation;

internal static class ScheduleInputBatchChecks
{
    internal static async Task Run()
    {
        var at=new DateTimeOffset(2031,1,1,0,0,0,TimeSpan.Zero);
        var context=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic_side_input_batch_v1",at,at,at,at);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started=0;var active=0;var maximum=0;var counts=new object();
        async Task<ScheduleInputValue<string>> Read(ResolvedCalculationContext supplied,string scenario,CancellationToken ct)
        {
            if(!ReferenceEquals(context,supplied))throw new Exception("Input reads must receive the exact selected immutable context.");
            lock(counts)
            {
                started++;active++;maximum=Math.Max(maximum,active);
                if(started==2)twoStarted.TrySetResult();
            }
            try{await release.Task.WaitAsync(ct);return new(supplied,scenario,"resolved");}
            finally{lock(counts)active--;}
        }
        var operation=ScheduleInputBatch.Load(context,"base",new[]{new ScheduleInputRead<string>("price",Read),new ScheduleInputRead<string>("discount",Read)},2);
        try{await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));}
        finally{release.SetResult();}
        var result=await operation;
        if(result.Count!=2||result["price"]!="resolved"||result["discount"]!="resolved")
            throw new Exception("Independent price and discount inputs must both start before either is released; async sequential reads do not satisfy parallel loading.");
        release=new(TaskCreationOptions.RunContinuationsAsynchronously);twoStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);started=0;maximum=0;
        operation=ScheduleInputBatch.Load(context,"base",new[]{new ScheduleInputRead<string>("price",Read),new ScheduleInputRead<string>("discount",Read),new ScheduleInputRead<string>("quantity",Read)},2);
        try
        {
            await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(Volatile.Read(ref started)!=2)throw new Exception("The third input must wait while the two available slots are occupied.");
        }
        finally{release.SetResult();}
        await operation;
        if(started!=3||maximum!=2)throw new Exception("A queued input must run after a slot is released, without exceeding two active reads.");

        try
        {
            await ScheduleInputBatch.Load(context,"base",new[]{new ScheduleInputRead<string>("price",(c,scenario,_)=>Task.FromResult(new ScheduleInputValue<string>(new(Guid.NewGuid(),c.ManifestId,c.EffectiveAt,c.KnownAt,c.ValuationAt,c.MarketAsOf),scenario,"wrong context")))},1);
            throw new Exception("An input from another knowledge context must refuse the complete value binding.");
        }
        catch(MarketDataException e)when(e.Code=="side_input_context"){}

        try
        {
            await ScheduleInputBatch.Load(context,"base",new[]{new ScheduleInputRead<string>("quantity",(c,scenario,_)=>Task.FromResult(new ScheduleInputValue<string>(c,scenario,null!)))},1);
            throw new Exception("A missing dynamic value must refuse rather than appear in a successfully resolved batch.");
        }
        catch(MarketDataException e)when(e.Code=="side_input_value"){}
        var zero=await ScheduleInputBatch.Load(context,"base",new[]{new ScheduleInputRead<int>("quantity",(c,scenario,_)=>Task.FromResult(new ScheduleInputValue<int>(c,scenario,0)))},1);
        if(zero["quantity"]!=0)throw new Exception("An explicitly resolved zero quantity is valid and must not be treated as missing.");

        var childStopped=false;
        async Task<ScheduleInputValue<string>> Blocked(ResolvedCalculationContext c,string scenario,CancellationToken ct)
        {
            try{await Task.Delay(Timeout.Infinite,ct);throw new Exception("An infinite input cannot complete successfully.");}
            catch(OperationCanceledException){childStopped=true;throw;}
        }
        try
        {
            await ScheduleInputBatch.Load(context,"base",new[]{new ScheduleInputRead<string>("price",Blocked),new ScheduleInputRead<string>("discount",(_,_,_)=>Task.FromException<ScheduleInputValue<string>>(new MarketDataException("missing_discount","Missing exact discount input.")))},2);
            throw new Exception("Missing input must refuse the batch, not return the successful subset.");
        }
        catch(MarketDataException e)when(e.Code=="missing_discount"){}
        if(!childStopped)throw new Exception("A failed batch must cancel and observe its outstanding source reads before returning.");
        using var cancelled=new CancellationTokenSource();
        release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ScheduleInputValue<string>> IgnoresCancellation(ResolvedCalculationContext c,string scenario,CancellationToken _)
        {await release.Task;return new(c,scenario,"late value");}
        operation=ScheduleInputBatch.Load(context,"base",new[]{new ScheduleInputRead<string>("price",IgnoresCancellation)},1,cancelled.Token);
        cancelled.Cancel();release.SetResult();
        try{await operation;throw new Exception("Cancellation accepted while a source runs must refuse its late successful response.");}
        catch(OperationCanceledException){}
        try
        {
            await ScheduleInputBatch.Load(context,"up",new[]{new ScheduleInputRead<string>("price",(c,_,_)=>Task.FromResult(new ScheduleInputValue<string>(c,"base","wrong scenario")))},1);
            throw new Exception("A base input cannot be substituted into a requested shock scenario.");
        }
        catch(MarketDataException e)when(e.Code=="side_input_context"){}
        await FrozenDemand(context);
        await FaultingCancellationCallback(context);
        await CancelledQueuedRead(context);
        Console.WriteLine("PASS common Side input loading: overlap, explicit bound, pinned-context refusal and complete cancellation");
    }

    private static async Task FrozenDemand(ResolvedCalculationContext context)
    {
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var addedReads=0;
        var reads=new List<ScheduleInputRead<string>>();
        reads.Add(new("price",async(c,scenario,ct)=>{await release.Task.WaitAsync(ct);return new(c,scenario,"price");}));
        reads.Add(new("discount",(c,scenario,_)=>
        {
            reads.Add(new("later",(cc,ss,_)=>{Interlocked.Increment(ref addedReads);return Task.FromResult(new ScheduleInputValue<string>(cc,ss,"unexpected"));}));
            changed.SetResult();return Task.FromResult(new ScheduleInputValue<string>(c,scenario,"discount"));
        }));
        var operation=ScheduleInputBatch.Load(context,"base",reads,2);
        bool waiting;
        try
        {
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            waiting=!operation.IsCompleted;
        }
        finally{release.SetResult();}
        var result=await operation;
        if(!waiting||result.Count!=2||addedReads!=0)
            throw new Exception("The admitted read set must stay fixed if the caller changes its list, and every admitted child must finish before the batch returns.");
    }

    private static async Task FaultingCancellationCallback(ResolvedCalculationContext context)
    {
        var stopped=false;
        async Task<ScheduleInputValue<string>> Read(ResolvedCalculationContext c,string scenario,CancellationToken ct)
        {
            using var registration=ct.Register(()=>throw new InvalidOperationException("Synthetic cancellation callback failure."));
            try{await Task.Delay(Timeout.Infinite,ct);return new(c,scenario,"unexpected");}
            finally{stopped=true;}
        }
        try
        {
            await ScheduleInputBatch.Load(context,"base",new[]{new ScheduleInputRead<string>("price",Read),
                new ScheduleInputRead<string>("discount",(_,_,_)=>Task.FromException<ScheduleInputValue<string>>(new MarketDataException("missing_discount","Missing exact discount input.")))},2);
            throw new Exception("A failed source must refuse the batch.");
        }
        catch(MarketDataException e)when(e.Code=="missing_discount"){}
        if(!stopped)throw new Exception("The original source failure must survive a cancellation callback failure, after observing every child.");
    }

    private static async Task CancelledQueuedRead(ResolvedCalculationContext context)
    {
        var register=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Synchronous completion deliberately releases an active read while
        // cancellation is still notifying the queued read's semaphore waiter.
        var finish=new TaskCompletionSource();
        var fail=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedRan=false;
        async Task<ScheduleInputValue<string>> First(ResolvedCalculationContext c,string scenario,CancellationToken ct)
        {
            await register.Task;
            using var subscription=ct.Register(()=>finish.SetResult());
            registered.SetResult();await finish.Task;return new(c,scenario,"late");
        }
        async Task<ScheduleInputValue<string>> Second(ResolvedCalculationContext c,string scenario,CancellationToken ct)
        {await fail.Task;throw new MarketDataException("missing_discount","Missing exact discount input.");}
        var operation=ScheduleInputBatch.Load(context,"base",new[]{new ScheduleInputRead<string>("price",First),
            new ScheduleInputRead<string>("discount",Second),new ScheduleInputRead<string>("quantity",(c,scenario,_)=>
            {queuedRan=true;return Task.FromResult(new ScheduleInputValue<string>(c,scenario,"unexpected"));})},2);
        register.SetResult();
        try{await registered.Task.WaitAsync(TimeSpan.FromSeconds(5));}
        finally{fail.SetResult();}
        try{await operation;throw new Exception("A failed source must refuse the batch.");}
        catch(MarketDataException e)when(e.Code=="missing_discount"){}
        if(queuedRan)throw new Exception("A queued source must not start after cancellation, even when an active read completes inside its cancellation callback.");
    }
}
