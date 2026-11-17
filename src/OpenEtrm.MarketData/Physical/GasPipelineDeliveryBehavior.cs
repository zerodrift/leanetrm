using System.Collections.Immutable;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.MarketData.Physical;

public sealed record GasPipelineDeliveryInput(GasTransportRouteProfile Route,
    ImmutableArray<GasTransportRouteFlow> Flows, ImmutableArray<GasTransportCapacityAssessment> Capacities);

// Capacity inputs are the complete native assessments already resolved by the
// operating adapter. This projection neither reserves capacity nor grants a
// native admission witness. Route arithmetic uses the existing qualified kernel.
public sealed class GasPipelineDeliveryBehavior : DeliveryBehavior<GasPipelineDeliveryInput>
{
    public const string Implementation = "gas_transport_interval_v1";
    public override string Profile => Implementation;
    private static readonly ImmutableArray<DeliveryCapability> Supported = [
        new("interval_flow", "Chosen receipt and delivery intervals"),
        new("route_conservation", "Explicit fuel, loss, transit and node conservation"),
        new("capacity_assessment", "Complete native commercial capacity assessments")];

    protected override DeliveryAssessment AssessResolved(GasPipelineDeliveryInput inputs)
    {
        if (inputs.Capacities.IsDefault || inputs.Capacities.Any(c => c is null || c.VersionId == Guid.Empty || c.Intervals.IsDefaultOrEmpty)) throw Invalid();
        var result = GasTransportRouteConservation.Assess(inputs.Route, inputs.Flows);
        var profile = inputs.Route;
        var nodes = profile.Nodes.ToDictionary(n => n.Id);
        var segments = profile.Segments.ToDictionary(s => s.Id);
        var activities = ImmutableArray.CreateBuilder<DeliveryActivity>();
        var constraints = ImmutableArray.CreateBuilder<DeliveryConstraint>();
        var zero = ExactQuantity.FromQuantity(new(default, profile.Quantity));
        ExactQuantity Sum(IEnumerable<ExactQuantity> quantities) => quantities.Aggregate(zero, (sum, q) => sum.Add(q));
        string Value(IEnumerable<ExactQuantity> quantities) => Sum(quantities).ToExactString();
        foreach (var flow in inputs.Flows.OrderBy(f => f.SegmentId))
        {
            var segment = segments[flow.SegmentId]; var from = nodes[segment.FromNodeId]; var to = nodes[segment.ToNodeId];
            activities.Add(new(flow.Id, from.Role == GasTransportRouteNode.Source ? "external_receipt" : "transit_receipt",
                "Chosen segment receipt", new DeliveryIntervalGeometry(flow.ReceiptFrom, flow.ReceiptThrough), from.Point, flow.ReceiptQuantity.ToExactString()));
            activities.Add(new(flow.Id, to.Role == GasTransportRouteNode.Sink ? "external_delivery" : "transit_delivery",
                "Chosen segment delivery", new DeliveryIntervalGeometry(flow.DeliveryFrom, flow.DeliveryThrough), to.Point, flow.DeliveryQuantity.ToExactString()));
            var assessment = result.Segments.Single(s => s.SegmentId == segment.Id);
            constraints.Add(new("transit_timing", "Selected elapsed transit", State(assessment.TimingAligned), flow.Id,
                new DeliveryIntervalGeometry(flow.DeliveryFrom, flow.DeliveryThrough), null, null, null, "timing"));
            constraints.AddRange(assessment.Intervals.Select(i => new DeliveryConstraint("segment_conservation", "Fuel/loss-adjusted segment delivery",
                State(i.Conserved), flow.Id, new DeliveryIntervalGeometry(i.From, i.Through), i.DeliveredPerHour.ToExactString(),
                i.ExpectedPerHour.ToExactString(), i.ResidualPerHour.ToExactString(), "per_elapsed_hour")));
        }
        foreach (var node in result.Nodes)
            constraints.AddRange(node.Intervals.Select(i => new DeliveryConstraint("node_conservation", "Intermediate-node flow conservation",
                State(i.Conserved), node.NodeId, new DeliveryIntervalGeometry(i.From, i.Through), i.IncomingPerHour.ToExactString(),
                i.OutgoingPerHour.ToExactString(), i.ResidualPerHour.ToExactString(), "per_elapsed_hour")));
        foreach (var capacity in inputs.Capacities.OrderBy(c => c.VersionId))
            foreach (var interval in capacity.Intervals)
            {
                if (interval.UsedPerHour.Specification != profile.Quantity || interval.MaximumPerHour.Specification != profile.Quantity) throw Invalid();
                var residual = interval.UsedPerHour.Add(interval.MaximumPerHour.Scale(ExactDecimal.Parse("-1")));
                constraints.Add(new("capacity_" + interval.Axis, "Gross " + interval.Axis + " capacity", State(interval.WithinLimit), capacity.VersionId,
                    new DeliveryIntervalGeometry(interval.From, interval.Through), interval.UsedPerHour.ToExactString(),
                    interval.MaximumPerHour.ToExactString(), residual.ToExactString(), "per_elapsed_hour"));
            }
        if (inputs.Capacities.IsEmpty)
            constraints.Add(new("capacity", "Native capacity evidence required", "unassessed", null, null, null, null, null, "per_elapsed_hour"));
        // Boundary totals exclude intermediate handling. Summing all segment
        // receipts would count serial or split/merge throughput as new delivery.
        var externalReceipts = inputs.Flows.Where(f => nodes[segments[f.SegmentId].FromNodeId].Role == GasTransportRouteNode.Source);
        var externalDeliveries = result.Segments.Where(s => nodes[segments[s.SegmentId].ToNodeId].Role == GasTransportRouteNode.Sink);
        return new(Profile, profile.Quantity, activities.ToImmutable(), [
            new("external_receipt", "External chosen receipt", Value(externalReceipts.Select(f => f.ReceiptQuantity))),
            new("fuel", "Selected segment fuel", Value(result.Segments.Select(s => s.FuelQuantity))),
            new("loss", "Selected segment loss", Value(result.Segments.Select(s => s.LossQuantity))),
            new("expected_external_delivery", "Expected external delivery", Value(externalDeliveries.Select(s => s.ExpectedDeliveryQuantity))),
            new("external_delivery", "External chosen delivery", Value(externalDeliveries.Select(s => s.DeliveryQuantity)))], constraints.ToImmutable(), Supported);
    }

    private static string State(bool within) => within ? "within_limit" : "breached";
}
