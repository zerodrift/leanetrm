using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;

static class FuturesAdmissionChecks
{
    public static void Run()
    {
        JsonElement Row(object value)=>JsonSerializer.SerializeToElement(value);
        var product=Row(new{code="synthetic_source_product",definition_state="candidate"});
        var series=Row(new{code="synthetic_source_series",definition_state="candidate"});
        var curve=Row(new{direct_role="trader_mark",direct_source_code="generated_closes"});
        var first=Row(new{series_code="synthetic_source_series",milestone="first_trading",value_state="known",business_date="2040-03-11",event_at=(string?)null,clock_code="utc"});
        var admission=Row(new{series_code="synthetic_source_series",implementation="contract_lot_execution_pnl_v2",usage_policy="generated_prices_sample_trades_v1",execution_venue="central_limit_order_book",eligibility_rule="after_source_first_trade_date_v1",eligible_from="2040-03-12T00:00:00Z",qualification_source_code="review",eligibility_offset_seconds="0",eligibility_tzdb_version=(string?)null});
        Dictionary<string,ImmutableArray<JsonElement>> references=new(){
            ["futures_milestones"]=[first],["futures_execution_admissions"]=[admission],
            ["sources"]=[Row(new{code="generated_closes",kind="synthetic",locator="generated:pilot-close-v1/synthetic",content_sha256=new string('a',64)}),Row(new{code="review",kind="manual_entry",locator="operator:synthetic-qualification",content_sha256=new string('b',64)})],
            ["clock_rules"]=[Row(new{code="utc",kind="fixed_offset",fixed_offset_seconds="0",iana_zone=(string?)null,tzdb_version=(string?)null})]};
        void Reject(Action action)
        {
            try{action();}catch(MarketDataException e) when(e.Code=="futures_admission"){return;}
            throw new Exception("Accepted unqualified sourced futures terms");
        }
        Reject(()=>FuturesAdmission.Require(product,series,curve,new Dictionary<string,ImmutableArray<JsonElement>>(references){["futures_execution_admissions"]=[]},FuturesMarketBinding.GeneralImplementation));
        FuturesAdmission.Require(product,series,curve,references,FuturesMarketBinding.GeneralImplementation);
        if(FuturesAdmission.EligibleFrom(series,references)!=DateTimeOffset.Parse("2040-03-12T00:00:00Z"))throw new Exception("Conservative source-date eligibility changed");
        Reject(()=>FuturesAdmission.Require(product,series,Row(new{direct_role="daily_settlement",direct_source_code="generated_closes"}),references,FuturesMarketBinding.GeneralImplementation));
        Reject(()=>FuturesAdmission.Require(product,series,curve,references,FuturesMarketBinding.Implementation));
        Reject(()=>FuturesAdmission.Require(Row(new{definition_state="placeholder"}),series,curve,references,FuturesMarketBinding.GeneralImplementation));
        Reject(()=>FuturesAdmission.EligibleFrom(series,new Dictionary<string,ImmutableArray<JsonElement>>(references){["futures_execution_admissions"]=[]}));
        Reject(()=>FuturesAdmission.Require(product,series,curve,new Dictionary<string,ImmutableArray<JsonElement>>(references){["futures_execution_admissions"]=[Row(new{series_code="synthetic_source_series",implementation="contract_lot_execution_pnl_v2",usage_policy="generated_prices_sample_trades_v1",execution_venue="block"})]},FuturesMarketBinding.GeneralImplementation));
        var wrong=Row(new{series_code="synthetic_source_series",implementation="contract_lot_execution_pnl_v2",usage_policy="generated_prices_sample_trades_v1",execution_venue="central_limit_order_book",eligibility_rule="after_source_first_trade_date_v1",eligible_from="2040-03-11T00:00:00Z",qualification_source_code="review",eligibility_offset_seconds="0",eligibility_tzdb_version=(string?)null});
        Reject(()=>FuturesAdmission.EligibleFrom(series,new Dictionary<string,ImmutableArray<JsonElement>>(references){["futures_execution_admissions"]=[wrong]}));
        var easternFirst=Row(new{series_code="synthetic_source_series",milestone="first_trading",value_state="known",business_date="2026-03-08",event_at=(string?)null,clock_code="eastern"});
        var easternReceipt=Row(new{series_code="synthetic_source_series",implementation="contract_lot_execution_pnl_v2",usage_policy="generated_prices_sample_trades_v1",eligibility_rule="after_source_first_trade_date_v1",eligible_from="2026-03-09T04:00:00Z",eligibility_offset_seconds="-14400",eligibility_tzdb_version=TimeZoneDatabase.Installed().Version()});
        var eastern=new Dictionary<string,ImmutableArray<JsonElement>>(references){["futures_milestones"]=[easternFirst],["futures_execution_admissions"]=[easternReceipt],["clock_rules"]=[Row(new{code="eastern",kind="iana",fixed_offset_seconds=(int?)null,iana_zone="America/New_York",tzdb_version=TimeZoneDatabase.Installed().Version()})]};
        if(FuturesAdmission.EligibleFrom(series,eastern)!=DateTimeOffset.Parse("2026-03-09T04:00:00Z"))throw new Exception("DST-qualified next date changed");
        var stale=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(easternReceipt.GetRawText())!;stale["eligibility_offset_seconds"]=Row("-18000");
        Reject(()=>FuturesAdmission.EligibleFrom(series,new Dictionary<string,ImmutableArray<JsonElement>>(eastern){["futures_execution_admissions"]=[Row(stale)]}));
        // Exact source instants remain exact; a native date never becomes a fabricated event instant.
        var precise=Row(new{series_code="synthetic_source_series",milestone="first_trading",value_state="known",business_date="2040-03-11",event_at="2040-03-10T23:50:00Z",clock_code="utc"});
        if(FuturesAdmission.EligibleFrom(series,new Dictionary<string,ImmutableArray<JsonElement>>(references){["futures_milestones"]=[precise],["futures_execution_admissions"]=[]})!=DateTimeOffset.Parse("2040-03-10T23:50:00Z"))throw new Exception("Exact first trade instant changed");
        Console.WriteLine("PASS sourced futures admission: exact allowlist, generated marks only, explicit eligibility and no invented first-trade instant");
    }
}
