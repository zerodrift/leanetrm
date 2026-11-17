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
    readonly Dictionary<CatalogueSource,Exception> metadataErrors=[];
    readonly MarketInputSelection.RateReads rateReads=new(database,connection);
    readonly Dictionary<Guid,Candidate> candidates=[];
    readonly Dictionary<(Guid Frame,CatalogueSource Source),PreviewData> inputs=[];
    readonly Dictionary<(Guid Frame,CatalogueSource Source),Exception> inputErrors=[];
    readonly Dictionary<(CatalogueSource Source,string Code),RateTenorSource?> rates=[];
    Dictionary<DateOnly,CatalogueStore.Dated> catalogues=[];
    DateOnly[] dates=[];

    internal async Task Prepare(ImmutableArray<InputFrame> frames,IEnumerable<string>? requested,PreviewData? initial,bool full,CancellationToken ct)
    {
        if(initial is not null){metadata.Add(CatalogueSource.From(initial),initial);rateReads.Seed(initial);}
        dates=frames.Select(f=>f.Time.Date).Distinct().Order().ToArray();
        catalogues=await new CatalogueStore(database.Manifest.CatalogId).ReadDates(connection,full?dates:dates.TakeLast(1),ct);
        if(!full)return;
        foreach(var frame in frames)await Plan(frame,requested,ct);
        var selections=frames.Where(f=>candidates[f.Id] is {Error:null,Selected.IsEmpty:false})
            .SelectMany(frame=>Sources(frame,candidates[frame.Id]).Select(source=>(Frame:frame,Source:source)));
        foreach(var group in selections.GroupBy(item=>item.Source))
        {
            try{await ReadPins([..group.Select(item=>item.Frame)],group.Key,ct);}
            catch(Exception error)when(error is MarketDataException or TemporalException)
            {
                foreach(var item in group)inputErrors.TryAdd((item.Frame.Id,item.Source),error);
            }
        }
    }
    async Task<PreviewData> Metadata(CatalogueSource source,CancellationToken ct)
    {
        if(metadataErrors.TryGetValue(source,out var prior))ExceptionDispatchInfo.Capture(prior).Throw();
        if(!metadata.TryGetValue(source,out var data))
        {
            try{data=await database.Read(connection,ct,ppaBundle:source,observationIds:[],includeCalendarDays:true);}
            catch(Exception error)when(error is MarketDataException or TemporalException){metadataErrors.Add(source,error);throw;}
            metadata.Add(source,data);
            rateReads.Seed(data);
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
            if(!catalogues.ContainsKey(frame.Time.Date))
                foreach(var(day,outcome)in await new CatalogueStore(database.Manifest.CatalogId).ReadDates(connection,dates.Where(day=>!catalogues.ContainsKey(day)),ct))
                    catalogues.Add(day,outcome);
            var(catalogue,error,denied)=catalogues[frame.Time.Date];
            if(denied)throw new PreviewDeniedException();
            if(error is not null)throw new MarketDataException(error,CatalogueStore.Refusal(error));
            AsyncReadVisibility.Catalogue(database.Manifest.CatalogId,catalogue!);
            var parts=frame.SourceInputs!;
            var selectedRates=await MarketInputSelection.SelectedRates(database,connection,frame,catalogue!,ct,rateReads);
            var codes=requested??parts.Values.SelectMany(p=>p.RootCurves).Concat(selectedRates.Select(r=>r.Code));
            var selected=ImmutableArray.CreateBuilder<(string,CatalogueSource)>();
            foreach(var code in codes.Distinct(StringComparer.Ordinal))
            {
                try{selected.Add((code,MarketInputSelection.SelectedCurve(frame,code,catalogue!,selectedRates)));}
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
        {
            if(inputErrors.TryGetValue((frame.Id,source),out var failure))ExceptionDispatchInfo.Capture(failure).Throw();
            if(!inputs.ContainsKey((frame.Id,source)))await ReadPins([frame],source,ct);
        }
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
