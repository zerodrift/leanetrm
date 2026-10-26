using System.Text.Json;
using System.IO.Compression;
using System.Text;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

static class PowerImportChecks
{
    public static void Run()
    {
        var selection=PowerHistory.SelectableThrough([new(2026,1,1),new(2026,1,2),new(2026,1,4)]);
        Require(selection["2026-01-01"]=="2026-01-02"&&selection["2026-01-04"]=="2026-01-04","delivery ranges stop at missing operating dates");
        var longSelection=PowerHistory.SelectableThrough(Enumerable.Range(0,40).Select(i=>new DateOnly(2026,1,1).AddDays(i)).ToArray());
        Require(longSelection["2026-01-01"]=="2026-01-31","delivery range selector honors the supported 31-day window");
        Require(PowerHistory.SelectableThrough([DateOnly.MaxValue])["9999-12-31"]=="9999-12-31","last supported operating date does not overflow availability");
        var priorCulture=System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture=new("ar-SA");
            var localized=PowerHistory.SelectableThrough([new(2026,1,1)]);
            Require(localized.TryGetValue("2026-01-01",out var end)&&end=="2026-01-01","delivery availability uses ISO dates independently of server culture");
        }
        finally {System.Globalization.CultureInfo.CurrentCulture=priorCulture;}
        var revision=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        var unit=new PriceUnit(new(revision,"usd"),new(revision,"power_mwh"),ExactDecimal.Parse("1"));
        var pjm=JsonSerializer.SerializeToElement(new{pnode_id=51288,datetime_beginning_utc="2026-11-01T05:00:00",total_lmp_da="-17.125",system_energy_price_da="20",congestion_price_da="-37",marginal_loss_price_da="-0.125",version_nbr=2});
        var parsed=PowerImportProtocol.Pjm(pjm,"DA","51288",60,unit);
        Require(parsed.Price.Value.ToString()=="-17.125"&&parsed.Price.Unit==unit&&parsed.Start==DateTimeOffset.Parse("2026-11-01T05:00:00Z"),"PJM exact negative price and UTC interval");
        Reject(()=>PowerImportProtocol.Pjm(pjm,"DA","51217",60,unit));
        var source=TimeZoneDatabase.Installed();
        foreach(var offset in new object[]{-18000,"-18000"})
        {
            var row=JsonSerializer.SerializeToElement(new{code="synthetic_fixed",kind="fixed_offset",fixed_offset_seconds=offset,iana_zone=(string?)null,tzdb_version=(string?)null});
            var retained=ReferenceClockJson.Read(row);
            Require(PowerClock.FromRule(retained,source).StartOfDay(new(2026,11,2))==DateTimeOffset.Parse("2026-11-02T05:00:00Z"),"raw and exact-string clock rows retain the same operating-day boundary");
        }
        var clock=PowerClock.FromRule(new("ercot","iana",null,"America/Chicago",source.Version()),source);
        var fall=PowerImportProtocol.Window(new(2026,11,1),new(2026,11,1),clock,60);
        Require(PowerImportProtocol.Window(new(2026,10,2),new(2026,11,1),clock,60).Length==745,"31 operating days crossing fall DST have 745 hours");
        Require(fall.Length==25,"fall-back operating day has 25 hourly prices");
        Require(PowerImportProtocol.Window(new(2026,3,8),new(2026,3,8),clock,15).Length==92,"spring operating day has 92 settlement intervals");
        JsonElement Report(bool repeat,string price)=>JsonSerializer.SerializeToElement(new{fields=new[]{new{name="deliveryDate"},new{name="hourEnding"},new{name="settlementPoint"},new{name="settlementPointPrice"},new{name="DSTFlag"}},data=new object[][]{new object[]{"2026-11-01","02:00","HB_NORTH",price,repeat}}});
        var first=PowerImportProtocol.Ercot(Report(false,"0"),"DA","HB_NORTH",fall,unit).Single();
        var second=PowerImportProtocol.Ercot(Report(true,"-2.375"),"DA","HB_NORTH",fall,unit).Single();
        Require(first.Start==DateTimeOffset.Parse("2026-11-01T06:00:00Z")&&second.Start==DateTimeOffset.Parse("2026-11-01T07:00:00Z"),"ERCOT repeated hours have distinct UTC identities");
        Require(first.Price.Value.ToString()=="0"&&second.Price.Value.ToString()=="-2.375","zero and negative SPP remain exact typed prices");
        Reject(()=>PowerImportProtocol.Ercot(Report(false,"1"),"DA","HB_HOUSTON",fall,unit));
        Reject(()=>PowerImportProtocol.Ercot(Report(true,"1"),"DA","HB_NORTH",PowerImportProtocol.Window(new(2026,11,2),new(2026,11,2),clock,60),unit));
        Reject(()=>PowerImportProtocol.ValidateFeed("ERCOT","np6-905-cd/spp_node_zone_hub","RT",60));
        var provider=new PowerProviderSettings("PJM","https://example.invalid/api","synthetic-key",4,null,null,null,null,null);
        var hourly=Enumerable.Range(0,24).Select(h=>new {pnode_id=51288,datetime_beginning_utc=$"2026-11-02T{h:D2}:00:00",total_lmp_da="-0.125",version_nbr=1}).ToArray();
        var response=JsonSerializer.Serialize(new{totalRows=24,items=hourly});
        using var http=new HttpClient(new PayloadHandler(response));
        var utc=PowerClock.Capture(TimeZoneInfo.Utc);
        var client=new PowerPriceClient(http,()=>Task.CompletedTask,provider,50,100000);
        var imported=client.Fetch("da_hrl_lmps","DA","51288",60,new(2026,11,2),new(2026,11,2),utc,unit,CancellationToken.None).GetAwaiter().GetResult();
        Require(imported.Length==24&&imported[23].Price.Value.ToString()=="-0.125","complete exact typed HTTP report");
        foreach(var encoding in new[]{"gzip","deflate","br"})
        {
            using var compressedHttp=new HttpClient(new EncodedPayloadHandler(response,encoding));
            var decoded=new PowerPriceClient(compressedHttp,()=>Task.CompletedTask,provider,50,100000).Fetch("da_hrl_lmps","DA","51288",60,new(2026,11,2),new(2026,11,2),utc,unit,CancellationToken.None).GetAwaiter().GetResult();
            Require(decoded.Length==24&&decoded.All(p=>p.Price.Value.ToString()=="-0.125"&&p.Price.Unit==unit),"HTTP compressed reports preserve complete typed prices: "+encoding);
        }
        using(var bomb=new HttpClient(new EncodedPayloadHandler(response+new string(' ',16*1024*1024+1),"gzip")))
            Reject(()=>new PowerPriceClient(bomb,()=>Task.CompletedTask,provider,50,100000).Fetch("da_hrl_lmps","DA","51288",60,new(2026,11,2),new(2026,11,2),utc,unit,CancellationToken.None).GetAwaiter().GetResult(),"response limit");
        using(var corrupt=new HttpClient(new PayloadHandler("not a gzip stream","gzip")))
            Reject(()=>new PowerPriceClient(corrupt,()=>Task.CompletedTask,provider,50,100000).Fetch("da_hrl_lmps","DA","51288",60,new(2026,11,2),new(2026,11,2),utc,unit,CancellationToken.None).GetAwaiter().GetResult(),"invalid compressed");
        // RFC 1950 fixture produced independently with Python's zlib.compress.
        using(var zlibHttp=new HttpClient(new PayloadHandler("","deflate",Convert.FromBase64String("eJy91EEKwjAQheG7zDqVzNjWmmOIO5HQmlACNilt1EXp3S16Al08yGIIw/y7b6Gccns/pddMRkpFIfthGy8LjTE5b4MjU7E0jSLXZp/D4G3n+xBjiL195BsZEi11wVxoOWttPo/U97C9D6N17bZU6B1Ltf0//TSHFG3sJjK8qn9LDCsJrLSHlUpYqYKValjpACs1sNIRVWKYEQwzgmFGMMwIhhnBMCMYZgTDjGCYEQwzQmBGCMwIgRkhPxtxXd8jWg6b"))))
        {
            var decoded=new PowerPriceClient(zlibHttp,()=>Task.CompletedTask,provider,50,100000).Fetch("da_hrl_lmps","DA","51288",60,new(2026,11,2),new(2026,11,2),utc,unit,CancellationToken.None).GetAwaiter().GetResult();
            Require(decoded.Length==24&&decoded[23].Price.Value.ToString()=="-0.125","independently encoded zlib HTTP deflate report");
        }
        foreach(var encoding in new[]{"zstd","gzip, br"})
        {
            using var unsupported=new HttpClient(new PayloadHandler(response,encoding));
            Reject(()=>new PowerPriceClient(unsupported,()=>Task.CompletedTask,provider,50,100000).Fetch("da_hrl_lmps","DA","51288",60,new(2026,11,2),new(2026,11,2),utc,unit,CancellationToken.None).GetAwaiter().GetResult(),"unsupported content encoding");
        }
        using var incompleteHttp=new HttpClient(new PayloadHandler(JsonSerializer.Serialize(new{totalRows=25,items=hourly})));
        Reject(()=>new PowerPriceClient(incompleteHttp,()=>Task.CompletedTask,provider,50,100000).Fetch("da_hrl_lmps","DA","51288",60,new(2026,11,2),new(2026,11,2),utc,unit,CancellationToken.None).GetAwaiter().GetResult());
        using var malformedHttp=new HttpClient(new PayloadHandler("{\"error\":\"synthetic malformed report\"}"));
        Reject(()=>new PowerPriceClient(malformedHttp,()=>Task.CompletedTask,provider,50,100000).Fetch("da_hrl_lmps","DA","51288",60,new(2026,11,2),new(2026,11,2),utc,unit,CancellationToken.None).GetAwaiter().GetResult());
        Console.WriteLine("PASS power imports: exact typed PJM LMP/ERCOT SPP, source identity, native resolution and independent DST expectations");
        DeliveryDesk(unit);
    }
    static void DeliveryDesk(PriceUnit unit)
    {
        Guid Id(int n)=>Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
        Require(PowerHistory.Opening([])is null,"no delivery sources open on no selection");
        Require(PowerHistory.Opening([new(Id(1),"Synthetic without prices",[])])is null,"a source without available days is not an opening");
        // The newest day wins; equal newest days fall to the ordinal name, then the identity.
        var opening=PowerHistory.Opening([
            new(Id(9),"Synthetic older",[new(2026,11,1),new(2026,10,30)]),
            new(Id(2),"Synthetic b",[new(2026,11,2),new(2026,10,31)]),
            new(Id(4),"Synthetic B",[new(2026,10,31),new(2026,11,2)]),
            new(Id(3),"Synthetic B",[new(2026,11,2)]),
            new(Id(1),"Synthetic Z",[new(2026,11,2)])]);
        Require(opening==new DeliveryOpening(Id(3),new(2026,11,2),new(2026,11,2),"newest_available_delivery_day_v1"),"delivery opening is the newest day, ties by ordinal source name then identity");

        var tz=TimeZoneDatabase.Installed();
        var eastern=PowerClock.FromRule(new("eastern","iana",null,"America/New_York",tz.Version()),tz);
        var central=PowerClock.FromRule(new("central","iana",null,"America/Chicago",tz.Version()),tz);
        var published=DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        IntervalActual Row(PowerInterval interval,string price,int minutesLater=0)=>new(interval.Start,interval.End,new(ExactDecimal.Parse(price),unit),published.AddMinutes(minutesLater),Guid.Empty,1);

        // 2026-11-01 in Chicago repeats an hour: 25 hours of quarter-hours, the last four priced 10.
        var fallDay=PowerImportProtocol.Window(new(2026,11,1),new(2026,11,1),central,15);
        var fall=PowerHistory.Summarize(fallDay,[..fallDay.Select((interval,i)=>Row(interval,i>=96?"10":"1"))]);
        Require(fall.Intervals==100&&fall.ExpectedIntervals==100&&fall.MissingIntervals==0,"a fall-back day of quarter-hours expects 100 intervals from its operating-day bounds");
        Require(fall.Average=="1.36"&&fall.Policy=="exact_rational_half_even_18_v1","fall-back average weights every published quarter-hour by its duration");

        // 2026-11-02 in New York, hour 5 unpublished. Hand total 286.5 over 23 published hours.
        var day=PowerImportProtocol.Window(new(2026,11,2),new(2026,11,2),eastern,60);
        string?[] prices=["-2.5","4","7.25","-2.50","0",null,..Enumerable.Range(6,14).Select(h=>h.ToString(System.Globalization.CultureInfo.InvariantCulture)),"31.125","21","22","31.125"];
        var rows=day.Zip(prices).Where(p=>p.Second is not null).Select((p,i)=>Row(p.First,p.Second!,i)).ToArray();
        var summary=PowerHistory.Summarize(day,rows);
        Require(summary.Intervals==23&&summary.ExpectedIntervals==24&&summary.MissingIntervals==1,"one unpublished hour is one missing interval");
        Require(summary.Average=="12.456521739130434783","average is over published intervals only, rounded half to even at 18 places");
        Require(summary.Low==new DeliveryPriceExtreme("-2.5",day[0].Start)&&summary.High==new DeliveryPriceExtreme("31.125",day[20].Start),"low and high report the earliest instant among equal prices");
        Require(summary.LatestKnownAt==published.AddMinutes(22),"summary states the latest knowledge instant it used");
        var empty=PowerHistory.Summarize(day,[]);
        Require(empty is {Intervals:0,ExpectedIntervals:24,MissingIntervals:24,Low:null,High:null,Average:null,LatestKnownAt:null},"an unpublished period has no statistics");

        // Overlays see the unpublished hour as a missing point: windows that touch it have no value.
        var overlay=OpenEtrm.Shared.Analytics.Overlays.Compute(PowerHistory.OverlayPoints(day,rows),[new("simple_moving_average_v1",new Dictionary<string,int>{["window"]=2})]).Single();
        var values=string.Join(",",overlay.Series["value"].Take(8).Select(v=>v.Value??"null"));
        Require(values=="null,0.75,5.625,2.375,-1.25,null,null,6.5",$"period overlays equal the hand-computed moving average: {values}");
        Console.WriteLine("PASS delivery desk: newest-day opening with ties, DST-aware expected intervals, missing intervals, exact time-weighted average, earliest extremes and period overlays");
    }
    public static async Task<int> MalformedCollection()
    {
        using var http=new HttpClient(new PayloadHandler("{\"error\":\"synthetic malformed report\"}"));
        return await PowerCollection.Run(["now","PJM"],http);
    }
    sealed class EncodedPayloadHandler(string payload,string encoding):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            using var output=new MemoryStream();
            using(Stream compressor=encoding switch {"gzip"=>new GZipStream(output,CompressionMode.Compress,true),"deflate"=>new ZLibStream(output,CompressionMode.Compress,true),"br"=>new BrotliStream(output,CompressionMode.Compress,true),_=>throw new Exception("Unknown test encoding")})
                compressor.Write(Encoding.UTF8.GetBytes(payload));
            var content=new ByteArrayContent(output.ToArray());content.Headers.ContentEncoding.Add(encoding);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=content});
        }
    }
    sealed class PayloadHandler(string payload,string? encoding=null,byte[]? bytes=null):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
            {
            HttpContent content=bytes is null?new StringContent(payload):new ByteArrayContent(bytes);if(encoding is not null)foreach(var item in encoding.Split(','))content.Headers.ContentEncoding.Add(item.Trim());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=content});
        }
    }
    static void Require(bool accepted,string message){if(!accepted)throw new Exception(message);}
    static void Reject(Action action,string? message=null){try{action();}catch(MarketDataException e) when(e.Code=="power_import"&&(message is null||e.Message.Contains(message,StringComparison.Ordinal))){return;}throw new Exception("Accepted invalid power import");}
}
