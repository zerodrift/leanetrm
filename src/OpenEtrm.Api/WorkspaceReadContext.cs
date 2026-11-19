using System.Globalization;
using Npgsql;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// A tab owns its applied selection and the coordinates currently displayed by
// its date editor. Back supplies the previous exact coordinates from page memory.
internal sealed class WorkspaceReadContext
{
    internal static readonly AsyncLocal<PreviewSession?> Page=new();
    internal static readonly AsyncLocal<KnowledgeSnapshot?> Executing=new();
    internal KnowledgeSnapshot? Prepared {get;set;}
    internal KnowledgeSnapshot? Applied {get;set;}

    internal KnowledgeSnapshot? Find(Guid id)=>
        Applied?.Context.ContextId==id?Applied:Prepared?.Context.ContextId==id?Prepared:null;

    // Carry only the admitted coordinates into workers. Execution and delivery
    // each bind under fresh database authority, independently of the page.
    internal static ReadPlan Capture(ReadPlan plan,KnowledgeSnapshot? selected)
    {
        if(selected?.Context.ReadCut is null)return plan;
        return plan with{Execute=async ct=>
        {
            var previous=Executing.Value;
            try
            {
                Executing.Value=selected;
                var outcome=await plan.Execute(ct);
                return outcome with{Recheck=async (connection,token)=>
                {
                    var prior=Executing.Value;
                    try{Executing.Value=selected;await outcome.Recheck(connection,token);}
                    finally{Executing.Value=prior;}
                }};
            }
            finally{Executing.Value=previous;}
        }};
    }

    internal Task<KnowledgeSnapshot> Select(NpgsqlConnection connection,Guid catalog,MarketContextRequest request,CancellationToken ct)
    {
        var expected=request.RestoreKey;
        if(expected is null&&request.Mode=="at"&&Prepared is {} prepared&&request.EffectiveOn==prepared.Context.EffectiveOn&&
            request.KnownAt is not null&&SchedulePreview.ParseAt(request.KnownAt)==SchedulePreview.ParseAt(prepared.Context.KnownAt))
            expected=prepared.RestoreKey;
        return Resolve(connection,catalog,new(request.EffectiveOn,request.KnownAt,request.Mode),ct,expected);
    }

    internal static async Task<KnowledgeSnapshot> Resolve(NpgsqlConnection connection,Guid catalog,ContextCaptureRequest request,CancellationToken ct,string? restoreKey=null)
    {
        var day=DateWorkspace.Date(request.EffectiveOn);
        DateTimeOffset? requested=request.Mode switch
        {
            "current" when request.KnownAt is null=>null,
            "at" when request.KnownAt is not null=>SchedulePreview.ParseAt(request.KnownAt),
            "end_of_day" when request.KnownAt is null=>UserTime.EndOfDay(day,request.TimeZone),
            _=>throw new TemporalException("temporal_context","Select an explicit knowledge policy and business date.")
        };
        if(requested is {} value&&value.Ticks%10!=0)throw new TemporalException("temporal_precision","Choose a cutoff with microsecond precision.");
        await using var command=ScheduleStore.Command(connection,"SELECT * FROM security.workspace_read_cut($1,$2,$3::timestamptz,$4)",catalog,day,(object?)requested??DBNull.Value,request.Mode=="end_of_day");
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))throw new TemporalException("knowledge_coverage","The selected cutoff is unavailable.");
        var cut=new CalendarReadCut(catalog,reader.GetFieldValue<DateTimeOffset>(1),reader.GetInt32(2),reader.GetString(5),reader.GetInt32(6));
        var selected=new ScheduleContext(day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),cut.SchedulePrefix,cut.KnownAt.ToString("O",CultureInfo.InvariantCulture),reader.GetGuid(0))
            {ReadCut=reader.GetBoolean(7)?null:cut,WorkspaceCut=!reader.GetBoolean(7)};
        var coordinates=FormattableString.Invariant($"{catalog:D}/{selected.ContextId:D}/{cut.SystemId}/{cut.Timeline}/{cut.SchedulePrefix}/{cut.KnownAt.UtcTicks}/{day.DayNumber}");
        var key=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(coordinates)));
        if(restoreKey is not null&&key!=restoreKey)throw new TemporalException("knowledge_coverage","The earlier workspace selection is outside its original certified history.");
        return new(selected,reader.GetFieldValue<DateTimeOffset>(3),reader.GetFieldValue<DateTimeOffset>(4)){RestoreKey=key};
    }
}
