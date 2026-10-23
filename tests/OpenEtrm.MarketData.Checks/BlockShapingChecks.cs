using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;

internal static class BlockShapingChecks
{
    public static int Run()
    {
        int passed=0;
        void Check(string label,Action action){action();passed++;Console.WriteLine("PASS block shaping: "+label);}
        void Equal<T>(T actual,T expected){if(!EqualityComparer<T>.Default.Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}.");}
        void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        var zone=TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var eastern=PowerClock.Capture(zone);
        var hour=TimeSpan.FromHours(1);
        PowerInterval Period(string start,int minutes)
        {
            var at=Fixture.Utc(start);
            return new(at,at.AddMinutes(minutes),DateOnly.FromDateTime(at.UtcDateTime),at.Hour,at.Hour+1,0,0,1,false);
        }
        (DateTimeOffset Start,DateTimeOffset End,string Name) Row(string start,int minutes,string name)=>(Fixture.Utc(start),Fixture.Utc(start).AddMinutes(minutes),name);
        Dictionary<DateTimeOffset,(DateTimeOffset Start,DateTimeOffset End,string Name)> Align(PowerInterval[] periods,params (DateTimeOffset,DateTimeOffset,string)[] rows)=>
            IntervalAlignment.Align(periods,rows,r=>r.Item1,r=>r.Item2,"test_coverage","Rows must be distinct.","Rows must cover the periods.");

        Check("alignment joins rows to periods on exact UTC boundaries",()=>{
            var periods=new[]{Period("2031-11-03T05:00:00Z",60),Period("2031-11-03T06:00:00Z",15)};
            var aligned=Align(periods,Row("2031-11-03T06:00:00Z",15,"b"),Row("2031-11-03T05:00:00Z",60,"a"),Row("2031-11-03T09:00:00Z",60,"unused"));
            Equal(aligned[periods[0].Start].Name,"a");Equal(aligned[periods[1].Start].Name,"b");});
        Check("alignment refuses duplicate rows, gaps, unequal ends and overlapping periods",()=>{
            var periods=new[]{Period("2031-11-03T05:00:00Z",60),Period("2031-11-03T06:00:00Z",60)};
            Reject("test_coverage",()=>Align(periods,Row("2031-11-03T05:00:00Z",60,"a"),Row("2031-11-03T05:00:00Z",60,"again"),Row("2031-11-03T06:00:00Z",60,"b")));
            Reject("test_coverage",()=>Align(periods,Row("2031-11-03T05:00:00Z",60,"a")));
            Reject("test_coverage",()=>Align(periods,Row("2031-11-03T05:00:00Z",60,"a"),Row("2031-11-03T06:00:00Z",30,"short")));
            Reject("test_coverage",()=>Align([periods[0],Period("2031-11-03T05:30:00Z",60)],Row("2031-11-03T05:00:00Z",60,"a"),Row("2031-11-03T05:30:00Z",60,"b")));
            Reject("test_coverage",()=>Align(periods,(Fixture.Utc("2031-11-03T05:00:00Z").ToOffset(TimeSpan.FromHours(-5)),Fixture.Utc("2031-11-03T06:00:00Z"),"local"),Row("2031-11-03T06:00:00Z",60,"b")));});
        Check("the duration-weighted mean is exact and reduced",()=>{
            Equal(DurationWeights.Mean([(hour,0,1),(TimeSpan.FromMinutes(15),10,1)]),(new BigInteger(2),BigInteger.One));
            Equal(DurationWeights.Mean([(hour,1,3),(hour,1,6)]),(BigInteger.One,new BigInteger(4)));
            Equal(DurationWeights.Mean([(hour,-7,2)]),(new BigInteger(-7),new BigInteger(2)));});

        var f=new Fixture();
        var pjm=new TouPeak(7,23,false);
        ImmutableArray<DateOnly> holidays=[new(2031,11,27)];
        MarketContract Month(int n,int year,int month)=>new(f.K(n),ContractKind.Forward,new(year,month,1),new DateOnly(year,month,1).AddMonths(1),
            Fixture.Utc($"{year}-{month:D2}-01T00:00:00Z").AddDays(-2),null);
        ImmutableArray<MarketContract> PeakMonths=[Month(601,2031,3),Month(611,2031,11),Month(612,2031,12)];
        ImmutableArray<MarketContract> OffMonths=[Month(701,2031,3),Month(711,2031,11),Month(712,2031,12)];
        var grid=new DeliveryGrid(eastern,60,holidays);
        IntervalBlockDefinition Block(string code,BlockMembership membership,ImmutableArray<MarketContract> contracts)=>
            new(f.C(code),membership,membership==BlockMembership.All?null:pjm,contracts);
        var peak=Block("peak_monthly",BlockMembership.Peak,PeakMonths);
        var off=Block("offpeak_monthly",BlockMembership.OffPeak,OffMonths);
        DateTime Local(PowerInterval p)=>TimeZoneInfo.ConvertTime(p.Start,zone).DateTime;

        Check("peak is weekday hours 7-22 with the holiday off-peak, and peak and off-peak partition each month",()=>{
            var novemberPeak=IntervalBlocks.Compile(grid,peak,PeakMonths[1]);var novemberOff=IntervalBlocks.Compile(grid,off,OffMonths[1]);
            Equal(novemberPeak.Length,304);
            Equal(novemberPeak.All(p=>Local(p).DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && p.HourBeginning is >=7 and <23 && p.OperatingDate!=new DateOnly(2031,11,27)),true);
            Equal(novemberOff.Count(p=>p.OperatingDate==new DateOnly(2031,11,27)),24);
            Equal(novemberOff.Count(p=>p.OperatingDate==new DateOnly(2031,11,2)),25);
            Equal(novemberPeak.Concat(novemberOff).Select(p=>p.Start).Distinct().Count(),721);
            Equal(novemberPeak.Length+novemberOff.Length,721);
            var marchPeak=IntervalBlocks.Compile(grid,peak,PeakMonths[0]);var marchOff=IntervalBlocks.Compile(grid,off,OffMonths[0]);
            Equal(marchPeak.Length,336);
            Equal(marchPeak.Concat(marchOff).Select(p=>p.Start).Distinct().Count(),743);
            Equal(IntervalBlocks.Compile(grid,Block("all_monthly",BlockMembership.All,PeakMonths),PeakMonths[1]).Length,721);});
        Check("a block compiles date-only constituents of at most 366 days, a peak block with its TOU window",()=>{
            var hourly=f.IntervalContract(650,Fixture.Interval(zone,Fixture.Utc("2031-11-03T05:00:00Z"),60));
            Reject("interval_block",()=>IntervalBlocks.Compile(grid,peak,hourly));
            var long_=Month(660,2031,1) with{DeliveryTo=new(2032,1,3)};
            Reject("power_period",()=>IntervalBlocks.Compile(grid,peak,long_));
            Reject("interval_block",()=>IntervalBlocks.Compile(grid,peak with{Peak=null},PeakMonths[1]));});

        var power=f.Quotes.Single(q=>q.Key.Code=="power_out");
        UnitPrice Price(string value)=>new(ExactDecimal.Parse(value),power);
        string Text(ExactUnitPrice p)=>p.Denominator=="1"?p.Numerator:p.Numerator+"/"+p.Denominator;
        SeriesCell Cell(DateTimeOffset start,int minutes,string value,int revision=1)=>new(start,start.AddMinutes(minutes),ExactDecimal.Parse(value),revision);
        ImmutableArray<SeriesCell> Hours(params string[] values)=>[..values.Select((v,i)=>Cell(Fixture.Utc("2031-11-03T05:00:00Z").AddHours(i),60,v))];

        Check("oracles 1-3: additive and multiplicative shapes calibrate to the mark; all-zero factors are refused",()=>{
            Equal(string.Join(",",BlockShaping.Calibrate(ShapeMethod.Additive,Price("50"),Hours("-10","0","10","20")).Select(Text)),"35,45,55,65");
            Equal(string.Join(",",BlockShaping.Calibrate(ShapeMethod.Multiplicative,Price("50"),Hours("0.8","1.0","1.2","1.4")).Select(Text)),"400/11,500/11,600/11,700/11");
            Reject("interval_shape",()=>BlockShaping.Calibrate(ShapeMethod.Multiplicative,Price("50"),Hours("0","0","0","0")));
            Reject("interval_shape",()=>BlockShaping.Calibrate(ShapeMethod.Multiplicative,Price("50"),Hours("1","-2")));
            Reject("interval_block",()=>BlockShaping.Calibrate(ShapeMethod.Additive,Price("50"),[]));});
        Check("calibration weights intervals by their actual durations",()=>{
            var start=Fixture.Utc("2031-11-03T05:00:00Z");
            var prices=BlockShaping.Calibrate(ShapeMethod.Additive,Price("50"),[Cell(start,60,"0"),Cell(start.AddHours(1),15,"10")]);
            Equal(string.Join(",",prices.Select(Text)),"48,58");
            Equal(prices[0].Unit,PriceUnit.FromQuote(power));});

        Curve BlockCurve(string code,ImmutableArray<MarketContract> contracts)=>new(f.C(code),power.Key,f.R("location"),f.R("hourly"),
            new(SettlementKind.Index,f.R("index"),null),CurveRule.Direct,[..contracts.Select(c=>c.Key)],[],ObservationRole.TraderMark,f.R("source"),null);
        ImmutableArray<MarketContract> months=[..PeakMonths,..OffMonths];
        // Each block constituent's mark series shares its contract's number.
        SeriesKey SeriesOf(MarketContract contract)=>f.S(int.Parse(contract.Key.Id.ToString()[^3..]));
        var model=new CurveModel(f.Revision,f.Reference,f.Quantities,f.Quotes,months,[BlockCurve("peak_monthly",PeakMonths),BlockCurve("offpeak_monthly",OffMonths)],[],[],
            [..months.Select(c=>new MarketSeries(SeriesOf(c),c.Key,power.Key,null))]);
        SelectedObservation Mark(MarketContract contract,string value)=>new(new(SeriesOf(contract),InputUse.Variable),
            new(new(f.Revision.CatalogId,Guid.NewGuid()),SeriesOf(contract),f.R("source"),ObservationRole.TraderMark,PublicationState.Final,
                Fixture.Utc("2030-12-18T00:00:00Z"),ExactDecimal.Parse(value)));
        ImmutableArray<SelectedObservation> marks=[Mark(PeakMonths[0],"55"),Mark(PeakMonths[1],"60"),Mark(PeakMonths[2],"70"),
            Mark(OffMonths[0],"35"),Mark(OffMonths[1],"40"),Mark(OffMonths[2],"45")];
        MarketSnapshot Snapshot(ImmutableArray<SelectedObservation> selected)=>new(Guid.NewGuid(),f.Revision,Fixture.Utc("2030-12-18T00:00:00Z"),
            Fixture.Utc("2030-12-18T00:00:00Z"),InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,selected);
        ImmutableArray<CurveRequest> requests=[..PeakMonths.Select(c=>new CurveRequest(f.C("peak_monthly"),c.Key)),..OffMonths.Select(c=>new CurveRequest(f.C("offpeak_monthly"),c.Key))];
        var snapshot=Snapshot(marks);
        var built=model.Build(snapshot,requests);
        // Offsets (local hour - 12)/4 and factors 1 + local hour/32 on every hour of the three months.
        ImmutableArray<SeriesCell> Shape(Func<int,string> value,int revision=1)=>
            [..new[]{("2031-03-01T05:00:00Z","2031-04-01T04:00:00Z"),("2031-11-01T04:00:00Z","2032-01-01T05:00:00Z")}.SelectMany(range=>
                Enumerable.Range(0,(int)(Fixture.Utc(range.Item2)-Fixture.Utc(range.Item1)).TotalHours).Select(i=>Fixture.Utc(range.Item1).AddHours(i)))
                .Select(start=>Cell(start,60,value(TimeZoneInfo.ConvertTime(start,zone).Hour),revision))];
        var offsets=Shape(h=>((h-12)/4m).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var factors=Shape(h=>(1+h/32m).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var shapeSeries=Guid.NewGuid();
        ShapedCurveDefinition Shaped(ShapeMethod method,params IntervalBlockDefinition[] blocks)=>
            new("hub_shaped",method,"duration_weighted_block_mean_v1","block_shape_v1",shapeSeries,grid,[..blocks]);
        var additive=Shaped(ShapeMethod.Additive,peak,off);
        // One window prepared and evaluated against one market.
        ShapedCurveBuild Evaluate(ShapedCurveDefinition definition,CurveBuild marks,IReadOnlyList<SeriesCell> shape,DateTimeOffset from,DateTimeOffset to,
            ShapedCurveBuild? @base=null)=>BlockShaping.Evaluate(BlockShaping.Prepare(definition,shape,from,to),marks,@base);
        DateTimeOffset november=Fixture.Utc("2031-11-01T04:00:00Z"),december=Fixture.Utc("2031-12-01T05:00:00Z"),january=Fixture.Utc("2032-01-01T05:00:00Z");
        Fraction Mean(IEnumerable<ShapedIntervalPrice> prices)
        {
            Fraction sum=new(0,1);BigInteger ticks=0;
            foreach(var p in prices){var t=(p.End-p.Start).Ticks;sum+=new Fraction(BigInteger.Parse(p.Price.Numerator)*t,BigInteger.Parse(p.Price.Denominator));ticks+=t;}
            return sum*new Fraction(1,ticks);
        }

        Check("every block's duration-weighted mean equals its mark, for both methods",()=>{
            foreach(var (definition,shape) in new[]{(additive,offsets),(Shaped(ShapeMethod.Multiplicative,peak,off),factors)})
            {
                var result=Evaluate(definition,built,shape,november,january);
                Equal(result.Prices.Length,721+744);Equal(result.CalibratedBlocks,4);Equal(result.ReusedBlocks,0);
                Equal(result.Prices.Select(p=>p.Start).SequenceEqual(result.Prices.Select(p=>p.Start).Order()),true);
                foreach(var (contract,mark) in new[]{(PeakMonths[1],60),(OffMonths[1],40),(PeakMonths[2],70),(OffMonths[2],45)})
                    Equal(Mean(result.Prices.Where(p=>p.Block.Contract==contract.Key)),new Fraction(mark,1));
            }
            var scaled=Evaluate(Shaped(ShapeMethod.Multiplicative,peak,off),built,factors,november,december);
            Equal(scaled.Prices.Any(p=>p.Price.Denominator!="1"),true);});
        Check("oracle 4: a 25-hour day with equal offsets prices every interval at the mark, the repeated hour twice",()=>{
            var day=new MarketContract(f.K(680),ContractKind.Forward,new(2031,11,2),new(2031,11,3),Fixture.Utc("2031-11-01T00:00:00Z"),null);
            var all=new IntervalBlockDefinition(f.C("day_block"),BlockMembership.All,null,[day]);
            var dayModel=new CurveModel(f.Revision,f.Reference,f.Quantities,f.Quotes,[day],[BlockCurve("day_block",[day])],[],[],[new(f.S(680),day.Key,power.Key,null)]);
            var dayBuilt=dayModel.Build(Snapshot([Mark(day,"50")]),[new(f.C("day_block"),day.Key)]);
            var prices=Evaluate(Shaped(ShapeMethod.Additive,all),dayBuilt,
                [..Enumerable.Range(0,25).Select(i=>Cell(Fixture.Utc("2031-11-02T04:00:00Z").AddHours(i),60,"3"))],
                Fixture.Utc("2031-11-02T04:00:00Z"),Fixture.Utc("2031-11-03T05:00:00Z")).Prices;
            Equal(prices.Length,25);Equal(prices.All(p=>Text(p.Price)=="50"),true);
            Equal(string.Join(",",prices.Where(p=>p.Interval.HourBeginning==1).Select(p=>p.Interval.Occurrence)),"1,2");});
        Check("blocks must partition the window exactly once",()=>{
            Reject("interval_block_partition",()=>Evaluate(Shaped(ShapeMethod.Additive,peak),built,offsets,november,december));
            var allPeak=peak with{Membership=BlockMembership.All,Peak=null};var allOff=off with{Membership=BlockMembership.All,Peak=null};
            Reject("interval_block_partition",()=>Evaluate(Shaped(ShapeMethod.Additive,allPeak,allOff),built,offsets,november,december));
            Reject("interval_block_partition",()=>Evaluate(additive,built,offsets,november,january.AddHours(1)));});
        Check("the shape is read over the whole period of every block the window touches",()=>{
            var (first,last)=BlockShaping.ShapeWindow(additive,Fixture.Utc("2031-11-10T05:00:00Z"),Fixture.Utc("2031-11-12T05:00:00Z"));
            Equal((first,last),(november,december));Equal((first.Offset,last.Offset),(TimeSpan.Zero,TimeSpan.Zero));
            Equal(BlockShaping.ShapeWindow(additive,Fixture.Utc("2031-11-20T05:00:00Z"),Fixture.Utc("2031-12-03T05:00:00Z")),(november,january));
            Reject("interval_block_partition",()=>BlockShaping.ShapeWindow(additive,Fixture.Utc("2031-02-01T05:00:00Z"),Fixture.Utc("2031-02-02T05:00:00Z")));});
        Check("a missing or duplicate shape interval inside a block refuses that block",()=>{
            var gap=offsets.RemoveAt(offsets.IndexOf(offsets.First(c=>c.Start==Fixture.Utc("2031-11-29T05:00:00Z"))));
            Reject("interval_shape_coverage",()=>Evaluate(additive,built,gap,Fixture.Utc("2031-11-10T05:00:00Z"),Fixture.Utc("2031-11-11T05:00:00Z")));
            Reject("interval_shape_coverage",()=>Evaluate(additive,built,[..offsets,offsets[0]],november,december));
            Evaluate(additive,built,gap,december,january);});
        Check("a window cutting a month calibrates the whole block and matches the whole-month prices",()=>{
            var whole=Evaluate(additive,built,offsets,november,december).Prices.ToDictionary(p=>p.Start);
            var cut=Evaluate(additive,built,offsets,Fixture.Utc("2031-11-10T05:00:00Z"),Fixture.Utc("2031-11-12T05:00:00Z"));
            Equal(cut.Prices.Length,48);Equal(cut.CalibratedBlocks,2);
            Equal(cut.Prices.All(p=>whole[p.Start]==p),true);});
        Check("each price keeps its block mark lineage and the revision that set its shape interval",()=>{
            var revised=offsets.Select(c=>c.Start>=Fixture.Utc("2031-11-10T12:00:00Z")&&c.Start<Fixture.Utc("2031-11-10T15:00:00Z")?c with{Value=ExactDecimal.Parse("9"),Revision=2}:c).ToImmutableArray();
            var prices=Evaluate(additive,built,revised,november,december).Prices;
            Equal(prices.Count(p=>p.ShapeRevision==2),3);Equal(prices.Count(p=>p.ShapeRevision==1),718);
            var novemberPeak=built.Values.Single(v=>v.Request==new CurveRequest(f.C("peak_monthly"),PeakMonths[1].Key));
            Equal(prices.Where(p=>p.Block==novemberPeak.Request).All(p=>p.Lineage.SetEquals(novemberPeak.Lineage)),true);
            Equal(prices.Single(p=>p.Start==Fixture.Utc("2031-11-10T13:00:00Z")).Block,novemberPeak.Request);});

        // The window is compiled and aligned once; each scenario only recalibrates the blocks whose marks moved.
        var inputs=BlockShaping.Prepare(additive,offsets,november,january);
        var @base=BlockShaping.Evaluate(inputs,built);
        Check("a block-mark shock recalibrates only its block, exactly once per scenario",()=>{
            var bump=new OpenEtrm.MarketData.Risk.QuoteBump(marks[1].Input,power.Key,PriceUnit.FromQuote(power),ExactDecimal.Parse("1"),OpenEtrm.MarketData.Risk.QuoteBump.Convention);
            var scenario=model.BuildScenarios(snapshot,requests,[bump]).Factors.Single();
            var up=BlockShaping.Evaluate(inputs,scenario.Up,@base);
            Equal(up.CalibratedBlocks,1);Equal(up.ReusedBlocks,3);
            var before=@base.Prices.ToDictionary(p=>p.Start);
            var novemberPeak=new CurveRequest(f.C("peak_monthly"),PeakMonths[1].Key);
            Equal(up.Prices.All(p=>p.Block==novemberPeak?p.Price==before[p.Start].Price.Add(ExactUnitPrice.FromPrice(Price("1"))):p==before[p.Start]),true);
            var down=BlockShaping.Evaluate(inputs,scenario.Down,@base);
            Equal(down.CalibratedBlocks,1);Equal(down.ReusedBlocks,3);});
        Check("a shape or mark correction recalibrates only the affected block",()=>{
            var corrected=offsets.Select(c=>c.Start==Fixture.Utc("2031-11-29T05:00:00Z")?c with{Value=ExactDecimal.Parse("5"),Revision=2}:c).ToImmutableArray();
            var shapeCorrection=Evaluate(additive,built,corrected,november,january,@base);
            Equal(shapeCorrection.CalibratedBlocks,1);Equal(shapeCorrection.ReusedBlocks,3);
            Equal(shapeCorrection.Prices.Where(p=>p.Price!=@base.Prices.Single(b=>b.Start==p.Start).Price).All(p=>p.Block.Contract==OffMonths[1].Key),true);
            var remarked=model.Build(Snapshot(marks.SetItem(2,Mark(PeakMonths[2],"71"))),requests);
            var markCorrection=Evaluate(additive,remarked,offsets,november,january,@base);
            Equal(markCorrection.CalibratedBlocks,1);Equal(markCorrection.ReusedBlocks,3);
            var changedDefinition=Evaluate(Shaped(ShapeMethod.Multiplicative,peak,off),built,factors,november,january,@base);
            Equal(changedDefinition.CalibratedBlocks,4);
            Equal(Evaluate(additive with{ShapeSeries=Guid.NewGuid()},built,offsets,november,january,@base).CalibratedBlocks,4);});
        Check("a shaped definition names its exact policy and operator",()=>{
            Reject("interval_shape",()=>Evaluate(additive with{CalibrationPolicy="duration_weighted_block_mean_v2"},built,offsets,november,december));
            Reject("interval_shape",()=>Evaluate(additive with{OperatorVersion="block_shape_v0"},built,offsets,november,december));
            Reject("interval_shape",()=>Evaluate(additive with{Method=(ShapeMethod)7},built,offsets,november,december));
            Reject("interval_shape",()=>Evaluate(additive with{Blocks=[]},built,offsets,november,december));
            Reject("interval_block",()=>Evaluate(Shaped(ShapeMethod.Additive,peak,off with{Membership=BlockMembership.All}),built,offsets,november,december));});
        return passed;
    }
}
