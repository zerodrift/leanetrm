using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasTransportFinancialLabelChecks
{
    internal static void Run()
    {
        var values=new Dictionary<string,string>{["profile"]="physical_gas_transport_direct_rights_v1",["perspective"]="provider",
            ["rights_valuation_policy"]="signed_current_whole_remaining_rights_pv_excluding_fees_and_owned_gas_v1",
            ["fee_profile"]="unconditional_one_off_fixed_amount_at_booking_v1",["fee_direction"]="pay",
            ["expiry_policy"]="fixed_window_capacity_rights_expire_at_through_v1",["entitlement"]=Guid.NewGuid().ToString(),
            ["capacity_version"]=Guid.NewGuid().ToString(),["valuation_definition"]=Guid.NewGuid().ToString(),
            ["valuation_reference_bundle"]=Guid.NewGuid()+"@"+Guid.NewGuid(),["package_quantity"]="one_whole_package",["currency"]="synthetic_currency",
            ["book"]=Guid.NewGuid().ToString(),["legal_entity"]=Guid.NewGuid()+"@1",["counterparty"]=Guid.NewGuid()+"@2"};
        var draft=new TicketDraft(GasTransportFinancialTicket.Product,1,values.ToImmutableDictionary(),[]);
        var labels=values.Keys.Where(k=>GasTransportFinancialTicket.StaticChoices(k) is null).ToDictionary(k=>k,k=>"Retained "+k);
        var result=GasTransportFinancialTicketStore.SelectedChoices(draft,labels);
        var expected=GasTransportFinancialTicket.Description.TradeFields.Where(f=>f.Choices is not null).Select(f=>f.Id).ToHashSet();
        if(!expected.SetEquals(result.Keys)||result.Any(p=>p.Value.Length!=1||p.Value[0].Value!=values[p.Key]||string.IsNullOrWhiteSpace(p.Value[0].Label))||
            result["fee_direction"][0].Label!="Pay"||result["perspective"][0].Label!="Provider")
            throw new Exception("Retained labels must attest each exact selected field once, without new-entry defaults or role-based fee changes.");
        labels.Remove("entitlement");Reject(()=>GasTransportFinancialTicketStore.SelectedChoices(draft,labels));
        labels["entitlement"]="Retained entitlement";
        Reject(()=>GasTransportFinancialTicketStore.SelectedChoices(draft with{Trade=draft.Trade.SetItem("fee_profile","unsupported")},labels));
        Reject(()=>GasTransportFinancialTicketStore.SelectedChoices(draft with{Trade=draft.Trade.Remove("capacity_version")},labels));
        Console.WriteLine("PASS financial Transport retained labels: complete exact saved selections, independent provider fee direction and explicit missing-label refusal");
    }
    static void Reject(Action action)
    {try{action();}catch(MarketDataException error)when(error.Code=="gas_transport_financial_source"){return;}throw new Exception("Expected retained financial label refusal.");}
}
