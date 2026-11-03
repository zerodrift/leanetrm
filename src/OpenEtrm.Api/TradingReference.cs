using System.Globalization;
using System.Text.Json;

namespace OpenEtrm.Api;

// Readable aliases are scoped to the selected trading catalog. UUIDs remain the
// exact relationship keys; aliases are allocated once and never encode revision.
internal static class TradingReference
{
    private static string Format(string prefix,JsonElement row,string field)=>Format(prefix,int.Parse(row.Text(field),CultureInfo.InvariantCulture));
    private static string Format(string prefix,int number)=>prefix+number.ToString("D6",CultureInfo.InvariantCulture);
    public static string Trade(JsonElement row)=>Format("TR-",row,"trade_reference");
    public static string Trade(int number)=>Format("TR-",number);
    public static string Instrument(JsonElement row)=>Instrument(row,"instrument_reference");
    public static string Instrument(JsonElement row,string field)=>Format("IN-",row,field);
}
