using System.Text.Json;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// HTTP entry and the explicit synthetic seed use the same authorized booking
// operation, including independently retained Side schedules and replay.
public sealed class SwapBookingOperations(PreviewDatabase database)
{
    public async Task<JsonElement> Save(string profile,InputFrame frame,SwapWriteRequest body,bool amend,CancellationToken cancellation)
    {
        if(frame.ContextId is not null||body.KnowledgeMode!="current")
            throw new MarketDataException("historical_read_only","Return to working data to book or amend.");
        if(amend!=(body.TradeId is not null))
            throw new MarketDataException("swap_input","Choose booking or latest-head amendment explicitly.");
        if(body.SnapshotId!=frame.Id)
            throw new MarketDataException("snapshot_missing","Select the exact booking frame.");
        var permission=amend?"AmendTrade":"BookTrade";
        var store=new SwapStore(database);var retained=new RetainedScheduleStore(database);
        var terms=await database.WithIdentity(profile,permission,async(connection,ct)=>
        {
            var status=await store.Status(connection,body.OperationId,ct);
            if(status.TryGetProperty("tradeId",out _))return(Terms:(RetainedScheduleTerms?)null,Command:(JsonElement?)null);
            var data=await database.Read(connection,ct,observationIds:frame.ObservationIds,observationScope:ObservationReadScope.ExactPins);
            var prepared=await store.PrepareScheduleTerms(connection,data,frame,body,ct);
            return(Terms:(RetainedScheduleTerms?)prepared,Command:(JsonElement?)await store.ScheduleCommand(connection,frame,body,prepared.Reference,ct));
        },cancellation);
        if(terms.Terms is not null)
            await retained.Stage(profile,body.OperationId,terms.Command!.Value,permission,body.TradeId,body.ExpectedRevision,terms.Terms.Generate(cancellation),cancellation);
        return await database.WithIdentity(profile,permission,async(connection,ct)=>
        {
            if(terms.Terms is not null)await retained.Lock(connection,body.OperationId,ct);
            await using(var gate=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849102)"))await gate.ExecuteNonQueryAsync(ct);
            var receipt=await store.Save(connection,await database.Read(connection,ct,observationIds:frame.ObservationIds,
                observationScope:ObservationReadScope.ExactPins),frame,body,ct);
            if(terms.Terms is not null)
            {
                var instrument=await retained.Publish(connection,body.OperationId,ct);
                await store.VerifyPublishedPrograms(connection,await database.Read(connection,ct,observationIds:frame.ObservationIds,
                    observationScope:ObservationReadScope.ExactPins),frame,body.OperationId,instrument,ct);
            }
            return receipt;
        },cancellation);
    }
}
