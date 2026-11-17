using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

public sealed partial class GasTransportRouteStore
{
    // The caller captures operation authority before binding this exact source.
    // Native choices expose compatible immutable paths and current complete
    // claims; no capacity, quantity, membership or financial fact is created.
    private async Task<GasTransportRouteChoices> Choices(NpgsqlConnection c,JsonElement? facts,ScheduleContext context,CancellationToken ct)
    {
        var value=await Native(c,"SELECT security.gas_transport_route_choices($1,$2)::text",ct,Catalog,
            facts is {} source?source.GetProperty("route").Id("id"):DBNull.Value);
        if(!value.TryGetProperty("sourceCapacities",out var rows)||rows.ValueKind!=JsonValueKind.Array)throw ChoiceSource();
        if(rows.GetArrayLength()>5000)throw new MarketDataException("gas_transport_route_scope","This route setup has more than 5,000 source entitlements. A qualified searched source path is required; no choices were silently omitted.");
        var at=UserTime.EndOfDay(DateWorkspace.Date(context.EffectiveOn),"UTC");
        foreach(var capacity in rows.EnumerateArray())
            if(context.KnownAt is null||capacity.Id("contextId")!=context.ContextId||TradingInstant.Parse(capacity.Text("asOf"),"gas_transport_route_context")!=at||
                TradingInstant.Parse(capacity.Text("knownAt"),"gas_transport_route_context")!=TradingInstant.Parse(context.KnownAt,"gas_transport_route_context"))throw ChoiceSource();
        return new([..rows.EnumerateArray().Select(row=>row.Clone())]);
    }
    private static MarketDataException ChoiceSource()=>new("gas_transport_route_source","Retain the complete route source-choice envelope under the exact bound business and knowledge context.");
}
