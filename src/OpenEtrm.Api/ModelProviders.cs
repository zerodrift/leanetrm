using System.Collections.Immutable;
using System.Globalization;
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
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecordTermsRequest(Guid OperationId,string? Vendor,int? ExpectedVersion,string? AccountType,
    string? Retention,string? RetentionSource,string? RetentionCheckedOn,string? Training,string? TrainingSource,string? TrainingCheckedOn,
    string? OwnerAccess,string? OwnerAccessSource,string? OwnerAccessCheckedOn);
// Attest, Enable and Disable name only the provider and the version the administrator saw.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProviderRequest(Guid OperationId,string? Vendor,int? ExpectedVersion);

// A vendor the Providers tab lists, with the protocol its adapter speaks (docs/ai-assistant.md §6.1) and, when
// that adapter ships in this release, its fixed endpoint (§5.2).
public sealed record ModelVendor(string Code,string Name,string Protocol,Uri? Endpoint=null);

// Model-provider administration (docs/ai-assistant.md §5, §6.3, §9): the Providers directory, Replace
// key, Record terms, Attest, Enable and Disable. A key is checked, digested and sealed here, so only its
// sealed form, last four characters and keyed digest reach the database. The database decides
// qualification and every terms field's form. Under the check endpoint, the directory shows where
// requests actually go, marked as a check endpoint.
public sealed partial class ModelProviders(CredentialCustody custody,PreviewManifest manifest,Uri? checkEndpoint)
{
    public static readonly ImmutableArray<ModelVendor> Vendors=[new("anthropic","Anthropic","Messages API",MessagesAdapter.Endpoint),
        new("openai","OpenAI","Responses API"),new("google","Google","generateContent"),new("xai","xAI","Responses API"),new("meta","Meta","Responses API")];
    // Each vendor's code and name, for the pages that name a model's provider.
    public static readonly JsonElement VendorNames=JsonSerializer.SerializeToElement(Vendors.Select(v=>new{code=v.Code,name=v.Name}));
    // A vendor without a provider row has had nothing administered for it.
    static readonly JsonElement Unadministered=JsonSerializer.Deserialize<JsonElement>(
        """{"version":0,"enabled":false,"qualified":false,"credential":null,"terms":null,"attestation":null}""");

    public async Task<object> Directory(NpgsqlConnection connection,CancellationToken ct)
    {
        var directory=await Read(connection,"SELECT assistant.provider_directory()::text",[],ct);
        var states=directory.GetProperty("providers").EnumerateArray().ToDictionary(p=>p.GetProperty("vendor").GetString()!);
        // The confirmed usage mapping the vendor's adapter applies, or none; its latest test, or none.
        var (mappings,tests)=(directory.GetProperty("mappings"),directory.GetProperty("tests"));
        object Provider(ModelVendor v,JsonElement state)=>new{vendor=v.Code,v.Name,v.Protocol,
            endpoint=v.Endpoint is {} fixedEndpoint?new{url=(checkEndpoint is null?fixedEndpoint:new Uri(checkEndpoint,fixedEndpoint.PathAndQuery)).ToString(),check=checkEndpoint is not null}:null,
            mapping=mappings.TryGetProperty(v.Code,out var applied)?applied:(JsonElement?)null,version=state.GetProperty("version").GetInt32(),
            enabled=state.GetProperty("enabled").GetBoolean(),qualified=state.GetProperty("qualified").GetBoolean(),credential=state.GetProperty("credential"),
            terms=state.GetProperty("terms"),attestation=state.GetProperty("attestation"),lastTest=tests.TryGetProperty(v.Code,out var last)?last:(JsonElement?)null};
        return new{providers=Vendors.Select(v=>Provider(v,states.GetValueOrDefault(v.Code,Unadministered))),audit=directory.GetProperty("audit")};
    }

    public async Task<JsonElement> ReplaceKey(NpgsqlConnection connection,ReplaceKeyRequest body,CancellationToken ct)
    {
        if(body is not {OperationId:var operation,Vendor:{} vendor,ExpectedVersion:>=0 and var expected,Key:{} key}||operation==Guid.Empty
            ||!Supported(vendor)||!KeyFormat().IsMatch(key))
            throw new ModelAdministrationException("model_admin_input","Choose a supported provider and enter its complete key with the version you are replacing.");
        var digest=custody.Digest(manifest.TenantId,manifest.WorkspaceId,operation,vendor,key);
        var slot=await Read(connection,"SELECT assistant.credential_slot($1,$2::assistant.vendor,$3,$4)::text",[operation,vendor,expected,digest],ct);
        if(slot.TryGetProperty("replayed",out var replayed))return replayed;
        var (provider,version)=(slot.GetProperty("providerId").GetGuid(),slot.GetProperty("credentialVersion").GetInt32());
        var sealedKey=custody.Seal(provider,vendor,version,key);
        return await Read(connection,"SELECT assistant.replace_credential($1,$2::assistant.vendor,$3,$4,$5,$6,$7,$8,$9,$10)::text",
            [operation,vendor,expected,provider,version,sealedKey.Nonce,sealedKey.Ciphertext,sealedKey.Tag,key[^4..],digest],ct);
    }

    // Terms are what an administrator copies from the vendor's published documents, each with its source
    // and the date it was checked. This boundary admits only what it transports exactly: text without
    // NUL, which PostgreSQL text cannot carry, and calendar dates written yyyy-MM-dd.
    public Task<JsonElement> RecordTerms(NpgsqlConnection connection,RecordTermsRequest body,CancellationToken ct)
    {
        if(body is not {OperationId:var operation,Vendor:{} vendor,ExpectedVersion:{} expected,AccountType:{} account,
                Retention:{} retention,RetentionSource:{} retentionSource,RetentionCheckedOn:{} retentionOn,
                Training:{} training,TrainingSource:{} trainingSource,TrainingCheckedOn:{} trainingOn,
                OwnerAccess:{} owner,OwnerAccessSource:{} ownerSource,OwnerAccessCheckedOn:{} ownerOn}
            ||operation==Guid.Empty||!Supported(vendor)||new[]{account,retention,retentionSource,training,trainingSource,owner,ownerSource}.Any(t=>t.Contains('\0')))
            throw ModelCatalog.Input();
        return Read(connection,"SELECT assistant.record_terms($1,$2::assistant.vendor,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)::text",
            [operation,vendor,expected,account,retention,retentionSource,Date(retentionOn),training,trainingSource,Date(trainingOn),owner,ownerSource,Date(ownerOn)],ct);
    }

    public Task<JsonElement> Attest(NpgsqlConnection connection,ProviderRequest body,CancellationToken ct)=>
        Command(connection,"SELECT assistant.attest($1,$2::assistant.vendor,$3)::text",body,ct);
    public Task<JsonElement> Enable(NpgsqlConnection connection,ProviderRequest body,CancellationToken ct)=>
        Command(connection,"SELECT assistant.enable_provider($1,$2::assistant.vendor,$3)::text",body,ct);
    public Task<JsonElement> Disable(NpgsqlConnection connection,ProviderRequest body,CancellationToken ct)=>
        Command(connection,"SELECT assistant.disable_provider($1,$2::assistant.vendor,$3)::text",body,ct);

    static Task<JsonElement> Command(NpgsqlConnection connection,string sql,ProviderRequest body,CancellationToken ct)=>
        body is {OperationId:var operation,Vendor:{} vendor,ExpectedVersion:{} expected}&&operation!=Guid.Empty&&Supported(vendor)
            ?Read(connection,sql,[operation,vendor,expected],ct)
            :throw ModelCatalog.Input();

    internal static bool Supported(string vendor)=>Vendors.Any(v=>v.Code==vendor);
    internal static DateOnly Date(string text)=>DateOnly.TryParseExact(text,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)?date:throw ModelCatalog.Input();

    // A vendor key is 8 to 512 visible ASCII characters; \z, unlike $, admits no final newline.
    [GeneratedRegex(@"^[!-~]{8,512}\z")] internal static partial Regex KeyFormat();

    internal static async Task<JsonElement> Read(NpgsqlConnection connection,string sql,object[] values,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,sql,values);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
}
