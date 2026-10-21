using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record CurveName(string Code,string Name);
public sealed record WorkspaceRead(PreviewData Data,UnitCatalogData? Units,ImmutableArray<CurveName>? CurveNames,
    ReferenceDisplayData? ReferenceDisplay=null,ReferenceDisplayData? UnitReferenceDisplay=null);
public sealed record PriceRead(object Result,ImmutableArray<JsonElement> Observations,WorkspaceRead? Workspace=null);
public sealed record HistorySample(DateTimeOffset At,string? Value,Guid SnapshotId,Guid[] ObservationIds,string? UnavailableReason);

// Market and reference reads callable without an HTTP request or workspace session. Each
// runs in a transaction already bound to an issued request context and binds its own pin,
// so a transaction runs exactly one read.
public sealed class MarketReads(PreviewDatabase database)
{
    public async Task<PreviewData> Data(NpgsqlConnection connection,PinnedContext pin,CancellationToken ct,
        ImmutableArray<Guid>? observationIds=null,ReferenceReadScope referenceScope=ReferenceReadScope.DefinitionClosure,bool includeCalendarDays=false)
    {
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        return await database.Read(connection,ct,observationIds:observationIds,referenceScope:referenceScope,includeCalendarDays:includeCalendarDays);
    }

    public async Task<WorkspaceRead> Workspace(NpgsqlConnection connection,PinnedContext pin,bool catalogOnly,CancellationToken ct)
    {
        var data=await Data(connection,pin,ct,catalogOnly?ImmutableArray<Guid>.Empty:null,catalogOnly?ReferenceReadScope.DefinitionClosure:ReferenceReadScope.AllVisible);
        return await WorkspaceMetadata(connection,data,catalogOnly,ct);
    }

    async Task<WorkspaceRead> WorkspaceMetadata(NpgsqlConnection connection,PreviewData data,bool catalogOnly,CancellationToken ct)
    {
        UnitCatalogData? units;
        try { units=await database.ReadUnitCatalog(connection,data,ct); }
        catch(MarketDataException e) when(e.Code=="unit_catalog") { units=null; }
        var display=await ReferenceEditors.Display(connection,data.ReferenceRevision,ct);
        var unitDisplay=units is null?null:units.Revision==data.ReferenceRevision?display:await ReferenceEditors.Display(connection,units.Revision,ct);
        return new(data,units,catalogOnly?await Names(connection,ct):null,display,unitDisplay);
    }

    public async Task<NavigationRead> Navigation(NpgsqlConnection connection,PinnedContext pin,CancellationToken ct)
    {
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        var pins=await MarketBundle.Require(connection,database.Manifest,ct);
        var curves=await MarketNavigation.ReadCurves(connection,database.Manifest,ct);
        var names=await Names(connection,ct);
        await MarketBundle.RequireReferenceVisibility(connection,database.Manifest,ct);
        var display=await ReferenceEditors.Display(connection,pins.References,ct);
        if(display is not null && display.References.TryGetValue("commodities",out var labels))
        {
            var namesByCode=labels.Where(r=>r.TryGetProperty("name",out _)).ToDictionary(r=>r.Text("code"),r=>r.Text("name"));
            curves=[..curves.Select(c=>c.Category is {} category&&namesByCode.TryGetValue(category.Code,out var name)?c with{Category=category with{Name=name}}:c)];
        }
        return new(pins.Definitions,pins.References,curves,names,display);
    }

    // Stored display names for every curve in this catalog, for screens that show curve codes from any bundle.
    public async Task<ImmutableArray<CurveName>> CurveNames(NpgsqlConnection connection,PinnedContext pin,CancellationToken ct)
    {
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        return await Names(connection,ct);
    }

    public async Task<ReferencePage> References(NpgsqlConnection connection,PinnedContext pin,string table,int offset,string? search,CancellationToken ct)
    {
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        return await database.ReadReferencePage(connection,table,offset,search,ct);
    }

    public async Task<PriceRead> Evaluate(NpgsqlConnection connection,PinnedContext pin,string curve,CancellationToken ct,bool includeWorkspace=false)
    {
        var frame=pin.RequireFrame();
        if(pin.Context is {} selected && DateOnly.FromDateTime(frame.At.UtcDateTime)!=DateWorkspace.Date(selected.EffectiveOn))
            throw new MarketDataException("market_date","Choose inputs for the selected context date. No earlier market date is substituted.");
        var data=await Data(connection,pin,ct,frame.ObservationIds);
        var result=data.Result(frame,curve);
        return new(result,data.Observations,includeWorkspace?await WorkspaceMetadata(connection,data,true,ct):null);
    }

    // Rebuilds the given frames of the pinned view; the pinned frame replaces the frame at its instant.
    public async Task<ImmutableArray<HistorySample>> History(NpgsqlConnection connection,PinnedContext pin,ImmutableArray<InputFrame> frames,
        string curve,Guid? contractId,Guid? seriesId,CancellationToken ct)
    {
        var current=pin.RequireFrame();
        var selected=frames.Select(pin.Require).Where(frame=>frame.At!=current.At).Append(current).OrderBy(frame=>frame.At).ThenBy(frame=>frame.Id).ToArray();
        var data=await Data(connection,pin,ct);
        return [..selected.Select(f=>
        {
            try {
                var value=data.HistoryValue(f,curve,contractId,seriesId);
                return new HistorySample(f.At,value.Value,f.Id,value.Inputs,null);
            }
            catch(MarketDataException error) when(error.Code is "missing_observation" or "source_expired")
            { return new HistorySample(f.At,null,f.Id,[],error.Code); }
        })];
    }

    async Task<ImmutableArray<CurveName>> Names(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT code,name FROM market_data.curves WHERE catalog_id=$1 UNION SELECT code,name FROM market_data.rate_curves WHERE catalog_id=$1 ORDER BY 1,2",database.Manifest.CatalogId);
        await using var reader=await command.ExecuteReaderAsync(ct);var rows=ImmutableArray.CreateBuilder<CurveName>();
        while(await reader.ReadAsync(ct))rows.Add(new(reader.GetString(0),reader.GetString(1)));
        return rows.ToImmutable();
    }
}
