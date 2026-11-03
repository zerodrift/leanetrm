using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveOperatingPolicyRequest([property:JsonRequired] Guid OperationId, [property:JsonRequired] string Kind,
    [property:JsonRequired] int ExpectedVersion, [property:JsonRequired] JsonElement Values);

public static class OperatingPolicies
{
    public static string PermissionFor(string kind) => kind switch
    {
        "assistant" => "ManageAgents",
        "access" => "ManageAccess",
        _ => throw new MarketDataException("operating_policy_input", "Choose assistant or access policy.")
    };
    public static Admission AdmissionFor(string kind) => kind switch
    {
        "assistant" => Admission.OperatingAgents,
        "access" => Admission.OperatingAccess,
        _ => throw new MarketDataException("operating_policy_input", "Choose assistant or access policy.")
    };

    public static Task<JsonElement> Directory(NpgsqlConnection connection, CancellationToken ct) =>
        ModelProviders.Read(connection, "SELECT security.operating_policy_directory()::text", [], ct);

    public static Task<JsonElement> Save(NpgsqlConnection connection, SaveOperatingPolicyRequest request, CancellationToken ct) =>
        ModelProviders.Read(connection, "SELECT security.save_operating_policy($1,$2,$3,$4::jsonb)::text",
            [request.Kind, request.OperationId, request.ExpectedVersion, request.Values.GetRawText()], ct);
}
