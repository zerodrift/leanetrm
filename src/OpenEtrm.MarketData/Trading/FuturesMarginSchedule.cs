using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// Selection over an already adopted dated schedule. Rational execution offsets
// let lattice nodes use the same saved occurrences without inventing event times.
public static class FuturesMarginSchedule
{
    public static ImmutableArray<FuturesMarginOccurrence> Eligible(FuturesMarginPolicy policy,DateTimeOffset executedAt)
    {
        if(!TradingInstant.IsSupported(executedAt))throw Error();
        var schedule=Schedule(policy);
        if(executedAt<policy.From||executedAt>=policy.Until)throw Error();
        return [..schedule.Where(o=>After(o.At.CompareTo(executedAt),policy.Opening))];
    }
    public static FuturesMarginOccurrence NextAfterExecution(FuturesMarginPolicy policy,DateTimeOffset origin,SignedRatio executionOffsetTicks)
    {
        if(!TradingInstant.IsSupported(origin)||executionOffsetTicks.Denominator.Sign<=0)throw Error();
        var schedule=Schedule(policy);
        int Compare(DateTimeOffset at)=>new SignedRatio(at.UtcTicks-origin.UtcTicks,1).CompareTo(executionOffsetTicks);
        if(Compare(policy.From)>0||Compare(policy.Until)<=0)throw Error();
        return schedule.FirstOrDefault(o=>After(Compare(o.At),policy.Opening))??throw Error();
    }
    public static FuturesMarginOccurrence? NextUnsettled(FuturesMarginPolicy policy,DateTimeOffset executedAt,
        ImmutableArray<Guid> acceptedOccurrenceIds,DateTimeOffset at)
    {
        if(!TradingInstant.IsSupported(at)||!TradingInstant.IsSupported(executedAt)||at<executedAt||acceptedOccurrenceIds.IsDefault)throw Error();
        var eligible=Eligible(policy,executedAt);
        if(acceptedOccurrenceIds.Length>eligible.Length||
            !acceptedOccurrenceIds.SequenceEqual(eligible.Take(acceptedOccurrenceIds.Length).Select(o=>o.Id))||
            eligible.Take(acceptedOccurrenceIds.Length).Any(o=>o.At>at))throw Error();
        if(acceptedOccurrenceIds.Length==eligible.Length)
        {
            if(eligible.IsEmpty||eligible[^1].Phase!=FuturesMarginPhase.Final)throw Error();
            return null;
        }
        var next=eligible[acceptedOccurrenceIds.Length];
        var pendingInception=acceptedOccurrenceIds.IsEmpty&&at==executedAt&&next.At==at&&policy.Opening==FuturesMarginOpening.IncludeOpeningInstant;
        if(next.At<=at&&!pendingInception)throw Error(); // Missing actual events cannot become hypothetical margin.
        return next;
    }
    private static bool After(int comparison,FuturesMarginOpening opening)=>comparison>0||comparison==0&&opening==FuturesMarginOpening.IncludeOpeningInstant;
    private static ImmutableArray<FuturesMarginOccurrence> Schedule(FuturesMarginPolicy policy)
    {
        if(policy is null||policy.Id==Guid.Empty||policy.Revision<1||policy.Implementation is not(FuturesMarginPolicy.Version or FuturesMarginPolicy.DeliveryVersion)||
            !Enum.IsDefined(policy.Opening)||!TradingInstant.IsSupported(policy.From)||!TradingInstant.IsSupported(policy.Until)||policy.From>=policy.Until||
            policy.Occurrences.IsDefaultOrEmpty||policy.Occurrences.Any(o=>o is null||o.Id==Guid.Empty||!Enum.IsDefined(o.Phase)||
                !TradingInstant.IsSupported(o.At)||!TradingInstant.IsSupported(o.DueAt)||o.At<policy.From||o.At>=policy.Until||o.DueAt<o.At)||
            policy.Occurrences.Select(o=>o.Id).Distinct().Count()!=policy.Occurrences.Length||
            policy.Occurrences.Select(o=>(o.At,o.Phase)).Distinct().Count()!=policy.Occurrences.Length||
            policy.Occurrences.Count(o=>o.Phase==FuturesMarginPhase.Final)!=1)throw Error();
        var ordered=policy.Occurrences.OrderBy(o=>o.At).ThenBy(o=>o.Phase).ToImmutableArray();
        if(ordered[^1].Phase!=FuturesMarginPhase.Final)throw Error();
        return ordered;
    }
    private static MarketDataException Error()=>new("futures_margin_schedule","Select the complete exact dated margin schedule, execution opening convention and accepted causal prefix; no required occurrence or due may be inferred or skipped.");
}
