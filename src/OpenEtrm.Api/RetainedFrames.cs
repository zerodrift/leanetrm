using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using Npgsql;

namespace OpenEtrm.Api;

// The shared execution envelope used by jobs and agent turns. Public snapshot
// summaries do not carry frozen input uses or the complete source matrix.
internal static class RetainedFrames
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    // The native risk binding attests the stored matrix under this live lease.
    // Loading its exact pins does not need the interactive close directory.
    internal static async Task<PreviewData> ReadBoundJobInputs(PreviewDatabase database,NpgsqlConnection connection,
        InputFrame frame,Guid revision,ReferenceReadScope references,CancellationToken ct)
    {
        var data=await database.Read(connection,ct,selectedRevision:revision,observationIds:frame.ObservationIds,
            referenceScope:references,observationScope:ObservationReadScope.ExactPins);
        MarketInputSelection.Require((data,frame),frame,CatalogueSource.From(data));
        return data;
    }
    internal static JsonObject Record(InputFrame frame)
    {
        var pinned=JsonSerializer.SerializeToNode(frame,Json)!.AsObject();
        // Public frame JSON omits input uses; the retained execution envelope
        // carries them explicitly so a frozen fact never becomes shockable.
        pinned["frozenObservationIds"]=JsonSerializer.SerializeToNode(
            frame.FrozenObservationIds.IsDefault?ImmutableArray<Guid>.Empty:frame.FrozenObservationIds,Json);
        pinned["source"]=JsonSerializer.SerializeToNode(frame.Source,Json);
        pinned["sourceInputs"]=JsonSerializer.SerializeToNode(frame.SourceInputs,Json);
        pinned["originCloseId"]=JsonSerializer.SerializeToNode(frame.OriginCloseId,Json);
        pinned["close"]=JsonSerializer.SerializeToNode(frame.Close,Json);
        pinned["swapMarketSelections"]=JsonSerializer.SerializeToNode(frame.SwapMarketSelections,Json);
        pinned["resolvesLatestCorrection"]=frame.ResolvesLatestCorrection;
        Validate(frame);
        return pinned;
    }

    internal static InputFrame Read(JsonNode pinned)
    {
        var frame=pinned.Deserialize<InputFrame>(Json)??throw new JsonException("Missing risk frame.");
        var frozen=pinned["frozenObservationIds"]?.Deserialize<ImmutableArray<Guid>>(Json);
        if(frozen is null||frozen.Value.IsDefault||frozen.Value.Distinct().Count()!=frozen.Value.Length||
            frozen.Value.Any(id=>!frame.ObservationIds.Contains(id)))
            throw new MarketDataException("risk_job_context","Frozen inputs must be an explicit exact subset of the retained observations.");
        if(!pinned.AsObject().ContainsKey("source")||!pinned.AsObject().ContainsKey("sourceInputs"))
            throw Invalid("Retained input selections require explicit source coordinates and matrix fields.");
        frame=frame with{FrozenObservationIds=frozen.Value,Source=pinned["source"]?.Deserialize<CatalogueSource>(Json),
            SourceInputs=pinned["sourceInputs"]?.Deserialize<ImmutableDictionary<Guid,MarketSourceInputs>>(Json),
            ResolvesLatestCorrection=pinned["resolvesLatestCorrection"]?.GetValue<bool>()??throw Invalid("Declare the retained correction-selection policy.")};
        Validate(frame);
        return frame;
    }

    private static void Validate(InputFrame frame)
    {
        _=frame.Time;
        if(frame.OriginCloseId is not null&&(frame.Source is null||frame.SourceInputs is null))
            throw Invalid("A recorded close requires its exact source input matrix.");
        if(frame.SourceInputs is not {} matrix)return;
        if(matrix.Count is <1 or >256||frame.Source is not {} owner||!matrix.TryGetValue(owner.DefinitionRevisionId,out var primary)||
            primary is null||primary.Source!=owner||!primary.Matches(frame))throw Invalid("The frame and its canonical source projection differ.");
        foreach(var (id,part) in matrix)
        {
            if(part is null||part.Source is null||id!=part.Source.DefinitionRevisionId||id==Guid.Empty||part.Source.ReferenceCatalogId==Guid.Empty||part.Source.ReferenceRevisionId==Guid.Empty||
                part.ObservationIds.IsDefault||part.FrozenObservationIds.IsDefault||part.RootCurves.IsDefault||
                part.RootCurves.Distinct(StringComparer.Ordinal).Count()!=part.RootCurves.Length||part.CurveContracts is null||part.RootCurves.Any(code=>!part.CurveContracts.ContainsKey(code))||
                part.CurveContracts.Any(p=>string.IsNullOrWhiteSpace(p.Key)||p.Value.IsDefault||p.Value.Distinct().Count()!=p.Value.Length))
                throw Invalid("Every source part requires exact coordinates, pins, input uses and ordered distinct members.");
            _=frame.ForMarketSource(part.ObservationIds,part.CurveContracts,part.FrozenObservationIds);
            if(part.ObservationIds.Distinct().Count()!=part.ObservationIds.Length)throw Invalid("Source pins must be distinct.");
        }
    }
    private static MarketDataException Invalid(string message)=>new("risk_job_context",message);
}
