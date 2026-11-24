using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public static class GasTransportFinancialTicket
{
    public const string Product="physical_gas_transport";
    public const string Measure="transport_remaining_rights_and_fee_present_value_v1";
    static TicketField Field(string id,string label,string meaning,string type="choice",string? choices=null,params string[] depends)=>
        new(id,label,meaning,type,null,"editable",true,null,type=="choice"?choices??id:null,[..depends],null,null,null);
    public static TicketDescription Description{get;}=ServiceTicketFieldGroups.Transport(new(Product,1,"flow",
    [
        Field("profile","Contract profile","Select the supported whole-rights contract explicitly."),
        Field("business_key","Agreement reference","Your stable business reference for this agreement.","text"),
        Field("entitlement","Operating entitlement","The exact retained commercial capacity entitlement.",depends:["book","legal_entity","counterparty"]),
        Field("capacity_version","Capacity version","The explicit operating definition covered by this financial package.",depends:["entitlement","book","legal_entity","counterparty"]),
        Field("perspective","Our perspective","Our customer or provider role; this does not reverse the signed rights value or fee."),
        Field("valuation_definition","Valuation definition","The exact published definition for whole-package rights marks."),
        Field("valuation_reference_bundle","Valuation reference bundle","The exact references belonging to the selected valuation definition; independent of the operating entitlement.",depends:["valuation_definition"]),
        Field("package_quantity","Whole-package basis","The reference basis for one whole remaining-rights package; no numeric resizing.",depends:["valuation_definition","valuation_reference_bundle"]),
        Field("currency","Currency","The exact currency for the rights package and independent fee.",depends:["valuation_definition","valuation_reference_bundle","package_quantity"]),
        Field("rights_valuation_policy","Rights valuation","A signed current value for one whole rights package, excluding independent fees and owned gas."),
        Field("fee_profile","Fee terms","The independently owed fixed fee; no flow or use is inferred."),
        Field("fixed_fee_amount","Fixed fee","Absolute nonnegative fee, with its direction selected separately.","decimal"),
        Field("fee_direction","Fee direction","Whether our party pays or receives the independently stated fee."),
        Field("fee_due_at","Fee due","An explicit due time for a nonzero fee, or Not applicable for an explicitly zero fee.","datetime") with{AllowNotApplicable=true},
        Field("expiry_policy","Rights expiry","Rights close through an explicit expiry action at the capacity window end; the fee remains independent."),
        Field("evidence","Agreement evidence","The legal agreement or supporting evidence reference.","text"),
        Field("book","Book","The book owning this whole rights package.",choices:"books"),
        Field("legal_entity","Legal entity","Our exact legal entity revision.",choices:"legal_entities"),
        Field("counterparty","Counterparty","The other exact legal party revision.",choices:"counterparties")
    ],[],[],[new("present_value","Remaining Transport rights and fee PV",null,"money",[])],["term"],[],["new","booked"]));
    public static TicketDescription Describe(TicketDraft? draft)
    {
        if(draft is not null)
        {
            Identity(draft);
            if(draft.Trade.TryGetValue("profile",out var profile)&&profile!=GasTransportFinancialProfile.Version)
                throw Invalid("profile","Select the supported Transport profile explicitly.");
        }
        return Description;
    }
    public static ImmutableArray<TicketChoice>? StaticChoices(string field)=>field switch
    {
        "profile"=>[new(GasTransportFinancialProfile.Version,"One whole rights package with independent fixed fee")],
        "perspective"=>[new("customer","Customer"),new("provider","Provider")],
        "rights_valuation_policy"=>[new(GasTransportDirectRightsPolicy.Version,"Signed current whole-rights PV; fees and owned gas separate")],
        "fee_profile"=>[new(GasTransportFinancialProfile.Fee,"One fixed fee owed from booking, independent of use")],
        "fee_direction"=>[new("pay","Pay"),new("receive","Receive")],
        "expiry_policy"=>[new(GasTransportDirectRightsPolicy.ExpiryAtThrough,"Explicit expiry at the capacity window end")],
        _=>null
    };
    static void Identity(TicketDraft draft)
    {
        if(draft is null||draft.Product!=Product||draft.Version!=1||draft.Trade is null||draft.Sides.IsDefault||!draft.Sides.IsEmpty||draft.ExerciseRights is {Length:>0})
            throw Invalid("profile","Supply the installed Transport ticket and its explicit terms.");
    }
    static Guid Id(string value,string field)=>Guid.TryParse(value,out var id)&&id!=Guid.Empty?id:throw Invalid(field,"Select an exact identity.");
    static (Guid Id,int Revision) Party(string value,string field)
    {
        var parts=value.Split('@');
        return parts.Length==2&&Guid.TryParse(parts[0],out var id)&&id!=Guid.Empty&&int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var revision)&&revision>0?
            (id,revision):throw Invalid(field,"Select the exact party and revision.");
    }
    static string Required(TicketDraft draft,string field)=>draft.Trade.TryGetValue(field,out var value)&&!string.IsNullOrEmpty(value)?value:throw Invalid(field,"Select this term explicitly.");
    public static CatalogueSource ValuationSource(TicketDraft draft)
    {
        Identity(draft);var definition=Id(Required(draft,"valuation_definition"),"valuation_definition");
        var bundle=Required(draft,"valuation_reference_bundle").Split('@');
        if(bundle.Length!=2)throw Invalid("valuation_reference_bundle","Select the exact valuation reference catalog and revision.");
        return new(definition,Id(bundle[0],"valuation_reference_bundle"),Id(bundle[1],"valuation_reference_bundle"));
    }
    sealed record Parsed(ImmutableDictionary<string,string> Values,CatalogueSource Source,Guid Book,(Guid Id,int Revision) Entity,(Guid Id,int Revision) Party,ExactDecimal Fee,DateTimeOffset? FeeDue,string BusinessKey,string Evidence);
    static Parsed Parse(TicketDraft draft)
    {
        Identity(draft);var v=draft.Trade;
        foreach(var pair in v)
            if(!Description.TradeFields.Any(field=>field.Id==pair.Key))throw Invalid(pair.Key,"The selected Transport profile has no such editable field.");
        foreach(var field in Description.TradeFields)
        {
            var value=Required(draft,field.Id);
            if(StaticChoices(field.Id) is {} choices&&!choices.Any(choice=>choice.Value==value))throw Invalid(field.Id,$"Select a supported {field.Label.ToLowerInvariant()}.");
        }
        var source=ValuationSource(draft);_=Id(v["entitlement"],"entitlement");_=Id(v["capacity_version"],"capacity_version");
        foreach(var field in new[]{"package_quantity","currency"})
            if(string.IsNullOrWhiteSpace(v[field])||v[field]!=v[field].Trim()||v[field].Any(char.IsControl))throw Invalid(field,"Select an exact reference code.");
        ExactDecimal fee;
        try{fee=ExactDecimal.Parse(v["fixed_fee_amount"]);}catch(MarketDataException error){throw Invalid("fixed_fee_amount",error.Message);}
        if(fee.CompareTo(default)<0)throw Invalid("fixed_fee_amount","Enter an absolute nonnegative fee and choose its direction separately.");
        DateTimeOffset? due=null;
        if(v["fee_due_at"]!="none")
            try{due=TradingInstant.Parse(v["fee_due_at"],"gas_transport_financial_input");}catch(MarketDataException error){throw Invalid("fee_due_at",error.Message);}
        if((fee==default)!=(due is null))throw Invalid("fee_due_at","Select an explicit due time for a nonzero fee, or Not applicable for zero.");
        string Text(string field,bool evidence)
        {
            try{return evidence?GasOperationText.Evidence(v[field],"gas_transport_financial_input"):GasOperationText.Key(v[field],"gas_transport_financial_input");}
            catch(MarketDataException error){throw Invalid(field,error.Message);}
        }
        return new(v,source,Id(v["book"],"book"),Party(v["legal_entity"],"legal_entity"),Party(v["counterparty"],"counterparty"),fee,due,Text("business_key",false),Text("evidence",true));
    }
    public static GasTransportFinancialBooking Request(TicketDraft draft,Guid snapshot,Guid context,string knowledgeMode,Guid operation,Guid definition)
    {
        var p=Parse(draft);var v=p.Values;
        if(definition!=p.Source.DefinitionRevisionId)throw Invalid("valuation_definition","Retain the exact explicitly selected valuation definition.");
        if(snapshot==Guid.Empty||context==Guid.Empty||knowledgeMode!="current")throw Invalid("profile","Use an explicit current certified financial source for Transport entry.");
        return new(operation,snapshot,context,definition,knowledgeMode,v["profile"],p.BusinessKey,p.Book,p.Entity.Id,p.Entity.Revision,p.Party.Id,p.Party.Revision,
            p.Source.ReferenceCatalogId,p.Source.ReferenceRevisionId,Id(v["entitlement"],"entitlement"),Id(v["capacity_version"],"capacity_version"),v["perspective"],v["package_quantity"],v["currency"],
            v["rights_valuation_policy"],v["fee_profile"],p.Fee.ToString(),v["fee_direction"],p.FeeDue,v["expiry_policy"],p.Evidence);
    }
    public static TicketDraft Reopen(JsonElement read)
    {
        if(read.Text("implementation")!=GasTransportFinancialProfile.Version)throw Invalid("profile","Retain the installed financial Transport profile.");
        if(!read.TryGetProperty("financialState",out var state)||state.ValueKind!=JsonValueKind.Object)
            throw Invalid("capacity_version","The selected financial state is unavailable in this view; no older capacity version is substituted.");
        var terms=read.GetProperty("terms");
        if(!terms.TryGetProperty("feeDueAt",out var due)||due.ValueKind is not(JsonValueKind.String or JsonValueKind.Null))throw Invalid("fee_due_at","The retained agreement must identify an exact fee due time or no date.");
        var values=new Dictionary<string,string>{["profile"]=terms.Text("profile"),["business_key"]=read.Text("businessKey"),["evidence"]=read.Text("evidence"),
            ["entitlement"]=terms.Id("entitlementId").ToString(),["capacity_version"]=state.Id("capacityVersionId").ToString(),["valuation_definition"]=terms.Id("definitionRevisionId").ToString(),
            ["valuation_reference_bundle"]=$"{terms.Id("valuationReferenceCatalogId")}@{terms.Id("valuationReferenceRevisionId")}",["book"]=read.Id("bookId").ToString(),
            ["legal_entity"]=$"{read.Id("legalEntityId")}@{read.GetProperty("legalEntityRevision").GetInt32()}",["counterparty"]=$"{read.Id("counterpartyId")}@{read.GetProperty("counterpartyRevision").GetInt32()}",
            ["fee_due_at"]=due.ValueKind==JsonValueKind.Null?"none":due.GetString()!};
        foreach(var(field,member)in new[]{("perspective","perspective"),("package_quantity","packageQuantity"),("currency","currency"),("rights_valuation_policy","rightsValuationPolicy"),
            ("fee_profile","feeProfile"),("fixed_fee_amount","fixedFeeAmount"),("fee_direction","feeDirection"),("expiry_policy","expiryPolicy")})values[field]=terms.Text(member);
        var draft=new TicketDraft(Product,1,values.ToImmutableDictionary(),[]);_=Parse(draft);return draft;
    }
    public static TicketFieldException Invalid(string field,string message)=>new("gas_transport_financial_input",message,null,field);
}
