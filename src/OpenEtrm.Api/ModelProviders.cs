using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplaceKeyRequest(Guid OperationId,string? Vendor,int? ExpectedVersion,string? Key)
{
    // The key never reaches a log or an exception message through the record's generated text.
    public override string ToString()=>$"ReplaceKeyRequest {{ OperationId = {OperationId}, Vendor = {Vendor}, ExpectedVersion = {ExpectedVersion} }}";
}

// A vendor the Providers tab lists, with the protocol its adapter speaks (docs/ai-assistant.md §6.1).
public sealed record ModelVendor(string Code,string Name,string Protocol);

// Model-provider administration (docs/ai-assistant.md §5, §9): the Providers directory and Replace
// key. A key is checked, digested and sealed here, so only its sealed form, last four characters and
// keyed digest reach the database. No adapter ships yet, so every provider is unqualified.
public sealed partial class ModelProviders(CredentialCustody custody,PreviewManifest manifest)
{
    public static readonly ImmutableArray<ModelVendor> Vendors=[new("anthropic","Anthropic","Messages API"),new("openai","OpenAI","Responses API"),
        new("google","Google","generateContent"),new("xai","xAI","Responses API"),new("meta","Meta","Responses API")];

    public async Task<object> Directory(NpgsqlConnection connection,CancellationToken ct)
    {
        var directory=await Read(connection,"SELECT assistant.provider_directory()::text",[],ct);
        var states=directory.GetProperty("providers").EnumerateArray().ToDictionary(p=>p.GetProperty("vendor").GetString()!);
        return new{providers=Vendors.Select(v=>states.TryGetValue(v.Code,out var state)
                ?new{vendor=v.Code,v.Name,v.Protocol,qualified=false,version=state.GetProperty("version").GetInt32(),credential=(JsonElement?)state.GetProperty("credential")}
                :new{vendor=v.Code,v.Name,v.Protocol,qualified=false,version=0,credential=(JsonElement?)null}),
            audit=directory.GetProperty("audit")};
    }

    public async Task<JsonElement> ReplaceKey(NpgsqlConnection connection,ReplaceKeyRequest body,CancellationToken ct)
    {
        if(body is not {OperationId:var operation,Vendor:{} vendor,ExpectedVersion:>=0 and var expected,Key:{} key}||operation==Guid.Empty
            ||Vendors.All(v=>v.Code!=vendor)||!KeyFormat().IsMatch(key))
            throw new ModelAdministrationException("model_admin_input","Choose a supported provider and enter its complete key with the version you are replacing.");
        var digest=custody.Digest(manifest.TenantId,manifest.WorkspaceId,operation,vendor,key);
        var slot=await Read(connection,"SELECT assistant.credential_slot($1,$2::assistant.vendor,$3,$4)::text",[operation,vendor,expected,digest],ct);
        if(slot.TryGetProperty("replayed",out var replayed))return replayed;
        var (provider,version)=(slot.GetProperty("providerId").GetGuid(),slot.GetProperty("credentialVersion").GetInt32());
        var sealedKey=custody.Seal(provider,vendor,version,key);
        return await Read(connection,"SELECT assistant.replace_credential($1,$2::assistant.vendor,$3,$4,$5,$6,$7,$8,$9,$10)::text",
            [operation,vendor,expected,provider,version,sealedKey.Nonce,sealedKey.Ciphertext,sealedKey.Tag,key[^4..],digest],ct);
    }

    // A vendor key is 8 to 512 visible ASCII characters; \z, unlike $, admits no final newline.
    [GeneratedRegex(@"^[!-~]{8,512}\z")] internal static partial Regex KeyFormat();

    internal static async Task<JsonElement> Read(NpgsqlConnection connection,string sql,object[] values,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,sql,values);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
}
