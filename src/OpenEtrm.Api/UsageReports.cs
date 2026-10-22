using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;

namespace OpenEtrm.Api;

// A report names its zone, its half-open range of local dates and what it is grouped by; nothing is inferred.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UsageReportRequest(string? Zone,string? From,string? To,string? By);
// An export names the same under a request identity.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UsageExportRequest(Guid OperationId,string? Zone,string? From,string? To,string? By);

// Usage (docs/ai-assistant.md §5.3, §5.4, §9): the tab's directory, a report and its audited export. The database owns
// every rule: the zone, the range and its days, the grouping, sums, ratios and shown amounts. This boundary admits only
// what it transports exactly, local dates written yyyy-MM-dd and text without NUL, and names each currency by the
// reader's own scoped read (D124) and each vendor, as Models & prices and Agents do.
public static class UsageReports
{
    public static Task<JsonElement> Directory(NpgsqlConnection connection,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT assistant.usage_directory()::text",[],ct);

    public static Task<JsonObject> Report(NpgsqlConnection connection,UsageReportRequest body,CancellationToken ct)=>
        Named(connection,"SELECT assistant.usage_report($1,$2,$3,$4)::text",[Zone(body.Zone),Date(body.From),Date(body.To),By(body.By)],ct);

    public static Task<JsonObject> Export(NpgsqlConnection connection,UsageExportRequest body,CancellationToken ct)=>
        body.OperationId!=Guid.Empty
            ?Named(connection,"SELECT assistant.export_usage($1,$2,$3,$4,$5)::text",[body.OperationId,Zone(body.Zone),Date(body.From),Date(body.To),By(body.By)],ct)
            :throw ModelCatalog.Input();

    // A currency the reader cannot read has no label.
    static async Task<JsonObject> Named(NpgsqlConnection connection,string sql,object[] values,CancellationToken ct)
    {
        var report=JsonNode.Parse((await ModelProviders.Read(connection,sql,values,ct)).GetRawText())!.AsObject();
        var readable=(await ModelCatalog.Currencies(connection,ct)).ToDictionary(c=>ModelCatalog.Key(c!));
        foreach(var section in report["currencies"]!.AsArray().Select(s=>s!.AsObject()))
            section["label"]=readable.TryGetValue(ModelCatalog.Key(section["currency"]!),out var label)?label!.DeepClone():null;
        report["vendors"]=JsonSerializer.SerializeToNode(ModelProviders.Vendors.Select(v=>new{code=v.Code,name=v.Name}));
        return report;
    }

    // PostgreSQL text cannot carry NUL, so such a zone is one the database cannot name.
    static string Zone(string? zone)=>zone is null||zone.Contains('\0')?throw Refused("report_zone_invalid"):zone;
    static DateOnly Date(string? text)=>
        DateOnly.TryParseExact(text,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)?date:throw Refused("report_range_invalid");
    static string By(string? by)=>by is null||by.Contains('\0')?throw ModelCatalog.Input():by;
    static ModelAdministrationException Refused(string code)=>new(code,Refusal(code));
    internal static string Refusal(string code)=>code switch
    {
        "report_zone_invalid"=>"Choose a time zone the preview's database knows, named exactly as it names it.",
        "report_range_invalid"=>"Enter a From date before the To date, each written yyyy-MM-dd, no further apart than the Usage tab states.",
        _=>ModelCatalog.Refusal(code)
    };
}
