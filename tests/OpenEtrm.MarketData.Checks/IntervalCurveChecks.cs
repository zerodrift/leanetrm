using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;

internal static class IntervalCurveChecks
{
    public static int Run()
    {
        int passed=0;
        void Check(string label,Action action){action();passed++;Console.WriteLine("PASS interval curves: "+label);}
        void Equal<T>(T actual,T expected){if(!EqualityComparer<T>.Default.Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}.");}
        void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        var f=new Fixture();
        var zone=TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var eastern=PowerClock.Capture(zone);
        var fallBack=Fixture.Utc("2031-11-02T04:00:00Z");
        // The repeated 01:00 hour is told apart by occurrence.
        DeliveryInterval Label(DateTimeOffset start,int minutes,int occurrence)=>Fixture.Interval(zone,start,minutes,occurrence);
        MarketContract Contract(int n,DeliveryInterval interval)=>f.IntervalContract(n,interval);
        var hours=Enumerable.Range(0,25).Select(i=>Contract(100+i,Label(fallBack.AddHours(i),60,i==2?2:1))).ToImmutableArray();
        MarketSeries Series(int n,ContractKey contract,string quote)=>new(f.S(n),contract,f.R(quote),null);
        Curve DirectHourly(string code,string quote)=>new(f.C(code),f.R(quote),f.R("location"),f.R("hourly"),
            new(SettlementKind.Index,f.R("index"),null),CurveRule.Direct,[..hours.Select(h=>h.Key)],[],ObservationRole.TraderMark,f.R("source"),null);
        CurveModel Model(ImmutableArray<MarketContract> contracts,ImmutableArray<Curve> curves,ImmutableArray<MarketSeries> series)=>
            new(f.Revision,f.Reference,f.Quantities,f.Quotes,contracts,curves,[],[],series);
        var hubSeries=hours.Select((h,i)=>Series(100+i,h.Key,"power_out")).ToImmutableArray();
        SelectedObservation Mark(int n,string value)=>new(new(f.S(n),InputUse.Variable),new(new(f.Revision.CatalogId,Guid.NewGuid()),f.S(n),
            f.R("source"),ObservationRole.TraderMark,PublicationState.Final,Fixture.Utc("2030-12-18T00:00:00Z"),ExactDecimal.Parse(value)));
        MarketSnapshot Snapshot(params SelectedObservation[] marks)=>new(Guid.NewGuid(),f.Revision,Fixture.Utc("2030-12-18T00:00:00Z"),
            Fixture.Utc("2030-12-18T00:00:00Z"),InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[..marks]);

        Check("hourly contracts on a fall-back day validate",()=>{
            IntervalContracts.Validate(eastern,60,hours);
            Equal(hours[1].Interval!.HourBeginning,1);Equal(hours[1].Interval!.Occurrence,1);
            Equal(hours[2].Interval!.HourBeginning,1);Equal(hours[2].Interval!.Occurrence,2);
            Equal(hours[24].Interval!.End,Fixture.Utc("2031-11-03T05:00:00Z"));});
        Check("a contract whose labels disagree with the clock is refused",()=>{
            Reject("interval_contract",()=>IntervalContracts.Validate(eastern,60,hours.SetItem(2,hours[2] with{Interval=hours[2].Interval! with{Occurrence=1}})));
            Reject("interval_contract",()=>IntervalContracts.Validate(eastern,60,hours.SetItem(5,hours[5] with{Interval=hours[5].Interval! with{HourBeginning=9}})));
            Reject("interval_contract",()=>IntervalContracts.Validate(eastern,60,hours.SetItem(5,hours[5] with{Interval=hours[5].Interval! with{OperatingDate=new(2031,11,3)}})));});
        Check("a misaligned or wrong-length contract is refused",()=>{
            Reject("interval_contract",()=>IntervalContracts.Validate(eastern,60,[Contract(200,Label(Fixture.Utc("2031-11-05T05:30:00Z"),60,1))]));
            Reject("interval_contract",()=>IntervalContracts.Validate(eastern,60,[Contract(200,Label(Fixture.Utc("2031-11-05T05:00:00Z"),30,1))]));
            Reject("interval_contract",()=>IntervalContracts.Validate(eastern,60,[hours[3],hours[3] with{Key=f.K(300)}]));
            Reject("interval_contract",()=>IntervalContracts.Validate(eastern,60,[f.Contracts[1]]));
            Reject("tou_resolution",()=>IntervalContracts.Validate(eastern,10,[hours[3]]));});
        Check("an interval contract is a forward with pinned dates and expiry",()=>{
            var hub=DirectHourly("hub_hourly","power_out");
            Reject("contract_dates",()=>Model(hours.SetItem(3,hours[3] with{Kind=ContractKind.Future,FuturesSeries=f.R("series")}),[hub],hubSeries));
            Reject("contract_dates",()=>Model(hours.SetItem(3,hours[3] with{DeliveryTo=hours[3].DeliveryFrom.AddDays(2)}),[hub],hubSeries));
            Reject("contract_dates",()=>Model(hours.SetItem(3,hours[3] with{Expiry=hours[3].Interval!.End.AddHours(1)}),[hub],hubSeries));
            Reject("contract_dates",()=>Model(hours.SetItem(3,hours[3] with{DeliveryFrom=hours[3].DeliveryFrom.AddDays(-1)}),[hub],hubSeries));
            Model(hours,[hub],hubSeries);});
        Check("a curve cannot mix interval and date-only constituents",()=>{
            var forward=f.Contracts[1];
            var mixed=DirectHourly("hub_hourly","power_out") with{Constituents=[hours[0].Key,forward.Key]};
            Reject("interval_contract",()=>Model([..hours,forward],[mixed],[..hubSeries,Series(400,forward.Key,"power_out")]));});
        Check("mapping pairs only identical intervals",()=>{
            var hub=DirectHourly("hub_hourly","power_out");var basis=DirectHourly("basis_hourly","power_basis");
            var basisSeries=hours.Select((h,i)=>Series(500+i,h.Key,"power_basis")).ToImmutableArray();
            Curve Local(int target,int source)=>new(f.C("local_hourly"),f.R("power_out"),f.R("location"),f.R("hourly"),new(SettlementKind.Index,f.R("index"),null),
                CurveRule.Add,[hours[target].Key],[new(InputSlot.Base,f.C("hub_hourly"),SourceExpiryRule.RequireLive,[new(hours[target].Key,hours[source].Key)]),
                new(InputSlot.Spread,f.C("basis_hourly"),SourceExpiryRule.RequireLive,[new(hours[target].Key,hours[target].Key)])],null,null,null);
            Reject("unsupported_mapping",()=>Model(hours,[hub,basis,Local(3,4)],[..hubSeries,..basisSeries]));
            var model=Model(hours,[hub,basis,Local(3,3)],[..hubSeries,..basisSeries]);
            var built=model.Build(Snapshot(Mark(103,"50"),Mark(503,"2")),[new(f.C("local_hourly"),hours[3].Key)]);
            Equal(built.Values[0].Price.Value.ToString(),"52");});
        Check("marked prices are read for an exact window",()=>{
            var hub=DirectHourly("hub_hourly","power_out");
            var marks=hours.Select((h,i)=>Mark(100+i,(40+i)+".5")).ToArray();
            var build=Model(hours,[hub],hubSeries).Build(Snapshot(marks),[..hours.Select(h=>new CurveRequest(hub.Key,h.Key))]);
            var prices=IntervalPrices.Marked(build,hub.Key,IntervalPrices.Within(hours,60,Fixture.Utc("2031-11-02T05:00:00Z"),Fixture.Utc("2031-11-02T08:00:00Z")));
            Reject("interval_curve_coverage",()=>IntervalPrices.Within(hours.RemoveAt(3),60,Fixture.Utc("2031-11-02T05:00:00Z"),Fixture.Utc("2031-11-02T08:00:00Z")));
            Equal(prices.Length,3);
            Equal(prices[0].Start,Fixture.Utc("2031-11-02T05:00:00Z"));Equal(prices[2].End,Fixture.Utc("2031-11-02T08:00:00Z"));
            Equal(prices[1].Price,ExactUnitPrice.FromPrice(build.Values[2].Price));
            Equal(prices[1].Price.Numerator+"/"+prices[1].Price.Denominator,"85/2");
            Equal(prices[1].Lineage.SetEquals([marks[2].Observation.Key]),true);});
        Check("windows must be grid-aligned and bounded",()=>{
            IntervalPrices.RequireWindow(eastern,60,fallBack,fallBack.AddHours(35136));
            Reject("interval_curve_window",()=>IntervalPrices.RequireWindow(eastern,60,fallBack,fallBack.AddHours(35137)));
            Reject("interval_curve_window",()=>IntervalPrices.RequireWindow(eastern,60,fallBack.AddMinutes(30),fallBack.AddHours(3)));
            Reject("interval_curve_window",()=>IntervalPrices.RequireWindow(eastern,60,fallBack,fallBack));
            IntervalPrices.RequireWindow(eastern,15,fallBack.AddMinutes(45),fallBack.AddMinutes(90));});
        return passed;
    }
}
