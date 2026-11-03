using OpenEtrm.Api;

static class PageSessionChecks
{
    public static void Run()
    {
        var manifest=new PreviewManifest("local-synthetic-preview",Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),[],null,FuturesConvention:"contract_lot_execution_pnl_v2");
        var sessions=new PageSessionRegistry(1);
        using var start=new Barrier(9);
        var admitted=0;var refused=0;
        var attempts=Enumerable.Range(0,8).Select(_=>Task.Run(()=>
        {
            start.SignalAndWait();
            try{sessions.GetOrCreate(null,_=>true,()=>new PreviewSession(manifest));Interlocked.Increment(ref admitted);}
            catch(PreviewDeniedException){Interlocked.Increment(ref refused);}
        })).ToArray();
        start.SignalAndWait();Task.WaitAll(attempts);
        if(admitted!=1||refused!=7)throw new Exception("Concurrent page opens exceeded the configured session capacity.");
        var owned=new PageSessionRegistry(1);var first=owned.GetOrCreate(null,_=>true,()=>new PreviewSession(manifest));
        if(owned.GetOrCreate(first.Id,_=>true,()=>throw new Exception("Existing session recreated")).Created)
            throw new Exception("An existing page consumes another capacity slot.");
        owned.Retire(first.Session.Digest);
        if(!owned.GetOrCreate(null,_=>true,()=>new PreviewSession(manifest)).Created)
            throw new Exception("Signing out must release its page capacity before signing in again.");
        Console.WriteLine("PASS concurrent page opens respect the explicit capacity and reuse existing sessions at capacity");
    }
}
