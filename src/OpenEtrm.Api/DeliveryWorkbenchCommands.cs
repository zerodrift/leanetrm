using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// The bridge selects exact native facts. It has no inferred delivery window,
// quantity conversion, source plan or target revision.
public static class DeliveryWorkbenchCommands
{
    public const string Policy="exact_terminal_native_interval_v1";
    internal static MarketDataException Invalid()=>new("delivery_workbench_input","Supply a complete explicit route assignment, exact native target interval and reviewed evidence.");
    private static void Keys(JsonElement value,params string[] names)
    {
        if(value.ValueKind!=JsonValueKind.Object||value.EnumerateObject().Count()!=names.Length||
            value.EnumerateObject().Any(p=>!names.Contains(p.Name,StringComparer.Ordinal))||names.Any(n=>!value.TryGetProperty(n,out _)))throw Invalid();
    }
    private static Guid Id(JsonElement value,string key)
    {
        var field=value.GetProperty(key);
        if(field.ValueKind!=JsonValueKind.String||!Guid.TryParseExact(field.GetString(),"D",out var id)||id==Guid.Empty)throw Invalid();return id;
    }
    private static int Revision(JsonElement value,string key)
    {
        var field=value.GetProperty(key);
        if(field.ValueKind!=JsonValueKind.Number||!field.TryGetInt32(out var number)||number is <1 or >999999999||field.GetRawText()!=number.ToString(CultureInfo.InvariantCulture))throw Invalid();return number;
    }
    private sealed record Allocation(Guid NodeId,Guid ObligationId,int TradeRevision,string Day,string Quantity);
    public static JsonElement Normalize(JsonElement body)
    {
        try
        {
            Keys(body,"kind","scopeId","routePlanId","expectedPreviousId","expectedRouteStateVersion","policy","allocations","evidence");
            var kind=body.Text("kind");if(kind is not("allocate" or "release")||body.Text("policy")!=Policy)throw Invalid();
            var scope=Id(body,"scopeId");var source=Id(body,"routePlanId");
            Guid? previous=body.GetProperty("expectedPreviousId").ValueKind==JsonValueKind.Null?null:Id(body,"expectedPreviousId");
            var state=Revision(body,"expectedRouteStateVersion");
            var evidence=GasOperationText.Evidence(body.Text("evidence"),"delivery_workbench_input");
            var rows=body.GetProperty("allocations");if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>1024)throw Invalid();
            var values=rows.EnumerateArray().Select(row=>{
                Keys(row,"nodeId","obligationId","tradeRevision","day","quantity");
                var day=row.Text("day");if(!DateOnly.TryParseExact(day,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _)||day.Length!=10)throw Invalid();
                var text=row.Text("quantity");if(text.Length>44||!Regex.IsMatch(text,@"^(0|[1-9][0-9]*)([.][0-9]{1,18})?$",RegexOptions.CultureInvariant))throw Invalid();
                var quantity=ExactDecimal.Parse(text);if(quantity.CompareTo(default)<=0)throw Invalid();
                return new Allocation(Id(row,"nodeId"),Id(row,"obligationId"),Revision(row,"tradeRevision"),day,quantity.ToString());
            }).OrderBy(v=>v.NodeId.ToString("D"),StringComparer.Ordinal).ThenBy(v=>v.ObligationId.ToString("D"),StringComparer.Ordinal).ThenBy(v=>v.Day,StringComparer.Ordinal).ToArray();
            if(values.Select(v=>(v.NodeId,v.ObligationId,v.Day)).Distinct().Count()!=values.Length||
                (kind=="allocate"&&values.Length==0)||(kind=="release"&&(values.Length!=0||previous is null)))throw Invalid();
            return JsonSerializer.SerializeToElement(new{kind,scopeId=scope,routePlanId=source,expectedPreviousId=previous,expectedRouteStateVersion=state,policy=Policy,allocations=values,evidence},GasOwnedOperations.Json);
        }
        catch(MarketDataException error)when(error.Code is "numeric_range" or "numeric_format"){throw Invalid();}
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {throw Invalid();}
    }
}
