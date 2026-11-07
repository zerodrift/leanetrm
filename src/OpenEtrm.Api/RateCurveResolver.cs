using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

// D359: an admitted rate curve's tenors resolve from the close's own t_0. Its date list is an exact published version whose
// schedules and calendars are pinned versions too, so it resolves on the close's business date with current knowledge, as
// authoring does (SelectedClose.Capture), under a savepoint that leaves the caller's context binding and write state unchanged.
// t_0 is both the date list's context date and the rate engine's valuation UTC date; a close whose two dates differ is refused.
public static class RateCurveResolver
{
    public static async Task<RateCurveDefinition> Resolve(NpgsqlConnection connection,PreviewData data,InputFrame frame,string code,CancellationToken ct)
    {
        if(data.RateDateList(code) is not {} list)return data.RateDefinition(code,null);
        var valuation=DateOnly.FromDateTime(frame.At.UtcDateTime);
        if(PreviewSession.BusinessDay(frame)!=valuation)
            throw new MarketDataException("rate_context",$"This close values on {valuation:yyyy-MM-dd} UTC but belongs to business date {PreviewSession.BusinessDay(frame):yyyy-MM-dd}; choose a close whose dates agree.");
        BoundDateList tenors;
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT rate_curve_tenors"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            var catalog=data.DefinitionRevision.CatalogId;
            var context=await SelectedClose.Capture(connection,catalog,frame,ct);
            var dates=new ContractDateStore(catalog,new ScheduleStore(catalog,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId));
            tenors=await dates.ResolveDateList(connection,new(list.Mapping,list.Version,context),null,includeDrafts:false,ct);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT rate_curve_tenors;RELEASE SAVEPOINT rate_curve_tenors");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        return data.RateDefinition(code,tenors.Dates.ToDictionary(d=>d.Label,d=>d.Date,StringComparer.Ordinal));
    }
}
