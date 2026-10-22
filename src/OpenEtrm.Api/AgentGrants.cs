using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace OpenEtrm.Api;

// A grant names only the agent version it is for; the database derives everything else from the stored version.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GrantRequest(Guid OperationId,Guid AgentId,int? Version);
// Revoke names only the grant.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RevokeRequest(Guid OperationId,Guid GrantId);

// Grants (docs/ai-assistant.md §7.1, §8): the agents a user may use, the user's grants, grant and Revoke, and a
// workspace's grants for those who administer its access. The database owns every rule; this boundary passes it the
// runtime set's bound on a grant's duration. Grant and Revoke write control tables, so the database refuses them with
// admission_required once every check has passed under the shared admission.
public static class AgentGrants
{
    public static Task<JsonElement> Usable(NpgsqlConnection connection,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT assistant.usable_agents()::text",[],ct);

    public static async Task<JsonElement> Grants(NpgsqlConnection connection,CancellationToken ct)=>
        WithVendors(await ModelProviders.Read(connection,"SELECT assistant.grants()::text",[],ct));

    // Every grant in force in the workspace and its latest grant audit records; read-only.
    public static async Task<JsonElement> Workspace(NpgsqlConnection connection,CancellationToken ct)=>
        WithVendors(await ModelProviders.Read(connection,"SELECT assistant.workspace_grants()::text",[],ct));

    // Grants name each model's vendor by code; the page looks its name up here.
    static JsonElement WithVendors(JsonElement read)
    {
        var named=read.Deserialize<Dictionary<string,JsonElement>>()!;
        named["vendors"]=ModelProviders.VendorNames;
        return JsonSerializer.SerializeToElement(named);
    }

    public static Task<JsonElement> Grant(NpgsqlConnection connection,GrantRequest body,CancellationToken ct)=>
        body is {OperationId:var operation,AgentId:var agent,Version:{} version}&&operation!=Guid.Empty&&agent!=Guid.Empty
            ?ModelProviders.Read(connection,"SELECT assistant.grant_agent($1,$2,$3,$4)::text",[operation,agent,version,RuntimeSet.MaxGrantDays],ct)
            :throw Input();

    public static Task<JsonElement> Revoke(NpgsqlConnection connection,RevokeRequest body,CancellationToken ct)=>
        body is {OperationId:var operation,GrantId:var grant}&&operation!=Guid.Empty&&grant!=Guid.Empty
            ?ModelProviders.Read(connection,"SELECT assistant.revoke_grant($1,$2)::text",[operation,grant],ct)
            :throw Input();

    internal static string Refusal(string code,string? hint=null)=>code switch
    {
        "grant_input"=>"Name a request identity and either the agent version to grant or the grant to revoke.",
        "agent_not_allowed"=>"This agent version is not one you may use: its published version needs one of its allowed roles, and a draft needs agent management.",
        "grant_permission_missing"=>hint is null?"You lack a permission this agent version's tools need.":$"You lack {hint}, which this agent version's tools need.",
        "grant_unknown"=>"You have no such grant.",
        "grant_conflict"=>"This request identity was used for another grant or revocation. Send the change under a new request identity.",
        _=>"Supply every required field in its documented form."
    };

    static ModelAdministrationException Input()=>new("grant_input",Refusal("grant_input"));
}
