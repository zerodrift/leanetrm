using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

internal static class GasChargeClockChecks
{
    internal static async Task Run(string path)
    {
        var body=JsonSerializer.Deserialize<GasCompositionRequest>(File.ReadAllText(path),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var template=body.ChargeSides!.Value.Single();
        await using var database=new PreviewDatabase(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")!);
        var store=new GasCompositionStore(database);
        var staged=new RetainedScheduleStore(database);
        var frame=await database.WithIdentity("trader","ReadMarketData",(c,ct)=>MarketCloses.Frame(c,database.Manifest.CatalogId,body.SnapshotId,null,ct),default);
        var terms=await database.WithIdentity("trader","BookTrade",(c,ct)=>store.Prepare(c,frame,body with{ChargeSides=[]},false,ct),default);
        var physical=((GasCompositionInstrument)terms.Instrument).Physical.Single();
        var gas=physical.Side;
        var quote=gas.Assignments.Single().Quote;
        var clock=gas.Quantities.Clock;
        var calendar=await database.WithIdentity("trader","ReadMarketData",(c,ct)=>
            TradingBookingRead.Run(database,c,frame,async(data,_)=>SideCalendarPolicies.Resolve(
                await SideCalendarPolicies.Prepare(c,data,[template.CalendarPolicy],ct),template.CalendarPolicy),ct),default);
        var price=new UnitPrice(ExactDecimal.Parse("1"),quote);
        var errors=new List<string>();
        foreach(var basis in new[]{ChargeBasis.LumpSum,ChargeBasis.Capacity})
        foreach(var first in new[]{2,1})
        {
            var from=new DateOnly(1,1,first);
            var to=from.AddDays(1);
            var payment=new DateTimeOffset(1,1,first+1,0,0,0,TimeSpan.Zero);
            var lump=basis==ChargeBasis.LumpSum;
            var charge=template with{Basis=lump?"lump_sum":"capacity", Price="1",DailyQuantity=lump?null:"1",
                From=lump?null:from.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture), To=lump?null:to.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),PaymentDay=lump?null:2,
                PaymentAt=lump?payment.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture):null};
            // Construct the fixed Side directly so the native writer receives matching,
            // fully sealed programs even for the date refused by managed ticket admission.
            var side=lump?GasChargeSides.LumpSum(Guid.NewGuid(),price,gas.UnitQuantity,clock,payment,calendar,quote):
                GasChargeSides.Capacity(Guid.NewGuid(),price,gas.UnitQuantity,clock,from,to,2,calendar,quote);
            if(side.DeliveryFrom!=from||clock.Boundary(to)!=new DateTimeOffset(1,1,first,10,0,0,TimeSpan.Zero))
                throw new Exception("Expected an owned day with a midnight UTC+14 clock.");
            var component=new GasChargeComponent(charge.Code,side,basis,physical.Code,ExactDecimal.Parse("1"),ExactDecimal.Parse("-1"));
            var instrument=new GasCompositionInstrument(Guid.NewGuid(),[physical],[],charges:[component]);
            var command=body with{ChargeSides=[charge],OperationId=Guid.NewGuid()};
            await staged.Stage("trader",command.OperationId,command,"BookTrade",null,null,(terms with{Instrument=instrument}).Generate(default),default);
            try
            {
                await database.WithIdentity("trader","BookTrade",async(c,ct)=>{
                    await staged.Lock(c,command.OperationId,ct);
                    var result=await store.Save(c,frame,command,false,ct);
                    await staged.Publish(c,command.OperationId,result.Id("instrumentId"),ct);
                    return true;
                },default);
                if(first==1)errors.Add($"{basis} retained an unrepresentable first UTC boundary");
            }
            catch(PostgresException e) when(first==1&&e.MessageText=="gas_day_boundary") {}
        }
        if(errors.Count>0)throw new Exception(string.Join(", ",errors));
        Console.WriteLine("PASS native year-one lump sum and capacity first UTC boundaries, with adjacent accepted days");
    }
}
