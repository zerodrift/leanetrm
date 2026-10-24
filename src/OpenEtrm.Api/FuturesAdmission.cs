using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

/// <summary>Separates sourced exchange facts from explicitly approved pilot execution eligibility.</summary>
public sealed class FuturesAdmission
{
    public const string Usage="generated_prices_sample_trades_v1";
    public const string ExactFirst="exchange_first_trade_instant_v1";
    public const string AfterDate="after_source_first_trade_date_v1";
    public static DateTimeOffset Require(JsonElement product,JsonElement series,JsonElement curve,
        IReadOnlyDictionary<string,ImmutableArray<JsonElement>> references,string implementation)
        =>new FuturesAdmission(references).Require(product,series,curve,implementation);
    private readonly ILookup<string,JsonElement> sources,milestones,receipts,clockRows;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,PowerClock> clocks=new(StringComparer.Ordinal);
    public FuturesAdmission(IReadOnlyDictionary<string,ImmutableArray<JsonElement>> references)
    {
        sources=Rows(references,"sources").ToLookup(r=>r.Text("code"));
        milestones=Rows(references,"futures_milestones").ToLookup(r=>r.Text("series_code"));
        receipts=Rows(references,"futures_execution_admissions").ToLookup(r=>r.Text("series_code"));
        clockRows=Rows(references,"clock_rules").ToLookup(r=>r.Text("code"));
    }
    public DateTimeOffset Require(JsonElement product,JsonElement series,JsonElement curve,string implementation)
    {
        try
        {
            if(product.Text("definition_state")=="synthetic"&&series.Text("definition_state")=="synthetic"&&curve.OptionalText("direct_role")=="daily_settlement")return EligibleFrom(series);
            var receipt=Receipt(series);
            if(product.Text("definition_state")!="candidate"||series.Text("definition_state")!="candidate"||
                implementation!=FuturesMarketBinding.GeneralImplementation||receipt.Text("usage_policy")!=Usage||receipt.Text("execution_venue")!="central_limit_order_book"||curve.OptionalText("direct_role")!="trader_mark")throw Invalid("The sourced product, series, implementation and generated-mark role must match the admission.");
            var source=One(sources[curve.Text("direct_source_code")]);
            var approval=One(sources[receipt.Text("qualification_source_code")]);
            if(source.Text("kind")!="synthetic"||!source.Text("locator").StartsWith("generated:pilot-close-v1/",StringComparison.Ordinal)||
                approval.Text("kind")!="manual_entry"||!Hash(source.OptionalText("content_sha256"))||!Hash(approval.OptionalText("content_sha256")))throw Invalid("The generated mark and qualification must have retained source hashes.");
            return EligibleFrom(series);
        }
        catch(Exception e) when(e is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException){throw Invalid();}
    }
    public static DateTimeOffset EligibleFrom(JsonElement series,IReadOnlyDictionary<string,ImmutableArray<JsonElement>> references)
        =>new FuturesAdmission(references).EligibleFrom(series);
    public DateTimeOffset EligibleFrom(JsonElement series)
    {
        try
        {
            var first=One(milestones[series.Text("code")].Where(m=>m.Text("milestone")=="first_trading"));
            if(first.Text("value_state")!="known")throw Invalid();
            var selected=receipts[series.Text("code")].Where(r=>r.Text("implementation")==FuturesMarketBinding.GeneralImplementation).ToArray();
            if(first.OptionalText("event_at") is not null)
            {
                var exact=SchedulePreview.RequiredMilestoneInstant(first);
                if(selected.Length>0&&(selected.Length!=1||selected[0].Text("eligibility_rule")!=ExactFirst||SchedulePreview.ParseAt(selected[0].Text("eligible_from"))!=exact))throw Invalid();
                return exact;
            }
            var receipt=One(selected);
            if(receipt.Text("usage_policy")!=Usage||receipt.Text("eligibility_rule")!=AfterDate)throw Invalid();
            var clock=One(clockRows[first.Text("clock_code")]);
            var rule=new ClockRule(clock.Text("code"),clock.Text("kind"),clock.GetProperty("fixed_offset_seconds").ValueKind==JsonValueKind.Null?null:int.Parse(clock.Text("fixed_offset_seconds"),CultureInfo.InvariantCulture),clock.OptionalText("iana_zone"),clock.OptionalText("tzdb_version"));
            var captured=clocks.GetOrAdd(rule.Code,_=>PowerClock.FromRule(rule,TimeZoneDatabase.Installed()));
            var nativeDate=DateOnly.ParseExact(first.Text("business_date"),"yyyy-MM-dd",CultureInfo.InvariantCulture);
            if(nativeDate.Year is <2000 or >=2100||!(rule.Kind=="fixed_offset"&&rule.FixedOffsetSeconds==0||rule.Kind=="iana"&&rule.IanaZone is "America/New_York" or "America/Chicago" or "America/Los_Angeles"))throw Invalid();
            var nextDay=captured.StartOfDay(nativeDate.AddDays(1));
            var bound=nextDay.ToUniversalTime();
            if(SchedulePreview.ParseAt(receipt.Text("eligible_from"))!=bound||
                int.Parse(receipt.Text("eligibility_offset_seconds"),CultureInfo.InvariantCulture)!=(int)nextDay.Offset.TotalSeconds||
                receipt.OptionalText("eligibility_tzdb_version")!=rule.TzdbVersion)throw Invalid();
            return bound;
        }
        catch(Exception e) when(e is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException){throw Invalid();}
    }
    JsonElement Receipt(JsonElement series)=>One(receipts[series.Text("code")].Where(r=>r.Text("implementation")==FuturesMarketBinding.GeneralImplementation));
    static ImmutableArray<JsonElement> Rows(IReadOnlyDictionary<string,ImmutableArray<JsonElement>> refs,string name)=>refs.TryGetValue(name,out var rows)?rows:[];
    static JsonElement One(IEnumerable<JsonElement> rows){var values=rows.Take(2).ToArray();return values.Length==1?values[0]:throw Invalid();}
    static bool Hash(string? value)=>Regex.IsMatch(value??"","\\A[0-9a-f]{64}\\z",RegexOptions.CultureInvariant);
    static MarketDataException Invalid(string message="This exact sourced contract needs approved pilot terms, generated-price provenance and an explicit trading eligibility rule.")=>new("futures_admission",message);
}
