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
            request.InstrumentType is not("future" or "financial_swap" or "heat_rate" or "ppa" or "physical_gas" or "physical_crude"))
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
            return new{head=position.Head,physical=position.Physical};
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
            return new{head=position.Head,physical=position.Physical};
        },ct);
    public Task<object> GasTrade(NpgsqlConnection connection,TradingView view,TradePositionReadRequest request,CancellationToken ct)
    {
        var selected=TradePositionSelection.From(request);
        if(selected.InstrumentType!=GasPositions.Type)throw TradePositionSelection.Unavailable();
        return ReadBound(connection,view,async()=>
        {
            var position=(await GasPositions.Read(connection,database.Manifest,[],[],ct,selected)).Single();
            return await GasTerms(connection,position,ct);
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
            return await GasTerms(connection,position,ct);
        },ct);
    async Task<object> GasTerms(NpgsqlConnection connection,GasPosition position,CancellationToken ct)
    {
        var components=await PreviewDatabase.Rows(connection,"SELECT row_to_json(m)::text FROM trading.gas_component_terms m WHERE(catalog_id,instrument_id)=($1,$2) ORDER BY slot",[database.Manifest.CatalogId,position.Binding],ct);
        return new{head=position.Head,physical=position.Physical,components,amend=new{allowed=false,reason=GasTicketStore.AmendReason}};
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
