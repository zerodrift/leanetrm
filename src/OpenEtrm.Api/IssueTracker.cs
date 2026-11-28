using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// The installation's private tracker connection: one repository, the GitHub App that may create issues in it and
// explicit bounds for every exchange. An installation without this file has no issue reporting.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed partial record IssueTrackerSettings([property:JsonRequired]string Repository,[property:JsonRequired]string AppId,
    [property:JsonRequired]long InstallationId,[property:JsonRequired]string PrivateKeyFile,[property:JsonRequired]string ApiVersion,
    [property:JsonRequired]int ConnectSeconds,[property:JsonRequired]int ResponseSeconds,[property:JsonRequired]int MaximumResponseBytes)
{
    public const string Variable="OPENETRM_ISSUE_TRACKER_CONFIG";
    // \z, not $, which would also accept a trailing line break.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/(?!\.{1,2}\z)[A-Za-z0-9._-]{1,100}\z")]private static partial Regex RepositoryName();
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]private static partial Regex AppIdentity();
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z")]private static partial Regex Version();

    public override string ToString()=>"Issue tracker connection (credentials hidden)";

    // ResponseSeconds bounds a whole exchange, token included, so it always ends well inside the two minutes
    // after which the database treats an unrecorded dispatch as uncertain. A listing page must fit the byte
    // bound, or no lookup could ever be read to its end.
    public void Validate()
    {
        if(Repository is null||!RepositoryName().IsMatch(Repository)||AppId is null||!AppIdentity().IsMatch(AppId)||InstallationId<1
            ||PrivateKeyFile is null||!Path.IsPathFullyQualified(PrivateKeyFile)||ApiVersion is null||!Version().IsMatch(ApiVersion)
            ||ConnectSeconds is <1 or >10||ResponseSeconds is <5 or >60||MaximumResponseBytes is <2*1024*1024 or >16*1024*1024)
            throw new MarketDataException("issue_tracker_configuration","Supply the repository, app identity, private key file, API version and bounded timeouts explicitly.");
    }

    public static IssueTrackerSettings? FromEnvironment()
    {
        if(Environment.GetEnvironmentVariable(Variable) is not {} file)return null;
        var settings=JsonSerializer.Deserialize<IssueTrackerSettings>(File.ReadAllText(file),new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ??throw new InvalidOperationException("Missing issue tracker configuration.");
        settings.Validate();
        return settings;
    }
}

public sealed record IssueReportClaim(Guid Id,int Attempt,Guid Marker,string Repository,string Kind,string Title,string Body,DateTimeOffset RequestedAt);

// What one dispatch established. Failed means the tracker did not create an issue; uncertain means it may have.
public sealed record IssueReportOutcome(string State,long? Number,string? Url,string? Code)
{
    public static IssueReportOutcome Submitted(long number,string url)=>new("submitted",number,url,null);
    public static IssueReportOutcome Failed(string code)=>new("failed",null,null,code);
    public static IssueReportOutcome Uncertain(string code)=>new("uncertain",null,null,code);
}

// Complete is false when the tracker's listing could not be read to its end, so absence proves nothing.
public sealed record IssueLookup(IssueReportOutcome? Issue,bool Complete);

// Creates and finds issues as a GitHub App installation. It exposes these two operations only; the token it
// obtains for each exchange is limited to issues in the configured repository and is never retained.
sealed class GitHubIssues(HttpMessageHandler handler,IssueTrackerSettings settings,RSA key)
{
    const string Origin="https://api.github.com";
    // Small pages, so that one page of long issues still fits the byte bound.
    const int PageSize=20,LookupPages=15;
    // The tracker's clock and this one may differ; a listing starts this long before the request was retained.
    static readonly TimeSpan ClockAllowance=TimeSpan.FromMinutes(5);
    readonly HttpMessageInvoker http=new(handler,false);
    readonly ModelTransportLimits limits=new(TimeSpan.FromSeconds(settings.ResponseSeconds),settings.MaximumResponseBytes);

    public string Repository=>settings.Repository;

    public static GitHubIssues Open(IssueTrackerSettings settings,Uri? checkEndpoint)
    {
        var file=new FileInfo(settings.PrivateKeyFile);
        if(OperatingSystem.IsWindows()||file is not {Exists:true,LinkTarget:null}||(File.GetUnixFileMode(file.FullName)&~(UnixFileMode.UserRead|UnixFileMode.UserWrite))!=0)
            throw new InvalidOperationException("The issue tracker private key must be a regular file that only its owner can read.");
        var key=RSA.Create();
        // Signing once here means a public key or a damaged one stops the API from starting, not a later dispatch.
        try{key.ImportFromPem(File.ReadAllText(file.FullName));key.SignData([],HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);}
        catch(Exception e) when(e is ArgumentException or CryptographicException){throw new InvalidOperationException("The issue tracker private key is not a private RSA key in PEM form.");}
        return new(ModelTransport.Handler(checkEndpoint,TimeSpan.FromSeconds(settings.ConnectSeconds)),settings,key);
    }

    public async Task<IssueReportOutcome> Create(IssueReportClaim claim,CancellationToken ct)
    {
        // An attempt after the first follows one that may have created the issue. Until a complete listing shows
        // no marked issue, nothing it learns can prove the report failed.
        var first=claim.Attempt==1;
        IssueReportOutcome Unsent(string code)=>first?IssueReportOutcome.Failed(code):IssueReportOutcome.Uncertain("outcome_unknown");
        if(claim.Repository!=settings.Repository)return Unsent("tracker_repository_changed");
        using var deadline=Deadline(ct);
        string token;
        try{token=await Token(deadline.Token);}
        catch(IssueTrackerException e){return Unsent(e.Code);}
        if(!first)
        {
            var earlier=await Listed(token,claim.Marker,claim.RequestedAt,deadline.Token);
            if(!earlier.Complete)return IssueReportOutcome.Uncertain("outcome_unknown");
            if(earlier.Issue is not null)return earlier.Issue;
        }
        try
        {
            using var response=await Send(HttpMethod.Post,$"/repos/{settings.Repository}/issues",token,
                JsonSerializer.Serialize(new{title=claim.Title,body=$"Kind: {claim.Kind}\n\n{claim.Body}\n\n{Needle(claim.Marker)}"}),deadline.Token);
            return (int)response.StatusCode switch
            {
                201=>await Issue(response,deadline.Token)??IssueReportOutcome.Uncertain("outcome_unknown"),
                429=>IssueReportOutcome.Failed("tracker_rate_limited"),
                >=300 and <500=>IssueReportOutcome.Failed("tracker_refused"),
                _=>IssueReportOutcome.Uncertain("outcome_unknown")
            };
        }
        catch(HttpRequestException e) when(NeverSent(e)){return IssueReportOutcome.Failed("tracker_unavailable");}
        catch(Exception e) when(Unreadable(e)){return IssueReportOutcome.Uncertain("outcome_unknown");}
    }

    public async Task<IssueLookup> Find(Guid marker,DateTimeOffset requestedAt,CancellationToken ct)
    {
        using var deadline=Deadline(ct);
        try{return await Listed(await Token(deadline.Token),marker,requestedAt,deadline.Token);}
        catch(IssueTrackerException){return new(null,false);}
    }

    CancellationTokenSource Deadline(CancellationToken ct)
    {
        var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.ResponseSeconds));
        return deadline;
    }

    static string Needle(Guid marker)=>$"<!-- report {marker:D} -->";

    // The report's issue is the oldest marked issue created since shortly before the request: the marker is public
    // once that issue exists, so a later copy and an older issue edited to carry it are not it. The listing is read
    // newest first and is complete at the first issue older than that, or at a short page.
    async Task<IssueLookup> Listed(string token,Guid marker,DateTimeOffset requestedAt,CancellationToken ct)
    {
        var earliest=requestedAt-ClockAllowance;
        var since=earliest.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture);
        IssueReportOutcome? oldest=null;
        try
        {
            for(var page=1;page<=LookupPages;page++)
            {
                using var response=await Send(HttpMethod.Get,$"/repos/{settings.Repository}/issues?state=all&since={since}&sort=created&direction=desc&per_page={PageSize}&page={page}",token,null,ct);
                if(response.StatusCode!=HttpStatusCode.OK)return new(null,false);
                using var listing=await Json(response,ct);
                if(listing.RootElement.ValueKind!=JsonValueKind.Array)return new(null,false);
                foreach(var item in listing.RootElement.EnumerateArray())
                {
                    if(item.ValueKind!=JsonValueKind.Object||!item.TryGetProperty("created_at",out var created)||created.ValueKind!=JsonValueKind.String
                        ||!created.TryGetDateTimeOffset(out var createdAt))return new(null,false);
                    if(createdAt<earliest)return new(oldest,true);
                    if(item.TryGetProperty("pull_request",out _)||!item.TryGetProperty("body",out var body)||body.ValueKind!=JsonValueKind.String
                        ||!body.GetString()!.Contains(Needle(marker),StringComparison.Ordinal))continue;
                    if((oldest=Issue(item)) is null)return new(null,false);
                }
                if(listing.RootElement.GetArrayLength()<PageSize)return new(oldest,true);
            }
            return new(null,false);
        }
        catch(Exception e) when(Unreadable(e)){return new(null,false);}
    }

    async Task<string> Token(CancellationToken ct)
    {
        try
        {
            using var response=await Send(HttpMethod.Post,$"/app/installations/{settings.InstallationId}/access_tokens",Assertion(),
                JsonSerializer.Serialize(new{repositories=new[]{settings.Repository[(settings.Repository.IndexOf('/')+1)..]},permissions=new{issues="write"}}),ct);
            if(response.StatusCode!=HttpStatusCode.Created)
                throw new IssueTrackerException((int)response.StatusCode is 401 or 403 or 404 or 422?"tracker_authentication":"tracker_unavailable");
            using var answer=await Json(response,ct);
            return answer.RootElement.ValueKind==JsonValueKind.Object&&answer.RootElement.TryGetProperty("token",out var token)&&token.ValueKind==JsonValueKind.String
                &&token.GetString() is {Length:>0 and <=4096} value&&value.All(c=>c is >' ' and <'\u007f')
                ?value:throw new IssueTrackerException("tracker_authentication");
        }
        catch(JsonException){throw new IssueTrackerException("tracker_authentication");}
        catch(Exception e) when(Unreadable(e)){throw new IssueTrackerException("tracker_unavailable");}
    }

    // A short-lived assertion that this application is the configured app, signed with its private key.
    string Assertion()
    {
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var unsigned=Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new{alg="RS256",typ="JWT"}))+"."
            +Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new{iat=now-60,exp=now+480,iss=settings.AppId}));
        return unsigned+"."+Base64Url.EncodeToString(key.SignData(Encoding.ASCII.GetBytes(unsigned),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1));
    }

    Task<HttpResponseMessage> Send(HttpMethod method,string path,string bearer,string? json,CancellationToken ct)
    {
        var request=new HttpRequestMessage(method,Origin+path);
        request.Headers.Authorization=new("Bearer",bearer);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version",settings.ApiVersion);
        request.Headers.UserAgent.ParseAdd("openetrm-issue-reports");
        if(json is not null)request.Content=new StringContent(json,Encoding.UTF8,"application/json");
        return http.SendAsync(request,ct);
    }

    async Task<JsonDocument> Json(HttpResponseMessage response,CancellationToken ct)
    {
        await using var body=await ModelTransport.Body(response,limits,ct);
        return ModelTransport.Parse(await ModelTransport.Text(body,ct));
    }

    async Task<IssueReportOutcome?> Issue(HttpResponseMessage response,CancellationToken ct)
    {
        using var created=await Json(response,ct);
        return Issue(created.RootElement);
    }

    // An issue is confirmed only by a positive number and this repository's own page for it.
    IssueReportOutcome? Issue(JsonElement issue)=>
        issue.ValueKind==JsonValueKind.Object&&issue.TryGetProperty("number",out var number)&&number.ValueKind==JsonValueKind.Number&&number.TryGetInt64(out var value)&&value>0
        &&issue.TryGetProperty("html_url",out var url)&&url.ValueKind==JsonValueKind.String&&url.GetString() is {} address
        &&string.Equals(address,$"https://github.com/{settings.Repository}/issues/{value}",StringComparison.OrdinalIgnoreCase)
            ?IssueReportOutcome.Submitted(value,address):null;

    // The connection was never established, so the request cannot have reached the tracker.
    static bool NeverSent(HttpRequestException e)=>
        e.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError;

    static bool Unreadable(Exception e)=>
        e is HttpRequestException or OperationCanceledException or IOException or JsonException or ModelStreamException or DecoderFallbackException;
}

sealed class IssueTrackerException(string code):Exception(code)
{
    public string Code{get;}=code;
}
