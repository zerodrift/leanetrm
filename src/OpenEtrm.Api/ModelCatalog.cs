using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddModelRequest(Guid OperationId,string? Vendor,string? Identifier,string? DisplayName);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RenameModelRequest(Guid OperationId,Guid ModelId,int? ExpectedVersion,string? DisplayName);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurrencyReference(Guid CatalogId,Guid RevisionId,string? Code);
// A member whose null has a meaning ("not offered", no tier, open-ended) must be present, so
// nothing is inferred from an omission.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TokenRates([property:JsonRequired] string? UncachedInput,[property:JsonRequired] string? CacheRead,
    [property:JsonRequired] string? CacheWrite,[property:JsonRequired] string? Output);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PriceTier(long? Threshold,TokenRates? Rates);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddPriceRequest(Guid OperationId,Guid ModelId,int? ExpectedSequence,CurrencyReference? Currency,TokenRates? Rates,
    [property:JsonRequired] PriceTier? Tier,string? EffectiveFrom,[property:JsonRequired] string? EffectiveTo);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WithdrawPriceRequest(Guid OperationId,Guid ModelId,int? ExpectedSequence,string? EffectiveFrom,[property:JsonRequired] string? EffectiveTo);

// Models & prices (docs/ai-assistant.md §5.1, §5.3, §9). The database owns every rule; this boundary
// admits only what it can transport exactly: required fields, a supported vendor, text without NUL,
// plain decimal rates within their bound and explicit UTC instants at microsecond precision.
// Currency choices and labels come from the reader's own scoped reference read (D124, D131).
public sealed partial class ModelCatalog
{
    // Row-level security limits this to sealed currencies the reader may currently see. Revisions can
    // repeat a currency, so each choice also names its catalog and revision.
    const string CurrencyChoices="SELECT coalesce(json_agg(json_build_object('catalogId',c.catalog_id,'revisionId',c.revision_id,'code',c.code,"
        +"'displayCode',c.display_code,'name',c.name,'catalogCode',g.code,'revisionKey',r.revision_key) ORDER BY c.display_code,g.code,r.revision_key,c.code),'[]')::text "
        +"FROM ref_data.currencies c JOIN ref_data.revisions r ON(r.catalog_id,r.id)=(c.catalog_id,c.revision_id) JOIN ref_data.catalogs g ON g.id=c.catalog_id "
        +"WHERE r.state='sealed'";

    public async Task<JsonObject> Directory(NpgsqlConnection connection,CancellationToken ct)
    {
        var directory=JsonNode.Parse((await ModelProviders.Read(connection,"SELECT assistant.model_directory()::text",[],ct)).GetRawText())!.AsObject();
        var currencies=JsonNode.Parse((await ModelProviders.Read(connection,CurrencyChoices,[],ct)).GetRawText())!.AsArray();
        var labels=currencies.ToDictionary(c=>Key(c!),c=>c!["displayCode"]!.GetValue<string>());
        // A withdrawal names no currency.
        foreach(var currency in directory["models"]!.AsArray().SelectMany(m=>m!["prices"]!.AsArray()).Select(p=>p!["currency"]).OfType<JsonObject>())
            currency["displayCode"]=labels.TryGetValue(Key(currency),out var label)?label:null;
        directory["currencies"]=currencies;
        return directory;
    }

    public Task<JsonElement> AddModel(NpgsqlConnection connection,AddModelRequest body,CancellationToken ct)=>
        body is {OperationId:var operation,Vendor:{} vendor,Identifier:{} identifier,DisplayName:{} name}&&operation!=Guid.Empty&&ModelProviders.Vendors.Any(v=>v.Code==vendor)
            ?ModelProviders.Read(connection,"SELECT assistant.add_model($1,$2::assistant.vendor,$3,$4)::text",
                [operation,vendor,Text(identifier,"model_identifier_invalid"),Text(name)],ct)
            :throw Input();

    public Task<JsonElement> RenameModel(NpgsqlConnection connection,RenameModelRequest body,CancellationToken ct)=>
        body is {OperationId:var operation,ModelId:var model,ExpectedVersion:{} expected,DisplayName:{} name}&&operation!=Guid.Empty&&model!=Guid.Empty
            ?ModelProviders.Read(connection,"SELECT assistant.rename_model($1,$2,$3,$4)::text",[operation,model,expected,Text(name)],ct)
            :throw Input();

    public Task<JsonElement> AddPrice(NpgsqlConnection connection,AddPriceRequest body,CancellationToken ct)
    {
        if(body is not {OperationId:var operation,ModelId:var model,ExpectedSequence:{} sequence,Currency:{CatalogId:var catalog,RevisionId:var revision,Code:{} code},
                Rates:{} rates,EffectiveFrom:{} from}||operation==Guid.Empty||model==Guid.Empty||catalog==Guid.Empty||revision==Guid.Empty
            ||body.Tier is {Threshold:null} or {Rates:null})
            throw Input();
        return ModelProviders.Read(connection,"SELECT assistant.add_price($1,$2,$3,$4,$5,$6,$7::numeric,$8::numeric,$9::numeric,$10::numeric,"
            +"$11::bigint,$12::numeric,$13::numeric,$14::numeric,$15::numeric,$16,$17::timestamptz)::text",
            [operation,model,sequence,catalog,revision,Text(code),..Rates(rates),body.Tier?.Threshold is {} threshold?threshold:DBNull.Value,..Rates(body.Tier?.Rates),
                Instant(from),End(body.EffectiveTo)],ct);
    }

    public Task<JsonElement> WithdrawPrice(NpgsqlConnection connection,WithdrawPriceRequest body,CancellationToken ct)=>
        body is {OperationId:var operation,ModelId:var model,ExpectedSequence:{} sequence,EffectiveFrom:{} from}&&operation!=Guid.Empty&&model!=Guid.Empty
            ?ModelProviders.Read(connection,"SELECT assistant.withdraw_price($1,$2,$3,$4,$5::timestamptz)::text",[operation,model,sequence,Instant(from),End(body.EffectiveTo)],ct)
            :throw Input();

    static (Guid,Guid,string) Key(JsonNode currency)=>(currency["catalogId"]!.GetValue<Guid>(),currency["revisionId"]!.GetValue<Guid>(),currency["code"]!.GetValue<string>());
    // PostgreSQL text cannot carry NUL, so it is malformed here rather than an unknown outcome there.
    static string Text(string text,string code="model_admin_input")=>text.Contains('\0')?throw Refused(code):text;
    // Null rates are "not offered"; each rate is plain decimal text, as ExactDecimal transports it.
    static object[] Rates(TokenRates? rates)=>[Rate(rates?.UncachedInput),Rate(rates?.CacheRead),Rate(rates?.CacheWrite),Rate(rates?.Output)];
    static object Rate(string? text)=>text is null?DBNull.Value:RateFormat().IsMatch(text)?text:throw Input();
    // Below 10^12 per million tokens with at most 12 fractional digits (§5.3), so no unbounded number reaches the database.
    [GeneratedRegex(@"^(0|[1-9][0-9]{0,11})(\.[0-9]{1,12})?\z")] private static partial Regex RateFormat();
    // An explicit UTC instant at the database's microsecond precision: a finer one is refused, never truncated.
    static DateTimeOffset Instant(string text)
    {
        try{var at=SchedulePreview.ParseAt(text);return at.Ticks%TimeSpan.TicksPerMicrosecond==0?at:throw Input();}
        catch(MarketDataException){throw Input();}
    }
    // A null end is open-ended.
    static object End(string? text)=>text is null?DBNull.Value:Instant(text);
    static ModelAdministrationException Input()=>Refused("model_admin_input");
    static ModelAdministrationException Refused(string code)=>new(code,Refusal(code));
    // The documented message for each refusal, whether this boundary or the database refuses.
    internal static string Refusal(string code)=>code switch
    {
        "model_identifier_invalid"=>"Enter the vendor's model identifier: up to 128 letters, digits, '.', '_' or '-', starting with a letter or digit.",
        "currency_unavailable"=>"Choose a currency of a sealed reference revision that your role can currently read.",
        _=>"Supply every required field in its documented form, with the version you are changing."
    };
}
