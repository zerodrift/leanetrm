using System.Net;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

static class ReferenceCaptureChecks
{
    public static async Task Run()
    {
        var settings=new CmeReferenceSettings("synthetic-client","synthetic-secret",2,20,1,100000,1000000);
        string Page(int number,int total,object[] rows,string? next=null)=>JsonSerializer.Serialize(new{
            _embedded=new{products=rows},_metadata=new{number,size=2,totalElements=total,totalPages=(total+1)/2},
            _links=next is null?new Dictionary<string,object>():new(){{"next",new{href=next}}}});
        object Product(string id)=>new{productGuid=id,productName="Synthetic product "+id,securityType="FUT",exchangeClearing="NYMEX"};
        const string next="https://refdata.api.cmegroup.com/refdata/v3/products?page=1&size=2";
        var handler=new Responses(Page(0,3,[Product("A"),Product("B")],next),Page(1,3,[Product("C")]));
        using var http=new HttpClient(handler);
        var capture=await new CmeReferenceCapture(http,settings).Products(CancellationToken.None);
        Require(capture.Records.Length==3 && capture.Documents.Length==2,"all reported pages retained");
        Require(capture.State=="unqualified" && !capture.TradingEnabled && !capture.PointInTimeSnapshot,"capture never grants activation or snapshot certification");
        Require(capture.Records.Select(r=>r.Identity.ProductId).SequenceEqual(new[]{"A","B","C"}),"provider product identities retained without symbol aliases");
        Require(capture.Documents.All(d=>d.Sha256.Length==64 && d.ReceivedAtUtc.Offset==TimeSpan.Zero),"source hashes and UTC retrieval facts retained");
        Require(handler.AuthorizedReads==2,"reference requests authenticate without putting secrets in URLs");
        Require(!JsonSerializer.Serialize(capture).Contains("synthetic-secret",StringComparison.Ordinal),"capture excludes credentials and tokens");
        async Task Reject(string first,string? second=null,string code="reference_response")
        {
            using var badHttp=new HttpClient(second is null?new Responses(first):new Responses(first,second));
            try{await new CmeReferenceCapture(badHttp,settings).Products(CancellationToken.None);throw new Exception("Accepted invalid reference response");}
            catch(MarketDataException e) when(e.Code==code){}
        }
        await Reject(Page(0,3,[Product("A"),Product("B")]));
        await Reject(Page(0,3,[Product("A"),Product("B")],next),Page(1,3,[Product("A")]));
        await Reject(Page(0,3,[Product("A"),Product("B")],next),Page(1,4,[Product("C"),Product("D")]));
        await Reject(Page(0,3,[Product("A"),Product("B")],"https://example.invalid/steal"));
        await Reject(Page(0,3,[Product("A"),Product("B")],"https://refdata.api.cmegroup.com/refdata/v3/instruments?page=1&size=2"));
        await Reject(Page(0,3,[Product("A"),Product("B")],"https://refdata.api.cmegroup.com/refdata/v3/products?page=0&size=2"));
        await Reject("{\"error\":\"synthetic incomplete response\"}");
        using(var denied=new HttpClient(new Responses(HttpStatusCode.Forbidden)))
        {
            try{await new CmeReferenceCapture(denied,settings).Products(CancellationToken.None);throw new Exception("Accepted forbidden source");}
            catch(MarketDataException e) when(e.Code=="reference_entitlement"){}
        }
        const string csv="\"CONTRACT SYMBOL\",\"FTD\",\"LTD\",\"FND\",\"LND\",\"FDD\",\"LDD\",\"FSD\",\"MWHS\"\r\n\"=\"\"Nov40\"\"\",\"10/02/2040\",\"11/30/2040\",\"\",\"\",\"\",\"\",\"12/04/2040\",35.125\r\n";
        var expiry=IceReferenceCapture.ExpiryCsv(Encoding.UTF8.GetBytes(csv));
        Require(expiry.Single().ContractSymbol=="Nov40" && expiry[0].FirstTradingDate==new DateOnly(2040,10,2),"ICE exact source label and native date");
        Require(expiry[0].FirstNoticeDate is null && expiry[0].FirstDeliveryDate is null,"ICE blank dates remain unknown");
        Require(expiry[0].ReportedMegawattHours=="35.125","ICE published MWh retained as exact source decimal without inferred hours");
        var gasCsv=csv.Replace(",\"MWHS\"","").Replace(",35.125",",");
        Require(IceReferenceCapture.ExpiryCsv(Encoding.UTF8.GetBytes(gasCsv))[0].ReportedMegawattHours is null,"ICE gas export's unnamed empty trailing field adds no MWh value");
        void RejectCsv(string value)
        {
            try{IceReferenceCapture.ExpiryCsv(Encoding.UTF8.GetBytes(value));throw new Exception("Accepted malformed ICE report");}
            catch(MarketDataException e) when(e.Code=="reference_response"){}
        }
        RejectCsv(csv.Replace("35.125","NaN"));
        RejectCsv(csv.Replace("35.125","35.0000000000000000001"));
        RejectCsv(csv.Replace("MWHS","MWH"));
        RejectCsv(gasCsv.Replace("12/04/2040\",","12/04/2040\",1"));
        RejectCsv(csv.Replace("11/30/2040","10/01/2040"));
        RejectCsv(csv.Replace("Nov40","NOW()"));
        RejectCsv(csv+csv.Split("\r\n")[1]+"\r\n");
        var product=JsonSerializer.Serialize(Product("A"));
        string Instruments(string link="A",string exchange="NYMEX",bool duplicate=false)=>JsonSerializer.Serialize(new{
            _embedded=new{instruments=Enumerable.Range(0,duplicate?2:1).Select(_=>new{guid="SOURCE-1",instrumentType="FUT",globexSymbol="SYNTH40",exchangeClearing=exchange,contractMonth="204011",firstTradeDate="2040-10-02",lastTradeDate="2040-11-30",_links=new{product=new{href="https://refdata.api.cmegroup.com/refdata/v3/products/"+link}}}).ToArray()},
            _metadata=new{number=0,size=2,totalElements=duplicate?2:1,totalPages=1},_links=new{}});
        using(var contractsHttp=new HttpClient(new Responses(product,Instruments())))
        {
            var contracts=await new CmeReferenceCapture(contractsHttp,settings).Contracts([new("A","NYMEX")],CancellationToken.None);
            var plan=await ReferenceImportProtocol.Prepare(contracts,new(null,[new("A","gas","synthetic_hub","Synthetic hub","hub",null)],null),CancellationToken.None);
            Require(plan.Series.Length==1&&plan.Series[0].Label=="SYNTH40"&&plan.Series[0].NativePeriod=="204011","CME import replays original product and paginated contract bytes offline");
            Require(contracts.Records.Length==2 && contracts.Records[1].Identity.InstrumentId=="SOURCE-1","exact product and instrument identities captured");
            Require(contracts.Records[1].Reported.GetProperty("lastTradeDate").GetString()=="2040-11-30","CME native date remains a date, without invented UTC deadline");
        }
        foreach(var instrumentPayload in new[]{Instruments("B"),Instruments(exchange:"OTHER"),Instruments(duplicate:true)})
        {
            using var bad=new HttpClient(new Responses(product,instrumentPayload));
            try{await new CmeReferenceCapture(bad,settings).Contracts([new("A","NYMEX")],CancellationToken.None);throw new Exception("Accepted conflicting contracts");}
            catch(MarketDataException e) when(e.Code=="reference_response"){}
        }
        using(var bad=new HttpClient(new Responses(product)))
        {
            try{await new CmeReferenceCapture(bad,settings).Contracts([null!],CancellationToken.None);throw new Exception("Accepted null selection");}
            catch(MarketDataException e) when(e.Code=="reference_response"){}
        }
        try{await IceReferenceCapture.Files(new(1000,1000,[null!]),CancellationToken.None);throw new Exception("Accepted null ICE file");}
        catch(MarketDataException e) when(e.Code=="reference_configuration"){}
        foreach(var limited in new[]{settings with{MaximumRequests=1},settings with{MaximumResponseBytes=1},settings with{MaximumResponseBytes=Encoding.UTF8.GetByteCount(Page(0,3,[Product("A"),Product("B")],next))+1,MaximumCaptureBytes=Encoding.UTF8.GetByteCount(Page(0,3,[Product("A"),Product("B")],next))+1}})
        {
            using var bad=new HttpClient(new Responses(Page(0,3,[Product("A"),Product("B")],next),Page(1,3,[Product("C")])));
            try{await new CmeReferenceCapture(bad,limited).Products(CancellationToken.None);throw new Exception("Accepted capture above explicit limit");}
            catch(MarketDataException e) when(e.Code=="reference_limit"){}
        }
        if(!OperatingSystem.IsWindows())
        {
            var directory=Directory.CreateTempSubdirectory("openetrm-capture-check-");
            File.SetUnixFileMode(directory.FullName,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            try
            {
                var csvPath=Path.Combine(directory.FullName,"source.csv");await File.WriteAllTextAsync(csvPath,csv);
                var download=new IceReferenceFile("123456","IFED","expiry_csv",csvPath,"https://www.ice.com/api/productguide/spec/123456/expiry/csv",DateTimeOffset.UtcNow.AddMinutes(-1),Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(csvPath))));
                var ice=await IceReferenceCapture.Files(new(10000,10000,[download]),CancellationToken.None);
                Require(ice.Records.Length==1 && ice.Documents.Single().AcquisitionKind=="operator_reported_download" && !ice.TradingEnabled,"ICE source file captures operator-asserted provenance without activation");
                foreach(var invalidFile in new[]{download with{File="relative.csv"},download with{Sha256=new string('0',64)}})
                {
                    try{await IceReferenceCapture.Files(new(10000,10000,[invalidFile]),CancellationToken.None);throw new Exception("Accepted invalid source file");}
                    catch(MarketDataException e) when(e.Code=="reference_response"){}
                }
                var link=Path.Combine(directory.FullName,"link.csv");File.CreateSymbolicLink(link,csvPath);
                try{await IceReferenceCapture.Files(new(10000,10000,[download with{File=link}]),CancellationToken.None);throw new Exception("Accepted symlink source file");}
                catch(MarketDataException e) when(e.Code=="reference_response"){}
                File.Delete(link);File.Delete(csvPath);
                var path=Path.Combine(directory.FullName,"capture.json");
                await ReferenceCaptureCommand.WriteNew(path,capture,CancellationToken.None);
                if(!OperatingSystem.IsWindows())Require(File.GetUnixFileMode(path)==(UnixFileMode.UserRead|UnixFileMode.UserWrite),"capture output is owner-only");
                var original=await File.ReadAllBytesAsync(path);
                try{await ReferenceCaptureCommand.WriteNew(path,capture,CancellationToken.None);throw new Exception("Overwrote existing capture");}catch(IOException){}
                var retained=await File.ReadAllBytesAsync(path);
                Require(original.SequenceEqual(retained),"existing output unchanged");
                using var cancelled=new CancellationTokenSource();cancelled.Cancel();
                var interrupted=Path.Combine(directory.FullName,"cancelled.json");
                try{await ReferenceCaptureCommand.WriteNew(interrupted,capture,cancelled.Token);throw new Exception("Accepted cancelled capture");}catch(OperationCanceledException){}
                Require(!File.Exists(interrupted)&&Directory.GetFiles(directory.FullName).Length==1,"cancellation leaves no accepted output or temporary file");
                if(!OperatingSystem.IsWindows())File.SetUnixFileMode(directory.FullName,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead);
                try{await ReferenceCaptureCommand.WriteNew(interrupted,capture,CancellationToken.None);throw new Exception("Accepted nonprivate output directory");}catch(IOException){}
            }
            finally{directory.Delete(true);}
        }
        Console.WriteLine("PASS reference capture: complete pagination, exact identities, provenance, credential isolation and fail-closed source responses");
    }
    static void Require(bool condition,string label){if(!condition)throw new Exception(label);}
    sealed class Responses:HttpMessageHandler
    {
        readonly Queue<string> payloads;readonly HttpStatusCode status;
        public int AuthorizedReads{get;private set;}
        public Responses(params string[] payloads){this.payloads=new(payloads);status=HttpStatusCode.OK;}
        public Responses(HttpStatusCode status){payloads=new();this.status=status;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.RequestUri!.Host=="auth.cmegroup.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"access_token\":\"synthetic-token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}")});
            Require(request.Headers.Authorization?.Parameter=="synthetic-token","bearer authorization required");AuthorizedReads++;
            return Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(status==HttpStatusCode.OK?payloads.Dequeue():"private-provider-error",Encoding.UTF8,"application/json")});
        }
    }
}
