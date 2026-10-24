using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.Shared.Temporal;

internal static class IntervalDataBenchmark
{
    // Former row-object layout, with shared immutable numeric inputs: a conservative baseline.
    private sealed record Row(DateTimeOffset Start,DateTimeOffset End,BigInteger Value,int Revision);
    private static readonly DateTimeOffset Start=new(2000,1,1,0,0,0,TimeSpan.Zero);
    private static readonly ExactDecimal One=ExactDecimal.Parse("1.25"),Two=ExactDecimal.Parse("0.75");
    private static readonly BigInteger First=BigInteger.Parse("1250000000000000000"),Second=BigInteger.Parse("750000000000000000");
    public static void Run(string path)
    {
        _=IntervalData.From([new(Start,Start.AddMinutes(5),One,1)],5).SumScaled([0]);
        var results=new List<object>();
        foreach(int minutes in new[]{60,15,5})
        {
            int count=(int)((Start.AddYears(20)-Start).TotalMinutes/minutes);
            int[] selected=Enumerable.Range(0,count).Where(i=>i%(1440/minutes)>=480/minutes && i%(1440/minutes)<1200/minutes).ToArray();
            var legacy=Enumerable.Range(0,3).Select(_=>Measure(count,minutes,selected,false)).ToArray();
            var compact=Enumerable.Range(0,3).Select(_=>Measure(count,minutes,selected,true)).ToArray();
            if(legacy.Any(m=>m.Sum!=compact[0].Sum)||compact.Any(m=>m.Sum!=legacy[0].Sum))throw new Exception("Benchmark oracle mismatch.");
            results.Add(new {years=20,minutes,points=count,selectedPoints=selected.Length,uniqueSeries=1,scenarios=1,
                legacy,compact,scope="Build and masked sum only; excludes database, TOU compilation, pricing and rendering"});
        }
        var matrix=Matrix();
        File.WriteAllText(path,JsonSerializer.Serialize(new {environment=new {runtime=RuntimeInformation.FrameworkDescription,os=RuntimeInformation.OSDescription,
            architecture=RuntimeInformation.ProcessArchitecture.ToString(),processors=Environment.ProcessorCount,configuration="Release",tieredCompilation=Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),processPeakBytes=Process.GetCurrentProcess().PeakWorkingSet64>0?(long?)Process.GetCurrentProcess().PeakWorkingSet64:null},results,matrix},new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine("PASS interval benchmark: identical exact sums; report "+path);
    }
    private sealed record Measurement(double BuildMs,long BuildAllocatedBytes,double SumMs,long SumAllocatedBytes,string Sum);
    private static Measurement Measure(int count,int minutes,int[] selected,bool compact)
    {
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long allocated=GC.GetAllocatedBytesForCurrentThread();
        var clock=Stopwatch.StartNew();IntervalData? table=null;Row[]? rows=null;
        if(compact)
        {
            var builder=new IntervalData.Builder(minutes);
            for(int i=0;i<count;i++)builder.Add(Start.AddMinutes((long)i*minutes),Start.AddMinutes((long)(i+1)*minutes),i%2==0?One:Two,1);
            table=builder.Build();
        }
        else
        {
            rows=new Row[count];for(int i=0;i<count;i++)rows[i]=new(Start.AddMinutes((long)i*minutes),Start.AddMinutes((long)(i+1)*minutes),i%2==0?First:Second,1);
        }
        clock.Stop();double build=clock.Elapsed.TotalMilliseconds;long buildBytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
        allocated=GC.GetAllocatedBytesForCurrentThread();clock.Restart();BigInteger sum=0;
        if(table is not null)sum=table.SumScaled(selected);else foreach(int i in selected)sum+=rows![i].Value;
        clock.Stop();long sumBytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
        GC.KeepAlive(table);GC.KeepAlive(rows);return new(build,buildBytes,clock.Elapsed.TotalMilliseconds,sumBytes,sum.ToString());
    }
    private static object Matrix()
    {
        const int rows=31*24*4,meters=32,prices=16;
        IntervalData Column(ExactDecimal value)
        {var b=new IntervalData.Builder(15);for(int i=0;i<rows;i++)b.Add(Start.AddMinutes(i*15),Start.AddMinutes((i+1)*15),value,1);return b.Build();}
        var left=Enumerable.Range(0,meters).Select(_=>Column(ExactDecimal.Parse("12"))).ToArray();var right=Enumerable.Range(0,prices).Select(_=>Column(ExactDecimal.Parse("50"))).ToArray();
        var mask=TimeOfUse.WeekdayBlock(15,8,20,false).Select(left[0].Grid,PowerClock.Capture(TimeZoneInfo.Utc),[]);
        _=IntervalData.CrossProducts(left,right,mask);
        long allocated=GC.GetAllocatedBytesForCurrentThread();var clock=Stopwatch.StartNew();var result=IntervalData.CrossProducts(left,right,mask);clock.Stop();
        long bytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
        foreach(var value in result)if(value.Numerator!=mask.Count*600*value.Denominator)throw new Exception("Matrix oracle mismatch.");
        return new {intervalMinutes=15,intervals=rows,meterColumns=meters,priceScenarioColumns=prices,uniqueSeries=meters+prices,
            selectedIntervals=mask.Count,evaluatedProducts=(long)meters*prices*mask.Count,elapsedMs=clock.Elapsed.TotalMilliseconds,allocatedBytes=bytes,
            scope="Exact X-transpose times Y over weekday 08–20 UTC mask; inputs prepared, no database or UI"};
    }
}
