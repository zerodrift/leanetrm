namespace OpenEtrm.Automation;

// One existing-close attempt owns cancellation and committed-outcome reconciliation.
public sealed class CloseRiskCompletion
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<AggregateException> failures=new();
    public AggregateException[] CancellationFailures=>failures.ToArray();
    public async Task Cancel(CancellationTokenSource cancellation)
    {
        try{await cancellation.CancelAsync();}
        catch(AggregateException error) when(cancellation.IsCancellationRequested)
        {
            // The token is already cancelled. A driver callback failure is evidence
            // to retain, not an authoritative statement about the database commit.
            failures.Enqueue(error);
        }
    }
    public async Task Stop(CancellationTokenSource heartbeat,Task renewing)
    {
        await Cancel(heartbeat);
        await renewing;
    }
    public async Task<bool> Reconcile(CancellationTokenSource heartbeat,Task renewing,
        Func<CancellationToken,Task<bool>> completed,CancellationToken ct)
    {
        await Stop(heartbeat,renewing);
        return await completed(ct);
    }
}
