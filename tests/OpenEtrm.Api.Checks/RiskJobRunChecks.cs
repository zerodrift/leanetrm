using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

// D222: each queued part of a run made in parts states the run it belongs to, which part it is and how many parts the run has, and
// its retained frame keeps them, so that the job history can show the parts as one run. A job queued on its own states none.
static class RiskJobRunChecks
{
    private static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    private static string? Code(Action action){try{action();return null;}catch(MarketDataException error){return error.Code;}}
    private static SubmitPortfolioRiskJob Submission(string run)=>
        JsonSerializer.Deserialize<SubmitPortfolioRiskJob>($$"""{"requestId":"{{Guid.NewGuid()}}","request":null{{run}}}""",RiskJobs.Json)!;

    public static void Run()
    {
        var run=Guid.NewGuid();
        var part=Submission($$""","run":{"id":"{{run}}","part":2,"parts":5}""");
        Require(part.Run==new RiskJobRun(run,2,5),"A submission states the run its part belongs to, the part and the run's part count.");
        var retained=RiskJobs.RunFrame(part.Run!);
        Require(retained.ToJsonString()==JsonSerializer.Serialize(new{id=run,part=2,parts=5},RiskJobs.Json),
            "The retained frame keeps the run's identity, the part and the part count, and nothing else.");
        Require(Submission("").Run is null,"A job queued on its own states no run.");
        try{Submission($$""","run":{"id":"{{run}}","part":1,"parts":2,"label":"Synthetic"}""");throw new Exception("A run stated another member.");}
        catch(JsonException){}
        foreach(var (invalid,why) in new[]{(new RiskJobRun(Guid.Empty,1,2),"no identity"),(new RiskJobRun(run,0,2),"part 0"),(new RiskJobRun(run,3,2),"a part beyond the count"),
            (new RiskJobRun(run,1,0),"no parts"),(new RiskJobRun(run,-1,2),"a negative part"),(new RiskJobRun(run,1,RiskJobRun.MaxParts+1),"more parts than a run takes")})
            Require(Code(()=>RiskJobs.RunFrame(invalid))=="risk_job_run",$"A run with {why} is refused before it is retained.");
        Require(RiskJobs.RunFrame(new RiskJobRun(run,RiskJobRun.MaxParts,RiskJobRun.MaxParts))["part"]!.GetValue<int>()==RiskJobRun.MaxParts,
            "The last part of the largest run is retained.");
        // Every job keeps the books and portfolios its run was asked for, as chosen, so that its results are shown only against them.
        var books=ImmutableArray.Create(Guid.NewGuid(),Guid.NewGuid());var portfolios=ImmutableArray.Create(Guid.NewGuid());
        Require(RiskJobs.ScopeFrame(books,portfolios).ToJsonString()==JsonSerializer.Serialize(new{bookIds=books,portfolioIds=portfolios},RiskJobs.Json)
                &&RiskJobs.ScopeFrame(books,[]).ToJsonString()==JsonSerializer.Serialize(new{bookIds=books,portfolioIds=Array.Empty<Guid>()},RiskJobs.Json),
            "A job's frame keeps the books and portfolios its run was asked for, in the order chosen, and an empty choice as empty.");
        Console.WriteLine("PASS risk job runs: a queued part states its run, its part and the run's part count, each checked before it is retained, and every job its books and portfolios");
    }
}
