using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Physical;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Commands;

namespace OpenEtrm.Api;

// Canonicalization supplies technical identities and notation only. Every
// physical quantity, interval, policy and source selection remains explicit.
internal static class StorageFacilityCommands
{
    internal static JsonElement Normalize(Guid operationId,JsonElement command)
    {
        if(operationId==Guid.Empty)throw Invalid();
        var kind=Text(command,"kind");
        string[] fields=kind switch{
            "define"=>["referenceCatalogId","referenceRevisionId","facilityCode","commodity","quantity","clock","minimum","maximum","implementation","inventoryBasis","rateBasis","simultaneousFlow","lossTreatment","ports","ratchets"],
            "opening"=>["businessKey","observedAt","quantity"],
            "activity"=>["businessKey","versionId","portId","from","through","direction","quantity","sourceRevisionId"],
            "coverage"=>["businessKey","versionId","openingRevisionId","from","through","statement","activityRevisionIds"],
            "plan"=>["versionId","openingRevisionId","from","through","coverageStatement","activities"],_=>throw Invalid()};
        Keys(command,["kind","facilityId","expectedStateVersion","expectedPreviousId","evidence",..fields],kind=="plan"?["planId"]:[]);
        var facility=Id(command,"facilityId",kind=="define");
        var head=Integer(command,"expectedStateVersion");if(head<0)throw Invalid();
        if(kind=="define"&&head==0)
        {
            // A new request identity must not create another asset identity for
            // the same exact catalog/location business key (D238).
            var identity=TechnicalId("storage_facility_identity_v1:"+Code(command,"facilityCode"),Id(command,"referenceCatalogId",false)!.Value);
            if(Id(command,"expectedPreviousId",true) is not null||facility is {} supplied&&supplied!=identity)throw Invalid();
            facility=identity;
        }
        if(facility is null)throw Invalid();
        var body=new JsonObject{["kind"]=kind,["facilityId"]=facility,["expectedStateVersion"]=head,
            ["expectedPreviousId"]=Id(command,"expectedPreviousId",true),["evidence"]=Text(command,"evidence").Trim(' ')};
        if(body["evidence"]!.GetValue<string>().Length is 0 or >500)throw Invalid();
        foreach(var key in fields)
        {
            body[key]=key switch{
                "ports"=>Ports(operationId,command.GetProperty(key)),
                "ratchets"=>Ratchets(command.GetProperty(key)),
                "activities"=>Activities(operationId,command.GetProperty(key)),
                "activityRevisionIds"=>RevisionIds(command.GetProperty(key)),
                "referenceCatalogId" or "referenceRevisionId" or "versionId" or "portId"=>JsonValue.Create(Id(command,key,false)),
                "openingRevisionId"=>JsonValue.Create(Id(command,key,kind=="coverage")),
                "sourceRevisionId"=>JsonValue.Create(Id(command,key,true)),
                "businessKey"=>JsonValue.Create(Text(command,key).Trim(' ')),
                "from" or "through" or "observedAt"=>JsonValue.Create(Instant(command,key)),
                "minimum" or "maximum"=>JsonValue.Create(Decimal(command,key)),
                "quantity" when kind!="define"=>command.GetProperty(key).ValueKind==JsonValueKind.Null&&kind=="activity"?null:JsonValue.Create(Decimal(command,key)),
                "facilityCode" or "commodity" or "quantity" or "clock" or "implementation" or "inventoryBasis" or "rateBasis" or "simultaneousFlow" or "lossTreatment"=>JsonValue.Create(Code(command,key)),
                _=>JsonValue.Create(Text(command,key))};
        }
        if((kind is "opening" or "activity" or "coverage")&&body["businessKey"]!.GetValue<string>().Length>128)throw Invalid();
        if(kind=="activity"&&(body["quantity"] is null)==(body["sourceRevisionId"] is null))throw Invalid();
        if(kind=="activity"&&body["quantity"] is {} amount&&ExactDecimal.Parse(amount.GetValue<string>()).CompareTo(default)<0)throw Invalid();
        if(kind is "activity" or "coverage" or "plan")Window(body);
        if(kind=="activity")Direction(body["direction"]!.GetValue<string>());
        if(kind=="coverage"&&body["statement"]!.GetValue<string>() is not(StorageFacilityCoverage.CompleteActual or StorageFacilityCoverage.IncompleteActual))throw Invalid();
        if(kind=="plan")
        {
            if(body["coverageStatement"]!.GetValue<string>()!=StorageFacilityCoverage.ChosenPlan)throw Invalid();
            if(command.TryGetProperty("planId",out _)&&Id(command,"planId",false)!=operationId)throw Invalid();
            body["planId"]=operationId;
        }
        return JsonSerializer.SerializeToElement(body);
    }

    private static JsonArray Ports(Guid operation,JsonElement rows)
    {
        var values=Array(rows,1,StorageFacilityOperations.MaximumPorts).Select(row=>{
            Keys(row,["pipeline","zone","point","product","direction","contractDirection"],["portId"]);
            var direction=Text(row,"direction");Direction(direction);
            var contract=Integer(row,"contractDirection");if(contract is not(1 or -1))throw Invalid();
            var value=new JsonObject{["pipeline"]=Code(row,"pipeline"),["zone"]=Code(row,"zone"),["point"]=Code(row,"point"),
                ["product"]=Code(row,"product"),["direction"]=direction,["contractDirection"]=contract};
            return(Value:value,Supplied:row.TryGetProperty("portId",out _)?Id(row,"portId",false):null);
        }).OrderBy(x=>x.Value.ToJsonString(),StringComparer.Ordinal).ToArray();
        if(values.Select(x=>x.Value.ToJsonString()).Distinct(StringComparer.Ordinal).Count()!=values.Length)throw Invalid();
        for(var i=0;i<values.Length;i++)
        {
            var id=TechnicalId("storage_facility_port_v1",operation,i);
            if(values[i].Supplied is {} supplied&&supplied!=id)throw Invalid();
            values[i].Value["portId"]=id;
        }
        return new JsonArray(values.OrderBy(x=>x.Value["portId"]!.GetValue<Guid>()).Select(x=>(JsonNode)x.Value).ToArray());
    }

    private static JsonArray Activities(Guid operation,JsonElement rows)
    {
        var values=Array(rows,0,StorageFacilityOperations.MaximumActivities).Select(row=>{
            Keys(row,["portId","from","through","direction","quantity"],["revisionId"]);
            var direction=Text(row,"direction");Direction(direction);
            var quantity=Decimal(row,"quantity");if(ExactDecimal.Parse(quantity).CompareTo(default)<0)throw Invalid();
            var value=new JsonObject{["portId"]=Id(row,"portId",false),["from"]=Instant(row,"from"),["through"]=Instant(row,"through"),
                ["direction"]=direction,["quantity"]=quantity};Window(value);
            return(Value:value,Supplied:row.TryGetProperty("revisionId",out _)?Id(row,"revisionId",false):null);
        }).OrderBy(x=>x.Value.ToJsonString(),StringComparer.Ordinal).ToArray();
        // Identical flows retain multiplicity. Supplied identities are checked
        // against their whole equal-value group so reordered retries are stable.
        var ordinal=0;
        foreach(var group in values.GroupBy(x=>x.Value.ToJsonString(),StringComparer.Ordinal))
        {
            var equal=group.ToArray();var ids=Enumerable.Range(ordinal,equal.Length).Select(i=>TechnicalId("storage_facility_plan_activity_v1",operation,i)).ToArray();
            var supplied=equal.Where(x=>x.Supplied.HasValue).Select(x=>x.Supplied!.Value).ToArray();
            if(supplied.Distinct().Count()!=supplied.Length||supplied.Any(id=>!ids.Contains(id)))throw Invalid();
            for(var i=0;i<equal.Length;i++)equal[i].Value["revisionId"]=ids[i];
            ordinal+=equal.Length;
        }
        return new JsonArray(values.OrderBy(x=>x.Value["portId"]!.GetValue<Guid>()).ThenBy(x=>x.Value["revisionId"]!.GetValue<Guid>()).Select(x=>(JsonNode)x.Value).ToArray());
    }

    private static JsonArray Ratchets(JsonElement rows)=>new(Array(rows,1,StorageFacilityOperations.MaximumRatchets).Select(row=>{
        Keys(row,["lower","upper","injectionPerHour","withdrawalPerHour"],[]);
        return(JsonNode)new JsonObject{["lower"]=Decimal(row,"lower"),["upper"]=Decimal(row,"upper"),
            ["injectionPerHour"]=Decimal(row,"injectionPerHour"),["withdrawalPerHour"]=Decimal(row,"withdrawalPerHour")};
    }).ToArray());

    private static JsonArray RevisionIds(JsonElement rows)
    {
        var ids=Array(rows,0,StorageFacilityOperations.MaximumActivities).Select(row=>{
            if(row.ValueKind==JsonValueKind.Object){Keys(row,["id"],[]);return Id(row,"id",false)!.Value;}
            return ParseId(row);
        }).Order().ToArray();
        if(ids.Distinct().Count()!=ids.Length)throw Invalid();
        return new JsonArray(ids.Select(id=>(JsonNode)JsonValue.Create(id)!).ToArray());
    }

    internal static Guid TechnicalId(string domain,Guid operation,int ordinal=0)=>OperationIdentity.For(domain,operation,ordinal);
    private static JsonElement[] Array(JsonElement value,int minimum,int maximum)
    {if(value.ValueKind!=JsonValueKind.Array||value.GetArrayLength()<minimum||value.GetArrayLength()>maximum)throw Invalid();return[..value.EnumerateArray()];}
    private static void Keys(JsonElement value,string[] required,string[] optional)
    {
        if(value.ValueKind!=JsonValueKind.Object)throw Invalid();
        var names=value.EnumerateObject().Select(x=>x.Name).ToArray();
        if(names.Length!=names.Distinct(StringComparer.Ordinal).Count()||required.Any(k=>!names.Contains(k,StringComparer.Ordinal))||
            names.Any(k=>!required.Contains(k,StringComparer.Ordinal)&&!optional.Contains(k,StringComparer.Ordinal)))throw Invalid();
    }
    private static string Text(JsonElement value,string key)
    {if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty(key,out var field)||field.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(field.GetString())||field.GetString()!.Length>4096||field.GetString()!.Contains('\0'))throw Invalid();return field.GetString()!;}
    private static string Code(JsonElement value,string key)
    {
        var text=Text(value,key);
        if(text.Length>64||text[0] is <'a' or >'z'||text.Any(c=>!(c is >='a' and <='z' or >='0' and <='9' or '_' or '.' or '-')))throw Invalid();
        return text;
    }
    private static Guid ParseId(JsonElement value)
    {if(value.ValueKind!=JsonValueKind.String||!Guid.TryParse(value.GetString(),out var id)||id==Guid.Empty)throw Invalid();return id;}
    private static Guid? Id(JsonElement value,string key,bool nullable)
    {if(!value.TryGetProperty(key,out var field))throw Invalid();return field.ValueKind==JsonValueKind.Null&&nullable?null:ParseId(field);}
    private static int Integer(JsonElement value,string key)
    {if(!value.TryGetProperty(key,out var field)||field.ValueKind!=JsonValueKind.Number||!field.TryGetInt32(out var result))throw Invalid();return result;}
    private static string Decimal(JsonElement value,string key)
    {try{return ExactDecimal.Parse(Text(value,key)).ToString();}catch(MarketDataException){throw Invalid();}}
    private static string Instant(JsonElement value,string key)=>LifecycleNative.Instant(TradingInstant.Parse(Text(value,key),"storage_facility_input"));
    private static void Direction(string value){if(value is not("inject" or "withdraw"))throw Invalid();}
    private static void Window(JsonObject value)
    {if(TradingInstant.Parse(value["through"]!.GetValue<string>(),"storage_facility_input")<=TradingInstant.Parse(value["from"]!.GetValue<string>(),"storage_facility_input"))throw Invalid();}
    private static MarketDataException Invalid()=>new("storage_facility_input","Supply every explicit storage facility field, exact source identity, UTC interval and native decimal quantity; remove unknown fields.");
}
