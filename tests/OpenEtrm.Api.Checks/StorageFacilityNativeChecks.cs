using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

// The native qualifier consumes one source produced through ordinary booking
// and facility operations. No legacy trade or source rows are manufactured.
internal static class StorageFacilityNativeChecks
{
    internal static async Task RunNative(string path)
    {
        using var packet=JsonDocument.Parse(await File.ReadAllTextAsync(path));var fixture=packet.RootElement;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Storage source qualification requires its owned disposable cluster.");
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var seed=manifest.Frames.Single(f=>f.At==DateTimeOffset.Parse("2020-12-18T16:00:00Z",System.Globalization.CultureInfo.InvariantCulture));
        var frame=new InputFrame(Guid.NewGuid(),"Synthetic source booking inputs",seed.At,new(2020,12,18),[..seed.ObservationIds],
            seed.CurveContracts.ToImmutableDictionary(p=>p.Key,p=>p.Value.ToImmutableArray())){PpaBundle=seed.PpaBundle};
        var gas=new PhysicalGasStore(database);
        var catalog=await database.WithIdentity("trader","ReadTrades",async(c,ct)=>
            J(await TradingBookingRead.Run(database,c,frame,(data,pinned)=>gas.CatalogRead(c,data,pinned,ct),ct,PhysicalMarketSelection.Gas)),default);
        var point=catalog.GetProperty("locations").EnumerateArray().Single(p=>p.Text("deliveryPoint")==fixture.Text("pointCode"));
        if(point.GetProperty("valuation").ValueKind==JsonValueKind.Null)throw new Exception("The explicitly selected synthetic gas point needs its configured valuation mapping.");
        var calendar=catalog.GetProperty("sideCalendars").EnumerateArray().Single(c=>c.Text("code")=="test_utc_every_day")
            .GetProperty("reference").Deserialize<ReferenceKey>(LifecycleNative.Json);
        SideCalendarPolicyInput Policy(bool payment)=>new("side_calendar_policy_v1",new(true,calendar),new(false,null),new(payment,payment?calendar:null));
        var book=catalog.GetProperty("books").EnumerateArray().Single(b=>b.Text("name")=="Book A").Id("id");
        var entity=catalog.GetProperty("legalEntities").EnumerateArray().Single(e=>e.Text("code")=="entity_1");
        var counterparty=catalog.GetProperty("counterparties").EnumerateArray().Single(e=>e.Text("code")=="party_1");
        var booking=new GasCompositionRequest(frame.Id,PhysicalGasStore.Convention,"current",GasCompositionStore.Profile,Guid.NewGuid(),book,
            point.Id("definitionRevisionId"),catalog.OptionalId("gasSetupId"),false,entity.Id("id"),entity.GetProperty("revision").GetInt32(),
            counterparty.Id("id"),counterparty.GetProperty("revision").GetInt32(),
            [new("synthetic_receipt",point.Text("pipeline"),point.Text("zone"),point.Text("deliveryPoint"),point.Text("product"),"receive",
                "2021-01-01","2021-01-02","period_total_equal_days","1000",null){CalendarPolicy=Policy(false)}],
            [new("synthetic_payment","consideration","pay","synthetic_receipt","delivery","3",null,"2021-02-02T00:00:00Z"){CalendarPolicy=Policy(true)}]);
        var compositions=new GasCompositionStore(database);
        var booked=await compositions.Book("trader",frame,booking,false,default);
        var replay=await compositions.Book("trader",frame,booking,false,default);
        if(!JsonElement.DeepEquals(booked,replay))throw new Exception("Ordinary gas booking lost its original request outcome.");
        var trade=booked.Id("tradeId");var quantities=new FacilityQuantityStore(database);
        var context=new FacilityQuantityContext("2021-01-15","current",null,FacilityQuantityStore.Convention);
        var directory=await database.WithIdentity("trader","ReadFacilityQuantities",async(c,ct)=>J(await quantities.Directory(c,new(context),null,ct)),default);
        if(directory.GetProperty("choicesTruncated").GetBoolean())throw new Exception("The disposable source fixture must expose its complete exact obligation set.");
        var target=directory.GetProperty("choices").EnumerateArray().Single(t=>t.Id("tradeId")==trade&&t.Text("day")=="2021-01-01");
        var obligation=target.Id("obligationId");var owner=directory.Text("ownerId");
        async Task<JsonElement> Save(object command,Guid? operation=null)
        {
            var request=new FacilityWriteRequest(context,operation??Guid.NewGuid(),fixture.Text("at"),J(command),owner);
            var result=await database.WithIdentity("trader","RecordFacilityQuantities",(c,ct)=>quantities.Save(c,request,null,ct),default);
            var retry=await database.WithIdentity("trader","RecordFacilityQuantities",(c,ct)=>quantities.Save(c,request,null,ct),default);
            if(!JsonElement.DeepEquals(result,retry))throw new Exception("A facility source command lost its exact original outcome.");
            return result.GetProperty("outcome");
        }
        var created=await Save(new{kind="create",obligationId=obligation,day="2021-01-01",facility=point.Text("deliveryPoint")});
        var ledger=created.Id("ledgerId");
        var source=await Save(new{kind="source",ledgerId=ledger,expectedPreviousId=(Guid?)null,quantity="100",evidence="Synthetic reported physical source"});
        var sourceId=source.Id("revisionId");
        var allocation=new FacilityAllocationInput(obligation,target.GetProperty("tradeRevision").GetInt32(),"60");
        _=await database.WithIdentity("trader","RecordFacilityQuantities",(c,ct)=>quantities.Preview(c,new(context,ledger,sourceId,null,[allocation],owner),null,ct),default);
        var allocated=await Save(new{kind="allocate",ledgerId=ledger,expectedPreviousId=(Guid?)null,sourceRevisionId=sourceId,
            evidence="Synthetic independent contract allocation",allocations=new[]{new{obligationId=obligation,tradeRevision=allocation.TradeRevision,quantity="60"}}});
        var read=await database.WithIdentity("trader","ReadFacilityQuantities",async(c,ct)=>J(await quantities.Read(c,new(context,ledger),null,ct)),default);
        var facts=read.GetProperty("facts");var balance=read.GetProperty("balance");var nativeLedger=facts.GetProperty("ledger");
        if(facts.GetProperty("source").Id("id")!=sourceId||facts.GetProperty("source").Text("quantity")!="100"||
            balance.Text("allocated")!="60"||balance.Text("available")!="40"||facts.GetProperty("allocations").GetArrayLength()!=1||
            nativeLedger.Text("quantity_code")!=fixture.Text("quantityCode"))
            throw new Exception("The one physical source and independent sixty-unit allocation did not retain100/60/40 in exact native quantity.");
        var output=J(new{ledgerId=ledger,sourceRevisionId=sourceId,obligationId=obligation,tradeId=trade,allocationRevisionId=allocated.Id("revisionId"),
            allocated="60",tradeRevision=allocation.TradeRevision,day="2021-01-01",bookId=book,quantity="100",from=nativeLedger.Text("native_from"),through=nativeLedger.Text("native_to"),
            pipeline=point.Text("pipeline"),zone=point.Text("zone"),point=point.Text("deliveryPoint"),product=point.Text("product"),quantityCode=nativeLedger.Text("quantity_code"),clock=point.Text("clock")});
        var outputPath=path+".source.json";
        await File.WriteAllTextAsync(outputPath,output.GetRawText());
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(outputPath,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        Console.WriteLine("PASS managed source: ordinary gas booking, exact native day, one reported source100, independent allocation60/available40 and original request outcomes");
    }

    private static JsonElement J(object value)=>JsonSerializer.SerializeToElement(value,LifecycleNative.Json);
}
