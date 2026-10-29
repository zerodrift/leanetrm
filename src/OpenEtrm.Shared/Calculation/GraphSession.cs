namespace OpenEtrm.Shared.Calculation;

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
            throw new CalculationException("graph_dependency","A calculation requested an undeclared dependency.");
        return session.ReadPrepared(dependency,cancellation);
    }
}

// ReusedReads counts parent accesses, not distinct nodes. The affected counts
// describe an overlay or advanced frame's reverse traversal. Memo-page hits are
// already completed values and are not counted as parent reads.
public readonly record struct GraphWork(long EvaluatedNodes,long ReusedReads,int AffectedNodes,long TraversedAffectedEdges,long EvaluatedWork=0);

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
    private readonly MemoPage?[]? pages;
    private readonly Dictionary<int,T>? changedValues;
    private readonly object gate=new();
    private long evaluated,reused;
    private long evaluatedWork;
    private readonly long[]? workWeights;
    private bool evaluating;
    public DependencyPlan Plan{get;}
    public int Depth=>layers.Length;
    public GraphWork Work
    {get{lock(gate)return new(evaluated,reused,affected?.Count??0,traversedEdges,evaluatedWork);}}

    // The owner binds deterministic operators, exact context and numerical policy.
    // Values must themselves be immutable; this container does not clone T.
    public GraphSession(DependencyPlan plan,GraphEvaluator<T> evaluator,Func<T,T,T> add,Action<int,T>? validate=null,IReadOnlyList<long>? workWeights=null)
    {
        ArgumentNullException.ThrowIfNull(plan);ArgumentNullException.ThrowIfNull(evaluator);ArgumentNullException.ThrowIfNull(add);
        Plan=plan;this.evaluator=evaluator;this.add=add;this.validate=validate;offsets=[];layers=[];
        if(workWeights is not null)
        {
            if(workWeights.Count!=plan.NodeCount)throw new CalculationException("graph_work","Supply one nonnegative work weight per node.");
            this.workWeights=new long[plan.NodeCount];long total=0;
            for(var node=0;node<plan.NodeCount;node++)
            {
                var weight=workWeights[node];
                if(weight<0||weight>long.MaxValue-total)throw new CalculationException("graph_work","Node work weights must have a nonnegative bounded total.");
                this.workWeights[node]=weight;total+=weight;
            }
        }
        pages=new MemoPage?[(plan.NodeCount+255L)/256];
    }
    // A new immutable input frame, never a scenario layer. The owner has admitted
    // its complete inputs and declares every operator whose bound inputs changed.
    // Completed memo pages are shared copy-on-write; no earlier frame is retained.
    public GraphSession<T> Advance(GraphEvaluator<T> next,IReadOnlyCollection<int> changed,CancellationToken cancellation=default,Action<int,T>? nextValidation=null)
    {
        ArgumentNullException.ThrowIfNull(next);ArgumentNullException.ThrowIfNull(changed);
        cancellation.ThrowIfCancellationRequested();
        if(Depth!=0)throw new CalculationException("graph_frame","Advance an admitted base frame, never a scenario overlay.");
        var (dirty,edges)=Affected(changed,cancellation);
        var result=new GraphSession<T>(Plan,next,add,nextValidation??validate,dirty,edges,workWeights);
        while(!Monitor.TryEnter(gate,25))cancellation.ThrowIfCancellationRequested();
        try
        {
            for(var i=0;i<pages!.Length;i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if(pages[i] is not {} page)continue;
                page.Shared=true;result.pages![i]=page;
            }
            foreach(var node in dirty)
            {
                cancellation.ThrowIfCancellationRequested();
                if(result.pages![node>>8] is null)continue;
                result.Writable(node).Clear(node&255);
            }
        }
        finally{Monitor.Exit(gate);}
        return result;
    }
    private (HashSet<int> Nodes,long Edges) Affected(IEnumerable<int> changed,CancellationToken cancellation)
        =>Affected(Plan,changed,cancellation);
    private static (HashSet<int> Nodes,long Edges) Affected(DependencyPlan plan,IEnumerable<int> changed,CancellationToken cancellation)
    {
        var dirty=new HashSet<int>();var pending=new Queue<int>();long edges=0;
        foreach(var node in changed)
        {cancellation.ThrowIfCancellationRequested();plan.RequireNode(node);if(dirty.Add(node))pending.Enqueue(node);}
        while(pending.TryDequeue(out var node))
            foreach(var dependent in plan.Dependents(node))
            {cancellation.ThrowIfCancellationRequested();edges++;if(dirty.Add(dependent))pending.Enqueue(dependent);}
        return (dirty,edges);
    }
    // The owner qualifies the semantic correspondence between independently
    // compiled layouts. Cached results remain memoized calculations, never new
    // input nodes. A permutation copies completed results once, without ancestry.
    public GraphSession<T> Rebind(DependencyPlan nextPlan,GraphEvaluator<T> next,IReadOnlyList<int> previousNodes,
        IReadOnlyCollection<int> changed,CancellationToken cancellation=default,Action<int,T>? nextValidation=null)
    {
        cancellation.ThrowIfCancellationRequested();
        if(Depth!=0||previousNodes.Count!=nextPlan.NodeCount||nextPlan.NodeCount!=Plan.NodeCount)
            throw new CalculationException("graph_frame","Rebind a qualified base frame with a complete slot correspondence.");
        var seen=new HashSet<int>();
        foreach(var prior in previousNodes)
        {cancellation.ThrowIfCancellationRequested();Plan.RequireNode(prior);if(!seen.Add(prior))throw new CalculationException("graph_frame","Frame slots must correspond uniquely.");}
        var (dirty,edges)=Affected(nextPlan,changed,cancellation);
        var mappedWeights=workWeights is null?null:previousNodes.Select(node=>workWeights[node]).ToArray();
        var result=new GraphSession<T>(nextPlan,next,add,nextValidation??validate,dirty,edges,mappedWeights);
        while(!Monitor.TryEnter(gate,25))cancellation.ThrowIfCancellationRequested();
        try
        {
            for(var node=0;node<nextPlan.NodeCount;node++)
            {cancellation.ThrowIfCancellationRequested();if(!dirty.Contains(node)&&TryValue(previousNodes[node],out var value))result.Writable(node).Set(node&255,value);}
        }
        finally{Monitor.Exit(gate);}
        return result;
    }
    private GraphSession(GraphSession<T> parent,Dictionary<int,T> offsets,HashSet<int> affected,long traversedEdges)
    {
        this.parent=parent;Plan=parent.Plan;evaluator=parent.evaluator;add=parent.add;validate=parent.validate;
        this.offsets=offsets;this.affected=affected;this.traversedEdges=traversedEdges;changedValues=[];
        workWeights=parent.workWeights;
        layers=new GraphSession<T>[parent.Depth+1];parent.layers.CopyTo(layers,0);layers[^1]=this;
    }
    private GraphSession(DependencyPlan plan,GraphEvaluator<T> next,Func<T,T,T> add,Action<int,T>? validate,HashSet<int> dirty,long edges,IReadOnlyList<long>? workWeights)
        :this(plan,next,add,validate,workWeights){affected=dirty;traversedEdges=edges;}
    public GraphSession<T> WithOffsets(IReadOnlyDictionary<int,T> changes,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(Depth>=MaximumDepth)throw new CalculationException("graph_depth","A calculation supports at most 64 nested scenario layers.");
        if(changes is null||changes.Count==0)throw new CalculationException("graph_offset","Supply explicit additive node offsets.");
        var captured=new Dictionary<int,T>();
        foreach(var (node,offset) in changes)
        {
            cancellation.ThrowIfCancellationRequested();Plan.RequireNode(node);
            if(offset is null||!captured.TryAdd(node,offset))
                throw new CalculationException("graph_offset","Each node requires one non-null typed offset.");
        }
        var (affected,edges)=Affected(captured.Keys,cancellation);
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
            if(evaluating)throw new CalculationException("graph_dependency","Use the supplied dependency reader during calculation.");
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
                    if(value is null)throw new CalculationException("graph_value","A calculation returned no value.");
                    // Apply every inherited output offset after recomputation.
                    // A child input change must not discard a parent's derived shift.
                    foreach(var layer in layers)
                        if(layer.offsets.TryGetValue(frame.Node,out var offset))
                        {
                            cancellation.ThrowIfCancellationRequested();value=add(value,offset);
                            if(value is null)throw new CalculationException("graph_value","An additive operator returned no value.");
                        }
                    // A consumer's declared domain applies to the effective
                    // value, including inherited overlays, before any dependent
                    // may consume or reuse it.
                    validate?.Invoke(frame.Node,value);
                    cancellation.ThrowIfCancellationRequested();
                    if(pages is not null)Writable(frame.Node).Set(frame.Node&255,value);
                    else changedValues!.Add(frame.Node,value);
                    evaluated++;
                    evaluatedWork+=workWeights?[frame.Node]??0;
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
        if(pages is not null)
        {
            if(pages[node>>8] is {} page&&page.TryGet(node&255,out value))return true;
            value=default!;return false;
        }
        return changedValues!.TryGetValue(node,out value!);
    }
    private MemoPage Writable(int node)
    {
        var index=node>>8;var page=pages![index];
        if(page is null)pages[index]=page=new();
        else if(page.Shared)pages[index]=page=page.Copy();
        return page;
    }
    private sealed class MemoPage
    {
        private readonly T[] values=new T[256];
        private readonly ulong[] completed=new ulong[4];
        public bool Shared;
        public bool TryGet(int index,out T value)
        {if((completed[index>>6]&(1UL<<(index&63)))!=0){value=values[index];return true;}value=default!;return false;}
        public void Set(int index,T value){values[index]=value;completed[index>>6]|=1UL<<(index&63);}
        public void Clear(int index){values[index]=default!;completed[index>>6]&=~(1UL<<(index&63));}
        public MemoPage Copy(){var copy=new MemoPage();values.CopyTo(copy.values,0);completed.CopyTo(copy.completed,0);return copy;}
    }
    private T ReadParent(int node,CancellationToken cancellation)
    {var value=parent!.Get(node,cancellation);reused++;return value;}
    internal T ReadPrepared(int node,CancellationToken cancellation)
    {
        if(TryValue(node,out var value))return value;
        if(Inherits(node))return ReadParent(node,cancellation);
        throw new CalculationException("graph_dependency","A declared dependency was not evaluated before its consumer.");
    }
}
