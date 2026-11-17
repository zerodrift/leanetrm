using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;

internal static class GasTransportFinancialPositionChecks
{
    internal static void Run()
    {
        var trade=Guid.NewGuid();var binding=Guid.NewGuid();var book=Guid.NewGuid();var reference=Guid.NewGuid();var revision=Guid.NewGuid();
        var source=JsonSerializer.SerializeToNode(new{implementation="physical_gas_transport_direct_rights_v1",tradeId=trade,instrumentId=binding,
            revision=1,bookId=book,businessKey="Synthetic whole Transport",tradeReference=11,instrumentReference=22,legalEntityId=Guid.NewGuid(),
            legalEntityRevision=1,counterpartyId=Guid.NewGuid(),counterpartyRevision=1,unpricedReason="rights_mark_required",
            booking=new{trade_date="2032-03-14",booker_id=Guid.NewGuid(),booker_name="Synthetic trader",booker_login="synthetic_trader",
                executed_at="2032-03-14T08:00:00Z",implementation="native_booking_v1",label_captured_at="2032-03-15T00:00:00Z"},
            terms=new{definitionRevisionId=Guid.NewGuid(),valuationReferenceCatalogId=reference,valuationReferenceRevisionId=revision,
                packageQuantity="one_transport_package",currency="synthetic_currency",perspective="provider",from="2032-03-14T08:00:00Z",through="2032-03-15T08:00:00Z"}})!.AsObject();
        var owner=JsonSerializer.SerializeToElement(new{id=book,name="Synthetic book",portfolio_id=Guid.NewGuid(),portfolio="Synthetic portfolio"});
        var labels=JsonSerializer.SerializeToElement(new{legal_entity="Synthetic entity",counterparty="Synthetic party",unit="package",currency="SCY",
            commodity_code="synthetic_package_rights",commodity_name="Synthetic whole rights"});
        GasTransportFinancialPosition Position()=>new(JsonSerializer.SerializeToElement(source),owner,labels);
        var fact=Position().Fact();var holding=Position().Holding();
        if(holding["commodity"] is not JsonObject commodity||commodity["code"]?.GetValue<string>()!="synthetic_package_rights"||
            commodity["name"]?.GetValue<string>()!="Synthetic whole rights")
            throw new Exception("A Transport holding must supply its exact valuation package commodity to the shared desk and exports.");
        if(fact.TradeId!=trade||fact.BindingId!=binding||fact.BookedQuantity!="1"||fact.BookedUnit!="package"||
            holding["quantity"]!.GetValue<string>()!="1"||holding["priceNavigation"]!.GetValue<bool>()||
            holding["sideSchedules"]!.GetValue<bool>()||holding["mark"] is not null||holding["presentValue"] is not null||
            holding["unavailableReason"]!.GetValue<string>()!="rights_mark_required")
            throw new Exception("An unpriced whole Transport holding was resized, given a gas mark or presented as complete value.");
        if(holding["quantitySpecification"]!["revision"]!["revisionId"]!.GetValue<Guid>()!=revision)
            throw new Exception("The holding lost its exact independent valuation package reference.");
        source["terms"]!["from"]=null;source["terms"]!["through"]=null;source["unpricedReason"]="financial_state_future";
        var future=Position().Fact();var futureHolding=Position().Holding();
        if(future.DeliveryFrom is not null||future.DeliveryTo is not null||futureHolding["deliveryFrom"] is not null||futureHolding["deliveryTo"] is not null)
            throw new Exception("An unavailable financial state resurrected an obsolete service horizon.");
        Console.WriteLine("PASS financial Transport holding: one whole package, independent reference, explicit unpriced status and no gas-price or obsolete-horizon substitution");
    }
}
