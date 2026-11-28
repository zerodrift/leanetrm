using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

// The tracker adapter against an in-process synthetic tracker: what it sends, how it reads each answer and when
// an outcome is settled or left uncertain.
static class IssueTrackerChecks
{
    const string Repository="synthetic-owner/synthetic-tracker";
    const string IssueUrl="https://github.com/synthetic-owner/synthetic-tracker/issues/17";
    static readonly Guid Marker=Guid.Parse("7c1f3a52-0d4e-4b7a-9a61-2f5d8c3e1b90");
    static readonly DateTimeOffset Requested=new(2040,1,2,3,4,5,TimeSpan.Zero);
    static readonly string Marked="Kind: bug\n\ntext\n\n<!-- report "+Marker.ToString("D")+" -->";

    public static async Task Run()
    {
        using var key=RSA.Create(2048);
        var settings=new IssueTrackerSettings(Repository,"synthetic-app",42,"/synthetic/private-key.pem","2022-11-28",2,5,1048576);
        settings.Validate();
        Require(!settings.ToString().Contains("private-key",StringComparison.Ordinal),"settings never print the key location");
        foreach(var invalid in new[]{settings with{Repository="synthetic-owner"},settings with{Repository="a/b/c"},settings with{Repository="synthetic-owner/.."},
            settings with{Repository=Repository+"\n"},settings with{AppId=" "},settings with{AppId="synthetic-app\n"},settings with{InstallationId=0},
            settings with{PrivateKeyFile="relative.pem"},settings with{ApiVersion="latest"},settings with{ApiVersion="2022-11-28\n"},settings with{ConnectSeconds=0},
            settings with{ResponseSeconds=4},settings with{ResponseSeconds=61},settings with{MaximumResponseBytes=65536}})
        {
            try{invalid.Validate();throw new Exception("Accepted incomplete tracker settings");}
            catch(MarketDataException e) when(e.Code=="issue_tracker_configuration"){}
        }
        Keys(settings,key);

        IssueReportClaim Claim(int attempt=1,string repository=Repository)=>new(Guid.NewGuid(),attempt,Marker,repository,"bug","Synthetic totals differ","Steps: amend a synthetic trade.",Requested);
        Task<IssueReportOutcome> Create(Tracker tracker,IssueReportClaim? claim=null)=>new GitHubIssues(tracker,settings,key).Create(claim??Claim(),CancellationToken.None);
        Task<IssueLookup> Find(Tracker tracker)=>new GitHubIssues(tracker,settings,key).Find(Marker,Requested,CancellationToken.None);
        var uncertain=IssueReportOutcome.Uncertain("outcome_unknown");
        const string created="{\"number\":17,\"html_url\":\""+IssueUrl+"\"}";

        // One token request, scoped to issues in the one repository, then one create request.
        var tracker=new Tracker(key,(HttpStatusCode.Created,created));
        var outcome=await Create(tracker);
        Require(outcome==IssueReportOutcome.Submitted(17,IssueUrl),"a created issue settles with its number and link");
        Require(tracker.Requests.SequenceEqual(new[]{"POST /app/installations/42/access_tokens","POST /repos/synthetic-owner/synthetic-tracker/issues"}),"exactly one token and one create request");
        Require(tracker.TokenScope=="{\"repositories\":[\"synthetic-tracker\"],\"permissions\":{\"issues\":\"write\"}}","the token is limited to issues in the configured repository");
        using(var issue=JsonDocument.Parse(tracker.Bodies[1]))
        {
            var body=issue.RootElement.GetProperty("body").GetString()!;
            Require(issue.RootElement.EnumerateObject().Select(p=>p.Name).SequenceEqual(new[]{"title","body"}),"only a title and a body are sent");
            Require(issue.RootElement.GetProperty("title").GetString()=="Synthetic totals differ","the reviewed title is sent unchanged");
            Require(body=="Kind: bug\n\nSteps: amend a synthetic trade.\n\n<!-- report "+Marker.ToString("D")+" -->","the reviewed text is followed by the correlation marker alone");
        }
        Require(!outcome.ToString().Contains(Tracker.Token,StringComparison.Ordinal),"an outcome carries no credential");
        Require(await Create(new Tracker(key,(HttpStatusCode.Created,created.Replace("synthetic-owner","Synthetic-Owner"))))
            ==IssueReportOutcome.Submitted(17,IssueUrl.Replace("synthetic-owner","Synthetic-Owner")),"the tracker's own spelling of the repository is accepted");

        // On a first attempt, answers that prove nothing was created settle as failed and are never uncertain.
        foreach(var (status,code) in new[]{(HttpStatusCode.UnprocessableEntity,"tracker_refused"),(HttpStatusCode.Gone,"tracker_refused"),(HttpStatusCode.Forbidden,"tracker_refused"),
            (HttpStatusCode.NotFound,"tracker_refused"),(HttpStatusCode.MovedPermanently,"tracker_refused"),(HttpStatusCode.TooManyRequests,"tracker_rate_limited")})
            Require(await Create(new Tracker(key,(status,"{\"message\":\"private tracker text\"}")))==IssueReportOutcome.Failed(code),$"{(int)status} settles as {code}");
        // A token that cannot be obtained means the create request was never sent.
        var unauthenticated=new Tracker(key){TokenStatus=HttpStatusCode.Unauthorized};
        Require(await Create(unauthenticated)==IssueReportOutcome.Failed("tracker_authentication")&&unauthenticated.Requests.Count==1,"a refused token settles without a create request");
        Require(await Create(new Tracker(key){TokenStatus=HttpStatusCode.BadGateway})==IssueReportOutcome.Failed("tracker_unavailable"),"an unavailable token service settles as unavailable");
        Require(await Create(new Tracker(key){TokenBody="{\"token\":\"\"}"})==IssueReportOutcome.Failed("tracker_authentication"),"an unusable token answer settles without a create request");
        Require(await Create(new Tracker(key){TokenBody="{\"token\":17}"})==IssueReportOutcome.Failed("tracker_authentication"),"a token of another type settles without a create request");
        Require(await Create(new Tracker(key){Fault=new HttpRequestException(HttpRequestError.ConnectionError)})==IssueReportOutcome.Failed("tracker_unavailable"),"a token connection that was never made settles as unavailable");
        var unreachable=new Tracker(key){CreateFault=new HttpRequestException(HttpRequestError.ConnectionError)};
        Require(await Create(unreachable)==IssueReportOutcome.Failed("tracker_unavailable")&&unreachable.Requests.Count==2,"a create connection that was never made settles as unavailable");
        var elsewhere=new Tracker(key,(HttpStatusCode.Created,created));
        Require(await Create(elsewhere,Claim(repository:"synthetic-owner/another-tracker"))==IssueReportOutcome.Failed("tracker_repository_changed")&&elsewhere.Requests.Count==0,"a report retained for another repository is never sent to this one");

        // Anything after the create request left that does not prove the result stays uncertain.
        foreach(var answer in new[]{(HttpStatusCode.ServiceUnavailable,"{}"),(HttpStatusCode.InternalServerError,"{}"),(HttpStatusCode.Created,"not json"),(HttpStatusCode.Created,"[]"),
            (HttpStatusCode.Created,"{\"number\":0,\"html_url\":\""+IssueUrl+"\"}"),(HttpStatusCode.Created,"{\"number\":\"17\",\"html_url\":\""+IssueUrl+"\"}"),
            (HttpStatusCode.Created,"{\"number\":17,\"html_url\":17}"),(HttpStatusCode.Created,"{\"number\":17,\"html_url\":\"http://github.com/synthetic-owner/synthetic-tracker/issues/17\"}"),
            (HttpStatusCode.Created,"{\"number\":17,\"html_url\":\"https://elsewhere.invalid/issues/17\"}"),
            (HttpStatusCode.Created,"{\"number\":17,\"html_url\":\"https://github.com/someone-else/elsewhere/issues/17\"}"),
            (HttpStatusCode.Created,"{\"number\":17,\"html_url\":\"https://github.com/synthetic-owner/synthetic-tracker/issues/18\"}"),
            (HttpStatusCode.Created,"{\"number\":17,\"html_url\":\""+IssueUrl+"?next=/issues/17\"}"),
            (HttpStatusCode.Created,"{\"number\":17,\"number\":18,\"html_url\":\""+IssueUrl+"\"}"),(HttpStatusCode.OK,created)})
            Require(await Create(new Tracker(key,answer))==uncertain,$"{(int)answer.Item1} {answer.Item2} is not a confirmed outcome");
        Require(await Create(new Tracker(key){CreateFault=new HttpRequestException(HttpRequestError.ResponseEnded)})==uncertain,"a response lost after sending is uncertain");
        var started=DateTimeOffset.UtcNow;
        Require(await Create(new Tracker(key){Silent=true})==uncertain&&DateTimeOffset.UtcNow-started<TimeSpan.FromSeconds(15),"a tracker that never answers is uncertain within the configured bound");
        Require(await Create(new Tracker(key,(HttpStatusCode.Created,"{\"number\":17,\"html_url\":\""+IssueUrl+"\",\"padding\":\""+new string('x',1100000)+"\"}")))==uncertain,"an answer over the byte bound is not read as confirmation");

        // A later attempt follows one that may have created the issue. It looks for the marker first, creates
        // only when the listing is complete without it, and settles as failed only after both.
        var found=new Tracker(key,(HttpStatusCode.OK,Listed((17,Marked,false,1),(16,Marked,true,1),(3,"unrelated",false,1))));
        Require(await Create(found,Claim(2))==IssueReportOutcome.Submitted(17,IssueUrl)&&found.Requests.Count==2,"an issue that already carries the marker settles the later attempt without another create");
        var absent=new Tracker(key,(HttpStatusCode.OK,Listed((3,"unrelated",false,1))),(HttpStatusCode.Created,created));
        Require(await Create(absent,Claim(2))==IssueReportOutcome.Submitted(17,IssueUrl)&&absent.Requests.Count==3,"a complete listing without the marker lets the later attempt create");
        Require(await Create(new Tracker(key,(HttpStatusCode.OK,"[]"),(HttpStatusCode.UnprocessableEntity,"{}")),Claim(2))==IssueReportOutcome.Failed("tracker_refused"),"a refusal after a complete listing without the marker settles as failed");
        var unreadable=new Tracker(key,(HttpStatusCode.ServiceUnavailable,"{}"));
        Require(await Create(unreadable,Claim(2))==uncertain&&unreadable.Requests.Count==2,"a later attempt that cannot read the listing sends nothing");
        foreach(var unavailable in new[]{new Tracker(key){TokenStatus=HttpStatusCode.Unauthorized},new Tracker(key){TokenStatus=HttpStatusCode.BadGateway},new Tracker(key){Fault=new HttpRequestException(HttpRequestError.ConnectionError)}})
            Require(await Create(unavailable,Claim(2))==uncertain,"a later attempt that cannot look stays uncertain, because the earlier one may have created the issue");
        var moved=new Tracker(key);
        Require(await Create(moved,Claim(2,"synthetic-owner/another-tracker"))==uncertain&&moved.Requests.Count==0,"a later attempt for another repository stays uncertain and sends nothing");
        Require(await Create(new Tracker(key,(HttpStatusCode.OK,"[]")){CreateFault=new HttpRequestException(HttpRequestError.ConnectionError)},Claim(2))==IssueReportOutcome.Failed("tracker_unavailable"),"after a complete listing without the marker, a create connection never made settles as unavailable");

        // The lookup reads issues created since shortly before the request, newest first, to the first older one.
        var lookup=new Tracker(key,(HttpStatusCode.OK,Listed((17,Marked,false,1))));
        Require(await Find(lookup)==new IssueLookup(IssueReportOutcome.Submitted(17,IssueUrl),true),"the marked issue is found");
        Require(lookup.Requests[1]=="GET /repos/synthetic-owner/synthetic-tracker/issues?state=all&since=2040-01-02T02:59:05Z&sort=created&direction=desc&per_page=100&page=1","the listing starts shortly before the request was retained and is read newest first");
        Require(await Find(new Tracker(key,(HttpStatusCode.OK,"[]")))==new IssueLookup(null,true),"a complete listing without the marker says so");
        // The marker is public once its issue exists. A later copy and an older issue edited to carry it are not the report's issue.
        Require(await Find(new Tracker(key,(HttpStatusCode.OK,Listed((20,Marked,false,10),(17,Marked,false,1),(3,Marked,false,-1440)))))==new IssueLookup(IssueReportOutcome.Submitted(17,IssueUrl),true),"the oldest marked issue created since the request is the report's");
        Require(await Find(new Tracker(key,(HttpStatusCode.OK,Listed((3,Marked,false,-1440)))))==new IssueLookup(null,true),"an issue created before the request never matches");
        var page=Enumerable.Range(1,99).Select(i=>(1000-i,"unrelated",false,1)).Append((5,"unrelated",false,-1440)).ToArray();
        var bounded=new Tracker(key,(HttpStatusCode.OK,Listed(page)));
        Require(await Find(bounded)==new IssueLookup(null,true)&&bounded.Requests.Count==2,"the listing ends at the first issue older than the request");
        var full=Listed(Enumerable.Range(1,100).Select(i=>(i,"unrelated",false,1)).ToArray());
        var endless=new Tracker(key,Enumerable.Repeat((HttpStatusCode.OK,full),5).ToArray());
        Require(await Find(endless)==new IssueLookup(null,false)&&endless.Requests.Count==6,"more than five pages of newer issues is reported as incomplete, not as absence");
        var first=Listed(Enumerable.Range(1,99).Select(i=>(i+100,"unrelated",false,2)).Prepend((17,Marked,false,3)).ToArray());
        Require(await Find(new Tracker(key,Enumerable.Repeat((HttpStatusCode.OK,full),4).Prepend((HttpStatusCode.OK,first)).ToArray()))==new IssueLookup(null,false),"a marked issue in a listing that was not read to its end is not yet the report's");
        foreach(var unusable in new[]{(HttpStatusCode.Forbidden,"{}"),(HttpStatusCode.OK,"{}"),(HttpStatusCode.OK,"[17]"),(HttpStatusCode.OK,"[{\"number\":17}]"),
            (HttpStatusCode.OK,"[{\"number\":17,\"created_at\":\"soon\",\"body\":\"x\"}]"),
            (HttpStatusCode.OK,Listed((17,Marked,false,1)).Replace("synthetic-tracker/issues/17","another-tracker/issues/17"))})
            Require(await Find(new Tracker(key,unusable))==new IssueLookup(null,false),$"{(int)unusable.Item1} {unusable.Item2} is an incomplete listing");
        Require(await Find(new Tracker(key){TokenStatus=HttpStatusCode.Unauthorized})==new IssueLookup(null,false),"a lookup without a token is incomplete");
        Console.WriteLine("PASS issue tracker: usable key, scoped token, exact reviewed text and marker, settled refusals, uncertain lost answers, later attempts never fail unproven, lookup bounded to issues created since the request");
    }
    static void Require(bool condition,string label){if(!condition)throw new Exception(label);}

    // Minutes are when the issue was created, relative to the request.
    static string Listed(params (int number,string body,bool pull,int minutes)[] items)=>JsonSerializer.Serialize(items.Select(i=>i.pull
        ?(object)new{number=i.number,html_url="https://github.com/synthetic-owner/synthetic-tracker/pull/"+i.number,created_at=Requested.AddMinutes(i.minutes),body=i.body,pull_request=new{}}
        :new{number=i.number,html_url="https://github.com/synthetic-owner/synthetic-tracker/issues/"+i.number,created_at=Requested.AddMinutes(i.minutes),body=i.body}));

    // The connection opens only with a private RSA key that its owner alone can read.
    static void Keys(IssueTrackerSettings settings,RSA key)
    {
        if(OperatingSystem.IsWindows())return;
        var directory=Directory.CreateTempSubdirectory("oetrm-issue-key-");
        try
        {
            string Stored(string name,string pem,UnixFileMode mode){var path=Path.Combine(directory.FullName,name);File.WriteAllText(path,pem);File.SetUnixFileMode(path,mode);return path;}
            const UnixFileMode owner=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            GitHubIssues.Open(settings with{PrivateKeyFile=Stored("private.pem",key.ExportRSAPrivateKeyPem(),owner)},null);
            foreach(var unusable in new[]{Stored("public.pem",key.ExportRSAPublicKeyPem(),owner),Stored("shared.pem",key.ExportRSAPrivateKeyPem(),owner|UnixFileMode.GroupRead),
                Stored("text.pem","not a key",owner),Path.Combine(directory.FullName,"missing.pem")})
            {
                try{GitHubIssues.Open(settings with{PrivateKeyFile=unusable},null);throw new Exception("Opened a tracker connection with "+Path.GetFileName(unusable));}
                catch(InvalidOperationException){}
            }
        }
        finally{directory.Delete(true);}
    }

    sealed class Tracker(RSA key,params (HttpStatusCode Status,string Body)[] answers):HttpMessageHandler
    {
        public const string Token="synthetic-installation-token";
        readonly Queue<(HttpStatusCode Status,string Body)> queue=new(answers);
        public List<string> Requests{get;}=[];
        public List<string> Bodies{get;}=[];
        public string? TokenScope{get;private set;}
        public HttpStatusCode TokenStatus{get;init;}=HttpStatusCode.Created;
        public string TokenBody{get;init;}="{\"token\":\""+Token+"\",\"expires_at\":\"2040-01-01T00:00:00Z\"}";
        public Exception? Fault{get;init;}
        public Exception? CreateFault{get;init;}
        public bool Silent{get;init;}

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(Fault is not null)throw Fault;
            Require(request.RequestUri!.GetLeftPart(UriPartial.Authority)=="https://api.github.com","requests go to the one tracker origin");
            Require(request.Headers.Accept.ToString()=="application/vnd.github+json"&&request.Headers.GetValues("X-GitHub-Api-Version").Single()=="2022-11-28"
                &&request.Headers.UserAgent.ToString()=="openetrm-issue-reports","the documented request headers are sent");
            Requests.Add(request.Method+" "+request.RequestUri.PathAndQuery);
            Bodies.Add(request.Content is null?"":await request.Content.ReadAsStringAsync(ct));
            var bearer=request.Headers.Authorization is{Scheme:"Bearer"} a?a.Parameter!:throw new Exception("bearer authorization required");
            if(request.RequestUri.AbsolutePath.EndsWith("/access_tokens",StringComparison.Ordinal))
            {
                Assertion(bearer);
                TokenScope=Bodies[^1];
                return Answer(TokenStatus,TokenBody);
            }
            Require(bearer==Token,"the installation token authorizes tracker requests");
            if(Silent)await Task.Delay(Timeout.Infinite,ct);
            if(CreateFault is not null&&request.Method==HttpMethod.Post)throw CreateFault;
            var (status,body)=queue.Dequeue();
            return Answer(status,body);
        }
        static HttpResponseMessage Answer(HttpStatusCode status,string body)=>new(status){Content=new StringContent(body,Encoding.UTF8,"application/json")};

        // The application signs a short-lived assertion as the configured app; nothing else is in it.
        void Assertion(string jwt)
        {
            var parts=jwt.Split('.');
            Require(parts.Length==3,"a three-part signed assertion");
            static byte[] Decode(string value)=>Convert.FromBase64String(value.Replace('-','+').Replace('_','/').PadRight(value.Length+(4-value.Length%4)%4,'='));
            Require(key.VerifyData(Encoding.ASCII.GetBytes(parts[0]+"."+parts[1]),Decode(parts[2]),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1),"the assertion is signed with the app key");
            using var header=JsonDocument.Parse(Decode(parts[0]));
            using var claims=JsonDocument.Parse(Decode(parts[1]));
            Require(header.RootElement.GetProperty("alg").GetString()=="RS256","RS256");
            var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long iat=claims.RootElement.GetProperty("iat").GetInt64(),exp=claims.RootElement.GetProperty("exp").GetInt64();
            Require(claims.RootElement.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(new[]{"exp","iat","iss"})&&claims.RootElement.GetProperty("iss").GetString()=="synthetic-app"
                &&iat<=now-30&&exp>now&&exp<=now+600,"issuer and a lifetime inside the documented ten minutes");
        }
    }
}
