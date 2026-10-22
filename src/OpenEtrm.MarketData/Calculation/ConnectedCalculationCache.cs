using OpenEtrm.Shared.Calculation;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Calculation;

// Numeric memoization only. Callers must still compile, admit every input and
// recheck authority. No context, permission, price receipt or instrument PV is
// supplied by this cache. One latest base frame per qualified execution shape.
public sealed class ConnectedCalculationCache
{
    private readonly int maximumPlans,maximumNodes;
    private readonly Dictionary<string,(ConnectedCurvePlan Plan,GraphSession<ExactUnitPrice> Session)> latest=[];
    private readonly Queue<string> order=[];
    private readonly object gate=new();
    private int nodes;
    public ConnectedCalculationCache(int maximumPlans,int maximumNodes)
    {
        if(maximumPlans<1||maximumNodes<1)throw new ArgumentOutOfRangeException(nameof(maximumPlans));
        this.maximumPlans=maximumPlans;this.maximumNodes=maximumNodes;
    }
    internal GraphSession<ExactUnitPrice> Bind(ConnectedCurvePlan plan,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(plan.DependencyPlan.NodeCount>maximumNodes)return plan.CreateSession();
        while(!Monitor.TryEnter(gate,25))cancellation.ThrowIfCancellationRequested();
        try
        {
            cancellation.ThrowIfCancellationRequested();var shape=plan.Layout(cancellation).Identity;
            if(latest.TryGetValue(shape,out var prior))
            {
                var next=plan.Advance(prior.Plan,prior.Session,cancellation);
                cancellation.ThrowIfCancellationRequested();
                latest[shape]=(plan,next);return next;
            }
            var session=plan.CreateSession();
            while(latest.Count>=maximumPlans||nodes+plan.DependencyPlan.NodeCount>maximumNodes)
            {var key=order.Dequeue();nodes-=latest[key].Plan.DependencyPlan.NodeCount;latest.Remove(key);}
            latest.Add(shape,(plan,session));order.Enqueue(shape);nodes+=plan.DependencyPlan.NodeCount;
            return session;
        }
        finally{Monitor.Exit(gate);}
    }
}

public sealed partial class ConnectedCurvePlan
{
    internal sealed record MemoLayout(string Identity,string[] Keys);
    private MemoLayout? memoLayout;
    internal MemoLayout Layout(CancellationToken cancellation)
    {
        if(memoLayout is {} retained)return retained;
        // Native addresses deliberately omit snapshot UUIDs ONLY for numerical
        // slot matching. Full point/evidence identities are never changed.
        object Address(ConnectedCurvePoint p)=>p.Expression switch
        {
            ConnectedSource {Sample:{} s} source=>new{Kind="native_source",source.Request,s.OriginalUse,s.ValuationAt,s.MarketCutoff,s.SelectionPolicy,s.FreezeHistorical},
            ConnectedBuiltCurve b when p.Contract is not null=>new{Kind="native_built",p.Key.Curve,p.Contract.Key,b.Sample.OriginalUse,b.Sample.ValuationAt,b.Sample.MarketCutoff,b.Sample.SelectionPolicy,b.Sample.FreezeHistorical},
            _=>p.Key
        };
        var points=Points.Values.Select(p=>{cancellation.ThrowIfCancellationRequested();return(Point:p,Address:ConnectedIdentity.Digest(Address(p)));}).ToArray();
        // Two separately pinned facts at the same semantic address are not an
        // alias. Retain exact keys rather than guessing a correspondence.
        if(points.Select(p=>p.Address).Distinct().Count()!=points.Length)
            points=Points.Values.Select(p=>(Point:p,Address:ConnectedIdentity.Digest(p.Key))).ToArray();
        var keys=new string[nodes.Length];
        foreach(var group in points.GroupBy(p=>IndexOf(p.Point.Key)))
        {cancellation.ThrowIfCancellationRequested();keys[group.Key]=ConnectedIdentity.Digest(group.Select(p=>p.Address).Order(StringComparer.Ordinal).ToArray());}
        var identity=ConnectedIdentity.Digest(new
        {
            NumericalPolicy,Context.EffectiveAt,Context.ValuationAt,Context.MarketAsOf,
            Points=points.OrderBy(p=>p.Address,StringComparer.Ordinal).Select(p=>new{p.Address,p.Point.Unit,p.Point.Contract,p.Point.DeliveryFrom,p.Point.DeliveryTo,Node=keys[IndexOf(p.Point.Key)]}).ToArray(),
            Nodes=nodes.Select((n,i)=>
            {
                cancellation.ThrowIfCancellationRequested();
                return new{Key=keys[i],Dependencies=n.Dependencies.Select(d=>keys[d]).ToArray(),n.Weights,n.Divisor,
                    Source=n.Source is {} s?new{s.Input,s.Series,s.Quote,s.Request,Sample=s.Sample is {} sample?
                        new{sample.ValuationAt,sample.MarketCutoff,sample.SelectionPolicy,sample.OriginalUse,sample.FreezeHistorical}:null}:null,
                    Built=n.Built is {} b?new{b.Rule,b.Quote,b.NumericalPolicy,MultiplierSpecification=b.Multiplier?.Specification,
                        MultiplierInput=b.MultiplierInput is {} m?new{m.Input,m.Series}:null}:null};
            }).OrderBy(n=>n.Key,StringComparer.Ordinal).ToArray()
        });
        cancellation.ThrowIfCancellationRequested();return memoLayout=new(identity,keys);
    }
    internal GraphSession<ExactUnitPrice> Advance(ConnectedCurvePlan prior,GraphSession<ExactUnitPrice> session,CancellationToken cancellation)
    {
        var previous=prior.Layout(cancellation).Keys.Select((key,index)=>(key,index)).ToDictionary(p=>p.key,p=>p.index,StringComparer.Ordinal);
        var mapping=Layout(cancellation).Keys.Select(key=>previous[key]).ToArray();
        var changed=new List<int>();var sameLayout=true;
        for(var i=0;i<nodes.Length;i++)
        {
            cancellation.ThrowIfCancellationRequested();var before=prior.nodes[mapping[i]];var after=nodes[i];sameLayout &= mapping[i]==i;
            if(before.Source?.Observation!=after.Source?.Observation||before.Built?.Multiplier!=after.Built?.Multiplier||
                before.Built?.MultiplierInput!=after.Built?.MultiplierInput)changed.Add(i);
        }
        return sameLayout?session.Advance(EvaluateNode,changed,cancellation,ValidateNode):
            session.Rebind(DependencyPlan,EvaluateNode,mapping,changed,cancellation,ValidateNode);
    }
}
