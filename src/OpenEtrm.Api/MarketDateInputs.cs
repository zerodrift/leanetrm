using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// One operation, one knowledge view. Native matrices are reattested before this
// scope is created. Metadata and exact pins are shared by source, never by code
// or across requests. Limited calendars leave older candidates' pins unread.
internal sealed class MarketDateInputs(PreviewDatabase database,NpgsqlConnection connection)
{
    sealed record Candidate(ImmutableArray<(string Code,CatalogueSource Source)> Selected,Exception? Error);
    readonly Dictionary<CatalogueSource,PreviewData> metadata=[];
    readonly Dictionary<Guid,Candidate> candidates=[];
    readonly Dictionary<(Guid Frame,CatalogueSource Source),PreviewData> inputs=[];
    readonly Dictionary<(CatalogueSource Source,string Code),RateTenorSource?> rates=[];
    Dictionary<DateOnly,(CataloguePublication? Publication,string? Error)> catalogues=[];

    internal async Task Prepare(ImmutableArray<InputFrame> frames,IEnumerable<string>? requested,PreviewData? initial,bool full,CancellationToken ct)
    {
        if(initial is not null)metadata.Add(CatalogueSource.From(initial),initial);
        catalogues=await new CatalogueStore(database.Manifest.CatalogId).ReadDates(connection,frames.Select(f=>f.Time.Date),ct);
        if(!full)return;
        foreach(var frame in frames)await Plan(frame,requested,ct);
        var selections=frames.Where(f=>candidates[f.Id] is {Error:null,Selected.IsEmpty:false})
            .SelectMany(frame=>Sources(frame,candidates[frame.Id]).Select(source=>(Frame:frame,Source:source)));
        foreach(var group in selections.GroupBy(item=>item.Source))
            await ReadPins([..group.Select(item=>item.Frame)],group.Key,ct);
    }
    async Task<PreviewData> Metadata(CatalogueSource source,CancellationToken ct)
    {
        if(!metadata.TryGetValue(source,out var data))
        {
            data=await database.Read(connection,ct,ppaBundle:source,observationIds:[],includeCalendarDays:true);
            metadata.Add(source,data);
        }
        return data;
    }
    async Task<Candidate> Plan(InputFrame frame,IEnumerable<string>? requested,CancellationToken ct)
    {
        if(candidates.TryGetValue(frame.Id,out var existing))return existing;
        Candidate result;
        try
        {
            // Retain root-metadata error precedence over the dated catalogue.
            _=await Metadata(frame.Source!,ct);
            var(catalogue,error)=catalogues[frame.Time.Date];
            if(error is not null)throw new MarketDataException(error,CatalogueStore.Refusal(error));
            var parts=frame.SourceInputs!;
            var selectedRates=ImmutableArray.CreateBuilder<CatalogueCurve>();
            foreach(var choice in catalogue!.Curves.Where(c=>c.Kind=="rate"&&parts.ContainsKey(c.DefinitionRevisionId)))
            {
                var data=await Metadata(choice.Source,ct);
                var rows=data.Definitions["rate_curves"].Where(r=>r.Text("code")==choice.Code).ToArray();
                if(rows.Length==0)continue;
                if(parts[choice.DefinitionRevisionId].Source!=choice.Source||rows.Any(r=>r.Id("reference_catalog_id")!=choice.ReferenceCatalogId||r.Id("reference_revision_id")!=choice.ReferenceRevisionId))
                    throw new MarketDataException("market_inputs","Selected rate inputs differ from the exact published choice.");
                selectedRates.Add(choice);
            }
            var codes=requested??parts.Values.SelectMany(p=>p.RootCurves).Concat(selectedRates.Select(r=>r.Code));
            var selected=ImmutableArray.CreateBuilder<(string,CatalogueSource)>();
            foreach(var code in codes.Distinct(StringComparer.Ordinal))
            {
                try{selected.Add((code,MarketInputSelection.SelectedCurve(frame,code,catalogue,selectedRates.ToImmutable())));}
                catch(MarketDataException refusal)when(refusal.Code is "curve_not_selected" or "catalogue_curve" or "market_inputs_missing"){}
            }
            result=new(selected.ToImmutable(),null);
        }
        catch(Exception error)when(error is MarketDataException or TemporalException){result=new([],error);}
        candidates.Add(frame.Id,result);return result;
    }
    static IEnumerable<CatalogueSource> Sources(InputFrame frame,Candidate candidate)=>
        candidate.Selected.Select(item=>item.Source).Prepend(frame.Source!).Distinct();
    async Task ReadPins(ImmutableArray<InputFrame> frames,CatalogueSource source,CancellationToken ct)
    {
        var data=await Metadata(source,ct);
        var pins=frames.SelectMany(frame=>frame.SourceInputs![source.DefinitionRevisionId].ObservationIds).Distinct().ToImmutableArray();
        var selected=await database.ReadObservationWindows(connection,data,[],ct,pins);
        foreach(var frame in frames)inputs.Add((frame.Id,source),selected);
    }
    internal async Task<ImmutableArray<string>> Qualified(InputFrame frame,IEnumerable<string>? requested,CancellationToken ct)
    {
        var candidate=await Plan(frame,requested,ct);
        if(candidate.Error is {} error)ExceptionDispatchInfo.Capture(error).Throw();
        if(candidate.Selected.IsEmpty)return [];
        foreach(var source in Sources(frame,candidate))
            if(!inputs.ContainsKey((frame.Id,source)))await ReadPins([frame],source,ct);
        var qualified=ImmutableArray.CreateBuilder<string>();
        foreach(var group in candidate.Selected.GroupBy(item=>item.Source))
        {
            var data=inputs[(frame.Id,group.Key)];
            var projected=frame.SourceInputs![group.Key.DefinitionRevisionId].Project(frame);
            MarketInputSelection.Require((data,projected),frame,group.Key);
            var selectedRates=new Dictionary<string,RateTenorSource>(StringComparer.Ordinal);
            foreach(var code in group.Select(item=>item.Code).Where(data.IsRateCurve))
            {
                var key=(group.Key,code);
                if(!rates.TryGetValue(key,out var rate))
                {
                    try{rate=await RateCurveResolver.Load(connection,data,projected,code,ct);}
                    catch(Exception refusal)when(refusal is MarketDataException or TemporalException){rate=null;}
                    rates.Add(key,rate);
                }
                if(rate is not null)selectedRates.Add(code,rate);
            }
            foreach(var item in group)
            {
                ct.ThrowIfCancellationRequested();
                try{MarketDates.QualifyCurve(data,projected,item.Code,selectedRates);qualified.Add(item.Code);}
                catch(Exception refusal)when(refusal is MarketDataException or TemporalException){}
            }
        }
        return qualified.ToImmutable();
    }
}
