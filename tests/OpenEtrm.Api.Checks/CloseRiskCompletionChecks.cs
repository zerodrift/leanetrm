using OpenEtrm.Automation;
using System.Net.Sockets;

internal static class CloseRiskCompletionChecks
{
    internal static async Task Run()
    {
        var failures=new List<string>();
        foreach(var fault in new[]{"work","heartbeat","both"})
        foreach(var outcome in new[]{"complete","incomplete","denied","cancelled"})
        {
            using var work=new CancellationTokenSource();using var heartbeat=new CancellationTokenSource();
            using var caller=new CancellationTokenSource();
            var completion=new CloseRiskCompletion();var workCallbacks=0;var heartbeatCallbacks=0;var joined=false;var lookups=0;
            using var workCallback=work.Token.Register(()=>{Interlocked.Increment(ref workCallbacks);if(fault is "work" or "both")throw new SocketException(22);});
            using var heartbeatCallback=heartbeat.Token.Register(()=>{Interlocked.Increment(ref heartbeatCallbacks);if(fault is "heartbeat" or "both")throw new SocketException(22);});
            var renewal=Task.Run(async()=>{await completion.Cancel(work);joined=true;});
            if(outcome=="cancelled")caller.Cancel();
            Task<bool> Committed(CancellationToken ct)
            {
                lookups++;
                if(!joined || ct!=caller.Token)throw new Exception("Reconciliation must join renewal and retain caller cancellation.");
                ct.ThrowIfCancellationRequested();
                if(outcome=="denied")throw new AutomationException("access_denied","Synthetic current authority refusal.");
                return Task.FromResult(outcome=="complete");
            }
            try
            {
                var completed=await completion.Reconcile(heartbeat,renewal,Committed,caller.Token);
                if(outcome is "denied" or "cancelled" || completed!=(outcome=="complete"))
                    throw new Exception("Callback failure must never fabricate a completed outcome or suppress an authoritative refusal.");
            }
            catch(AutomationException e) when(outcome=="denied" && e.Code=="access_denied") { }
            catch(OperationCanceledException) when(outcome=="cancelled" && caller.IsCancellationRequested) { }
            catch(Exception error){failures.Add(fault+"/"+outcome+": "+error.GetType().Name);}
            try{await completion.Stop(heartbeat,renewal);}catch(Exception error){failures.Add(fault+" cleanup: "+error.GetType().Name);}
            if(!work.IsCancellationRequested || !heartbeat.IsCancellationRequested || !joined || lookups!=1 || workCallbacks!=1 || heartbeatCallbacks!=1 ||
                completion.CancellationFailures.Length!=(fault=="both"?2:1))
                failures.Add(fault+"/"+outcome+": cancellation, callback evidence, join or exact lookup was lost");
        }
        // Unexpected application failures keep their identity even when the
        // cleanup cancellation callback also fails; no success lookup is inferred.
        using(var heartbeat=new CancellationTokenSource())
        {
            var completion=new CloseRiskCompletion();var original=new InvalidOperationException("Synthetic processing failure.");
            using var callback=heartbeat.Token.Register(()=>throw new SocketException(22));
            try
            {
                try{throw original;}
                finally{await completion.Stop(heartbeat,Task.CompletedTask);}
            }
            catch(Exception error) when(ReferenceEquals(error,original)) { }
            catch(Exception error){failures.Add("unexpected process failure masked by "+error.GetType().Name);}
            if(!heartbeat.IsCancellationRequested || completion.CancellationFailures.Length!=1)failures.Add("unexpected process cleanup lost cancellation evidence");
        }
        if(failures.Count>0)throw new Exception(string.Join(Environment.NewLine,failures));
        Console.WriteLine("PASS existing-close cancellation callback faults preserve cancellation, renewal join and exact committed/refused outcomes");
    }
}
