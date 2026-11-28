using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveIssueReportPolicyRequest([property:JsonRequired]Guid OperationId,[property:JsonRequired]int ExpectedVersion,
    [property:JsonRequired]string Mode,[property:JsonRequired]int? DailyLimit);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitIssueReportRequest([property:JsonRequired]Guid OperationId,[property:JsonRequired]string Kind,
    [property:JsonRequired]string Title,[property:JsonRequired]string Body);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IssueReportReference([property:JsonRequired]Guid Id);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResolveIssueReportRequest([property:JsonRequired]Guid Id,[property:JsonRequired]int ExpectedAttempt,[property:JsonRequired]string Resolution);

// A person's report to the installation's issue tracker (docs/issue-reports.md). The database retains each
// request and decides what may be sent; this class carries one admitted dispatch to the tracker and back.
static class IssueReports
{
    public const string Submit="SubmitIssueReports",Manage="ManageIssueReporting";
    // The name the assistant discovers these operations under. It is not a screen the assistant reads.
    public const string Page="issue_reports";
    static readonly JsonSerializerOptions Web=new(JsonSerializerDefaults.Web);
    sealed record Reconciliation(Guid Id,int Attempt,Guid Marker,string Repository,DateTimeOffset RequestedAt);

    public static int Status(string code)=>code switch
    {
        "issue_report_not_found"=>404,
        "issue_report_limit"=>429,
        "issue_report_input" or "issue_report_policy_input"=>422,
        "issue_report_outcome_unknown"=>503,
        _=>409
    };
    public static string Text(string code)=>code switch
    {
        "issue_reporting_disabled"=>"Issue reporting is not enabled for this workspace.",
        "issue_tracker_unconfigured"=>"This installation has no issue tracker connection.",
        "issue_report_input"=>"Supply a kind, a one-line title of at most 120 characters and a description of at most 4,000 characters.",
        "issue_report_policy_input"=>"Choose disabled, or review required with a daily limit from 1 to 50 for each person.",
        "issue_report_limit"=>"You have reached this workspace's daily limit for issue reports.",
        "issue_report_not_found"=>"That issue report is not one of yours.",
        "issue_report_wait"=>"The tracker was asked about this report a moment ago. Wait two minutes before sending it again, or one minute before looking again.",
        "issue_report_fence" or "issue_report_transition"=>"The issue report changed. Reload it before deciding again.",
        "issue_report_outcome_unknown"=>"The issue report's outcome was not recorded. Reload your reports; do not submit it again.",
        _=>"The request or the setting changed. Reload the current version, or retry the original request."
    };

    public static async Task<object> Policy(NpgsqlConnection connection,GitHubIssues? tracker,CancellationToken ct)=>
        new{repository=tracker?.Repository,policy=await Read(connection,"SELECT assistant.issue_report_policy_directory()::text",[],ct)};
    public static Task<JsonElement> SavePolicy(NpgsqlConnection connection,SaveIssueReportPolicyRequest body,CancellationToken ct)=>
        Read(connection,"SELECT assistant.save_issue_report_policy($1,$2,$3,$4)::text",[body.OperationId,body.ExpectedVersion,Value(body.Mode),Value(body.DailyLimit)],ct);
    public static async Task<object> Mine(NpgsqlConnection connection,GitHubIssues? tracker,CancellationToken ct)=>
        new{repository=tracker?.Repository,reports=await Read(connection,"SELECT assistant.my_issue_reports()::text",[],ct)};

    public static async Task<JsonElement> Request(PreviewDatabase database,string profile,GitHubIssues? tracker,SubmitIssueReportRequest body,CancellationToken ct)
    {
        var configured=Configured(tracker);
        var report=await database.WithIdentity(profile,Submit,(connection,token)=>Read(connection,"SELECT assistant.request_issue_report($1,$2,$3,$4,$5)::text",
            [body.OperationId,Value(body.Kind),Value(body.Title),Value(body.Body),configured.Repository],token),ct);
        return await Dispatch(database,profile,configured,"SELECT assistant.admit_issue_report_dispatch($1)::text",[report.GetProperty("id").GetGuid()],ct)??report;
    }

    public static async Task<object> Reconcile(PreviewDatabase database,string profile,GitHubIssues? tracker,IssueReportReference body,CancellationToken ct)
    {
        var configured=Configured(tracker);
        if(await database.WithIdentity(profile,Submit,(connection,token)=>Scalar(connection,"SELECT assistant.admit_issue_report_lookup($1)::text",[body.Id],token),ct) is not {} admitted)
            return new{report=await Current(database,profile,body.Id,ct),lookup="not_needed"};
        var unconfirmed=JsonSerializer.Deserialize<Reconciliation>(admitted,Web)!;
        var found=unconfirmed.Repository==configured.Repository?await configured.Find(unconfirmed.Marker,unconfirmed.RequestedAt,CancellationToken.None):new(null,false);
        return found.Issue is not null
            ?new{report=await Complete(database,profile,unconfirmed.Id,unconfirmed.Attempt,found.Issue),lookup="found"}
            :new{report=await Current(database,profile,body.Id,ct),lookup=found.Complete?"absent":"incomplete"};
    }

    // The person's decision about a report that is not settled: give it up, or send it again. A resend whose
    // admission answers nothing was already carried out, and the report's current state is its result.
    public static async Task<JsonElement> Resolve(PreviewDatabase database,string profile,GitHubIssues? tracker,ResolveIssueReportRequest body,CancellationToken ct)=>body.Resolution switch
    {
        "abandon"=>await database.WithIdentity(profile,null,(connection,token)=>Read(connection,"SELECT assistant.abandon_issue_report($1,$2)::text",[body.Id,body.ExpectedAttempt],token),ct),
        "resend"=>await Dispatch(database,profile,Configured(tracker),"SELECT assistant.admit_issue_report_resend($1,$2)::text",[body.Id,body.ExpectedAttempt],ct)
            ??await Current(database,profile,body.Id,ct),
        _=>throw new MarketDataException("issue_report_input","Choose resend or abandon.")
    };

    static GitHubIssues Configured(GitHubIssues? tracker)=>
        tracker??throw new MarketDataException("issue_tracker_unconfigured",Text("issue_tracker_unconfigured"));

    static Task<JsonElement> Current(PreviewDatabase database,string profile,Guid report,CancellationToken ct)=>
        database.WithIdentity(profile,null,(connection,token)=>Read(connection,"SELECT assistant.issue_report_status($1)::text",[report],token),ct);

    // Admission commits before the tracker is called. From then on neither the caller leaving nor the request's
    // time limit may abandon the exchange: the tracker call has its own bound and its outcome its own short one.
    // Null means the database admitted nothing: the report is settled, unconfirmed or already in flight.
    static async Task<JsonElement?> Dispatch(PreviewDatabase database,string profile,GitHubIssues tracker,string admission,object[] values,CancellationToken ct)
    {
        if(await database.WithIdentity(profile,Submit,(connection,token)=>Scalar(connection,admission,values,token),ct) is not {} admitted)return null;
        var claim=JsonSerializer.Deserialize<IssueReportClaim>(admitted,Web)!;
        IssueReportOutcome outcome;
        try{outcome=await tracker.Create(claim,CancellationToken.None);}
        // The adapter classifies every answer it expects. Anything else happened after admission, when the
        // tracker may already have been reached, so it is recorded as unconfirmed, not left in flight.
        catch(Exception){outcome=IssueReportOutcome.Uncertain("outcome_unknown");}
        return await Complete(database,profile,claim.Id,claim.Attempt,outcome);
    }

    static async Task<JsonElement> Complete(PreviewDatabase database,string profile,Guid report,int attempt,IssueReportOutcome outcome)
    {
        using var recording=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            return await database.WithIdentity(profile,null,(connection,token)=>Read(connection,"SELECT assistant.complete_issue_report($1,$2,$3,$4,$5,$6)::text",
                [report,attempt,outcome.State,Value(outcome.Number),Value(outcome.Url),Value(outcome.Code)],token),recording.Token);
        }
        // The dispatch stays admitted and unrecorded, which the database later treats as unconfirmed.
        catch(Exception e) when(e is NpgsqlException and not PostgresException or OperationCanceledException)
        {
            throw new MarketDataException("issue_report_outcome_unknown",Text("issue_report_outcome_unknown"),e);
        }
    }

    // A missing member is the database's to refuse. PostgreSQL text cannot hold a NUL, which no report needs.
    static object Value(object? value)=>value switch
    {
        null=>DBNull.Value,
        string text when text.Contains('\0')=>throw new MarketDataException("issue_report_input",Text("issue_report_input")),
        _=>value
    };
    static async Task<string?> Scalar(NpgsqlConnection connection,string sql,object[] values,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,sql,values);
        return await command.ExecuteScalarAsync(ct) as string;
    }
    static async Task<JsonElement> Read(NpgsqlConnection connection,string sql,object[] values,CancellationToken ct)=>
        JsonSerializer.Deserialize<JsonElement>(await Scalar(connection,sql,values,ct)??throw new InvalidOperationException("The issue report operation returned nothing."));
}
