using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveUserPreferencesRequest([property:JsonRequired] Guid OperationId,
    [property:JsonRequired] int ExpectedVersion, [property:JsonRequired] JsonElement Preferences, [property:JsonRequired] string OwnerId);

public static class UserPreferences
{
    public static Task<JsonElement> Read(NpgsqlConnection connection, string scope, CancellationToken ct) =>
        ModelProviders.Read(connection, "SELECT presentation.user_preferences($1)::text", [scope], ct);

    public static Task<JsonElement> Save(NpgsqlConnection connection, string scope, SaveUserPreferencesRequest request, CancellationToken ct)
    {
        if(request.OwnerId is null||request.OwnerId.Length!=64||!request.OwnerId.All(char.IsAsciiHexDigitLower))
            throw new MarketDataException("user_preferences_owner_changed","Reload your profile before saving preferences.");
        return ModelProviders.Read(connection, "SELECT presentation.save_user_preferences($1,$2,$3,$4::jsonb,$5)::text",
            [scope, request.OperationId, request.ExpectedVersion, request.Preferences.GetRawText(),request.OwnerId], ct);
    }
}
