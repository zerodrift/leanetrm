using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

public sealed record PowerProviderSettings(string Provider,string BaseUrl,string ApiKey,int RequestsPerMinute,
    string? TokenUrl,string? ClientId,string? TokenScope,string? AccountId,string? ApiSecret);

public sealed class PowerPriceClient(HttpClient http,Func<Task> requestSlot,PowerProviderSettings settings,int pageSize,int maximumRows)
{
    static Uri Https(string text)
    {
        if(!Uri.TryCreate(text,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!="")throw Error("An explicit HTTPS provider URL is required.");
        return uri;
    }
    public async Task<ImportedPowerPrice[]> Fetch(string dataset,string market,string point,int minutes,DateOnly first,DateOnly last,PowerClock clock,PriceUnit unit,CancellationToken ct)
    {
        try{return await FetchCore(dataset,market,point,minutes,first,last,clock,unit,ct);}
        catch(Exception e) when(e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {throw Error("The provider response does not match its declared report schema.");}
    }
    public async Task<ImportedPowerPrice[]> FetchCore(string dataset,string market,string point,int minutes,DateOnly first,DateOnly last,PowerClock clock,PriceUnit unit,CancellationToken ct)
    {
        PowerImportProtocol.ValidateFeed(settings.Provider,dataset,market,minutes);
        if(string.IsNullOrWhiteSpace(settings.ApiKey)||settings.RequestsPerMinute<=0||pageSize<1||pageSize>(settings.Provider=="PJM"?50000:1000)||maximumRows is <1 or >100000)throw Error("Supply explicit provider credentials and collection limits.");
        var window=PowerImportProtocol.Window(first,last,clock,minutes);
        var baseUrl=Https(settings.BaseUrl).AbsoluteUri.TrimEnd('/');
        string? token=null;
        if(settings.Provider=="ERCOT")
        {
            if(new[]{settings.TokenUrl,settings.ClientId,settings.TokenScope,settings.AccountId,settings.ApiSecret}.Any(string.IsNullOrWhiteSpace))throw Error("ERCOT authentication configuration is incomplete.");
            using var authentication=new HttpRequestMessage(HttpMethod.Post,Https(settings.TokenUrl!))
            {Content=new FormUrlEncodedContent(new Dictionary<string,string>{["username"]=settings.AccountId!,["password"]=settings.ApiSecret!,["client_id"]=settings.ClientId!,["scope"]=settings.TokenScope!,["grant_type"]="password",["response_type"]="id_token"})};
            using var response=await Send(authentication,ct);
            token=response.RootElement.GetProperty("id_token").GetString();
            if(string.IsNullOrWhiteSpace(token))throw Error("ERCOT authentication returned no token.");
        }
        var prices=new List<ImportedPowerPrice>();int page=1,received=0;int? expected=null;
        do
        {
            string query;
            if(settings.Provider=="PJM")
            {
                var range=string.Create(CultureInfo.InvariantCulture,$"{window[0].Start:MM/dd/yyyy HH:mm:ss} to {window[^1].End.AddSeconds(-1):MM/dd/yyyy HH:mm:ss}");
                query=$"rowCount={pageSize}&startRow={received+1}&sort=datetime_beginning_ept&order=Asc&pnode_id={Uri.EscapeDataString(point)}&datetime_beginning_utc={Uri.EscapeDataString(range)}&row_is_current=true&fields=datetime_beginning_utc,pnode_id,total_lmp_{market.ToLowerInvariant()},version_nbr";
            }
            else query=string.Create(CultureInfo.InvariantCulture,$"deliveryDateFrom={first:yyyy-MM-dd}&deliveryDateTo={last:yyyy-MM-dd}&settlementPoint={Uri.EscapeDataString(point)}&page={page}&size={pageSize}");
            using var request=new HttpRequestMessage(HttpMethod.Get,baseUrl+"/"+dataset+"?"+query);
            request.Headers.Add("Ocp-Apim-Subscription-Key",settings.ApiKey);
            if(token is not null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
            using var document=await Send(request,ct);var root=document.RootElement;
            var total=settings.Provider=="PJM"?root.GetProperty("totalRows").GetInt32():root.GetProperty("_meta").GetProperty("totalRecords").GetInt32();
            var rows=root.GetProperty(settings.Provider=="PJM"?"items":"data");var count=rows.GetArrayLength();
            if(total<0||total>maximumRows||(expected is not null&&expected!=total)||count>pageSize||count==0&&received<total||received+count>total || settings.Provider=="ERCOT"&&total!=0&&root.GetProperty("_meta").GetProperty("currentPage").GetInt32()!=page)
                throw Error("The provider returned incomplete or inconsistent pagination.");
            expected=total;received+=count;page++;
            prices.AddRange(settings.Provider=="PJM"?rows.EnumerateArray().Select(row=>PowerImportProtocol.Pjm(row,market,point,minutes,unit)):PowerImportProtocol.Ercot(root,market,point,window,unit));
        }while(received<expected);
        var actual=prices.OrderBy(p=>p.Start).ToArray();
        if(actual.Length!=window.Length||actual.Where((p,i)=>p.Start!=window[i].Start||p.End!=window[i].End).Any())
            throw Error("The selected operating-date window does not have complete, unique native interval coverage; nothing was published.");
        return actual;
    }
    async Task<JsonDocument> Send(HttpRequestMessage request,CancellationToken ct)
    {
        await requestSlot();
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode)throw new MarketDataException("provider_http","Provider request failed with HTTP "+(int)response.StatusCode+"; no response content or credentials were logged.");
        // Bound each page before parsing; automatic redirects are disabled by the worker.
        await response.Content.LoadIntoBufferAsync(16*1024*1024,ct);
        var bytes=await response.Content.ReadAsByteArrayAsync(ct);
        if(bytes.Length>16*1024*1024)throw Error("Provider page exceeds the response limit.");
        if(response.Content.Headers.ContentEncoding.Count>0)
        {
            if(response.Content.Headers.ContentEncoding.Count!=1)throw Error("Provider page has an unsupported content encoding.");
            using var encoded=new MemoryStream(bytes,false);
            using Stream decoded=response.Content.Headers.ContentEncoding.Single().ToLowerInvariant() switch
            {
                "gzip"=>new GZipStream(encoded,CompressionMode.Decompress),
                "deflate"=>new ZLibStream(encoded,CompressionMode.Decompress),
                "br"=>new BrotliStream(encoded,CompressionMode.Decompress),
                _=>throw Error("Provider page has an unsupported content encoding.")
            };
            using var output=new MemoryStream();var buffer=new byte[8192];
            try
            {
                int count;
                while((count=await decoded.ReadAsync(buffer,ct))>0)
                {
                    if(output.Length+count>16*1024*1024)throw Error("Decoded provider page exceeds the response limit.");
                    output.Write(buffer,0,count);
                }
            }
            catch(InvalidDataException){throw Error("Provider page has invalid compressed content.");}
            bytes=output.ToArray();
        }
        try{return JsonDocument.Parse(bytes);}catch(JsonException){throw Error("Provider returned malformed JSON.");}
    }
    static MarketDataException Error(string message)=>new("power_import",message);
}
