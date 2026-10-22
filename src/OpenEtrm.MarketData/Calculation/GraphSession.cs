namespace OpenEtrm.MarketData.Calculation;

public delegate T GraphEvaluator<T>(int node,GraphInputs<T> inputs,CancellationToken cancellation) where T:notnull;

// A stack-scoped reader cannot escape evaluation. The plan, not the last executed
// branch, is the complete dependency contract used for invalidation.
public readonly ref struct GraphInputs<T> where T:notnull
{
    private readonly GraphSession<T> session;
    private readonly int node;
    private readonly CancellationToken cancellation;
    internal GraphInputs(GraphSession<T> session,int node,CancellationToken cancellation)
    {this.session=session;this.node=node;this.cancellation=cancellation;}
    public T Get(int dependency)
    {
        cancellation.ThrowIfCancellationRequested();
        if(!session.Plan.Declares(node,dependency))
            throw new MarketDataException("graph_dependency","A calculation requested an undeclared dependency.");
        return session.ReadPrepared(dependency,cancellation);
    }
}

// ReusedReads counts parent accesses, not distinct nodes. The affected counts
// describe the child overlay's reverse traversal; a base session has no overlay.
public readonly record struct GraphWork(long EvaluatedNodes,long ReusedReads,int AffectedNodes,long TraversedAffectedEdges);

public sealed class GraphSession<T> where T:notnull
{
    public const int MaximumDepth=64;
    private readonly GraphEvaluator<T> evaluator;
    private readonly Func<T,T,T> add;
    private readonly Action<int,T>? validate;
    private readonly GraphSession<T>? parent;
    private readonly GraphSession<T>[] layers;
    private readonly Dictionary<int,T> offsets;
    private readonly HashSet<int>? affected;
    private readonly long traversedEdges;
    private readonly T[]? baseValues;
    private readonly ulong[]? completed;
    private readonly Dictionary<int,T>? changedValues;
    private readonly object gate=new();
    private long evaluated,reused;
    private bool evaluating;
    public DependencyPlan Plan{get;}
    public int Depth=>layers.Length;
    public GraphWork Work
    {get{lock(gate)return new(evaluated,reused,affected?.Count??0,traversedEdges);}}

    // The owner binds deterministic operators, exact context and numerical policy.
    // Values must themselves be immutable; this container does not clone T.
    public GraphSession(DependencyPlan plan,GraphEvaluator<T> evaluator,Func<T,T,T> add,Action<int,T>? validate=null)
    {
        ArgumentNullException.ThrowIfNull(plan);ArgumentNullException.ThrowIfNull(evaluator);ArgumentNullException.ThrowIfNull(add);
        Plan=plan;this.evaluator=evaluator;this.add=add;this.validate=validate;offsets=[];layers=[];
        baseValues=new T[plan.NodeCount];completed=new ulong[(plan.NodeCount+63L)/64];
    }
    private GraphSession(GraphSession<T> parent,Dictionary<int,T> offsets,HashSet<int> affected,long traversedEdges)
    {
        this.parent=parent;Plan=parent.Plan;evaluator=parent.evaluator;add=parent.add;validate=parent.validate;
        this.offsets=offsets;this.affected=affected;this.traversedEdges=traversedEdges;changedValues=[];
        layers=new GraphSession<T>[parent.Depth+1];parent.layers.CopyTo(layers,0);layers[^1]=this;
    }
    public GraphSession<T> WithOffsets(IReadOnlyDictionary<int,T> changes,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(Depth>=MaximumDepth)throw new MarketDataException("graph_depth","A calculation supports at most 64 nested scenario layers.");
        if(changes is null||changes.Count==0)throw new MarketDataException("graph_offset","Supply explicit additive node offsets.");
        var captured=new Dictionary<int,T>();var affected=new HashSet<int>();var pending=new Queue<int>();
        foreach(var (node,offset) in changes)
        {
            cancellation.ThrowIfCancellationRequested();Plan.RequireNode(node);
            if(offset is null||!captured.TryAdd(node,offset))
                throw new MarketDataException("graph_offset","Each node requires one non-null typed offset.");
            affected.Add(node);pending.Enqueue(node);
        }
        long edges=0;
        while(pending.TryDequeue(out var node))
        {
            cancellation.ThrowIfCancellationRequested();
            foreach(var dependent in Plan.Dependents(node))
            {
                cancellation.ThrowIfCancellationRequested();edges++;
                if(affected.Add(dependent))pending.Enqueue(dependent);
            }
        }
        return new(this,captured,affected,edges);
    }
    public T Get(int node,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();Plan.RequireNode(node);
        // One owner populates each session. Lock ordering is child -> parent;
        // independent siblings compute locally and safely share a lazy parent.
        while(!Monitor.TryEnter(gate,25))cancellation.ThrowIfCancellationRequested();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if(evaluating)throw new MarketDataException("graph_dependency","Use the supplied dependency reader during calculation.");
            if(TryValue(node,out var value))return value;
            if(Inherits(node))return ReadParent(node,cancellation);
            var stack=new List<Frame>{new(node,0)};
            while(stack.Count>0)
            {
                cancellation.ThrowIfCancellationRequested();
                var frame=stack[^1];var dependencies=Plan.Dependencies(frame.Node);
                if(frame.Next<dependencies.Length)
                {
                    var dependency=dependencies[frame.Next];stack[^1]=new(frame.Node,frame.Next+1);
                    if(TryValue(dependency,out _))continue;
                    if(Inherits(dependency)){ReadParent(dependency,cancellation);continue;}
                    stack.Add(new(dependency,0));continue;
                }
                evaluating=true;
                try
                {
                    value=evaluator(frame.Node,new(this,frame.Node,cancellation),cancellation);
                    if(value is null)throw new MarketDataException("graph_value","A calculation returned no value.");
                    // Apply every inherited output offset after recomputation.
                    // A child input change must not discard a parent's derived shift.
                    foreach(var layer in layers)
                        if(layer.offsets.TryGetValue(frame.Node,out var offset))
                        {
                            cancellation.ThrowIfCancellationRequested();value=add(value,offset);
                            if(value is null)throw new MarketDataException("graph_value","An additive operator returned no value.");
                        }
                    // A consumer's declared domain applies to the effective
                    // value, including inherited overlays, before any dependent
                    // may consume or reuse it.
                    validate?.Invoke(frame.Node,value);
                    cancellation.ThrowIfCancellationRequested();
                    if(baseValues is not null)
                    {baseValues[frame.Node]=value;completed![frame.Node>>6]|=1UL<<(frame.Node&63);}
                    else changedValues!.Add(frame.Node,value);
                    evaluated++;
                }
                finally{evaluating=false;}
                stack.RemoveAt(stack.Count-1);
            }
            return ReadPrepared(node,cancellation);
        }
        finally{Monitor.Exit(gate);}
    }
    private readonly record struct Frame(int Node,int Next);
    private bool Inherits(int node)=>parent is not null&&!affected!.Contains(node);
    private bool TryValue(int node,out T value)
    {
        if(baseValues is not null)
        {
            if((completed![node>>6]&(1UL<<(node&63)))!=0){value=baseValues[node];return true;}
            value=default!;return false;
        }
        return changedValues!.TryGetValue(node,out value!);
    }
    private T ReadParent(int node,CancellationToken cancellation)
    {var value=parent!.Get(node,cancellation);reused++;return value;}
    internal T ReadPrepared(int node,CancellationToken cancellation)
    {
        if(TryValue(node,out var value))return value;
        if(Inherits(node))return ReadParent(node,cancellation);
        throw new MarketDataException("graph_dependency","A declared dependency was not evaluated before its consumer.");
    }
}
