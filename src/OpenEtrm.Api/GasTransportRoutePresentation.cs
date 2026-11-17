using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Projection only: every number and interval comes from the managed assessment.
internal static class GasTransportRoutePresentation
{
    internal static object[] GraphTables(JsonElement facts)=>Project(()=>{
        var nodes=Rows(facts,"nodes");var names=nodes.ToDictionary(n=>n.Id("id"),n=>Label(n,"node_key"));
        var capacities=Rows(facts,"sourceCapacities").ToDictionary(c=>c.GetProperty("entitlement").Id("id"));
        return [new{label="Saved route nodes",columns=new[]{"Node","Role","Pipeline","Zone","Point"},
            rows=nodes.Select(n=>new[]{Label(n,"node_key"),Role(n.Text("role")),Label(n,"pipeline_code"),Label(n,"zone_code"),Label(n,"point_code")}).ToArray()},
            new{label="Saved immutable route segments",columns=new[]{"Segment","From node","To node","Source entitlement","Source path","Gross fuel fraction","Gross loss fraction","Elapsed transit microseconds"},
                rows=Rows(facts,"segments").Select(s=>{
                    var capacity=capacities[s.Id("entitlement_id")];var path=Rows(capacity,"paths").Single(p=>p.Id("id")==s.Id("path_id"));
                    return new[]{Label(s,"segment_key"),names[s.Id("from_node_id")],names[s.Id("to_node_id")],Label(capacity.GetProperty("entitlement"),"business_key"),Label(path,"path_key"),
                        s.Text("fuel_n")+"/"+s.Text("fuel_d"),s.Text("loss_n")+"/"+s.Text("loss_d"),s.Text("transit_microseconds")};
                }).ToArray()}];
    });

    internal static object[] RecordedTables(JsonElement facts,JsonElement plan)=>Project(()=>{
        if(plan.Text("kind")!="plan")return [];
        var segments=Rows(facts,"segments").ToDictionary(s=>s.Id("id"),s=>Label(s,"segment_key"));
        var nodes=Rows(facts,"nodes").ToDictionary(n=>n.Id("id"),n=>Label(n,"node_key"));
        var accepted=plan.GetProperty("assessment");
        if(accepted.Text("implementation")!="gas_transport_route_witness_v1"||accepted.Id("revisionId")!=plan.Id("id")||!accepted.GetProperty("feasible").GetBoolean())throw Invalid();
        var prefix=Label(plan,"business_key")+" · Recorded historical evidence · ";
        string R(JsonElement row,string field)=>Exact(row.GetProperty(field));
        string At(JsonElement row,string field)=>LifecycleNative.Instant(TradingInstant.Parse(row.Text(field),"gas_transport_route_source"));
        string Contributors(JsonElement interval,string field)=>string.Join(", ",Rows(interval,field).Select(c=>segments[c.Id("segmentId")]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return [new{label=prefix+"Segment assessment",columns=new[]{"Segment","Receipt","Fuel","Loss","Expected delivery","Chosen delivery","Transit timing","Conservation"},
            rows=Rows(accepted,"segments").Select(s=>new[]{segments[s.Id("segmentId")],R(s,"receiptQuantity"),R(s,"fuelQuantity"),R(s,"lossQuantity"),R(s,"expectedDeliveryQuantity"),
                R(s,"deliveryQuantity"),s.GetProperty("timingAligned").GetBoolean()?"Aligned":"Timing mismatch",s.GetProperty("conserved").GetBoolean()?"Conserved":"Mismatch"}).ToArray()},
            new{label=prefix+"Accepted source claims",columns=new[]{"Segment","Receipt from","Receipt through","Receipt quantity","Delivery from","Delivery through","Delivery quantity"},
                rows=Rows(plan,"flows").Select(f=>{
                    var c=f.GetProperty("claim");if(c.Id("id")!=f.Id("claim_id"))throw Invalid();
                    return new[]{segments[f.Id("segment_id")],At(c,"receipt_from"),At(c,"receipt_to"),c.Text("receipt_quantity"),At(c,"delivery_from"),At(c,"delivery_to"),c.Text("delivery_quantity")};
                }).ToArray(),instantColumns=new[]{1,2,4,5}},
            new{label=prefix+"Intermediate-node assessment",columns=new[]{"Node","From","Through","Incoming / hour","Outgoing / hour","Residual / hour","Incoming segments","Outgoing segments","Conservation"},
                rows=Rows(accepted,"nodes").SelectMany(n=>Rows(n,"intervals").Select(i=>new[]{nodes[n.Id("nodeId")],At(i,"from"),At(i,"through"),R(i,"incomingPerHour"),R(i,"outgoingPerHour"),
                    R(i,"residualPerHour"),Contributors(i,"incoming"),Contributors(i,"outgoing"),i.GetProperty("conserved").GetBoolean()?"Conserved":"Mismatch"})).ToArray(),instantColumns=new[]{1,2}}];
    });

    internal static object[] Tables(GasTransportRoutePreparedAssessment? assessment,JsonElement? facts,JsonElement? command)
    {
        if(assessment is null)return [];
        try
        {
            if(facts is not {} source)throw Invalid();
            var segments=Rows(source,"segments").ToDictionary(s=>s.Id("id"),s=>Label(s,"segment_key"));
            var nodes=Rows(source,"nodes").ToDictionary(n=>n.Id("id"),n=>(Key:Label(n,"node_key"),Point:Label(n,"point_code")));
            var plans=Rows(source,"plans");
            var name=command is {} proposed?Label(proposed,"businessKey"):
                Label(plans.Single(p=>p.Id("id")==assessment.RevisionId),"business_key");
            var route=assessment.Route;
            if(route.Segments.IsDefault||route.Nodes.IsDefault||route.Segments.Any(s=>!segments.ContainsKey(s.SegmentId))||route.Nodes.Any(n=>!nodes.ContainsKey(n.NodeId)))throw Invalid();
            string Contributors(IEnumerable<OpenEtrm.MarketData.Gas.GasTransportRouteContribution> values)=>
                string.Join(", ",values.Select(c=>segments.TryGetValue(c.SegmentId,out var key)?key:throw Invalid()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            return [new{label=name+" · Segment quantities",columns=new[]{"Segment","Receipt","Fuel","Loss","Expected delivery","Chosen delivery","Transit timing","Conservation"},
                rows=route.Segments.Select(s=>new[]{segments[s.SegmentId],s.ReceiptQuantity.ToExactString(),s.FuelQuantity.ToExactString(),s.LossQuantity.ToExactString(),
                    s.ExpectedDeliveryQuantity.ToExactString(),s.DeliveryQuantity.ToExactString(),s.TimingAligned?"Aligned":"Timing mismatch",s.Conserved?"Conserved":"Mismatch"}).ToArray()},
                new{label=name+" · Delivery timing and rate",columns=new[]{"Segment","From","Through","Expected delivery / hour","Chosen delivery / hour","Residual / hour","Conservation"},
                    rows=route.Segments.SelectMany(s=>s.Intervals.Select(i=>new[]{segments[s.SegmentId],LifecycleNative.Instant(i.From),LifecycleNative.Instant(i.Through),
                        i.ExpectedPerHour.ToExactString(),i.DeliveredPerHour.ToExactString(),i.ResidualPerHour.ToExactString(),i.Conserved?"Conserved":"Mismatch"})).ToArray(),instantColumns=new[]{1,2}},
                new{label=name+" · Intermediate node balance",columns=new[]{"Node","Point","From","Through","Incoming / hour","Outgoing / hour","Residual / hour","Incoming segments","Outgoing segments","Conservation"},
                    rows=route.Nodes.SelectMany(n=>n.Intervals.Select(i=>new[]{nodes[n.NodeId].Key,nodes[n.NodeId].Point,LifecycleNative.Instant(i.From),LifecycleNative.Instant(i.Through),
                        i.IncomingPerHour.ToExactString(),i.OutgoingPerHour.ToExactString(),i.ResidualPerHour.ToExactString(),Contributors(i.Incoming),Contributors(i.Outgoing),i.Conserved?"Conserved":"Mismatch"})).ToArray(),instantColumns=new[]{2,3}}];
        }
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {throw new MarketDataException("gas_transport_route_source","Retain exact saved route, node, segment and plan references before displaying the assessment.",error);}
    }
    static JsonElement[] Rows(JsonElement value,string name)
    {var rows=value.GetProperty(name);return rows.ValueKind==JsonValueKind.Array?[..rows.EnumerateArray()]:throw Invalid();}
    static string Label(JsonElement value,string name)
    {
        var text=value.Text(name);var canonical=GasOperationText.Key(text,"gas_transport_route_source");
        return text==canonical?text:throw Invalid();
    }
    static MarketDataException Invalid()=>new("gas_transport_route_source","Retain exact saved route, node, segment and plan references before displaying the assessment.");
    static string Role(string value)=>value switch{GasTransportRouteNode.Source=>"External receipt",GasTransportRouteNode.Transit=>"Transit without storage",GasTransportRouteNode.Sink=>"External delivery",_=>throw Invalid()};
    static string Exact(JsonElement value)
    {
        var n=value.Text("n");var d=value.Text("d");
        if(n.Length is <1 or >1235||d.Length is <1 or >1234)throw Invalid();
        var numerator=BigInteger.Parse(n,CultureInfo.InvariantCulture);var denominator=BigInteger.Parse(d,CultureInfo.InvariantCulture);
        if(numerator.ToString(CultureInfo.InvariantCulture)!=n||denominator.ToString(CultureInfo.InvariantCulture)!=d||denominator<=0||
            numerator.GetBitLength()>4096||denominator.GetBitLength()>4096||BigInteger.GreatestCommonDivisor(numerator,denominator)!=1)throw Invalid();
        return d=="1"?n:n+"/"+d;
    }
    static object[] Project(Func<object[]> projection)
    {
        try{return projection();}
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {throw new MarketDataException("gas_transport_route_source","Retain the exact saved graph and accepted source evidence before displaying route history.",error);}
    }
}
