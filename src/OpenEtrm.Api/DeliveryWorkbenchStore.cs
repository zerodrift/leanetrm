using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Physical;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeliveryWorkbenchReadRequest([property:JsonRequired]FacilityQuantityContext Context,Guid ScopeId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeliveryWorkbenchWriteRequest([property:JsonRequired]FacilityQuantityContext Context,[property:JsonRequired]string OwnerId,
    Guid OperationId,[property:JsonRequired]string EffectiveAt,[property:JsonRequired]JsonElement Command);

// A common presentation boundary over native operating owners. Only the gas
// adapter owns new facts: typed relationships from route terminals to booked
// occurrences. The crude adapter consumes its existing receipt/title ledger.
public sealed partial class DeliveryWorkbenchStore(PreviewDatabase database)
{
    public const string GasProfile="gas_transport_delivery_v1",CrudeProfile="crude_receipt_delivery_v1";
    private Guid Catalog=>database.Manifest.CatalogId;
    private readonly OperationalBusinessContext contexts=new(database);
    private static readonly ImmutableDictionary<string,FacilityOperationForm> Empty=ImmutableDictionary<string,FacilityOperationForm>.Empty;
    private static string Profile(string path)=>path switch{"gas-transport"=>GasProfile,"crude-receipt"=>CrudeProfile,_=>throw DeliveryWorkbenchCommands.Invalid()};
    private static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,GasOwnedOperations.Json);
    private Task<JsonElement> Native(NpgsqlConnection c,string sql,CancellationToken ct,params object[] args)=>LifecycleNative.Read(c,sql,ct,args);
    private async Task Capture(NpgsqlConnection c,bool gas,bool write,CancellationToken ct)
    {
        if(gas)
        {
            await using var q=ScheduleStore.Command(c,"SELECT security.gas_transport_route_authority($1,$2)",Catalog,write?"plan":DBNull.Value);
            var until=new DateTimeOffset((DateTime)(await q.ExecuteScalarAsync(ct))!);
            database.RegisterCommitAdmission(c,new GasTransportRouteAdmission(Catalog,write?"plan":null,until));
        }
        else
        {
            var until=await LifecycleNative.Authority(c,Catalog,"ReadTrades",ct);
            database.RegisterCommitAdmission(c,new LifecycleReadAdmission(Catalog,until));
        }
    }
    private async Task<string> Owner(NpgsqlConnection c,bool gas,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,gas?"SELECT security.gas_transport_route_owner($1)":
            "SELECT security.delivery_workbench_crude_owner($1)",Catalog);
        return (string)(await q.ExecuteScalarAsync(ct))!;
    }
    private async Task RequireOwner(NpgsqlConnection c,string expected,bool gas,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(expected)||expected!=await Owner(c,gas,ct))
            throw new MarketDataException("delivery_workbench_owner_changed","Return to the original account to reconcile its delivery operation.");
    }
    private async Task<(ScheduleContext Context,DateTimeOffset At,bool Working)> Bind(NpgsqlConnection c,FacilityQuantityContext request,ScheduleContext? selected,CancellationToken ct)
    {
        var scope=await contexts.Resolve(c,request,selected,"delivery_workbench_context",ct);await contexts.BindResolved(c,scope,ct);return scope;
    }
    public async Task<object> Directory(NpgsqlConnection c,FacilityDirectoryRequest request,ScheduleContext? selected,string path,CancellationToken ct)
    {
        var profile=Profile(path);var gas=profile==GasProfile;await Capture(c,gas,false,ct);var scope=await Bind(c,request.Context,selected,ct);
        object scopes;
        if(gas)scopes=await Native(c,"SELECT security.gas_transport_route_directory($1)::text",ct,Catalog);
        else
        {
            var positions=await CrudePositions.Read(c,database.Manifest,[],[],ct);if(positions.Length>1000)throw new MarketDataException("delivery_workbench_limit","Use a qualified searched scope before selecting from more than 1,000 crude positions.");
            scopes=positions.Where(p=>p.Head.Text("direction")=="1").Select(p=>new{id=p.Trade,label=TradingReference.Trade(p.Head)+" · "+p.Physical.Text("grade_name")+" · "+p.Physical.Text("custody_point_name"),revision=int.Parse(p.Head.Text("revision"),System.Globalization.CultureInfo.InvariantCulture)}).ToArray();
        }
        return new{profile,ownerId=await Owner(c,gas,ct),context=scope.Context,at=scope.At,historical=!scope.Working,scopes,hasMore=false,
            forms=Empty,configuration=Empty,canRecord=false,canConfigure=false,overviewSections=Array.Empty<object>(),tables=Array.Empty<object>()};
    }
    public async Task<object> Read(NpgsqlConnection c,DeliveryWorkbenchReadRequest request,ScheduleContext? selected,string path,CancellationToken ct)
    {
        var profile=Profile(path);if(request.ScopeId==Guid.Empty)throw DeliveryWorkbenchCommands.Invalid();var gas=profile==GasProfile;
        await Capture(c,gas,false,ct);var scope=await Bind(c,request.Context,selected,ct);
        return gas?await GasRead(c,request.ScopeId,scope,ct):await CrudeRead(c,request.ScopeId,scope,ct);
    }
    private async Task<object> CrudeRead(NpgsqlConnection c,Guid trade,(ScheduleContext Context,DateTimeOffset At,bool Working) scope,CancellationToken ct)
    {
        var positions=await CrudePositions.Read(c,database.Manifest,[],[],ct,trade:trade);
        var position=positions.SingleOrDefault()??throw new MarketDataException("delivery_workbench_source","Retain the exact visible purchase trade.");
        if(position.Head.Text("direction")!="1")throw new MarketDataException("delivery_workbench_profile","Select a purchase receipt trade for the transfer delivery profile.");
        var facts=await Native(c,"SELECT security.read_crude_lifecycle($1,$2)::text",ct,Catalog,trade);
        var assessment=CrudeLifecycleStore.DeliveryAssessment(facts,scope.At);
        return new{profile=CrudeProfile,ownerId=await Owner(c,false,ct),context=scope.Context,at=scope.At,historical=!scope.Working,scopeId=trade,
            forms=Empty,configuration=Empty,canRecord=false,canConfigure=false,assessment,assessmentQuantityLabel=position.Physical.Text("unit")+" · certified standard volume",
            nativeActions=new[]{new{label="Open receipt, dispatch and correction operations",tradeId=trade,bindingId=position.Binding,revision=int.Parse(position.Head.Text("revision"),System.Globalization.CultureInfo.InvariantCulture),instrumentType="physical_crude",tab="delivery"}},
            overviewSections=new[]{new{label="Retained crude transfer delivery",values=new[]{new{label="Trade",value=TradingReference.Trade(position.Head)},new{label="Custody",value=position.Physical.Text("custody_point_name")},new{label="Evidence",value="Accepted receipts, native inventory reservations, dispatches and correction history retain their existing title and cash owners."}}}},
            tables=new object[]{new{label="Receipt evidence and revisions",columns=new[]{"Receipt","Effective at","Quantity","Disposition","Previous receipt","Evidence"},instantColumns=new[]{1},
                rows=facts.GetProperty("receipts").EnumerateArray().Select(r=>new[]{r.Id("id").ToString(),r.Text("effective_at"),Number(r,"quantity"),r.Text("disposition"),r.OptionalId("previous_id")?.ToString()??"None",r.Text("evidence")}).ToArray()},
                new{label="Dispatch evidence",columns=new[]{"Dispatch","Effective at","Quantity","Source receipt","Reservation","Evidence"},instantColumns=new[]{1},
                    rows=facts.GetProperty("dispatches").EnumerateArray().Select(r=>new[]{r.Id("id").ToString(),r.Text("effective_at"),Number(r,"quantity"),r.Id("receipt_id").ToString(),r.OptionalId("allocation_id")?.ToString()??"Direct disposition",r.Text("evidence")}).ToArray()}}};
    }
    private static string Number(JsonElement row,string field)=>row.GetProperty(field).ValueKind==JsonValueKind.String?row.Text(field):row.GetProperty(field).GetRawText();
    private static JsonElement[] Rows(JsonElement value,string field)=>[..value.GetProperty(field).EnumerateArray()];
    private static DateTimeOffset At(JsonElement value,string field)=>TradingInstant.Parse(value.Text(field),"delivery_workbench_source");
    private static GasPipelineDeliveryInput GasInput(JsonElement facts,JsonElement plan,GasTransportRoutePreparedAssessment assessment)
    {
        var profile=GasTransportRouteAssessments.Profile(facts);
        ExactQuantity Q(JsonElement claim,string field)=>ExactQuantity.FromQuantity(new(ExactDecimal.Parse(claim.Text(field)),profile.Quantity));
        return new(profile,[..Rows(plan,"flows").Select(flow=>{
            var claim=flow.GetProperty("claim");return new GasTransportRouteFlow(flow.Id("id"),flow.Id("segment_id"),At(claim,"receipt_from"),At(claim,"receipt_to"),Q(claim,"receipt_quantity"),At(claim,"delivery_from"),At(claim,"delivery_to"),Q(claim,"delivery_quantity"));
        })],[..assessment.Capacities.Select(c=>c.Assessment)]);
    }
    private async Task<string> GasUnit(NpgsqlConnection c,JsonElement facts,CancellationToken ct)
    {
        var route=facts.GetProperty("route");await using var q=ScheduleStore.Command(c,"""
            SELECT u.symbol||' · '||q.basis_code FROM ref_data.quantity_specifications q
            JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
            WHERE(q.catalog_id,q.revision_id,q.code)=($1,$2,$3)
            """,route.Id("reference_catalog_id"),route.Id("reference_revision_id"),route.Text("quantity_code"));
        return (string?)await q.ExecuteScalarAsync(ct)??throw new MarketDataException("delivery_workbench_source","Retain the exact native quantity unit and basis.");
    }
    public static string Refusal(string code)=>code switch{
        "delivery_workbench_stale"=>"A retained route, capacity, claim, allocation or target trade head changed. Refresh and review its exact current scope.",
        "delivery_workbench_conflict"=>"This operation or allocation head has different terms. Reconcile the original request or review a new revision.",
        "delivery_workbench_geometry"=>"This profile requires terminal claim windows to equal the complete booked native interval. Split or prorated fulfillment is not supported.",
        "delivery_workbench_target"=>"Select compatible booked occurrences with the same owner, point, direction and exact quantity specification.",
        "delivery_workbench_conservation"=>"The complete replacement assignments exceed a terminal source or booked target quantity.",
        "delivery_workbench_denied"=>"Current authority is required over the exact source route and every retained target.",
        _=>"Supply the complete explicit delivery profile, source, target revisions, quantities and evidence."};
}
