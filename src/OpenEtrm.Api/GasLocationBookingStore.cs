using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

internal sealed class GasLocationBookingStore(PreviewDatabase database,GasCompositionStore gas)
{
    readonly RetainedScheduleStore schedules=new(database);

    internal async Task<object> Book(string profile,InputFrame frame,GasLocationBooking input,CancellationToken ct)
    {
        var terms=GasLocationEntry.Terms(input);
        var payload=JsonSerializer.Serialize(new{input.Agreements,terms},OpenEtrm.MarketData.Trading.ScheduleDefinition.Json);
        var prior=await database.WithIdentity(profile,"BookTrade",(c,t)=>Read(c,input.OperationId,payload,t),ct);
        if(prior is not null)return prior.Value;

        foreach(var leg in terms)
        {
            var preparation=await database.WithIdentity(profile,"BookTrade",(c,t)=>gas.Prepare(c,frame,leg,false,t),ct);
            await schedules.Stage(profile,leg.OperationId,leg,"BookTrade",null,null,preparation.Generate(ct),ct);
        }
        return await database.WithIdentity(profile,"BookTrade",async(c,t)=>
        {
            // Serialize both child publications. A retry waiting behind another writer reads its outcome.
            foreach(var leg in terms)await schedules.Lock(c,leg.OperationId,t);
            if(await Read(c,input.OperationId,payload,t) is {} done)return done;
            foreach(var leg in terms)
            {
                await gas.Save(c,frame,leg,false,t);
                await schedules.Publish(c,leg.OperationId,t);
            }
            return await LifecycleNative.Read(c,"SELECT security.record_gas_location_booking($1,$2,$3::jsonb)::text",t,
                database.Manifest.CatalogId,input.OperationId,payload);
        },ct);
    }

    internal async Task<JsonElement?> Read(NpgsqlConnection c,Guid operation,string? payload,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.gas_location_receipt($1,$2,$3::jsonb)::text",
            database.Manifest.CatalogId,operation,(object?)payload??DBNull.Value);
        var receipt=await q.ExecuteScalarAsync(ct);
        if(receipt is not string value)return null;
        return JsonSerializer.Deserialize<JsonElement>(value);
    }
}
