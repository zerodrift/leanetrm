using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// The lifecycle coordinator owns authorization and binds one certified context
// before this reader. Transport order and received_at never select a revision.
internal static class FuturesMarginSources
{
    internal static async Task<ImmutableDictionary<Guid,FuturesSettlementFact>> Read(NpgsqlConnection connection,
        PreviewDatabase database,PreviewData data,InputFrame frame,ScheduleContext context,FuturesMarketBinding binding,
        FuturesMarginPolicy policy,ImmutableArray<FuturesMarginOccurrence> occurrences,CancellationToken ct)
    {
        var exact=PinnedContext.Exact(context);
        frame.Time.RequireDate(DateWorkspace.Date(exact.EffectiveOn));
        if(frame.ContextId!=exact.ContextId||policy.Implementation is not(FuturesMarginPolicy.Version or FuturesMarginPolicy.DeliveryVersion)||policy.Contract!=binding.Contract.Key||
            occurrences.IsDefaultOrEmpty||occurrences.Any(o=>o is null)||occurrences.Select(o=>o.Id).Distinct().Count()!=occurrences.Length)
            throw Invalid("Supply the adopted policy, exact occurrences and bound certified context.");
        var source=binding.Instrument.SettlementSource(binding,policy.SettlementCurve,data.Model);
        var windows=occurrences.Select(o=>new ObservationWindow(source.ObservationSeries.Id,source.Source.Code,
            PreviewData.RoleCode(Role(o)),o.At,o.At)).ToImmutableArray();
        var history=await database.ReadObservationWindows(connection,data,windows,ct);
        var ids=history.Observations.Select(o=>o.Id("id")).Distinct().ToArray();
        var rows=await PreviewDatabase.Rows(connection,"""
            SELECT row_to_json(q)::text FROM(
              SELECT e.observation_id AS id,k.known_from FROM market_data.knowledge_events e
              JOIN market_data.market_knowledge_receipts k
                ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
              JOIN market_data.market_contexts m ON m.catalog_id=e.catalog_id AND m.context_id=$3
              JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(m.catalog_id,m.context_id)
              WHERE e.catalog_id=$1 AND e.observation_id=ANY($2::uuid[]) AND e.revision_id=$4
                AND e.event_order<=m.known_through AND k.known_from<=t.known_at AND t.known_at=$5
            )q
            """,[data.DefinitionRevision.CatalogId,ids,exact.ContextId!.Value,data.DefinitionRevision.RevisionId,
                SchedulePreview.ParseAt(exact.KnownAt)],ct);
        var known=rows.ToDictionary(row=>row.Id("id"),row=>row.Instant("known_from"));
        var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),exact.ContextId.Value,SchedulePreview.ParseAt(exact.KnownAt));
        return occurrences.ToImmutableDictionary(o=>o.Id,o=>SelectCurrentCorrection(history,snapshot,binding,policy.SettlementCurve,o,known));
    }

    // Economic replay reads source-known official corrections through this
    // exact native owner/policy closure. Worker roles never read receipt tables.
    internal static async Task<ImmutableDictionary<Guid,FuturesSettlementFact>> ReadForProjection(NpgsqlConnection connection,
        PreviewDatabase database,PreviewData data,InputFrame frame,ScheduleContext context,AccountingPositionKey owner,FuturesMarketBinding binding,
        FuturesMarginPolicy policy,ImmutableArray<FuturesMarginOccurrence> occurrences,CancellationToken ct)
    {
        var exact=PinnedContext.Exact(context);
        frame.Time.RequireDate(PublishedSchedules.ContextDate(exact));
        if(!owner.IsValid||owner.Owner.Kind!=AccountingOwnerKind.Trade||frame.ContextId!=exact.ContextId||policy.Contract!=binding.Contract.Key||
            occurrences.IsDefaultOrEmpty||occurrences.Any(o=>o is null||o.At>frame.At||!policy.Occurrences.Contains(o))||
            occurrences.Select(o=>o.Id).Distinct().Count()!=occurrences.Length)
            throw Invalid("Retain the exact adopted future, complete past occurrences and original certified source.");
        var until=await TradingContext.AdmitSource(database,connection,exact,frame.At,ct);
        var native=await LifecycleNative.Read(connection,"SELECT settlement_data.read_futures_margin_sources($1,$2,$3,$4,$5,$6,$7)::text",ct,
            database.Manifest.CatalogId,owner.TradeId,owner.BindingId,exact.ContextId!.Value,frame.At,frame.BusinessDate,until);
        if(native.Id("contextId")!=exact.ContextId.Value||native.Instant("asOf")!=frame.At||
            DateWorkspace.Date(native.Text("businessDate"))!=frame.BusinessDate||native.Instant("knownAt")!=SchedulePreview.ParseAt(exact.KnownAt)||
            native.Instant("authorityUntil")!=until)
            throw Invalid("Official source evidence must retain this admitted owner and exact original source clocks.");
        var required=native.GetProperty("occurrenceIds").EnumerateArray().Select(v=>v.GetGuid()).ToArray();
        if(required.Distinct().Count()!=required.Length||!required.ToHashSet().SetEquals(occurrences.Select(o=>o.Id)))
            throw Invalid("The economic source requires the complete native past margin occurrence set.");
        var rows=native.GetProperty("observations").EnumerateArray().ToArray();
        var history=data.WithObservations([..rows.Select(r=>r.GetProperty("observation"))]);
        var official=binding.Instrument.SettlementSource(binding,policy.SettlementCurve,data.Model);
        if(history.Observations.Any(row=>row.Id("catalog_id")!=data.DefinitionRevision.CatalogId||row.Id("definition_revision_id")!=data.DefinitionRevision.RevisionId||
                row.Id("series_id")!=official.ObservationSeries.Id||row.Text("source_code")!=official.Source.Code||
                !occurrences.Any(o=>row.Instant("observed_at")==o.At&&PreviewData.ParseRole(row.Text("role"))==Role(o)))||
            history.Observations.Select(o=>o.Id("id")).Distinct().Count()!=history.Observations.Length)
            throw Invalid("Official source revisions require distinct native observation identities.");
        var known=rows.ToDictionary(r=>r.GetProperty("observation").Id("id"),r=>r.Instant("knownAt"));
        var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),exact.ContextId.Value,SchedulePreview.ParseAt(exact.KnownAt));
        return occurrences.ToImmutableDictionary(o=>o.Id,o=>SelectCurrentCorrection(history,snapshot,binding,policy.SettlementCurve,o,known));
    }

    internal static FuturesSettlementFact SelectCurrentCorrection(PreviewData history,MarketSnapshot context,FuturesMarketBinding binding,
        CurveKey settlementCurve,FuturesMarginOccurrence occurrence,IReadOnlyDictionary<Guid,DateTimeOffset> known)
    {
        if(context.Knowledge is null||context.Definitions!=history.DefinitionRevision)
            throw Invalid("Settlement history requires its exact certified source context.");
        var source=binding.Instrument.SettlementSource(binding,settlementCurve,history.Model);
        var role=Role(occurrence);
        var candidates=history.Observations.Where(row=>row.Id("series_id")==source.ObservationSeries.Id&&row.Text("source_code")==source.Source.Code&&
            PreviewData.ParseRole(row.Text("role"))==role&&row.Instant("observed_at")==occurrence.At).ToArray();
        if(candidates.Any(row=>!known.TryGetValue(row.Id("id"),out var at)||!TradingInstant.IsSupported(at)))
            throw Invalid("Every source revision requires its exact certified publication receipt.");
        candidates=candidates.Where(row=>known[row.Id("id")]<=context.Knowledge.KnownAt).ToArray();
        var visible=candidates.Select(row=>row.Id("id")).ToHashSet();
        if(visible.Count!=candidates.Length||candidates.Any(row=>row.OptionalId("corrects_id") is {} prior&&
            (!visible.Contains(prior)||known[prior]>known[row.Id("id")])))
            throw Invalid("Source corrections require their complete distinct visible ancestry.");
        var superseded=candidates.Select(row=>row.OptionalId("corrects_id")).OfType<Guid>().ToHashSet();
        var current=candidates.Where(row=>!superseded.Contains(row.Id("id"))).ToArray();
        if(current.Length>1)throw Invalid("The exact official occurrence has multiple independent visible source events.");
        if(current.Length==0||current[0].Text("publication_state")!="final")
            throw new MarketDataException("missing_observation","The exact official settlement is unavailable at the certified knowledge cutoff.");
        var selected=history.ReadObservation(current[0]);
        var sample=context with{Selections=[new(new(selected.Series,occurrence.Phase==FuturesMarginPhase.Final?InputUse.FrozenFact:InputUse.Variable),selected)]};
        sample=sample with{Id=CustomCurves.StableSampleId(sample)};
        return binding.Instrument.ResolveSettlementFact(binding,settlementCurve,history.Model,sample,selected.Key,known[selected.Key.Id],
            occurrence.At,occurrence.Phase==FuturesMarginPhase.Final);
    }
    private static ObservationRole Role(FuturesMarginOccurrence occurrence)=>occurrence.Phase switch{
        FuturesMarginPhase.Daily=>ObservationRole.DailySettlement,FuturesMarginPhase.Final=>ObservationRole.FinalContractSettlement,
        _=>throw Invalid("Select an exact supported margin occurrence phase.")};
    private static MarketDataException Invalid(string message)=>new("futures_settlement_evidence",message);
}
