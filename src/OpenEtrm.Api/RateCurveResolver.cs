using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Rates;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// An admitted rate curve's exact pinned date list and schedule versions, loaded once. They are immutable versions, so each close's
// tenors compile from them in memory, with that close's date as the context date.
public sealed record RateTenorSource(PublishedContractRequest Definition,ImmutableArray<ScheduleVersion> Sources,ImmutableArray<ContractDateBinding> Bindings,int KnownThrough);

// D359: an admitted rate curve's tenors resolve from the close's own t_0. The pinned date list loads with current knowledge
// captured on a close's business date, as authoring does (SelectedClose.Capture), under a savepoint that leaves the caller's
// context binding and write state unchanged. t_0 is both the date list's context date and the rate engine's valuation UTC date; a
// close whose two dates differ is refused.
public static class RateCurveResolver
{
    public static async Task<RateCurveDefinition> Resolve(NpgsqlConnection connection,PreviewData data,InputFrame frame,string code,CancellationToken ct)=>
        At(data,frame,code,await Load(connection,data,frame,code,ct));

    // The pinned sources of an admitted curve, or none for a literal-maturity curve.
    public static async Task<RateTenorSource?> Load(NpgsqlConnection connection,PreviewData data,InputFrame frame,string code,CancellationToken ct)
    {
        if(data.RateDateList(code) is not {} list)return null;
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT rate_curve_tenors"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            var catalog=data.DefinitionRevision.CatalogId;
            var context=await SelectedClose.Capture(connection,catalog,frame,ct);
            var dates=new ContractDateStore(catalog,new ScheduleStore(catalog,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId));
            var (definition,sources,bindings,retained)=await dates.PinnedDateList(connection,new(list.Mapping,list.Version,context),ct);
            return new(definition,sources,bindings,retained.KnownThrough??throw new MarketDataException("rate_definition","The date list's knowledge view is incomplete."));
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT rate_curve_tenors;RELEASE SAVEPOINT rate_curve_tenors");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    // The curve at one close, in memory.
    public static RateCurveDefinition At(PreviewData data,InputFrame frame,string code,RateTenorSource? source)
    {
        if(source is null)return data.RateDefinition(code,null);
        var valuation=DateOnly.FromDateTime(frame.At.UtcDateTime);
        if(PreviewSession.BusinessDay(frame)!=valuation)
            throw new MarketDataException("rate_context",$"This close values on {valuation:yyyy-MM-dd} UTC but belongs to business date {PreviewSession.BusinessDay(frame):yyyy-MM-dd}; choose a close whose dates agree.");
        try
        {
            var context=new ScheduleContext(valuation.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),source.KnownThrough);
            var tenors=ContractDateStore.CompileDateList(source.Definition,source.Sources,context,source.Bindings);
            return data.RateDefinition(code,tenors.Dates.ToDictionary(d=>d.Label,d=>d.Date,StringComparer.Ordinal));
        }
        // A tenor date that cannot resolve at this close is this curve's refusal there, with its own code, like any rate refusal.
        catch(TemporalException error){throw new MarketDataException(error.Code,error.Message);}
    }
}
