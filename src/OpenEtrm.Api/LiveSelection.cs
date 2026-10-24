using System.Globalization;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record LiveSelectRequest(Guid SnapshotId,string Policy);
public sealed record LivePauseRequest(Guid Selection,Guid SnapshotId);
public sealed record LiveSelection
{
    public const string Corrections="selected_facts_latest_committed_corrections_v1";
    public Guid Id {get;private init;}
    public long Generation {get;private init;}
    public InputFrame Seed {get;private init;}=null!;
    public InputFrame? Frame {get;private init;}
    public ScheduleContext? Context {get;private init;}
    internal string? Head {get;private init;}
    private LiveSelection(){}
    public static LiveSelection Start(InputFrame seed,string policy)
    {
        if(policy!=Corrections||seed.ContextId is not null)throw Invalid();
        return new(){Id=Guid.NewGuid(),Seed=seed};
    }
    public LiveSelection Resolve(ScheduleContext context,InputFrame frame,string head)
    {
        PinnedContext.Exact(context);
        if(frame.At!=Seed.At||frame.ContextId!=context.ContextId||!frame.ResolvesLatestCorrection||
            context.EffectiveOn!=Seed.At.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)||string.IsNullOrEmpty(head)||
            frame.CurveContracts.Count!=Seed.CurveContracts.Count||Seed.CurveContracts.Any(p=>!frame.CurveContracts.TryGetValue(p.Key,out var values)||!values.SequenceEqual(p.Value)))throw Invalid();
        return this with{Frame=frame,Context=context,Head=head,Generation=checked(Generation+1)};
    }
    public object Summary()=>new{Id,Generation,policy=Corrections};
    private static MarketDataException Invalid()=>new("live_selection","Select an explicit working snapshot and the supported live correction policy. Effective date, observation times, sources and contracts remain fixed.");
}
