using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

// Each product's configured local close of trading for a milestone, on its reference clock. A milestone instant is
// that local time on the sourced date; a product without a setting has no derived instant, which is an error.
internal sealed class ConfiguredCloses
{
    private readonly ILookup<(string Milestone,string Product),JsonElement> settings;
    private readonly ILookup<string,JsonElement> clocks;
    private readonly Dictionary<string,PowerClock> resolved=[];

    public ConfiguredCloses(IReadOnlyDictionary<string,ImmutableArray<JsonElement>> references)
    {
        settings=Table(references,"trading_close_times").ToLookup(row=>(row.Text("milestone"),
            row.Text(row.Text("milestone")=="last_trading"?"futures_product_code":"option_product_code")));
        clocks=Table(references,"clock_rules").ToLookup(row=>row.Text("code"));
    }

    private static ImmutableArray<JsonElement> Table(IReadOnlyDictionary<string,ImmutableArray<JsonElement>> references,string table)=>
        references.TryGetValue(table,out var rows)?rows:throw new MarketDataException("reference_read",$"The read does not include {table}.");

    public bool Configured(string milestone,string product)=>settings[(milestone,product)].Any();

    public DateTimeOffset Instant(string milestone,string product,DateOnly day)
    {
        var setting=One(settings[(milestone,product)],$"The {milestone} close of trading of {product}","close_time_setting_missing");
        var code=setting.Text("clock_code");
        if(!resolved.TryGetValue(code,out var clock))
            resolved.Add(code,clock=PowerClock.FromRule(ReferenceClockJson.Read(One(clocks[code],$"Clock {code}","ambiguous_reference")),TimeZoneDatabase.Installed()));
        if(!TimeOnly.TryParseExact(setting.Text("local_time"),"HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var time))
            throw new MarketDataException("close_time_local",$"The {milestone} close of trading of {product} is not a whole-second local time.");
        try { return clock.AtLocal(day,time).ToUniversalTime(); }
        catch(MarketDataException error) when(error.Code=="clock_rule")
        { throw new MarketDataException("close_time_local",$"The {milestone} close of trading of {product} does not exist or is ambiguous on {day:yyyy-MM-dd}.",error); }
    }

    private static JsonElement One(IEnumerable<JsonElement> rows,string label,string missing)=>rows.Take(2).ToArray() switch
    {
        [var one]=>one,
        []=>throw new MarketDataException(missing,$"{label} is not configured."),
        _=>throw new MarketDataException("ambiguous_reference",$"{label} is configured more than once."),
    };

    // A stored instant must equal the configured close on its date.
    public DateTimeOffset Require(string milestone,string product,DateOnly day,DateTimeOffset stored)=>
        Instant(milestone,product,day)==stored?stored:
            throw new MarketDataException("close_time_mismatch",$"The stored {milestone} instant of {product} on {day:yyyy-MM-dd} is not its configured close of trading.");
}
