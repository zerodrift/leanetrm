using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Commands;

namespace OpenEtrm.Api;

// Supply technical identities and canonical notation only. Every capacity law,
// quantity, dated limit, party, path and selected claim remains explicit input.
internal static class GasTransportCapacityCommands
{
    internal static JsonElement Normalize(Guid operation,JsonElement input)
    {
        try{return NormalizeCore(operation,input);}
        catch(MarketDataException error)when(error.Code!="gas_transport_capacity_input"){throw Invalid();}
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException){throw Invalid();}
    }
    static JsonElement NormalizeCore(Guid operation,JsonElement input)
    {
        if(operation==Guid.Empty)throw Invalid();
        var kind=Text(input,"kind");
        string[] specific=kind switch{
            "define"=>["businessKey","referenceCatalogId","referenceRevisionId","bookId","legalEntityId","legalEntityRevision","counterpartyId","counterpartyRevision",
                "product","quantity","from","through","implementation","rateBasis","flowShape","agreementBasis","deliveryBasis","paths","agreement","receipts","deliveries"],
            "plan"=>["businessKey","versionId","admissionPolicy","claims"],_=>throw Invalid()};
        Keys(input,["kind","entitlementId","expectedStateVersion","expectedPreviousId","evidence",..specific]);
        var state=Integer(input,"expectedStateVersion",0);var prior=Id(input,"expectedPreviousId",true);
        var key=GasOperationText.Key(Text(input,"businessKey"),"gas_transport_capacity_input");
        var entitlement=Id(input,"entitlementId",kind=="define");
        if(kind=="define"&&state==0)
        {
            var identity=OperationIdentity.For("gas_transport_entitlement_v1:"+key,Id(input,"bookId")!.Value);
            if(prior is not null||entitlement is {} supplied&&supplied!=identity)throw Invalid();
            entitlement=identity;
        }
        if(entitlement is null)throw Invalid();
        var body=new JsonObject{["kind"]=kind,["entitlementId"]=entitlement,["expectedStateVersion"]=state,["expectedPreviousId"]=prior,
            ["businessKey"]=key,["evidence"]=GasOperationText.Evidence(Text(input,"evidence"),"gas_transport_capacity_input")};
        if(kind=="define")
        {
            foreach(var field in new[]{"referenceCatalogId","referenceRevisionId","bookId","legalEntityId","counterpartyId"})body[field]=Id(input,field);
            foreach(var field in new[]{"legalEntityRevision","counterpartyRevision"})body[field]=Integer(input,field,1);
            foreach(var field in new[]{"product","quantity","implementation","rateBasis","flowShape","agreementBasis","deliveryBasis"})body[field]=Code(input,field);
            body["from"]=Instant(input,"from");body["through"]=Instant(input,"through");Window(body,"from","through");
            body["agreement"]=Limits(input.GetProperty("agreement"));
            body["paths"]=Paths(entitlement.Value,input.GetProperty("paths"));
            body["receipts"]=Points(input.GetProperty("receipts"));body["deliveries"]=Points(input.GetProperty("deliveries"));
        }
        else
        {
            body["versionId"]=Id(input,"versionId");body["claims"]=Claims(operation,input.GetProperty("claims"));
            var policy=Text(input,"admissionPolicy");
            if(policy is not(GasTransportCapacityPlanAdmission.FeasibleReplacement or GasTransportCapacityPlanAdmission.EmptyRelease)||
                policy==GasTransportCapacityPlanAdmission.EmptyRelease&&(prior is null||input.GetProperty("claims").GetArrayLength()!=0))throw Invalid();
            body["admissionPolicy"]=policy;
        }
        return JsonSerializer.SerializeToElement(body);
    }
    static JsonArray Paths(Guid entitlement,JsonElement input)
    {
        var rows=Rows(input,1,GasTransportCapacity.MaximumPaths).Select(row=>{
            Keys(row,["pathKey","pipeline","receiptZone","receiptPoint","deliveryZone","deliveryPoint","product","limits"],["pathId"]);
            var key=GasOperationText.Key(Text(row,"pathKey"),"gas_transport_capacity_input");
            var id=OperationIdentity.For("gas_transport_path_v1:"+key,entitlement);
            if(row.TryGetProperty("pathId",out _)&&Id(row,"pathId")!=id)throw Invalid();
            var value=new JsonObject{["pathId"]=id,["pathKey"]=key};
            foreach(var field in new[]{"pipeline","receiptZone","receiptPoint","deliveryZone","deliveryPoint","product"})value[field]=Code(row,field);
            value["limits"]=Limits(row.GetProperty("limits"));return value;
        }).OrderBy(row=>row["pathId"]!.GetValue<Guid>()).ToArray();
        if(rows.Select(row=>row["pathId"]!.GetValue<Guid>()).Distinct().Count()!=rows.Length)throw Invalid();
        return new JsonArray(rows.Select(row=>(JsonNode)row).ToArray());
    }
    static JsonArray Points(JsonElement input)
    {
        var rows=Rows(input,1,GasTransportCapacity.MaximumPaths).Select(row=>{
            Keys(row,["point","limits"]);return new JsonObject{["point"]=Code(row,"point"),["limits"]=Limits(row.GetProperty("limits"))};
        }).OrderBy(row=>row["point"]!.GetValue<string>(),StringComparer.Ordinal).ToArray();
        if(rows.Select(row=>row["point"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count()!=rows.Length)throw Invalid();
        return new JsonArray(rows.Select(row=>(JsonNode)row).ToArray());
    }
    static JsonArray Limits(JsonElement input)
    {
        var rows=Rows(input,1,GasTransportCapacity.MaximumSlicesPerScope).Select(row=>{
            Keys(row,["from","through","maximumPerHour"]);
            var value=new JsonObject{["from"]=Instant(row,"from"),["through"]=Instant(row,"through"),["maximumPerHour"]=Decimal(row,"maximumPerHour")};
            Window(value,"from","through");return value;
        }).OrderBy(row=>row["from"]!.GetValue<string>(),StringComparer.Ordinal).ToArray();
        return new JsonArray(rows.Select(row=>(JsonNode)row).ToArray());
    }
    static JsonArray Claims(Guid operation,JsonElement input)
    {
        var rows=Rows(input,0,GasTransportCapacity.MaximumUses).Select(row=>{
            Keys(row,["pathId","receiptFrom","receiptTo","receiptQuantity","deliveryFrom","deliveryTo","deliveryQuantity"],["claimId"]);
            var value=new JsonObject{["pathId"]=Id(row,"pathId"),["receiptFrom"]=Instant(row,"receiptFrom"),["receiptTo"]=Instant(row,"receiptTo"),
                ["receiptQuantity"]=Decimal(row,"receiptQuantity"),["deliveryFrom"]=Instant(row,"deliveryFrom"),["deliveryTo"]=Instant(row,"deliveryTo"),
                ["deliveryQuantity"]=Decimal(row,"deliveryQuantity")};
            Window(value,"receiptFrom","receiptTo");Window(value,"deliveryFrom","deliveryTo");
            return(Value:value,Supplied:row.TryGetProperty("claimId",out _)?Id(row,"claimId"):null);
        }).OrderBy(row=>row.Value.ToJsonString(),StringComparer.Ordinal).ToArray();
        var ordinal=0;
        foreach(var group in rows.GroupBy(row=>row.Value.ToJsonString(),StringComparer.Ordinal))
        {
            var equal=group.ToArray();var ids=Enumerable.Range(ordinal,equal.Length).Select(i=>OperationIdentity.For("gas_transport_claim_v1",operation,i)).ToArray();
            var supplied=equal.Where(row=>row.Supplied.HasValue).Select(row=>row.Supplied!.Value).ToArray();
            if(supplied.Distinct().Count()!=supplied.Length||supplied.Any(id=>!ids.Contains(id)))throw Invalid();
            for(var i=0;i<equal.Length;i++)equal[i].Value["claimId"]=ids[i];
            ordinal+=equal.Length;
        }
        return new JsonArray(rows.OrderBy(row=>row.Value["pathId"]!.GetValue<Guid>()).ThenBy(row=>row.Value["claimId"]!.GetValue<Guid>()).Select(row=>(JsonNode)row.Value).ToArray());
    }
    static JsonElement[] Rows(JsonElement input,int minimum,int maximum)
    {if(input.ValueKind!=JsonValueKind.Array||input.GetArrayLength()<minimum||input.GetArrayLength()>maximum)throw Invalid();return[..input.EnumerateArray()];}
    static void Keys(JsonElement input,string[] required,string[]? optional=null)
    {
        if(input.ValueKind!=JsonValueKind.Object)throw Invalid();optional??=[];
        var names=input.EnumerateObject().Select(p=>p.Name).ToArray();
        if(names.Length!=names.Distinct(StringComparer.Ordinal).Count()||required.Any(k=>!names.Contains(k,StringComparer.Ordinal))||
            names.Any(k=>!required.Contains(k,StringComparer.Ordinal)&&!optional.Contains(k,StringComparer.Ordinal)))throw Invalid();
    }
    static string Text(JsonElement input,string field)
    {if(!input.TryGetProperty(field,out var value)||value.ValueKind!=JsonValueKind.String)throw Invalid();return value.GetString()!;}
    static string Code(JsonElement input,string field)
    {var value=Text(input,field);if(value.Length is <1 or >64||value[0] is <'a' or >'z'||value.Any(c=>!(c is >='a' and <='z' or >='0' and <='9' or '_' or '.' or '-')))throw Invalid();return value;}
    static Guid? Id(JsonElement input,string field,bool nullable=false)
    {var value=input.GetProperty(field);if(nullable&&value.ValueKind==JsonValueKind.Null)return null;if(value.ValueKind!=JsonValueKind.String||!Guid.TryParse(value.GetString(),out var id)||id==Guid.Empty)throw Invalid();return id;}
    static int Integer(JsonElement input,string field,int minimum)
    {var value=input.GetProperty(field);return value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out var n)&&n>=minimum?n:throw Invalid();}
    static string Decimal(JsonElement input,string field)
    {var value=ExactDecimal.Parse(Text(input,field));if(value.CompareTo(default)<0)throw Invalid();return value.ToString();}
    static string Instant(JsonElement input,string field)=>LifecycleNative.Instant(TradingInstant.Parse(Text(input,field),"gas_transport_capacity_input"));
    static void Window(JsonObject value,string from,string through)
    {if(TradingInstant.Parse(value[from]!.GetValue<string>(),"gas_transport_capacity_input")>=TradingInstant.Parse(value[through]!.GetValue<string>(),"gas_transport_capacity_input"))throw Invalid();}
    static MarketDataException Invalid()=>new("gas_transport_capacity_input","Supply complete explicit Transport capacity terms, exact references, native quantities, dated limits, source identities and evidence; remove unknown fields.");
}
