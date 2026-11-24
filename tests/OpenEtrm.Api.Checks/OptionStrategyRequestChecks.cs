using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

// What a strategy analysis may ask for and what its setup offers, without a database: every member of a request is
// stated and within the limits or the request is refused, the asynchronous channel admits the same typed request, the
// offer is drawn from a stored surface alone, and the strategy's figures are its legs' scaled and added.
internal static class OptionStrategyRequestChecks
{
    private static readonly DateTimeOffset At=DateTimeOffset.Parse("2026-10-16T17:00:00Z",CultureInfo.InvariantCulture),Expiry=DateTimeOffset.Parse("2026-10-20T19:30:00Z",CultureInfo.InvariantCulture);
    private static readonly ClockRule Utc=new("UTC","fixed_offset",0,null,null);
    private static readonly FuturesReadRequest Context=new(Guid.Parse("00000000-0000-0000-0000-00000000a001"),FuturesOptionStore.Convention,"current");
    private static ExactDecimal D(string value)=>ExactDecimal.Parse(value);
    private static OptionStrategyLegRequest Leg(string series,string side="buy",int lots=1,string entry="0.08")=>new(series,side,lots,D(entry));
    internal static OptionStrategyAnalyzeRequest Request()=>new(Context,"synthetic_on",Expiry,[Leg("c3"),Leg("p3","sell",2)],new(D("2.5"),D("3.5"),21),[At,At.AddDays(1)],D("5"),
        FuturesOptionValuationPolicy.Release2,new(FuturesOptionSensitivities.Policy,TradingPnlReport.Offered,OptionSurfaceAnalytics.VolatilityBump,Utc),OptionStrategies.ExerciseRule);

    internal static void Run()
    {
        var selected=OptionStrategyRead.Validate(Request());
        if(string.Join(' ',selected.Legs.Select(l=>$"{l.Lots}×{l.Series}@{l.EntryPrice}"))!="1×c3@0.08 -2×p3@0.08"||selected.Prices.Length!=21||selected.Prices[0]!=D("2.5")||selected.Prices[1]!=D("2.55")||
            selected.Prices[^1]!=D("3.5")||selected.VolatilityShift!=D("0.05"))throw new Exception($"The selection is not the request's: {selected}.");
        Console.WriteLine("PASS a complete request selects signed lots, even price points and a volatility shift in points");

        var request=Request();
        foreach(var (code,invalid) in new (string,OptionStrategyAnalyzeRequest?)[]
        {
            ("option_context",null),("option_context",request with{Context=null!}),("strategy_product",request with{Product=" "}),
            ("strategy_legs",request with{Legs=[]}),("strategy_legs",request with{Legs=default}),("strategy_legs",request with{Legs=[Leg("c3"),Leg("c3","sell")]}),("strategy_legs",request with{Legs=[Leg("")]}),
            ("strategy_leg_side",request with{Legs=[Leg("c3","long")]}),("strategy_leg_lots",request with{Legs=[Leg("c3",lots:0)]}),("strategy_leg_lots",request with{Legs=[Leg("c3",lots:-1)]}),
            ("strategy_leg_lots",request with{Legs=[Leg("c3",lots:OptionStrategyRead.MaxLots+1)]}),("strategy_leg_entry",request with{Legs=[Leg("c3",entry:"-0.01")]}),
            ("strategy_price_range",request with{Prices=null!}),("strategy_price_range",request with{Prices=new(D("0"),D("3.5"),21)}),("strategy_price_range",request with{Prices=new(D("3.5"),D("2.5"),21)}),
            ("strategy_price_range",request with{Prices=new(D("2.5"),D("3.5"),1)}),("strategy_price_range",request with{Prices=new(D("2.5"),D("3.5"),4)}),
            ("strategy_curve_instant",request with{Instants=default}),("strategy_curve_instant",request with{Instants=[At.AddDays(1),At]}),("strategy_curve_instant",request with{Instants=[At,At]}),
            ("strategy_volatility_shift",request with{VolatilityShiftPoints=D("100.01")}),("strategy_volatility_shift",request with{VolatilityShiftPoints=D("-101")}),
            ("strategy_volatility_shift",request with{VolatilityShiftPoints=D("0.005")}),
            ("option_policy",request with{Valuation=FuturesOptionValuationPolicy.Release2 with{CalibrationFallback=FuturesOptionValuationPolicy.NoFallback}}),
            ("option_policy",request with{Valuation=FuturesOptionValuationPolicy.Release2 with{NumericalPolicy="synthetic_other_policy"}}),("option_policy",request with{Valuation=null!}),
            // The policy offered before European series were admitted is no longer the offered one.
            ("option_policy",request with{Valuation=FuturesOptionValuationPolicy.Release1}),
            ("strategy_expiry_rule",request with{ExpiryRule="synthetic_other_rule"}),("option_sensitivity_policy",request with{Sensitivities=null!}),
            ("option_sensitivity_policy",request with{Sensitivities=request.Sensitivities with{VolatilityBump="0.02"}}),
            ("option_sensitivity_policy",request with{Sensitivities=request.Sensitivities with{Bump=TradingPnlReport.Offered with{Ticks=0}}})
        })Reject(code,()=>OptionStrategyRead.Validate(invalid));
        Console.WriteLine("PASS a request missing a member, or naming another policy, rule, side or range, is refused with its code");

        var atLimits=request with{Legs=[..Enumerable.Range(0,OptionStrategyRead.MaxLegs).Select(n=>Leg($"c{n}"))],Prices=new(D("2"),D("4"),OptionStrategyRead.MaxPoints),
            Instants=[..Enumerable.Range(0,OptionStrategyRead.MaxCurves).Select(n=>At.AddDays(n))],VolatilityShiftPoints=D("-100")};
        OptionStrategyRead.Validate(atLimits);
        Reject(OptionStrategyRead.Limit,()=>OptionStrategyRead.Validate(atLimits with{Legs=atLimits.Legs.Add(Leg("c9"))}));
        Reject(OptionStrategyRead.Limit,()=>OptionStrategyRead.Validate(atLimits with{Prices=new(D("2"),D("4.1"),OptionStrategyRead.MaxPoints+1)}));
        Reject(OptionStrategyRead.Limit,()=>OptionStrategyRead.Validate(atLimits with{Instants=atLimits.Instants.Add(At.AddDays(9))}));
        if(OptionStrategyRead.Limits.ToJsonString()!="""{"legs":4,"points":41,"curves":3,"lots":100000,"volatilityShiftPoints":100}""")throw new Exception($"The stated limits changed: {OptionStrategyRead.Limits}.");
        Console.WriteLine("PASS a request at the stated limits is accepted and one beyond any of them is refused, not cut down");

        var channel=Guid.NewGuid();
        var wire=JsonSerializer.SerializeToNode(request,AsyncReadContract.Json)!.AsObject();
        ReadInput Decode(JsonNode body)=>AsyncReadContract.Decode(new(channel,Guid.NewGuid(),"options.strategy.analyze.v1",JsonSerializer.SerializeToElement(body)));
        var admitted=Decode(wire);
        if(admitted.Body is not OptionStrategyAnalyzeRequest decoded||decoded.Legs.Length!=2||decoded.Legs[1]!=request.Legs[1]||decoded.Prices!=request.Prices||!decoded.Instants.SequenceEqual(request.Instants)||
            decoded.Valuation!=request.Valuation||decoded.Sensitivities!=request.Sensitivities||admitted.SnapshotId!=Context.SnapshotId||admitted.Context!=Context||
            !admitted.Permissions.SequenceEqual(["ReadTrades","ReadMarketData"]))throw new Exception("The channel must admit the exact typed analysis under its context and both read permissions.");
        foreach(var member in new[]{"legs","prices","instants","volatilityShiftPoints","valuation","sensitivities","expiryRule","expiryAt","product"})
        {
            var partial=wire.DeepClone().AsObject();partial.Remove(member);
            try{Decode(partial);}catch(JsonException){continue;}
            throw new Exception($"An analysis without '{member}' must not be admitted.");
        }
        var extra=wire.DeepClone().AsObject();extra["book"]=true;
        try{Decode(extra);throw new Exception("An analysis with an unknown member must not be admitted.");}catch(JsonException){}
        var unpinned=wire.DeepClone().AsObject();unpinned["context"]!["snapshotId"]=Guid.Empty;
        try{Decode(unpinned);throw new Exception("An analysis without a snapshot must not be admitted.");}catch(AsyncReadException e)when(e.Code=="async_request"){}
        Console.WriteLine("PASS the asynchronous channel admits the same complete typed analysis and nothing less");

        Offers();
        Composes();
    }

    private static readonly string[] Strikes=["2.6","2.7","2.8","2.9","3","3.1","3.2","3.3","3.4"];
    private static VolSurfaceResult Surface(string? atTheMoney="3",Func<SurfaceSeries,bool>? listed=null)
    {
        var series=Strikes.SelectMany(strike=>new[]{OptionRight.Call,OptionRight.Put}.Select(right=>new SurfaceSeries($"{(right==OptionRight.Call?'c':'p')}{strike}",right,D(strike),Expiry,"synthetic_ng_x26",
            right==OptionRight.Call&&strike=="3.4"?null:D("0.08"),"solved",null))).Where(s=>listed?.Invoke(s)??true);
        return new("smile_quadratic_log_moneyness_v1",[..Strikes.Select(D)],[new(Expiry,"synthetic_ng_x26",LatticeDecimal.Parse("3.01"),"fitted",null,0,null,null,[],atTheMoney is null?null:D(atTheMoney))],
            [..series.Select(s=>new SurfaceSeriesResult(s,null,null,null,false,null))]);
    }

    private static void Offers()
    {
        var offer=OptionStrategyRead.Offer(Surface(),Expiry,At,Expiry,Utc,Utc,OptionStrategyRead.SurfaceClock);
        string Legs(JsonNode offered,string id)=>offered["templates"]!.AsArray().Single(t=>t!["id"]!.GetValue<string>()==id)!["legs"] is JsonArray legs?
            string.Join(' ',legs.Select(l=>$"{l!["side"]} {l["lots"]} {l["series"]} @{l["entryPrice"]?.GetValue<string>()??"none"}")):"refused "+offered["templates"]!.AsArray().Single(t=>t!["id"]!.GetValue<string>()==id)!["refusal"]!["code"];
        if(Legs(offer,"iron_condor")!="sell 1 p2.8 @0.08 buy 1 p2.6 @0.08 sell 1 c3.2 @0.08 buy 1 c3.4 @none"||Legs(offer,"butterfly")!="buy 1 c2.8 @0.08 sell 2 c3 @0.08 buy 1 c3.2 @0.08")
            throw new Exception($"Templates offer their legs at the series' settlements: {Legs(offer,"iron_condor")} / {Legs(offer,"butterfly")}.");
        if(offer["templates"]!.AsArray().Count!=7||offer["templateRule"]!.GetValue<string>()!=OptionStrategyTemplates.Rule||offer["atTheMoneyStrike"]!.GetValue<string>()!="3"||offer["futures"]!.GetValue<string>()!="3.01")
            throw new Exception("The offer names every template, their rule and the money they count from.");
        var offered=offer["offered"]!;
        // Six strikes either side of the money would pass both ends of the nine listed, so the range is the listed strikes.
        if(offered["prices"]!.ToJsonString()!="""{"lower":"2.6","upper":"3.4","points":21}"""||offered["pointCounts"]!.ToJsonString()!="[11,21,41]")throw new Exception($"Offered range: {offered["prices"]}.");
        OptionStrategies.Prices(D("2.6"),D("3.4"),21);
        // 17:00 on the 16th, then the same time on the 17th, 18th, 19th and 20th: expiry is at 19:30 on the 20th.
        var instants=offered["instants"]!.AsArray().Select(i=>DateTimeOffset.Parse(i!.GetValue<string>(),CultureInfo.InvariantCulture)).ToArray();
        if(!instants.SequenceEqual(Enumerable.Range(0,5).Select(n=>At.AddDays(n)))||!offered["instantsComplete"]!.GetValue<bool>()||offered["selectedInstants"]!.AsArray().Count!=1||
            DateTimeOffset.Parse(offered["selectedInstants"]![0]!.GetValue<string>(),CultureInfo.InvariantCulture)!=At)throw new Exception($"Offered instants: {offered["instants"]}.");
        if(offer["clock"]!.ToJsonString()!="""{"source":"surface","zone":"UTC"}"""||offered["sensitivities"]!["clock"]!["code"]!.GetValue<string>()!="UTC"||
            offered["sensitivities"]!["policy"]!.GetValue<string>()!=FuturesOptionSensitivities.Policy||offered["expiryRule"]!.GetValue<string>()!=OptionStrategies.ExerciseRule||
            offered["volatilityShiftPoints"]!.GetValue<string>()!="0")throw new Exception($"Offered policies: {offered}.");
        // What is offered is what an analysis accepts when it is sent back.
        var sent=new OptionStrategyAnalyzeRequest(Context,"synthetic_on",Expiry,[Leg("c3")],offered["prices"].Deserialize<OptionStrategyPriceRange>(AsyncReadContract.Json)!,
            [..offered["selectedInstants"]!.AsArray().Select(i=>DateTimeOffset.Parse(i!.GetValue<string>(),CultureInfo.InvariantCulture))],D(offered["volatilityShiftPoints"]!.GetValue<string>()),
            offered["valuation"].Deserialize<FuturesOptionValuationPolicy>(AsyncReadContract.Json)!,offered["sensitivities"].Deserialize<OptionSensitivityRequest>(AsyncReadContract.Json)!,offered["expiryRule"]!.GetValue<string>());
        OptionStrategyRead.Validate(sent);
        OptionSurfaceAnalytics.Pin(sent.Sensitivities,null);
        Console.WriteLine("PASS setup offers each template's legs at their settlements, a range about the money, the days to expiry and the policies an analysis accepts back");

        // A recorded close counts days on its convention's clock and its analysis names none.
        var newYork=new ClockRule("New York","iana",null,"America/New_York",TimeZoneDatabase.Installed().Version());
        var close=OptionStrategyRead.Offer(Surface(),Expiry,At,Expiry,newYork,null,OptionSurfaceAnalytics.ConventionClock);
        if(close["clock"]!.ToJsonString()!="""{"source":"close_convention","zone":"America/New_York"}"""||close["offered"]!["sensitivities"]!["clock"] is not null||close["offered"]!["instants"]!.AsArray().Count!=5)
            throw new Exception($"A close offers its convention's days and no named clock: {close["clock"]} {close["offered"]!["sensitivities"]}.");
        // Without a clock only the valuation instant is offered.
        var uncounted=OptionStrategyRead.Offer(Surface(),Expiry,At,Expiry,null,null,OptionStrategyRead.SurfaceClock);
        if(uncounted["clock"] is not null||uncounted["offered"]!["instants"]!.AsArray().Count!=1||uncounted["offered"]!["sensitivities"]!["clock"] is not null)throw new Exception("No clock offers the valuation instant alone.");
        // Curves stop before the series' instruction cutoff: at 17:00 on the 20th the day's instant is no longer before it.
        var cut=OptionStrategyRead.Offer(Surface(),Expiry,At,At.AddDays(4),Utc,Utc,OptionStrategyRead.SurfaceClock);
        if(cut["offered"]!["instants"]!.AsArray().Count!=4||DateTimeOffset.Parse(cut["offered"]!["instantsBefore"]!.GetValue<string>(),CultureInfo.InvariantCulture)!=At.AddDays(4)||
            !cut["offered"]!["instantsComplete"]!.GetValue<bool>())throw new Exception($"Dated curves are offered up to before the horizon: {cut["offered"]}.");
        // A named clock this host cannot count days on is still the one an analysis names; the offer says its dates are not all there.
        var foreign=Utc with{Code="Synthetic",Kind="iana",FixedOffsetSeconds=null,IanaZone="America/New_York",TzdbVersion="synthetic_other_release"};
        var unread=OptionStrategyRead.Offer(Surface(),Expiry,At,Expiry,foreign,foreign,OptionStrategyRead.SurfaceClock);
        if(unread["offered"]!["instants"]!.AsArray().Count!=1||unread["offered"]!["instantsComplete"]!.GetValue<bool>()||unread["offered"]!["sensitivities"]!["clock"]?["code"]?.GetValue<string>()!="Synthetic")
            throw new Exception($"A clock the host cannot count on: {unread["offered"]}.");
        Console.WriteLine("PASS dated curves are offered on the close convention's clock, or the surface's at a working snapshot, and without one only at the valuation instant");

        var narrow=OptionStrategyRead.Offer(Surface(listed:s=>s.Strike!=D("2.6")&&s.Code!="c3.2"),Expiry,At,Expiry,Utc,Utc,OptionStrategyRead.SurfaceClock);
        if(Legs(narrow,"iron_condor")!="refused strategy_template_strike"||Legs(narrow,"call_spread")!="refused strategy_template_series"||Legs(narrow,"straddle")!="buy 1 c3 @0.08 buy 1 p3 @0.08"||
            narrow["offered"]!["prices"]!["lower"]!.GetValue<string>()!="2.7")throw new Exception($"A template with a strike or series that is not listed is refused alone: {narrow["templates"]}.");
        var unsettled=OptionStrategyRead.Offer(Surface(atTheMoney:null),Expiry,At,Expiry,Utc,Utc,OptionStrategyRead.SurfaceClock);
        if(unsettled["templates"]!.AsArray().Any(t=>t!["refusal"]?["code"]?.GetValue<string>()!="strategy_template_money")||unsettled["offered"]!["prices"] is not null)
            throw new Exception("Without an at-the-money strike no template and no range is offered.");
        Reject("strategy_expiry",()=>OptionStrategyRead.Offer(Surface(),Expiry.AddDays(30),At,Expiry.AddDays(30),Utc,Utc,OptionStrategyRead.SurfaceClock));
        Console.WriteLine("PASS a template whose strike or series is not listed is refused with its code, and nothing nearer is offered");
    }

    private static void Composes()
    {
        var reference=new ReferenceRevision(Guid.Parse("00000000-0000-0000-0000-00000000c001"),Guid.Parse("00000000-0000-0000-0000-00000000c002"));
        ReferenceKey R(string code)=>new(reference,code);
        FuturesOptionInstrument Option(string code,OptionRight right)=>new(new(R(code),R("synthetic_on"),R("synthetic_ng_x26"),new(right,D("3"),Expiry,"american","exercise_into_future"),D("10000"),R("USD"),
            R("contract"),D("0.001"),"synthetic_auto_exercise_v1",false,null,FuturesOptionInstrument.PremiumPaidUpFront));
        OptionMeasure Stated(string? value,string? missing=null)=>new(value is null?null:D(value),missing);
        var call=Option("c3",OptionRight.Call);var put=Option("p3",OptionRight.Put);
        ImmutableArray<OptionStrategyLeg> legs=[new(call,D("2"),D("0.07")),new(put,D("-3"),D("0.09"))];
        var analytics=new Dictionary<Guid,OptionSeriesSensitivities>
        {
            [call.Id]=new(call.Id,D("0.08"),D("0.012"),Stated("0.5"),Stated("2"),Stated("0.002"),Stated("-0.004")),
            [put.Id]=new(put.Id,D("0.1"),D("0.012"),Stated("-0.5"),Stated("2"),Stated(null,"sensitivity_unstable"),Stated("-0.003"))
        };
        var day=At.AddDays(1);
        var composed=OptionStrategyRead.Compose(legs,OptionStrategies.AtExpiry(legs,[D("2.5"),D("3.5")]),ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty,
            ImmutableDictionary<Guid,string>.Empty.Add(put.Id,"option_settlement_missing"),analytics,day,[],new OptionStrategyCurveRefusal("option_at_intrinsic","A leg is valued at intrinsic."));
        var strategy=composed["strategy"]!;
        string Amount(string measure)=>strategy[measure]!["amount"]?.GetValue<string>()??"missing:"+strategy[measure]!["missing"]!.GetValue<string>();
        // 10,000 × (2 × 0.08 − 3 × 0.1), 10,000 × (2 × 0.5 + 3 × 0.5), 10,000 × (2 × 2 − 3 × 2) and 10,000 × (2 × −0.004 + 3 × 0.003).
        if(Amount("value")!="-1400"||Amount("delta")!="25000"||Amount("gamma")!="-20000"||Amount("vega")!="missing:incomplete"||Amount("theta")!="10"||
            DateTimeOffset.Parse(strategy["dayTo"]!.GetValue<string>(),CultureInfo.InvariantCulture)!=day)throw new Exception($"Strategy figures: {strategy}.");
        var expiry=composed["expiry"]!;
        // 10,000 × (2 × 0.07 − 3 × 0.09) is a credit of 1,300; above 3 two calls gain without bound, and at zero three puts cost 90,000.
        if(expiry["netPremium"]!.ToJsonString()!="""{"amount":"1300","direction":"credit"}"""||expiry["maximumProfit"]!.ToJsonString()!="""{"amount":null,"bounded":false}"""||
            expiry["maximumLoss"]!.ToJsonString()!="""{"amount":"-88700","bounded":true}"""||expiry["breakevens"]!.ToJsonString()!="""[{"lower":"2.956666666666666667","upper":"2.956666666666666667"}]"""||
            expiry["curve"]!.ToJsonString()!="""[{"price":"2.5","profitAndLoss":"-13700"},{"price":"3","profitAndLoss":"1300"},{"price":"3.5","profitAndLoss":"11300"}]""")
            throw new Exception($"Expiry analysis: {expiry}.");
        var rows=composed["legs"]!.AsArray();
        string Row(int leg,string member)=>rows[leg]![member]!.GetValue<string>();
        if(Row(0,"side")!="buy"||Row(0,"lots")!="2"||Row(0,"premium")!="1400"||Row(1,"side")!="sell"||Row(1,"lots")!="3"||Row(1,"premium")!="-2700"||Row(1,"state")!="unavailable"||
            Row(1,"reason")!="option_settlement_missing")throw new Exception($"Leg rows: {rows}.");
        var dated=composed["dated"]!;
        if(dated["state"]!.GetValue<string>()!="refused"||dated["reason"]!.GetValue<string>()!="option_at_intrinsic"||dated["curves"]!.AsArray().Count!=0)throw new Exception($"Dated curves: {dated}.");
        Console.WriteLine("PASS a strategy's figures are its legs' times multiplier and signed lots, added; a sum with a missing member is missing, and refused curves say why");
    }

    private static void Reject(string code,Action action)
    {
        try{action();}catch(MarketDataException e)when(e.Code==code){return;}
        throw new Exception($"Expected {code}.");
    }
}
