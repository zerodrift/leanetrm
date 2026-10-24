using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class LiveContextChecks
{
    public static void Run()
    {
        var frame=new InputFrame(Guid.NewGuid(),"Synthetic close",DateTimeOffset.Parse("2026-01-02T16:00:00Z"),[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty);
        var live=LiveSelection.Start(frame,LiveSelection.Corrections);
        var context=new ScheduleContext("2026-01-02",0,"2026-01-03T00:00:00Z",Guid.NewGuid());
        var resolved=frame with{Id=Guid.NewGuid(),ContextId=context.ContextId,ResolvesLatestCorrection=true};
        var next=live.Resolve(context,resolved,"synthetic-head");
        if(next.Id!=live.Id||next.Generation!=1||next.Seed!=frame||next.Frame!=resolved||live.Frame is not null)
            throw new Exception("Live selection must advance immutably while preserving its exact seed.");
        void Reject(Action action){try{action();}catch(MarketDataException e)when(e.Code=="live_selection"){return;}throw new Exception("Expected explicit live selection rejection.");}
        Reject(()=>LiveSelection.Start(frame,"latest"));
        Reject(()=>LiveSelection.Start(frame with{ContextId=context.ContextId},LiveSelection.Corrections));
        Reject(()=>live.Resolve(context,resolved with{At=frame.At.AddDays(1)},"synthetic-head"));
        Reject(()=>live.Resolve(context,resolved with{ContextId=Guid.NewGuid()},"synthetic-head"));
        Reject(()=>live.Resolve(context with{EffectiveOn="2026-01-03"},resolved,"synthetic-head"));
        Console.WriteLine("PASS live context: explicit correction policy, immutable generations and fixed date coordinates");
        // A hosted sign-in session revoked elsewhere ends the stream at its next read, as a refused read does (docs/ai-assistant.md §11).
        if(!LiveContexts.Ends(new PreviewSessionEnded())||!LiveContexts.Ends(new PreviewDeniedException())||LiveContexts.Ends(new InvalidOperationException()))
            throw new Exception("A started live stream must end quietly when its session ends or its access is refused, and only then.");
        Console.WriteLine("PASS live context: a started stream ends quietly when its session ends or a read is refused, and not on an unexpected failure");
    }
}
