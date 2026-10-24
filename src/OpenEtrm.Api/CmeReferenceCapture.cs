using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Provider payloads stop at this intake boundary. They are not executable domain definitions.
public sealed record ReferenceSourceIdentity(string Provider,string Exchange,string ProductId,string? InstrumentId);
public sealed record ReferenceSourceRecord(ReferenceSourceIdentity Identity,JsonElement Reported,string DocumentSha256);
public sealed record ReferenceSourceDocument(string Url,DateTimeOffset ReceivedAtUtc,string Sha256,string ContentBase64,string AcquisitionKind);
public sealed record ReferenceCapturePackage(string Schema,string Provider,string Kind,string State,bool TradingEnabled,
    bool PointInTimeSnapshot,DateTimeOffset StartedAtUtc,DateTimeOffset CompletedAtUtc,
    ImmutableArray<ReferenceSourceRecord> Records,ImmutableArray<ReferenceSourceDocument> Documents);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CmeProductSelection(string ProductId,string Exchange);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CmeReferenceSettings(string ClientId,string ClientSecret,int PageSize,int MaximumRequests,
    int MinimumRequestSpacingMilliseconds,int MaximumResponseBytes,int MaximumCaptureBytes)
{
    public override string ToString()=>"CME reference connection (credentials hidden)";
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(ClientId)||string.IsNullOrWhiteSpace(ClientSecret)||PageSize is <1 or >2000||
            MaximumRequests is <1 or >10000||MinimumRequestSpacingMilliseconds is <1 or >60000||
            MaximumResponseBytes is <1 or >16*1024*1024||MaximumCaptureBytes<MaximumResponseBytes||MaximumCaptureBytes>256*1024*1024)
            throw new MarketDataException("reference_configuration","Supply explicit bounded connection and request policies.");
    }
}

/// <summary>Captures a complete reported enumeration, without inferring listing status or contract economics.</summary>
public sealed class CmeReferenceCapture(HttpClient http,CmeReferenceSettings settings)
{
    const string Origin="https://refdata.api.cmegroup.com";
    readonly List<ReferenceSourceDocument> documents=[];
    int requests,bytes;DateTimeOffset? previousRequest;string? token;DateTimeOffset tokenUntil;

    public Task<ReferenceCapturePackage> Products(CancellationToken ct)=>Capture("products",async()=>
    {
        var rows=await Pages("/refdata/v3/products","products",ct);
        return rows.Select(r=>new ReferenceSourceRecord(new("CME",Required(r.Item,"exchangeClearing"),Required(r.Item,"productGuid"),null),r.Item,r.Hash)).ToImmutableArray();
    },ct);

    public Task<ReferenceCapturePackage> Contracts(ImmutableArray<CmeProductSelection> selection,CancellationToken ct)=>Capture("instruments",async()=>
    {
        if(selection.IsDefaultOrEmpty||selection.Length>128||selection.Any(s=>s is null)||selection.Distinct().Count()!=selection.Length)
            throw Invalid("Select distinct exact products and exchanges explicitly.");
        var output=ImmutableArray.CreateBuilder<ReferenceSourceRecord>();
        foreach(var selected in selection)
        {
            var id=Identifier(selected.ProductId);Identifier(selected.Exchange);
            var product=await Get(new Uri($"{Origin}/refdata/v3/products/{id}"),ct);
            if(Required(product.Json,"productGuid")!=id||Required(product.Json,"exchangeClearing")!=selected.Exchange||Required(product.Json,"securityType")!="FUT")
                throw Invalid("The exact selected product must be a future on the selected exchange.");
            output.Add(new(new("CME",selected.Exchange,id,null),product.Json,product.Hash));
            var rows=await Pages($"/refdata/v3/products/{id}/instruments","instruments",ct);
            foreach(var row in rows)
            {
                var linked=row.Item.GetProperty("_links").GetProperty("product").GetProperty("href").GetString();
                if(linked!=$"{Origin}/refdata/v3/products/{id}"||Required(row.Item,"exchangeClearing")!=selected.Exchange)
                    throw Invalid("A returned contract does not reference the exact selected product and exchange.");
                output.Add(new(new("CME",selected.Exchange,id,Required(row.Item,"guid")),row.Item,row.Hash));
            }
        }
        return output.ToImmutable();
    },ct);

    async Task<ReferenceCapturePackage> Capture(string kind,Func<Task<ImmutableArray<ReferenceSourceRecord>>> read,CancellationToken ct)
    {
        settings.Validate();
        if(requests!=0)throw Invalid("Use a new capture session for each enumeration.");
        var start=DateTimeOffset.UtcNow;
        try
        {
            ct.ThrowIfCancellationRequested();
            var records=await read();
            if(records.Select(r=>r.Identity).Distinct().Count()!=records.Length)throw Invalid("The enumeration contains duplicate source identities.");
            return new("exchange_reference_capture_v1","CME",kind,"unqualified",false,false,start,DateTimeOffset.UtcNow,records,[..documents]);
        }
        catch(Exception e) when(e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {throw Invalid("The response does not match the selected CME reference protocol.");}
        catch(HttpRequestException){throw new MarketDataException("reference_unavailable","The reference provider could not be reached.");}
    }

    async Task<List<(JsonElement Item,string Hash)>> Pages(string path,string collection,CancellationToken ct)
    {
        var output=new List<(JsonElement,string)>();var visited=new HashSet<string>(StringComparer.Ordinal);
        var url=new Uri($"{Origin}{path}?page=0&size={settings.PageSize}");
        int? total=null,pages=null;var number=0;
        while(true)
        {
            if(!visited.Add(url.AbsoluteUri))throw Invalid("The source repeated a pagination link.");
            var response=await Get(url,ct);var root=response.Json;
            var metadata=root.GetProperty("_metadata");
            var count=metadata.GetProperty("totalElements").GetInt32();var pageCount=metadata.GetProperty("totalPages").GetInt32();
            if(count<0||pageCount!=(count+settings.PageSize-1L)/settings.PageSize||
                metadata.GetProperty("size").GetInt32()!=settings.PageSize||metadata.GetProperty("number").GetInt32()!=number||
                (total is not null&&total!=count)||(pages is not null&&pages!=pageCount))
                throw Invalid("The source enumeration changed or has inconsistent pagination metadata.");
            total=count;pages=pageCount;
            var rows=root.GetProperty("_embedded").GetProperty(collection).EnumerateArray().ToArray();
            var expected=Math.Min(settings.PageSize,count-number*settings.PageSize);
            if(rows.Length!=expected)throw Invalid("A source page is incomplete.");
            output.AddRange(rows.Select(r=>(r.Clone(),response.Hash)));
            var hasNext=root.GetProperty("_links").TryGetProperty("next",out var next);
            if(number+1>=pageCount)
            {
                if(hasNext||output.Count!=count)throw Invalid("The source enumeration did not terminate consistently.");
                return output;
            }
            if(!hasNext)throw Invalid("The source omitted a required next page.");
            number++;
            // No provider-controlled URLs receive credentials. Require the exact next query.
            var expectedNext=$"{Origin}{path}?page={number}&size={settings.PageSize}";
            if(next.GetProperty("href").GetString()!=expectedNext)throw Invalid("The source next link changed scope or page order.");
            url=new Uri(expectedNext);
        }
    }

    async Task<(JsonElement Json,string Hash)> Get(Uri url,CancellationToken ct)
    {
        await Authenticate(ct);
        using var request=new HttpRequestMessage(HttpMethod.Get,url);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        using var response=await Send(request,ct);
        await using var body=await response.Content.ReadAsStreamAsync(ct);
        var content=await Read(body,settings.MaximumResponseBytes,ct);bytes=checked(bytes+content.Length);
        if(bytes>settings.MaximumCaptureBytes)throw new MarketDataException("reference_limit","The capture exceeds its explicit byte limit.");
        var hash=Convert.ToHexStringLower(SHA256.HashData(content));
        using var document=JsonDocument.Parse(content,new JsonDocumentOptions{MaxDepth=64});
        RejectDuplicateProperties(document.RootElement);
        documents.Add(new(url.AbsoluteUri,DateTimeOffset.UtcNow,hash,Convert.ToBase64String(content),"http_response"));
        return(document.RootElement.Clone(),hash);
    }

    async Task Authenticate(CancellationToken ct)
    {
        if(token is not null&&DateTimeOffset.UtcNow<tokenUntil)return;
        using var request=new HttpRequestMessage(HttpMethod.Post,"https://auth.cmegroup.com/as/token.oauth2"){
            Content=new FormUrlEncodedContent(new Dictionary<string,string>{{"grant_type","client_credentials"},{"client_id",settings.ClientId},{"client_secret",settings.ClientSecret}})};
        using var response=await Send(request,ct);
        await using var stream=await response.Content.ReadAsStreamAsync(ct);var body=await Read(stream,32768,ct);
        using var json=JsonDocument.Parse(body);var root=json.RootElement;RejectDuplicateProperties(root);
        token=Required(root,"access_token");var lifetime=root.GetProperty("expires_in").GetInt32();
        if(lifetime<1||Required(root,"token_type")!="Bearer")throw Invalid("The authentication response has no usable token.");
        tokenUntil=DateTimeOffset.UtcNow.AddSeconds(Math.Max(0,lifetime-60));
    }

    async Task<HttpResponseMessage> Send(HttpRequestMessage request,CancellationToken ct)
    {
        if(++requests>settings.MaximumRequests)throw new MarketDataException("reference_limit","The capture exceeds its explicit request limit.");
        if(previousRequest is {} previous)
        {
            var wait=TimeSpan.FromMilliseconds(settings.MinimumRequestSpacingMilliseconds)-(DateTimeOffset.UtcNow-previous);
            if(wait>TimeSpan.Zero)await Task.Delay(wait,ct);
        }
        previousRequest=DateTimeOffset.UtcNow;
        var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(response.IsSuccessStatusCode)return response;
        var status=response.StatusCode;response.Dispose();
        throw new MarketDataException(status switch{
            HttpStatusCode.Unauthorized=>"reference_authentication",HttpStatusCode.Forbidden=>"reference_entitlement",
            HttpStatusCode.TooManyRequests=>"reference_rate_limit",_=>"reference_unavailable"},"The provider did not authorize or complete the reference request. No partial capture was accepted.");
    }

    internal static async Task<byte[]> Read(Stream stream,int limit,CancellationToken ct)
    {
        using var output=new MemoryStream();var buffer=new byte[8192];int count;
        while((count=await stream.ReadAsync(buffer,ct))!=0)
        {if(output.Length+count>limit)throw new MarketDataException("reference_limit","The source document exceeds its explicit byte limit.");await output.WriteAsync(buffer.AsMemory(0,count),ct);}
        return output.ToArray();
    }
    internal static void RejectDuplicateProperties(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {var keys=new HashSet<string>(StringComparer.Ordinal);foreach(var property in element.EnumerateObject()){if(!keys.Add(property.Name))throw Invalid("Duplicate provider fields are ambiguous.");RejectDuplicateProperties(property.Value);}}
        else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())RejectDuplicateProperties(item);
    }
    static string Identifier(string value)=>Regex.IsMatch(value??"","^[A-Za-z0-9_-]{1,64}$",RegexOptions.CultureInvariant)?value!:throw Invalid("An exact provider identity is required.");
    static string Required(JsonElement row,string name)=>row.GetProperty(name).ValueKind==JsonValueKind.String&&row.GetProperty(name).GetString() is {Length:>0} value?value:throw Invalid("A required provider identity is missing.");
    static MarketDataException Invalid(string message)=>new("reference_response",message);
}
