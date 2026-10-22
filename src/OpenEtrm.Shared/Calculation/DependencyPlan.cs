namespace OpenEtrm.Shared.Calculation;

public readonly record struct DependencyEdge(int Node,int Dependency);

// Immutable compressed adjacency in both directions. Dense integer identities are
// local to this resolved plan; financial/version identities remain with its owner.
public sealed class DependencyPlan
{
    private readonly int[] offsets,dependencies,reverseOffsets,dependents,order;
    public int NodeCount=>offsets.Length-1;
    public int EdgeCount=>dependencies.Length;
    public ReadOnlySpan<int> TopologicalOrder=>order;
    private DependencyPlan(int[] offsets,int[] dependencies,int[] reverseOffsets,int[] dependents,int[] order)
    {this.offsets=offsets;this.dependencies=dependencies;this.reverseOffsets=reverseOffsets;this.dependents=dependents;this.order=order;}

    public static DependencyPlan Compile(int nodeCount,IEnumerable<DependencyEdge> edges,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(nodeCount<0||nodeCount==int.MaxValue||edges is null)
            throw new CalculationException("graph_plan","Supply a bounded node count and explicit dependency edges.");
        // Capture a possibly single-use enumerable exactly once; no object per edge.
        var captured=new List<DependencyEdge>();
        var offsets=new int[nodeCount+1];var reverseOffsets=new int[nodeCount+1];
        foreach(var edge in edges)
        {
            cancellation.ThrowIfCancellationRequested();
            if((uint)edge.Node>=(uint)nodeCount||(uint)edge.Dependency>=(uint)nodeCount)
                throw new CalculationException("graph_edge","Every edge must name two nodes in this plan.");
            if(captured.Count==int.MaxValue)throw new CalculationException("graph_plan","The plan has too many edges.");
            captured.Add(edge);offsets[edge.Node+1]++;reverseOffsets[edge.Dependency+1]++;
        }
        for(var node=0;node<nodeCount;node++)
        {
            cancellation.ThrowIfCancellationRequested();
            offsets[node+1]+=offsets[node];reverseOffsets[node+1]+=reverseOffsets[node];
        }
        var dependencies=new int[captured.Count];var dependents=new int[captured.Count];
        var next=(int[])offsets.Clone();var reverseNext=(int[])reverseOffsets.Clone();
        foreach(var edge in captured)
        {
            cancellation.ThrowIfCancellationRequested();
            dependencies[next[edge.Node]++]=edge.Dependency;dependents[reverseNext[edge.Dependency]++]=edge.Node;
        }
        for(var node=0;node<nodeCount;node++)
        {
            cancellation.ThrowIfCancellationRequested();
            Array.Sort(dependencies,offsets[node],offsets[node+1]-offsets[node]);
            Array.Sort(dependents,reverseOffsets[node],reverseOffsets[node+1]-reverseOffsets[node]);
            for(var index=offsets[node]+1;index<offsets[node+1];index++)
            {
                cancellation.ThrowIfCancellationRequested();
                if(dependencies[index]==dependencies[index-1])
                    throw new CalculationException("graph_edge","Declare each dependency edge exactly once.");
            }
        }
        // Kahn ordering validates cycles without recursion even on very deep plans.
        var order=new int[nodeCount];var head=0;var tail=0;
        for(var node=0;node<nodeCount;node++)
        {
            cancellation.ThrowIfCancellationRequested();
            next[node]=offsets[node+1]-offsets[node];
            if(next[node]==0)order[tail++]=node;
        }
        while(head<tail)
        {
            cancellation.ThrowIfCancellationRequested();var node=order[head++];
            for(var index=reverseOffsets[node];index<reverseOffsets[node+1];index++)
            {
                cancellation.ThrowIfCancellationRequested();
                if(--next[dependents[index]]==0)order[tail++]=dependents[index];
            }
        }
        cancellation.ThrowIfCancellationRequested();
        if(tail!=nodeCount)throw new CalculationException("graph_cycle","Calculation dependencies must be acyclic.");
        return new(offsets,dependencies,reverseOffsets,dependents,order);
    }
    public ReadOnlySpan<int> Dependencies(int node)
    {RequireNode(node);return dependencies.AsSpan(offsets[node],offsets[node+1]-offsets[node]);}
    public ReadOnlySpan<int> Dependents(int node)
    {RequireNode(node);return dependents.AsSpan(reverseOffsets[node],reverseOffsets[node+1]-reverseOffsets[node]);}
    internal bool Declares(int node,int dependency)=>Dependencies(node).BinarySearch(dependency)>=0;
    internal void RequireNode(int node)
    {
        if((uint)node>=(uint)NodeCount)throw new CalculationException("graph_node","The node does not belong to this resolved plan.");
    }
}
