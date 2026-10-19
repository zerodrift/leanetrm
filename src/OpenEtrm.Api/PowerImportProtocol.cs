using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

// Provider payloads end here. Stored/calculation values retain exact units and UTC intervals.
public sealed record ImportedPowerPrice(DateTimeOffset Start, DateTimeOffset End, UnitPrice Price, int? Revision);

public static class PowerImportProtocol
{
    public static void ValidateFeed(string provider,string dataset,string market,int minutes)
    {
        if((provider,dataset,market,minutes) is not
            ("PJM","da_hrl_lmps","DA",60) and not
            ("PJM","rt_hrl_lmps","RT",60) and not
            ("ERCOT","np4-190-cd/dam_stlmnt_pnt_prices","DA",60) and not
            ("ERCOT","np6-905-cd/spp_node_zone_hub","RT",15))
            throw Invalid("The provider, dataset, market and native interval must identify a supported feed.");
    }

    public static PowerInterval[] Window(DateOnly from,DateOnly through,PowerClock clock,int minutes)
    {
        if(through<from || through.DayNumber-from.DayNumber>=31)
            throw Invalid("Choose an explicit inclusive collection window of 1–31 operating days.");
        return PowerHours.Compile(from,through.AddDays(1),clock,TimeOfUse.All(minutes),[],minutes);
    }

    public static ImportedPowerPrice Pjm(JsonElement row,string market,string point,int minutes,PriceUnit unit) => Parse<ImportedPowerPrice>(() =>
    {
        if(market is not("DA" or "RT") || minutes!=60 || Text(row.GetProperty("pnode_id"))!=point)
            throw Invalid("PJM row does not match the selected hourly source.");
        var text=Text(row.GetProperty("datetime_beginning_utc"));
        if(!DateTime.TryParseExact(text,["yyyy-MM-dd'T'HH:mm:ss","yyyy-MM-dd'T'HH:mm:ss'Z'","yyyy-MM-dd HH:mm:ss"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var utc) || utc.Minute!=0 || utc.Second!=0)
            throw Invalid("PJM row requires an exact UTC hourly boundary.");
        var start=new DateTimeOffset(DateTime.SpecifyKind(utc,DateTimeKind.Utc));
        var suffix=market.ToLowerInvariant();
        var price=Number(row.GetProperty("total_lmp_"+suffix));
        var revision=row.GetProperty("version_nbr").GetInt32();
        if(revision<1)throw Invalid("PJM revision must be positive.");
        return new(start,start.AddMinutes(minutes),new(price,unit),revision);
    });

    public static ImportedPowerPrice[] Ercot(JsonElement report,string market,string point,PowerInterval[] window,PriceUnit unit) => Parse(() =>
    {
        if(market is not("DA" or "RT") || window.Length==0)throw Invalid("ERCOT requires an explicit market and interval window.");
        var fields=report.GetProperty("fields").EnumerateArray().Select(f=>f.GetProperty("name").GetString()!).ToArray();
        if(fields.Any(string.IsNullOrEmpty) || fields.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=fields.Length)
            throw Invalid("ERCOT field names must be distinct.");
        var columns=fields.Select((name,index)=>(name,index)).ToDictionary(p=>p.name,p=>p.index,StringComparer.OrdinalIgnoreCase);
        var expected=window.ToDictionary(i=>(i.OperatingDate,i.HourEnding,i.Minute,i.Occurrence));
        var result=new List<ImportedPowerPrice>();
        var starts=new HashSet<DateTimeOffset>();
        foreach(var data in report.GetProperty("data").EnumerateArray())
        {
            var values=data.EnumerateArray().ToArray();
            if(values.Length!=fields.Length)throw Invalid("ERCOT row does not match its field schema.");
            JsonElement Field(string name)=>columns.TryGetValue(name,out var index)?values[index]:throw Invalid("ERCOT response lacks a required field.");
            if(Text(Field("settlementPoint"))!=point)throw Invalid("ERCOT row does not match the selected settlement point.");
            if(market=="RT")
            {
                var type=Text(Field("settlementPointType"));
                if(type is "LZEW" or "LZ_DCEW")continue;
                if(type is not("HU" or "SH" or "AH" or "LZ" or "LZ_DC" or "RN" or "PCCRN" or "LCCRN" or "PUN"))throw Invalid("Unsupported ERCOT settlement point type.");
            }
            if(!DateOnly.TryParseExact(Text(Field("deliveryDate")),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))
                throw Invalid("ERCOT delivery date must be explicit.");
            var hourText=Text(Field(market=="DA"?"hourEnding":"deliveryHour"));
            if(market=="DA" && hourText.EndsWith(":00",StringComparison.Ordinal))hourText=hourText[..^3];
            if(!int.TryParse(hourText,NumberStyles.None,CultureInfo.InvariantCulture,out var hour) || hour is <1 or >24)
                throw Invalid("ERCOT hour ending is invalid.");
            var minute=0;
            if(market=="RT")
            {
                if(!int.TryParse(Text(Field("deliveryInterval")),NumberStyles.None,CultureInfo.InvariantCulture,out var interval) || interval is <1 or >4)
                    throw Invalid("ERCOT settlement interval must be 1–4.");
                minute=(interval-1)*15;
            }
            var repeated=Field("DSTFlag");
            var occurrence=repeated.ValueKind switch
            {
                JsonValueKind.True=>2,JsonValueKind.False=>1,
                JsonValueKind.String when repeated.GetString() is "Y" or "true"=>2,
                JsonValueKind.String when repeated.GetString() is "N" or "false"=>1,
                _=>throw Invalid("ERCOT repeated-hour flag is missing or invalid.")
            };
            if(!expected.TryGetValue((date,hour,minute,occurrence),out var period) ||
                period.End-period.Start!=TimeSpan.FromMinutes(market=="DA"?60:15) || !starts.Add(period.Start))
                throw Invalid("ERCOT row is duplicated or outside the selected native interval window.");
            result.Add(new(period.Start,period.End,new(Number(Field("settlementPointPrice")),unit),null));
        }
        return result.ToArray();
    });

    static string Text(JsonElement value)=>value.ValueKind switch
    {
        JsonValueKind.String=>value.GetString()!,JsonValueKind.Number=>value.GetRawText(),
        _=>throw Invalid("A required provider value is missing or has the wrong type.")
    };
    static ExactDecimal Number(JsonElement value)=>ExactDecimal.Parse(Text(value));
    static T Parse<T>(Func<T> parse)
    {
        try{return parse();}
        catch(Exception e) when(e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {throw Invalid("The provider response does not match the selected feed contract.");}
    }
    static MarketDataException Invalid(string message)=>new("power_import",message);
}
