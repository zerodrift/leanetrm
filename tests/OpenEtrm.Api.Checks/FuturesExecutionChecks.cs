using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

internal static class FuturesExecutionChecks
{
    public static void Run()
    {
        JsonElement Row(object value)=>JsonSerializer.SerializeToElement(value);
        JsonElement Policy(string rule="after_source_first_trade_date_v1",string bound="2040-03-15T12:30:00Z",
            int? offset=0,string? tzdb=null,string role="trader_mark",string source="chosen_source")=>Row(new{
                series_code="test_month",implementation=FuturesMarketBinding.Implementation,
                eligibility_rule=rule,eligible_from=bound,source_date_offset_seconds=offset,source_date_tzdb_version=tzdb,
                price_role=role,price_source_code=source,evidence_source_code="reviewed_terms"});
        JsonElement First(string state="known",string? date="2040-03-11",string? instant=null)=>Row(new{
            series_code="test_month",milestone="first_trading",value_state=state,business_date=date,event_at=instant,clock_code="test_clock"});
        JsonElement FixedClock(int offset)=>Row(new{code="test_clock",kind="fixed_offset",fixed_offset_seconds=offset,iana_zone=(string?)null,tzdb_version=(string?)null});
        var product=Row(new{definition_state="candidate"});
        var series=Row(new{code="test_month",definition_state="candidate"});
        var curve=Row(new{direct_role="trader_mark",direct_source_code="chosen_source"});
        Dictionary<string,ImmutableArray<JsonElement>> references=new(){
            ["futures_milestones"]=[First()],["futures_execution_policies"]=[Policy()],["clock_rules"]=[FixedClock(0)],
            ["sources"]=[Row(new{code="chosen_source",kind="manual_entry",locator="arbitrary:operator-selected-source"}),
                Row(new{code="reviewed_terms",kind="manual_entry",locator="arbitrary:qualified-terms"})]};
        DateTimeOffset Resolve(Dictionary<string,ImmutableArray<JsonElement>>? rows=null,JsonElement? selectedCurve=null)=>
            FuturesExecutionPolicies.Resolve(product,series,selectedCurve??curve,rows??references,FuturesMarketBinding.Implementation);
        Dictionary<string,ImmutableArray<JsonElement>> With(JsonElement policy,JsonElement? first=null,JsonElement? clock=null)=>
            new(references){["futures_execution_policies"]=[policy],["futures_milestones"]=[first??First()],["clock_rules"]=[clock??FixedClock(0)]};
        void Reject(Action action)
        {try{action();}catch(MarketDataException e)when(e.Code=="futures_execution_policy"){return;}throw new Exception("An incomplete or conflicting execution policy was accepted.");}
        if(Resolve()!=DateTimeOffset.Parse("2040-03-15T12:30:00Z"))throw new Exception("The approved bound was replaced by an inferred source event.");
        if(First().GetProperty("event_at").ValueKind!=JsonValueKind.Null)throw new Exception("Date-only facts must remain date-only.");
        Reject(()=>Resolve(new(references){["futures_execution_policies"]=[]}));
        Reject(()=>Resolve(new(references){["futures_execution_policies"]=[Policy(),Policy()]}));
        Reject(()=>Resolve(selectedCurve:Row(new{direct_role="daily_settlement",direct_source_code="chosen_source"})));
        Reject(()=>Resolve(selectedCurve:Row(new{direct_role="trader_mark",direct_source_code="another_source"})));
        Reject(()=>Resolve(new(references){["sources"]=[references["sources"][0]]}));
        Reject(()=>FuturesExecutionPolicies.Resolve(Row(new{definition_state="placeholder"}),series,curve,references,FuturesMarketBinding.Implementation));
        Reject(()=>Resolve(With(Policy(rule:"explicit_utc_bound_v1",offset:null))));
        Reject(()=>Resolve(With(Policy(bound:"2040-03-11T23:59:59Z"))));
        Reject(()=>Resolve(With(Policy(offset:3600))));
        Reject(()=>Resolve(With(Policy(tzdb:"stale"))));
        Reject(()=>Resolve(With(Policy(offset:null))));
        var exact=First(instant:"2040-03-11T13:00:00Z");
        var exactPolicy=Policy("source_first_trade_instant_v1","2040-03-11T13:00:00Z",null);
        if(Resolve(With(exactPolicy,exact))!=DateTimeOffset.Parse("2040-03-11T13:00:00Z"))throw new Exception("Exact source instants must be retained.");
        Reject(()=>Resolve(With(exactPolicy)));
        Reject(()=>Resolve(With(Policy("explicit_utc_bound_v1","2040-03-10T12:30:00Z",null),exact)));
        Reject(()=>Resolve(With(Policy("source_first_trade_instant_v1","2040-03-11T13:00:00Z",0),exact)));
        var unknown=First("unknown",null);
        Resolve(With(Policy("explicit_utc_bound_v1",offset:null),unknown));
        Reject(()=>Resolve(With(Policy(),unknown)));
        Reject(()=>FuturesExecutionPolicies.Resolve(product,series,curve,references,"unsupported"));
        // Independent civil-time oracle: the spring date has23 hours; fall has25.
        var tzdb=TimeZoneDatabase.Installed().Version();
        var chicago=Row(new{code="test_clock",kind="iana",fixed_offset_seconds=(int?)null,iana_zone="America/Chicago",tzdb_version=tzdb});
        foreach(var (day,bound,offset) in new[]{("2026-03-08","2026-03-09T05:00:00Z",-18000),("2026-11-01","2026-11-02T06:00:00Z",-21600)})
        {
            var proof=Policy(bound:bound,offset:offset,tzdb:tzdb);
            if(Resolve(With(proof,First(date:day),chicago))!=DateTimeOffset.Parse(bound))throw new Exception("Wrong DST date boundary.");
            Reject(()=>Resolve(With(Policy(bound:bound,offset:offset+3600,tzdb:tzdb),First(date:day),chicago)));
            Reject(()=>Resolve(With(Policy(bound:DateTimeOffset.Parse(bound).AddSeconds(-1).ToString("O"),offset:offset,tzdb:tzdb),First(date:day),chicago)));
            Reject(()=>Resolve(With(Policy(bound:bound,offset:offset,tzdb:"stale"),First(date:day),chicago)));
        }
        var syntheticProduct=Row(new{definition_state="synthetic"});
        var syntheticSeries=Row(new{code="test_month",definition_state="synthetic"});
        var settledCurve=Row(new{direct_role="daily_settlement",direct_source_code="chosen_source"});
        var missingSyntheticPolicy=new Dictionary<string,ImmutableArray<JsonElement>>(references){["futures_milestones"]=[exact],["futures_execution_policies"]=[]};
        Reject(()=>FuturesExecutionPolicies.Resolve(syntheticProduct,syntheticSeries,settledCurve,missingSyntheticPolicy,FuturesMarketBinding.Implementation));
        Reject(()=>FuturesExecutionPolicies.Resolve(syntheticProduct,syntheticSeries,settledCurve,missingSyntheticPolicy,"fixed_total_cash_execution_pnl_v1"));
        var explicitSyntheticPolicy=Policy("source_first_trade_instant_v1","2040-03-11T13:00:00Z",null,role:"daily_settlement");
        if(FuturesExecutionPolicies.Resolve(syntheticProduct,syntheticSeries,settledCurve,With(explicitSyntheticPolicy,exact),FuturesMarketBinding.Implementation)
            !=DateTimeOffset.Parse("2040-03-11T13:00:00Z"))throw new Exception("Explicit synthetic source policy rejected.");
        // Havana repeats midnight at the fall transition; neither offset is a proof.
        var havana=Row(new{code="test_clock",kind="iana",fixed_offset_seconds=(int?)null,iana_zone="America/Havana",tzdb_version=tzdb});
        foreach(var offset in new[]{-14400,-18000})
            Reject(()=>Resolve(With(Policy(bound:"2026-11-02T12:30:00Z",offset:offset,tzdb:tzdb),First(date:"2026-10-31"),havana)));
        Reject(()=>Resolve(With(Policy(bound:"2026-03-09T12:30:00Z",offset:-18000,tzdb:tzdb),First(date:"2026-03-07"),havana)));
        // Official marks use the same contract with their own declared price role.
        Resolve(With(Policy(role:"daily_settlement")),Row(new{direct_role="daily_settlement",direct_source_code="chosen_source"}));
        Console.WriteLine("PASS standard futures execution: exact/date-only/unknown source facts, qualified DST boundaries, matching source/role and arbitrary source names; no installation-specific admission");
    }
}
