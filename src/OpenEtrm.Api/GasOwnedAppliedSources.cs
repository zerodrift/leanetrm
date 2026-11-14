using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal enum GasAppliedSourceKind { OperationalEvidence=1,MarketObservation }
internal enum GasAppliedEffectKind { Position=1,Consumption,Settlement }
internal sealed record GasOwnedAppliedSourceConflict(GasAppliedEffectKind Kind,Guid EffectId,Guid PositionId,Guid AccountId,
    GasAppliedSourceKind SourceKind,Guid AcceptedSourceId,ImmutableArray<Guid> CurrentSourceIds,GasOwnedKnownTime Time);

// A source revision alone cannot change accepted ownership, physical supply or
// recorded cash. Valuation refuses the relevant unresolved accepted effect;
// operational readers continue to expose the facts needed for reconciliation.
internal static class GasOwnedAppliedSources
{
    internal static async Task<ImmutableArray<GasOwnedAppliedSourceConflict>> Read(NpgsqlConnection connection,PreviewDatabase database,
        InputFrame frame,MarketSnapshot snapshot,ImmutableArray<Guid> books,CancellationToken ct)
    {
        if(frame.ContextId is not {} context||snapshot.Knowledge?.ContextId!=context||snapshot.ValuationAt!=frame.At||books.IsDefaultOrEmpty)
            throw Error("Retain the exact source certificate, instant and explicit book scope for applied gas sources.");
        var native=await LifecycleNative.Read(connection,"SELECT security.read_gas_owned_applied_sources($1,$2,$3,$4::uuid[])::text",ct,
            database.Manifest.CatalogId,context,frame.At,books.ToArray());
        if(native.Text("stateProfile")!="known_gas_applied_source_conflicts_v1"||native.Id("contextId")!=context||native.Instant("asOf")!=frame.At||
            native.Instant("knownAt")!=snapshot.Knowledge.KnownAt||DateWorkspace.Date(native.Text("effectiveOn"))!=frame.BusinessDate)
            throw Error("Applied gas sources cannot replace the certified effective or knowledge coordinates.");
        var admission=new GasOwnedSourceReadAdmission(database.Manifest.CatalogId,context,frame.At,native.Instant("authorityUntil"));
        database.RegisterCommitAdmission(connection,admission);
        var result=Materialize(native.GetProperty("conflicts"),snapshot);
        await admission.RequireCommit(connection,ct);return result;
    }
    internal static ImmutableArray<GasOwnedAppliedSourceConflict> Materialize(JsonElement conflicts,MarketSnapshot snapshot)
    {
        if(snapshot.Knowledge is not {} knowledge||conflicts.ValueKind!=JsonValueKind.Array)throw Error("Retain the complete certified native applied-source conflict list.");
        var result=conflicts.EnumerateArray().Select(row=>new GasOwnedAppliedSourceConflict(row.Text("kind") switch{
            "position"=>GasAppliedEffectKind.Position,"consumption"=>GasAppliedEffectKind.Consumption,"settlement"=>GasAppliedEffectKind.Settlement,
            _=>throw Error("Unknown accepted gas effect kind.")},row.Id("effectId"),row.Id("positionId"),row.Id("accountId"),row.Text("sourceKind") switch{
                "gas_evidence"=>GasAppliedSourceKind.OperationalEvidence,"market_observation"=>GasAppliedSourceKind.MarketObservation,
                _=>throw Error("Unknown accepted gas source kind.")},row.Id("acceptedSourceId"),[..row.GetProperty("currentSourceIds").EnumerateArray().Select(v=>v.GetGuid())],
            new(row.GetProperty("publicationOrder").GetInt32(),row.Instant("effectiveAt"),row.Instant("knownAt")))).ToImmutableArray();
        if(result.Any(c=>c.EffectId==Guid.Empty||c.PositionId==Guid.Empty||c.AccountId==Guid.Empty||c.AcceptedSourceId==Guid.Empty||c.CurrentSourceIds.Any(id=>id==Guid.Empty)||
            c.CurrentSourceIds.Distinct().Count()!=c.CurrentSourceIds.Length||c.Time.PublicationOrder<1||c.Time.KnownAt>knowledge.KnownAt)||
            result.Select(c=>(c.Kind,c.EffectId,c.SourceKind)).Distinct().Count()!=result.Length)
            throw Error("Retain distinct accepted effects and their exact visible source heads.");
        return result;
    }
    internal static void RequireClear(ImmutableArray<GasOwnedAppliedSourceConflict> conflicts,DateTimeOffset through)
    {
        if(conflicts.IsDefault||!TradingInstant.IsSupported(through))throw Error("Retain the complete applied-source proof and exact valuation horizon.");
        if(conflicts.Any(c=>c.Time.EffectiveAt<=through))
            throw new MarketDataException("gas_owned_reconciliation","Accepted gas effects reference corrected source evidence. Reconcile the identified quantity, title or cash effects before complete valuation.");
    }
    private static MarketDataException Error(string message)=>new("gas_owned_applied_source",message);
}
