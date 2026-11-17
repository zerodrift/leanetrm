using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// The read clock is shared by two operating consumers. Authorization remains
// operation-specific and must be captured by the caller before resolving data.
internal sealed class OperationalBusinessContext(PreviewDatabase database)
{
    internal const string Convention="facility_business_date_end_utc_v1";
    internal static (DateTimeOffset At,bool Working) Select(FacilityQuantityContext request,ScheduleContext? selected,string invalidCode)
    {
        if(request is null||request.Convention!=Convention)
            throw new MarketDataException(invalidCode,"Select the explicit installed operating business-date read clock.");
        var day=DateWorkspace.Date(request.EffectiveOn);
        var working=request.KnowledgeMode=="current"&&request.Retained is null&&selected is null;
        if(!working&&!(request.KnowledgeMode=="retained"&&request.Retained is {} retained&&selected is not null&&
            retained.ContextId==selected.ContextId&&retained.EffectiveOn==request.EffectiveOn))
            throw new MarketDataException("temporal_context","Use the selected working context or the exact historical context applied in the header.");
        return(UserTime.EndOfDay(day,"UTC"),working);
    }
    internal async Task<(ScheduleContext Context,DateTimeOffset At,bool Working)> Resolve(NpgsqlConnection c,
        FacilityQuantityContext request,ScheduleContext? selected,string invalidCode,CancellationToken ct)
    {
        var selection=Select(request,selected,invalidCode);var store=new KnowledgeStore(database.Manifest.CatalogId);
        var context=selection.Working?(await store.Capture(c,new(request.EffectiveOn,null,"current"),ct)).Context:
            await store.Retained(c,request.Retained!,ct);
        return(context,selection.At,selection.Working);
    }
    internal async Task BindResolved(NpgsqlConnection c,(ScheduleContext Context,DateTimeOffset At,bool Working) scope,CancellationToken ct)
    {
        await PinnedContext.BindExact(c,database.Manifest.CatalogId,scope.Context,ct);
        await using var bind=ScheduleStore.Command(c,"SELECT security.use_trading_context($1,$2)",database.Manifest.CatalogId,scope.At);
        await bind.ExecuteNonQueryAsync(ct);
    }
}
