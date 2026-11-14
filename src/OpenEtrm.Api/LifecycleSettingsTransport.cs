using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Native row reads preserve every numeric lexeme as text. Only explicitly named
// structural integers become JSON numbers in the lifecycle presentation contract.
internal static class LifecycleSettingsTransport
{
    internal static ImmutableArray<JsonElement> Integers(ImmutableArray<JsonElement> rows,params string[] fields)=>
        [..rows.Select(row=>
        {
            var values=row.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.Clone(),StringComparer.Ordinal);
            foreach(var field in fields)
            {
                if(!values.TryGetValue(field,out var value))throw Invalid();
                if(value.ValueKind==JsonValueKind.Null)continue;
                var text=value.ValueKind==JsonValueKind.String?value.GetString():value.GetRawText();
                if(!int.TryParse(text,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var integer))throw Invalid();
                values[field]=JsonSerializer.SerializeToElement(integer);
            }
            return JsonSerializer.SerializeToElement(values,LifecycleNative.Json);
        })];
    private static MarketDataException Invalid()=>new("lifecycle_settings_metadata","The native lifecycle settings did not retain complete integer version metadata.");
}
