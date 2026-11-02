using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// This is an allowlist of the application's ordinary operations, not a URL, SQL or code execution tool.
// A read and a proposed command use the same handler and request type as their page. New endpoints are closed
// to the assistant until their effect, permissions and temporal contract are explicitly classified here.
internal sealed record AssistantOperation(string Id,string Page,string Method,string Path,bool RequiresApproval,
    ImmutableArray<string> Permissions,bool CurrentOnly);

internal static class AssistantOperations
{
    public const string Permission="UseAssistantWorkspace";
    public static IReadOnlyList<AssistantOperation> Catalog {get;}=Build();
    static IReadOnlyList<AssistantOperation> Build()
    {
        var result=new List<AssistantOperation>();
        void Add(string page,string permissions,bool current,string reads,string commands="")
        {
            foreach(var (paths,write) in new[]{(reads,false),(commands,true)})
                foreach(var entry in paths.Split(' ',StringSplitOptions.RemoveEmptyEntries))
                {
                    var get=entry.StartsWith('!');var path=get?entry[1..]:entry;
                    var id=path.Replace("/{id:guid}",".id",StringComparison.Ordinal).Replace('/','.')+"_v1";
                    if(result.Any(o=>o.Id==id))id=id[..^3]+"_command_v1";
                    result.Add(new(id,page,get?"GET":"POST",path,write,[..permissions.Split('|',StringSplitOptions.RemoveEmptyEntries)],current));
                }
        }
        Add("curves","ReadMarketData",false,"!workspace/navigation !workspace/catalog !curve-names evaluate history market/dates market/curve-shapes !analytics/overlays");
        Add("curves","ImportObservations",true,"","corrections");
        Add("curves","ReadMarketData",true,"","market/closes/register");
        Add("custom_curves","ReadMarketData",false,"!custom-curves !custom-curves/activation custom-curves/preview custom-curves/compile custom-curves/format custom-curves/compose custom-curves/effective-preview");
        Add("custom_curves","WriteMarketDefinitions",true,"","custom-curves/save");
        Add("custom_curves","SealMarketDefinitions",true,"","custom-curves/publish custom-curves/activation");
        Add("reference_data","ReadMarketData",false,"!reference/catalog !reference/editor units/convert");
        Add("reference_data","WriteReferenceData",true,"","reference/save");
        Add("reference_data","SealReferenceData",true,"","reference/apply");
        Add("parties","ReadTradeParties|ReadMarketData",false,"parties/catalog");
        Add("parties","WriteTradeParties|ReadMarketData",true,"","parties/save");
        Add("schedules","ReadMarketData",false,"!schedules !date-schedules date-schedules/preview date-schedules/resolve date-schedules/catalog date-schedules/contracts/preview date-schedules/contracts/resolve schedules/observation-contracts schedules/preview schedules/dates/preview schedules/expression schedules/contracts/preview");
        Add("schedules","WriteMarketDefinitions",true,"","date-schedules/save date-schedules/contracts/save");
        Add("schedules","SealMarketDefinitions",true,"","date-schedules/publish date-schedules/contracts/publish");
        Add("power_tous","ReadMarketData",true,"!tou-products power/catalog power/preview power/settlement");
        Add("power_tous","WriteMarketDefinitions",true,"","tou-products/save");
        Add("power_tous","SealMarketDefinitions",true,"","tou-products/publish");
        Add("delivery_prices","ReadMarketData",true,"!power-imports power-imports/period power-imports/history");
        Add("interval_series","ReadMarketData",true,"!interval-series interval-series/latest interval-series/values interval-series/changes");
        Add("interval_series","WriteMarketDefinitions",true,"","interval-series/define");
        Add("interval_series","ImportObservations",true,"","interval-series/revise");
        Add("interval_curves","ReadMarketData",true,"interval-curves/catalog interval-curves/values period-curves/values ppa/quantities ppa/capacity ppa/profile");
        Add("blotter","ReadTrades|ReadMarketData",false,"trading/directory trading/workspace trading/blotter trading/positions trading/position futures/catalog futures/editor futures/blotter futures/holdings swaps/catalog swaps/editor swaps/quantity-schedule swaps/scheduled-periods swaps/contract-observation-preview swaps/observation-options trade-formats/read");
        Add("blotter_new_future","ReadTrades|ReadMarketData",false,"futures/price");
        Add("blotter_new_swap","ReadTrades|ReadMarketData",false,"swaps/price ");
        Add("blotter_new_future","RunDealRisk|ReadTrades|ReadMarketData",false,"futures/draft-risk");
        Add("blotter_new_swap","RunDealRisk|ReadTrades|ReadMarketData",false,"swaps/draft-risk");
        // Legacy amendment/scheduled-write previews do not forward an exact context id. They stay closed until
        // their ordinary handlers can preserve the question's pin; typed draft pricing above remains available.
        Add("blotter","BookTrade|ReadTrades|ReadMarketData",true,"","futures/book swaps/book");
        Add("blotter","AmendTrade|ReadTrades|ReadMarketData",true,"","futures/amend swaps/amend trade-formats/save");
        Add("blotter","ReadTrades|ReadMarketData",false,"futures/status swaps/status swaps/settlement/preview swaps/settlement/inspect swaps/settlement/pnl swaps/settlement/net-preview swaps/settlement/status");
        Add("blotter","AmendTrade|ReadTrades|ReadMarketData",true,"","swaps/settlement/determine swaps/settlement/net swaps/settlement/payment");
        Add("blotter_risk","RunScenario|ReadTrades|ReadMarketData",false,"portfolio/risk/catalog portfolio/risk");
        Add("risk_jobs","RunScenario|ReadTrades|ReadMarketData",true,"!portfolio/risk/jobs !portfolio/risk/jobs/{id:guid}","portfolio/risk/jobs portfolio/risk/jobs/{id:guid}/cancel");
        Add("ppa","ReadTrades|ReadMarketData",true,"!ppa/frames ppa/catalog ppa/directory ppa/editor ppa/legs ppa/validate ppa/preview ppa/inspect ppa/break-even ppa/status settlement/payment-lineages/plan settlement/ppa/preview");
        Add("ppa","BookTrade|ReadTrades|ReadMarketData",true,"","ppa/book");
        Add("ppa","AmendTrade|ReadTrades|ReadMarketData",true,"","ppa/amend ppa/terminate");
        Add("jobs","ReadJobs",true,"!jobs !jobs/pilot-close/catalog !jobs/pilot-close/definition !jobs/pilot-close/results");
        Add("jobs","RunJobs",true,"","jobs/create jobs/close-book-risk/create jobs/pilot-close/create");
        // Own-job control and infrastructure operations also enforce ownership, exact versions and their distinct
        // capabilities inside the ordinary handlers; proposing one never confers any of those rights.
        Add("jobs","ReadJobs",true,"","jobs/control jobs/update jobs/collection/update infrastructure/availability infrastructure/resolve");
        Add("users_access","ManageAccess|ManageIdentities",true,"!admin","admin/create admin/update");
        Add("settings_ai","ManageModelProviders",true,"!settings/ai/providers !settings/ai/models","settings/ai/providers/terms settings/ai/providers/attest settings/ai/providers/enable settings/ai/providers/disable settings/ai/providers/test settings/ai/models/add settings/ai/models/rename settings/ai/models/price settings/ai/models/withdraw settings/ai/models/edit settings/ai/models/enable settings/ai/models/disable");
        Add("settings_ai","ManageAgents",true,"!settings/ai/agents","settings/ai/agents/create settings/ai/agents/save settings/ai/agents/publish settings/ai/agents/retire");
        Add("settings_ai","ReadModelUsage",true,"!settings/ai/usage settings/ai/usage/report","settings/ai/usage/export");
        Add("settings_ai","ManageAssistantPrompts",true,"!settings/ai/page-prompts","settings/ai/page-prompts/save");
        return result.AsReadOnly();
    }
    public static AssistantOperation? Find(string id)=>Catalog.SingleOrDefault(o=>o.Id==id);
    // These ordinary single-transaction commands predate request identities. Approval supplies their missing
    // atomic outcome receipt; every other command keeps its existing request-id protocol.
    public static bool NeedsApprovalReceipt(AssistantOperation operation)=>operation.Path is
        "date-schedules/save" or "date-schedules/publish" or "tou-products/save" or "tou-products/publish" or "trade-formats/save" or "portfolio/risk/jobs/{id:guid}/cancel" or "corrections";
    public static AssistantOperation RequireRead(string id)
    {
        var operation=Find(id)??throw new ToolRefusal("tool_arguments_invalid");
        return !operation.RequiresApproval?operation:throw new ToolRefusal("approval_required");
    }

    // Schemas describe the actual handler signature. They do not validate or authorize a business operation;
    // the original handler still binds the request and runs all of its ordinary managed/database checks.
    public static JsonObject Schema(MethodInfo handler)
    {
        var query=new JsonObject();Type? body=null;
        foreach(var parameter in handler.GetParameters())
        {
            var type=parameter.ParameterType;
            if(type==typeof(HttpContext)||type==typeof(CancellationToken)||parameter.GetCustomAttribute<FromServicesAttribute>() is not null)continue;
            if(Simple(type))query[parameter.Name!]=Describe(type,[]);
            else if(!type.IsInterface)body=type;
        }
        return new(){["body"]=body is null?new JsonObject{["type"]="null"}:Describe(body,[]),["parameters"]=query};
    }
    static bool Simple(Type type)=>type.IsPrimitive||type==typeof(string)||type==typeof(Guid)||type==typeof(DateTimeOffset)||type==typeof(DateOnly)||Nullable.GetUnderlyingType(type) is {} inner&&Simple(inner);
    static JsonNode Describe(Type type,ImmutableHashSet<Type> path)
    {
        if(Nullable.GetUnderlyingType(type) is {} nullable)return new JsonObject{["nullable"]=true,["value"]=Describe(nullable,path)};
        if(type==typeof(string)||type==typeof(Guid)||type==typeof(DateOnly)||type==typeof(DateTimeOffset))
            return new JsonObject{["type"]="string",["format"]=type==typeof(Guid)?"uuid":type==typeof(DateOnly)?"date":type==typeof(DateTimeOffset)?"date-time":null};
        if(type==typeof(bool))return new JsonObject{["type"]="boolean"};
        if(type==typeof(ExactDecimal))return new JsonObject{["type"]="string",["format"]="exact-decimal"};
        if(type.IsPrimitive||type==typeof(decimal))return new JsonObject{["type"]="number"};
        if(type.IsEnum)return new JsonObject{["values"]=JsonSerializer.SerializeToNode(Enum.GetNames(type))};
        if(type==typeof(JsonElement)||type==typeof(JsonNode)||path.Contains(type))return new JsonObject{["type"]="object",["contract"]=type.Name};
        if(type.IsArray)return new JsonObject{["type"]="array",["items"]=Describe(type.GetElementType()!,path)};
        var dictionary=type.GetInterfaces().Append(type).FirstOrDefault(t=>t.IsGenericType&&
            (t.GetGenericTypeDefinition()==typeof(IDictionary<,>)||t.GetGenericTypeDefinition()==typeof(IReadOnlyDictionary<,>)));
        if(dictionary is not null)return new JsonObject{["type"]="object",["additionalProperties"]=Describe(dictionary.GetGenericArguments()[1],path.Add(type))};
        if(type.IsGenericType&&type.GetInterfaces().Any(i=>i.IsGenericType&&i.GetGenericTypeDefinition()==typeof(IEnumerable<>)))
            return new JsonObject{["type"]="array",["items"]=Describe(type.GetGenericArguments()[0],path)};
        var properties=new JsonObject();
        foreach(var property in type.GetProperties().Where(p=>p.GetMethod is not null&&p.GetCustomAttribute<JsonIgnoreAttribute>() is not {Condition:JsonIgnoreCondition.Always}))
            properties[JsonNamingPolicy.CamelCase.ConvertName(property.Name)]=Describe(property.PropertyType,path.Add(type));
        return new JsonObject{["type"]="object",["properties"]=properties,["contract"]=type.Name};
    }
}
