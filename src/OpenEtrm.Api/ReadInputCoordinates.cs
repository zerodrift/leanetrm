using System.Globalization;
using Npgsql;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// Input evidence owned by a result or command, never a reusable context object.
internal sealed record ReadInputCoordinates(DateOnly BusinessOn,DateTimeOffset KnownAt,string SystemId,int Timeline)
{
    internal static ReadInputCoordinates From(ScheduleContext source)=>source.ReadCut is {} cut
        ?new(PublishedSchedules.ContextDate(source),cut.KnownAt,cut.SystemId,cut.Timeline)
        :throw new TemporalException("temporal_context","The result requires complete certified input coordinates.");

    internal Guid Correlation(Guid catalog)=>new CalendarReadCut(catalog,KnownAt,0,SystemId,Timeline).On(BusinessOn).ContextId!.Value;

    internal async Task<ScheduleContext> Resolve(NpgsqlConnection connection,Guid catalog,CancellationToken ct)=>
        (await ResolveSnapshot(connection,catalog,ct)).Context;

    internal async Task<KnowledgeSnapshot> ResolveSnapshot(NpgsqlConnection connection,Guid catalog,CancellationToken ct)
    {
        var resolved=await new KnowledgeResolver(catalog).Source(connection,new(BusinessOn.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            KnownAt.ToString("O",CultureInfo.InvariantCulture),"at"),ct);
        var source=resolved.Context;
        if(source.ReadCut is not {} cut||cut.SystemId!=SystemId||cut.Timeline!=Timeline||source.ContextId!=Correlation(catalog))
            throw new TemporalException("knowledge_coverage","The original input clock lineage is unavailable.");
        return resolved;
    }
}
