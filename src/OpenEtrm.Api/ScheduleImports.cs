using System.Collections.Immutable;
using OpenEtrm.Shared.Temporal;
using Npgsql;

namespace OpenEtrm.Api;

public sealed record ScheduleImportRequest(string Name,string Symbol,string[] Dates,CalendarReference? Calendar,
    string Selection,string EffectiveFrom,string EffectiveTo);

// Set-union import: replaying a file or overlapping batches does not add versions.
// Corrections use the existing explicit version editor, never implicit replacement.
public static class ScheduleImports
{
    public static ImmutableList<string> Merge(IEnumerable<string> existing,IEnumerable<string> incoming)=>
        existing.Concat(incoming).Select(DateWorkspace.Date).Distinct().Order()
            .Select(d=>d.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture)).ToImmutableList();

    public static async Task<ScheduleVersion> Import(NpgsqlConnection connection,ScheduleStore store,ScheduleImportRequest body,CancellationToken ct)
    {
        if(body.Dates is null||body.Dates.Length is <1 or >2000)throw new TemporalException("schedule_definition","Import 1–2,000 date entries at a time.");
        await using(var gate=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849103)"))await gate.ExecuteNonQueryAsync(ct);
        var versions=await store.Read(connection,ct);
        var old=versions.Where(v=>v.Definition.Symbol==body.Symbol).OrderByDescending(v=>v.Version).FirstOrDefault();
        if(old is not null&&(old.Definition.Name!=body.Name||old.Selection!=body.Selection||old.Calendar!=body.Calendar||
            old.EffectiveFrom!=DateWorkspace.Date(body.EffectiveFrom)||old.EffectiveTo!=DateWorkspace.Date(body.EffectiveTo)))
            throw new TemporalException("schedule_conflict","The existing schedule has different settings. Open it to review changes before importing.");
        if(old is {Selection:"context_month",Definition.Dates:null})throw new TemporalException("schedule_conflict","This generated schedule retains anchor-month semantics. Import dates into a separate schedule with an explicit selection rule.");
        var previous=old is null?[]:PublishedSchedules.Compile(old).Dates.Select(d=>d.Date.ToString("yyyy-MM-dd")).ToArray();
        var dates=Merge(previous,body.Dates);
        if(old is not null&&dates.SequenceEqual(previous))return old;
        var definition=new MonthlyDraft(body.Name,body.Symbol,null,null,null,null,null,null,null,dates);
        return await store.Save(connection,new(old?.Id,old?.Version,old?.Generation,definition,body.Selection,body.EffectiveFrom,body.EffectiveTo,old?.State,body.Calendar),ct);
    }
}
