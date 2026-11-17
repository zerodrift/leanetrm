using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Financial source selection is explicit. The native boundary independently
// admits the selected state and observation; this adapter never supplies money.
internal static class GasTransportFinancialCommands
{
    internal static JsonElement Normalize(JsonElement input)
    {
        try{return NormalizeCore(input);}
        catch(MarketDataException error)when(error.Code!="gas_transport_financial_input"){throw Invalid();}
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {throw Invalid();}
    }

    static JsonElement NormalizeCore(JsonElement input)
    {
        var kind=Text(input,"kind");
        string[] selected=kind switch{
            "adopt_capacity_version"=>["expectedFinancialStateId","capacityVersionId"],
            "adopt_rights_mark"=>["financialStateId","expectedPreviousMarkId","valuationAt","marketDefinitionRevisionId",
                "curveCode","contractId","seriesId","observationId","source","quote"],
            "expire"=>["expectedFinancialStateId","policyId","policyRevision"],_=>throw Invalid()};
        var names=input.EnumerateObject().Select(p=>p.Name).ToArray();
        if(names.Length!=names.Distinct(StringComparer.Ordinal).Count()||
            !names.ToHashSet(StringComparer.Ordinal).SetEquals(["kind","tradeId","evidence",..selected]))throw Invalid();
        var result=new JsonObject{["kind"]=kind,["tradeId"]=Id(input,"tradeId"),
            ["evidence"]=GasOperationText.Evidence(Text(input,"evidence"),"gas_transport_financial_input")};
        foreach(var field in selected)
        {
            if(field=="policyRevision")
                result[field]=input.GetProperty(field).TryGetInt32(out var revision)&&revision>0?revision:throw Invalid();
            else if(field=="expectedPreviousMarkId")
                result[field]=input.GetProperty(field).ValueKind==JsonValueKind.Null?null:JsonValue.Create(Id(input,field));
            else if(field=="valuationAt")
                result[field]=TradingInstant.Parse(Text(input,field),"gas_transport_financial_input").ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",System.Globalization.CultureInfo.InvariantCulture);
            else if(field is "curveCode" or "source" or "quote")
            {
                var code=Text(input,field);
                if(code!=GasOperationText.Key(code,"gas_transport_financial_input"))throw Invalid();
                result[field]=code;
            }
            else result[field]=Id(input,field);
        }
        return JsonSerializer.SerializeToElement(result);
    }

    static string Text(JsonElement row,string field)=>row.ValueKind==JsonValueKind.Object&&
        row.TryGetProperty(field,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()!:throw Invalid();
    static Guid Id(JsonElement row,string field)=>Guid.TryParse(Text(row,field),out var id)&&id!=Guid.Empty?id:throw Invalid();
    static MarketDataException Invalid()=>new("gas_transport_financial_input",
        "Supply the complete installed financial operation, exact state and source identities, explicit predecessor and evidence.");
}
