using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;

internal static class DependencyPlanChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string name,Action action){action();count++;Console.WriteLine("PASS connected graph: "+name);}
        void Equal<T>(T actual,T expected) where T:notnull
        {if(!actual.Equals(expected))throw new Exception($"Expected {expected}; got {actual}.");}
        void Reject(string code,Action action)
        {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        void Cancel(Action action)
        {try{action();}catch(OperationCanceledException){return;}throw new Exception("Expected cancellation.");}
        Check("cycles, duplicate edges and invalid node identities reject before execution",()=>
        {
            Reject("graph_plan",()=>DependencyPlan.Compile(-1,[]));
            Reject("graph_edge",()=>DependencyPlan.Compile(2,[new(0,2)]));
            Reject("graph_edge",()=>DependencyPlan.Compile(2,[new(1,0),new(1,0)]));
            Reject("graph_cycle",()=>DependencyPlan.Compile(2,[new(1,0),new(0,1)]));
            Reject("graph_cycle",()=>DependencyPlan.Compile(1,[new(0,0)]));
            Equal(DependencyPlan.Compile(0,[]).NodeCount,0);
        });
        Check("a shared diamond evaluates once and nested output offsets survive upstream changes",()=>
        {
            var plan=DependencyPlan.Compile(5,[new(2,0),new(2,1),new(3,2),new(4,2),new(4,3)]);
            var root=new GraphSession<long>(plan,(node,read,_)=>node switch
            {0=>2,1=>3,2=>read.Get(0)+read.Get(1),3=>read.Get(2)*read.Get(2),4=>read.Get(2)+read.Get(3),_=>throw new Exception()},checkedAdd);
            Equal(root.Get(4),30L);Equal(root.Work.EvaluatedNodes,5L);
            var outer=root.WithOffsets(new Dictionary<int,long>{{2,10}});
            Equal(outer.Get(4),240L);Equal(outer.Work.AffectedNodes,3);Equal(outer.Work.EvaluatedNodes,3L);
            var inner=outer.WithOffsets(new Dictionary<int,long>{{0,1}});
            Equal(inner.Get(4),272L); // (2+1+3+10) + (2+1+3+10)^2
            var sibling=outer.WithOffsets(new Dictionary<int,long>{{0,-1}});
            Equal(sibling.Get(4),210L);Equal(outer.Get(4),240L);Equal(root.Get(4),30L);
            Equal(inner.Get(4),272L);Equal(inner.Work.EvaluatedNodes,4L);
            Equal(inner.Work.TraversedAffectedEdges,4L);
        });
        Check("declared dependencies cover either nonlinear branch and undeclared reads fail",()=>
        {
            var plan=DependencyPlan.Compile(4,[new(3,0),new(3,1),new(3,2)]);
            var root=new GraphSession<long>(plan,(node,read,_)=>node switch
            {0=>-1,1=>4,2=>7,3=>read.Get(0)<0?read.Get(1)*read.Get(1):read.Get(2)*read.Get(2),_=>throw new Exception()},checkedAdd);
            Equal(root.Get(3),16L);Equal(root.WithOffsets(new Dictionary<int,long>{{0,2}}).Get(3),49L);
            var hidden=new GraphSession<long>(plan,(node,read,_)=>node==0?read.Get(1):0,checkedAdd);
            Reject("graph_dependency",()=>hidden.Get(0));
            Reject("graph_node",()=>root.Get(4));
            Reject("graph_offset",()=>root.WithOffsets(new Dictionary<int,long>()));
            Reject("graph_node",()=>root.WithOffsets(new Dictionary<int,long>{{4,1}}));
        });
        Check("deep execution and cycle checks use no recursive call stack",()=>
        {
            const int nodes=100_000;
            var plan=DependencyPlan.Compile(nodes,Enumerable.Range(1,nodes-1).Select(n=>new DependencyEdge(n,n-1)));
            var root=new GraphSession<long>(plan,(node,read,_)=>node==0?1:read.Get(node-1)+1,checkedAdd);
            Equal(root.Get(nodes-1),(long)nodes);Equal(root.Work.EvaluatedNodes,(long)nodes);
            var leaf=root.WithOffsets(new Dictionary<int,long>{{nodes-1,2}});
            Equal(leaf.Get(nodes-1),nodes+2L);Equal(leaf.Work.EvaluatedNodes,1L);Equal(leaf.Work.AffectedNodes,1);
        });
        Check("sparse child allocation does not scale with total plan size",()=>
        {
            var plan=DependencyPlan.Compile(1_000_000,[new(999_999,0)]);
            var root=new GraphSession<long>(plan,(node,read,_)=>node==999_999?read.Get(0)+1:1,checkedAdd);
            Equal(root.Get(999_999),2L);
            var before=GC.GetAllocatedBytesForCurrentThread();
            var child=root.WithOffsets(new Dictionary<int,long>{{0,1}});
            var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            if(allocated>100_000)throw new Exception($"A two-node change allocated {allocated} bytes.");
            Equal(child.Work.AffectedNodes,2);Equal(child.Work.TraversedAffectedEdges,1L);Equal(child.Get(999_999),3L);
        });
        Check("parallel cold siblings safely share their lazy parent",()=>
        {
            var calls=0;var plan=DependencyPlan.Compile(3,[new(2,0),new(2,1)]);
            var root=new GraphSession<long>(plan,(node,read,_)=>{Interlocked.Increment(ref calls);return node==2?read.Get(0)*read.Get(1):node+2;},checkedAdd);
            var children=Enumerable.Range(1,24).Select(n=>root.WithOffsets(new Dictionary<int,long>{{0,n}})).ToArray();
            Parallel.For(0,children.Length,n=>Equal(children[n].Get(2),(n+3L)*3));
            Equal(root.Get(2),6L);Equal(root.Work.EvaluatedNodes,3L);Equal(calls,51);
            Equal(children.Sum(c=>c.Work.EvaluatedNodes),48L);
        });
        Check("cancelled and failed evaluations cannot publish a cached value",()=>
        {
            using var cancellation=new CancellationTokenSource();var calls=0;
            var root=new GraphSession<long>(DependencyPlan.Compile(1,[]),(_,_,_)=>{calls++;if(calls==1)cancellation.Cancel();return 17;},checkedAdd);
            Cancel(()=>root.Get(0,cancellation.Token));Equal(root.Work.EvaluatedNodes,0L);
            Equal(root.Get(0),17L);Equal(calls,2);Equal(root.Work.EvaluatedNodes,1L);
            Cancel(()=>root.Get(0,cancellation.Token));
            Cancel(()=>root.WithOffsets(new Dictionary<int,long>{{0,1}},cancellation.Token));
            Cancel(()=>DependencyPlan.Compile(1,[],cancellation.Token));
            using var finishCancellation=new CancellationTokenSource();
            IEnumerable<DependencyEdge> CancelAtEnd()
            {finishCancellation.Cancel();yield break;}
            Cancel(()=>DependencyPlan.Compile(0,CancelAtEnd(),finishCancellation.Token));
            var failedCalls=0;
            var failed=new GraphSession<long>(DependencyPlan.Compile(1,[]),(_,_,_)=>++failedCalls==1?throw new InvalidOperationException("synthetic failure"):8,checkedAdd);
            try{failed.Get(0);throw new Exception("Expected failure.");}catch(InvalidOperationException){}
            Equal(failed.Get(0),8L);Equal(failed.Work.EvaluatedNodes,1L);
        });
        Check("offset mappings are copied and ancestry is explicitly bounded",()=>
        {
            var root=new GraphSession<long>(DependencyPlan.Compile(1,[]),(_,_,_)=>1,checkedAdd);
            var map=new Dictionary<int,long>{{0,2}};var child=root.WithOffsets(map);map[0]=100;
            Equal(child.Get(0),3L);
            for(var n=1;n<64;n++)child=child.WithOffsets(new Dictionary<int,long>{{0,1}});
            Equal(child.Depth,64);Equal(child.Get(0),66L);
            Reject("graph_depth",()=>child.WithOffsets(new Dictionary<int,long>{{0,1}}));Equal(root.Get(0),1L);
        });
        return count;
    }
    private static long checkedAdd(long left,long right)=>checked(left+right);
}
