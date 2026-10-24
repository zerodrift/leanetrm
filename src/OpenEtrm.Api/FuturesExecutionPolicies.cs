using System.Collections.Immutable;
using System.Text.Json;
using System.Globalization;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Exact-version execution qualification is reference data, independently of
// source provenance, installation identity and whether observations are generated.
public sealed class FuturesExecutionPolicies
{
    private readonly Dictionary<string,PowerClock> resolvedClocks=[];
    private readonly ILookup<string,JsonElement> policies,milestones,sources,clocks;
    public FuturesExecutionPolicies(IReadOnlyDictionary<string,ImmutableArray<JsonElement>> references)
    {
        clocks=Rows(references,"clock_rules").ToLookup(r=>r.Text("code"));
        policies=Rows(references,"futures_execution_policies").ToLookup(r=>r.Text("series_code"));
        milestones=Rows(references,"futures_milestones").ToLookup(r=>r.Text("series_code"));
        sources=Rows(references,"sources").ToLookup(r=>r.Text("code"));
    }
    public static DateTimeOffset Resolve(JsonElement product,JsonElement series,JsonElement curve,
        IReadOnlyDictionary<string,ImmutableArray<JsonElement>> references,string implementation)=>
        new FuturesExecutionPolicies(references).Resolve(product,series,curve,implementation);
    public DateTimeOffset Resolve(JsonElement product,JsonElement series,JsonElement curve,string implementation)
    {
        try
        {
            if(!FuturesMarketBinding.Supported(implementation)||product.Text("definition_state") is not ("candidate" or "synthetic")||
                series.Text("definition_state") is not ("candidate" or "synthetic"))throw Invalid();
            var first=One(milestones[series.Text("code")].Where(m=>m.Text("milestone")=="first_trading"));
            DateTimeOffset? sourceFirst=first.Text("value_state")=="known"&&first.OptionalText("event_at") is not null?
                SchedulePreview.RequiredMilestoneInstant(first):null;
            var selected=policies[series.Text("code")].Where(p=>p.Text("implementation")==implementation).ToArray();
            // Retain the existing explicitly synthetic exact-instant convention.
            if(selected.Length==0&&product.Text("definition_state")=="synthetic"&&series.Text("definition_state")=="synthetic"&&
                curve.OptionalText("direct_role")=="daily_settlement"&&sourceFirst is {} legacy)
            {
                if(policies[series.Text("code")].Any(p=>p.Text("eligibility_rule")!="source_first_trade_instant_v1"||
                    SchedulePreview.ParseAt(p.Text("eligible_from"))!=legacy))throw Invalid();
                return legacy;
            }
            var policy=One(selected);var role=policy.Text("price_role");
            if(role is not ("daily_settlement" or "trader_mark")||role!=curve.OptionalText("direct_role")||
                policy.Text("price_source_code")!=curve.OptionalText("direct_source_code"))throw Invalid();
            _=One(sources[policy.Text("price_source_code")]);_=One(sources[policy.Text("evidence_source_code")]);
            var bound=SchedulePreview.ParseAt(policy.Text("eligible_from"));
            if(bound.Ticks%10!=0||bound<=DateTimeOffset.MinValue||bound.Year>=9999||sourceFirst is {} known&&bound<known)throw Invalid();
            var hasDateProof=policy.OptionalText("source_date_offset_seconds") is not null||policy.OptionalText("source_date_tzdb_version") is not null;
            switch(policy.Text("eligibility_rule"))
            {
                case "source_first_trade_instant_v1":if(sourceFirst!=bound||hasDateProof)throw Invalid();break;
                case "after_source_first_trade_date_v1":
                    if(sourceFirst is not null||first.Text("value_state")!="known"||policy.OptionalText("source_date_offset_seconds") is not {} retainedOffset)throw Invalid();
                    var clockRow=One(clocks[first.Text("clock_code")]);
                    var clock=ReferenceClockJson.Read(clockRow);
                    var next=DateOnly.ParseExact(first.Text("business_date"),"yyyy-MM-dd",CultureInfo.InvariantCulture).AddDays(1);
                    if(!resolvedClocks.TryGetValue(clock.Code,out var resolvedClock))
                    {resolvedClock=PowerClock.FromRule(clock,TimeZoneDatabase.Installed());resolvedClocks.Add(clock.Code,resolvedClock);}
                    var boundary=resolvedClock.StartOfDay(next).ToUniversalTime();
                    var offset=checked((int)(new DateTimeOffset(next.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc))-boundary).TotalSeconds);
                    if(offset!=int.Parse(retainedOffset,CultureInfo.InvariantCulture)||policy.OptionalText("source_date_tzdb_version")!=clock.TzdbVersion||bound<boundary)throw Invalid();
                    break;
                case "explicit_utc_bound_v1":
                    if(hasDateProof||sourceFirst is null&&first.Text("value_state")=="known")throw Invalid();
                    break;
                default:throw Invalid();
            }
            return bound;
        }
        catch(MarketDataException e)when(e.Code!="futures_execution_policy"){throw Invalid();}
        catch(Exception e)when(e is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException){throw Invalid();}
    }
    private static ImmutableArray<JsonElement> Rows(IReadOnlyDictionary<string,ImmutableArray<JsonElement>> rows,string name)=>rows.TryGetValue(name,out var values)?values:[];
    private static JsonElement One(IEnumerable<JsonElement> rows)
    {var values=rows.Take(2).ToArray();return values.Length==1?values[0]:throw Invalid();}
    private static MarketDataException Invalid()=>new("futures_execution_policy","This contract requires complete qualified terms, an exact price source/role and an explicit versioned execution eligibility policy.");
}
