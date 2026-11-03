using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Risk;

public sealed record RiskHistoryDayWindow(DateOnly Date,DateTimeOffset Start,int StartOffsetSeconds,DateTimeOffset End,int EndOffsetSeconds);
public sealed record RiskHistoryPriceType(string Code,string Label,ObservationRole Role,PublicationState State);
public sealed record RiskHistoryPriceChoice(string? SelectedType,string OfficialCloseStatus,ImmutableArray<string> AvailableTypes);
public sealed record RiskHistoryDayPrices(DateTimeOffset At,ImmutableArray<RiskHistoryPriceChoice> Choices);

public static partial class FactorHistory
{
    // Classification is domain role + publication state, never a free-text source label.
    public static readonly ImmutableArray<RiskHistoryPriceType> PriceTypes=[
        new("official_close_v1","Official daily close",ObservationRole.DailySettlement,PublicationState.Final),
        new("trader_mark_final_v1","Final trader mark",ObservationRole.TraderMark,PublicationState.Final),
        new("index_fixing_final_v1","Final index fixing",ObservationRole.IndexFixing,PublicationState.Final),
        new("parameter_final_v1","Final parameter",ObservationRole.Parameter,PublicationState.Final),
        new("contract_settlement_final_v1","Final contract settlement",ObservationRole.FinalContractSettlement,PublicationState.Final),
        new("trader_mark_preliminary_v1","Preliminary trader mark",ObservationRole.TraderMark,PublicationState.Preliminary),
        new("settlement_preliminary_v1","Preliminary daily settlement",ObservationRole.DailySettlement,PublicationState.Preliminary)];

    private static RiskHistoryClock CaptureDayClock(RiskHistoryCalendar calendar,ReferenceKey reference,PowerClock clock,string ambiguous,string invalid)
    {
        if(reference.Revision!=calendar.Reference.Revision||clock is null||ambiguous!="reject"||invalid!="reject")
            throw Error("risk_history_calendar","Choose an available business calendar for end-of-day history.");
        var windows=calendar.BusinessDates.Order().Select(d=>{
            var start=clock.StartOfDay(d);var end=clock.StartOfDay(d.AddDays(1));
            return new RiskHistoryDayWindow(d,start.ToUniversalTime(),checked((int)start.Offset.TotalSeconds),end.ToUniversalTime(),checked((int)end.Offset.TotalSeconds));
        }).ToImmutableArray();
        return new(reference,clock.Rules,clock.Fingerprint,RiskHistoryClock.EndOfDay,ambiguous,invalid,RiskHistoryClock.DayEnds,
            [..windows.Select(w=>new RiskHistoryResolvedClose(w.Date,w.End,w.EndOffsetSeconds))],windows);
    }
    private static ImmutableArray<RiskHistorySession> DaySessions(RiskHistoryCalendar calendar,RiskHistoryClock clock,RiskHistoryForecast forecast)
    {
        RequireCalendar(calendar);
        if(clock is null||clock.Resolution!=RiskHistoryClock.DayEnds||clock.CloseTime!=RiskHistoryClock.EndOfDay||clock.AmbiguousTime!="reject"||clock.InvalidTime!="reject"||
            clock.Reference.Revision!=calendar.Reference.Revision||string.IsNullOrWhiteSpace(clock.Rules)||clock.Rules.Length>65536||!Hash(clock.Fingerprint)||
            clock.Fingerprint!=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(clock.Rules)))||
            clock.DayWindows.IsDefault||clock.ResolvedCloses.IsDefault||clock.DayWindows.Length!=calendar.BusinessDates.Length||
            !clock.DayWindows.Select(w=>w.Date).SequenceEqual(calendar.BusinessDates.Order())||clock.ResolvedCloses.Length!=clock.DayWindows.Length)
            throw Error("risk_history_calendar","Supply the captured end-of-day windows and pinned calendar clock evidence.");
        Money.RequireKey(clock.Reference);
        bool Boundary(DateOnly date,DateTimeOffset at,int offset)=>Utc(at)&&offset%60==0&&offset is >=-50400 and <=50400&&
            new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Unspecified),TimeSpan.FromSeconds(offset)).ToUniversalTime()==at;
        for(var i=0;i<clock.DayWindows.Length;i++){
            var w=clock.DayWindows[i];var close=clock.ResolvedCloses[i];
            if(!Boundary(w.Date,w.Start,w.StartOffsetSeconds)||!Boundary(w.Date.AddDays(1),w.End,w.EndOffsetSeconds)||w.Start>=w.End||
                close.Date!=w.Date||close.Instant!=w.End||close.OffsetSeconds!=w.EndOffsetSeconds||i>0&&clock.DayWindows[i-1].End>w.Start)
                throw Error("risk_history_calendar","Each end-of-day window must retain both exact local-midnight boundaries and offsets.");
        }
        var completed=clock.DayWindows.Where(w=>w.End<=forecast.MarketCutoff&&w.End<=forecast.ValuationAt).Select(w=>new RiskHistorySession(w.Date,w.End)).ToImmutableArray();
        if(completed.Length<2)throw Error("risk_history_calendar","Choose history containing at least two completed business days before the selected market close.");
        return completed;
    }
    private static RiskHistoryMark? MarkDay(RiskHistoryColumn column,RiskHistoryDayWindow window,RiskHistoryPolicy policy,
        Dictionary<ObservationKey,RiskHistoryObservation> visible,Dictionary<(SeriesKey,ReferenceKey),RiskHistoryObservation[]> dayRows,HashSet<ObservationKey> superseded,out string? reason,out RiskHistoryPriceChoice choice,CancellationToken ct)
    {
        var f=column.Mapping.Factor;
        var timeline=dayRows.GetValueOrDefault((f.Input.Series,f.Source))??[];
        int Lower(DateTimeOffset at){var lo=0;var hi=timeline.Length;while(lo<hi){var mid=lo+(hi-lo)/2;if(timeline[mid].Observation.ObservedAt<at)lo=mid+1;else hi=mid;}return lo;}
        var rows=timeline[Lower(window.Start)..Lower(window.End)];
        (RiskHistoryObservation? Row,string? Error) Candidate(RiskHistoryPriceType type){
            var sameRole=rows.Where(r=>r.Observation.Role==type.Role).ToArray();
            if(sameRole.Length==0)return(null,"missing_mark");
            var latest=sameRole.Max(r=>r.Observation.ObservedAt);var heads=sameRole.Where(r=>r.Observation.ObservedAt==latest&&!superseded.Contains(r.Observation.Key)).ToArray();
            if(heads.Length!=1)return(null,"ambiguous_revision");
            var row=heads[0];
            for(var current=row;;current=visible[current.Revises!.Value]){
                ct.ThrowIfCancellationRequested();
                if(current.Quote!=f.Quote||current.Unit!=f.Unit||current.Contract!=f.Contract)return(null,"mapping_regime");
                if(current.Revises is null)break;
            }
            return row.Observation.State==type.State?(row,null):(null,"publication_state");
        }
        var candidates=PriceTypes.ToDictionary(t=>t.Code,Candidate,StringComparer.Ordinal);
        choice=new(null,candidates["official_close_v1"].Error??"available",[..PriceTypes.Where(t=>candidates[t.Code].Row is not null).Select(t=>t.Code)]);
        reason="price_type_unavailable";
        foreach(var code in policy.PriceTypes){
            var (row,error)=candidates[code];
            if(error is "ambiguous_revision" or "mapping_regime"){reason=error;return null;}
            if(row is null)continue;
            var lineage=ImmutableArray.CreateBuilder<ObservationKey>();
            for(var current=row;;current=visible[current.Revises!.Value]){ct.ThrowIfCancellationRequested();lineage.Add(current.Observation.Key);if(current.Revises is null)break;}
            lineage.Reverse();choice=choice with{SelectedType=code};reason=null;
            return new(row.Observation.Key,row.Observation.Value,row.KnowledgeOrder!.Value,row.KnownAt!.Value,lineage.ToImmutable());
        }
        return null;
    }
}
