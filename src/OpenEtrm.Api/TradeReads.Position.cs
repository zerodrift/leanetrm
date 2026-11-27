using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TradePositionReadRequest([property:JsonRequired] FuturesReadRequest Context,
    [property:JsonRequired] Guid TradeId,[property:JsonRequired] Guid BindingId,
    [property:JsonRequired] int Revision,[property:JsonRequired] string InstrumentType);

// Identifies one binding of the trade revision visible under the selected context.
// Selection precedes schedule loading and valuation, including stale-revision refusal.
internal sealed record TradePositionSelection(Guid TradeId,Guid BindingId,int Revision,string InstrumentType)
{
    internal static TradePositionSelection From(TradePositionReadRequest request)
    {
        if(request.Context is null||request.TradeId==Guid.Empty||request.BindingId==Guid.Empty||request.Revision is <1 or >256||
            string.IsNullOrWhiteSpace(request.InstrumentType)||request.InstrumentType.Length>64||request.InstrumentType.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c=='_')))
            throw new MarketDataException("position_request","Select an exact trade, binding, revision and supported instrument type.");
        return new(request.TradeId,request.BindingId,request.Revision,request.InstrumentType);
    }
    internal void Require(IEnumerable<JsonElement> rows)
    {
        var matching=rows.Where(r=>r.Id("trade_id")==TradeId&&r.Id("binding_id")==BindingId).ToArray();
        if(matching.Length!=1)throw Unavailable();
        if(matching[0].Number("revision").ToString()!=Revision.ToString(CultureInfo.InvariantCulture))
            throw new MarketDataException("position_revision","The selected context does not contain the requested trade revision. Refresh its position facts or select its historical context.");
    }
    internal static MarketDataException Unavailable()=>new("position_unavailable","The requested position is unavailable under this context and current authority.");
}

public sealed partial class TradeReads
{
    public Task<object> LngTrade(NpgsqlConnection connection,TradingView view,Guid trade,int? revision,CancellationToken ct)=>
        ReadBound(connection,view,async()=>
        {
            var positions=await LngPositions.Read(connection,database.Manifest,[],[],ct,trade:trade);
            if(positions.Length!=1)throw TradePositionSelection.Unavailable();
            var position=positions[0];
            if(revision is {} requested&&requested!=int.Parse(position.Head.Text("revision"),CultureInfo.InvariantCulture))
                throw new MarketDataException("position_revision","The selected context does not contain the requested LNG revision.");
            return await LngTerms(connection,view,position,ct);
        },ct);
    public Task<object> LngTrade(NpgsqlConnection connection,TradingView view,TradePositionReadRequest request,CancellationToken ct)
    {
        var selected=TradePositionSelection.From(request);
        if(selected.InstrumentType!=LngPositions.Type)throw TradePositionSelection.Unavailable();
        return ReadBound(connection,view,async()=>
        {
            var position=(await LngPositions.Read(connection,database.Manifest,[],[],ct,selected)).Single();
            return await LngTerms(connection,view,position,ct);
        },ct);
    }
    // A read of one position needs current trading authority before any frame or trade is selected, so that a principal without it is
    // denied rather than told the position is unavailable.
    private async Task RequireReadTrades(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var permission=ScheduleStore.Command(connection,"SELECT security.trading_access($1,'ReadTrades')",database.Manifest.CatalogId);
        if(!Equals(await permission.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
    }

    public Task<object> CrudeTrade(NpgsqlConnection connection,TradingView view,TradePositionReadRequest request,CancellationToken ct)
    {
        var selected=TradePositionSelection.From(request);
        if(selected.InstrumentType!=CrudePositions.Type)throw TradePositionSelection.Unavailable();
        return ReadBound(connection,view,async()=>
        {
            var position=(await CrudePositions.Read(connection,database.Manifest,[],[],ct,selected)).Single();
            return await CrudeTerms(connection,view,position,ct);
        },ct);
    }
    public Task<object> CrudeTrade(NpgsqlConnection connection,TradingView view,Guid trade,int? revision,CancellationToken ct)=>
        ReadBound(connection,view,async()=>
        {
            var positions=await CrudePositions.Read(connection,database.Manifest,[],[],ct,trade:trade);
            if(positions.Length!=1)throw TradePositionSelection.Unavailable();
            var position=positions[0];
            if(revision is {} requested&&requested!=int.Parse(position.Head.Text("revision"),CultureInfo.InvariantCulture))
                throw new MarketDataException("position_revision","The selected context does not contain the requested crude revision.");
            return await CrudeTerms(connection,view,position,ct);
        },ct);
    Task<PhysicalDeliveryView> CrudeDelivery(NpgsqlConnection connection,TradingView view,CrudePosition position,CancellationToken ct)=>
        PhysicalDeliveryViews.Contract(connection,database,view.CalculationFrame,position.Fact(),position.TotalQuantity,position.Head.Text("direction"),
            "delivery","custody",position.Physical.Text("custody_point_name"),[new("crude_delivery_cash_v1","Delivery & cash",true)],ct);
    Task<PhysicalDeliveryView> LngDelivery(NpgsqlConnection connection,TradingView view,LngPosition position,CancellationToken ct)=>
        PhysicalDeliveryViews.Contract(connection,database,view.CalculationFrame,position.Fact(),position.TotalQuantity,position.Head.Text("direction"),
            "cargo","lng_custody",position.Physical.Text("discharge_custody_name"),[new("lng_actuals","Cargo actuals & reconciliation",false)],ct);
    static string CrudeProfileName(CrudePosition position)=>PhysicalCrudeStore.ProfileName(
        position.Head.TryGetProperty("request",out var request)&&request.TryGetProperty("profile",out var profile)?profile.GetString():null);
    async Task<object> CrudeTerms(NpgsqlConnection connection,TradingView view,CrudePosition position,CancellationToken ct)
    {
        var delivery=await CrudeDelivery(connection,view,position,ct);
        var programs=await new RetainedScheduleStore(database).LoadPrograms(connection,position.Binding,view.CalculationFrame.ContextId,view.CalculationFrame.At,ct);
        return new{head=position.Head,physical=position.Physical,profileName=CrudeProfileName(position),delivery,
            sideGroups=new[]{RetainedInstrumentSidePresentation.Crude(position,programs)}};
    }
    async Task<object> LngTerms(NpgsqlConnection connection,TradingView view,LngPosition position,CancellationToken ct)
    {
        var delivery=await LngDelivery(connection,view,position,ct);
        var programs=await new RetainedScheduleStore(database).LoadPrograms(connection,position.Binding,view.CalculationFrame.ContextId,view.CalculationFrame.At,ct);
        return new{head=position.Head,physical=position.Physical,delivery,sideGroups=new[]{RetainedInstrumentSidePresentation.Lng(position,programs)}};
    }
    public Task<object> GasTrade(NpgsqlConnection connection,TradingView view,TradePositionReadRequest request,CancellationToken ct)
    {
        var selected=TradePositionSelection.From(request);
        if(selected.InstrumentType!=GasPositions.Type)throw TradePositionSelection.Unavailable();
        return ReadBound(connection,view,async()=>
        {
            var position=(await GasPositions.Read(connection,database.Manifest,[],[],ct,selected)).Single();
            return await GasTerms(connection,view,position,ct);
        },ct);
    }
    public Task<object> GasTrade(NpgsqlConnection connection,TradingView view,Guid trade,int? revision,CancellationToken ct)=>
        ReadBound(connection,view,async()=>
        {
            var positions=await GasPositions.Read(connection,database.Manifest,[],[],ct,trade:trade);
            if(positions.Length!=1)throw TradePositionSelection.Unavailable();
            var position=positions[0];
            if(revision is {} requested&&requested!=int.Parse(position.Head.Text("revision"),CultureInfo.InvariantCulture))
                throw new MarketDataException("position_revision","The selected context does not contain the requested gas revision.");
            return await GasTerms(connection,view,position,ct);
        },ct);
    async Task<object> GasTerms(NpgsqlConnection connection,TradingView view,GasPosition position,CancellationToken ct)
    {
        var components=await PreviewDatabase.Rows(connection,"SELECT row_to_json(m)::text FROM trading.gas_component_terms m WHERE(catalog_id,instrument_id)=($1,$2) ORDER BY slot",[database.Manifest.CatalogId,position.Binding],ct);
        var indexedCash=await PreviewDatabase.Rows(connection,"SELECT (to_jsonb(i)||jsonb_build_object('spread',trim_scale(i.spread)::text))::text FROM trading.gas_index_cash_terms i WHERE(catalog_id,instrument_id)=($1,$2) ORDER BY slot",[database.Manifest.CatalogId,position.Binding],ct);
        if(components.Any(m=>m.Text("role")=="indexed_consideration"&&!indexedCash.Any(i=>i.Text("slot")==m.Text("slot"))))
            throw new MarketDataException("position_facts","The indexed gas component requires its exact retained pricing and payment terms.");
        var composed=position.Head.GetProperty("composition").GetBoolean();
        var facilityAllocations=composed
            ?await new FacilityQuantityStore(database).TradeAllocations(connection,position.Head.Id("trade_id"),ct)
            :JsonSerializer.SerializeToElement(Array.Empty<object>());
        await using var rights=ScheduleStore.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM trading.gas_exercise_rights WHERE(catalog_id,instrument_id)=($1,$2))",
            database.Manifest.CatalogId,position.Binding);
        var hasExerciseRights=composed&&await rights.ExecuteScalarAsync(ct) is true;
        await using var swingRights=ScheduleStore.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM trading.gas_swing_rights WHERE(catalog_id,instrument_id)=($1,$2))",database.Manifest.CatalogId,position.Binding);
        var hasSwingRights=composed&&await swingRights.ExecuteScalarAsync(ct) is true;
        var history=await PreviewDatabase.Rows(connection,"SELECT row_to_json(x)::text FROM(SELECT r.revision,r.instrument_id,r.actor_id,r.effective_at,a.reason FROM trading.gas_trade_revisions r LEFT JOIN trading.gas_amendment_commands a ON(a.catalog_id,a.operation_id,a.changed)=(r.catalog_id,r.operation_id,true) WHERE(r.catalog_id,r.trade_id)=($1,$2) ORDER BY r.revision DESC)x",[database.Manifest.CatalogId,position.Trade],ct);
        var amend=await GasAmendments.Action(connection,database.Manifest.CatalogId,position.Trade,position.Binding,int.Parse(position.Head.Text("revision"),CultureInfo.InvariantCulture),view.CalculationFrame.At,view.Working,ct);
        var programs=await new RetainedScheduleStore(database).LoadPrograms(connection,position.Binding,view.CalculationFrame.ContextId,view.CalculationFrame.At,ct);
        // The trade's location booking is found from the trade's own revisions, by either of the booking's two operations.
        // Filtering the joined purchase and sale revisions by trade instead leaves the planner to pair every gas revision
        // with every trade reference and to look a revision up under row security for each pair.
        var confirmations=await PreviewDatabase.Rows(connection,"""
            WITH own AS MATERIALIZED(SELECT catalog_id,operation_id FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=($1,$2)),
            booked AS MATERIALIZED(
             SELECT b.catalog_id,b.agreements,b.purchase_operation,b.sale_operation
             FROM own JOIN trading.gas_location_bookings b ON(b.catalog_id,b.purchase_operation)=(own.catalog_id,own.operation_id)
             UNION
             SELECT b.catalog_id,b.agreements,b.purchase_operation,b.sale_operation
             FROM own JOIN trading.gas_location_bookings b ON(b.catalog_id,b.sale_operation)=(own.catalog_id,own.operation_id))
            SELECT row_to_json(x)::text FROM(
             SELECT b.agreements,p.trade_id AS purchase_id,s.trade_id AS sale_id,pr.reference_number AS purchase_reference,sr.reference_number AS sale_reference
             FROM booked b
             JOIN trading.gas_trade_revisions p ON(p.catalog_id,p.operation_id)=(b.catalog_id,b.purchase_operation)
             JOIN trading.gas_trade_revisions s ON(s.catalog_id,s.operation_id)=(b.catalog_id,b.sale_operation)
             JOIN trading.trade_references pr ON(pr.catalog_id,pr.trade_id)=(p.catalog_id,p.trade_id)
             JOIN trading.trade_references sr ON(sr.catalog_id,sr.trade_id)=(s.catalog_id,s.trade_id)
            ) x
            """,[database.Manifest.CatalogId,position.Trade],ct);
        var terms=new{head=position.Head,physical=position.Physical,components,indexedCash,facilityAllocations,history,
            confirmations=confirmations.Select(r=>new{agreements=r.Text("agreements"),purchaseTradeId=r.Id("purchase_id"),saleTradeId=r.Id("sale_id"),
                purchaseReference=TradingReference.Trade(int.Parse(r.Text("purchase_reference"),CultureInfo.InvariantCulture)),
                saleReference=TradingReference.Trade(int.Parse(r.Text("sale_reference"),CultureInfo.InvariantCulture))}),
            amend,sideGroups=new[]{RetainedInstrumentSidePresentation.Gas(position,programs,components)}};
        if(!hasExerciseRights&&!hasSwingRights)return terms;
        var exercise=JsonSerializer.SerializeToNode(terms,RiskJobs.Json)!.AsObject();
        if(hasExerciseRights)exercise["hasExerciseRights"]=true;
        if(hasSwingRights)exercise["hasSwingRights"]=true;
        return exercise;
    }
    public Task<object> Position(NpgsqlConnection connection,TradingView view,TradePositionReadRequest request,CancellationToken ct)
    {
        var selected=TradePositionSelection.From(request);
        return ReadBound(connection,view,async()=>
        {
            await RequireReadTrades(connection,ct);
            var data=await database.Read(connection,ct,observationIds:view.Frame.ObservationIds,
                referenceScope:ReferenceReadScope.TradeValuation,observationScope:ObservationReadScope.ExactPins);
            return new{holding=await blotter.Position(connection,data,view.CalculationFrame,selected,ct),
                curveNames=await new MarketReads(database).BoundCurveNames(connection,ct)};
        },ct);
    }
}
