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
    private readonly int affectedCount;
    private readonly long traversedEdges;
    private readonly MemoPage?[]?[] pages;
    private readonly object gate=new();
    private long evaluated,reused;
    private long evaluatedWork;
    private readonly long[]? workWeights;
    private bool evaluating;
    private Frame[]? frames;
    public DependencyPlan Plan{get;}
    public int Depth=>layers.Length;
    public GraphWork Work
    {get{lock(gate)return new(evaluated,reused,affectedCount,traversedEdges,evaluatedWork);}}

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
        pages=NewTable(plan);
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
            for(var i=0;i<pages.Length;i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if(pages[i] is not {} chunk)continue;
                foreach(var page in chunk)if(page is not null)page.Shared=true;
                result.pages[i]=(MemoPage?[])chunk.Clone();
            }
            foreach(var node in dirty)
            {
                cancellation.ThrowIfCancellationRequested();
                if(result.Page(node) is null)continue;
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
    private GraphSession(GraphSession<T> parent,Dictionary<int,T> offsets,MemoPage?[]?[] pages,int affectedCount,long traversedEdges)
    {
        this.parent=parent;Plan=parent.Plan;evaluator=parent.evaluator;add=parent.add;validate=parent.validate;
        this.offsets=offsets;this.pages=pages;this.affectedCount=affectedCount;this.traversedEdges=traversedEdges;
        workWeights=parent.workWeights;
        layers=new GraphSession<T>[parent.Depth+1];parent.layers.CopyTo(layers,0);layers[^1]=this;
        // Only a recomputed node that some layer shifts needs the layer walk.
        foreach(var layer in layers)
            foreach(var node in layer.offsets.Keys)
                if(Page(node) is {} page&&page.Affected(node&255))page.Shift(node&255);
    }
    private GraphSession(DependencyPlan plan,GraphEvaluator<T> next,Func<T,T,T> add,Action<int,T>? validate,HashSet<int> dirty,long edges,IReadOnlyList<long>? workWeights)
        :this(plan,next,add,validate,workWeights){affectedCount=dirty.Count;traversedEdges=edges;}
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
        // Affected marks and changed values share lazily allocated pages, so a
        // scenario's storage follows the 256-node blocks it touches.
        var marks=NewTable(Plan);var pending=new Queue<int>();var affected=0;long edges=0;
        foreach(var node in captured.Keys)
        {cancellation.ThrowIfCancellationRequested();if(Mark(marks,node)){affected++;pending.Enqueue(node);}}
        while(pending.TryDequeue(out var node))
            foreach(var dependent in Plan.Dependents(node))
            {cancellation.ThrowIfCancellationRequested();edges++;if(Mark(marks,dependent)){affected++;pending.Enqueue(dependent);}}
        return new(this,captured,marks,affected,edges);
    }
    public T Get(int node,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();Plan.RequireNode(node);Frame[]? rented=null;
        // One owner populates each session. Lock ordering is child -> parent;
        // independent siblings compute locally and safely share a lazy parent.
        while(!Monitor.TryEnter(gate,25))cancellation.ThrowIfCancellationRequested();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if(evaluating)throw new CalculationException("graph_dependency","Use the supplied dependency reader during calculation.");
            if(TryValue(node,out var value))return value;
            if(Inherits(node))return ReadParent(node,cancellation);
            // The explicit stack is reused, but leaves the session while in use:
            // a nested read on this thread through a parent takes its own.
            var stack=rented=frames??new Frame[8];frames=null;var depth=0;stack[depth++]=new(node,0);
            while(depth>0)
            {
                cancellation.ThrowIfCancellationRequested();
                var frame=stack[depth-1];var dependencies=Plan.Dependencies(frame.Node);
                if(frame.Next<dependencies.Length)
                {
                    var dependency=dependencies[frame.Next];stack[depth-1]=new(frame.Node,frame.Next+1);
                    if(TryValue(dependency,out _))continue;
                    if(Inherits(dependency)){ReadParent(dependency,cancellation);continue;}
                    if(depth==stack.Length){Array.Resize(ref stack,(int)Math.Min(2L*depth,Array.MaxLength));rented=stack;}
                    stack[depth++]=new(dependency,0);continue;
                }
                evaluating=true;
                try
                {
                    value=evaluator(frame.Node,new(this,frame.Node,cancellation),cancellation);
                    if(value is null)throw new CalculationException("graph_value","A calculation returned no value.");
                    // Apply every inherited output offset after recomputation.
                    // A child input change must not discard a parent's derived shift.
                    if(layers.Length!=0&&Page(frame.Node)!.Shifted(frame.Node&255))
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
                    Writable(frame.Node).Set(frame.Node&255,value);
                    evaluated++;
                    evaluatedWork+=workWeights?[frame.Node]??0;
                }
                finally{evaluating=false;}
                depth--;
            }
            return ReadPrepared(node,cancellation);
        }
        finally{if(rented is {Length:<=RetainedFrames})frames=rented;Monitor.Exit(gate);}
    }
    private readonly record struct Frame(int Node,int Next);
    private const int RetainedFrames=4096;
    private bool Inherits(int node)=>parent is not null&&!(Page(node) is {} page&&page.Affected(node&255));
    private bool TryValue(int node,out T value)
    {
        if(Page(node) is {} page&&page.TryGet(node&255,out value))return true;
        value=default!;return false;
    }
    private MemoPage Writable(int node)
    {
        var chunk=pages[node>>16]??=new MemoPage?[256];ref var page=ref chunk[(node>>8)&255];
        if(page is null)page=new();
        else if(page.Shared)page=page.Copy();
        return page;
    }
    // Two levels keep the table itself sparse: 256 pages per chunk, 256 nodes
    // per page. Neither a base frame nor a scenario pays for untouched blocks.
    private static MemoPage?[]?[] NewTable(DependencyPlan plan)=>new MemoPage?[(int)((plan.NodeCount+65535L)>>16)][];
    private MemoPage? Page(int node)=>pages[node>>16]?[(node>>8)&255];
    private static bool Mark(MemoPage?[]?[] table,int node)=>
        ((table[node>>16]??=new MemoPage?[256])[(node>>8)&255]??=new()).Mark(node&255);
    private sealed class MemoPage
    {
        private T[]? values;
        private readonly ulong[] completed=new ulong[4];
        // Scenario pages only: the reverse closure of the layer's offsets, and
        // its recomputed nodes that some layer shifts. Only base-frame pages
        // are ever shared, so Copy leaves these out.
        private ulong[]? affected,shifted;
        public bool Shared;
        public bool TryGet(int index,out T value)
        {if((completed[index>>6]&(1UL<<(index&63)))!=0){value=values![index];return true;}value=default!;return false;}
        public void Set(int index,T value){(values??=new T[256])[index]=value;completed[index>>6]|=1UL<<(index&63);}
        public void Clear(int index){if(values is not null)values[index]=default!;completed[index>>6]&=~(1UL<<(index&63));}
        public MemoPage Copy(){var copy=new MemoPage{values=(T[]?)values?.Clone()};completed.CopyTo(copy.completed,0);return copy;}
        public bool Mark(int index)
        {
            affected??=new ulong[4];var bit=1UL<<(index&63);
            if((affected[index>>6]&bit)!=0)return false;
            affected[index>>6]|=bit;return true;
        }
        public bool Affected(int index)=>affected is not null&&(affected[index>>6]&(1UL<<(index&63)))!=0;
        public void Shift(int index)=>(shifted??=new ulong[4])[index>>6]|=1UL<<(index&63);
        public bool Shifted(int index)=>shifted is not null&&(shifted[index>>6]&(1UL<<(index&63)))!=0;
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
