using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed partial class SwapStore
{
    internal async Task<JsonElement> ScheduleCommand(NpgsqlConnection connection,InputFrame frame,SwapWriteRequest body,
        ReferenceRevision reference,CancellationToken ct)
    {
        if(body.Scheduled is {} scheduled)body=body with{Scheduled=CanonicalScheduled(scheduled)};
        await using var command=ScheduleStore.Command(connection,"SELECT security.swap_schedule_command($1::jsonb,$2,$3,$4)::text",
            JsonSerializer.Serialize(body,new JsonSerializerOptions(JsonSerializerDefaults.Web)),reference.CatalogId,reference.RevisionId,frame.At);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    internal async Task VerifyPublishedPrograms(NpgsqlConnection connection,PreviewData data,InputFrame frame,Guid operation,Guid instrument,CancellationToken ct)
    {
        async Task<JsonElement> Read()
        {
            await using var command=ScheduleStore.Command(connection,"SELECT security.read_swap_publication($1,$2,$3)::text",manifest.CatalogId,operation,instrument);
            return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
        }
        var retained=await Read();var row=retained.GetProperty("terms");
        if(row.Id("definition_revision_id")!=data.DefinitionRevision.RevisionId)
            (data,frame)=await ScheduledMarket(connection,data,frame,row.Id("definition_revision_id"),ct);
        var body=Terms(data,row,frame.Id);
        var actual=ResolveAny(data,body,instrument,row.OptionalId("fixed_side_id"),row.OptionalId("floating_side_id"));
        var identities=retained.GetProperty("identities").EnumerateArray().Select(value=>new RetainedPublicationIdentity(
            value.GetProperty("slot").GetInt32(),value.GetProperty("sideId").GetGuid(),value.GetProperty("economicIdentity").GetString()!,
            value.GetProperty("pricingBindingIdentity").GetString()!)).ToArray();
        RetainedSchedulePublication.Require(actual.Instrument.Sides.Select(member=>member.Side).ToArray(),identities);
        _=await Read(); // Recheck all current authority after compilation, before the booking commits.
    }
}
