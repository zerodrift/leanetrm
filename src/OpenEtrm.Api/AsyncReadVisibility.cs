using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;
using System.Text.Json;

namespace OpenEtrm.Api;

internal sealed record AsyncReadBookScope(Guid Catalog,ImmutableArray<Guid> Ids);
internal sealed record AsyncReadCalendar(ReferenceKey Reference,CalendarPublicationProof Publication);
internal sealed record AsyncReadInstrument(Guid Catalog,Guid Id,Guid? Context,DateTimeOffset? AsOf,int Members);
internal sealed record AsyncReadVisibilityProof(ImmutableArray<PreviewManifest> Bundles,ImmutableArray<PreviewManifest> References,
    ImmutableArray<(Guid Catalog,Guid Revision)> Versions,ImmutableArray<Guid> Catalogs,ImmutableArray<AsyncReadBookScope> Books,
    ImmutableArray<AsyncReadCalendar> Calendars,ImmutableArray<AsyncReadInstrument> Instruments)
{
    internal async Task Require(NpgsqlConnection connection,CancellationToken ct)
    {
        foreach(var scope in Books)
        {
            var rows=await PreviewDatabase.Rows(connection,"SELECT json_build_object('id',id)::text FROM trading.books WHERE catalog_id=$1 AND id=ANY($2::uuid[])",[scope.Catalog,scope.Ids.ToArray()],ct);
            if(!scope.Ids.ToHashSet().SetEquals(rows.Select(row=>row.Id("id"))))throw new PreviewDeniedException();
        }
        foreach(var manifest in Bundles)await MarketBundle.Require(connection,manifest,ct);
        foreach(var manifest in References)await MarketBundle.RequireReferenceVisibility(connection,manifest,ct);
        foreach(var(catalog,revision)in Versions)
        {
            await using var check=ScheduleStore.Command(connection,"SELECT security.reference_access($1,'ReadMarketData') AND security.reference_version_visible($1,$2)",catalog,revision);
            if(!Equals(await check.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        }
        if(!Catalogs.IsEmpty)
        {
            await using var check=ScheduleStore.Command(connection,"SELECT bool_and(security.reference_access(catalog,'ReadMarketData')) FROM unnest($1::uuid[]) catalog",Catalogs.ToArray());
            if(!Equals(await check.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        }
        foreach(var calendar in Calendars)await SideCalendarPolicies.RequirePublication(connection,calendar.Reference,calendar.Publication,ct);
        foreach(var instrument in Instruments)await new RetainedScheduleStore(instrument.Catalog).RequireProgramAuthority(connection,
            instrument.Id,instrument.Context,instrument.AsOf,instrument.Members,ct);
    }
}

// Exact coordinates touched by one asynchronous read are inputs to fresh
// publication checks, never cached authorization decisions. Nested scopes restore
// their parent; parallel worker execution contexts never share this collector.
internal sealed class AsyncReadVisibility:IDisposable
{
    internal const int MaximumCoordinates=512,MaximumBooks=4096,MaximumInstruments=4096;
    static readonly AsyncLocal<AsyncReadVisibility?> current=new();
    readonly AsyncReadVisibility? prior;
    readonly object gate=new();
    readonly Dictionary<(Guid Catalog,Guid Revision,Guid References,Guid ReferenceRevision),PreviewManifest> bundles=[];
    readonly Dictionary<(Guid Catalog,Guid Revision),PreviewManifest> references=[];
    readonly HashSet<Guid> referenceCatalogs=[];
    readonly HashSet<(Guid Catalog,Guid Revision)> referenceVersions=[];
    readonly Dictionary<Guid,HashSet<Guid>> books=[];
    readonly HashSet<AsyncReadCalendar> calendars=[];
    readonly HashSet<AsyncReadInstrument> instruments=[];
    readonly bool marketDates;
    readonly ScheduleContext? rootContext;
    ScheduleContext? marketContext;
    readonly Dictionary<(Guid Catalog,Guid Revision,ScheduleContext? Context),HashSet<Guid>> observationPins=[];
    readonly HashSet<(Guid Catalog,CatalogueSource Source,ScheduleContext? Context)> catalogueSources=[];
    internal AsyncReadVisibility(bool marketDates=false,ScheduleContext? context=null)
    {prior=current.Value;current.Value=this;this.marketDates=marketDates;rootContext=marketContext=context;}
    // The plan run while its exact coordinates are collected: its outcome's recheck then also requires them, in the
    // same fresh publication transaction, whichever transport discloses the result.
    internal static ReadPlan Observed(ReadPlan plan,bool marketDates=false,ScheduleContext? context=null)=>plan with{Execute=async ct=>
    {
        using var visibility=new AsyncReadVisibility(marketDates,context);
        var outcome=await plan.Execute(ct);var evidence=visibility.Seal();var recheck=outcome.Recheck;
        return outcome with{Recheck=async(connection,t)=>
        {await recheck(connection,t);await evidence.Require(connection,t);await visibility.RequireMarketInputs(connection,t);}};
    }};
    // Calendar reads may sample several explicit date contexts. Capture actual
    // returned facts at the read boundary, not at later in-memory projections.
    internal static IDisposable AtMarketContext(ScheduleContext context)
    {
        var scope=current.Value;var previous=scope?.marketContext;
        if(scope is not null)scope.marketContext=context;
        return new Restore(()=>{if(scope is not null)scope.marketContext=previous;});
    }
    sealed class Restore(Action restore):IDisposable{public void Dispose()=>restore();}
    internal static void Observations(Guid catalog,Guid revision,ImmutableArray<JsonElement> rows)
    {
        if(current.Value is not {marketDates:true} scope||rows.IsEmpty)return;
        lock(scope.gate)
        {
            var key=(catalog,revision,scope.marketContext);
            if(!scope.observationPins.TryGetValue(key,out var pins))scope.observationPins.Add(key,pins=[]);
            pins.UnionWith(rows.Select(row=>row.Id("id")));
        }
    }
    internal static void Catalogue(Guid catalog,CataloguePublication publication)
    {
        if(current.Value is not {marketDates:true} scope)return;
        lock(scope.gate)
            foreach(var source in publication.Curves.Select(c=>c.Source).Concat(publication.Roles.Values))
                scope.catalogueSources.Add((catalog,source,scope.marketContext));
    }
    async Task RequireMarketInputs(NpgsqlConnection connection,CancellationToken ct)
    {
        // Storage is local to this read and contains at most the facts actually
        // materialized by it; duplicate samples and frames share their proof.
        var contexts=observationPins.Keys.Select(k=>k.Context).Concat(catalogueSources.Select(k=>k.Context)).Distinct();
        foreach(var context in contexts)
        {
            var temporary=context!=rootContext;
            if(temporary)
                await using(var save=ScheduleStore.Command(connection,"SAVEPOINT calendar_publication"))await save.ExecuteNonQueryAsync(ct);
            try
            {
                if(temporary)
                {
                    if(rootContext is not null||context is null)throw new PreviewDeniedException();
                    var catalog=observationPins.Keys.Where(k=>k.Context==context).Select(k=>k.Catalog)
                        .Concat(catalogueSources.Where(k=>k.Context==context).Select(k=>k.Catalog)).Distinct().Single();
                    await PinnedContext.BindExact(connection,catalog,context,ct);
                }
                foreach(var(catalog,source,_)in catalogueSources.Where(k=>k.Context==context))
                {
                    await using var check=ScheduleStore.Command(connection,"SELECT security.reference_access($1,'ReadMarketData') AND security.reference_version_visible($1,$2) AND security.market_fact_visible($3,$4,NULL)",
                        source.ReferenceCatalogId,source.ReferenceRevisionId,catalog,source.DefinitionRevisionId);
                    if(!Equals(await check.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
                }
                foreach(var(key,pins)in observationPins.Where(p=>p.Key.Context==context))
                {
                    var visible=await PreviewDatabase.Rows(connection,"SELECT json_build_object('id',id)::text FROM security.read_observation_pins($1,$2,$3::uuid[])",[key.Catalog,key.Revision,pins.ToArray()],ct);
                    if(!pins.SetEquals(visible.Select(row=>row.Id("id"))))throw new PreviewDeniedException();
                }
            }
            finally
            {
                if(temporary)
                {
                    await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT calendar_publication;RELEASE SAVEPOINT calendar_publication");
                    await restore.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
        }
    }
    internal static void Bundle(PreviewManifest manifest)=>current.Value?.Capture(manifest,true);
    internal static void Reference(PreviewManifest manifest)=>current.Value?.Capture(manifest,false);
    internal static void Books(Guid catalog,IEnumerable<Guid> ids)
    {
        if(current.Value is not {} scope)return;
        lock(scope.gate)
        {
            if(!scope.books.TryGetValue(catalog,out var selected))scope.books[catalog]=selected=[];
            selected.UnionWith(ids);scope.RequireLimit();
        }
    }
    internal static void ReferenceCatalog(Guid catalog)
    {
        if(current.Value is not {} scope)return;
        lock(scope.gate){scope.referenceCatalogs.Add(catalog);scope.RequireLimit();}
    }
    internal static void ReferenceVersion(Guid catalog,Guid revision)
    {
        if(current.Value is not {} scope)return;
        lock(scope.gate){scope.referenceVersions.Add((catalog,revision));scope.RequireLimit();}
    }
    internal static void Calendar(ReferenceKey reference,CalendarPublicationProof publication)
    {
        if(current.Value is not {} scope)return;
        lock(scope.gate){scope.calendars.Add(new(reference,publication));scope.RequireLimit();}
    }
    internal static void Instrument(Guid catalog,Guid id,Guid? context,DateTimeOffset? asOf,int members)
    {
        if(current.Value is not {} scope)return;
        lock(scope.gate){scope.instruments.Add(new(catalog,id,context,asOf,members));scope.RequireLimit();}
    }
    void RequireLimit()
    {
        if(bundles.Count+references.Count+referenceCatalogs.Count+referenceVersions.Count+calendars.Count>MaximumCoordinates||books.Sum(p=>p.Value.Count)>MaximumBooks||instruments.Count>MaximumInstruments)
            throw new AsyncReadException(422,"async_visibility_limit","The read exceeds the bounded publication evidence limit.");
    }
    void Capture(PreviewManifest manifest,bool bundle)
    {
        lock(gate)
        {
            if(bundle)bundles.TryAdd((manifest.CatalogId,manifest.RevisionId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId),manifest);
            else references.TryAdd((manifest.ReferenceCatalogId,manifest.ReferenceRevisionId),manifest);
            RequireLimit();
        }
    }
    internal AsyncReadVisibilityProof Seal()
    {
        lock(gate)
        {
            ImmutableArray<PreviewManifest> exactBundles=[..bundles.Values];
            var covered=exactBundles.Select(m=>(m.ReferenceCatalogId,m.ReferenceRevisionId)).ToHashSet();
            ImmutableArray<PreviewManifest> exactReferences=[..references.Where(p=>!covered.Contains(p.Key)).Select(p=>p.Value)];
            covered.UnionWith(exactReferences.Select(m=>(m.ReferenceCatalogId,m.ReferenceRevisionId)));
            ImmutableArray<(Guid Catalog,Guid Revision)> versions=[..referenceVersions.Where(v=>!covered.Contains(v))];
            var checkedCatalogs=covered.Select(v=>v.Item1).Concat(versions.Select(v=>v.Catalog)).ToHashSet();
            return new(exactBundles,exactReferences,versions,[..referenceCatalogs.Where(c=>!checkedCatalogs.Contains(c))],
                [..books.Select(p=>new AsyncReadBookScope(p.Key,[..p.Value]))],[..calendars],[..instruments]);
        }
    }
    public void Dispose()=>current.Value=prior;
}
