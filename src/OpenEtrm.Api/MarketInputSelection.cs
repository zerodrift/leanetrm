using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Resolves pricing inputs independently of catalogue choice and retained terms.
// A source is an explicit input selection, never another snapshot found by code.
internal static class MarketInputSelection
{
    internal static ImmutableArray<(PreviewData Data,InputFrame Frame)> WithRoot(PreviewData data,InputFrame frame,
        ImmutableArray<(PreviewData Data,InputFrame Frame)> supplemental)=>
        [(data,frame),..supplemental.IsDefault?[]:supplemental];

    internal sealed record CatalogueTerms(CatalogueSource Source,ImmutableArray<CatalogueCurve> Members,PreviewData Data);
    internal static async Task<ImmutableArray<CatalogueTerms>> ReadCatalogueTerms(PreviewDatabase database,NpgsqlConnection connection,CancellationToken ct,DateOnly? effectiveOn=null)
    {
        var catalogue=await new CatalogueStore(database.Manifest.CatalogId).ReadBound(connection,ct,effectiveOn);
        var result=ImmutableArray.CreateBuilder<CatalogueTerms>();
        foreach(var source in catalogue.Curves.GroupBy(c=>c.Source).OrderBy(g=>g.Key.DefinitionRevisionId))
            result.Add(new(source.Key,[..source],await database.Read(connection,ct,observationIds:[],includeCalendarDays:false,ppaBundle:source.Key)));
        return result.ToImmutable();
    }

    internal static async Task<CatalogueSource> ExactSource(PreviewDatabase database,NpgsqlConnection connection,Guid revision,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"SELECT row_to_json(r)::text FROM market_data.revisions r WHERE(r.catalog_id,r.id)=($1,$2) AND r.state='sealed' AND security.reference_access(r.reference_catalog_id,'ReadMarketData')",
            [database.Manifest.CatalogId,revision],ct);
        if(rows.Length!=1)throw new PreviewDeniedException();
        var source=new CatalogueSource(revision,rows[0].Id("reference_catalog_id"),rows[0].Id("reference_revision_id"));
        await MarketBundle.RequireReferenceVisibility(connection,source.Apply(database.Manifest),ct);
        return source;
    }

    internal static void RequireTerms(PreviewData data,Guid revision)
    {
        if(revision==Guid.Empty||data.DefinitionRevision.RevisionId!=revision)
            throw Invalid("The operation requires the exact definition version supplied with its selected terms.");
    }

    internal static async Task<PreviewData> ReadTerms(PreviewDatabase database,NpgsqlConnection connection,Guid revision,CancellationToken ct)
    {
        if(revision==Guid.Empty)throw Invalid("Choose the exact definition version supplied with the trade terms.");
        var source=await ExactSource(database,connection,revision,ct);
        return await database.Read(connection,ct,ppaBundle:source,observationIds:[],includeCalendarDays:false);
    }

    internal static async Task<(PreviewData Data,InputFrame Frame)> DefinitionInput(PreviewDatabase database,NpgsqlConnection connection,
        PreviewData root,InputFrame frame,Guid revision,CancellationToken ct)
    {
        if(revision==Guid.Empty)throw Invalid("Choose the exact definition version supplied with the trade terms.");
        return await Resolve(database,connection,frame,await ExactSource(database,connection,revision,ct),[(root,frame)],false,ct);
    }

    internal static async Task<PreviewData> Root(PreviewDatabase database,NpgsqlConnection connection,InputFrame frame,CancellationToken ct,
        ImmutableArray<Guid>? observationIds=null,ReferenceReadScope referenceScope=ReferenceReadScope.DefinitionClosure,
        ObservationReadScope observationScope=ObservationReadScope.ExactPins,bool includeCalendarDays=true,MarketInputReadScope? reads=null)
    {
        var exact=await RootSource(database,connection,frame,ct);
        return reads is null?await database.Read(connection,ct,ppaBundle:exact,observationIds:observationIds??frame.ObservationIds,
            referenceScope:referenceScope,observationScope:observationScope,includeCalendarDays:includeCalendarDays):
            await reads.Read(frame,exact,observationIds??frame.ObservationIds,referenceScope,observationScope,includeCalendarDays,ct);
    }

    // Resolve identity only. Callers authorize the returned native bundle before
    // reading or returning any market data.
    internal static async Task<CatalogueSource> RootSource(PreviewDatabase database,NpgsqlConnection connection,InputFrame frame,CancellationToken ct)
    {
        var exact=frame.Source;
        if(frame.OriginCloseId is {} id)
        {
            var recorded=await MarketCloses.Frame(connection,database.Manifest.CatalogId,id,frame.ContextId,ct);
            if(frame.Source is null||frame.SourceInputs is null||recorded.SourceInputs is null||frame.SourceInputs.Count!=recorded.SourceInputs.Count||
                frame.SourceInputs.Any(p=>!recorded.SourceInputs.TryGetValue(p.Key,out var native)||!native.Matches(p.Value)))
                throw Invalid("The frame does not carry its recorded exact source matrix.");
            if(recorded.Time!=frame.Time||exact is null||recorded.SourceInputs.GetValueOrDefault(exact.DefinitionRevisionId) is not {} part||
                part.Source!=exact||!part.Matches(frame))
                throw Invalid("The frame differs from its recorded exact source inputs.");
        }
        if(exact is null)
        {
            // Follow the selected native facts' exact foreign keys. Neither the
            // current catalogue head nor deployment configuration owns a pin.
            if(frame.ObservationIds.IsDefaultOrEmpty)throw Invalid("An input frame requires an explicit source or selected observations.");
            var owners=await PreviewDatabase.Rows(connection,"SELECT row_to_json(t)::text FROM(SELECT r.id,r.reference_catalog_id,r.reference_revision_id,count(*) pins "+
                "FROM market_data.observations o JOIN market_data.revisions r ON(r.catalog_id,r.id)=(o.catalog_id,o.definition_revision_id) "+
                "WHERE o.catalog_id=$1 AND o.id=ANY($2::uuid[]) GROUP BY r.id,r.reference_catalog_id,r.reference_revision_id)t",
                [database.Manifest.CatalogId,frame.ObservationIds.ToArray()],ct);
            if(owners.Length!=1||int.Parse(owners[0].Text("pins"),System.Globalization.CultureInfo.InvariantCulture)!=frame.ObservationIds.Length)
                throw Invalid("A root selection requires one exact source and every selected native observation.");
            exact=new(owners[0].Id("id"),owners[0].Id("reference_catalog_id"),owners[0].Id("reference_revision_id"));
        }
        return exact;
    }

    internal static async Task<(PreviewData Data,InputFrame Frame)> ForCurve(PreviewDatabase database,NpgsqlConnection connection,
        InputFrame frame,string curve,CancellationToken ct)
    {
        var root=await Root(database,connection,frame,ct);
        var catalogue=frame.SourceInputs is null?await new CatalogueStore(database.Manifest.CatalogId).ReadBound(connection,ct,frame.Time.Date):null;
        var source=SelectedCurve(frame,curve,catalogue,root);
        var own=new CatalogueSource(root.DefinitionRevision.RevisionId,root.ReferenceRevision.CatalogId,root.ReferenceRevision.RevisionId);
        return await Resolve(database,connection,frame,source,[(root,frame with{Source=own})],false,ct);
    }

    internal static CatalogueSource SelectedCurve(InputFrame frame,string curve,CataloguePublication? catalogue,PreviewData root)
    {
        // Native rate inspection has exact tenor pins in the primary source;
        // commodity session membership does not attest signed rate coverage.
        if(root.IsRateCurve(curve))
        {
            if(root.Definitions["curves"].Any(c=>c.Text("code")==curve))throw Invalid("A code ambiguously names a commodity and rate curve.");
            return new(root.DefinitionRevision.RevisionId,root.ReferenceRevision.CatalogId,root.ReferenceRevision.RevisionId);
        }
        if(frame.SourceInputs is not null)
        {
            var roots=frame.SourceInputs.Values.Where(p=>p.RootCurves.Contains(curve,StringComparer.Ordinal)).ToArray();
            if(roots.Length==0)throw new MarketDataException("curve_not_selected","The selected inputs do not declare this curve as a root.");
            if(roots.Length!=1)throw Invalid("The selected inputs declare several sources for one root curve.");
            return roots[0].Source;
        }
        return (catalogue??throw Invalid("A working input selection requires its dated catalogue membership.")).RequireCurve(curve);
    }

    // Project a known exact definition onto the selected inputs without changing
    // the selected business/knowledge coordinates or signed close membership.
    internal static async Task<InputFrame> FrameFor(PreviewDatabase database,NpgsqlConnection connection,
        PreviewData data,InputFrame frame,CancellationToken ct)
    {
        var source=CatalogueSource.From(data);
        InputFrame projected;
        if(frame.SourceInputs is {} parts)
        {
            var part=parts.GetValueOrDefault(source.DefinitionRevisionId)??throw new MarketDataException("market_inputs_missing","The selected inputs omit this exact source.");
            if(part.Source!=source)throw Invalid("The selected source has different exact reference pins.");
            projected=part.Project(frame);
        }
        else if(frame.Source==source)projected=frame;
        else projected=(await Resolve(database,connection,frame,source,[],false,ct)).Frame;
        Require((data,projected),frame,source);
        return projected;
    }

    internal static void Require((PreviewData Data,InputFrame Frame) source,InputFrame selected,CatalogueSource exact)
    {
        if(source.Frame.Source is {} owner&&owner!=exact||source.Frame.ContextId!=selected.ContextId||source.Frame.Time!=selected.Time||
            source.Data.DefinitionRevision.RevisionId!=exact.DefinitionRevisionId||
            source.Data.ReferenceRevision!=new ReferenceRevision(exact.ReferenceCatalogId,exact.ReferenceRevisionId))
            throw Invalid("Market inputs require the exact definition, references, business date, valuation instant and selected knowledge view.");
        var periods=source.Data.Definitions.GetValueOrDefault("revision_periods",[]);
        if(periods.Length!=1)throw Invalid("Market inputs require their source's declared effective interval.");
        var day=selected.Time.Date;
        if(day<DateWorkspace.Date(periods[0].Text("effective_from"))||day>=DateWorkspace.Date(periods[0].Text("effective_to")))
            throw new MarketDataException("market_inputs_not_effective","The selected source does not apply on this business date.");
    }

    internal static async Task<(PreviewData Data,InputFrame Frame)> Resolve(PreviewDatabase database,NpgsqlConnection connection,
        InputFrame frame,CatalogueSource exact,ImmutableArray<(PreviewData Data,InputFrame Frame)> supplied,bool requirePinned,CancellationToken ct,MarketInputReadScope? reads=null)
    {
        if(supplied.IsDefault)throw Invalid("Supply explicit input sources.");
        MarketSourceInputs? recorded=null;
        if(frame.OriginCloseId is not null)
        {
            if(frame.SourceInputs is null)throw Invalid("A recorded close requires its exact source matrix.");
            if(frame.SourceInputs.GetValueOrDefault(exact.DefinitionRevisionId) is not {} part)
                throw requirePinned?Invalid("The retained execution omitted a required source."):
                    new MarketDataException("market_inputs_missing","The recorded close does not contain the requested source inputs.");
            if(part.Source!=exact)throw Invalid("The requested source references differ from the recorded input matrix.");
            recorded=part;
        }
        var found=supplied.Where(s=>s.Data.DefinitionRevision.RevisionId==exact.DefinitionRevisionId).ToArray();
        if(found.Length==0&&!requirePinned&&frame.SourceInputs?.GetValueOrDefault(exact.DefinitionRevisionId) is {} retained)
        {
            if(retained.Source!=exact)throw Invalid("The recorded input source has different exact references.");
            var projection=retained.Project(frame);
            var data=reads is null?await database.Read(connection,ct,ppaBundle:exact,observationIds:projection.ObservationIds,observationScope:ObservationReadScope.ExactPins):
                await reads.Read(projection,exact,projection.ObservationIds,ReferenceReadScope.DefinitionClosure,ObservationReadScope.ExactPins,true,ct);
            found=[(data,projection)];
        }
        if(found.Length==0&&!requirePinned&&frame.OriginCloseId is null)
            found=(await database.ReadInputMarkets(connection,frame,ct,requiredRevisions:new HashSet<Guid>{exact.DefinitionRevisionId})).ToArray();
        if(found.Length==0&&requirePinned)throw Invalid("The retained execution omitted a required source.");
        if(found.Length==0)throw new MarketDataException("market_inputs_missing","The required source has no explicit inputs at this business date and valuation instant.");
        if(found.Length!=1)throw Invalid("Every required market must have exactly one explicit source selection. Retained calculations cannot select replacement inputs.");
        if(found[0].Data.DefinitionRevision.CatalogId!=database.Manifest.CatalogId)throw Invalid("The source belongs to a different catalogue.");
        Require(found[0],frame,exact);
        if(recorded is not null&&!recorded.Matches(found[0].Frame))
            throw Invalid("Recorded closes require their exact source input selections.");
        return found[0];
    }

    internal static async Task<PreviewData> Terms(PreviewDatabase database,NpgsqlConnection connection,PreviewData selected,CatalogueSource exact,CancellationToken ct)
    {
        var data=selected.DefinitionRevision.RevisionId==exact.DefinitionRevisionId?selected:
            await database.Read(connection,ct,observationIds:[],includeCalendarDays:false,ppaBundle:exact);
        if(data.DefinitionRevision.CatalogId!=database.Manifest.CatalogId||data.ReferenceRevision!=new ReferenceRevision(exact.ReferenceCatalogId,exact.ReferenceRevisionId))
            throw Invalid("Retained terms require their exact reference revision.");
        return data;
    }
    private static MarketDataException Invalid(string message)=>new("market_inputs",message);
}
