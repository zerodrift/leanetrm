using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.CustomCurves;

internal static class PowerFlatChecks
{
    internal static void Run()
    {
        var f=new Fixture();
        var clock=PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        var grid=new DeliveryGrid(clock,60,[new(2020,12,25),new(2021,1,1)]);
        var peak=new TouPeak(7,23,false);
        var cutoffs=new[]{"2021-01-29T23:00:00Z","2021-02-26T23:00:00Z","2021-03-31T22:00:00Z"};
        ImmutableArray<MarketContract> Contracts(int offset)=>[..Enumerable.Range(1,3).Select(month=>
            new MarketContract(f.K(offset+month),ContractKind.Forward,new(2021,month,1),new DateOnly(2021,month,1).AddMonths(1),Fixture.Utc(cutoffs[month-1]),null))];
        var pk=new IntervalBlockDefinition(f.C("flat_peak"),BlockMembership.Peak,peak,Contracts(800));
        var off=new IntervalBlockDefinition(f.C("flat_off"),BlockMembership.OffPeak,peak,Contracts(900));
        var at=Fixture.Utc("2020-12-18T16:00:00Z");
        var from=Fixture.Utc("2021-01-01T05:00:00Z");
        var to=Fixture.Utc("2021-04-01T04:00:00Z");
        void Equal<T>(T actual,T expected){if(!EqualityComparer<T>.Default.Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}.");}
        void Refuses(string code,Action action)
        {try{action();}catch(MarketDataException e) when(e.Code==code){return;}throw new Exception("Expected "+code);}
        var result=BlockShaping.PrepareFlat(grid,[pk,off],from,to,at,"flat_forward_only_v1");
        Equal(result.Intervals.Length,2159);
        Equal(result.Anchors.Length,6);
        Equal(result.Intervals.Count(p=>p.OperatingDate==new DateOnly(2021,3,14)),23);
        Equal(result.Intervals.Where(p=>p.OperatingDate==new DateOnly(2021,1,1))
            .All(p=>result.Anchors[result.Owners[result.Intervals.IndexOf(p)]].Request.Curve==off.Curve),true);
        Equal(result.Intervals.Where(p=>p.OperatingDate.Month==1)
            .Count(p=>result.Anchors[result.Owners[result.Intervals.IndexOf(p)]].Request.Curve==pk.Curve),320);
        Equal(result.Owners.Distinct().Count(),6);
        var one=BlockShaping.PrepareFlat(grid,[pk,off],Fixture.Utc("2021-02-02T15:00:00Z"),Fixture.Utc("2021-02-02T16:00:00Z"),at,"flat_forward_only_v1");
        Equal(one.Anchors[one.Owners[0]].Request,new CurveRequest(pk.Curve,pk.Contracts[1].Key));
        Refuses("interval_block_partition",()=>BlockShaping.PrepareFlat(grid,[pk],from,to,at,"flat_forward_only_v1"));
        Refuses("interval_block_partition",()=>BlockShaping.PrepareFlat(grid,[pk,pk,off],from,to,at,"flat_forward_only_v1"));
        Refuses("interval_flat_policy",()=>BlockShaping.PrepareFlat(grid,[pk,off],from,to,at,""));
        Refuses("interval_flat_delivered",()=>BlockShaping.PrepareFlat(grid,[pk,off],Fixture.Utc("2021-01-25T05:00:00Z"),Fixture.Utc("2021-01-26T05:00:00Z"),Fixture.Utc("2021-01-18T16:00:00Z"),"flat_forward_only_v1"));
        var optionBlock=pk with{Contracts=pk.Contracts.SetItem(0,pk.Contracts[0] with{Kind=ContractKind.FutureOption})};
        Refuses("interval_flat_source",()=>BlockShaping.PrepareFlat(grid,[optionBlock,off],from,to,at,"flat_forward_only_v1"));
        var quote=f.Quotes.Single(q=>q.Key.Code=="power_out");
        var curve=new FlatIntervalCurve(f.C("flat_view"),PriceUnit.FromQuote(quote),grid,[pk,off],"flat_forward_only_v1");
        var prepared=PreparedCurve.Prepare(curve,[(from,to)],[],[],at,at);
        Equal(prepared.Months.Length,3);
        Equal(prepared.Dependencies(new(2021,2,1)).Length,2);
        Equal(prepared.Dependencies(new(2021,2,1)).Contains(new CurveRequest(pk.Curve,pk.Contracts[1].Key)),true);
        Refuses("interval_flat_policy",()=>PreparedCurve.Prepare(curve,[(from,to)],[new(from,from.AddHours(1),ExactDecimal.Parse("0"),1)],[],at,at));
        var dailyFrom=new DateOnly(2020,12,19);var cutover=new DateOnly(2021,3,1);
        ImmutableArray<MarketContract> Daily(int offset)=>[..Enumerable.Range(0,72).Select(day=>new MarketContract(f.K(offset+day),ContractKind.Forward,
            dailyFrom.AddDays(day),dailyFrom.AddDays(day+1),at.AddHours(12+24*day),null))];
        var dailyPeak=new IntervalBlockDefinition(f.C("daily_peak"),BlockMembership.DailyPeak,null,Daily(3000));
        var dailyOff=new IntervalBlockDefinition(f.C("daily_off"),BlockMembership.DailyOffPeak,null,Daily(4000));
        ImmutableArray<IntervalBlockDefinition> Sections(DateOnly boundary)=>[
            IntervalBlocks.Section(dailyPeak,dailyFrom,boundary,true),IntervalBlocks.Section(dailyOff,dailyFrom,boundary,true),
            IntervalBlocks.Section(pk,boundary,new(2021,4,1),false),IntervalBlocks.Section(off,boundary,new(2021,4,1),false)];
        var stitchedFrom=clock.StartOfDay(dailyFrom).ToUniversalTime();
        var sections=Sections(cutover);
        var stitch=BlockShaping.PrepareFlat(grid,sections,stitchedFrom,to,at,"flat_forward_only_v1");
        Equal(stitch.Intervals.Length,2471);Equal(stitch.Anchors.Length,146);
        foreach(var date in new[]{new DateOnly(2020,12,20),new DateOnly(2020,12,25),new DateOnly(2021,1,1)})
        {
            var owners=stitch.Intervals.Select((cell,index)=>(cell,index)).Where(v=>v.cell.OperatingDate==date)
                .Select(v=>stitch.Anchors[stitch.Owners[v.index]].Request.Curve).ToArray();
            Equal(owners.Count(c=>c==dailyPeak.Curve),16);Equal(owners.Count(c=>c==dailyOff.Curve),8);
        }
        Equal(stitch.Anchors.Any(a=>a.Request==new CurveRequest(pk.Curve,pk.Contracts[0].Key)),false);
        var earlier=BlockShaping.PrepareFlat(grid,Sections(new(2021,1,1)),stitchedFrom,to,at,"flat_forward_only_v1");
        Equal(earlier.Intervals.Select((cell,index)=>(cell,index)).Where(v=>v.cell.OperatingDate==new DateOnly(2021,1,1))
            .All(v=>earlier.Anchors[earlier.Owners[v.index]].Request.Curve==off.Curve),true);
        Refuses("power_forward_period",()=>IntervalBlocks.Section(pk,new(2021,1,2),new(2021,4,1),false));
        Refuses("interval_block_partition",()=>BlockShaping.PrepareFlat(grid,sections.SetItem(0,sections[0] with{Contracts=sections[0].Contracts.RemoveAt(0)}),stitchedFrom,to,at,"flat_forward_only_v1"));
        var spreadQuote=f.Quotes.Single(q=>q.Key.Code=="power_basis");
        var sourceCurves=ImmutableArray.CreateBuilder<Curve>();
        var series=ImmutableArray.CreateBuilder<MarketSeries>();
        var observations=ImmutableArray.CreateBuilder<SelectedObservation>();
        foreach(var block in new[]{pk,off,dailyPeak,dailyOff})
        {
            var baseCode="base_"+block.Curve.Code;var spreadCode="spread_"+block.Curve.Code;
            Curve Direct(string code,QuoteSpecification specification,SettlementKind kind)=>new(f.C(code),specification.Key,f.R("location"),f.R("hourly"),
                new(kind,f.R("index"),kind==SettlementKind.IndexDifference?f.R("index_b"):null),CurveRule.Direct,[..block.Contracts.Select(c=>c.Key)],[],ObservationRole.TraderMark,f.R("source"),null);
            sourceCurves.Add(Direct(baseCode,quote,SettlementKind.Index));
            sourceCurves.Add(Direct(spreadCode,spreadQuote,SettlementKind.IndexDifference));
            sourceCurves.Add(new(block.Curve,quote.Key,f.R("location"),f.R("hourly"),new(SettlementKind.Index,f.R("index"),null),CurveRule.Add,
                [..block.Contracts.Select(c=>c.Key)],
                [new(InputSlot.Base,f.C(baseCode),SourceExpiryRule.RequireLive,[..block.Contracts.Select(c=>new ConstituentMapping(c.Key,c.Key))]),
                 new(InputSlot.Spread,f.C(spreadCode),SourceExpiryRule.RequireLive,[..block.Contracts.Select(c=>new ConstituentMapping(c.Key,c.Key))])],null,null,null));
            for(var i=0;i<block.Contracts.Length;i++)
                foreach(var basis in new[]{false,true})
                {
                    var isPeak=block.Membership is BlockMembership.Peak or BlockMembership.DailyPeak;
                    var key=f.S((block==dailyPeak?1000:block==dailyOff?2000:isPeak?100:200)+(basis?(block==dailyPeak||block==dailyOff?100:10):0)+i);
                    var specification=basis?spreadQuote:quote;
                    var value=basis?(isPeak?"2":"-1"):(isPeak?"50":"30");
                    series.Add(new(key,block.Contracts[i].Key,specification.Key,null));
                    observations.Add(new(new(key,InputUse.Variable),new(new(f.Revision.CatalogId,Guid.NewGuid()),key,f.R("source"),ObservationRole.TraderMark,PublicationState.Final,at,ExactDecimal.Parse(value))));
                }
        }
        var model=new CurveModel(f.Revision,f.Reference,f.Quantities,f.Quotes,[..pk.Contracts,..off.Contracts,..dailyPeak.Contracts,..dailyOff.Contracts],sourceCurves.ToImmutable(),[],[],series.ToImmutable());
        var snapshot=new MarketSnapshot(Guid.NewGuid(),f.Revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,observations.ToImmutable());
        ImmutableArray<CurveRequest> dependencies=[..prepared.Months.SelectMany(month=>prepared.Dependencies(month))];
        var built=model.Build(snapshot,dependencies);
        var resolver=new IntervalPriceResolver(built,ImmutableDictionary<CurveKey,PreparedCurve>.Empty.Add(curve.Key,prepared));
        var january=resolver.Prices(curve.Key,new(2021,1,1));
        Equal(january.Sum(p=>decimal.Parse(p.Price!.ToExactString(),System.Globalization.CultureInfo.InvariantCulture)),28936m);
        Equal(january.All(p=>p.Anchor is not null&&p.Anchor.Request.Curve!=curve.Key&&p.ForwardLineage.Count==2&&p.DueAt is null),true);
        foreach(var input in observations.Where(o=>o.Input.Series==f.S(100)||o.Input.Series==f.S(110)))
        {
            var specification=input.Observation.Series==f.S(100)?quote:spreadQuote;
            var bump=new OpenEtrm.MarketData.Risk.QuoteBump(input.Input,specification.Key,PriceUnit.FromQuote(specification),ExactDecimal.Parse("1"),OpenEtrm.MarketData.Risk.QuoteBump.Convention);
            var scenarios=model.BuildScenarios(snapshot,dependencies,[bump]).Factors.Single();
            var up=new IntervalPriceResolver(scenarios.Up,ImmutableDictionary<CurveKey,PreparedCurve>.Empty.Add(curve.Key,prepared),resolver).Prices(curve.Key,new(2021,1,1));
            Equal(up.Sum(p=>decimal.Parse(p.Price!.ToExactString(),System.Globalization.CultureInfo.InvariantCulture)),29256m);
        }
        // An actual replaces the whole outright price, including its forecast basis.
        // The explicit policy keeps the native mark on other live hours; it does not
        // recalibrate the block to preserve a whole-period average.
        var fixingAt=Fixture.Utc("2021-01-04T14:00:00Z");
        var fixingFrom=fixingAt.AddHours(-1);var fixingTo=fixingAt.AddHours(1);
        var resolving=curve with{Policy=FlatIntervalCurve.ReplaceActuals,ActualRule=new("interval_end",60,IntervalActualRule.SupportedVersion)};
        var actual=new IntervalActual(fixingFrom,fixingAt,new(ExactDecimal.Parse("7"),curve.Unit),fixingAt,Guid.NewGuid(),1);
        var mixed=PreparedCurve.Prepare(resolving,[(fixingFrom,fixingTo)],[],[actual],fixingAt,fixingAt);
        var mixedSnapshot=new MarketSnapshot(Guid.NewGuid(),f.Revision,fixingAt,fixingAt,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,observations.ToImmutable());
        var mixedBuild=model.Build(mixedSnapshot,mixed.Dependencies(new(2021,1,1)));
        var mixedResolver=new IntervalPriceResolver(mixedBuild,ImmutableDictionary<CurveKey,PreparedCurve>.Empty.Add(curve.Key,mixed));
        var mixedPrices=mixedResolver.Prices(curve.Key,new(2021,1,1));
        Equal(mixedPrices[0].State,IntervalPriceState.Actual);Equal(mixedPrices[0].Price!.ToExactString(),"7");
        Equal(mixedPrices[0].ForwardLineage.Count,0);Equal(mixedPrices[0].Actual!.BatchId,actual.BatchId);
        Equal(mixedPrices[1].State,IntervalPriceState.Projected);Equal(mixedPrices[1].Price!.ToExactString(),"52");
        Equal(mixed.Dependencies(new(2021,1,1)).Length,1);
        Refuses("interval_actual_missing",()=>PreparedCurve.Prepare(resolving,[(fixingFrom,fixingTo)],[],[],fixingAt.AddHours(1),fixingAt.AddHours(1)));
        Refuses("interval_flat_policy",()=>PreparedCurve.Prepare(resolving with{ActualRule=null},[(fixingFrom,fixingTo)],[],[actual],fixingAt,fixingAt));
        var expiredAt=Fixture.Utc("2021-02-01T05:00:00Z");var endedFrom=expiredAt.AddHours(-1);
        var endedActual=actual with{Start=endedFrom,End=expiredAt,KnownAt=expiredAt};
        var fullyFixed=PreparedCurve.Prepare(resolving,[(endedFrom,expiredAt)],[],[endedActual],expiredAt,expiredAt);
        Equal(fullyFixed.Dependencies(new(2021,1,1)).Length,0);
        var fixedSnapshot=new MarketSnapshot(Guid.NewGuid(),f.Revision,expiredAt,expiredAt,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,observations.ToImmutable());
        var fixedBuild=model.Build(fixedSnapshot,[]);
        var emptyPrices=new IntervalPriceResolver(fixedBuild,ImmutableDictionary<CurveKey,PreparedCurve>.Empty);
        Refuses("risk_dependency",()=>emptyPrices.Prices(curve.Key,new(2021,1,1)));

        var fixedPrice=new IntervalPriceResolver(fixedBuild,ImmutableDictionary<CurveKey,PreparedCurve>.Empty.Add(curve.Key,fullyFixed)).Prices(curve.Key,new(2021,1,1)).Single();
        Equal(fixedPrice.Price!.ToExactString(),"7");Equal(fixedPrice.ForwardLineage.Count,0);
        var da=actual with{Start=fixingAt,End=fixingTo,KnownAt=fixingAt.AddMinutes(-1)};
        var early=PreparedCurve.Prepare(resolving,[(fixingAt,fixingTo)],[],[da],fixingAt,fixingAt);
        Equal(early.Dependencies(new(2021,1,1)).Length,0);
        var later=da with{KnownAt=fixingAt.AddMinutes(1)};
        var hindsight=PreparedCurve.Prepare(resolving,[(fixingAt,fixingTo)],[],[later],fixingAt,later.KnownAt);
        Equal(hindsight.Dependencies(new(2021,1,1)).Length,1);
        var replayRule=new IntervalActualRule("interval_end",0,IntervalActualRule.PjmDayAheadReplay){ReleaseClock=clock};
        Equal(replayRule.ReleaseAt(new(2021,3,15)),Fixture.Utc("2021-03-14T17:30:00Z"));
        Equal(replayRule.ReleaseAt(new(2020,11,2)),Fixture.Utc("2020-11-01T18:30:00Z"));
        var release=Fixture.Utc("2021-01-03T18:30:00Z");
        var learned=Fixture.Utc("2026-11-11T00:00:00Z");
        var monday=Fixture.Utc("2021-01-04T12:00:00Z");var tuesday=monday.AddDays(1);
        var replay=curve with{Policy=FlatIntervalCurve.ReplaceActuals,ActualRule=replayRule};
        IntervalActual Captured(DateTimeOffset start,string price)=>new(start,start.AddHours(1),new(ExactDecimal.Parse(price),curve.Unit),learned,Guid.NewGuid(),1);
        var mondayActual=Captured(monday,"7");var tuesdayActual=Captured(tuesday,"9");
        IntervalPriceResolution[] Released(DateTimeOffset valuation,IntervalActual first)
        {
            var inputs=PreparedCurve.Prepare(replay,[(monday,monday.AddHours(1)),(tuesday,tuesday.AddHours(1))],[],[first,tuesdayActual],valuation,learned);
            var market=new MarketSnapshot(Guid.NewGuid(),f.Revision,valuation,valuation,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,observations.ToImmutable())
                .WithKnowledge(Guid.NewGuid(),learned);
            var values=model.Build(market,inputs.Dependencies(new(2021,1,1)));
            return new IntervalPriceResolver(values,ImmutableDictionary<CurveKey,PreparedCurve>.Empty.Add(curve.Key,inputs)).Prices(curve.Key,new(2021,1,1)).ToArray();
        }
        var beforeRelease=Released(release.AddTicks(-10),mondayActual);
        Equal(beforeRelease.All(p=>p.State==IntervalPriceState.Projected),true);
        Equal(Released(release.AddTicks(-10),mondayActual with{KnownAt=release.AddDays(-1)}).All(p=>p.State==IntervalPriceState.Projected),true);
        var onRelease=Released(release,mondayActual);
        Equal(onRelease[0].Price!.ToExactString(),"7");Equal(onRelease[0].State,IntervalPriceState.Actual);
        Equal(onRelease[0].Actual!.KnownAt,learned);Equal(onRelease[0].ForwardLineage.Count,0);
        Equal(onRelease[1].State,IntervalPriceState.Projected);Equal(onRelease[1].Price!.ToExactString(),"52");
        var corrected=Released(release,mondayActual with{Price=new(ExactDecimal.Parse("11"),curve.Unit),Revision=2});
        Equal(corrected[0].Price!.ToExactString(),"11");Equal(onRelease[0].Price!.ToExactString(),"7");
        Refuses("interval_actual",()=>PreparedCurve.Prepare(replay with{ActualRule=replayRule with{ReleaseClock=null}},[(monday,monday.AddHours(1))],[],[mondayActual],release,learned));
        var decemberHour=Fixture.Utc("2020-12-18T17:00:00Z");
        var prefixCurve=replay with{ActualPrefix=new(Fixture.Utc("2020-12-01T05:00:00Z"),Fixture.Utc("2021-01-01T05:00:00Z"))};
        var prefixActual=Captured(decemberHour,"7");
        var prefix=PreparedCurve.Prepare(prefixCurve,[(decemberHour,decemberHour.AddHours(1)),(monday,monday.AddHours(1))],[],[prefixActual,mondayActual],at,learned);
        Equal(prefix.Dependencies(new(2020,12,1)).Length,0);Equal(prefix.Dependencies(new(2021,1,1)).Length,1);
        var prefixBuild=model.Build(new MarketSnapshot(Guid.NewGuid(),f.Revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,observations.ToImmutable())
            .WithKnowledge(Guid.NewGuid(),learned),prefix.Dependencies(new(2021,1,1)));
        var prefixResolver=new IntervalPriceResolver(prefixBuild,ImmutableDictionary<CurveKey,PreparedCurve>.Empty.Add(curve.Key,prefix));
        var historical=prefixResolver.Prices(curve.Key,new(2020,12,1)).Single();
        Equal(historical.State,IntervalPriceState.Actual);Equal(historical.Price!.ToExactString(),"7");
        Equal(historical.ConstructionSource is null,true);Equal(historical.ForwardLineage.Count,0);
        Equal(prefixResolver.Prices(curve.Key,new(2021,1,1)).Single().Price!.ToExactString(),"52");
        Refuses("interval_actual_missing",()=>PreparedCurve.Prepare(prefixCurve,[(decemberHour,decemberHour.AddHours(1))],[],[],at,learned));
        Refuses("interval_flat_delivered",()=>PreparedCurve.Prepare(resolving,[(Fixture.Utc("2021-01-30T05:00:00Z"),Fixture.Utc("2021-01-30T06:00:00Z"))],[],[],Fixture.Utc("2021-01-29T23:00:00Z"),Fixture.Utc("2021-01-29T23:00:00Z")));
        var stitchedCurve=curve with{Key=f.C("daily_monthly"),Blocks=sections};
        var stitchedPrepared=PreparedCurve.Prepare(stitchedCurve,[(stitchedFrom,to)],[],[],at,at);
        ImmutableArray<CurveRequest> stitchedDependencies=[..stitchedPrepared.Months.SelectMany(m=>stitchedPrepared.Dependencies(m)).Distinct()];
        Equal(stitchedDependencies.Length,146);
        var stitchedBuild=model.Build(snapshot,stitchedDependencies);
        var stitchedResolver=new IntervalPriceResolver(stitchedBuild,ImmutableDictionary<CurveKey,PreparedCurve>.Empty.Add(stitchedCurve.Key,stitchedPrepared));
        decimal Total(IntervalPriceResolver r)=>stitchedPrepared.Months.SelectMany(m=>r.Prices(stitchedCurve.Key,m))
            .Sum(p=>decimal.Parse(p.Price!.ToExactString(),System.Globalization.CultureInfo.InvariantCulture));
        Equal(Total(stitchedResolver),106619m);
        var shapedDayFrom=Fixture.Utc("2020-12-19T05:00:00Z");var shapedDayTo=shapedDayFrom.AddDays(1);
        var shapedDay=stitchedCurve with{PriceShape=new(stitchedCurve.Key.Code,ShapeMethod.Additive,BlockShaping.CalibrationPolicy,BlockShaping.OperatorVersion,Guid.NewGuid(),grid,[..sections.Where(b=>b.Membership is BlockMembership.DailyPeak or BlockMembership.DailyOffPeak)])};
        var offsets=PowerHours.Compile(new(2020,12,19),new(2020,12,20),clock,TimeOfUse.All(60),[],60)
            .Select(h=>new SeriesCell(h.Start,h.End,ExactDecimal.Parse(h.HourBeginning is >=7 and <15?"4":"-4"),1)).ToArray();
        var shapedInputs=PreparedCurve.Prepare(shapedDay,[(shapedDayFrom,shapedDayTo)],offsets,[],at,at);
        var shapedBuild=model.Build(snapshot,shapedInputs.Dependencies(new(2020,12,1)));
        var shapedPrices=new IntervalPriceResolver(shapedBuild,ImmutableDictionary<CurveKey,PreparedCurve>.Empty.Add(shapedDay.Key,shapedInputs)).Prices(shapedDay.Key,new(2020,12,1));
        Equal(shapedPrices.Single(p=>clock.OperatingDate(p.Start)==new DateOnly(2020,12,19)&&TimeZoneInfo.ConvertTime(p.Start,clock.Zone).Hour==7).Price!.ToExactString(),"56");
        Equal(shapedPrices.Single(p=>TimeZoneInfo.ConvertTime(p.Start,clock.Zone).Hour==15).Price!.ToExactString(),"48");
        Equal(shapedPrices.Where(p=>TimeZoneInfo.ConvertTime(p.Start,clock.Zone).Hour<7).All(p=>p.Price!.ToExactString()=="29"),true);
        Equal(shapedPrices.Sum(p=>decimal.Parse(p.Price!.ToExactString(),System.Globalization.CultureInfo.InvariantCulture)),1064m);
        Equal(shapedPrices.Sum(p=>decimal.Parse(p.Price!.ToExactString(),System.Globalization.CultureInfo.InvariantCulture)*(TimeZoneInfo.ConvertTime(p.Start,clock.Zone).Hour is >=7 and <15?2:1)),1512m);
        var partialCurve=shapedDay with{Policy=FlatIntervalCurve.ReplaceActuals,ActualRule=new("interval_end",0,IntervalActualRule.SupportedVersion)};
        var fixedCell=offsets[7];
        var oneActual=new IntervalActual(fixedCell.Start,fixedCell.End,new(ExactDecimal.Parse("10"),curve.Unit),at,Guid.NewGuid(),1);
        var partialShape=PreparedCurve.Prepare(partialCurve,[(shapedDayFrom,shapedDayTo)],offsets,[oneActual],at,at);
        var partialBuild=model.Build(snapshot,partialShape.Dependencies(new(2020,12,1)));
        var partialPrices=new IntervalPriceResolver(partialBuild,ImmutableDictionary<CurveKey,PreparedCurve>.Empty.Add(shapedDay.Key,partialShape)).Prices(shapedDay.Key,new(2020,12,1));
        Equal(partialPrices.Single(p=>p.Start==fixedCell.Start).Price!.ToExactString(),"10");
        Equal(partialPrices.Single(p=>p.Start==fixedCell.End).Price!.ToExactString(),"56");
        Equal(partialPrices.Single(p=>TimeZoneInfo.ConvertTime(p.Start,clock.Zone).Hour==15).Price!.ToExactString(),"48");
        Refuses("interval_shape_coverage",()=>PreparedCurve.Prepare(partialCurve,[(shapedDayFrom,shapedDayTo)],[..offsets.Where(c=>c.Start!=fixedCell.Start)],[oneActual],at,at));
        var allActual=offsets.Select(c=>new IntervalActual(c.Start,c.End,new(ExactDecimal.Parse("10"),curve.Unit),at,Guid.NewGuid(),1)).ToArray();
        var actualOnly=PreparedCurve.Prepare(partialCurve,[(shapedDayFrom,shapedDayTo)],[],allActual,at,at);
        Equal(actualOnly.Dependencies(new(2020,12,1)).Length,0);Equal(actualOnly.ShapeInputs.Length,0);
        var monthlyOnly=PreparedCurve.Prepare(shapedDay,[(Fixture.Utc("2021-03-01T05:00:00Z"),to)],[],[],at,at);
        Equal(monthlyOnly.ShapeInputs.Length,0);Equal(monthlyOnly.Dependencies(new(2021,3,1)).Length,2);
        Refuses("interval_flat_policy",()=>PreparedCurve.Prepare(shapedDay with{PriceShape=shapedDay.PriceShape! with{Blocks=[dailyPeak]}},[(shapedDayFrom,shapedDayTo)],offsets,[],at,at));
        Console.WriteLine("PASS additive native daily shaping: 56/48 peak,29 off, unchanged1064 flat MWh value and1512 shaped-MW value");
        var selectedPeak=PowerHours.Compile(dailyFrom,new(2021,4,1),clock,IntervalBlocks.Mask(BlockMembership.Peak,60,peak),[..grid.Holidays],60)
            .Select(h=>h.Start).ToHashSet();
        Equal(selectedPeak.Count,1136);
        var selectedOff=stitch.Intervals.Where(h=>!selectedPeak.Contains(h.Start)).Select(h=>h.Start).ToHashSet();
        Equal(selectedOff.Count,1335);
        Equal(stitch.Intervals.Select((cell,index)=>(cell,index)).Count(v=>selectedOff.Contains(v.cell.Start)&&
            stitch.Anchors[stitch.Owners[v.index]].Request.Curve==dailyPeak.Curve),384);
        decimal ProductTotal(IntervalPriceResolver r,HashSet<DateTimeOffset> selected)=>stitchedPrepared.Months.SelectMany(m=>r.Prices(stitchedCurve.Key,m))
            .Where(p=>selected.Contains(p.Interval.Start)).Sum(p=>decimal.Parse(p.Price!.ToExactString(),System.Globalization.CultureInfo.InvariantCulture));
        Equal(ProductTotal(stitchedResolver,selectedPeak),59072m);
        Equal(ProductTotal(stitchedResolver,selectedOff),47547m);
        foreach(var (seriesId,peakHours,offHours) in new[]{(1000,0,16),(1100,0,16),(1002,16,0),(1102,16,0),(2000,0,8),(2100,0,8),
            (102,368,0),(112,368,0),(202,0,375),(212,0,375)})
        {
            var input=observations.Single(o=>o.Input.Series==f.S(seriesId));
            var specification=seriesId is 1100 or 1102 or 2100 or 112 or 212?spreadQuote:quote;
            var bump=new OpenEtrm.MarketData.Risk.QuoteBump(input.Input,specification.Key,PriceUnit.FromQuote(specification),ExactDecimal.Parse("1"),OpenEtrm.MarketData.Risk.QuoteBump.Convention);
            var scenarios=model.BuildScenarios(snapshot,stitchedDependencies,[bump]).Factors.Single();
            IntervalPriceResolver Scenario(CurveBuild build)=>new(build,ImmutableDictionary<CurveKey,PreparedCurve>.Empty.Add(stitchedCurve.Key,stitchedPrepared),stitchedResolver);
            Equal(Total(Scenario(scenarios.Up)),106619m+peakHours+offHours);Equal(Total(Scenario(scenarios.Down)),106619m-peakHours-offHours);
            Equal(ProductTotal(Scenario(scenarios.Up),selectedPeak),59072m+peakHours);
            Equal(ProductTotal(Scenario(scenarios.Down),selectedPeak),59072m-peakHours);
            Equal(ProductTotal(Scenario(scenarios.Up),selectedOff),47547m+offHours);
            Equal(ProductTotal(Scenario(scenarios.Down),selectedOff),47547m-offHours);
        }
        RecipeNode Node(string id,string kind,Dictionary<string,string> parameters,Dictionary<string,string> inputs)=>
            new(id,kind,parameters.ToImmutableDictionary(),inputs.ToImmutableDictionary());
        var definition=new RecipeDefinition("flat_location","Synthetic location forward intervals",
            [Node("pk","source",new(){{"curve",pk.Curve.Code}},new()),Node("off","source",new(){{"curve",off.Curve.Code}},new()),
             Node("hours","power_intervals",new(){{"implementation","flat_forward_only_v1"},{"product",Guid.NewGuid().ToString("D")},{"productVersion","1"},
                {"profile","hourly"},{"profileMapping","exact_native_grid_terms_v1"},{"market","DA"},{"from","2021-01-01"},{"to","2021-04-01"}},new(){{"peak","pk"},{"offPeak","off"}}),
             Node("result","output",new(),new(){{"values","hours"}})],"result");
        RecipeEngine.Validate(definition);
        var settledNode=definition.Nodes[2] with{Parameters=definition.Nodes[2].Parameters
            .Add("settlementPublication",Guid.NewGuid().ToString("D")).Add("settlementVersion","1").Add("settlementIndex","index")
            .Add("settlementSource",Guid.NewGuid().ToString("D")).Add("settlementPolicy","captured_required_for_ended_v1")
            .Add("projectionPolicy",FlatIntervalCurve.ReplaceActuals)};
        RecipeEngine.Validate(definition with{Nodes=definition.Nodes.SetItem(2,settledNode)});
        RecipeEngine.Validate(definition with{Nodes=definition.Nodes.SetItem(2,settledNode with{Parameters=settledNode.Parameters
            .SetItem("settlementPolicy","pjm_da_normal_release_replay_v1")})});
        Refuses("recipe_definition",()=>RecipeEngine.Validate(definition with{Nodes=definition.Nodes.SetItem(2,settledNode with{Parameters=settledNode.Parameters.Remove("settlementIndex")})}));
        Refuses("recipe_definition",()=>RecipeEngine.Validate(definition with{Nodes=definition.Nodes.SetItem(2,settledNode with{Parameters=settledNode.Parameters.SetItem("settlementPolicy","assume_final")})}));

        Refuses("recipe_definition",()=>RecipeEngine.Validate(definition with{Nodes=definition.Nodes.SetItem(2,definition.Nodes[2] with{Parameters=definition.Nodes[2].Parameters.Remove("implementation")})}));
        var stitched=definition with{Code="daily_monthly_location",Nodes=[
            definition.Nodes[0],definition.Nodes[1],
            Node("daily_pk","source",new(){{"curve","daily_peak"}},new()),
            Node("daily_off","source",new(){{"curve","daily_off"}},new()),
            definition.Nodes[2] with{Parameters=definition.Nodes[2].Parameters.SetItem("implementation","daily_then_monthly_forward_v1").SetItem("from","2020-12-19").Add("cutover","2021-03-01"),
                Inputs=definition.Nodes[2].Inputs.Add("dailyPeak","daily_pk").Add("dailyOffPeak","daily_off")},definition.Nodes[3]]};
        RecipeEngine.Validate(stitched);
        Equal(RecipeEngine.PowerIntervalSettings(stitched.Nodes[4]).From,new DateOnly(2020,12,19));
        Refuses("recipe_definition",()=>RecipeEngine.Validate(stitched with{Nodes=stitched.Nodes.SetItem(4,stitched.Nodes[4] with{Parameters=stitched.Nodes[4].Parameters.SetItem("cutover","2021-02-19")})}));
        RecipeDefinition Product(IntervalBlockDefinition front,IntervalBlockDefinition back)=>new("synthetic_product","Synthetic mixed tenor product",[
            Node("daily","source",new(){{"curve",front.Curve.Code}},new()),Node("monthly","source",new(){{"curve",back.Curve.Code}},new()),
            Node("daily_map","weighted_contracts",new(){{"aggregation",RecipeEngine.SignedSum}},new(){{"source","daily"}}) with{
                ContractWeights=[..front.Contracts.Select((c,i)=>new RecipeContractWeight(i+1,"source",c.Key.Id,"1"))]},
            Node("monthly_map","weighted_contracts",new(){{"aggregation",RecipeEngine.SignedSum}},new(){{"source","monthly"}}) with{
                ContractWeights=[..back.Contracts.Select((c,i)=>new RecipeContractWeight(i+1,"source",c.Key.Id,"1"))]},
            Node("dates","dates",new(){{"mode","context"},{"sampling","context_utc_time"},{"corrections","exact_context_frame_history_latest_visible"}},new()),
            Node("daily_values","sample",new(),new(){{"selection","daily_map"},{"dates","dates"}}),
            Node("monthly_values","sample",new(),new(){{"selection","monthly_map"},{"dates","dates"}}),
            Node("splice","splice",new(){{"from","2020-12-19"},{"cutover","2021-03-01"},{"to","2021-04-01"},{"implementation",RecipeEngine.WholeDeliverySplice}},new(){{"before","daily_values"},{"after","monthly_values"}}),
            Node("result","output",new(),new(){{"values","splice"}})],"result");
        var productCurve=Product(dailyPeak,pk);var nativeProduct=RecipeEngine.PowerProductSplice(productCurve,dailyFrom,new(2021,4,1));
        Equal(nativeProduct.Cutover,cutover);Equal(nativeProduct.Sources[0].Contracts.Length,72);Equal(nativeProduct.Sources[1].Contracts.Length,3);
        Refuses("recipe_definition",()=>RecipeEngine.PowerProductSplice(productCurve with{Nodes=productCurve.Nodes.SetItem(2,productCurve.Nodes[2] with{
            ContractWeights=productCurve.Nodes[2].ContractWeights.SetItem(0,productCurve.Nodes[2].ContractWeights[0] with{Weight="2"})})},dailyFrom,new(2021,4,1)));
        Refuses("power_forward_period",()=>IntervalBlocks.Section(dailyPeak with{Contracts=dailyPeak.Contracts.RemoveAt(5)},dailyFrom,cutover,true));
        var overrides=new OpenEtrm.MarketData.Curves.ContractPriceTermsSet(
            new("derived_inputs_v1",null,null,"single_assessment_v1","require_final_v1",null),[],"explicit_contracts_v1",
            [..dailyPeak.Contracts.Select(c=>new ConstituentMapping(c.Key,c.Key))]);
        var overridden=productCurve with{Nodes=productCurve.Nodes.SetItem(0,productCurve.Nodes[0] with{ContractTerms=overrides})};
        RecipeEngine.Validate(overridden);
        Refuses("recipe_definition",()=>RecipeEngine.PowerProductSplice(overridden,dailyFrom,new(2021,4,1)));
        var savedProducts=definition with{Code="saved_product_location",Nodes=[
            Node("pk","published_source",new(){{"selection","all_output_points_v1"}},new()) with{PublishedSource=new(f.Revision.CatalogId,Guid.NewGuid(),1,"result")},
            Node("off","published_source",new(){{"selection","all_output_points_v1"}},new()) with{PublishedSource=new(f.Revision.CatalogId,Guid.NewGuid(),1,"result")},
            definition.Nodes[2] with{Parameters=definition.Nodes[2].Parameters.SetItem("implementation","saved_product_forwards_v1")},definition.Nodes[3]]};
        RecipeEngine.Validate(savedProducts);
        Console.WriteLine("PASS daily/monthly: 146 anchors, 2471 cells, trade peak1136/off1335, 384 off-trade daily-peak hours, independent futures/basis product scenario sensitivities");
        Console.WriteLine("PASS power flat: exact anchor ownership, January TOU/holiday counts, spring DST, sparse demand, policy/partition/whole-source eligibility refusal");
    }
}
