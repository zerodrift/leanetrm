using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloseSessionDraft([property:JsonRequired] string Code,[property:JsonRequired] string Name,[property:JsonRequired] string CalendarCode,
    [property:JsonRequired] string Cutoff,[property:JsonRequired] ImmutableArray<string> Curves,
    [property:JsonRequired] DateOnly EffectiveFrom,[property:JsonRequired] DateOnly EffectiveTo);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloseSessionSave([property:JsonRequired] Guid RequestId,[property:JsonRequired] CloseSessionDraft Draft);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloseSessionPublish([property:JsonRequired] Guid RequestId,[property:JsonRequired] string Code,[property:JsonRequired] int Version,[property:JsonRequired] int Generation);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloseSessionGrant([property:JsonRequired] Guid RequestId,[property:JsonRequired] Guid PrincipalId,[property:JsonRequired] string Session,
    [property:JsonRequired] DateTimeOffset GrantedFrom,[property:JsonRequired] DateTimeOffset GrantedTo);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloseSessionChoice([property:JsonRequired] string Session);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloseSessionRevoke([property:JsonRequired] Guid RequestId,[property:JsonRequired] Guid GrantId);

// D334 close sessions: versioned configuration of a regional close (calendar, its clock, local cutoff and exact curves) and
// the per-session grants that let a person record that session's closes. The database decides every rule.
public static class CloseSessions
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public static async Task<object> Catalog(NpgsqlConnection connection,Guid catalog,CancellationToken ct)=>
        new{sessions=await ModelProviders.Read(connection,"SELECT security.close_session_catalog($1)::text",[catalog],ct)};

    public static Task<JsonElement> Save(NpgsqlConnection connection,PreviewDatabase database,CloseSessionSave request,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT security.save_close_session($1,$2,$3,$4::jsonb)::text",
            [database.Manifest.CatalogId,database.Manifest.RevisionId,request.RequestId,JsonSerializer.Serialize(request.Draft,Json)],ct);

    public static Task<JsonElement> Publish(NpgsqlConnection connection,Guid catalog,CloseSessionPublish request,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT security.publish_close_session($1,$2,$3,$4,$5)::text",
            [catalog,request.RequestId,request.Code,request.Version,request.Generation],ct);

    public static Task<JsonElement> Grant(NpgsqlConnection connection,Guid catalog,CloseSessionGrant request,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT security.grant_close_session($1,$2,$3,$4,$5,$6)::text",
            [catalog,request.RequestId,request.PrincipalId,request.Session,request.GrantedFrom,request.GrantedTo],ct);

    public static Task<JsonElement> Revoke(NpgsqlConnection connection,CloseSessionRevoke request,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT security.revoke_close_session_grant($1,$2)::text",[request.RequestId,request.GrantId],ct);

    // The person's remembered session, or null: nothing chooses one for them.
    public static async Task<object> Choice(NpgsqlConnection connection,Guid catalog,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.close_session_choice($1)",catalog);
        return new{session=await command.ExecuteScalarAsync(ct) as string};
    }
    public static async Task<object> Choose(NpgsqlConnection connection,Guid catalog,CloseSessionChoice request,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.choose_close_session($1,$2)",catalog,request.Session);
        return new{session=(string)(await command.ExecuteScalarAsync(ct))!};
    }
    public static async Task<object> Grants(NpgsqlConnection connection,CancellationToken ct)=>
        new{grants=await ModelProviders.Read(connection,"SELECT security.close_session_grants()::text",[],ct)};

    public static string Refusal(string code)=>code switch
    {
        "close_session"=>"Choose a published close session whose calendar, cutoff, curves and effective dates cover this business day; a new version must start after the versions it supersedes.",
        "close_session_instant"=>"The session's cutoff does not exist exactly once on this business day's local clock, or the snapshot is not at the session cutoff.",
        "close_incomplete"=>"The snapshot does not carry every curve of the session.",
        "close_outside_session"=>"The snapshot carries curves outside the session.",
        "close_replaced"=>"This close has been replaced. Use the close in force for its session and business day.",
        "close_not_visible"=>"The close in force for this session and business day is not visible to you; no older close is used in its place.",
        "close_exists"=>"This session and business day already have a close; automation does not replace it.",
        _=>"The close session request is incomplete or unsupported."
    };
}
