using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class ContractObservationSamples
{
    // One authorized Holdings operation owns this reader; it never outlives
    // that operation's transaction and permission fence.
    internal sealed class History
    {
        private readonly Dictionary<(DefinitionRevision Revision,ScheduleContext Context,DateTimeOffset At),PreviewData> histories=[];
        internal async Task<PreviewData> Read(DefinitionRevision revision,InputFrame frame,ScheduleContext context,
            bool retained,Func<Task<PreviewData>> load)
        {
            var exact=PinnedContext.Exact(context);
            if(frame.ContextId!=exact.ContextId)throw Invalid("Source history requires the bound certified context.");
            if(retained)return await load();
            var key=(revision,exact,frame.At);
            if(histories.TryGetValue(key,out var history))return history;
            history=await load();
            histories.Add(key,history);
            return history;
        }
    }

    internal static async Task<ImmutableArray<ContractObservationSample>> Read(NpgsqlConnection connection,PreviewDatabase database,
        PreviewData data,InputFrame frame,ScheduleContext context,ImmutableArray<ContractObservationLookup> lookups,CancellationToken ct,ConnectedMarketEvidence? retained=null,History? histories=null)
    {
        ct.ThrowIfCancellationRequested();
        if(lookups.IsEmpty)return [];
        var exact=PinnedContext.Exact(context);
        if(frame.ContextId!=exact.ContextId)throw Invalid("Source samples require the bound certified knowledge context.");
        // The calling operation already bound this exact context. RLS supplies
        // only facts committed by its certified knowledge cutoff.
        var pins=retained is null?(ImmutableArray<Guid>?)null:retained.Samples.Where(sample=>sample.Kind=="contract_observation")
            .SelectMany(sample=>sample.Observations.Select(observation=>observation.Id)).Concat(frame.ObservationIds).Distinct().ToImmutableArray();
        Task<PreviewData> Load()=>database.Read(connection,ct,data.DefinitionRevision.RevisionId,data,pins,
            includeCalendarDays:false,observationScope:retained is null?ObservationReadScope.FactHistory:ObservationReadScope.ExactPins);
        var history=histories is null?await Load():await histories.Read(data.DefinitionRevision,frame,exact,retained is not null,Load);
        return [..lookups.Distinct().Select(lookup=>{
            ConnectedSampleEvidence? evidence=null;
            if(retained is not null)
            {
                var matching=retained.Samples.Where(sample=>Matches(sample,lookup,frame.At)).ToArray();
                if(matching.Length!=1)throw Invalid("The retained manifest must identify exactly one source sample for every observation.");
                evidence=matching[0];
            }
            return new ContractObservationSample(lookup,history.Model,
                Select(history,frame,lookup,exact.ContextId!.Value,SchedulePreview.ParseAt(exact.KnownAt),evidence));
        })];
    }
    internal static MarketSnapshot Select(PreviewData data,InputFrame frame,ContractObservationLookup lookup,Guid context,DateTimeOffset knownAt,ConnectedSampleEvidence? retained=null)
    {
        lookup.Validate();
        var at=lookup.FixingAt<frame.At?lookup.FixingAt:frame.At;
        var actual=lookup.FixingAt<=frame.At;
        var carry=at>=lookup.Contract.Expiry;
        if(carry&&lookup.ExpiryPolicy!=ContractObservationExpiry.CarryFinalSettlement)throw Invalid("An expired source requires its explicit final-settlement policy.");
        var role=carry?ObservationRole.FinalContractSettlement:lookup.Role;
        if(retained is not null&&(!Matches(retained,lookup,frame.At)||retained.Observations.IsDefaultOrEmpty||retained.Observations.Length!=1))
            throw Invalid("Retained source sample coordinates and freezing must match the exact booked observation.");
        var available=retained is not null?data.Observations.Where(row=>retained.Observations.Any(pin=>pin.Id==row.Id("id"))):
            actual||carry?data.Observations:frame.ObservationIds.Select(data.Observation);
        var candidates=available.Where(row=>row.Id("series_id")==lookup.Series.Id&&row.Text("source_code")==lookup.Source.Code&&
            PreviewData.ParseRole(row.Text("role"))==role&&
            (carry?row.Instant("observed_at")>=lookup.Contract.Expiry&&row.Instant("observed_at")<=at:
                actual?row.Instant("observed_at")==lookup.FixingAt:row.Instant("observed_at")<=at)).ToArray();
        if(retained is null&&(actual||carry))
        {
            var superseded=candidates.Select(row=>row.OptionalId("corrects_id")).OfType<Guid>().ToHashSet();
            candidates=candidates.Where(row=>!superseded.Contains(row.Id("id"))).ToArray();
        }
        candidates=candidates.Where(row=>row.Text("publication_state")=="final").ToArray();
        if(candidates.Length==0)throw new MarketDataException("missing_observation","The exact required contract observation is unavailable at the certified knowledge cutoff.");
        if(candidates.Length!=1)throw Invalid("The required contract observation has multiple visible source events.");
        var selected=candidates[0];
        var use=carry?InputUse.FrozenFact:InputUse.Variable;
        var snapshot=new MarketSnapshot(Guid.NewGuid(),data.DefinitionRevision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [new(new(lookup.Series,use),new(new(data.DefinitionRevision.CatalogId,selected.Id("id")),lookup.Series,lookup.Source,role,
                PublicationState.Final,selected.Instant("observed_at"),selected.Number("value")))]);
        snapshot=snapshot with{Id=CustomCurves.StableSampleId(snapshot)};
        if(retained is not null&&(retained.SnapshotId!=snapshot.Id||retained.Observations[0]!=new ConnectedSampleObservation(selected.Id("id"),lookup.Series.Id,use.ToString())))
            throw Invalid("The retained source frame differs from its exact observation and input use.");
        return MarketSnapshotBinding.Bind(snapshot,context,knownAt);
    }
    internal static ConnectedSampleEvidence Evidence(ContractObservationSample sample,DateTimeOffset valuationAt)=>
        new(sample.Snapshot.Id,sample.Snapshot.ValuationAt,sample.Lookup.Market.Curve.Code,sample.Lookup.Contract.Key.Id,
            sample.Snapshot.ValuationAt<valuationAt,[..sample.Snapshot.Selections.OrderBy(row=>row.Input.Series.Id).ThenBy(row=>row.Input.Use)
                .Select(row=>new ConnectedSampleObservation(row.Observation.Key.Id,row.Input.Series.Id,row.Input.Use.ToString()))],
            "contract_observation",FreezeObservation(sample.Lookup,valuationAt));
    private static bool FreezeObservation(ContractObservationLookup lookup,DateTimeOffset valuationAt)=>
        lookup.FixingAt==valuationAt&&valuationAt<lookup.Contract.Expiry;
    private static bool Matches(ConnectedSampleEvidence sample,ContractObservationLookup lookup,DateTimeOffset valuationAt)=>
        sample.Kind=="contract_observation"&&sample.SourceCurve==lookup.Market.Curve.Code&&sample.ContractId==lookup.Contract.Key.Id&&
        sample.At==(lookup.FixingAt<valuationAt?lookup.FixingAt:valuationAt)&&sample.FreezeHistorical==(lookup.FixingAt<valuationAt)&&
        sample.FreezeObservation==FreezeObservation(lookup,valuationAt);
    private static MarketDataException Invalid(string message)=>new("contract_observation_source",message);
}
