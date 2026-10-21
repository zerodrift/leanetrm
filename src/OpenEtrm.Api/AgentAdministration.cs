using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAgentRequest(Guid OperationId,string? Name);
// The limits a version declares (docs/ai-assistant.md §7.5), each named.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentLimits(int? ToolSteps,long? InputTokens,int? OutputTokens,int? ElapsedSeconds,int? Retries);
// A save names every field of the version it creates; nothing is inferred from an omission.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveAgentRequest(Guid OperationId,Guid AgentId,int? ExpectedVersion,string? Description,string? Instructions,
    Guid ModelId,int? ModelVersion,string? Reasoning,bool? Caching,string[]? Tools,string[]? AllowedRoles,AgentLimits? Limits,int? GrantDays);
// Publish and Retire name only the agent and the version the administrator saw.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentCommandRequest(Guid OperationId,Guid AgentId,int? ExpectedVersion);

// Agent administration (docs/ai-assistant.md §5.4, §9): the Agents directory, create, save, Publish and Retire. The
// database owns every rule; this boundary passes it the runtime set's bounds on the instructions' length and the
// grant's duration, which it applies after a repeat's replay. Create and Retire write control tables, so the
// database refuses them with admission_required until the caller repeats them under exclusive admission.
public static class AgentAdministration
{
    public static Task<JsonElement> Directory(NpgsqlConnection connection,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT assistant.agent_directory()::text",[],ct);

    public static Task<JsonElement> Create(NpgsqlConnection connection,CreateAgentRequest body,CancellationToken ct)=>
        body is {OperationId:var operation,Name:{} name}&&operation!=Guid.Empty
            ?ModelProviders.Read(connection,"SELECT assistant.create_agent($1,$2)::text",[operation,Text(name)],ct)
            :throw Input();

    public static Task<JsonElement> Save(NpgsqlConnection connection,SaveAgentRequest body,CancellationToken ct)
    {
        if(body is not {OperationId:var operation,AgentId:var agent,ExpectedVersion:{} expected,Description:{} description,Instructions:{} instructions,
                ModelId:var model,ModelVersion:{} modelVersion,Reasoning:{} reasoning,Caching:{} caching,Tools:{} tools,AllowedRoles:{} roles,
                Limits:{ToolSteps:{} steps,InputTokens:{} input,OutputTokens:{} output,ElapsedSeconds:{} elapsed,Retries:{} retries},GrantDays:{} days}
            ||operation==Guid.Empty||agent==Guid.Empty||model==Guid.Empty)
            throw Input();
        return ModelProviders.Read(connection,
            "SELECT assistant.save_agent_version($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19)::text",
            [operation,agent,expected,Text(description),Text(instructions),model,modelVersion,Text(reasoning),caching,Texts(tools),Texts(roles),
             steps,input,output,elapsed,retries,days,RuntimeSet.AgentInstructions,RuntimeSet.MaxGrantDays],ct);
    }

    public static Task<JsonElement> Publish(NpgsqlConnection connection,AgentCommandRequest body,CancellationToken ct)=>
        Command(connection,"SELECT assistant.publish_agent($1,$2,$3)::text",body,ct);

    public static Task<JsonElement> Retire(NpgsqlConnection connection,AgentCommandRequest body,CancellationToken ct)=>
        Command(connection,"SELECT assistant.retire_agent($1,$2,$3)::text",body,ct);

    // The role and permission the database names when an allowed role lacks one a version needs (§5.4).
    internal static string RoleRefusal(string? hint)
    {
        var lacking=hint is null?null:JsonSerializer.Deserialize<Dictionary<string,string>>(hint);
        return lacking is not null&&lacking.TryGetValue("role",out var role)&&lacking.TryGetValue("permission",out var permission)
            ?$"The allowed role {role} lacks {permission}, which this version needs. Allow only roles that use agents and hold every permission its tools need."
            :"An allowed role lacks a permission this version needs.";
    }

    static Task<JsonElement> Command(NpgsqlConnection connection,string sql,AgentCommandRequest body,CancellationToken ct)=>
        body is {OperationId:var operation,AgentId:var agent,ExpectedVersion:{} expected}&&operation!=Guid.Empty&&agent!=Guid.Empty
            ?ModelProviders.Read(connection,sql,[operation,agent,expected],ct)
            :throw Input();

    static string Text(string text)=>text.Contains('\0')?throw Input():text;
    static string[] Texts(string[] texts)=>texts.Any(t=>t is null||t.Contains('\0'))?throw Input():texts;
    static ModelAdministrationException Input()=>Refused("model_admin_input");
    static ModelAdministrationException Refused(string code)=>new(code,ModelCatalog.Refusal(code));
}
