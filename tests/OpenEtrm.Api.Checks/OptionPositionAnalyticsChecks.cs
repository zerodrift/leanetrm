using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;

// Booked option positions with the stored analytics of their series, scaled by multiplier and signed open lots. A figure
// is the backend's or it is missing with its reason; a sum with a missing member is missing, never the sum of the rest.
internal static class OptionPositionAnalyticsChecks
{
    private const string Model="smile_quadratic_log_moneyness_v1",Jan="2027-01-26T19:30:00Z",Feb="2027-02-23T19:30:00Z";
    private static int next;
    private static FuturesOptionPosition Position(string series,string right,string strike,string expiry,string underlying,string lots,string closed="0",string product="synthetic_on",
        string currency="USD",string unit="MMBtu",string multiplier="10000",Guid? referenceRevision=null,Guid? referenceCatalog=null)
    {
        var n=++next;Guid G(int k)=>Guid.Parse($"00000000-0000-0000-0000-{n*100+k:000000000000}");
        JsonElement Row(object row)=>JsonSerializer.SerializeToElement(row);
        return new(Row(new{trade_id=G(1),option_instrument_id=G(2),premium_instrument_id=G(3),definition_revision_id=Guid.Parse("00000000-0000-0000-0000-00000000d001"),revision=1,
                reference_catalog_id=referenceCatalog??Guid.Parse("00000000-0000-0000-0000-00000000c001"),reference_revision_id=referenceRevision??Guid.Parse("00000000-0000-0000-0000-00000000c002"),series_code=series,
                lots,closed_lots=closed,expired=false,trade_reference=n}),
            Row(new{id=Guid.Parse("00000000-0000-0000-0000-00000000b001"),name="Synthetic options"}),
            Row(new{product_code=product,product_name="Synthetic gas options",option_right=right,strike,expiry_at=expiry,underlying_series_code=underlying,multiplier,unit="contract",quote_unit=unit,currency,currency_code=currency.ToLowerInvariant(),quote_quantity_code="synthetic_"+unit,quote_multiplier="1"}));
    }
    private static OptionMeasure Stated(string? value,string? missing=null)=>new(value is null?null:ExactDecimal.Parse(value),missing);
    private static SurfaceSeriesResult Series(string code,string outcome,OptionSeriesSensitivities? analytics)=>
        new(new(code,OptionRight.Call,ExactDecimal.Parse("3"),DateTimeOffset.Parse(Jan,CultureInfo.InvariantCulture),"synthetic_ng_f27",ExactDecimal.Parse("0.2"),outcome,null),null,null,null,false,null){Analytics=analytics};
    private static OptionSeriesSensitivities Solved(string value,string delta,string gamma,string? vega,string theta)=>
        new(Guid.NewGuid(),ExactDecimal.Parse(value),ExactDecimal.Parse("0.01"),Stated(delta),Stated(gamma),vega is null?Stated(null,"sensitivity_unstable"):Stated(vega),Stated(theta));
    private static SurfaceSensitivities Selection(string dayTo)=>new(FuturesOptionSensitivities.Policy,1,2,"0.000001","0.15","0.1",ExactDecimal.Parse("0.01"),"request","UTC",
        DateTimeOffset.Parse(dayTo,CultureInfo.InvariantCulture));
    private static OptionSurfaceLookup Surface(params SurfaceSeriesResult[] series)=>new(new(Model,[],[],[..series]){Sensitivities=Selection("2026-10-17T17:00:00Z")},null);
    private static readonly OptionSurfaceLookup None=new(null,null);

    internal static void Run()
    {
        var call=Position("on_f27_c3","call","3","2027-01-26T19:30:00+00:00","synthetic_ng_f27","2");
        var put=Position("on_f27_p3","put","3","2027-01-26T19:30:00+00:00","synthetic_ng_f27","-3","1");
        var unstable=Position("on_g27_c3","call","3","2027-02-23T19:30:00+00:00","synthetic_ng_g27","1");
        var intrinsic=Position("on_g27_c1","call","1","2027-02-23T19:30:00+00:00","synthetic_ng_g27","1");
        var unfitted=Position("lo_g27_c70","call","70","2027-02-23T19:30:00+00:00","synthetic_cl_g27","4",product:"synthetic_lo",unit:"bbl",multiplier:"1000");
        var closed=Position("on_f27_c4","call","4","2027-01-26T19:30:00+00:00","synthetic_ng_f27","5","5");
        var gas=Surface(Series("on_f27_c3","solved",Solved("0.2","0.5","1.5","0.004","-0.002")),Series("on_f27_p3","solved",Solved("0.15","-0.5","1.5","0.004","-0.0015")),
            Series("on_g27_c3","solved",Solved("0.3","0.55","1.2",null,"-0.001")),
            Series("on_g27_c1","at_intrinsic",new(Guid.NewGuid(),ExactDecimal.Parse("2"),null,Stated(null,"option_at_intrinsic"),Stated(null,"option_at_intrinsic"),
                Stated(null,"option_at_intrinsic"),Stated(null,"option_at_intrinsic"))));
        var read=OptionPositionAnalytics.Build(Model,[call,put,unstable,intrinsic,unfitted,closed],p=>p.Series.Text("product_code")=="synthetic_on"?gas:None);

        var groups=read["groups"]!.AsArray();
        JsonObject Group(string underlying)=>groups.Single(g=>g!["underlyingSeries"]!.GetValue<string>()==underlying)!.AsObject();
        JsonObject Row(string series)=>groups.SelectMany(g=>g!["rows"]!.AsArray()).Single(r=>r!["series"]!.GetValue<string>()==series)!.AsObject();
        decimal? Amount(JsonNode figure)=>figure["amount"]?.GetValue<string>() is {} text?decimal.Parse(text,CultureInfo.InvariantCulture):null;
        string? Missing(JsonNode figure)=>figure["missing"]?.GetValue<string>();
        void Expect(bool condition,string message){if(!condition)throw new Exception(message+" "+read.ToJsonString());}

        // 2 long calls: unit × 10,000 × 2. 2 short puts (3 sold, 1 closed): unit × 10,000 × -2.
        Expect(Amount(Row("on_f27_c3")["value"]!)==4000m&&Amount(Row("on_f27_c3")["delta"]!)==10000m&&Amount(Row("on_f27_c3")["gamma"]!)==30000m&&
            Amount(Row("on_f27_c3")["vega"]!)==80m&&Amount(Row("on_f27_c3")["theta"]!)==-40m&&Row("on_f27_c3")["state"]!.GetValue<string>()=="analysed"&&
            Row("on_f27_c3")["openLots"]!.GetValue<string>()=="2","A long position scales each unit figure by its multiplier and open lots.");
        Expect(Amount(Row("on_f27_p3")["value"]!)==-3000m&&Amount(Row("on_f27_p3")["delta"]!)==10000m&&Amount(Row("on_f27_p3")["gamma"]!)==-30000m&&
            Amount(Row("on_f27_p3")["theta"]!)==30m&&Row("on_f27_p3")["openLots"]!.GetValue<string>()=="-2","A short position's figures take the sign of its open lots.");
        Console.WriteLine("PASS position figures are the series' unit figures times multiplier and signed open lots");

        var january=Group("synthetic_ng_f27")["subtotal"]!;
        Expect(Amount(january["value"]!)==1000m&&Amount(january["delta"]!)==20000m&&Amount(january["gamma"]!)==0m&&Amount(january["vega"]!)==0m&&Amount(january["theta"]!)==-10m&&
            Group("synthetic_ng_f27")["quantityUnit"]!.GetValue<string>()=="MMBtu"&&Group("synthetic_ng_f27")["currency"]!.GetValue<string>()=="USD"&&
            Group("synthetic_ng_f27")["rows"]!.AsArray().Count==2,"One underlying future's rows are subtotalled exactly, and a closed position is left out.");
        Console.WriteLine("PASS one underlying future's positions are subtotalled, without positions that hold no open lots");

        var february=Group("synthetic_ng_g27")["subtotal"]!;
        Expect(Missing(Row("on_g27_c3")["vega"]!)=="sensitivity_unstable"&&Amount(Row("on_g27_c3")["delta"]!)==5500m&&
            Row("on_g27_c1")["state"]!.GetValue<string>()=="unavailable"&&Row("on_g27_c1")["reason"]!.GetValue<string>()=="option_at_intrinsic"&&
            Amount(Row("on_g27_c1")["value"]!)==20000m&&Missing(Row("on_g27_c1")["delta"]!)=="option_at_intrinsic",
            "A missing measure keeps its code, and a series at intrinsic has its value and no sensitivities.");
        Expect(Amount(february["value"]!)==23000m&&Missing(february["delta"]!)=="incomplete"&&Missing(february["gamma"]!)=="incomplete"&&Missing(february["vega"]!)=="incomplete"&&
            Missing(february["theta"]!)=="incomplete"&&Amount(february["delta"]!) is null,"A subtotal with a missing member is missing, not the sum of the rest.");
        Console.WriteLine("PASS a subtotal with a missing member is missing");

        var crude=Row("lo_g27_c70");
        Expect(crude["state"]!.GetValue<string>()=="notCalibrated"&&crude["openLots"]!.GetValue<string>()=="4"&&
            new[]{"value","delta","gamma","vega","theta"}.All(m=>Amount(crude[m]!) is null&&Missing(crude[m]!)=="not_calibrated")&&
            Missing(Group("synthetic_cl_g27")["subtotal"]!["value"]!)=="incomplete"&&Group("synthetic_cl_g27")["quantityUnit"]!.GetValue<string>()=="bbl",
            "A product with no surface here shows its lots and no figures.");
        Console.WriteLine("PASS a product not calibrated at this close shows lots and no figures");

        // Equal labels do not establish currency identity, nor does a repeated series code establish one holding.
        foreach(var other in new[]{Position("on_f27_c3","call","3",Jan,"synthetic_ng_f27","1",referenceRevision:Guid.NewGuid()),
            Position("on_f27_c3","call","3",Jan,"synthetic_ng_f27","1",referenceCatalog:Guid.NewGuid()),
            Position("on_f27_c3","call","3",Jan,"synthetic_ng_f27","1",currency:"GBP"),
            Position("on_f27_c3","call","3",Jan,"synthetic_ng_f27","1",currency:"EUR"),
            Position("on_f27_c3","call","3",Jan,"synthetic_ng_f27","1",currency:"CAD")})
        {
            var separate=OptionPositionAnalytics.Build(Model,[call,other],_=>gas);
            Expect(separate["totals"]!.AsArray().Count==2,"Distinct exact currencies cannot share an option cash total.");
            Expect(separate["totals"]!.AsArray().All(t=>t!["currencyReference"] is not null),"Cash totals retain exact currency references.");
            if(other.Reference!=call.Reference)
                Expect(separate["held"]!.AsArray().Count==2&&separate["held"]!.AsArray().All(h=>h!["seriesReference"] is not null),
                    "Identically named series in different exact references retain separate held lots.");
        }
        Console.WriteLine("PASS option totals and held lots preserve exact currency and series identity across equal labels");

        var totals=read["totals"]!.AsArray();
        Expect(totals.Count==1&&totals[0]!["currency"]!.GetValue<string>()=="USD"&&Missing(totals[0]!["value"]!)=="incomplete"&&Missing(totals[0]!["theta"]!)=="incomplete"&&
            totals[0]!.AsObject().Select(p=>p.Key).Order().SequenceEqual(new[]{"currency","currencyReference","theta","value"}),"Value and theta total per currency; sensitivities to different futures are never added.");
        var gasOnly=OptionPositionAnalytics.Build(Model,[call,put],_=>gas)["totals"]!.AsArray();
        Expect(gasOnly.Count==1&&Amount(gasOnly[0]!["value"]!)==1000m&&Amount(gasOnly[0]!["theta"]!)==-10m,"Complete rows total exactly.");
        Console.WriteLine("PASS value and theta total per currency, and only when every member has one");

        // What a chain shows beside a series: the open lots held in it, added across the positions that hold it.
        var again=Position("on_f27_c3","call","3","2027-01-26T19:30:00+00:00","synthetic_ng_f27","-5");
        var held=OptionPositionAnalytics.Build(Model,[call,again,put,closed],_=>None)["held"]!.AsArray().Select(h=>(h!["series"]!.GetValue<string>(),h!["openLots"]!.GetValue<string>())).ToArray();
        Expect(held.SequenceEqual(new[]{("on_f27_c3","-3"),("on_f27_p3","-2")}),"Held lots are the exact sum of open lots per series, without closed positions.");
        Console.WriteLine("PASS held lots per series are the exact sum of its positions' open lots");

        // A product whose inputs cannot be read at this close is said so on its rows; the other products are still read.
        var unreadable=OptionPositionAnalytics.Build(Model,[call,unfitted],p=>p.Series.Text("product_code")=="synthetic_on"?gas:new(null,"market_inputs_missing"));
        var blocked=unreadable["groups"]!.AsArray().SelectMany(g=>g!["rows"]!.AsArray()).Single(r=>r!["series"]!.GetValue<string>()=="lo_g27_c70")!;
        Expect(blocked["state"]!.GetValue<string>()=="unavailable"&&blocked["reason"]!.GetValue<string>()=="market_inputs_missing"&&Missing(blocked["value"]!)=="market_inputs_missing"&&
            Amount(unreadable["groups"]!.AsArray().SelectMany(g=>g!["rows"]!.AsArray()).Single(r=>r!["series"]!.GetValue<string>()=="on_f27_c3")!["value"]!)==4000m,
            "A product whose surface cannot be read is a row state, not a failed read.");
        Console.WriteLine("PASS a product whose surface cannot be read at this close is a row state");

        // Each group states the selection its figures were fitted under and the day its theta runs to. Thetas that run to
        // different days are not one sum.
        var otherDay=new OptionSurfaceLookup(gas.Surface! with{Sensitivities=Selection("2026-10-17T21:00:00Z")},null);
        var crudeFitted=Position("lo_g27_c70b","call","70","2027-02-23T19:30:00+00:00","synthetic_cl_g27","1",product:"synthetic_lo",unit:"bbl",multiplier:"1000");
        var crudeSurface=new OptionSurfaceLookup(new VolSurfaceResult(Model,[],[],[Series("lo_g27_c70b","solved",Solved("1.5","0.4","0.01","0.05","-0.02"))]){Sensitivities=Selection("2026-10-17T21:00:00Z")},null);
        var mixed=OptionPositionAnalytics.Build(Model,[call,crudeFitted],p=>p.Series.Text("product_code")=="synthetic_on"?gas:crudeSurface);
        var same=OptionPositionAnalytics.Build(Model,[call,crudeFitted],p=>p.Series.Text("product_code")=="synthetic_on"?otherDay:crudeSurface);
        Expect(Group("synthetic_ng_f27")["sensitivities"]!["dayTo"]!.GetValue<DateTimeOffset>()==DateTimeOffset.Parse("2026-10-17T17:00:00Z",CultureInfo.InvariantCulture)&&
            Group("synthetic_ng_f27")["sensitivities"]!["policy"]!.GetValue<string>()==FuturesOptionSensitivities.Policy&&Group("synthetic_cl_g27")["sensitivities"] is null,
            "A group states the selection of its surface, and none where there is no surface.");
        Expect(Amount(mixed["totals"]![0]!["value"]!)==5500m&&Missing(mixed["totals"]![0]!["theta"]!)=="theta_days_differ"&&
            Amount(same["totals"]![0]!["theta"]!)==-60m,"Thetas for different days are not added; thetas for one day are.");
        Console.WriteLine("PASS a theta total needs every member to run to the same day");

        Expect(read["model"]!.GetValue<string>()==Model&&groups.Select(g=>g!["underlyingSeries"]!.GetValue<string>()).SequenceEqual(new[]{"synthetic_ng_f27","synthetic_cl_g27","synthetic_ng_g27"}),
            "Groups are ordered by expiry, then underlying.");
        Console.WriteLine("PASS groups run by expiry and underlying future");
    }
}
