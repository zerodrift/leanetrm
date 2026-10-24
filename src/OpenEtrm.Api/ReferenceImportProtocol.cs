using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReferenceProductLabel(string ProductId,string Commodity,string LocationCode,string LocationName,string LocationType,string? DisplayName);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReferenceImportOptions(ReferenceKey? EnergyQuantity,ImmutableArray<ReferenceProductLabel> Products,string? LicenseReference);
public sealed record ImportedReferenceProduct(string Code,string Provider,string ExchangeId,string ProductId,string Name,
    string Commodity,string LocationCode,string LocationName,string LocationType,string DocumentCode);
public sealed record ImportedReferenceSeries(string Code,string ProductCode,string Provider,string ExchangeId,string ProductId,
    string ContractId,string Label,string? NativePeriod,string DocumentCode,DateOnly? FirstTradingDate,DateOnly? LastTradingDate,
    DateOnly? FirstNoticeDate,DateOnly? LastNoticeDate,DateOnly? FirstDeliveryDate,DateOnly? LastDeliveryDate,DateOnly? FinalSettlementDate,Quantity? ReportedEnergy);
public sealed record ImportedReferenceDocument(string Code,ReferenceSourceDocument Source);
public sealed record ReferenceImportPlan(string Provider,string PackageSha256,string? LicenseReference,
    ImmutableArray<ImportedReferenceProduct> Products,ImmutableArray<ImportedReferenceSeries> Series,ImmutableArray<ImportedReferenceDocument> Documents)
{
    public string Payload()=>JsonSerializer.Serialize(new{
        protocol="reference_import_v1",provider=Provider,packageSha256=PackageSha256,licenseReference=LicenseReference,
        documents=Documents.Select(d=>new{code=d.Code,url=d.Source.Url,
            receivedAtUtc=new DateTimeOffset(d.Source.ReceivedAtUtc.Ticks/10*10,TimeSpan.Zero),
            reportedReceivedAtUtc=d.Source.ReceivedAtUtc.ToString("O",CultureInfo.InvariantCulture),
            sha256=d.Source.Sha256,contentBase64=d.Source.ContentBase64,acquisitionKind=d.Source.AcquisitionKind}),
        products=Products.Select(p=>new{p.Code,p.Provider,p.ExchangeId,p.ProductId,p.Name,p.Commodity,p.LocationCode,p.LocationName,p.LocationType,p.DocumentCode}),
        series=Series.Select(s=>new{s.Code,s.ProductCode,s.Provider,s.ExchangeId,s.ProductId,s.ContractId,s.Label,s.NativePeriod,s.DocumentCode,
            s.FirstTradingDate,s.LastTradingDate,s.FirstNoticeDate,s.LastNoticeDate,s.FirstDeliveryDate,s.LastDeliveryDate,s.FinalSettlementDate,
            reportedEnergy=s.ReportedEnergy?.Amount.ToString(),quantityCatalog=s.ReportedEnergy?.Specification.Revision.CatalogId,
            quantityRevision=s.ReportedEnergy?.Specification.Revision.RevisionId,quantityCode=s.ReportedEnergy?.Specification.Code})
    },ReferenceImportProtocol.Json);
}

public static class ReferenceImportIdentity
{
    public static string Product(string provider,string exchange,string product)=>Code("product",provider,exchange,product,"");
    public static string Series(string provider,string exchange,string product,string contract)=>Code("series",provider,exchange,product,contract);
    static string Code(params string[] fields)
    {
        if(fields.Any(f=>f is null||f.Contains('\0')))throw new MarketDataException("reference_import","Source identity contains an invalid field.");
        var digest=SHA256.HashData(Encoding.UTF8.GetBytes("reference_source_identity_v1\0"+string.Join('\0',fields)));
        const string alphabet="abcdefghijklmnopqrstuvwxyz234567";var text=new StringBuilder("r_");uint buffer=0;var bits=0;
        foreach(var value in digest)
        {
            buffer=(buffer<<8)|value;bits+=8;
            while(bits>=5){bits-=5;text.Append(alphabet[(int)((buffer>>bits)&31)]);}
        }
        if(bits!=0)text.Append(alphabet[(int)((buffer<<(5-bits))&31)]);
        return text.ToString();
    }
}

/// <summary>Verifies retained source bytes before constructing non-executable reference rows.</summary>
public static class ReferenceImportProtocol
{
    public static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=false};
    public static async Task<ReferenceImportPlan> Prepare(ReferenceCapturePackage package,ReferenceImportOptions options,CancellationToken ct)
    {
        try{return await PrepareCore(package,options,ct);}
        catch(Exception e) when(e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {throw Invalid();}
        catch(MarketDataException e) when(e.Code!="reference_import"){throw Invalid();}
    }
    static async Task<ReferenceImportPlan> PrepareCore(ReferenceCapturePackage package,ReferenceImportOptions options,CancellationToken ct)
    {
        if(package.Schema!="exchange_reference_capture_v1"||package.State!="unqualified"||package.TradingEnabled||package.PointInTimeSnapshot||
            package.Provider is not("CME" or "ICE")||package.StartedAtUtc.Offset!=TimeSpan.Zero||package.CompletedAtUtc.Offset!=TimeSpan.Zero||
            package.CompletedAtUtc<package.StartedAtUtc||package.CompletedAtUtc>DateTimeOffset.UtcNow||
            package.Documents.IsDefaultOrEmpty||package.Documents.Length>1024||package.Documents.Any(d=>d is null)||package.Records.IsDefault||
            package.Records.Length>50000||package.Records.Any(r=>r is null)||options.Products.IsDefaultOrEmpty||options.Products.Any(p=>p is null)||
            options.Products.Select(p=>p.ProductId).Distinct().Count()!=options.Products.Length||options.LicenseReference?.Length>1000)
            throw Invalid();
        var documents=ImmutableArray.CreateBuilder<ImportedReferenceDocument>();var bodies=new Dictionary<string,byte[]>(StringComparer.Ordinal);var total=0;
        foreach(var source in package.Documents.OrderBy(d=>d.Url,StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();var raw=Convert.FromBase64String(source.ContentBase64);total=checked(total+raw.Length);
            if(raw.Length>16*1024*1024||total>32*1024*1024||Convert.ToHexStringLower(SHA256.HashData(raw))!=source.Sha256||
                source.ReceivedAtUtc.Offset!=TimeSpan.Zero||source.ReceivedAtUtc>DateTimeOffset.UtcNow||!bodies.TryAdd(source.Url,raw))throw Invalid();
            documents.Add(new("document_"+documents.Count.ToString("D4",CultureInfo.InvariantCulture),source));
        }
        var sourceRecords=package.Provider=="CME"?await Cme(package,bodies,ct):Ice(package,bodies);
        if(sourceRecords.Length!=package.Records.Length||package.Records.Select(r=>r.Identity).Distinct().Count()!=package.Records.Length)throw Invalid();
        var asserted=package.Records.ToDictionary(r=>r.Identity);
        foreach(var actual in sourceRecords)
            if(!asserted.TryGetValue(actual.Identity,out var claim)||actual.DocumentSha256!=claim.DocumentSha256||!JsonElement.DeepEquals(actual.Reported,claim.Reported))throw Invalid();
        var products=ImmutableArray.CreateBuilder<ImportedReferenceProduct>();var series=ImmutableArray.CreateBuilder<ImportedReferenceSeries>();
        var productIds=package.Provider=="CME"?sourceRecords.Where(r=>r.Identity.InstrumentId is null).Select(r=>r.Identity.ProductId).ToArray():
            package.Documents.Select(d=>IceProduct(d.Url)).Distinct().ToArray();
        if(!productIds.Order().SequenceEqual(options.Products.Select(p=>p.ProductId).Order()))throw Invalid();
        foreach(var productId in productIds.Order(StringComparer.Ordinal))
        {
            var label=options.Products.Single(p=>p.ProductId==productId);
            if(label.Commodity is not("gas" or "electricity")||!Code(label.LocationCode)||string.IsNullOrWhiteSpace(label.LocationName)||label.LocationName.Length>160||
                label.LocationType is not("hub" or "pricing_location"))throw Invalid();
            var product=sourceRecords.SingleOrDefault(r=>r.Identity.ProductId==productId&&r.Identity.InstrumentId is null);
            var exchange=package.Provider=="CME"?Text(product!.Reported,"exchangeClearing"):"IFED";
            if((package.Provider=="CME"&&exchange!="NYMEX")||(package.Provider=="CME"&&label.DisplayName is not null))throw Invalid();
            var name=package.Provider=="CME"?Text(product!.Reported,"productName"):label.DisplayName;
            if(string.IsNullOrWhiteSpace(name)||name.Length>300)throw Invalid();
            var document=package.Provider=="CME"?documents.Single(d=>d.Source.Sha256==product!.DocumentSha256):
                documents.First(d=>IceProduct(d.Source.Url)==productId);
            var key=ReferenceImportIdentity.Product(package.Provider,exchange,productId);
            products.Add(new(key,package.Provider,exchange,productId,name,label.Commodity,label.LocationCode,label.LocationName,label.LocationType,document.Code));
            foreach(var row in sourceRecords.Where(r=>r.Identity.ProductId==productId&&r.Identity.InstrumentId is not null).OrderBy(r=>r.Identity.InstrumentId,StringComparer.Ordinal))
            {
                var id=row.Identity.InstrumentId!;var doc=documents.Single(d=>d.Source.Sha256==row.DocumentSha256&&
                    (package.Provider=="ICE"?IceProduct(d.Source.Url)==productId:d.Source.Url.StartsWith("https://refdata.api.cmegroup.com/refdata/v3/products/"+productId+"/",StringComparison.Ordinal)));
                if(id.Length>160||row.Identity.Exchange!=exchange)throw Invalid();
                DateOnly? first,last,firstNotice,lastNotice,deliveryFirst,deliveryLast,settlement;Quantity? quantity=null;string nativeLabel;string? period;
                if(package.Provider=="CME")
                {
                    if(Text(row.Reported,"instrumentType")!="FUT")throw Invalid();
                    first=Date(row.Reported,"firstTradeDate");last=Date(row.Reported,"lastTradeDate");firstNotice=Date(row.Reported,"firstNoticeDate");lastNotice=Date(row.Reported,"lastNoticeDate");
                    deliveryFirst=Date(row.Reported,"firstDeliveryDate");deliveryLast=Date(row.Reported,"lastDeliveryDate");settlement=Date(row.Reported,"finalSettlementDate");
                    nativeLabel=Text(row.Reported,"globexSymbol");period=Text(row.Reported,"contractMonth");
                }
                else
                {
                    var expiry=row.Reported.Deserialize<IceReportedExpiry>()??throw Invalid();
                    first=expiry.FirstTradingDate;last=expiry.LastTradingDate;firstNotice=expiry.FirstNoticeDate;lastNotice=expiry.LastNoticeDate;
                    deliveryFirst=expiry.FirstDeliveryDate;deliveryLast=expiry.LastDeliveryDate;settlement=expiry.FinalSettlementDate;nativeLabel=expiry.ContractSymbol;period=null;
                    if(expiry.ReportedMegawattHours is {} energy)
                    {
                        if(label.Commodity!="electricity"||options.EnergyQuantity is not {} energyKey)throw Invalid();
                        quantity=new(ExactDecimal.Parse(energy),energyKey);
                    }
                }
                if(first>last||deliveryFirst>deliveryLast||nativeLabel.Length>160)throw Invalid();
                series.Add(new(ReferenceImportIdentity.Series(package.Provider,exchange,productId,id),key,package.Provider,exchange,productId,id,nativeLabel,period,doc.Code,
                    first,last,firstNotice,lastNotice,deliveryFirst,deliveryLast,settlement,quantity));
            }
        }
        if(products.Select(p=>p.Code).Distinct().Count()!=products.Count||series.Select(s=>s.Code).Distinct().Count()!=series.Count||
            products.GroupBy(p=>p.LocationCode).Any(g=>g.Select(p=>(p.LocationName,p.LocationType)).Distinct().Count()!=1))throw Invalid();
        // This is retained source-package identity, not exchange authentication or approval.
        var digest=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(package,Json)));
        return new(package.Provider,digest,options.LicenseReference,products.ToImmutable(),series.ToImmutable(),documents.ToImmutable());
    }
    static async Task<ImmutableArray<ReferenceSourceRecord>> Cme(ReferenceCapturePackage package,Dictionary<string,byte[]> bodies,CancellationToken ct)
    {
        if(package.Kind!="instruments"||package.Documents.Any(d=>d.AcquisitionKind!="http_response"||d.ReceivedAtUtc<package.StartedAtUtc||d.ReceivedAtUtc>package.CompletedAtUtc))throw Invalid();
        var selection=ImmutableArray.CreateBuilder<CmeProductSelection>();int? pageSize=null;
        foreach(var entry in bodies)
        {
            var match=Regex.Match(entry.Key,"^https://refdata\\.api\\.cmegroup\\.com/refdata/v3/products/([A-Za-z0-9_-]{1,64})(/instruments\\?page=[0-9]+&size=[0-9]+)?$",RegexOptions.CultureInvariant);
            if(!match.Success)throw Invalid();
            using var raw=JsonDocument.Parse(entry.Value);CmeReferenceCapture.RejectDuplicateProperties(raw.RootElement);
            if(!match.Groups[2].Success)selection.Add(new(Text(raw.RootElement,"productGuid"),Text(raw.RootElement,"exchangeClearing")));
            else
            {
                var size=raw.RootElement.GetProperty("_metadata").GetProperty("size").GetInt32();
                if(pageSize is not null&&pageSize!=size)throw Invalid();pageSize=size;
            }
        }
        if(pageSize is null||selection.Count==0)throw Invalid();
        using var replay=new Replay(bodies);using var http=new HttpClient(replay);
        var validated=await new CmeReferenceCapture(http,new("offline-validation","offline-validation",pageSize.Value,2048,1,16*1024*1024,32*1024*1024)).Contracts(selection.ToImmutable(),ct);
        if(replay.Used.Count!=bodies.Count)throw Invalid();return validated.Records;
    }
    static ImmutableArray<ReferenceSourceRecord> Ice(ReferenceCapturePackage package,Dictionary<string,byte[]> bodies)
    {
        if(package.Kind!="website_downloads"||package.Documents.Any(d=>d.AcquisitionKind!="operator_reported_download"))throw Invalid();
        var rows=ImmutableArray.CreateBuilder<ReferenceSourceRecord>();
        foreach(var doc in package.Documents)
        {
            var product=IceProduct(doc.Url);var raw=bodies[doc.Url];
            if(doc.Url.EndsWith("/expiry/csv",StringComparison.Ordinal))
                foreach(var expiry in IceReferenceCapture.ExpiryCsv(raw))rows.Add(new(new("ICE","IFED",product,expiry.ContractSymbol),JsonSerializer.SerializeToElement(expiry),doc.Sha256));
            else
            {
                if(!raw.AsSpan().StartsWith("%PDF-"u8))throw Invalid();
                rows.Add(new(new("ICE","IFED",product,null),JsonSerializer.SerializeToElement(new{evidenceKind="specification_pdf",termsParsed=false}),doc.Sha256));
            }
        }
        return rows.ToImmutable();
    }
    static string IceProduct(string url)
    {
        var match=Regex.Match(url,"^https://www\\.ice\\.com/api/productguide/spec/([0-9]{1,18})/(expiry/csv|pdf)$",RegexOptions.CultureInvariant);
        return match.Success?match.Groups[1].Value:throw Invalid();
    }
    static bool Code(string code)=>Regex.IsMatch(code??"","^[a-z][a-z0-9_.-]{0,63}$",RegexOptions.CultureInvariant);
    static string Text(JsonElement row,string field)=>row.GetProperty(field).ValueKind==JsonValueKind.String&&row.GetProperty(field).GetString() is {Length:>0} value?value:throw Invalid();
    static DateOnly? Date(JsonElement row,string field)
    {
        if(!row.TryGetProperty(field,out var value)||value.ValueKind==JsonValueKind.Null)return null;
        if(value.ValueKind!=JsonValueKind.String||!DateOnly.TryParseExact(value.GetString(),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)||date>=new DateOnly(9999,12,31))throw Invalid();
        return date;
    }
    sealed class Replay(Dictionary<string,byte[]> documents):HttpMessageHandler
    {
        public HashSet<string> Used{get;}=new(StringComparer.Ordinal);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var url=request.RequestUri!.AbsoluteUri;
            if(url=="https://auth.cmegroup.com/as/token.oauth2")return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"access_token\":\"offline\",\"expires_in\":3600,\"token_type\":\"Bearer\"}")});
            if(!documents.TryGetValue(url,out var content)||!Used.Add(url))throw Invalid();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(content)});
        }
    }
    static MarketDataException Invalid()=>new("reference_import","The retained source bytes, exact identities or explicitly supplied reference mappings are incomplete or conflicting.");
}
