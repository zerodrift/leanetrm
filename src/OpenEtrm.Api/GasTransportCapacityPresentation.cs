using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

// Human references are a projection of exact immutable memberships. Keep raw
// identities in the source/domain result; never substitute a UUID for a label.
internal static class GasTransportCapacityPresentation
{
    internal static object[] Tables(GasTransportCapacityAssessment? assessment,JsonElement? facts,JsonElement? command)
    {
        if(assessment is null)return [];
        try
        {
            if(assessment.Intervals.IsDefault)throw Source();
            var paths=new Dictionary<Guid,string>();var plans=new Dictionary<Guid,string>();var claims=new Dictionary<Guid,string>();
            if(facts is {} source)
            {
                foreach(var path in Rows(source,"paths"))Add(paths,Id(path,"id"),Label(path,"path_key"));
                foreach(var plan in Rows(source,"plans"))Add(plans,Id(plan,"id"),Label(plan,"business_key"));
                foreach(var claim in Rows(source,"claims"))
                {
                    if(!plans.TryGetValue(Id(claim,"plan_id"),out var owner))throw Source();
                    Add(claims,Id(claim,"id"),owner);
                }
            }
            if(command is {} proposed)
            {
                switch(proposed.Text("kind"))
                {
                    case "define":
                        foreach(var path in Rows(proposed,"paths"))Merge(paths,Id(path,"pathId"),Label(path,"pathKey"));
                        break;
                    case "plan":
                        var label=Label(proposed,"businessKey");
                        foreach(var claim in Rows(proposed,"claims"))Merge(claims,Id(claim,"claimId"),label);
                        break;
                    default:throw Source();
                }
            }
            string Scope(GasTransportCapacityInterval interval)=>interval.Axis switch
            {
                "agreement" when interval.Point is null&&interval.RouteVersionId is null=>"Whole entitlement",
                "receipt" or "delivery" when interval.Point is {} point&&interval.RouteVersionId is null&&!string.IsNullOrEmpty(point.Code)=>point.Code,
                "route" when interval.Point is null&&interval.RouteVersionId is {} path&&paths.TryGetValue(path,out var name)=>name,
                _=>throw Source()
            };
            string Contributors(GasTransportCapacityInterval interval)=>string.Join(", ",interval.Uses.Select(id=>
                claims.TryGetValue(id,out var owner)?owner:throw Source()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            return [new{label="Gross capacity by interval",
                columns=new[]{"Capacity scope","Point or path","From","Through","Chosen / hour","Maximum / hour","Assessment","Chosen plans","Remaining / hour"},
                rows=assessment.Intervals.Select(i=>new[]{i.Axis switch{"agreement"=>"Agreement","receipt"=>"Receipt point","delivery"=>"Delivery point","route"=>"Path",_=>throw Source()},
                    Scope(i),LifecycleNative.Instant(i.From),LifecycleNative.Instant(i.Through),i.UsedPerHour.ToExactString(),i.MaximumPerHour.ToExactString(),
                    i.WithinLimit?"Within limit":"Capacity exceeded",Contributors(i),i.MaximumPerHour.Add(i.UsedPerHour.Scale(ExactDecimal.Parse("-1"))).ToExactString()}).ToArray(),instantColumns=new[]{2,3}}];
        }
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {throw new MarketDataException("gas_transport_capacity_source","Retain exact path references and complete claim-to-plan memberships before displaying capacity.",error);}
    }
    static JsonElement[] Rows(JsonElement source,string field)
    {var rows=source.GetProperty(field);if(rows.ValueKind!=JsonValueKind.Array)throw Source();return[..rows.EnumerateArray()];}
    static Guid Id(JsonElement source,string field)
    {var id=source.Id(field);return id==Guid.Empty?throw Source():id;}
    static string Label(JsonElement source,string field)
    {
        var value=source.GetProperty(field);if(value.ValueKind!=JsonValueKind.String)throw Source();
        var text=value.GetString()!;var canonical=GasOperationText.Key(text,"gas_transport_capacity_source");
        return canonical==text?text:throw Source();
    }
    static void Add(Dictionary<Guid,string> values,Guid id,string label)
    {if(!values.TryAdd(id,label))throw Source();}
    static void Merge(Dictionary<Guid,string> values,Guid id,string label)
    {if(values.TryGetValue(id,out var current)&&current!=label)throw Source();values[id]=label;}
    static MarketDataException Source()=>new("gas_transport_capacity_source","Retain exact path references and complete claim-to-plan memberships before displaying capacity.");
}
