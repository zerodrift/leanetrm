using System.Collections.Immutable;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Commands;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasLocationStart(FuturesReadRequest Context,string Agreements,string PurchasePrice,string SalePrice);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasLocationLeg(TicketDraft Draft,Guid DefinitionRevisionId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasLocationBooking(FuturesReadRequest Context,Guid OperationId,string Agreements,string PurchasePrice,string SalePrice,ImmutableArray<GasLocationLeg> Tickets);

internal static class GasLocationEntry
{
    private static MarketDataException Invalid()=>new("gas_location_input","Choose the confirmation structure and the fixed or indexed payment for each of two distinct gas locations.");

    internal static ImmutableArray<TicketDraft> Start(string agreements,string purchasePrice,string salePrice)
    {
        var empty=ImmutableDictionary<string,string>.Empty;
        TicketSideDraft[] Endpoint(string code,string physical,string payment,string price)
        {
            var kind=price switch{"fixed"=>"cash","indexed"=>"floating",_=>throw Invalid()};
            return [new("physical",code,empty.Add("direction",physical)),
                new(kind,code+"_payment",empty.Add(kind+"_direction",payment).Add(kind+"_source",code))];
        }
        TicketDraft Draft(IEnumerable<TicketSideDraft> sides)=>new(GasTicket.Description.Product,GasTicket.Description.Version,empty,[..sides]);
        var purchase=Endpoint("purchase","receive","pay",purchasePrice);
        var sale=Endpoint("sale","deliver","receive",salePrice);
        return agreements switch{
            "one_agreement"=>[Draft(purchase.Concat(sale))],
            "separate_confirmations"=>[Draft(purchase),Draft(sale)],
            _=>throw Invalid()};
    }

    internal static ImmutableArray<GasCompositionRequest> Terms(GasLocationBooking input)
    {
        var expected=Start(input.Agreements,input.PurchasePrice,input.SalePrice);
        if(input.OperationId==Guid.Empty||input.Tickets.IsDefault||input.Tickets.Length!=expected.Length||
            input.Context is not {KnowledgeMode:"current",Convention:PhysicalGasStore.Convention,ContextId:null})throw Invalid();
        var result=ImmutableArray.CreateBuilder<GasCompositionRequest>();
        for(var i=0;i<expected.Length;i++)
        {
            var leg=input.Tickets[i];
            if(leg?.Draft is not { } draft||leg.DefinitionRevisionId==Guid.Empty||draft.Sides.IsDefault||draft.Sides.Length!=expected[i].Sides.Length)throw Invalid();
            foreach(var side in expected[i].Sides)
            {
                var found=draft.Sides.FirstOrDefault(s=>s?.Code==side.Code&&s.Kind==side.Kind);
                if(found?.Values is null||side.Values.Any(v=>found.Values.GetValueOrDefault(v.Key)!=v.Value))throw Invalid();
            }
            var child=OperationIdentity.For("gas_location_booking_child_v1",input.OperationId,i);
            result.Add(GasTicket.Request(draft,input.Context.SnapshotId,"current",child,leg.DefinitionRevisionId));
        }
        var points=result.SelectMany(r=>r.PhysicalSides).ToArray();
        if((points[0].Pipeline,points[0].Zone,points[0].DeliveryPoint)==(points[1].Pipeline,points[1].Zone,points[1].DeliveryPoint))throw Invalid();
        if(input.Tickets.Any(t=>t.DefinitionRevisionId!=input.Tickets[0].DefinitionRevisionId))throw Invalid();
        return result.ToImmutable();
    }
}
