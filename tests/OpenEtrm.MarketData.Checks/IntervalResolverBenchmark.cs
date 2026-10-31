using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

internal static class IntervalResolverBenchmark
{
    public static void Run(string output)
    {
        const int months=240,factors=24;
        var fixture=new ResolverReuseFixture(months);
        var samples=new List<object>();
        for(var sample=0;sample<3;sample++)
        {
            var allocated=GC.GetTotalAllocatedBytes(true);
            var started=Stopwatch.GetTimestamp();
            var prepared=fixture.Prepare();
            var preparationMilliseconds=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var curves=new Dictionary<CurveKey,PreparedCurve>{{prepared.Key,prepared}};
            var market=fixture.Model.Build(fixture.Snapshot,fixture.Requests);
            var @base=new IntervalPriceResolver(market,curves);
            var basePrices=prepared.Months.ToDictionary(month=>month,month=>@base.Prices(prepared.Key,month));
            var baseMilliseconds=Stopwatch.GetElapsedTime(started).TotalMilliseconds-preparationMilliseconds;
            IntervalResolverReuseChecks.Equal(@base.Resolved,months);
            IntervalResolverReuseChecks.Equal(@base.CalibratedBlocks,2*months);
            var rows=basePrices.Values.Sum(p=>p.Length);
            var scenarioStart=Stopwatch.GetTimestamp();
            var work=new List<object>();
            var touchedRows=0;var reusedRows=0;var primitiveEvaluations=market.EvaluatedConstituents;
            var factorIndex=0;
            foreach(var pair in fixture.Model.Scenarios(market,fixture.Bumps(factors)))
            {
                var affectedMonth=fixture.First.AddMonths(factorIndex*(months/factors));
                foreach(var (build,direction) in new[]{(pair.Up,1),(pair.Down,-1)})
                {
                    var resolver=new IntervalPriceResolver(build,curves,@base);
                    primitiveEvaluations+=build.EvaluatedConstituents;
                    foreach(var month in prepared.Months)
                    {
                        var result=resolver.Prices(prepared.Key,month);
                        if(month!=affectedMonth)
                        {
                            IntervalResolverReuseChecks.Equal(result,basePrices[month]);
                            reusedRows+=result.Length;
                            continue;
                        }
                        touchedRows+=result.Length;
                        CheckAffectedMonth(result,basePrices[month],direction,fixture);
                    }
                    IntervalResolverReuseChecks.Equal(resolver.Resolved,1);
                    IntervalResolverReuseChecks.Equal(resolver.ReusedMonths,months-1);
                    IntervalResolverReuseChecks.Equal(resolver.CalibratedBlocks,1);
                    IntervalResolverReuseChecks.Equal(resolver.ReusedBlocks,1);
                    work.Add(new{factor=factorIndex,direction,affectedMonth,resolver.Resolved,resolver.ReusedMonths,resolver.CalibratedBlocks,resolver.ReusedBlocks});
                }
                factorIndex++;
            }
            IntervalResolverReuseChecks.Equal(factorIndex,factors);
            var scenarioMilliseconds=Stopwatch.GetElapsedTime(scenarioStart).TotalMilliseconds;
            var milliseconds=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var allocatedBytes=GC.GetTotalAllocatedBytes(true)-allocated;
            using var process=Process.GetCurrentProcess();process.Refresh();
            long? peakWorkingSet=process.PeakWorkingSet64>0?process.PeakWorkingSet64:null;
            samples.Add(new{sample=sample+1,milliseconds,preparationMilliseconds,baseMilliseconds,scenarioMilliseconds,allocatedBytes,
                workingSetBytes=process.WorkingSet64,processLifetimePeakWorkingSetBytes=peakWorkingSet,
                baseWork=new{@base.Resolved,@base.ReusedMonths,@base.CalibratedBlocks,@base.ReusedBlocks,rows},
                primitiveEvaluations,affectedMonthRowEvaluations=touchedRows,reusedMonthRows=reusedRows,
                resolvedRowEvaluations=rows+touchedRows,requestedRowScenarioCells=(long)rows*(1+2*factors),scenarioWork=work});
            Console.WriteLine($"Resolver sample {sample+1}: {rows} hourly cells, {months} months, {2*months} unique block contracts, {factors} factors/{2*factors} shocks; {milliseconds:F2} ms, {allocatedBytes} allocated bytes; every shock recalibrated 1 block and reused 239 months.");
            GC.KeepAlive(@base);
        }
        var report=new{schema="interval_resolver_reuse_v1",workload="hourly_20_year_two_block_shaped_curve",
            years=20,months,curves=1,uniqueBlockContracts=2*months,shapeIntervals=fixture.Shape.Length,factors,scenarios=1+2*factors,
            clock=fixture.Clock.Id,intervalMinutes=60,shapeMethod="additive",quoteBump=OpenEtrm.MarketData.Risk.QuoteBump.Convention,bumpSize="1",
            environment=new{framework=RuntimeInformation.FrameworkDescription,os=RuntimeInformation.OSDescription,
                architecture=RuntimeInformation.ProcessArchitecture.ToString(),processors=Environment.ProcessorCount,
                configuration=Configuration,stopwatchFrequency=Stopwatch.Frequency},
            protocol="Three sequential samples with fresh prepared inputs and resolver caches. Synthetic fixture construction excluded. Each sample prepares shape once, builds one base, streams 24 up/down factor pairs and requests all 240 months. Timings include calendar-based exact price checks for each affected month and immutable-array identity checks for unaffected months. No explicit warm-up; first sample includes JIT effects. No database, API, authorization, trade, instrument or portfolio work is measured.",
            memory="Allocated bytes are process-wide managed allocations during each sample. Working set is sampled at completion; peak working set is the operating system's process-lifetime high-water mark, not a per-sample peak. Null means the runtime does not supply that measurement; obtain an external process peak separately.",
            counterScope="Resolved counts recomputed months, ReusedMonths counts reused complete month arrays. CalibratedBlocks and ReusedBlocks count block work within recomputed months only; whole-month reuse is not also counted as block reuse. Affected-month row evaluations include its unchanged reused block rows. Primitive evaluations include the market base and scenario changed nodes.",samples};
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true})+"\n");
    }

    private static void CheckAffectedMonth(ImmutableArray<IntervalPriceResolution> actual,ImmutableArray<IntervalPriceResolution> before,int direction,ResolverReuseFixture fixture)
    {
        IntervalResolverReuseChecks.Equal(actual.Length,before.Length);
        var increment=OpenEtrm.MarketData.ExactUnitPrice.FromPrice(new(ExactDecimal.Parse(direction.ToString()),fixture.Unit));
        for(var i=0;i<actual.Length;i++)
        {
            var interval=actual[i].Interval;
            var peak=interval.OperatingDate.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)&&interval.HourBeginning is >=7 and <23;
            IntervalResolverReuseChecks.Equal(actual[i].Price,peak?before[i].Price!.Add(increment):before[i].Price);
            IntervalResolverReuseChecks.Equal(actual[i].State,IntervalPriceState.Projected);
        }
    }

    private const string Configuration=
#if DEBUG
        "Debug";
#else
        "Release";
#endif
}
