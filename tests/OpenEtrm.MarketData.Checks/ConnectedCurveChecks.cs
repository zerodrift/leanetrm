using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.Shared.Temporal;

internal static class ConnectedCurveChecks
{
    public static void Run()
    {
        var f=new Fixture();
        var plan=ConnectedCurves.Compile(f.Definitions,[f.Spread.Key],f.Context,f.Resolver);
        var result=plan.Evaluate(f.Spread.Key);
        // (1 + 2 + 4) / 3 = 7/3; the dependent spread is 2*(7/3)-1 = 11/3.
        Equal("A computed spread retains its upstream exact mean",result.Numerator,"11");
        Equal("A repeating mean is never rounded before the spread",result.Denominator,"3");
        Equal("All five connected points belong to one plan",plan.Points.Count,5);
        Equal("Three raw observations retain their shared primitive identities",plan.Primitives.Length,3);
        Equal("Definition enumeration order cannot alter the resolved manifest",ConnectedCurves.Compile(f.Definitions.Reverse(),[f.Spread.Key],f.Context,f.Resolver).ManifestId,plan.ManifestId);
        var session=plan.CreateSession();var original=session.Get(plan.IndexOf(f.Spread.Key));
        var shifted=session.WithOffsets(new Dictionary<int,ExactUnitPrice>{{plan.IndexOf(f.RawPoints[1].Key),ExactUnitPrice.FromPrice(new(ExactDecimal.Parse("3"),f.Mean.Unit))}});
        Equal("An upstream shock reprices its computed mean and spread",shifted.Get(plan.IndexOf(f.Spread.Key)).Numerator,"17");
        Equal("An upstream shock preserves its exact denominator",shifted.Get(plan.IndexOf(f.Spread.Key)).Denominator,"3");
        Equal("The parent session remains unchanged",session.Get(plan.IndexOf(f.Spread.Key)),original);

        ConnectedCurvePlan ReplaceMean(ConnectedCurveExpression expression,PriceUnit? unit=null)=>ConnectedCurves.Compile(
            f.Definitions.Select(d=>d.Key==f.Mean.Key.Curve?d with{Points=[f.Mean with{Expression=expression,Unit=unit??f.Mean.Unit}]}:d),[f.Spread.Key],f.Context,f.Resolver);
        var terms=f.RawPoints.Select((p,i)=>new ConnectedWeightedTerm(CurvePointReference.Exact(p.Key),ExactDecimal.Parse((i+1).ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToImmutableArray();
        var weighted=ReplaceMean(new ConnectedWeightedMean(terms));
        Equal("Weights are normalized by their explicit positive sum",weighted.Evaluate(f.Mean.Key).Numerator,"17");
        Equal("Weighted mean preserves its rational divisor",weighted.Evaluate(f.Mean.Key).Denominator,"6");
        var tiny=ReplaceMean(new ConnectedWeightedSum([new(CurvePointReference.Exact(f.RawPoints[0].Key),ExactDecimal.Parse("0.000000000000000001"))]));
        Equal("Small weights retain exact scale",tiny.Evaluate(f.Mean.Key).Denominator,"1000000000000000000");
        Reject("Missing weight",()=>ReplaceMean(new ConnectedWeightedMean(terms.SetItem(0,terms[0] with{Weight=null}))));
        Reject("Negative mean weight",()=>ReplaceMean(new ConnectedWeightedMean(terms.SetItem(0,terms[0] with{Weight=ExactDecimal.Parse("-1")}))));
        Reject("Zero denominator",()=>ReplaceMean(new ConnectedWeightedMean(terms.Select(t=>t with{Weight=default(ExactDecimal)}).ToImmutableArray())));
        Reject("Missing uniform rule",()=>ReplaceMean(new ConnectedMean([CurvePointReference.Exact(f.RawPoints[0].Key)],"")));
        Reject("Repeated dependency identity",()=>ReplaceMean(new ConnectedMean([CurvePointReference.Exact(f.RawPoints[0].Key),CurvePointReference.Exact(f.RawPoints[0].Key)],ConnectedCurves.UniformMean)));
        Reject("Missing point",()=>ReplaceMean(new ConnectedMean([CurvePointReference.Exact(new(f.RawPoints[0].Key.Curve,Guid.NewGuid()))],ConnectedCurves.UniformMean)));
        Reject("Different exact price units",()=>ReplaceMean(f.Mean.Expression,new(f.Mean.Unit.Currency,f.Mean.Unit.Quantity,ExactDecimal.Parse("100"))));
        Reject("A computed curve cycle",()=>ReplaceMean(new ConnectedMean([CurvePointReference.Exact(f.Spread.Key)],ConnectedCurves.UniformMean)));
        Reject("Connected SQL is unsupported",()=>ReplaceMean(new ConnectedSqlTransform("sql_mean_dates_v1")));

        // Exact aliases of one observation share a primitive rather than a copy.
        var first=f.RawPoints[0];var source=(ConnectedSource)first.Expression;
        var aliasKey=new CurveKey(first.Key.Curve.Revision,"raw_alias");
        var alias=first with{Key=new(aliasKey,Guid.NewGuid()),Expression=source with{Request=source.Request with{Curve=aliasKey}}};
        var aliases=ConnectedCurves.Compile(f.Definitions.Add(new(aliasKey,[alias])),[first.Key,alias.Key],f.Context,f.Resolver);
        Equal("Source aliases share one primitive node",aliases.IndexOf(first.Key),aliases.IndexOf(alias.Key));
        Equal("Only reached sources are compiled",aliases.DependencyPlan.NodeCount,1);
        Equal("Reversing shared-primitive roots preserves the manifest",ConnectedCurves.Compile(f.Definitions.Add(new(aliasKey,[alias])),[alias.Key,first.Key],f.Context,f.Resolver).ManifestId,aliases.ManifestId);
        var conflictingContract=alias with{Contract=alias.Contract! with{Expiry=alias.Contract.Expiry.AddDays(1)}};
        Reject("One exact contract cannot carry conflicting source alias metadata",()=>ConnectedCurves.Compile(f.Definitions.Add(new(aliasKey,[conflictingContract])),[first.Key,alias.Key],f.Context,f.Resolver));
        var conflicting=alias with{Expression=((ConnectedSource)alias.Expression) with{Observation=source.Observation with{Key=new(source.Observation.Key.CatalogId,Guid.NewGuid())}}};
        Reject("A variable series has only one pinned observation",()=>ConnectedCurves.Compile(f.Definitions.Add(new(aliasKey,[conflicting])),[first.Key,conflicting.Key],f.Context,f.Resolver));
        var broken=f.Spread with{Expression=new ConnectedSqlTransform("unsupported")};
        Equal("Unconsumed derived definitions do not materialize",ConnectedCurves.Compile(f.Definitions.Select(d=>d.Key==broken.Key.Curve?d with{Points=[broken]}:d),[first.Key],f.Context,f.Resolver).Points.Count,1);

        void RejectSource(string label,ConnectedSource changed)=>Reject(label,()=>ConnectedCurves.Compile(
            f.Definitions.Select(d=>d.Key==first.Key.Curve?d with{Points=d.Points.SetItem(0,first with{Expression=changed})}:d),[first.Key],f.Context,f.Resolver));
        RejectSource("Future observation",source with{Observation=source.Observation with{ObservedAt=f.Context.MarketAsOf.AddSeconds(1)}});
        RejectSource("Non-final observation",source with{Observation=source.Observation with{State=PublicationState.Preliminary}});
        RejectSource("Variable index fixing",source with{Observation=source.Observation with{Role=ObservationRole.IndexFixing}});
        RejectSource("A numeric parameter is not a quoted price",source with{Input=source.Input with{Use=InputUse.FrozenFact},Observation=source.Observation with{Role=ObservationRole.Parameter}});
        RejectSource("Observation from a different series",source with{Observation=source.Observation with{Series=new(source.Input.Series.Revision,Guid.NewGuid())}});
        RejectSource("A series registered to another contract cannot price this source",source with{Series=source.Series with{Contract=new(source.Request.Contract.Revision,Guid.NewGuid())}});
        RejectSource("A series registered to another quote cannot price this source",source with{Series=source.Series with{Quote=new(source.Quote.Key.Revision,"different_quote")}});
        RejectSource("Quote domain violation",source with{Observation=source.Observation with{Value=ExactDecimal.Parse("1001")}});
        var fixedPoint=first with{Expression=source with{Input=source.Input with{Use=InputUse.FrozenFact},Observation=source.Observation with{Role=ObservationRole.IndexFixing}}};
        var fixedPlan=ConnectedCurves.Compile([new(first.Key.Curve,[fixedPoint])],[first.Key],f.Context,f.Resolver);
        Equal("A deliberately frozen index fixing remains a price input",fixedPlan.Evaluate(first.Key).Numerator,"1");
        Equal("Frozen source metadata remains available to scenario validation",fixedPlan.Primitives.Single().Source.Input.Use,InputUse.FrozenFact);

        var rolling=f.Mean with{Expression=new ConnectedRollingMean(new ExactCurveReference(first.Key.Curve),
            f.RawPoints.Select(p=>new ConnectedRollingPoint(new(p.Contract!,f.Context.ValuationAt.AddDays(-1)),p.Key.PointId)).ToImmutableArray(),
            "positions",1,2,DateOnly.FromDateTime(f.Context.ValuationAt.UtcDateTime),ConnectedCurves.UniformMean)};
        var rollingDefinitions=f.Definitions.Select(d=>d.Key==rolling.Key.Curve?d with{Points=[rolling]}:d).ToImmutableArray();
        var rollingPlan=ConnectedCurves.Compile(rollingDefinitions,[rolling.Key],f.Context,f.Resolver);
        Equal("Rolling first two live contracts use their explicit rank universe",rollingPlan.Evaluate(rolling.Key).Numerator,"3");
        Equal("Rolling first two live contracts retain the denominator",rollingPlan.Evaluate(rolling.Key).Denominator,"2");
        var later=f.Context.ValuationAt.AddDays(22);var laterContext=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-later-context",later,later,later,later);
        Equal("A separately resolved later graph rolls to the next two contracts",ConnectedCurves.Compile(rollingDefinitions,[rolling.Key],laterContext,f.Resolver).Evaluate(rolling.Key).Numerator,"3");
        Equal("The original rolling graph retains its selected contracts",rollingPlan.Evaluate(rolling.Key).Numerator,"3");
        Equal("The original rolling graph does not rebind on evaluation",rollingPlan.Evaluate(rolling.Key).Denominator,"2");
        Reject("Rolling universe cannot omit an explicit rank",()=>ConnectedCurves.Compile(rollingDefinitions.Select(d=>d.Key==rolling.Key.Curve?d with{Points=[rolling with{Expression=((ConnectedRollingMean)rolling.Expression) with{Count=4}}]}:d),[rolling.Key],f.Context,f.Resolver));
        var missingContract=new MarketContract(new(f.Mean.Key.Curve.Revision,Guid.Empty),ContractKind.Future,f.Mean.DeliveryFrom,f.Mean.DeliveryTo,f.Context.ValuationAt.AddDays(100),null);
        var invalidRolling=f.Spread with{Expression=new ConnectedRollingMean(new ExactCurveReference(f.Mean.Key.Curve),[new(new(missingContract,f.Context.ValuationAt),f.Mean.Key.PointId)],"positions",1,1,new(2026,1,1),ConnectedCurves.UniformMean)};
        Reject("Rolling computed points require nonempty exact contract identities",()=>ConnectedCurves.Compile(f.Definitions.Select(d=>d.Key==invalidRolling.Key.Curve?d with{Points=[invalidRolling]}:d),[invalidRolling.Key],f.Context,f.Resolver));
        var daily=f.RawPoints.Take(2).Select((p,i)=>
        {
            var contract=p.Contract! with{Key=new(p.Key.Curve.Revision,Guid.NewGuid()),Kind=ContractKind.Forward,DeliveryFrom=new(2026,1,2+i),DeliveryTo=new(2026,1,3+i)};
            return p with{DeliveryFrom=contract.DeliveryFrom,DeliveryTo=contract.DeliveryTo,Contract=contract,
                Expression=((ConnectedSource)p.Expression) with{Request=new(p.Key.Curve,contract.Key),Series=((ConnectedSource)p.Expression).Series with{Contract=contract.Key}}};
        }).ToImmutableArray();
        var dailyMean=rolling with{Expression=new ConnectedRollingMean(new ExactCurveReference(first.Key.Curve),
            daily.Select(p=>new ConnectedRollingPoint(new(p.Contract!,f.Context.ValuationAt.AddDays(-1)),p.Key.PointId)).ToImmutableArray(),
            "daily_delivery",1,1,new(2026,1,3),ConnectedCurves.UniformMean)};
        Equal("Daily selection uses the explicit delivery date",ConnectedCurves.Compile([new(first.Key.Curve,daily),new(dailyMean.Key.Curve,[dailyMean])],[dailyMean.Key],f.Context,f.Resolver).Evaluate(dailyMean.Key).Numerator,"2");

        var binding=new MutableResolver(first.Key.Curve,f.Context);
        var logical=f.Mean with{Expression=new ConnectedMean(f.RawPoints.Select(p=>new CurvePointReference(new LogicalCurveReference(new(first.Key.Curve.Revision.CatalogId,"selected_curve")),p.Key.PointId)).ToImmutableArray(),ConnectedCurves.UniformMean)};
        var logicalPlan=ConnectedCurves.Compile(f.Definitions.Select(d=>d.Key==logical.Key.Curve?d with{Points=[logical]}:d),[logical.Key],f.Context,binding);
        Equal("One logical binding resolves once under the common context",binding.Resolutions,1);
        binding.Closed=true;
        Equal("Compiled logical references stay pinned without further resolution",logicalPlan.Evaluate(logical.Key).Numerator,"7");

        var calendarFrom=new DateOnly(2026,1,1);var calendarTo=new DateOnly(2026,6,1);
        var calendar=DateSet.Create(calendarFrom,calendarTo,Enumerable.Range(0,calendarTo.DayNumber-calendarFrom.DayNumber).Select(calendarFrom.AddDays));
        var occurrences=MonthlyDates.Generate(new(2026,2,1),new(2026,4,1),MonthlyAnchor.DayOfMonth,1,ShortMonth.Reject,DateRoll.Unadjusted,calendar);
        var scheduledDates=DateSet.Create(new(2026,2,1),new(2026,5,1),occurrences.Select(o=>o.Date));
        var schedule=new ResolvedCurveSchedule(Guid.NewGuid(),Guid.NewGuid(),1,ConnectedCurves.ManagedDateSet,f.Context.KnownAt.AddDays(-1),f.Context,scheduledDates);
        var schedulePoints=occurrences.Select((o,i)=>new ConnectedScheduledPoint(o.Date,CurvePointReference.Exact(f.RawPoints[i].Key))).ToImmutableArray();
        var scheduled=f.Mean with{Expression=new ConnectedScheduledMean(schedule,schedulePoints,ConnectedCurves.UniformMean)};
        ImmutableArray<ConnectedCurveDefinition> WithScheduled(ConnectedCurvePoint point)=>f.Definitions.Select(d=>d.Key==point.Key.Curve?d with{Points=[point]}:d).ToImmutableArray();
        var scheduledPlan=ConnectedCurves.Compile(WithScheduled(scheduled),[scheduled.Key],f.Context,f.Resolver);
        Equal("Managed monthly schedule occurrences select connected curve points",scheduledPlan.Evaluate(scheduled.Key).Numerator,"7");
        Equal("Scheduled averaging preserves exact rational prices",scheduledPlan.Evaluate(scheduled.Key).Denominator,"3");
        var repeatedPoint=scheduled with{Expression=new ConnectedScheduledMean(schedule,schedulePoints.SetItem(1,schedulePoints[1] with{Point=schedulePoints[0].Point}),ConnectedCurves.UniformMean)};
        Equal("Distinct schedule occurrences retain explicitly repeated point contributions",ConnectedCurves.Compile(WithScheduled(repeatedPoint),[scheduled.Key],f.Context,f.Resolver).Evaluate(scheduled.Key).Numerator,"2");
        Reject("A schedule date cannot have ambiguous point mappings",()=>ConnectedCurves.Compile(WithScheduled(scheduled with{Expression=new ConnectedScheduledMean(schedule,schedulePoints.SetItem(1,schedulePoints[1] with{Date=schedulePoints[0].Date}),ConnectedCurves.UniformMean)}),[scheduled.Key],f.Context,f.Resolver));
        Reject("Every schedule occurrence requires a point mapping",()=>ConnectedCurves.Compile(WithScheduled(scheduled with{Expression=new ConnectedScheduledMean(schedule,schedulePoints.RemoveAt(0),ConnectedCurves.UniformMean)}),[scheduled.Key],f.Context,f.Resolver));
        Reject("A schedule publication after the knowledge cutoff is unavailable",()=>ConnectedCurves.Compile(WithScheduled(scheduled with{Expression=new ConnectedScheduledMean(schedule with{KnownFrom=f.Context.KnownAt.AddSeconds(1)},schedulePoints,ConnectedCurves.UniformMean)}),[scheduled.Key],f.Context,f.Resolver));
        var nextContext=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-schedule-v2",f.Context.EffectiveAt,f.Context.KnownAt,f.Context.ValuationAt,f.Context.MarketAsOf);
        var nextSchedule=schedule with{Version=2,Context=nextContext,Dates=DateSet.Create(scheduledDates.From,scheduledDates.To,occurrences.Skip(1).Select(o=>o.Date))};
        var nextScheduled=scheduled with{Expression=new ConnectedScheduledMean(nextSchedule,schedulePoints.RemoveAt(0),ConnectedCurves.UniformMean)};
        Reject("A newly resolved schedule cannot enter the old financial context",()=>ConnectedCurves.Compile(WithScheduled(nextScheduled),[scheduled.Key],f.Context,f.Resolver));
        var nextPlan=ConnectedCurves.Compile(WithScheduled(nextScheduled),[scheduled.Key],nextContext,f.Resolver);
        Equal("A new schedule version explicitly selects its new occurrence mapping",nextPlan.Evaluate(scheduled.Key).Numerator,"3");
        Equal("The earlier schedule version stays pinned",scheduledPlan.Evaluate(scheduled.Key).Denominator,"3");
        if(nextPlan.ManifestId==scheduledPlan.ManifestId)throw new Exception("Different resolved schedule pins shared a manifest.");

        var differentQuote=first with{Expression=source with{Quote=source.Quote with{Minimum=ExactDecimal.Parse("-2000")}}};
        Reject("One exact quote cannot carry conflicting bounds across source series",()=>ConnectedCurves.Compile(
            f.Definitions.Select(d=>d.Key==first.Key.Curve?d with{Points=d.Points.SetItem(0,differentQuote)}:d),[f.Spread.Key],f.Context,f.Resolver));
        var otherContract=first.Contract! with{Key=new(first.Key.Curve.Revision,Guid.NewGuid())};
        var otherFixedSource=((ConnectedSource)fixedPoint.Expression) with{Request=new(first.Key.Curve,otherContract.Key),
            Observation=((ConnectedSource)fixedPoint.Expression).Observation with{Key=new(source.Observation.Key.CatalogId,Guid.NewGuid())},
            Series=source.Series with{Contract=otherContract.Key}};
        var otherFixed=first with{Key=new(first.Key.Curve,Guid.NewGuid()),Contract=otherContract,Expression=otherFixedSource};
        Reject("One exact series cannot change registration across frozen observations",()=>ConnectedCurves.Compile(
            [new(first.Key.Curve,[fixedPoint,otherFixed])],[fixedPoint.Key,otherFixed.Key],f.Context,f.Resolver));
        var conflictingSchedule=schedule with{Dates=DateSet.Create(schedule.Dates.From,schedule.Dates.To,occurrences.Skip(1).Select(o=>o.Date))};
        var conflictingScheduled=f.Spread with{Expression=new ConnectedScheduledMean(conflictingSchedule,schedulePoints.RemoveAt(0),ConnectedCurves.UniformMean)};
        Reject("One exact schedule cannot carry different dates for the same resolved coverage",()=>ConnectedCurves.Compile(
            [f.Definitions[0],new(scheduled.Key.Curve,[scheduled]),new(conflictingScheduled.Key.Curve,[conflictingScheduled])],
            [scheduled.Key,conflictingScheduled.Key],f.Context,f.Resolver));
        var overlapSchedule=schedule with{Dates=DateSet.Create(new(2026,3,1),new(2026,6,1),occurrences.Skip(1).Select(o=>o.Date))};
        var overlapping=f.Spread with{Expression=new ConnectedScheduledMean(overlapSchedule,schedulePoints.RemoveAt(0),ConnectedCurves.UniformMean)};
        ConnectedCurvePlan CompileOverlap(ConnectedCurvePoint other)=>ConnectedCurves.Compile(
            [f.Definitions[0],new(scheduled.Key.Curve,[scheduled]),new(other.Key.Curve,[other])],[scheduled.Key,other.Key],f.Context,f.Resolver);
        Equal("Agreeing overlapping schedule windows remain independently selectable",CompileOverlap(overlapping).Evaluate(overlapping.Key).Numerator,"3");
        var changedOverlap=overlapSchedule with{Dates=DateSet.Create(overlapSchedule.Dates.From,overlapSchedule.Dates.To,[occurrences[2].Date])};
        Reject("Overlapping windows of an exact schedule must agree on occurrence dates",()=>CompileOverlap(overlapping with{Expression=new ConnectedScheduledMean(changedOverlap,[schedulePoints[2]],ConnectedCurves.UniformMean)}));
        Reject("Schedule publication evidence cannot change across coverage windows",()=>CompileOverlap(overlapping with{Expression=new ConnectedScheduledMean(overlapSchedule with{KnownFrom=overlapSchedule.KnownFrom.AddSeconds(1)},schedulePoints.RemoveAt(0),ConnectedCurves.UniformMean)}));
        using(var cancellation=new CancellationTokenSource())
        {cancellation.Cancel();try{ConnectedCurves.Compile(f.Definitions,[f.Spread.Key],f.Context,f.Resolver,cancellation.Token);throw new Exception("Cancelled compilation succeeded.");}catch(OperationCanceledException){}}
        Console.WriteLine("PASS connected curves: exact linked means/spreads, explicit weights, rolling/logical/schedule pins, source sharing, units, strict invalid definitions and cancellation");
    }

    private static void Equal<T>(string label,T actual,T expected) where T:notnull
    {if(!actual.Equals(expected))throw new Exception($"{label}: expected {expected}, got {actual}.");}
    private static void Reject(string label,Action action)
    {try{action();}catch(MarketDataException){return;}throw new Exception(label+" was accepted.");}

    internal sealed class Fixture
    {
        public readonly ResolvedCalculationContext Context;
        public readonly ICurveBindingResolver Resolver=new VisibleResolver();
        public readonly ImmutableArray<ConnectedCurveDefinition> Definitions;
        public readonly ImmutableArray<ConnectedCurvePoint> RawPoints;
        public readonly ConnectedCurvePoint Mean;
        public readonly ConnectedCurvePoint Spread;
        public Fixture()
        {
            var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());
            var refs=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
            var raw=new CurveKey(revision,"raw");var mean=new CurveKey(revision,"mean");var spread=new CurveKey(revision,"spread");
            var quote=new QuoteSpecification(new(refs,"quote"),QuoteKind.Outright,new(refs,"usd"),new(refs,"energy"),
                ExactDecimal.Parse("1"),ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
            var unit=PriceUnit.FromQuote(quote);var at=new DateTimeOffset(2026,1,1,12,0,0,TimeSpan.Zero);
            Context=new(Guid.NewGuid(),"synthetic-connected-inputs",at,at,at,at);
            var points=new[]{"1","2","4"}.Select((value,i)=>
            {
                var key=new CurvePointKey(raw,Guid.NewGuid());
                var contract=new MarketContract(new(revision,Guid.NewGuid()),ContractKind.Future,new(2026,i+2,1),new(2026,i+3,1),at.AddDays(20+i*28),null);
                var series=new SeriesKey(revision,Guid.NewGuid());
                var observation=new MarketObservation(new(revision.CatalogId,Guid.NewGuid()),series,new(refs,"synthetic"),ObservationRole.TraderMark,PublicationState.Final,at,ExactDecimal.Parse(value));
                return new ConnectedCurvePoint(key,contract.DeliveryFrom,contract.DeliveryTo,unit,
                    new ConnectedSource(new(raw,contract.Key),new(series,InputUse.Variable),observation,quote,new(series,contract.Key,quote.Key,null)),contract);
            }).ToImmutableArray();
            var average=new ConnectedCurvePoint(new(mean,Guid.NewGuid()),new(2026,2,1),new(2026,5,1),unit,
                new ConnectedMean(points.Select(p=>CurvePointReference.Exact(p.Key)).ToImmutableArray(),ConnectedCurves.UniformMean));
            RawPoints=points;Mean=average;
            Spread=new(new(spread,Guid.NewGuid()),average.DeliveryFrom,average.DeliveryTo,unit,new ConnectedWeightedSum([
                new(CurvePointReference.Exact(average.Key),ExactDecimal.Parse("2")),new(CurvePointReference.Exact(points[0].Key),ExactDecimal.Parse("-1"))]));
            Definitions=[new(raw,points),new(mean,[average]),new(spread,[Spread])];
        }
    }

    private sealed class VisibleResolver : ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey curve,ResolvedCalculationContext context)=>throw new MarketDataException("curve_binding","No synthetic logical binding was supplied.");
        public void RequireVisible(CurveKey curve,ResolvedCalculationContext context){}
    }
    private sealed class MutableResolver(CurveKey selected,ResolvedCalculationContext expected) : ICurveBindingResolver
    {
        public int Resolutions {get;private set;}
        public bool Closed {get;set;}
        public CurveKey Resolve(LogicalCurveKey curve,ResolvedCalculationContext context)
        {
            if(Closed||!ReferenceEquals(expected,context))throw new Exception("The binding was re-resolved or used a different context.");
            Resolutions++;return selected;
        }
        public void RequireVisible(CurveKey curve,ResolvedCalculationContext context)
        {if(Closed||!ReferenceEquals(expected,context))throw new Exception("Visibility used a different context or was checked during evaluation.");}
    }
}
