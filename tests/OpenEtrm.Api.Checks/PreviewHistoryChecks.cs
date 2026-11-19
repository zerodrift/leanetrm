using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.Shared.Temporal;

internal static class PreviewHistoryChecks
{
    internal static async Task Run()
    {
        var fixture=PreviewDataChecks.Fixture(1);
        var selected=new PreviewData(fixture.Manifest,fixture.Definitions,fixture.References,fixture.Observations);
        var at=DateTimeOffset.UnixEpoch;
        var window=new ObservationWindow(Guid.NewGuid(),"synthetic","trader_mark",at,at);
        var queries=0;
        Task<PreviewData> Read(ImmutableArray<ObservationWindow> windows,CancellationToken ct)
        {
            queries++;
            if(windows.Length!=1||windows[0]!=window)throw new Exception("Unexpected historical demand.");
            return Task.FromResult(selected);
        }
        var empty=new CustomCurves.PreviewHistory((add,ct)=>throw new Exception("An empty demand must bypass planning."),Read);
        if(await empty.Find([],default) is not null)throw new Exception("An empty demand must use the ordinary no-SQL reader.");
        var shared=new CustomCurves.PreviewHistory((add,ct)=>{add(window);add(window);return Task.CompletedTask;},Read);
        if(await shared.Find([window],default)!=selected||await shared.Find([window],default)!=selected||queries!=1)
            throw new Exception("Overlapping demands must share one authorized observation read.");
        var unplanned=window with{To=at.AddDays(1)};
        if(await shared.Find([unplanned],default) is not null)throw new Exception("An unplanned span cannot use the batch.");
        var over=Enumerable.Repeat(window,PreviewDatabase.MaximumObservationWindows+1).ToImmutableArray();
        if(await shared.Find(over,default) is not null)throw new Exception("Duplicate windows cannot bypass the underlying reader's request limit.");
        var large=new CustomCurves.PreviewHistory((add,ct)=>{
            for(int day=0;day<=PreviewDatabase.MaximumObservationWindows;day++)add(window with{To=at.AddDays(day)});
            return Task.CompletedTask;
        },Read);
        var laterInvalid=new CustomCurves.PreviewHistory((add,ct)=>{add(window);throw new TemporalException("schedule_context","Unavailable later ancestor.");},Read);
        if(await large.Find([window],default) is not null||await laterInvalid.Find([window],default) is not null||queries!=1)
            throw new Exception("Oversized graphs and later invalid dates must fall back to ordinary sample selection.");
        var denied=new CustomCurves.PreviewHistory((add,ct)=>throw new PreviewDeniedException(),Read);
        try{await denied.Find([window],default);throw new Exception("Planning swallowed an authority refusal.");}
        catch(PreviewDeniedException){}
        try{await shared.Find([window],new CancellationToken(true));throw new Exception("Cancelled selection used a cached batch.");}
        catch(OperationCanceledException){}
        Console.WriteLine("PASS preview history: shared demand, ordinary fallback, later schedule refusal, authority and cancellation.");
    }
}
