using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Calculation;

namespace OpenEtrm.Api;

internal static partial class LifecyclePortfolioProjection
{
    private sealed record FinalAdmission(Guid Catalog,Guid Definition,string Projection,string Official,Guid Contract,Guid Context,
        DateTimeOffset At,DateOnly Day,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var command=ScheduleStore.Command(connection,"SELECT security.require_lifecycle_projection_input($1,$2,$3,$4,$5,$6,$7,$8,$9)",
                Catalog,Definition,Projection,Official,Contract,Context,At,Day,Until);await command.ExecuteNonQueryAsync(ct);
        }
    }
    private static async Task<(ImmutableArray<LifecycleInputFinal> Inputs,ImmutableArray<JsonElement> Evidence)> FinalInputs(NpgsqlConnection connection,
        PreviewDatabase database,LifecycleMarketSource source,ImmutableArray<ILifecycleProjection> components,
        ImmutableDictionary<CurveRequest,CurveKey> officialSources,LifecycleProjectionPolicy policy,DateTimeOffset horizon,ConnectedCurvePlan plan,CancellationToken ct)
    {
        var uses=components.SelectMany(c=>c.MarketUsesFor(horizon).Where(u=>c.DependencySource(u.Market).HasSameInputs(source.Snapshot)))
            .GroupBy(u=>u.Market).ToImmutableDictionary(g=>g.Key,g=>g.Max(u=>u.Through));
        var selected=components.SelectMany(c=>c.InputFinals).Where(f=>f.Market.Curve.Revision==source.Snapshot.Definitions)
            .GroupBy(f=>f.Market).ToImmutableDictionary(g=>g.Key,g=>g.Select(f=>f.At).Distinct().Single());
        var required=source.Data.Model.LifecycleFinals(uses,source.Snapshot.ValuationAt,selected,LifecycleProjection.ExpiryTreatment(policy));
        var inputs=ImmutableArray.CreateBuilder<LifecycleInputFinal>();var evidence=ImmutableArray.CreateBuilder<JsonElement>();
        foreach(var (market,freezeAt) in required.OrderBy(p=>p.Key.Curve.Code,StringComparer.Ordinal).ThenBy(p=>p.Key.Contract.Id))
        {
            var at=selected.GetValueOrDefault(market,freezeAt);
            var official=officialSources.GetValueOrDefault(market,market.Curve);var snapshot=source.Snapshot;var context=snapshot.Knowledge!.ContextId;
            var native=await LifecycleNative.Read(connection,"SELECT security.read_lifecycle_projection_final($1,$2,$3,$4,$5,$6,$7,$8,$9)::text",ct,
                database.Manifest.CatalogId,snapshot.Definitions.RevisionId,market.Curve.Code,official.Code,market.Contract.Id,at,context,snapshot.ValuationAt,source.Frame.Time.Date);
            database.RegisterCommitAdmission(connection,new FinalAdmission(database.Manifest.CatalogId,snapshot.Definitions.RevisionId,market.Curve.Code,
                official.Code,market.Contract.Id,context,snapshot.ValuationAt,source.Frame.Time.Date,native.Instant("authorityUntil")));
            if(native.Id("contextId")!=context||native.Instant("knownAt")!=snapshot.Knowledge.KnownAt||native.Id("definitionRevisionId")!=snapshot.Definitions.RevisionId||
                native.Id("contractId")!=market.Contract.Id||native.Text("projectionCurve")!=market.Curve.Code||native.Text("officialCurve")!=official.Code||native.Instant("finalAt")!=at)
                throw Error("projection_input_evidence","Every future final input requires its exact source context, selected market and final occurrence.");
            KnownLifecycleInputFinal? actual=null;
            if(native.GetProperty("observation").ValueKind!=JsonValueKind.Null)
            {
                var row=native.GetProperty("observation");actual=new(new(official,market.Contract),source.Data.ReadObservation(row),row.Instant("known_from"),context);
            }
            inputs.Add(new(market,at,actual));
            var openingInputs=plan.Primitives.Select(p=>p.Source).Where(p=>p.Request==market).Select(p=>new{
                snapshotId=p.Sample?.SnapshotId??snapshot.Id,at=p.Sample?.ValuationAt??snapshot.ValuationAt,
                observationId=p.Observation.Key.Id,seriesId=p.Input.Series.Id,use=(p.Sample?.OriginalUse??p.Input.Use).ToString()})
                .Distinct().OrderBy(p=>p.snapshotId).ThenBy(p=>p.seriesId).ThenBy(p=>p.use,StringComparer.Ordinal).ThenBy(p=>p.observationId).ToArray();
            if(openingInputs.Length==0)throw Error("projection_input_evidence","Every final-input declaration must retain its actual prepared opening primitive sources.");
            var retained=native.EnumerateObject().Where(p=>p.Name!="authorityUntil").ToDictionary(p=>p.Name,p=>p.Value.Clone());
            retained.Add("openingInputs",JsonSerializer.SerializeToElement(openingInputs,RiskJobs.Json));
            evidence.Add(JsonSerializer.SerializeToElement(retained,RiskJobs.Json));
        }
        return(inputs.ToImmutable(),evidence.ToImmutable());
    }
}
