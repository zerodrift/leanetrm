using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;

internal static class IntervalPricingChecks
{
    public static int Run()
    {
        int passed=0;
        void Check(string label,Action action){action();passed++;Console.WriteLine("PASS interval pricing: "+label);}
        void Equal<T>(T actual,T expected){if(!EqualityComparer<T>.Default.Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}.");}
        void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        var f=new Fixture();
        var zone=TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var eastern=PowerClock.Capture(zone);
        var power=f.Quotes.Single(q=>q.Key.Code=="power_out");
        DateTimeOffset At(string time)=>Fixture.Utc("2031-11-03T"+time+":00Z");
        DeliveryInterval Interval(string start,int minutes=60)=>Fixture.Interval(zone,At(start),minutes);
        // PWR01: two one-hour intervals; the first is fixed at 40, the second is marked at 60.
        DeliveryInterval first=Interval("10:00"),second=Interval("11:00");
        ImmutableArray<MarketContract> contracts=[f.IntervalContract(801,first),f.IntervalContract(802,second)];
        var curve=new Curve(f.C("hourly"),power.Key,f.R("location"),f.R("hourly"),new(SettlementKind.Index,f.R("index"),null),CurveRule.Direct,
            [..contracts.Select(c=>c.Key)],[],ObservationRole.TraderMark,f.R("source"),null);
        var model=new CurveModel(f.Revision,f.Reference,f.Quantities,f.Quotes,contracts,[curve],[],[],
            [new(f.S(801),contracts[0].Key,power.Key,null),new(f.S(802),contracts[1].Key,power.Key,null)]);
        SelectedObservation Mark(int series,string value)=>new(new(f.S(series),InputUse.Variable),new(new(f.Revision.CatalogId,Guid.NewGuid()),f.S(series),
            f.R("source"),ObservationRole.TraderMark,PublicationState.Final,At("09:00"),ExactDecimal.Parse(value)));
        ImmutableArray<SelectedObservation> marks=[Mark(801,"55"),Mark(802,"60")];
        MarketSnapshot Snapshot(DateTimeOffset at)=>new(Guid.NewGuid(),f.Revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,marks);
        // Only intervals still live at the valuation instant have a forward; an ended interval's forward is never built.
        ImmutableArray<IntervalPrice> Forwards(CurveBuild build)=>IntervalPrices.Marked(build,curve.Key,[..contracts.Where(c=>c.Expiry>build.Snapshot.ValuationAt)]);
        CurveBuild Build(DateTimeOffset at)=>model.Build(Snapshot(at),[..contracts.Where(c=>c.Expiry>at).Select(c=>new CurveRequest(curve.Key,c.Key))]);
        var unit=PriceUnit.FromQuote(power);
        IntervalActual Actual(DeliveryInterval i,string price,string knownAt,int? revision=1)=>
            new(i.Start,i.End,new(ExactDecimal.Parse(price),unit),At(knownAt),Guid.NewGuid(),revision);
        var rule=new IntervalActualRule("interval_end",60,"interval_actual_due_v1");
        var fixedFirst=Actual(first,"40","11:05");
        ImmutableArray<IntervalPriceResolution> Resolve(DateTimeOffset at,DateTimeOffset knownAt,IReadOnlyList<IntervalActual> actuals,IReadOnlyList<IntervalPrice>? forwards=null)=>
            IntervalPricing.Resolve([first,second],unit,(forwards??(at<second.End?Forwards(Build(at)):ImmutableArray<IntervalPrice>.Empty)).ToDictionary(p=>p.Start),actuals,rule,at,knownAt);
        Fraction Price(IntervalPriceResolution r)=>new(BigInteger.Parse(r.Price!.Numerator),BigInteger.Parse(r.Price.Denominator));
        // Value of receiving 10 MWh per interval at its price and paying 50.
        Fraction Value(IEnumerable<IntervalPriceResolution> rows)=>rows.Select(r=>(Price(r)-new Fraction(50,1))*new Fraction(10,1)).Aggregate(new Fraction(0,1),(a,b)=>a+b);

        Check("PWR01: a fixed first interval and a marked second interval value at zero",()=>{
            var rows=Resolve(At("11:30"),At("11:30"),[fixedFirst]);
            Equal(string.Join(",",rows.Select(r=>r.State)),"Actual,Projected");
            Equal(rows[0].Actual,fixedFirst);Equal(rows[0].ForwardLineage.IsEmpty,true);
            Equal(rows[1].ForwardLineage.SetEquals([marks[1].Observation.Key]),true);
            Equal(rows[0].DueAt,At("12:00"));Equal(rows[1].DueAt,At("13:00"));
            Equal(Value(rows),new Fraction(0,1));});
        Check("PWR01: a +1 shock to the second interval's mark moves only it; the fixed interval is unchanged",()=>{
            var at=At("11:30");
            var bump=new OpenEtrm.MarketData.Risk.QuoteBump(marks[1].Input,power.Key,unit,ExactDecimal.Parse("1"),OpenEtrm.MarketData.Risk.QuoteBump.Convention);
            var scenario=model.BuildScenarios(Snapshot(at),[new(curve.Key,contracts[1].Key)],[bump]).Factors.Single();
            var @base=Resolve(at,at,[fixedFirst]);
            var up=Resolve(at,at,[fixedFirst],Forwards(scenario.Up));
            Equal(Price(up[1]),new Fraction(61,1));Equal(Value(up),new Fraction(10,1));
            Equal(up[0],@base[0]);});
        Check("PWR01: an ended interval without its due actual is unresolved, never its expired forward",()=>{
            var rows=Resolve(At("12:30"),At("12:30"),[fixedFirst]);
            Equal(rows[1].State,IntervalPriceState.Unresolved);Equal(rows[1].Price,null);Equal(rows[1].ForwardLineage.IsEmpty,true);
            Reject("interval_actual_missing",()=>Resolve(At("13:00"),At("13:00"),[fixedFirst]));
            Reject("interval_actual_missing",()=>Resolve(At("12:00"),At("12:00"),[]));});
        Check("PWR01: a day-ahead actual known before its hour is used",()=>{
            var dayAhead=Actual(second,"58","10:45");
            var rows=Resolve(At("10:50"),At("10:50"),[dayAhead]);
            Equal(string.Join(",",rows.Select(r=>r.State)),"Projected,Actual");Equal(Price(rows[1]),new Fraction(58,1));});
        Check("PWR01: an actual known after the cutoff is refused; a later cutoff uses the correction",()=>{
            var correction=Actual(first,"41","11:20",2);
            Reject("interval_actual",()=>Resolve(At("11:30"),At("11:10"),[correction]));
            Equal(Price(Resolve(At("11:30"),At("11:30"),[correction])[0]),new Fraction(41,1));});
        Check("an actual published after the valuation instant never prices an interval still live at it",()=>{
            // Valued at 11:30 with knowledge at 12:30: the second hour is live at 11:30, the first has ended.
            var late=Actual(second,"58","12:10");
            var rows=Resolve(At("11:30"),At("12:30"),[fixedFirst,late]);
            Equal(string.Join(",",rows.Select(r=>r.State)),"Actual,Projected");Equal(Price(rows[1]),new Fraction(60,1));
            var collectedLater=Actual(first,"40","11:40");
            Equal(Resolve(At("11:30"),At("12:30"),[collectedLater])[0].Actual,collectedLater);});
        Check("actuals must lie on the curve's intervals in its price unit, under the supported due rule",()=>{
            Reject("interval_actual",()=>Resolve(At("11:30"),At("11:30"),[fixedFirst with{End=first.End.AddMinutes(-30)}]));
            Reject("interval_actual",()=>Resolve(At("11:30"),At("11:30"),[fixedFirst,fixedFirst with{BatchId=Guid.NewGuid()}]));
            Reject("interval_actual",()=>Resolve(At("11:30"),At("11:30"),[fixedFirst with{Price=new(ExactDecimal.Parse("40"),PriceUnit.FromQuote(f.Quotes[0]))}]));
            Reject("interval_actual",()=>IntervalPricing.Resolve([first,second],unit,new Dictionary<DateTimeOffset,IntervalPrice>(),[],rule with{Version="interval_actual_due_v2"},At("09:00"),At("09:00")));
            Reject("interval_actual",()=>IntervalPricing.Resolve([first,second],unit,new Dictionary<DateTimeOffset,IntervalPrice>(),[],rule with{Anchor="interval_middle"},At("09:00"),At("09:00")));
            Reject("request_member",()=>Resolve(At("10:30"),At("10:30"),[],Forwards(Build(At("10:30"))).Take(1).ToArray()));});

        var quarters=new[]{"10:00","10:15","10:30","10:45"}.Select(t=>Interval(t,15)).ToArray();
        IntervalPriceResolution Projected(DeliveryInterval i,string price)=>new(i,IntervalPriceState.Projected,
            ExactUnitPrice.FromPrice(new(ExactDecimal.Parse(price),unit)),[],null,i.End.AddHours(1));
        var quarterPrices=quarters.Zip(["10","-20","30","60"],Projected).ToImmutableArray();
        string Means(IEnumerable<IntervalAggregate> aggregates)=>string.Join(",",aggregates.Select(a=>a.Mean is null?"none":a.Mean.Numerator+"/"+a.Mean.Denominator));
        Check("hour and day aggregates are duration-weighted means that keep their native intervals",()=>{
            var hourly=IntervalPricing.Aggregate(quarterPrices,IntervalAggregation.Hour,eastern).Single();
            Equal(Means([hourly]),"20/1");Equal(hourly.Constituents.SequenceEqual(quarterPrices),true);
            ImmutableArray<IntervalPriceResolution> morning=[..quarterPrices,Projected(Interval("11:00"),"40")];
            Equal(Means(IntervalPricing.Aggregate(morning,IntervalAggregation.Hour,eastern)),"20/1,40/1");
            // 3 November 2031 on the New York clock: 05:00Z to 05:00Z, 24 hours at 30 except one at 54.
            ImmutableArray<IntervalPriceResolution> day=[..Enumerable.Range(0,24).Select(h=>Projected(Fixture.Interval(zone,At("05:00").AddHours(h),60),h==0?"54":"30"))];
            Equal(Means(IntervalPricing.Aggregate(day,IntervalAggregation.Day,eastern)),"31/1");
            Reject("interval_aggregate",()=>IntervalPricing.Aggregate(morning,IntervalAggregation.Day,eastern));
            Reject("interval_aggregate",()=>IntervalPricing.Aggregate(quarterPrices[..3],IntervalAggregation.Hour,eastern));
            Reject("interval_aggregate",()=>IntervalPricing.Aggregate(morning,(IntervalAggregation)9,eastern));});
        Check("a group with an unresolved interval has no mean but keeps every interval",()=>{
            var gap=quarterPrices.SetItem(2,quarterPrices[2] with{State=IntervalPriceState.Unresolved,Price=null});
            var hourly=IntervalPricing.Aggregate(gap,IntervalAggregation.Hour,eastern).Single();
            Equal(hourly.Mean,null);Equal(hourly.Constituents.Length,4);});
        Check("PWR02: rate quantities integrate each explicit interval's own duration once",()=>{
            var r=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());ReferenceKey R(string code)=>new(r,code);
            var energy=new QuantitySpecification(R("energy_quantity"),R("power_product"),R("delivered_energy"),R("megawatt_hour"),R("energy"));
            var rate=new QuantitySpecification(R("power_quantity"),R("power_product"),R("delivery_rate"),R("megawatt"),R("power"));
            string Energy(PowerInterval[] periods,string megawatts)=>PowerIntervalQuantities.Compile(periods,
                [..periods.Select(p=>new PowerIntervalQuantity(p.Start,p.End,new(ExactDecimal.Parse(megawatts),rate.Key),ExactDecimal.Parse("1")))],rate,energy)
                .Select(q=>q.Energy).Aggregate((a,b)=>a.Add(b)).ToQuantity().Amount.ToString();
            var utc=PowerClock.Capture(TimeZoneInfo.Utc);
            Equal(Energy(PowerHours.Compile(new(2031,11,3),new(2031,11,4),utc,TimeOfUse.All(5),[],5).Take(1).ToArray(),"12"),"1");
            Equal(Energy(PowerHours.Compile(new(2031,11,3),new(2031,11,4),utc,TimeOfUse.All(30),[],30).Take(1).ToArray(),"12"),"6");
            var fixedEst=PowerClock.FromRule(new("synthetic_fixed_est","fixed_offset",-18000,null,null),TimeZoneDatabase.Installed());
            Equal(Energy(PowerHours.Compile(new(2031,11,2),new(2031,11,3),fixedEst,TimeOfUse.All(60),[],60),"10"),"240");});
        return passed;
    }
}
