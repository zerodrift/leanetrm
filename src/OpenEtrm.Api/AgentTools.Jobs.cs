using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace OpenEtrm.Api;

// Jobs & Infrastructure (docs/devops-assistant.md §2–§3, D231): the jobs the turn's delegator owns, with each job's runs,
// audit actions and command receipts, read in the agent's request context under ReadJobs alone. The database decides
// whose jobs they are, so another person's job reads like an unknown one, a job manager's included.
public sealed partial class AgentTools
{
    static AgentTool JobTool=>Tool("read_jobs","Reads the user's own jobs on Jobs & Infrastructure as they are now: never another person's, and "+
        "never under a historical view. View jobs lists them newest first, 25 to a page, with their terms, state, version, whether they can be "+
        "edited and their latest run. View history returns one of them with its latest 25 runs, its audit actions newest first, which "+
        "record finished runs' outcomes, 100 before an audit sequence or the newest when before is null, and the terms its latest 25 create, "+
        "update or control requests carried. The database holds no container output: explain a run only from its states, error code and explanation, and say so. Send every member: those the other "+
        "view takes are null.",
        $$$"""
        {"type":"object","properties":{"view":{"type":"string","enum":["jobs","history"]},
         "job":{"type":["string","null"],"pattern":"{{{Uuid}}}","description":"History: the job to read. Null for jobs."},
         "page":{"type":["integer","null"],"minimum":0,"maximum":10000,"description":"Jobs: the page of 25, from 0. Null for history."},
         "before":{"type":["integer","null"],"minimum":1,"description":"History: return audit actions before this sequence, or null for the newest. Null for jobs."}},
         "required":["view","job","page","before"],"additionalProperties":false}
        """,["ReadJobs"]);

    static ImmutableArray<ReadAccess> JobAccess(PreviewManifest _)=>[new("job_access",null,"ReadJobs")];

    static ToolCall ParseJobs(ToolInput members)
    {
        members.Exactly("view","job","page","before");
        if(members.Choice("view","jobs","history")=="jobs")
        {
            members.Null("job","before");
            return new(Operations["jobs.list_v1"],new JobListArguments((int)members.Whole("page",0,10000)));
        }
        members.Null("page");
        return new(Operations["jobs.history_v1"],new JobHistoryArguments(members.Id("job"),members.OptionalWhole("before",1,long.MaxValue)));
    }

    static async Task<ToolResult> Jobs(NpgsqlConnection connection,JobListArguments list,CancellationToken ct)
    {
        var result=await JobRead(connection,"SELECT automation.owned_jobs($1)::text",ct,new NpgsqlParameter{Value=list.Page,NpgsqlDbType=NpgsqlDbType.Integer});
        return new(result,result["jobs"]!.AsArray().Count,false);
    }

    static async Task<ToolResult> JobHistory(NpgsqlConnection connection,JobHistoryArguments job,CancellationToken ct)
    {
        var result=await JobRead(connection,"SELECT automation.owned_job_history($1,$2)::text",ct,
            new NpgsqlParameter{Value=job.Job,NpgsqlDbType=NpgsqlDbType.Uuid},
            new NpgsqlParameter{Value=(object?)job.Before??DBNull.Value,NpgsqlDbType=NpgsqlDbType.Bigint});
        return new(result,1+new[]{"runs","audit","commands"}.Sum(name=>result[name]!.AsArray().Count),false);
    }

    static async Task<JsonObject> JobRead(NpgsqlConnection connection,string sql,CancellationToken ct,params NpgsqlParameter[] parameters)
    {
        await using var command=new NpgsqlCommand(sql,connection);
        command.Parameters.AddRange(parameters);
        return JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!)!.AsObject();
    }
}
