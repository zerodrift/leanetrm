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
        var pages=new PageSessionRegistry(3);
        var a=pages.GetOrCreate(null,_=>true,()=>new PreviewSession(manifest));
        var b=pages.GetOrCreate(null,_=>true,()=>new PreviewSession(manifest));
        var tab=Guid.NewGuid();var document=Guid.NewGuid();var popup=Guid.NewGuid();
        var selection=pages.View(a.Id,a.Session,tab,()=>new PreviewSession(manifest,account:a.Session),document);
        var independent=pages.View(b.Id,b.Session,Guid.NewGuid(),()=>new PreviewSession(manifest,account:b.Session));
        if(independent==selection||selection==a.Session)throw new Exception("Independent pages must own different selections.");
        pages.View(a.Id,a.Session,tab,document:popup);
        var before=selection.ViewGeneration;
        pages.Detach(a.Id,tab,document,TimeSpan.FromMinutes(1));
        if(selection.ViewGeneration!=before)throw new Exception("A detached trade window still owns the view.");
        pages.Detach(a.Id,tab,popup,TimeSpan.FromMinutes(1));
        if(selection.ViewGeneration==before)throw new Exception("The final document must invalidate outstanding results.");
        if(pages.View(a.Id,a.Session,tab,document:Guid.NewGuid())!=selection)throw new Exception("Refresh lost the live workspace.");
        Console.WriteLine("PASS concurrent page opens respect the explicit capacity and reuse existing sessions at capacity");
    }
}
