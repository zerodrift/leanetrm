using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenEtrm.MarketData.Calculation;

// A graph-kernel workload, deliberately separate from exact financial pricing.
// Its independent formulas do not use graph traversal, overlays or cached values.
internal static class ConnectedGraphBenchmark
{
    public static void Run(string output)
    {
        WarmUp();
        var results=new List<object>();
        foreach(var edges in new[]{100_000,1_000_000,5_000_000})results.Add(FanOut(edges));
        results.Add(Chain(100_000));
        results.Add(Diamonds(25_000));
        var report=new
        {
            schema="connected_graph_kernel_benchmark_v1",
            environment=new
            {
                framework=RuntimeInformation.FrameworkDescription,os=RuntimeInformation.OSDescription,
                architecture=RuntimeInformation.ProcessArchitecture.ToString(),processors=Environment.ProcessorCount,
                serverGc=GCSettings.IsServerGC,configuration=DebugBuild?"Debug":"Release",stopwatchFrequency=Stopwatch.Frequency,
                tieredCompilationOverride=Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                readyToRunOverride=Environment.GetEnvironmentVariable("DOTNET_ReadyToRun")
            },
            protocol="One small warm-up, then one observation per phase and size in the stated order. Compile includes edge enumeration, validation, sorting and forward/reverse CSR construction. Evaluation includes output traversal and checksum accumulation. Independent per-output formula validation runs outside timing. Work counters are cumulative session snapshots and include validation/isolation reads and descendant accesses to parents; they are not per-phase counters. No latency percentile, capacity or production throughput conclusion follows from one observation.",
            memory="AllocatedBytes is the process-wide managed allocation delta, including parallel workers. ManagedBytesAfterCollection is retained managed memory after a forced collection outside the timed interval. WorkingSetBytesAfterCollection is an end-of-phase process sample. ProcessLifetimePeakWorkingSetBytes is the process high-water mark, not a phase-specific peak; null means the platform did not provide it and requires an external operating-system measurement. Earlier workloads may contribute to that peak. Compilation temporary arrays and broad-scenario hash/dictionary storage are included in allocation measurements.",
            scope="Checked Int64 arithmetic with synthetic values. No financial instruments, exact rational arithmetic, temporal resolution, database reads, authorization, transport, durable workers or service throughput are measured. Concurrent contexts here are separately bound synthetic value sessions sharing one immutable plan, not a certification of historical data loading.",
            results
        };
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true})+"\n");
    }

    private static object FanOut(int edgeCount)
    {
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        var pairs=edgeCount/2;var nodeCount=2*pairs+1;var phases=new List<Phase>();
        var plan=Measure(phases,"compile",()=>DependencyPlan.Compile(nodeCount,FanOutEdges(pairs)));
        var root=Measure(phases,"base_session",()=>FanOutSession(plan,pairs,11));
        var baseline=Measure(phases,"base_evaluation",()=>SumOutputs(root,pairs+1,nodeCount));
        VerifyFanOut(root,pairs,11);Equal(baseline,ExpectedFanOut(pairs,11),"base checksum");
        Equal(root.Work.EvaluatedNodes,nodeCount,"base evaluated nodes");
        var sparse=Measure(phases,"sparse_overlay",()=>root.WithOffsets(new Dictionary<int,long>{{nodeCount-1,5}}));
        var sparseValue=Measure(phases,"sparse_evaluation",()=>sparse.Get(nodeCount-1));
        Equal(sparseValue,11+Leaf(pairs)+5,"sparse output");
        Equal(sparse.Work.EvaluatedNodes,1,"sparse evaluated nodes");Equal(sparse.Work.AffectedNodes,1,"sparse affected nodes");
        Equal(sparse.Work.TraversedAffectedEdges,0,"sparse affected edges");
        var nested=Measure(phases,"nested_overlay",()=>sparse.WithOffsets(new Dictionary<int,long>{{pairs,2}}));
        var nestedValue=Measure(phases,"nested_evaluation",()=>nested.Get(nodeCount-1));
        Equal(nestedValue,11+Leaf(pairs)+7,"inherited derived output offset");
        Equal(nested.Work.AffectedNodes,2,"nested affected nodes");Equal(nested.Work.EvaluatedNodes,2,"nested evaluated nodes");
        Equal(sparse.Get(nodeCount-1),sparseValue,"parent isolation");Equal(root.Get(nodeCount-1),11+Leaf(pairs),"base isolation");
        var broad=Measure(phases,"broad_overlay",()=>root.WithOffsets(new Dictionary<int,long>{{0,2}}));
        var broadChecksum=Measure(phases,"broad_evaluation",()=>SumOutputs(broad,pairs+1,nodeCount));
        VerifyFanOut(broad,pairs,13);Equal(broadChecksum,ExpectedFanOut(pairs,13),"broad checksum");
        Equal(broad.Work.EvaluatedNodes,pairs+1L,"broad evaluated nodes");Equal(broad.Work.AffectedNodes,pairs+1L,"broad affected nodes");
        Equal(broad.Work.TraversedAffectedEdges,pairs,"broad affected edges");
        var contexts=Measure(phases,"two_context_sessions",()=>new[]{FanOutSession(plan,pairs,17),FanOutSession(plan,pairs,23)});
        var concurrent=Measure(phases,"two_contexts_parallel_evaluation",()=>
        {
            var sums=new long[2];Parallel.For(0,2,n=>sums[n]=SumOutputs(contexts[n],pairs+1,nodeCount));return sums;
        });
        VerifyFanOut(contexts[0],pairs,17);VerifyFanOut(contexts[1],pairs,23);
        Equal(concurrent[0],ExpectedFanOut(pairs,17),"first context checksum");Equal(concurrent[1],ExpectedFanOut(pairs,23),"second context checksum");
        var result=new
        {
            name="shared_source_fanout",nodes=nodeCount,edges=edgeCount,requestedOutputs=pairs,
            planStorage="Four CSR Int32 arrays plus one Int32 topological order; compiled plan shared by every session.",
            sessions=6,scenarioWork=new{basis=root.Work,sparse=sparse.Work,nested=nested.Work,broad=broad.Work,
                concurrent=contexts.Select(session=>session.Work).ToArray()},
            validation="Each output checked against independent source + leaf arithmetic; nested output offset survives an upstream leaf change; root and parent unchanged. Broad source shift must visit every dependent output. Both concurrent value contexts check every output independently.",
            checksums=new{baseline,broad=broadChecksum,concurrent},phases
        };
        Console.WriteLine($"Connected graph: {edgeCount:N0} edges / {nodeCount:N0} nodes; base {Milliseconds(phases,"base_evaluation"):F2} ms; sparse {sparse.Work.EvaluatedNodes} node; broad {broad.Work.EvaluatedNodes:N0} nodes.");
        GC.KeepAlive(plan);GC.KeepAlive(root);GC.KeepAlive(sparse);GC.KeepAlive(nested);GC.KeepAlive(broad);GC.KeepAlive(contexts);
        return result;
    }

    private static IEnumerable<DependencyEdge> FanOutEdges(int pairs)
    {
        for(var n=1;n<=pairs;n++){yield return new(pairs+n,0);yield return new(pairs+n,n);}
    }
    private static GraphSession<long> FanOutSession(DependencyPlan plan,int pairs,long source)=>new(plan,
        (node,read,_)=>node==0?source:node<=pairs?Leaf(node):checked(read.Get(0)+read.Get(node-pairs)),Add);
    private static long Leaf(int node)=>node%13-6;
    private static long SumOutputs(GraphSession<long> session,int from,int to)
    {
        long sum=0;for(var n=from;n<to;n++)sum=checked(sum+session.Get(n));return sum;
    }
    private static long ExpectedFanOut(int pairs,long source)
    {
        long result=0;for(var n=1;n<=pairs;n++)result=checked(result+source+n%13-6);return result;
    }
    private static void VerifyFanOut(GraphSession<long> session,int pairs,long source)
    {
        for(var n=1;n<=pairs;n++)Equal(session.Get(pairs+n),source+n%13-6,"fan-out formula");
    }

    private static object Chain(int nodes)
    {
        var phases=new List<Phase>();
        var plan=Measure(phases,"compile",()=>DependencyPlan.Compile(nodes,Enumerable.Range(1,nodes-1).Select(n=>new DependencyEdge(n,n-1))));
        var root=Measure(phases,"base_session",()=>new GraphSession<long>(plan,(node,read,_)=>node==0?1:checked(read.Get(node-1)+1),Add));
        Equal(Measure(phases,"base_evaluation",()=>root.Get(nodes-1)),nodes,"chain formula");
        var broad=Measure(phases,"broad_overlay",()=>root.WithOffsets(new Dictionary<int,long>{{0,2}}));
        Equal(Measure(phases,"broad_evaluation",()=>broad.Get(nodes-1)),nodes+2L,"shifted chain formula");
        Equal(broad.Work.EvaluatedNodes,nodes,"chain evaluated nodes");Equal(broad.Work.TraversedAffectedEdges,nodes-1L,"chain edges");
        var result=new{name="deep_chain",nodes,edges=nodes-1,requestedOutputs=1,scenarioWork=new{basis=root.Work,broad=broad.Work},
            validation="Independent last-node value is chain length, or length + root offset; traversal cannot use recursion proportional to chain depth.",phases};
        Console.WriteLine($"Connected graph: chain {nodes:N0} nodes; base {Milliseconds(phases,"base_evaluation"):F2} ms.");
        GC.KeepAlive(plan);GC.KeepAlive(root);GC.KeepAlive(broad);return result;
    }

    private static object Diamonds(int count)
    {
        var phases=new List<Phase>();
        IEnumerable<DependencyEdge> Edges()
        {
            for(var n=0;n<count;n++)
            {
                var a=4*n;yield return new(a+1,a);yield return new(a+2,a);yield return new(a+3,a+1);yield return new(a+3,a+2);
            }
        }
        var plan=Measure(phases,"compile",()=>DependencyPlan.Compile(4*count,Edges()));
        var root=Measure(phases,"base_session",()=>new GraphSession<long>(plan,(node,read,_)=> (node%4) switch
        {0=>1,1=>checked(read.Get(node-1)+1),2=>checked(read.Get(node-2)+2),3=>checked(read.Get(node-2)+read.Get(node-1)),_=>throw new Exception()},Add));
        var sum=Measure(phases,"base_evaluation",()=>
        {long result=0;for(var n=0;n<count;n++)result=checked(result+root.Get(4*n+3));return result;});
        Equal(sum,5L*count,"diamond formula");
        for(var n=0;n<count;n++)Equal(root.Get(4*n+3),5,"diamond output");
        var child=Measure(phases,"sparse_overlay",()=>root.WithOffsets(new Dictionary<int,long>{{0,1}}));
        Equal(Measure(phases,"sparse_evaluation",()=>child.Get(3)),7,"shifted diamond formula");
        Equal(child.Work.EvaluatedNodes,4,"diamond evaluated nodes");Equal(child.Work.TraversedAffectedEdges,4,"diamond edges");
        var result=new{name="disjoint_diamonds",nodes=4*count,edges=4*count,requestedOutputs=count,
            scenarioWork=new{basis=root.Work,sparse=child.Work},validation="Independent per-diamond value 5; shifting its shared source by 1 gives 7, with the shared source evaluated once.",phases};
        Console.WriteLine($"Connected graph: {count:N0} diamonds; base {Milliseconds(phases,"base_evaluation"):F2} ms.");
        GC.KeepAlive(plan);GC.KeepAlive(root);GC.KeepAlive(child);return result;
    }

    private sealed record Phase(string Name,double Milliseconds,long AllocatedBytes,long ManagedBytesAfterCollection,
        long WorkingSetBytesAfterCollection,long? ProcessLifetimePeakWorkingSetBytes,int Gen0Collections,int Gen1Collections,int Gen2Collections);
    private static T Measure<T>(List<Phase> phases,string name,Func<T> run)
    {
        var before=GC.GetTotalAllocatedBytes(true);var g0=GC.CollectionCount(0);var g1=GC.CollectionCount(1);var g2=GC.CollectionCount(2);
        var started=Stopwatch.GetTimestamp();var value=run();var milliseconds=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var bytes=GC.GetTotalAllocatedBytes(true)-before;g0=GC.CollectionCount(0)-g0;g1=GC.CollectionCount(1)-g1;g2=GC.CollectionCount(2)-g2;
        var managed=GC.GetTotalMemory(true);using var process=Process.GetCurrentProcess();process.Refresh();
        phases.Add(new(name,milliseconds,bytes,managed,process.WorkingSet64,process.PeakWorkingSet64>0?process.PeakWorkingSet64:null,g0,g1,g2));
        GC.KeepAlive(value);return value;
    }
    private static double Milliseconds(List<Phase> phases,string name)=>phases.Single(phase=>phase.Name==name).Milliseconds;
    private static long Add(long a,long b)=>checked(a+b);
    private static void Equal(long actual,long expected,string label)
    {if(actual!=expected)throw new Exception($"Connected graph benchmark {label}: expected {expected}, got {actual}.");}
    private static void WarmUp()
    {
        var plan=DependencyPlan.Compile(2001,FanOutEdges(1000));var root=FanOutSession(plan,1000,11);
        SumOutputs(root,1001,2001);SumOutputs(root.WithOffsets(new Dictionary<int,long>{{0,2}}),1001,2001);
    }
    private const bool DebugBuild=
#if DEBUG
        true;
#else
        false;
#endif
}
