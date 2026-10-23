using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

internal static class IntervalResolverReuseChecks
{
    internal static void Equal<T>(T actual,T expected)
    {if(!EqualityComparer<T>.Default.Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}

    public static int Run()
    {
        var fixture=new ResolverReuseFixture(2);
        var prepared=fixture.Prepare();
        var curves=new Dictionary<CurveKey,PreparedCurve>{{prepared.Key,prepared}};
        var market=fixture.Model.Build(fixture.Snapshot,fixture.Requests);
        Equal(prepared.Dependencies(new(2031,11,1)).ToHashSet().SetEquals([fixture.Requests[0],fixture.Requests[2]]),true);
        Equal(prepared.Dependencies(new(2031,12,1)).ToHashSet().SetEquals([fixture.Requests[1],fixture.Requests[3]]),true);
        var resolver=new IntervalPriceResolver(market,curves);
        var november=resolver.Prices(prepared.Key,new(2031,11,1));
        Equal(november.Length,721);
        Equal(november.Count(p=>p.Interval.OperatingDate==new DateOnly(2031,11,2)&&p.Interval.HourBeginning==1),2);
        Equal(resolver.Prices(prepared.Key,new(2031,11,1)),november);
        Equal(resolver.Resolved,1);
        var december=resolver.Prices(prepared.Key,new(2031,12,1));
        Equal(december.Length,744);Equal(resolver.Resolved,2);Equal(resolver.CalibratedBlocks,4);
        Console.WriteLine("PASS resolver reuse: repeated consumers share a curve month; November retains both repeated hours");

        var pair=fixture.Model.Scenarios(market,[fixture.Bump(1)]).Single();
        foreach(var (scenario,direction) in new[]{(pair.Up,1),(pair.Down,-1)})
        {
            var changed=new IntervalPriceResolver(scenario,curves,resolver);
            Equal(changed.Prices(prepared.Key,new(2031,11,1)),november);
            var prices=changed.Prices(prepared.Key,new(2031,12,1));
            for(var i=0;i<prices.Length;i++)
            {
                // Weekday 07:00–23:00 is the declared peak product. This oracle uses the
                // calendar labels, independently of the resolver's dependency selection.
                var interval=prices[i].Interval;
                var peak=interval.OperatingDate.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
                    && interval.HourBeginning is >=7 and <23;
                Equal(prices[i].Price,peak?december[i].Price!.Add(ExactUnitPrice.FromPrice(new(ExactDecimal.Parse(direction.ToString()),fixture.Unit))):december[i].Price);
            }
            Equal(changed.Resolved,1);Equal(changed.ReusedMonths,1);
            Equal(changed.CalibratedBlocks,1);Equal(changed.ReusedBlocks,1);
            Equal(changed.Prices(prepared.Key,new(2031,12,1)),prices);
            Equal(changed.Resolved,1);Equal(changed.CalibratedBlocks,1);
        }
        Console.WriteLine("PASS resolver reuse: each up/down shock recalibrates one declared block, reuses the other block and untouched month");

        var actualStart=fixture.Clock.StartOfDay(new(2031,12,1)).ToUniversalTime().AddHours(10);
        var actual=new IntervalActual(actualStart,actualStart.AddHours(1),new(ExactDecimal.Parse("123"),fixture.Unit),fixture.At.AddMinutes(-1),Guid.NewGuid(),1);
        var withActual=fixture.Prepare([actual]);
        var actualCurves=new Dictionary<CurveKey,PreparedCurve>{{withActual.Key,withActual}};
        var actualBase=new IntervalPriceResolver(market,actualCurves);
        var actualBefore=actualBase.Prices(withActual.Key,new(2031,12,1));
        var actualAfter=new IntervalPriceResolver(pair.Up,actualCurves,actualBase).Prices(withActual.Key,new(2031,12,1));
        var before=actualBefore.Single(p=>p.Start==actualStart);var after=actualAfter.Single(p=>p.Start==actualStart);
        Equal(before.State,IntervalPriceState.Actual);Equal(before.Price!.Numerator,"123");Equal(after,before);
        Equal(actualBefore.Where(p=>p.Start!=actualStart).Select(p=>p.Price).SequenceEqual(december.Where(p=>p.Start!=actualStart).Select(p=>p.Price)),true);
        Console.WriteLine("PASS resolver reuse: an eligible collected actual remains unchanged under shock without rescaling neighboring cells");

        var later=fixture.Prepare(knownAt:fixture.At.AddMinutes(1));
        var other=PreparedCurve.Prepare(fixture.Curve with{Key=fixture.F.C("other")},fixture.Spans,fixture.Shape,[],fixture.At,fixture.At.AddMinutes(1));
        Reject("interval_price_source",()=>new IntervalPriceResolver(market,new Dictionary<CurveKey,PreparedCurve>{{prepared.Key,prepared},{other.Key,other}}));
        Reject("interval_price_source",()=>new IntervalPriceResolver(pair.Up,new Dictionary<CurveKey,PreparedCurve>{{later.Key,later}},resolver));
        var rePrepared=fixture.Prepare();
        Reject("interval_price_source",()=>new IntervalPriceResolver(pair.Up,new Dictionary<CurveKey,PreparedCurve>{{rePrepared.Key,rePrepared}},resolver));
        var otherFrame=fixture.Model.Build(fixture.Snapshot with{Id=Guid.NewGuid()},fixture.Requests);
        Reject("interval_price_source",()=>new IntervalPriceResolver(otherFrame,curves,resolver));
        var missing=fixture.Model.Build(fixture.Snapshot,[fixture.Requests[0]]);
        Reject("risk_dependency",()=>new IntervalPriceResolver(missing,curves).Prices(prepared.Key,new(2031,11,1)));
        Console.WriteLine("PASS resolver reuse: mixed cutoffs and cross-request prepared-input reuse are refused");
        MarkedSources();
        CoverageRefusals();
        return 6;
    }

    private static void CoverageRefusals()
    {
        var fixture=new ResolverReuseFixture(2);
        var contracts=fixture.Curve.Definition.Blocks[0].Contracts;
        var period=new PeriodContractCurve(fixture.Curve.Definition.Blocks[0].Curve,fixture.Unit,[contracts[0]],[]);
        var from=Fixture.Utc("2031-11-01T00:00:00Z");
        var end=Fixture.Utc("2032-01-01T00:00:00Z");
        // Asking for two months cannot successfully return only the one retained month.
        Reject("interval_price_source",()=>PreparedCurve.Prepare(period,[(from,end)],[],[],fixture.At,fixture.At));
        // Overlap must be checked across starting-month groups, not only within each group.
        var overlapping=period with{Contracts=[contracts[0] with{DeliveryTo=new(2032,1,1)},contracts[1] with{DeliveryTo=new(2032,2,1)}]};
        Reject("interval_price_source",()=>PreparedCurve.Prepare(overlapping,[(from,Fixture.Utc("2032-02-01T00:00:00Z"))],[],[],fixture.At,fixture.At));
        Console.WriteLine("PASS resolver coverage: period gaps and cross-month contract overlaps are refused");
    }

    private static void MarkedSources()
    {
        var f=new Fixture();var at=Fixture.Utc("2031-11-01T00:00:00Z");
        var unit=PriceUnit.FromQuote(f.Quotes.Single(q=>q.Key.Code=="power_out"));
        var interval=Fixture.Interval(TimeZoneInfo.Utc,at,60);
        var contracts=new[]{f.IntervalContract(9101,interval),f.IntervalContract(9102,interval)};
        var da=f.Direct("day_ahead",contracts[0].Key,f.R("power_out"),SettlementKind.Index) with{DirectSource=f.R("day_ahead_source")};
        var rt=f.Direct("real_time",contracts[1].Key,f.R("power_out"),SettlementKind.Index) with{DirectSource=f.R("real_time_source")};
        var sources=new[]{da,rt};
        var model=new CurveModel(f.Revision,f.Reference,f.Quantities,f.Quotes,[..contracts],[..sources],[],[],
            [new(f.S(9101),contracts[0].Key,f.R("power_out"),null),new(f.S(9102),contracts[1].Key,f.R("power_out"),null)]);
        var snapshot=new MarketSnapshot(Guid.NewGuid(),f.Revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [..Enumerable.Range(0,2).Select(i=>new SelectedObservation(new(f.S(9101+i),InputUse.Variable),
                new(new(f.Revision.CatalogId,Guid.NewGuid()),f.S(9101+i),sources[i].DirectSource!.Value,ObservationRole.TraderMark,PublicationState.Final,
                    at.AddMinutes(-1),ExactDecimal.Parse(i==0?"31":"47"))))]);
        var prepared=Enumerable.Range(0,2).Select(i=>PreparedCurve.Prepare(new MarkedIntervalCurve(sources[i].Key,unit,
            new("interval_end",60,IntervalActualRule.SupportedVersion),PowerClock.Capture(TimeZoneInfo.Utc),60,[contracts[i]]),[(at,at.AddHours(1))],[],[],at,at)).ToDictionary(p=>p.Key);
        var resolver=new IntervalPriceResolver(model.Build(snapshot,[..Enumerable.Range(0,2).Select(i=>new CurveRequest(sources[i].Key,contracts[i].Key))]),prepared);
        Equal(resolver.Prices(da.Key,new(2031,11,1)).Single().Price!.Numerator,"31");
        Equal(resolver.Prices(rt.Key,new(2031,11,1)).Single().Price!.Numerator,"47");Equal(resolver.Resolved,2);
        Console.WriteLine("PASS resolver reuse: separate day-ahead and real-time curves at one location never share prices");
        var unretained=new MarkedIntervalCurve(f.C("absent_curve"),unit,new("interval_end",60,IntervalActualRule.SupportedVersion),
            PowerClock.Capture(TimeZoneInfo.Utc),60,[contracts[0]]);
        var observed=new IntervalActual(at,at.AddHours(1),new(ExactDecimal.Parse("99"),unit),at,Guid.NewGuid(),1);
        var actualOnly=PreparedCurve.Prepare(unretained,[(at,at.AddHours(1))],[],[observed],at,at);
        Reject("interval_price_source",()=>new IntervalPriceResolver(model.Build(snapshot,[]),
            new Dictionary<CurveKey,PreparedCurve>{{actualOnly.Key,actualOnly}}).Prices(actualOnly.Key,new(2031,11,1)));
    }
}

// A request-scoped synthetic workload shared by the checks and the measured benchmark.
internal sealed class ResolverReuseFixture
{
    public Fixture F { get; }=new();
    public PowerClock Clock { get; }=PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
    public DateTimeOffset At { get; }=Fixture.Utc("2031-10-30T00:00:00Z");
    public DateOnly First { get; }=new(2031,11,1);
    public PriceUnit Unit { get; }
    public CurveModel Model { get; }
    public MarketSnapshot Snapshot { get; }
    public ShapedIntervalCurve Curve { get; }
    public ImmutableArray<CurveRequest> Requests { get; }
    public ImmutableArray<SeriesCell> Shape { get; }
    public ImmutableArray<(DateTimeOffset From,DateTimeOffset To)> Spans { get; }
    private readonly QuoteSpecification quote;
    private readonly int months;

    public ResolverReuseFixture(int months,int minutes=60)
    {
        this.months=months;quote=F.Quotes.Single(q=>q.Key.Code=="power_out");Unit=PriceUnit.FromQuote(quote);
        MarketContract Contract(int i,bool peak)
        {
            var from=First.AddMonths(i);
            return new(F.K((peak?10000:20000)+i),ContractKind.Forward,from,from.AddMonths(1),Clock.StartOfDay(from).ToUniversalTime(),null);
        }
        var peak=Enumerable.Range(0,months).Select(i=>Contract(i,true)).ToImmutableArray();
        var off=Enumerable.Range(0,months).Select(i=>Contract(i,false)).ToImmutableArray();
        Curve Block(string code,ImmutableArray<MarketContract> contracts)=>new(F.C(code),quote.Key,F.R("location"),F.R(code),
            new(SettlementKind.Index,F.R("index"),null),CurveRule.Direct,[..contracts.Select(c=>c.Key)],[],ObservationRole.TraderMark,F.R("source"),null);
        var curves=new[]{Block("peak",peak),Block("off_peak",off)};
        var contracts=peak.AddRange(off);
        var series=contracts.Select((c,i)=>new MarketSeries(F.S(10000+i),c.Key,quote.Key,null)).ToImmutableArray();
        Model=new(F.Revision,F.Reference,F.Quantities,F.Quotes,contracts,[..curves],[],[],series);
        Snapshot=new(Guid.NewGuid(),F.Revision,At,At,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [..series.Select((s,i)=>new SelectedObservation(new(s.Key,InputUse.Variable),new(new(F.Revision.CatalogId,Guid.NewGuid()),s.Key,F.R("source"),
                ObservationRole.TraderMark,PublicationState.Final,At,ExactDecimal.Parse(i<months?"60":"40"))))]);
        Requests=[..peak.Select(c=>new CurveRequest(curves[0].Key,c.Key)),..off.Select(c=>new CurveRequest(curves[1].Key,c.Key))];
        var tou=new TouPeak(7,23,false);
        var definition=new ShapedCurveDefinition("synthetic_reuse",ShapeMethod.Additive,BlockShaping.CalibrationPolicy,BlockShaping.OperatorVersion,
            Guid.NewGuid(),new(Clock,minutes,[]),[new(curves[0].Key,BlockMembership.Peak,tou,peak),new(curves[1].Key,BlockMembership.OffPeak,tou,off)]);
        Curve=new(F.C("shaped"),Unit,new("interval_end",60,IntervalActualRule.SupportedVersion),definition);
        Spans=[..Enumerable.Range(0,months).Select(i=>(Clock.StartOfDay(First.AddMonths(i)).ToUniversalTime(),Clock.StartOfDay(First.AddMonths(i+1)).ToUniversalTime()))];
        var zone=TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Shape=[..Spans.SelectMany(s=>Enumerable.Range(0,(int)(s.To-s.From).TotalMinutes/minutes).Select(i=>s.From.AddMinutes(i*minutes)))
            .Select(start=>new SeriesCell(start,start.AddMinutes(minutes),ExactDecimal.Parse(TimeZoneInfo.ConvertTime(start,zone).Hour.ToString()),1))];
    }

    public PreparedCurve Prepare(IReadOnlyList<IntervalActual>? actuals=null,DateTimeOffset? knownAt=null)=>
        PreparedCurve.Prepare(Curve,Spans,Shape,actuals??[],At,knownAt??At);
    public QuoteBump BlockBump(int factor)=>new(new(F.S(10000+(factor%2==0?0:months)+factor/2),InputUse.Variable),quote.Key,Unit,ExactDecimal.Parse("1"),QuoteBump.Convention);
    public QuoteBump Bump(int month)=>new(new(F.S(10000+month),InputUse.Variable),quote.Key,Unit,ExactDecimal.Parse("1"),QuoteBump.Convention);
    public ImmutableArray<QuoteBump> Bumps(int count)=>[..Enumerable.Range(0,count).Select(i=>Bump(i*(months/count)))];
}
