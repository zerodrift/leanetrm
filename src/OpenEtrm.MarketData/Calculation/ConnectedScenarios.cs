using OpenEtrm.Shared.Calculation;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Calculation;

public enum CurveShockCoordinate { MarketQuote=1, ComputedOutput }
public abstract record CurveShockTarget(CurveKey Curve);
public sealed record ParallelCurveTarget(CurveKey Curve) : CurveShockTarget(Curve);
public sealed record ContractCurveTarget(CurveKey Curve,ContractKey Contract) : CurveShockTarget(Curve);
public sealed record PointCurveTarget(CurveKey Curve,ImmutableArray<Guid> Points) : CurveShockTarget(Curve);
public sealed record DeliveryBucketCurveTarget(CurveKey Curve,DateOnly From,DateOnly To,string Convention) : CurveShockTarget(Curve)
{
    public const string WholeDelivery="whole_point_delivery_contained_half_open_v1";
}
public sealed record CurveShift(CurveShockCoordinate Coordinate,CurveShockTarget Target,PriceUnit Unit,ExactDecimal Size,string Convention)
{
    public const string ParentAdditive="absolute_curve_coordinate_parent_additive_v1";
}
public sealed record ResolvedCurveShift(CurveShockCoordinate Coordinate,CurvePointKey Point,int Node,PriceUnit Unit,
    ExactDecimal Size,InputKey? Input,ObservationKey? Observation);

// A market scenario owns only its overlays and completed changed results. The
// immutable graph, exact context, definitions and target indexes are shared.
public sealed class ConnectedScenario
{
    private const string IdentityVersion="connected_curve_scenario_v1";
    private readonly GraphSession<ExactUnitPrice> session;
    private readonly TargetIndex targets;
    public ConnectedCurvePlan Plan{get;}
    public string Id{get;}
    public string? ParentId{get;}
    public int Depth=>session.Depth;
    public GraphWork Work=>session.Work;
    public ImmutableArray<ResolvedCurveShift> Shifts{get;}

    private ConnectedScenario(ConnectedCurvePlan plan,GraphSession<ExactUnitPrice> session,TargetIndex targets,
        string id,string? parent,ImmutableArray<ResolvedCurveShift> shifts)
    {Plan=plan;this.session=session;this.targets=targets;Id=id;ParentId=parent;Shifts=shifts;}

    public static ConnectedScenario Base(ConnectedCurvePlan plan,ConnectedCalculationCache? cache=null,CancellationToken cancellation=default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellation.ThrowIfCancellationRequested();
        var id=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {version=IdentityVersion,plan=plan.ManifestId,context=plan.Context,policy=ConnectedCurves.NumericalPolicy})));
        try{return new(plan,cache?.Bind(plan,cancellation)??plan.CreateSession(),new(plan),id,null,[]);}
        catch(CalculationException error){throw new MarketDataException(error.Code,error.Message);}
    }

    public ExactUnitPrice Price(CurvePointKey point,CancellationToken cancellation=default)
    {
        try{return session.Get(Plan.IndexOf(point),cancellation);}
        catch(CalculationException error){throw new MarketDataException(error.Code,error.Message);}
    }

    public ImmutableArray<ConnectedCurvePoint> ResolveTarget(CurveShockCoordinate coordinate,CurveShockTarget target,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(!Enum.IsDefined(coordinate))throw Error("scenario_coordinate","Select a supported price-input or computed-output coordinate.");
        if(target is null)throw Error("scenario_target","Supply an explicit curve target.");
        var selected=targets.Resolve(target,cancellation);
        foreach(var point in selected)
        {
            cancellation.ThrowIfCancellationRequested();var source=point.Expression as ConnectedSource;
            if(coordinate==CurveShockCoordinate.MarketQuote&&(source is null||source.Input.Use!=InputUse.Variable))
                throw Error("scenario_coordinate","An input shock requires a live primitive quote; frozen facts and computed outputs are different coordinates.");
            if(coordinate==CurveShockCoordinate.ComputedOutput&&source is not null)
                throw Error("scenario_coordinate","An output shock requires a computed curve point; select the input coordinate for primitive quotes.");
        }
        return selected;
    }

    public ConnectedScenario WithShifts(ImmutableArray<CurveShift> shifts,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(shifts.IsDefaultOrEmpty||shifts.Any(s=>s is null||s.Target is null||s.Unit is null||s.Size==default||s.Convention!=CurveShift.ParentAdditive))
            throw Error("scenario_shift","Supply nonzero signed absolute shifts with explicit targets, units and the parent-additive convention.");
        var resolved=new Dictionary<int,ResolvedCurveShift>();
        foreach(var shift in shifts)
        {
            var selected=ResolveTarget(shift.Coordinate,shift.Target,cancellation);var aliases=new HashSet<int>();
            foreach(var point in selected)
            {
                cancellation.ThrowIfCancellationRequested();var node=Plan.IndexOf(point.Key);
                if(!aliases.Add(node))continue; // Exact aliases of one input receive one shared shift.
                if(resolved.ContainsKey(node))throw Error("scenario_target","A layer cannot shift the same resolved coordinate twice; use an explicit nested layer.");
                if(point.Unit!=shift.Unit)throw Error("unit_mismatch","A shift must retain its target's exact price unit and quote multiplier.");
                var source=point.Expression as ConnectedSource;
                if(shift.Coordinate==CurveShockCoordinate.MarketQuote)
                {
                    var moved=Price(point.Key,cancellation).Add(ExactUnitPrice.FromPrice(new(shift.Size,shift.Unit))).ToUnitPriceExact();
                    if(moved.Value.CompareTo(source!.Quote.Minimum)<0||moved.Value.CompareTo(source!.Quote.Maximum)>0)
                        throw Error("price_domain","The shifted quote is outside its explicitly selected domain.");
                }
                resolved.Add(node,new(shift.Coordinate,point.Key,node,shift.Unit,shift.Size,source?.Input,source?.Observation.Key));
            }
        }
        var ordered=resolved.Values.OrderBy(s=>s.Node).ToImmutableArray();
        var offsets=ordered.ToDictionary(s=>s.Node,s=>ExactUnitPrice.FromPrice(new(s.Size,s.Unit)));
        GraphSession<ExactUnitPrice> child;
        try{child=session.WithOffsets(offsets,cancellation);}
        catch(CalculationException error){throw new MarketDataException(error.Code,error.Message);}
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(IdentityVersion+":"+Id+":"+CurveShift.ParentAdditive));
        foreach(var shift in ordered)
        {
            cancellation.ThrowIfCancellationRequested();
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new{coordinate=shift.Coordinate,point=shift.Point,
                size=shift.Size.ToString(),currency=shift.Unit.Currency,quantity=shift.Unit.Quantity,
                multiplier=shift.Unit.Multiplier.ToString(),input=shift.Input,observation=shift.Observation}));
        }
        return new(Plan,child,targets,Convert.ToHexStringLower(hash.GetHashAndReset()),Id,ordered);
    }

    private sealed class TargetIndex
    {
        private sealed class CurvePoints
        {
            public ConnectedCurvePoint[] Ordered{get;}
            public int[] MaximumEnd{get;}
            public Dictionary<ContractKey,ConnectedCurvePoint[]> Contracts{get;}
            public CurvePoints(IEnumerable<ConnectedCurvePoint> points)
            {
                Ordered=points.OrderBy(p=>p.DeliveryFrom).ThenBy(p=>p.DeliveryTo).ThenBy(p=>p.Key.PointId).ToArray();
                MaximumEnd=new int[Ordered.Length];var end=0;
                for(var i=0;i<Ordered.Length;i++){end=Math.Max(end,Ordered[i].DeliveryTo.DayNumber);MaximumEnd[i]=end;}
                Contracts=Ordered.Where(p=>p.Contract is not null).GroupBy(p=>p.Contract!.Key).ToDictionary(g=>g.Key,g=>g.ToArray());
            }
        }
        private readonly ConnectedCurvePlan plan;
        private readonly Dictionary<CurveKey,CurvePoints> curves;
        public TargetIndex(ConnectedCurvePlan plan)
        {this.plan=plan;curves=plan.Points.Values.GroupBy(p=>p.Key.Curve).ToDictionary(g=>g.Key,g=>new CurvePoints(g));}
        public ImmutableArray<ConnectedCurvePoint> Resolve(CurveShockTarget target,CancellationToken cancellation)
        {
            if(!curves.TryGetValue(target.Curve,out var curve))throw Error("scenario_target","The exact target curve is outside this resolved calculation plan.");
            IEnumerable<ConnectedCurvePoint> selected;
            switch(target)
            {
                case ParallelCurveTarget: selected=curve.Ordered;break;
                case ContractCurveTarget contract:
                    if(!curve.Contracts.TryGetValue(contract.Contract,out var matches))throw Error("scenario_target","The exact dated contract is absent from the selected curve.");
                    selected=matches;break;
                case PointCurveTarget points:
                    if(points.Points.IsDefaultOrEmpty||points.Points.Any(p=>p==Guid.Empty)||points.Points.Distinct().Count()!=points.Points.Length)
                        throw Error("scenario_target","Select distinct retained point identities for a curve bucket.");
                    selected=points.Points.Order().Select(id=>plan.Points.TryGetValue(new(target.Curve,id),out var point)?point:
                        throw Error("scenario_target","A selected point is absent from this exact curve version."));break;
                case DeliveryBucketCurveTarget bucket:
                    if(bucket.From>=bucket.To||bucket.Convention!=DeliveryBucketCurveTarget.WholeDelivery)
                        throw Error("scenario_bucket","Select a nonempty half-open delivery bucket and its whole-point containment convention.");
                    var start=0;var end=curve.Ordered.Length;
                    while(start<end){var middle=start+(end-start)/2;if(curve.MaximumEnd[middle]<=bucket.From.DayNumber)start=middle+1;else end=middle;}
                    var contained=new List<ConnectedCurvePoint>();
                    for(var i=start;i<curve.Ordered.Length&&curve.Ordered[i].DeliveryFrom<bucket.To;i++)
                    {
                        cancellation.ThrowIfCancellationRequested();var point=curve.Ordered[i];
                        if(point.PointDate is not null||point.DeliveryTo<=bucket.From)continue;
                        if(point.DeliveryFrom<bucket.From||point.DeliveryTo>bucket.To)
                            throw Error("scenario_bucket","A bucket boundary splits a retained curve point; select explicit points or a supported full delivery range.");
                        contained.Add(point);
                    }
                    selected=contained;break;
                default:throw Error("scenario_target","Unsupported curve target selection.");
            }
            var result=selected.ToImmutableArray();
            if(result.IsEmpty)throw Error("scenario_target","The selected curve bucket contains no retained points.");
            return result;
        }
    }
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
