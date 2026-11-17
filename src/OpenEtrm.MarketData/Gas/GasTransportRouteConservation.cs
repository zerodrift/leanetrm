using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasTransportRouteNode(Guid Id,ReferenceKey Point,string Role)
{
    public const string Source="external_receipt_v1",Transit="no_storage_transit_v1",Sink="external_delivery_v1";
}
public sealed record GasTransportRouteSegment(Guid Id,Guid FromNodeId,Guid ToNodeId,SignedRatio FuelFraction,SignedRatio LossFraction,long TransitMicroseconds);
public sealed record GasTransportRouteProfile(Guid VersionId,string Implementation,ReferenceKey Quantity,string FractionBasis,string FlowShape,
    string TransitLaw,string NodeBalanceLaw,ImmutableArray<GasTransportRouteNode> Nodes,ImmutableArray<GasTransportRouteSegment> Segments)
{
    public const string Version="gas_transport_chosen_uniform_route_v1";
    public const string GrossReceiptFractions="fuel_and_loss_each_fraction_of_gross_receipt_v1";
    public const string UniformSegmentFlow="one_explicit_uniform_receipt_and_delivery_per_segment_v1";
    public const string FixedTransit="fixed_nonnegative_elapsed_microseconds_v1";
    public const string NoNodeStorage="simultaneous_inflow_equals_outflow_no_storage_v1";
}

// A complete chosen plan supplies every segment, including an explicit zero for
// an idle segment. Receipt and delivery are separate entered stages; assessment
// never adjusts either stage or invents an allocation at a split or merge.
public sealed record GasTransportRouteFlow(Guid Id,Guid SegmentId,DateTimeOffset ReceiptFrom,DateTimeOffset ReceiptThrough,ExactQuantity ReceiptQuantity,
    DateTimeOffset DeliveryFrom,DateTimeOffset DeliveryThrough,ExactQuantity DeliveryQuantity);
public sealed record GasTransportRouteContribution(Guid SegmentId,Guid FlowId);
public sealed record GasTransportRouteSegmentInterval(DateTimeOffset From,DateTimeOffset Through,ExactQuantity ExpectedPerHour,
    ExactQuantity DeliveredPerHour,ExactQuantity ResidualPerHour,bool Conserved);
public sealed record GasTransportRouteSegmentAssessment(Guid SegmentId,Guid FlowId,ExactQuantity ReceiptQuantity,ExactQuantity FuelQuantity,
    ExactQuantity LossQuantity,ExactQuantity ExpectedDeliveryQuantity,ExactQuantity DeliveryQuantity,bool TimingAligned,bool Conserved,
    ImmutableArray<GasTransportRouteSegmentInterval> Intervals);
public sealed record GasTransportRouteNodeInterval(DateTimeOffset From,DateTimeOffset Through,ImmutableArray<GasTransportRouteContribution> Incoming,
    ImmutableArray<GasTransportRouteContribution> Outgoing,ExactQuantity IncomingPerHour,ExactQuantity OutgoingPerHour,ExactQuantity ResidualPerHour,bool Conserved);
public sealed record GasTransportRouteNodeAssessment(Guid NodeId,ReferenceKey Point,bool Conserved,ImmutableArray<GasTransportRouteNodeInterval> Intervals);
public sealed record GasTransportRouteAssessment(Guid VersionId,bool Feasible,ImmutableArray<GasTransportRouteSegmentAssessment> Segments,
    ImmutableArray<GasTransportRouteNodeAssessment> Nodes)
{
    public GasTransportRouteAssessment RequireFeasible()=>Feasible?this:
        throw new MarketDataException("gas_transport_route_conservation","The chosen segment plan violates its saved fuel, loss, transit or intermediate-node conservation law.");
}

public static class GasTransportRouteConservation
{
    public const int MaximumNodes=128,MaximumSegments=256;
    sealed record Stage(Guid SegmentId,Guid FlowId,DateTimeOffset From,DateTimeOffset Through,SignedRatio Total)
    {
        public SignedRatio PerHour=>Total*new SignedRatio(TimeSpan.TicksPerHour,(Through-From).Ticks);
        public GasTransportRouteContribution Contribution=>new(SegmentId,FlowId);
    }

    // This component assesses a declared chosen plan. It grants no title,
    // custody, physical evidence, balancing right, cash or pricing capability.
    public static GasTransportRouteAssessment Assess(GasTransportRouteProfile profile,ImmutableArray<GasTransportRouteFlow> flows)
    {
        Validate(profile,flows);
        ExactQuantity Exact(SignedRatio value)=>new(value.Numerator,value.Denominator,profile.Quantity);
        var uses=flows.ToDictionary(f=>f.SegmentId);
        var segments=ImmutableArray.CreateBuilder<GasTransportRouteSegmentAssessment>();
        var receipts=new Dictionary<Guid,Stage>();var deliveries=new Dictionary<Guid,Stage>();
        foreach(var segment in profile.Segments.OrderBy(s=>s.Id))
        {
            var flow=uses[segment.Id];var receipt=Value(flow.ReceiptQuantity);
            var fuel=receipt*segment.FuelFraction;var loss=receipt*segment.LossFraction;var remaining=receipt-fuel-loss;
            var expected=new Stage(segment.Id,flow.Id,Shift(flow.ReceiptFrom,segment.TransitMicroseconds),Shift(flow.ReceiptThrough,segment.TransitMicroseconds),remaining);
            var actual=new Stage(segment.Id,flow.Id,flow.DeliveryFrom,flow.DeliveryThrough,Value(flow.DeliveryQuantity));
            receipts.Add(segment.Id,new(segment.Id,flow.Id,flow.ReceiptFrom,flow.ReceiptThrough,receipt));deliveries.Add(segment.Id,actual);
            var timing=expected.From==actual.From&&expected.Through==actual.Through;
            var intervals=ImmutableArray.CreateBuilder<GasTransportRouteSegmentInterval>();
            foreach(var (from,through) in Windows([expected,actual]))
            {
                var expectedRate=Rate([expected],from,through);var deliveredRate=Rate([actual],from,through);var residual=deliveredRate-expectedRate;
                intervals.Add(new(from,through,Exact(expectedRate),Exact(deliveredRate),Exact(residual),residual.Numerator.IsZero));
            }
            segments.Add(new(segment.Id,flow.Id,flow.ReceiptQuantity,Exact(fuel),Exact(loss),Exact(remaining),flow.DeliveryQuantity,
                timing,timing&&intervals.All(i=>i.Conserved),intervals.ToImmutable()));
        }
        var nodes=ImmutableArray.CreateBuilder<GasTransportRouteNodeAssessment>();
        foreach(var node in profile.Nodes.Where(n=>n.Role==GasTransportRouteNode.Transit).OrderBy(n=>n.Id))
        {
            var incoming=profile.Segments.Where(s=>s.ToNodeId==node.Id).Select(s=>deliveries[s.Id]).OrderBy(s=>s.SegmentId).ToArray();
            var outgoing=profile.Segments.Where(s=>s.FromNodeId==node.Id).Select(s=>receipts[s.Id]).OrderBy(s=>s.SegmentId).ToArray();
            var intervals=ImmutableArray.CreateBuilder<GasTransportRouteNodeInterval>();
            foreach(var (from,through) in Windows(incoming.Concat(outgoing)))
            {
                var inflow=incoming.Where(s=>Active(s,from,through)).ToArray();var outflow=outgoing.Where(s=>Active(s,from,through)).ToArray();
                var incomingRate=Rate(inflow,from,through);var outgoingRate=Rate(outflow,from,through);var residual=incomingRate-outgoingRate;
                intervals.Add(new(from,through,[..inflow.Select(s=>s.Contribution)],[..outflow.Select(s=>s.Contribution)],
                    Exact(incomingRate),Exact(outgoingRate),Exact(residual),residual.Numerator.IsZero));
            }
            nodes.Add(new(node.Id,node.Point,intervals.All(i=>i.Conserved),intervals.ToImmutable()));
        }
        return new(profile.VersionId,segments.All(s=>s.Conserved)&&nodes.All(n=>n.Conserved),segments.ToImmutable(),nodes.ToImmutable());
    }

    static SignedRatio Value(ExactQuantity quantity)=>new(quantity.NumeratorValue,quantity.DenominatorValue);
    static bool Active(Stage stage,DateTimeOffset from,DateTimeOffset through)=>stage.From<=from&&stage.Through>=through;
    static SignedRatio Rate(IEnumerable<Stage> stages,DateTimeOffset from,DateTimeOffset through)=>
        SignedRatio.Sum(stages.Where(s=>Active(s,from,through)).Select(s=>s.PerHour));
    static IEnumerable<(DateTimeOffset From,DateTimeOffset Through)> Windows(IEnumerable<Stage> stages)
    {
        var boundaries=stages.SelectMany(s=>new[]{s.From,s.Through}).Distinct().Order().ToArray();
        for(var i=1;i<boundaries.Length;i++)yield return(boundaries[i-1],boundaries[i]);
    }
    static DateTimeOffset Shift(DateTimeOffset at,long microseconds)
    {
        if(microseconds<0||microseconds>(DateTimeOffset.MaxValue.Ticks-at.Ticks)/10)throw Invalid();
        var shifted=at.AddTicks(microseconds*10);
        if(!TradingInstant.IsSupported(shifted))throw Invalid();
        return shifted;
    }
    static MarketDataException Invalid()=>new("gas_transport_route_input",
        "Supply the installed route laws, explicit connected acyclic segments and source/sink roles, exact coherent references, fuel/loss fractions, transit lags and one complete uniform flow per segment.");

    public static void ValidateProfile(GasTransportRouteProfile profile)
    {
        if(profile is null||profile.VersionId==Guid.Empty||profile.Implementation!=GasTransportRouteProfile.Version||
            profile.FractionBasis!=GasTransportRouteProfile.GrossReceiptFractions||profile.FlowShape!=GasTransportRouteProfile.UniformSegmentFlow||
            profile.TransitLaw!=GasTransportRouteProfile.FixedTransit||profile.NodeBalanceLaw!=GasTransportRouteProfile.NoNodeStorage||
            profile.Nodes.IsDefaultOrEmpty||profile.Nodes.Length<2||profile.Nodes.Length>MaximumNodes||profile.Nodes.Any(n=>n is null)||
            profile.Segments.IsDefaultOrEmpty||profile.Segments.Length>MaximumSegments||profile.Segments.Any(s=>s is null)||
            profile.Nodes.Select(n=>n.Id).Distinct().Count()!=profile.Nodes.Length||profile.Nodes.Select(n=>n.Point).Distinct().Count()!=profile.Nodes.Length||
            profile.Segments.Select(s=>s.Id).Distinct().Count()!=profile.Segments.Length)throw Invalid();
        void Key(ReferenceKey key)
        {
            try{Money.RequireKey(key);}catch(MarketDataException){throw Invalid();}
            if(key.Revision!=profile.Quantity.Revision)throw Invalid();
        }
        Key(profile.Quantity);
        var nodes=profile.Nodes.ToDictionary(n=>n.Id);
        foreach(var node in profile.Nodes)
        {
            Key(node.Point);
            if(node.Id==Guid.Empty)throw Invalid();
        }
        bool Fraction(SignedRatio value)=>value.Denominator>0&&value.Numerator>=0&&value.Numerator<=value.Denominator;
        foreach(var segment in profile.Segments)
            if(segment.Id==Guid.Empty||segment.FromNodeId==segment.ToNodeId||!nodes.ContainsKey(segment.FromNodeId)||!nodes.ContainsKey(segment.ToNodeId)||
                !Fraction(segment.FuelFraction)||!Fraction(segment.LossFraction)||
                (segment.FuelFraction+segment.LossFraction).CompareTo(new SignedRatio(1,1))>0||segment.TransitMicroseconds<0)throw Invalid();
        foreach(var node in profile.Nodes)
        {
            var incoming=profile.Segments.Count(s=>s.ToNodeId==node.Id);var outgoing=profile.Segments.Count(s=>s.FromNodeId==node.Id);
            if(node.Role switch
            {
                GasTransportRouteNode.Source=>incoming!=0||outgoing==0,
                GasTransportRouteNode.Sink=>incoming==0||outgoing!=0,
                GasTransportRouteNode.Transit=>incoming==0||outgoing==0,
                _=>true
            })throw Invalid();
        }
        var reached=new HashSet<Guid>();var pending=new Stack<Guid>();pending.Push(profile.Nodes[0].Id);
        while(pending.TryPop(out var current))
            if(reached.Add(current))
                foreach(var edge in profile.Segments.Where(s=>s.FromNodeId==current||s.ToNodeId==current))pending.Push(edge.FromNodeId==current?edge.ToNodeId:edge.FromNodeId);
        if(reached.Count!=nodes.Count)throw Invalid();
        var indegrees=profile.Nodes.ToDictionary(n=>n.Id,n=>profile.Segments.Count(s=>s.ToNodeId==n.Id));
        var ready=new Queue<Guid>(indegrees.Where(p=>p.Value==0).Select(p=>p.Key));var visited=0;
        while(ready.TryDequeue(out var current))
        {
            visited++;
            foreach(var edge in profile.Segments.Where(s=>s.FromNodeId==current))if(--indegrees[edge.ToNodeId]==0)ready.Enqueue(edge.ToNodeId);
        }
        if(visited!=nodes.Count)throw Invalid();
    }
    static void Validate(GasTransportRouteProfile profile,ImmutableArray<GasTransportRouteFlow> flows)
    {
        ValidateProfile(profile);
        if(flows.IsDefault||flows.Length!=profile.Segments.Length||flows.Any(f=>f is null)||
            flows.Select(f=>f.Id).Distinct().Count()!=flows.Length||flows.Select(f=>f.SegmentId).Distinct().Count()!=flows.Length)throw Invalid();
        var segmentIds=profile.Segments.Select(s=>s.Id).ToHashSet();
        bool Quantity(ExactQuantity? value)=>value is not null&&value.Specification==profile.Quantity&&value.NumeratorValue>=0;
        bool Window(DateTimeOffset from,DateTimeOffset through)=>TradingInstant.IsSupported(from)&&TradingInstant.IsSupported(through)&&from<through;
        foreach(var flow in flows)
        {
            if(flow.Id==Guid.Empty||!segmentIds.Contains(flow.SegmentId)||!Quantity(flow.ReceiptQuantity)||!Quantity(flow.DeliveryQuantity)||
                !Window(flow.ReceiptFrom,flow.ReceiptThrough)||!Window(flow.DeliveryFrom,flow.DeliveryThrough))throw Invalid();
            var segment=profile.Segments.Single(s=>s.Id==flow.SegmentId);
            Shift(flow.ReceiptFrom,segment.TransitMicroseconds);Shift(flow.ReceiptThrough,segment.TransitMicroseconds);
        }
    }
}
