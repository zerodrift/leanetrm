using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

public static class StructuredPowerChecks
{
    public static int Run()
    {
        var f=Fixture();var data=f.Data;var terms=f.Terms;var from=DateTimeOffset.Parse("2040-02-01T00:00:00Z");var to=from.AddYears(1);
        var resolved=StructuredPowerPreview.ResolveTerms(data,terms,from,to);
        var catalog=StructuredPowerPreview.Catalog(data,true,true);
        Equal(catalog.EnergyQuantities.Single(x=>x.Code=="energy").Label,"energy");
        Equal(resolved.CanonicalTerms,terms);Equal(resolved.Battery!.Limits.MaximumStoredEnergy.Amount.ToString(),"20");
        Reject("structured_power_pin",()=>StructuredPowerPreview.ResolveTerms(data,terms with{ReferenceRevisionId=Guid.NewGuid()},from,to));
        Reject("structured_power_reference",()=>StructuredPowerPreview.ResolveTerms(data,terms with{FacilityCode="absent"},from,to));
        Reject("structured_power_union",()=>StructuredPowerPreview.ResolveTerms(data,terms with{Solar=new("delivered")},from,to));
        Reject("structured_power_policy",()=>StructuredPowerPreview.ResolveTerms(data,terms with{Battery=terms.Battery! with{CapacityPolicy="degrading"}},from,to));
        Reject("structured_power_policy",()=>StructuredPowerPreview.ResolveTerms(data,terms with{Battery=terms.Battery! with{ThroughputPolicy="uncapped",MaximumThroughputMwh="1"}},from,to));
        Reject("battery_efficiency",()=>StructuredPowerPreview.ResolveTerms(data,terms with{Battery=terms.Battery! with{ChargeEfficiency="0"}},from,to));
        Reject("structured_power_time",()=>StructuredPowerPreview.ResolveTerms(data,terms,to,from));
        var duplicate=data.References.ToDictionary(x=>x.Key,x=>x.Value);duplicate["locations"]=duplicate["locations"].Add(duplicate["locations"][0]);
        Reject("structured_power_reference",()=>StructuredPowerPreview.ResolveTerms(NewData(f.Manifest,duplicate),terms,from,to));
        var json=JsonSerializer.Serialize(terms,Options);RejectJson(json.Replace("\"kind\":\"battery\"","\"kind\":\"battery\",\"fallback\":true"));
        RejectJson(json.Replace("\"maximumStoredMwh\":\"20\",", ""));
        Calculations(f,from,to);
        OperatingCompositionChecks.Run();
        OperatingSideSelectionChecks.Run();
        Console.WriteLine("PASS strict structured power facility references, variants, policies and required JSON fields");return 27;
    }
    static void RejectJson(string json){try{JsonSerializer.Deserialize<StructuredPowerFacilityTerms>(json,Options);throw new Exception("Expected closed JSON rejection.");}catch(JsonException){}}
    static readonly JsonSerializerOptions Options=new(JsonSerializerDefaults.Web);
    static void Equal<T>(T actual,T expected){if(!Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}.");}
    static void Reject(string code,Action run){try{run();throw new Exception("Expected "+code);}catch(MarketDataException e)when(e.Code==code){}}
    public sealed record TestFixture(PreviewManifest Manifest,PreviewData Data,StructuredPowerFacilityTerms Terms);
    public static TestFixture Fixture()
    {
        var cat=Guid.Parse("f0df1a57-0cae-4464-8df9-73994e69caca");var rev=Guid.Parse("4cc6c754-9a70-44da-a457-a18ab5dc4521");
        var rcat=Guid.Parse("1d795743-2b7d-4ec1-9e42-9c5f115c8334");var rrev=Guid.Parse("73335e64-3411-4723-bab7-8e28f9b5921c");
        var manifest=new PreviewManifest("local-synthetic-preview",cat,rev,rcat,rrev,Guid.NewGuid(),Guid.NewGuid(),[],null);
        Dictionary<string,ImmutableArray<JsonElement>> refs=new()
        {
            ["quantity_specifications"]=[Q("power","megawatt","power"),Q("energy","megawatt_hour","energy"),Q("right","resource_right","count"),Row(new{code="fuel",commodity_code="gas",basis_code="energy",unit_code="megawatt_hour",dimension_code="energy"})],
            ["quote_specifications"]=[Price("energy_price","energy"),Price("fuel_price","fuel"),Price("right_price","right")],
            ["trading_close_times"]=[],["currencies"]=[Row(new{code="usd",display_code="USD"})],["settlement_targets"]=[],["value_specifications"]=[],["futures_basis_settlements"]=[],
            ["locations"]=[Row(new{code="synthetic_facility",clock_code="utc"})],
            ["clock_rules"]=[Row(new{code="utc",kind="fixed_offset",fixed_offset_seconds=0,iana_zone=(string?)null,tzdb_version=(string?)null})],
            ["commodities"]=[Row(new{code="power"}),Row(new{code="gas"})],
            ["measurement_bases"]=[Row(new{code="power"}),Row(new{code="energy"}),Row(new{code="count",mass_power=0,length_power=0,time_power=0,temperature_power=0,amount_power=0,current_power=0,luminous_power=0})],
            ["dimensions"]=[Row(new{code="power"}),Row(new{code="energy"}),Row(new{code="count",mass_power=0,length_power=0,time_power=0,temperature_power=0,amount_power=0,current_power=0,luminous_power=0})],
            ["units"]=[Row(new{code="megawatt",symbol="MW",dimension_code="power"}),Row(new{code="megawatt_hour",symbol="MWh",dimension_code="energy"}),Row(new{code="resource_right",symbol="right",dimension_code="count"})]
        };
        var terms=new StructuredPowerFacilityTerms("battery",cat,rev,rcat,rrev,"synthetic_facility","utc",60,"power","energy",null,
            new("10","10","0","20","0.9","0.9","grid_side_ac","zero_auxiliary_and_self_discharge","fixed","prohibited","grid","uncapped",null),null,null,"structured_power_facility_v1");
        return new(manifest,NewData(manifest,refs),terms);
    }
    static void Calculations(TestFixture f,DateTimeOffset from,DateTimeOffset to)
    {
        var frame=new InputFrame(Guid.Parse("b0edf4c6-f994-4a0e-8b63-e94d59a4d8d2"),"Synthetic operating preview",from.AddDays(-1),DateOnly.FromDateTime(from.AddDays(-1).UtcDateTime),[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty);
        var saved=new StructuredPowerFacilityVersion(Guid.Parse("c7f520fb-9ad7-4023-b456-e5d04a740373"),1,"2040-02-01T00:00:00Z","2041-02-01T00:00:00Z",f.Terms);
        var valuation=new StructuredPowerValuationInput("resource_right_holder_v1","remaining_operating_value_v1","usd","right",false,
            new("monthly","days_after_period_end",18,null,"calendar_days",null,"unadjusted",null,OpenEtrm.MarketData.Trading.PaymentRule.Implementation),
            new("fixed","energy_price","100",null,null),new("fixed","energy_price","20",null,null),null,null,"right_price","5","preview_horizon_once_v1","1","grid_charge_plus_discharge",null,null,null);
        var request=new StructuredPowerPreviewRequest(frame.Id,saved.Id,1,new("2040-02-01",1),new("2040-02-01T00:00:00Z","0","0","0","0"),
            [new("10","0","1"),new("0","8.1","1")],null,[],null,[],valuation);
        var result=StructuredPowerPreview.Calculate(f.Data,frame,saved,frame.At,request);
        Equal(result.Valuation!.Value.Numerator,"5869");Equal(result.Valuation.Value.Denominator,"10");
        var malformed=f.Data.References.ToDictionary(x=>x.Key,x=>x.Value);
        malformed["dimensions"]=malformed["dimensions"].Where(x=>x.GetProperty("code").GetString()!="count").Append(Row(new{code="count",mass_power=1,length_power=0,time_power=0,temperature_power=0,amount_power=0,current_power=0,luminous_power=0})).ToImmutableArray();
        Reject("structured_power_reference",()=>StructuredPowerPreview.Calculate(NewData(f.Manifest,malformed),frame,saved,frame.At,request));
        malformed["dimensions"]=malformed["dimensions"].Where(x=>x.GetProperty("code").GetString()!="count").Append(Row(new{code="count",mass_power=0})).ToImmutableArray();
        Reject("structured_power_reference",()=>StructuredPowerPreview.Calculate(NewData(f.Manifest,malformed),frame,saved,frame.At,request));
        Equal(result.Valuation.Sides.Length,4);Equal(result.ClosingState!.Quantities[0].Quantity.Numerator,"0");
        Equal(result.Valuation.Sides[0].Rows[0].State,OpenEtrm.MarketData.Power.VolumeState.Forecast);
        Equal(result.Valuation.Sides[0].Rows[0].PriceUnitLabel,"USD/MWh");
        Equal(result.Valuation.Sides[0].Rows[0].EvidenceId,null);
        Equal(JsonSerializer.Serialize(result),JsonSerializer.Serialize(StructuredPowerPreview.Calculate(f.Data,frame,saved,frame.At,request)));
        Reject("battery_simultaneous",()=>StructuredPowerPreview.Calculate(f.Data,frame,saved,frame.At,request with{BatteryDispatch=[new("1","1","1")]}));
        Reject("structured_power_context",()=>StructuredPowerPreview.Calculate(f.Data,frame,saved,frame.At,request with{SnapshotId=Guid.NewGuid()}));
        Reject("structured_power_policy",()=>StructuredPowerPreview.Calculate(f.Data,frame,saved,frame.At,request with{Valuation=valuation with{VariableQuantityBasis=null}}));
        Reject("structured_power_reference",()=>StructuredPowerPreview.Calculate(f.Data,frame,saved,frame.At,request with{Valuation=valuation with{RightQuantityCode="energy"}}));
        Reject("structured_power_unavailable",()=>StructuredPowerPreview.Calculate(f.Data,frame,saved,frame.At,request with{Valuation=valuation with{Discounting=true}}));
        var opening=request with{BatteryState=new("2040-02-01T00:00:00Z","10","0","0","0"),BatteryDispatch=[new("0","9","1")],Valuation=valuation with{AccessPrice=null,AccessQuoteCode=null,AccessFeeBasis=null,VariablePrice=null,VariableQuantityBasis=null}};
        Equal(StructuredPowerPreview.Calculate(f.Data,frame,saved,frame.At,opening).Valuation!.Value.Numerator,"900");
        var solar=saved with{Terms=f.Terms with{Kind="solar",PowerQuantityCode=null,Battery=null,Solar=new("delivered")}};
        var solarPrice=valuation with{ChargingPrice=null,AccessQuoteCode=null,AccessPrice=null,AccessFeeBasis=null,VariablePrice=null,VariableQuantityBasis=null};
        var solarRequest=request with{BatteryState=null,BatteryDispatch=[],SolarRight=new("hypothetical_supplied_forecast_v1","1","2","as_produced","produced_only"),SolarDispatch=[new("10",null)],Valuation=solarPrice};
        var solarResult=StructuredPowerPreview.Calculate(f.Data,frame,solar,frame.At,solarRequest);Equal(solarResult.Valuation!.Value.Numerator,"500");
        Equal(solarResult.Intervals[0].Quantities.Single(x=>x.Name=="eligible_actual_energy").Quantity.Numerator,"0");
        var firm=solarRequest with{SolarRight=solarRequest.SolarRight! with{DeliveryBasis="fixed_profile",PaymentBasis="delivery_obligation"},SolarDispatch=[new("10","12")],Valuation=null};
        Reject("structured_power_unavailable",()=>StructuredPowerPreview.Calculate(f.Data,frame,solar,frame.At,firm with{Valuation=solarPrice}));
        var firmResult=StructuredPowerPreview.Calculate(f.Data,frame,solar,frame.At,firm);Equal(firmResult.Valuation,null);Equal(firmResult.Intervals[0].Quantities.Single(x=>x.Name=="replacement_energy").Quantity.Numerator,"7");
        Equal(StructuredPowerPreview.Calculate(f.Data,frame,solar,frame.At.AddHours(1),solarRequest).Valuation!.Value.Numerator,"500");
        var thermal=saved with{Terms=f.Terms with{Kind="thermal",Battery=null,FuelQuantityCode="fuel",Thermal=new("0","10","100","100","10","10",0,0,0,3,"1.5","0","1")}};
        var thermalPrice=solarPrice with{FuelPrice=new("fixed","fuel_price","10",null,null),FuelQuantityBasis="guaranteed"};
        var thermalRequest=request with{BatteryState=null,BatteryDispatch=[],ThermalState=new("2040-02-01T00:00:00Z","offline",60,"0",0,"2","1","3","require_minimum_times_satisfied","thermal_exact_elapsed_quantity_v1"),ThermalDispatch=[new("online","10","10","2040-02-01T00:00:00Z")],Valuation=thermalPrice};
        var thermalResult=StructuredPowerPreview.Calculate(f.Data,frame,thermal,frame.At,thermalRequest);
        Equal(thermalResult.Valuation!.Value.Numerator,"760");
        Equal(thermalResult.ClosingState!.Quantities.Single(x=>x.Name=="previous_output").Quantity.Numerator,"10");
        Equal(StructuredPowerPreview.Calculate(f.Data,frame,thermal,frame.At,thermalRequest with{Valuation=thermalPrice with{FuelQuantityBasis="consumption"}}).Valuation!.Value.Numerator,"840");
        Console.WriteLine("PASS supplied battery/thermal/solar plans, exact gross side valuation, carried opening state and deterministic context");
    }
    static JsonElement Price(string code,string quantity)=>Row(new{code,kind="outright",currency_code="usd",quantity_code=quantity,multiplier="1",trading_tick="0.001",clearing_increment="0.001",minimum="-1000",maximum="1000"});
    static JsonElement Q(string code,string unit,string dimension)=>Row(new{code,commodity_code="power",basis_code=dimension=="power"?"energy":dimension,unit_code=unit,dimension_code=dimension});
    public static JsonElement Row(object value)=>JsonSerializer.SerializeToElement(value);
    static PreviewData NewData(PreviewManifest manifest,IReadOnlyDictionary<string,ImmutableArray<JsonElement>> refs)
    {
        var definitions=new Dictionary<string,ImmutableArray<JsonElement>>();
        foreach(var table in new[]{"contracts","curves","curve_members","series","curve_inputs","constituent_mappings","rate_curves","rate_curve_points","revision_periods","contract_intervals","curve_expiry_rules"})definitions[table]=[];
        definitions["revisions"]=[Row(new{catalog_id=manifest.CatalogId,id=manifest.RevisionId,reference_catalog_id=manifest.ReferenceCatalogId,reference_revision_id=manifest.ReferenceRevisionId,state="sealed"})];
        return new(manifest,definitions,refs,[]);
    }
}
