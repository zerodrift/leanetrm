using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Commands;

namespace OpenEtrm.Api;

internal sealed partial class PointTraderMarkStore
{
    internal static void Offer(JsonObject reply,PreviewSession session)
    {
        if(reply["found"]?.GetValue<bool>()==false)return;
        var body=reply["command"]!.Deserialize<PointTraderMarkCommand>(PointTraderMarkCommands.Json)!;
        var operation=reply["operationId"]!.GetValue<Guid>();
        var original=body.OriginalInputs;
        var frame=original is null?null:session.Frames.GetValueOrDefault(original.SnapshotId);
        string? reason=original is null?"No exact original input selection was retained with this publication.":
            frame is null?"The original input selection is no longer in this session.":null;
        if(reason is null)
        {
            try{if(PointTraderMarkInputSelection.Fingerprint(frame!)!=original!.Fingerprint)reason="The original input selection changed.";}
            catch(MarketDataException){reason="The original selection does not retain a complete exact source matrix.";}
        }
        reply["editedSelection"]=reason is not null?JsonSerializer.SerializeToNode(new{state="unavailable",reason},PointTraderMarkCommands.Json):
            JsonSerializer.SerializeToNode(new{state="available",selectionId=OperationIdentity.For("point_trader_mark_edited_inputs_v1",operation),
                originalSnapshotId=original!.SnapshotId,original.Fingerprint},PointTraderMarkCommands.Json);
    }

    internal async Task<InputFrame> Inputs(NpgsqlConnection c,PointTraderMarkInputsRequest request,InputFrame original,CancellationToken ct)
    {
        var status=await NativeStatus(c,request.OperationId,ct);await RequireOwner(c,request.OwnerId,ct);
        if(!status.GetProperty("found").GetBoolean())throw InputsUnavailable("The original market publication has no accepted receipt.");
        var body=PointTraderMarkCommands.Normalize(Catalog,status.GetProperty("request").GetProperty("body"));
        var saved=body.Deserialize<PointTraderMarkCommand>(PointTraderMarkCommands.Json)!;
        var provenance=saved.OriginalInputs;
        if(provenance is null||request.OriginalSnapshotId!=original.Id||provenance.SnapshotId!=request.OriginalSnapshotId||
            provenance.Fingerprint!=request.Fingerprint||PointTraderMarkInputSelection.Fingerprint(original)!=request.Fingerprint||
            request.SelectionId!=OperationIdentity.For("point_trader_mark_edited_inputs_v1",request.OperationId))
            throw InputsUnavailable("Retain the exact original selection and accepted publication provenance.");
        var outcome=status.GetProperty("outcome");PointTraderMarkReceipts.Require(Catalog,request.OperationId,body,outcome);
        var target=saved.Target;var source=new CatalogueSource(target.DefinitionRevisionId,target.ReferenceCatalogId,target.ReferenceRevisionId);
        if(original.At!=outcome.Instant("observedAt")||original.BusinessDate!=PublishedSchedules.ContextDate(
            await status.GetProperty("sourceInputs").Deserialize<ReadInputCoordinates>(PointTraderMarkCommands.Json)!.Resolve(c,Catalog,ct)))
            throw InputsUnavailable("The accepted point and original selection have different business coordinates.");

        // Inspect the complete original matrix, independently of the producer's
        // predecessor. Preserve every other source, rate, pin and frozen use.
        var until=status.Instant("authorityUntil");
        foreach(var part in original.SourceInputs!.Values)
        {
            await MarketBundle.Require(c,part.Source.Apply(database.Manifest),ct);
            await MarketBundle.RequireObservationPins(c,part.Source.Apply(database.Manifest),part.ObservationIds,ct);
            database.RegisterCommitAdmission(c,new PointTraderMarkAdmission(Catalog,part.Source,false,until));
        }
        var pins=original.SourceInputs.GetValueOrDefault(source.DefinitionRevisionId)?.ObservationIds??[];
        var selected=await PreviewDatabase.Rows(c,"""
            SELECT jsonb_build_object('id',id)::text FROM market_data.observations
            WHERE catalog_id=$1 AND definition_revision_id=$2 AND series_id=$3 AND source_code=$4
             AND role='trader_mark' AND observed_at=$5 AND id=ANY($6::uuid[])
            """,[Catalog,target.DefinitionRevisionId,target.SeriesId,target.Source,original.At,pins.ToArray()],ct);
        var edited=PointTraderMarkInputSelection.Edit(original,request.OperationId,source,target.CurveCode,target.ContractId,
            outcome.Id("observationId"),[..selected.Select(row=>row.Id("id"))]);
        await MarketBundle.Require(c,source.Apply(database.Manifest),ct);
        await MarketBundle.RequireObservationPins(c,source.Apply(database.Manifest),[outcome.Id("observationId")],ct);
        database.RegisterCommitAdmission(c,new PointTraderMarkAdmission(Catalog,source,false,until));
        return edited;
    }
    private static MarketDataException InputsUnavailable(string message)=>new("point_trader_mark_inputs_unavailable",message);
}
