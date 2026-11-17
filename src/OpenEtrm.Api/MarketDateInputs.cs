using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

internal enum MarketDateRead{Calendar,LastCloses}

// One operation, one knowledge view. Native matrices are reattested before this
// scope is created. Metadata and exact pins are shared by source, never by code
// or across requests. Limited calendars read the first frame exactly, then batch
// remaining potential inputs without consuming unvisited facts or refusals.
internal sealed class MarketDateInputs(PreviewDatabase database,NpgsqlConnection connection,MarketDateRead read)
{
    sealed record Candidate(ImmutableArray<(string Code,CatalogueSource Source)> Selected,Exception? Error);
    readonly Dictionary<CatalogueSource,PreviewData> metadata=[];
    readonly Dictionary<CatalogueSource,Exception> metadataErrors=[];
    readonly MarketInputSelection.RateReads rateReads=new(database,connection);
    readonly Dictionary<Guid,Candidate> candidates=[];
    readonly Dictionary<(Guid Frame,CatalogueSource Source),PreviewData> inputs=[];
    readonly Dictionary<(Guid Frame,CatalogueSource Source),Exception> inputErrors=[];
    readonly Dictionary<CatalogueSource,PreviewDatabase.ObservationPinSets> remainingPins=[];
    Guid? firstPinFrame;
    readonly Dictionary<(CatalogueSource Source,string Code),RateTenorSource?> rates=[];
    readonly HashSet<Guid> attested=[];
    readonly Dictionary<Guid,Exception> matrixErrors=[];
    Dictionary<DateOnly,CatalogueStore.Dated> catalogues=[];
    DateOnly[] dates=[];
    ImmutableArray<InputFrame> frames=[];

    internal async Task Attest(ImmutableArray<InputFrame> selected,CancellationToken ct)
    {
        await PrefetchMatrices(selected,ct);
        foreach(var frame in selected)RequireMatrix(frame);
    }
    internal async Task PrefetchMatrices(ImmutableArray<InputFrame> selected,CancellationToken ct)
    {
        selected=[..selected.Where(frame=>!attested.Contains(frame.Id))];
        if(selected.IsEmpty)return;
        foreach(var(id,error)in await MarketCloses.ReadFrameResults(connection,database.Manifest.CatalogId,selected,selected[0].ContextId,ct))
            if(error is not null)matrixErrors.Add(id,error);
        attested.UnionWith(selected.Select(frame=>frame.Id));
    }
    internal void RequireMatrix(InputFrame frame)
    {
        if(!attested.Contains(frame.Id))throw new InvalidOperationException("A calendar candidate must have its native matrix result before use.");
        if(matrixErrors.TryGetValue(frame.Id,out var error))ExceptionDispatchInfo.Capture(error).Throw();
    }
    internal async Task RequireRoot(ImmutableArray<InputFrame> selected,string code,CancellationToken ct)
    {
        var roots=selected.Where(frame=>frame.SourceInputs?.Values.Any(part=>part.RootCurves.Contains(code,StringComparer.Ordinal))==true).ToImmutableArray();
        if(roots.IsEmpty)
        {
            await Attest(selected,ct);
            throw new MarketDataException("missing_curve","Choose an exact curve in the published catalogue or selected close scope.");
        }
        await PrefetchMatrices(roots,ct);
        if(roots.All(frame=>matrixErrors.ContainsKey(frame.Id)))RequireMatrix(roots[0]);
    }

    internal async Task Prepare(ImmutableArray<InputFrame> frames,IEnumerable<string>? requested,PreviewData? initial,bool full,CancellationToken ct)
    {
        this.frames=frames;
        if(initial is not null){metadata.Add(CatalogueSource.From(initial),initial);rateReads.Seed(initial);}
        dates=frames.Select(f=>f.Time.Date).Distinct().Order().ToArray();
        await Attest(full?frames:frames.IsEmpty?[]:[..frames.Where(frame=>frame.Time.Date==dates[^1])],ct);
        catalogues=await new CatalogueStore(database.Manifest.CatalogId).ReadDates(connection,full?dates:dates.TakeLast(1),ct);
        if(!full)return;
        foreach(var frame in frames.OrderBy(frame=>frame.At).ThenBy(frame=>frame.Id))await Plan(frame,requested,ct);
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
            if(!attested.Contains(frame.Id))await PrefetchMatrices(frames,ct);
            RequireMatrix(frame);
            // Retain root-metadata error precedence over the dated catalogue.
            _=await Metadata(frame.Source!,ct);
            if(!catalogues.ContainsKey(frame.Time.Date))
                foreach(var(day,outcome)in await new CatalogueStore(database.Manifest.CatalogId).ReadDates(connection,dates.Where(day=>!catalogues.ContainsKey(day)),ct))
                    catalogues.Add(day,outcome);
            var(catalogue,error,denied)=catalogues[frame.Time.Date];
            if(denied)throw new PreviewDeniedException();
            if(error is not null)
            {
                var refusal=new MarketDataException(error,CatalogueStore.Refusal(error));
                // The legacy calendar's per-date catch could not recover an
                // aborted native transaction. Preserve its whole-read failure
                // explicitly, without poisoning the transaction or returning a
                // new partial calendar. Last-closes keeps its structured error.
                if(read==MarketDateRead.Calendar)throw new InvalidOperationException("The dated catalogue selector failed to complete the calendar read.",refusal);
                throw refusal;
            }
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
    static bool MayUse(InputFrame frame,CatalogueSource source,IEnumerable<string>? requested,CataloguePublication catalogue)
    {
        // This is only an I/O superset. The real plan still authorizes typed
        // rates and applies the same exact selection contract before use.
        var rates=catalogue.Curves.Where(curve=>curve.Kind=="rate").ToImmutableArray();
        var codes=requested??frame.SourceInputs!.Values.SelectMany(part=>part.RootCurves).Concat(rates.Select(rate=>rate.Code));
        foreach(var code in codes)
        {
            try
            {
                var selected=MarketInputSelection.SelectedCurve(frame,code,catalogue,rates);
                if(source==frame.Source||source==selected)return true;
            }
            catch(MarketDataException){}
        }
        return false;
    }
    async Task ReadRemainingPins(InputFrame frame,CatalogueSource source,IEnumerable<string>? requested,CancellationToken ct)
    {
        if(!remainingPins.TryGetValue(source,out var batch))
        {
            var possible=frames.Where(candidate=>candidate.Id!=firstPinFrame&&!matrixErrors.ContainsKey(candidate.Id)&&
                catalogues[candidate.Time.Date] is{Error:null,Denied:false,Publication:{} publication}&&
                MayUse(candidate,source,requested,publication)).ToArray();
            var data=await Metadata(source,ct);
            batch=await database.ReadObservationPinSets(connection,data,
                [..possible.Select(candidate=>candidate.SourceInputs![source.DefinitionRevisionId].Project(candidate))],ct);
            remainingPins.Add(source,batch);
        }
        if(batch.Denied.Contains(frame.Id))throw new PreviewDeniedException();
        inputs.Add((frame.Id,source),batch.Data);
        var own=frame.SourceInputs![source.DefinitionRevisionId].ObservationIds.ToHashSet();
        AsyncReadVisibility.Observations(batch.Data.DefinitionRevision.CatalogId,source.DefinitionRevisionId,
            [..batch.Data.Observations.Where(row=>own.Contains(row.Id("id")))]);
    }
    internal async Task<ImmutableArray<string>> Qualified(InputFrame frame,IEnumerable<string>? requested,CancellationToken ct)
    {
        var candidate=await Plan(frame,requested,ct);
        if(candidate.Error is {} error)ExceptionDispatchInfo.Capture(error).Throw();
        if(candidate.Selected.IsEmpty)return [];
        foreach(var source in Sources(frame,candidate))
        {
            if(inputErrors.TryGetValue((frame.Id,source),out var failure))ExceptionDispatchInfo.Capture(failure).Throw();
            if(!inputs.ContainsKey((frame.Id,source)))
            {
                firstPinFrame??=frame.Id;
                if(firstPinFrame==frame.Id)await ReadPins([frame],source,ct);
                else await ReadRemainingPins(frame,source,requested,ct);
            }
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
