using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData.Trading;

// Stage through normal authorized, sealed RPCs; invoke final native admission
// directly so HTTP/domain preflight cannot mask its own contract.
internal static class LngAdmissionChecks
{
    internal static async Task Run(string path)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new Exception("Use the owned preview runner.");
        await using var database=new PreviewDatabase(root);
        var original=JsonSerializer.Deserialize<PhysicalLngWriteRequest>(File.ReadAllText(path),ScheduleDefinition.Json)!;
        var seed=database.Manifest.Frames.Single(f=>f.At==DateTimeOffset.Parse("2020-12-18T16:00:00Z",System.Globalization.CultureInfo.InvariantCulture));
        var frame=new InputFrame(original.SnapshotId,"Synthetic admission check",seed.At,[..seed.ObservationIds],seed.CurveContracts.ToImmutableDictionary(p=>p.Key,p=>p.Value.ToImmutableArray()));
        var store=new PhysicalLngStore(database);
        var serial=0;
        foreach(var mutation in new[]{"dated_root","permuted_rows","missing_publication"})
        {
            var body=original with{OperationId=Guid.NewGuid(),FixedPrice="10.00"+(++serial).ToString(System.Globalization.CultureInfo.InvariantCulture)};
            var preparation=(await database.WithIdentity("trader","BookTrade",(c,ct)=>store.Prepare(c,frame,body,ct),CancellationToken.None)).Generate(CancellationToken.None);
            if(mutation!="missing_publication")
            {
                var cash=preparation.Schedules[1];
                int Address(int n)=>mutation=="permuted_rows"&&n<2?1-n:n;
                var rows=cash.Rows.Select(r=>mutation=="dated_root"&&r.Key=="side/pv"
                    ?r with{DeliveryFrom=new(2021,1,3),DeliveryTo=new(2021,1,15)}:r with{Ordinal=Address(r.Ordinal)}).OrderBy(r=>r.Ordinal).ToArray();
                var changed=new ScheduleDefinition(cash.SideId,cash.Family,cash.EconomicIdentity,cash.PricingBindingIdentity,cash.SchemaVersion,cash.GeneratorVersion,
                    rows,cash.Edges.Select(e=>e with{Target=Address(e.Target),Source=Address(e.Source)}).ToArray(),cash.Bindings.Select(b=>b with{Row=Address(b.Row)}).ToArray(),
                    cash.Roots.Select(r=>r with{Row=Address(r.Row)}).ToArray(),cash.Ports,cash.Grids);
                preparation=preparation with{Schedules=[preparation.Schedules[0],changed]};
            }
            var retained=new RetainedScheduleStore(database);
            await retained.Stage("trader",body.OperationId,body,"BookTrade",null,null,preparation,CancellationToken.None);
            var expected=mutation=="missing_publication"?"side_schedule_missing":"lng_schedule_rows";
            try
            {
                await database.WithIdentity("trader","BookTrade",async(c,ct)=>
                {
                    await retained.Lock(c,body.OperationId,ct);
                    _=await store.Save(c,frame,body,ct);
                    if(mutation=="missing_publication")return true; // Deferred commit constraint must refuse.
                    await retained.Publish(c,body.OperationId,ct);
                    throw new Exception("Native LNG admission accepted "+mutation);
                },CancellationToken.None);
                throw new Exception("Native LNG admission committed "+mutation);
            }
            catch(PostgresException error)when(error.SqlState=="P0001"&&error.MessageText==expected) { }
            var status=await database.WithIdentity("trader","ReadTrades",(c,ct)=>store.Status(c,body.OperationId,ct),CancellationToken.None);
            if(status.TryGetProperty("tradeId",out _))throw new Exception("Failed admission left a booked LNG trade.");
        }
        Console.WriteLine("PASS native LNG admission: normally sealed dated-root and permuted DAGs refuse; missing publication rolls back at commit");
    }
}
