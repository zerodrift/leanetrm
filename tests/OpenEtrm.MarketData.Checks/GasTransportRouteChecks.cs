using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasTransportRouteChecks
{
    static readonly ReferenceRevision Reference=new(Guid.Parse("c1111111-1111-1111-1111-111111111111"),Guid.Parse("c2222222-2222-2222-2222-222222222222"));
    static readonly ReferenceKey Unit=new(Reference,"synthetic_native_quantity");
    static readonly DateTimeOffset Start=TradingInstant.Parse("2032-01-15T00:00:00Z","synthetic_time");
    static Guid Id(int value)=>Guid.Parse($"c0000000-0000-0000-0000-{value:D12}");
    static ExactQuantity Q(string value)=>ExactQuantity.FromQuantity(new(ExactDecimal.Parse(value),Unit));
    static ExactQuantity Fraction(string numerator,int denominator)=>Q(numerator).Scale(new ExactRatio(1,denominator));
    static GasTransportRouteNode Node(int id,string role)=>new(Id(id),new(Reference,$"synthetic_point_{id}"),role);
    static GasTransportRouteSegment Segment(int id,int from,int to,long lag=0,SignedRatio? fuel=null,SignedRatio? loss=null)=>
        new(Id(id),Id(from),Id(to),fuel??SignedRatio.Zero,loss??SignedRatio.Zero,lag);
    static GasTransportRouteFlow Flow(int segment,DateTimeOffset receiptFrom,DateTimeOffset receiptThrough,ExactQuantity receipt,
        DateTimeOffset deliveryFrom,DateTimeOffset deliveryThrough,ExactQuantity delivery)=>
        new(Id(segment+1000),Id(segment),receiptFrom,receiptThrough,receipt,deliveryFrom,deliveryThrough,delivery);
    static GasTransportRouteProfile Profile(ImmutableArray<GasTransportRouteNode> nodes,ImmutableArray<GasTransportRouteSegment> segments)=>
        new(Id(900),GasTransportRouteProfile.Version,Unit,GasTransportRouteProfile.GrossReceiptFractions,
            GasTransportRouteProfile.UniformSegmentFlow,GasTransportRouteProfile.FixedTransit,GasTransportRouteProfile.NoNodeStorage,nodes,segments);
    static void Exact(ExactQuantity quantity,string numerator,string denominator="1")
    {
        if(quantity.Specification!=Unit||quantity.Numerator!=numerator||quantity.Denominator!=denominator)
            throw new Exception($"Expected exact native quantity {numerator}/{denominator}, received {quantity.Numerator}/{quantity.Denominator}.");
    }
    static void Invalid(Action action)
    {
        try{action();}catch(MarketDataException error)when(error.Code=="gas_transport_route_input"){return;}
        throw new Exception("Missing, conflicting, disconnected or unsupported route inputs must refuse explicitly.");
    }

    internal static void Run()
    {
        var one=Start.AddHours(1);var two=Start.AddHours(2);var three=Start.AddHours(3);
        var route=Profile([Node(1,GasTransportRouteNode.Source),Node(2,GasTransportRouteNode.Transit),Node(3,GasTransportRouteNode.Transit),
            Node(4,GasTransportRouteNode.Transit),Node(5,GasTransportRouteNode.Transit),Node(6,GasTransportRouteNode.Sink)],
            [Segment(11,1,2,3_600_000_000,new(1,50)),Segment(12,2,3),Segment(13,2,4),
                Segment(14,3,5,3_600_000_000),Segment(15,4,5,3_600_000_000),Segment(16,5,6),Segment(17,2,5,3_600_000_000)]);
        ImmutableArray<GasTransportRouteFlow> flows=[Flow(11,Start,one,Q("100"),one,two,Q("98")),
            Flow(12,one,two,Q("60"),one,two,Q("60")),Flow(13,one,two,Q("38"),one,two,Q("38")),
            Flow(14,one,two,Q("60"),two,three,Q("60")),Flow(15,one,two,Q("38"),two,three,Q("38")),
            Flow(16,two,three,Q("98"),two,three,Q("98")),Flow(17,one,two,Q("0"),two,three,Q("0"))];
        var split=GasTransportRouteConservation.Assess(route,flows).RequireFeasible();
        if(!split.Feasible||split.VersionId!=route.VersionId||split.Segments.Length!=7||split.Nodes.Length!=4)
            throw new Exception("A complete chosen plan must conserve 100 receipts less explicit fuel2, split60+38, and merge98 across saved transit lags.");
        var first=split.Segments.Single(s=>s.SegmentId==Id(11));
        if(first.FlowId!=Id(1011)||!first.TimingAligned||!first.Conserved)throw new Exception("The assessment must preserve exact segment and flow identities.");
        Exact(first.ReceiptQuantity,"100");Exact(first.FuelQuantity,"2");Exact(first.LossQuantity,"0");Exact(first.ExpectedDeliveryQuantity,"98");Exact(first.DeliveryQuantity,"98");
        var hub=split.Nodes.Single(n=>n.NodeId==Id(2)).Intervals.Single();
        Exact(hub.IncomingPerHour,"98");Exact(hub.OutgoingPerHour,"98");Exact(hub.ResidualPerHour,"0");
        if(!hub.Incoming.SequenceEqual([new GasTransportRouteContribution(Id(11),Id(1011))])||
            !hub.Outgoing.Select(c=>c.FlowId).ToHashSet().SetEquals(new[]{Id(1012),Id(1013),Id(1017)}))
            throw new Exception("Node conservation must retain the explicit split allocations, including the declared idle segment.");
        Exact(split.Nodes.Single(n=>n.NodeId==Id(5)).Intervals.Single().IncomingPerHour,"98");
        var reordered=GasTransportRouteConservation.Assess(route with{Nodes=[..route.Nodes.Reverse()],Segments=[..route.Segments.Reverse()]},[..flows.Reverse()]);
        if(JsonSerializer.Serialize(split)!=JsonSerializer.Serialize(reordered))
            throw new Exception("Enumeration order must not change the complete route assessment or its contributing identities.");

        // Local excess and shortfall cannot cancel merely because endpoint totals match.
        var wrongSplit=flows.SetItem(1,flows[1] with{ReceiptQuantity=Q("61"),DeliveryQuantity=Q("61")});
        var residual=GasTransportRouteConservation.Assess(route,wrongSplit);
        if(residual.Feasible||residual.Segments.Any(s=>!s.Conserved))throw new Exception("Explicit segment conservation cannot hide a mismatched intermediate split.");
        Exact(residual.Nodes.Single(n=>n.NodeId==Id(2)).Intervals.Single().ResidualPerHour,"-1");
        Exact(residual.Nodes.Single(n=>n.NodeId==Id(3)).Intervals.Single().ResidualPerHour,"1");
        try{residual.RequireFeasible();throw new Exception("Voluntary admission must require route conservation explicitly.");}
        catch(MarketDataException error)when(error.Code=="gas_transport_route_conservation"){}
        Exact(residual.Segments.Single(s=>s.SegmentId==Id(12)).ReceiptQuantity,"61");

        var early=GasTransportRouteConservation.Assess(route,flows.SetItem(0,flows[0] with{DeliveryFrom=Start,DeliveryThrough=one}));
        var earlySegment=early.Segments.Single(s=>s.SegmentId==Id(11));
        if(early.Feasible||earlySegment.TimingAligned||earlySegment.Conserved||earlySegment.Intervals.Length!=2)
            throw new Exception("Equal period totals do not repair a delivery interval that violates the explicit transit lag.");
        Exact(earlySegment.Intervals[0].ResidualPerHour,"98");Exact(earlySegment.Intervals[1].ResidualPerHour,"-98");
        var spread=GasTransportRouteConservation.Assess(route,flows.SetItem(0,flows[0] with{DeliveryThrough=three}));
        Exact(spread.Segments.Single(s=>s.SegmentId==Id(11)).Intervals[0].DeliveredPerHour,"49");
        if(spread.Feasible)throw new Exception("A delivery with a different uniform duration cannot conserve the selected instantaneous plan.");

        var direct=Profile([Node(1,GasTransportRouteNode.Source),Node(6,GasTransportRouteNode.Sink)],
            [Segment(11,1,6,1,new(1,3))]);
        var fractional=GasTransportRouteConservation.Assess(direct,
            [Flow(11,Start,Start.AddSeconds(7),Q("1"),Start.AddMicroseconds(1),Start.AddSeconds(7).AddMicroseconds(1),Fraction("2",3))]).RequireFeasible();
        Exact(fractional.Segments.Single().FuelQuantity,"1","3");Exact(fractional.Segments.Single().ExpectedDeliveryQuantity,"2","3");
        Exact(fractional.Segments.Single().Intervals.Single().ExpectedPerHour,"2400","7");
        var micros=direct with{Segments=[Segment(11,1,6,7,new(1,3),new(1,6))]};
        var micro=GasTransportRouteConservation.Assess(micros,
            [Flow(11,Start,Start.AddMicroseconds(3),Q("1"),Start.AddMicroseconds(7),Start.AddMicroseconds(10),Fraction("1",2))]).RequireFeasible();
        Exact(micro.Segments.Single().FuelQuantity,"1","3");Exact(micro.Segments.Single().LossQuantity,"1","6");
        Exact(micro.Segments.Single().ExpectedDeliveryQuantity,"1","2");Exact(micro.Segments.Single().Intervals.Single().ExpectedPerHour,"600000000");
        var rounded=GasTransportRouteConservation.Assess(direct,
            [Flow(11,Start,Start.AddSeconds(7),Q("1"),Start.AddMicroseconds(1),Start.AddSeconds(7).AddMicroseconds(1),Q("0.666666666666666667"))]);
        if(rounded.Feasible)throw new Exception("An explicit decimal approximation must not become an exactly conserving fractional delivery.");
        Exact(rounded.Segments.Single().Intervals.Single().ResidualPerHour,"3","17500000000000000");

        // Inputs are complete declarations, not requests to infer route geometry or economics.
        Invalid(()=>GasTransportRouteConservation.Assess(route,flows.RemoveAt(6)));
        Invalid(()=>GasTransportRouteConservation.Assess(route,[..flows,flows[0] with{Id=Id(2000)}]));
        Invalid(()=>GasTransportRouteConservation.Assess(route,flows.SetItem(1,flows[1] with{Id=flows[0].Id})));
        Invalid(()=>GasTransportRouteConservation.Assess(route,flows.SetItem(1,flows[1] with{SegmentId=Id(999)})));
        Invalid(()=>GasTransportRouteConservation.Assess(route,default));
        Invalid(()=>GasTransportRouteConservation.Assess(route with{FractionBasis=""},flows));
        Invalid(()=>GasTransportRouteConservation.Assess(route with{NodeBalanceLaw="implicit_storage"},flows));
        Invalid(()=>GasTransportRouteConservation.Assess(route with{Nodes=route.Nodes.SetItem(0,route.Nodes[0] with{Role=GasTransportRouteNode.Transit})},flows));
        Invalid(()=>GasTransportRouteConservation.Assess(route with{Nodes=route.Nodes.SetItem(1,route.Nodes[1] with{Role=GasTransportRouteNode.Source})},flows));
        Invalid(()=>GasTransportRouteConservation.Assess(route with{Nodes=route.Nodes.SetItem(1,route.Nodes[1] with{Point=route.Nodes[0].Point})},flows));
        Invalid(()=>GasTransportRouteConservation.Assess(route with{Segments=route.Segments.SetItem(0,route.Segments[0] with{FuelFraction=new(3,4),LossFraction=new(1,2)})},flows));
        Invalid(()=>GasTransportRouteConservation.Assess(route with{Segments=route.Segments.SetItem(0,route.Segments[0] with{FuelFraction=new(-1,3)})},flows));
        Invalid(()=>GasTransportRouteConservation.Assess(route with{Segments=route.Segments.SetItem(0,route.Segments[0] with{LossFraction=default})},flows));
        Invalid(()=>GasTransportRouteConservation.Assess(route with{Segments=route.Segments.SetItem(0,route.Segments[0] with{TransitMicroseconds=-1})},flows));
        Invalid(()=>GasTransportRouteConservation.Assess(route with{Segments=route.Segments.SetItem(0,route.Segments[0] with{TransitMicroseconds=long.MaxValue})},flows));
        Invalid(()=>GasTransportRouteConservation.Assess(route,flows.SetItem(0,flows[0] with{ReceiptFrom=Start.AddTicks(1)})));
        Invalid(()=>GasTransportRouteConservation.Assess(route,flows.SetItem(0,flows[0] with{DeliveryFrom=one.ToOffset(TimeSpan.FromHours(1))})));
        Invalid(()=>GasTransportRouteConservation.Assess(route,flows.SetItem(0,flows[0] with{ReceiptThrough=Start})));
        Invalid(()=>GasTransportRouteConservation.Assess(route,flows.SetItem(0,flows[0] with{ReceiptQuantity=Q("-1")})));
        Invalid(()=>GasTransportRouteConservation.Assess(route,flows.SetItem(0,flows[0] with{DeliveryQuantity=ExactQuantity.FromQuantity(new(ExactDecimal.Parse("98"),new(Reference,"other_native_basis")))})));
        var otherReference=Reference with{RevisionId=Id(700)};
        Invalid(()=>GasTransportRouteConservation.Assess(route with{Nodes=route.Nodes.SetItem(0,route.Nodes[0] with{Point=new(otherReference,"synthetic_point_1")})},flows));
        var cycle=route with{Segments=[..route.Segments,Segment(18,5,2)]};
        Invalid(()=>GasTransportRouteConservation.Assess(cycle,[..flows,Flow(18,one,two,Q("0"),one,two,Q("0"))]));
        var disconnected=route with{Nodes=[..route.Nodes,Node(7,GasTransportRouteNode.Source),Node(8,GasTransportRouteNode.Sink)],Segments=[..route.Segments,Segment(18,7,8)]};
        Invalid(()=>GasTransportRouteConservation.Assess(disconnected,[..flows,Flow(18,one,two,Q("0"),one,two,Q("0"))]));
        Console.WriteLine("PASS transport route: explicit roots/sinks, gross fuel/loss, split/merge, fixed transit, exact fractional and microsecond conservation, complete chosen inputs and unmodified breaches");
    }
}
