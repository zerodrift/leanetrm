using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Rates;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// An admitted rate curve's exact pinned date list and schedule versions, loaded once. They are immutable versions, so each close's
// tenors compile from them in memory, with that close's date as the context date.
public sealed record RateTenorSource(PublishedContractRequest Definition,ImmutableArray<ScheduleVersion> Sources,ImmutableArray<ContractDateBinding> Bindings,int KnownThrough);

// D359: an admitted rate curve's tenors resolve from the close's own t_0. The pinned date list loads in the market context the
// request has bound, whose knowledge view decides what is visible; with none bound it loads with current knowledge captured on
// the close's business date, as authoring does (SelectedClose.Capture), under a savepoint that leaves the caller's state
// unchanged. t_0 is both the date list's context date and the rate engine's valuation UTC date; a close whose dates differ is refused.
public static class RateCurveResolver
{
    public static async Task<RateCurveDefinition> Resolve(NpgsqlConnection connection,PreviewData data,InputFrame frame,string code,CancellationToken ct)=>
        At(data,frame,code,await Load(connection,data,frame,code,ct));

    // A curve's pinned sources.
    public static async Task<RateTenorSource> Load(NpgsqlConnection connection,PreviewData data,InputFrame frame,string code,CancellationToken ct)
    {
        var list=data.RateDateList(code);
        var catalog=data.DefinitionRevision.CatalogId;
        var dates=new ContractDateStore(catalog,new ScheduleStore(catalog,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId));
        await using(var bound=ScheduleStore.Command(connection,"SELECT NOT security.current_write_context()"))
            if(Equals(await bound.ExecuteScalarAsync(ct),true))
            {
                var (body,versions,pinned)=await dates.PinnedDateListInView(connection,list.Mapping,list.Version,ct);
                // The bound view already limits what is visible; its newest visible publication is the knowledge the list compiles under.
                return new(body,versions,pinned,versions.Where(v=>v.State=="published").Max(v=>v.PublicationOrder)??0);
            }
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT rate_curve_tenors"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            var context=await SelectedClose.Capture(connection,catalog,frame,ct);
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
    public static RateCurveDefinition At(PreviewData data,InputFrame frame,string code,RateTenorSource source)
    {
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
