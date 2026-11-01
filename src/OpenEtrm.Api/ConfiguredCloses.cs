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
        settings=references["trading_close_times"].ToLookup(row=>(row.Text("milestone"),
            row.Text(row.Text("milestone")=="last_trading"?"futures_product_code":"option_product_code")));
        clocks=references["clock_rules"].ToLookup(row=>row.Text("code"));
    }

    public bool Configured(string milestone,string product)=>settings[(milestone,product)].Any();

    public DateTimeOffset Instant(string milestone,string product,DateOnly day)
    {
        var setting=settings[(milestone,product)].ToArray() switch
        {
            [var one]=>one,
            []=>throw new MarketDataException("close_time_setting_missing",$"Configure the {milestone} close of trading of {product}."),
            _=>throw new MarketDataException("ambiguous_reference",$"The {milestone} close of trading of {product} is configured more than once."),
        };
        var code=setting.Text("clock_code");
        if(!resolved.TryGetValue(code,out var clock))
        {
            var rule=clocks[code].SingleOrDefault();
            if(rule.ValueKind==JsonValueKind.Undefined)throw new MarketDataException("ambiguous_reference",$"Clock {code} is missing or ambiguous.");
            resolved.Add(code,clock=PowerClock.FromRule(ReferenceClockJson.Read(rule),TimeZoneDatabase.Installed()));
        }
        return clock.AtLocal(day,TimeOnly.ParseExact(setting.Text("local_time"),"HH:mm:ss",CultureInfo.InvariantCulture)).ToUniversalTime();
    }

    // A stored instant must equal the configured close on its date.
    public DateTimeOffset Require(string milestone,string product,DateOnly day,DateTimeOffset stored)=>
        Instant(milestone,product,day)==stored?stored:
            throw new MarketDataException("close_time_mismatch",$"The stored {milestone} instant of {product} on {day:yyyy-MM-dd} is not its configured close of trading.");
}
