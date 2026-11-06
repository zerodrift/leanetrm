using System.Collections.Immutable;
using System.Globalization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Physical;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

public sealed record PhysicalDeliveryColumn(string Code,string Label);
public sealed record PhysicalDeliveryCapability(string Code,string Label,bool Implemented);
public sealed record PhysicalDeliveryHead(Guid Id,string Kind,string Status,string? Quantity,string Evidence);
public sealed record PhysicalDeliveryOrigin(int Slot,Guid SideId,Guid ScheduleId,string ProgramDigest,string Root,int Instruction,int Grid,int Cell,Guid? ObligationId);
public sealed record PhysicalDeliveryRow(string Key,string SideCode,string Period,DateTimeOffset Start,DateTimeOffset End,
    string Direction,ReferenceKey Location,string LocationLabel,ReferenceKey QuantitySpecification,string Unit,
    string Contractual,string Selected,string SelectedTrack,string SourceStatus,ImmutableArray<PhysicalDeliveryHead> Heads,
    ImmutableDictionary<string,string> Attributes,PhysicalDeliveryOrigin Origin);
public sealed record PhysicalDeliveryView(string Version,Guid TradeId,Guid BindingId,int Revision,string PeriodLabel,string SelectionPolicy,
    ImmutableArray<PhysicalDeliveryColumn> Tracks,ImmutableArray<PhysicalDeliveryColumn> Attributes,
    ImmutableArray<PhysicalDeliveryCapability> Capabilities,ImmutableArray<PhysicalDeliveryRow> Rows);

// Native read adapters supply a common display contract. Track selection and
// permitted commands stay with their existing commodity operations; no ledger,
// calendar, quantity policy or financial exposure is inferred by this view.
internal static class PhysicalDeliveryViews
{
    private const string Version="physical_delivery_view_v1";
    private static MarketDataException Invalid()=>new("physical_delivery_view","The physical delivery view requires complete exact booked scope, source addresses and reference labels.");
    private static PhysicalDeliveryOrigin Origin(RetainedSideProgram program,PhysicalDeliveryInterval row,Guid? obligation=null)=>
        new(program.Slot,program.SideId,program.ScheduleId,program.Identity.ContentDigest,row.Root,row.Instruction,row.Grid,row.Cell,obligation);
    private static ReferenceKey Location(RetainedSideProgram program,string port)
    {
        var keys=program.Program.Document.Instructions.SelectMany(i=>i.Bindings).Where(b=>b.Kind=="location"&&b.Port==port).Select(b=>b.Reference).Distinct().ToArray();
        return keys is [ReferenceKey key]?key:throw Invalid();
    }
    private static string Direction(string value)=>value switch{"1"=>"Receive","-1"=>"Deliver",_=>throw Invalid()};
    private static async Task<ImmutableDictionary<ReferenceKey,string>> Units(NpgsqlConnection connection,IEnumerable<ReferenceKey> specifications,Func<Task> requireAuthority,CancellationToken ct)
    {
        var result=ImmutableDictionary.CreateBuilder<ReferenceKey,string>();
        // Batch by exact catalog/revision, never choose a current unit master.
        foreach(var group in specifications.Distinct().GroupBy(k=>k.Revision))
        {
            var rows=await PreviewDatabase.Rows(connection,"""
                SELECT json_build_object('code',q.code,'unit',u.symbol)::text
                FROM ref_data.quantity_specifications q JOIN ref_data.units u
                 ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
                WHERE(q.catalog_id,q.revision_id)=($1,$2) AND q.code=ANY($3::text[])
                """,[group.Key.CatalogId,group.Key.RevisionId,group.Select(k=>k.Code).ToArray()],ct);
            foreach(var row in rows){var unit=row.Text("unit");if(string.IsNullOrWhiteSpace(unit))throw Invalid();result.Add(new(group.Key,row.Text("code")),unit);}
        }
        // A label read can wait past authority expiry and then be filtered by RLS.
        // Recheck the exact source closure before classifying missing metadata.
        await requireAuthority();
        if(result.Count!=specifications.Distinct().Count())throw Invalid();
        return result.ToImmutable();
    }
    internal static async Task<PhysicalDeliveryView> Gas(NpgsqlConnection connection,Guid trade,GasDailyQuantities.Selection selection,InputFrame frame,string policy,CancellationToken ct)
    {
        var components=await PreviewDatabase.Rows(connection,"SELECT row_to_json(m)::text FROM trading.gas_component_terms m WHERE(catalog_id,instrument_id)=($1,$2) ORDER BY slot",
            [selection.Header.Id("catalog_id"),selection.Header.Id("instrument_id")],ct);
        var units=await Units(connection,selection.Days.Select(d=>d.Contract.Quantity.Specification),
            ()=>new RetainedScheduleStore(selection.Header.Id("catalog_id")).RequireProgramAuthority(connection,selection.Header.Id("instrument_id"),frame.ContextId,frame.At,selection.Programs.Length,ct),ct);
        var rows=selection.Days.Select(d=>
        {
            var program=selection.Programs.Single(p=>p.Program.Document.Roots.Any(r=>r.Name==d.Contract.Root)&&
                p.Program.Document.Family=="gas"&&components.Any(m=>m.Text("code")==d.SideCode&&m.Text("slot")==p.Slot.ToString(CultureInfo.InvariantCulture)));
            var component=components.Single(m=>m.Text("slot")==program.Slot.ToString(CultureInfo.InvariantCulture));
            var contract=d.Contract;var selected=d.Selected;
            var head=selected.RevisionId is {} id?d.Heads.Single(h=>h.Id("id")==id):(System.Text.Json.JsonElement?)null;
            var interval=new PhysicalDeliveryInterval(contract.Root,contract.Instruction,contract.Grid,0,contract.Day,contract.Start,contract.End,contract.Quantity);
            return new PhysicalDeliveryRow(d.ObligationId+":"+contract.Day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),d.SideCode,contract.Day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),contract.Start,contract.End,
                Direction(component.Text("direction")),Location(program,"point"),d.DeliveryPoint,contract.Quantity.Specification,units[contract.Quantity.Specification],
                contract.Quantity.ToExactString(),selected.Quantity.ToExactString(),selected.Kind,head?.Text("status")??"contractual",
                [..d.Heads.Select(h=>new PhysicalDeliveryHead(h.Id("id"),h.Text("kind"),h.Text("status"),h.OptionalText("quantity"),h.Text("evidence")))],
                ImmutableDictionary<string,string>.Empty.Add("pipeline",d.Pipeline).Add("zone",d.Zone),Origin(program,interval,d.ObligationId));
        }).ToImmutableArray();
        return new(Version,trade,selection.Header.Id("instrument_id"),int.Parse(selection.Header.Text("revision"),CultureInfo.InvariantCulture),"Delivery day",policy,
            [new("nominated","Nominated"),new("confirmed","Confirmed"),new("scheduled","Scheduled"),new("allocated","Allocated")],
            [new("pipeline","Pipeline"),new("zone","Zone")],
            [new("gas_manual_operations_v1","Manual scheduling & reconciliation",true),new("pipeline_connectivity","Live pipeline connection",false)],rows);
    }
    internal static async Task<PhysicalDeliveryView> Contract(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        TradePositionFact fact,ExactDecimal quantity,string direction,string sideCode,string locationPort,string locationLabel,
        ImmutableArray<PhysicalDeliveryCapability> capabilities,CancellationToken ct)
    {
        var store=new RetainedScheduleStore(database);
        var programs=await store.LoadPrograms(connection,fact.BindingId,frame.ContextId,frame.At,ct);
        // These bounded adapters explicitly admit membership zero as physical.
        // Cash/fee siblings are never discovered by inspecting their quantities.
        var program=programs.Single(p=>p.Slot==0);
        var scale=ScheduleNumber.Parse(quantity.ToString());
        var intervals=PhysicalDeliverySchedule.ReadLiteralQuantities(program.Program,new ExactRatio(scale.Numerator,scale.Denominator));
        var units=await Units(connection,intervals.Select(r=>r.Quantity.Specification),
            ()=>store.RequireProgramAuthority(connection,fact.BindingId,frame.ContextId,frame.At,programs.Length,ct),ct);
        var rows=intervals.Select(r=>new PhysicalDeliveryRow($"{fact.TradeId}:{fact.BindingId}:{program.Slot}:{program.ScheduleId}:{r.Root}:{r.Grid}:{r.Cell}",sideCode,
            r.OperatingDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),r.Start,r.End,Direction(direction),Location(program,locationPort),locationLabel,
            r.Quantity.Specification,units[r.Quantity.Specification],r.Quantity.ToExactString(),r.Quantity.ToExactString(),"contractual","contractual",[],
            ImmutableDictionary<string,string>.Empty,Origin(program,r))).ToImmutableArray();
        return new(Version,fact.TradeId,fact.BindingId,fact.Revision,"Delivery window","contractual_schedule_v1",[],[],capabilities,rows);
    }
}
