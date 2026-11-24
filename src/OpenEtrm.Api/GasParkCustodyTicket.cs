using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public static class GasParkCustodyTicket
{
    public const string Product="physical_gas_park_custody";
    public const string TicketProduct=GasParkLoanTicket.Product;
    public static bool IsSelected(TicketDraft? draft)=>draft is not null&&draft.Product==TicketProduct&&draft.Trade is not null&&draft.Trade.GetValueOrDefault("profile")==GasParkCustodyTerms.Version;
    public const string Measure="custody_remaining_plan_present_value_v1";
    public const string NoAccount="none";
    static TicketField Field(string id,string label,string meaning,string type="choice",string? choices=null,params string[] depends)=>
        new(id,label,meaning,type,null,"editable",true,null,type=="choice"?choices??id:null,[..depends],null,null,null);
    public static TicketDescription Description{get;}=Build(false);
    static readonly TicketDescription SelectedDescription=Build(true);
    static TicketDescription Build(bool selected)=>ServiceTicketFieldGroups.ParkCustody(new TicketDescription(TicketProduct,1,"flow",
    [
        Field("profile","Contract profile","Select the qualified custody agreement explicitly.") with{RefreshDescription=true},
        Field("service_kind","Service","The supported retained-title service."),
        Field("perspective","Our perspective","Our customer or custody-provider role."),
        Field("reference_bundle","Reference bundle","One exact published reference catalog and revision."),
        Field("point","Custody point","The exact point at which custody is accepted and returned.",depends:["reference_bundle"]),
        Field("quantity_specification","Quantity specification","The explicit native quantity basis in the selected reference bundle.",depends:["reference_bundle","point"]),
        Field("currency","Fee currency","The independently selected exact currency.",depends:["reference_bundle"]),
        Field("owned_account","Owned inventory account","Customer's exact compatible inventory account, or explicitly Not applicable for the provider.",
            depends:["perspective","reference_bundle","point","quantity_specification","currency","book","legal_entity","counterparty"]),
        Field("custody_quantity","Custody quantity","Positive quantity covered by this custody agreement.","decimal"),
        Field("park_at","Earliest custody acceptance","Earliest accepted custody under the selected plan.","datetime"),
        Field("return_at","Custody return due","Due time for return from custody; passing it does not record a return.","datetime"),
        Field("title_policy","Title treatment","The customer's title is retained throughout custody."),
        Field("return_policy","Return quantity rule","Partial returns at the same point and quantity basis."),
        Field("return_timing","Return timing","Returns follow accepted custody."),
        Field("return_order","Return order","Accepted custody returns retain the selected chronological order."),
        Field("use_policy","Use and sale","The custody provider cannot use or sell the customer's gas."),
        Field("loss_policy","Loss application","Loss application is unqualified and requires reconciliation; no liability amount is assumed."),
        Field("physical_policy","Physical activity","Custody creates no inferred pipeline or facility flow."),
        Field("plan_policy","Unaccepted custody plan","Before return due, the selected plan starts at the later of earliest acceptance or evaluation."),
        ..selected?new[]{
            Field("fixed_fee_amount","Fixed fee","Absolute fee independent of custody quantity; its direction is selected separately.","decimal"),
            Field("fee_due_at","Fee due","Due no earlier than earliest custody acceptance, or explicitly Not applicable for a zero fee.","datetime") with{AllowNotApplicable=true},
            Field("fee_recognition","Fee determination","The fixed fee becomes independent cash only on accepted custody.")}:[],
        Field("fee_direction","Fee direction","Whether our party pays or receives the independently stated fee."),
        Field("book","Book","The book owning the custody agreement.",choices:"books"),
        Field("legal_entity","Legal entity","Our exact legal entity revision.",choices:"legal_entities"),
        Field("counterparty","Counterparty","The other exact legal party revision.",choices:"counterparties")
    ],[],[],[new("custody","Custody","quantity","quantity",[]),new("present_value","Remaining custody service PV",null,"money",[])],["term"],[],["new","booked"])
    {Selection=selected?ImmutableDictionary<string,string>.Empty.Add("profile",GasParkCustodyTerms.Version):ImmutableDictionary<string,string>.Empty});

    public static TicketDescription Describe(TicketDraft? draft)
    {
        if(draft is not null&&(draft.Product!=TicketProduct||draft.Version!=1||draft.Trade is null||draft.Sides.IsDefault||!draft.Sides.IsEmpty||draft.ExerciseRights is {Length:>0}))
            throw Invalid("profile","Supply the installed custody ticket.");
        return draft?.Trade.GetValueOrDefault("profile") switch
        {
            null=>Description,
            GasParkCustodyTerms.Version=>SelectedDescription,
            _=>throw Invalid("profile","Select the supported custody profile explicitly.")
        };
    }
    public static ImmutableArray<TicketChoice>? StaticChoices(string field)=>field switch
    {
        "profile"=>GasParkLoanTicket.StaticChoices("profile"),
        "service_kind"=>[new("park","Park")],
        "perspective"=>[new("customer","Customer"),new("provider","Provider")],
        "title_policy"=>[new(GasParkCustodyTerms.RetainTitle,"Customer retains title")],
        "return_policy"=>[new(GasParkCustodyTerms.SameBasis,"Same point and basis; partial custody returns")],
        "return_timing"=>[new(GasParkCustodyTerms.ReturnsAfterAcceptance,"After accepted custody")],
        "return_order"=>[new(GasParkCustodyTerms.NondecreasingReturns,"Nondecreasing accepted return times")],
        "use_policy"=>[new(GasParkCustodyTerms.NoUse,"No use or sale of custody gas")],
        "loss_policy"=>[new(GasParkCustodyTerms.LossUnqualified,"Loss application unqualified; reconciliation required")],
        "physical_policy"=>[new(GasParkCustodyTerms.SeparatePhysical,"Custody only; no inferred physical flow")],
        "plan_policy"=>[new(GasParkCustodyTerms.UnacceptedPlan,"Later of earliest acceptance or evaluation, before return due")],
        "fee_direction"=>[new("pay","Pay"),new("receive","Receive")],
        "fee_recognition"=>[new(GasParkCustodyTerms.FeeAtAcceptance,"Independent fixed-fee cash on accepted custody")],
        _=>null
    };
    sealed record Parsed(GasParkCustodyTerms Terms,ExactDecimal Quantity,Guid? Account,Guid Book,Guid Entity,int EntityRevision,Guid Party,int PartyRevision,string FeeDirection);
    static Parsed Parse(TicketDraft draft)
    {
        if(draft is null||draft.Product!=TicketProduct||draft.Version!=1||draft.Trade is null||draft.Sides.IsDefault||!draft.Sides.IsEmpty||draft.ExerciseRights is {Length:>0})
            throw Invalid("profile","Supply the installed custody ticket and its explicit terms.");
        var v=draft.Trade;
        if(!v.TryGetValue("profile",out var profile)||string.IsNullOrWhiteSpace(profile))throw Invalid("profile","Select a custody profile explicitly.");
        var description=Describe(draft);
        foreach(var pair in v)
        {
            if(!description.TradeFields.Any(field=>field.Id==pair.Key))throw Invalid(pair.Key,"The custody profile has no such editable field.");
            if(string.IsNullOrWhiteSpace(pair.Value))throw Invalid(pair.Key,"Select an explicit value.");
        }
        foreach(var field in description.TradeFields)
        {
            if(!v.ContainsKey(field.Id))throw Invalid(field.Id,$"Select {field.Label.ToLowerInvariant()} explicitly.");
            if(StaticChoices(field.Id) is {} choices&&!choices.Any(choice=>choice.Value==v[field.Id]))throw Invalid(field.Id,$"Select a supported {field.Label.ToLowerInvariant()}.");
        }
        var bundle=v["reference_bundle"].Split('@');
        if(bundle.Length!=2||!Guid.TryParse(bundle[0],out var catalog)||catalog==Guid.Empty||!Guid.TryParse(bundle[1],out var revision)||revision==Guid.Empty)
            throw Invalid("reference_bundle","Select one exact reference catalog and revision.");
        var reference=new ReferenceRevision(catalog,revision);
        var quantity=Number("custody_quantity");if(quantity.CompareTo(default)<=0)throw Invalid("custody_quantity","Custody quantity must be positive in the selected basis.");
        Guid? account=v["owned_account"]==NoAccount?null:Id("owned_account");
        if((v["perspective"]=="provider")!=(account is null))throw Invalid("owned_account",v["perspective"]=="provider"?"Select Not applicable for provider custody.":"Select the customer's exact compatible owned inventory account.");
        var park=Instant("park_at");var due=Instant("return_at");if(due<=park)throw Invalid("return_at","Custody return due must follow earliest acceptance.");
        var fee=Number("fixed_fee_amount");if(fee.CompareTo(default)<0)throw Invalid("fixed_fee_amount","Enter an absolute nonnegative fee and choose its direction separately.");
        DateTimeOffset? feeDue=v["fee_due_at"]=="none"?null:Instant("fee_due_at");
        if((fee==default)!=(feeDue is null))throw Invalid("fee_due_at","Select a due time for a nonzero fee, or Not applicable for a zero fee.");
        if(feeDue is {} feeAt&&feeAt<park)throw Invalid("fee_due_at","Fee due cannot precede earliest accepted custody.");
        GasParkCustodyTerms terms;
        try{terms=new(profile,v["service_kind"],v["perspective"],v["title_policy"],v["return_policy"],v["return_timing"],v["return_order"],v["use_policy"],v["loss_policy"],v["physical_policy"],v["plan_policy"],
            new(reference,v["point"]),new(reference,v["quantity_specification"]),new(reference,v["currency"]),park,due,new(fee,new(reference,v["currency"])),
            ExactDecimal.Parse(v["fee_direction"]=="pay"?"-1":"1"),feeDue,v["fee_recognition"]);}
        catch(MarketDataException error){throw Invalid("profile",error.Message);}
        var (entity,entityRevision)=Party("legal_entity");var (party,partyRevision)=Party("counterparty");
        return new(terms,quantity,account,Id("book"),entity,entityRevision,party,partyRevision,v["fee_direction"]);
        Guid Id(string field)=>Guid.TryParse(v[field],out var value)&&value!=Guid.Empty?value:throw Invalid(field,"Select an exact identity.");
        (Guid,int) Party(string field)
        {
            var parts=v[field].Split('@');
            return parts.Length==2&&Guid.TryParse(parts[0],out var id)&&id!=Guid.Empty&&int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var value)&&value>0?
                (id,value):throw Invalid(field,"Select an exact party and revision.");
        }
        ExactDecimal Number(string field){try{return ExactDecimal.Parse(v[field]);}catch(MarketDataException error){throw Invalid(field,error.Message);}}
        DateTimeOffset Instant(string field){try{return TradingInstant.Parse(v[field],"gas_custody_input");}catch(MarketDataException error){throw Invalid(field,error.Message);}}
    }
    public static GasParkCustodyBooking Request(TicketDraft draft,Guid snapshot,Guid context,string knowledgeMode,Guid operation,Guid definition)
    {
        var p=Parse(draft);var t=p.Terms;
        if(snapshot==Guid.Empty||context==Guid.Empty||definition==Guid.Empty||knowledgeMode!="current")throw Invalid("profile","Use an explicit current certified source context for custody entry.");
        return new(operation,snapshot,context,definition,PhysicalGasStore.Convention,knowledgeMode,t.Implementation,p.Book,p.Entity,p.EntityRevision,p.Party,p.PartyRevision,
            t.Point.Revision.CatalogId,t.Point.Revision.RevisionId,t.Point.Code,t.Quantity.Code,t.Currency.Code,p.Account,t.Service,t.Perspective,p.Quantity.ToString(),t.ParkAt,t.ReturnAt,
            t.TitlePolicy,t.ReturnPolicy,t.ReturnTiming,t.ReturnOrder,t.UsePolicy,t.LossPolicy,t.PhysicalPolicy,t.PlanPolicy,t.FixedFeeAmount.ToString(),p.FeeDirection,t.FeeDueAt,t.FeeRecognition);
    }
    public static TicketDraft Reopen(JsonElement read)
    {
        if(!read.TryGetProperty("implementation",out var profile)||profile.ValueKind!=JsonValueKind.String||profile.GetString()!=GasParkCustodyTerms.Version)
            throw Invalid("profile","The retained agreement must identify its installed custody profile.");
        var t=read.GetProperty("terms");
        if(!read.TryGetProperty("ownedAccountId",out var account)||account.ValueKind is not(JsonValueKind.String or JsonValueKind.Null))throw Invalid("owned_account","The retained agreement must explicitly identify its owned account or no account.");
        if(!t.TryGetProperty("feeDueAt",out var feeDue)||feeDue.ValueKind is not(JsonValueKind.String or JsonValueKind.Null))throw Invalid("fee_due_at","The retained agreement must explicitly identify its fee due time or no date.");
        var values=new Dictionary<string,string>{["profile"]=profile.GetString()!,["reference_bundle"]=$"{t.Id("referenceCatalogId")}@{t.Id("referenceRevisionId")}",
            ["book"]=read.Id("bookId").ToString(),["legal_entity"]=$"{read.Id("legalEntityId")}@{read.GetProperty("legalEntityRevision").GetInt32()}",
            ["counterparty"]=$"{read.Id("counterpartyId")}@{read.GetProperty("counterpartyRevision").GetInt32()}",
            ["owned_account"]=account.ValueKind==JsonValueKind.Null?NoAccount:account.GetString()!,["custody_quantity"]=read.Text("quantity"),
            ["quantity_specification"]=t.Text("quantity"),["fee_due_at"]=feeDue.ValueKind==JsonValueKind.Null?"none":feeDue.GetString()!};
        foreach(var (field,member) in new[]{("service_kind","serviceKind"),("perspective","perspective"),("point","point"),("currency","currency"),("park_at","parkAt"),("return_at","returnAt"),
            ("title_policy","titlePolicy"),("return_policy","returnPolicy"),("return_timing","returnTiming"),("return_order","returnOrder"),("use_policy","usePolicy"),("loss_policy","lossPolicy"),
            ("physical_policy","physicalPolicy"),("plan_policy","planPolicy"),("fixed_fee_amount","fixedFeeAmount"),("fee_direction","feeDirection"),("fee_recognition","feeRecognition")})values[field]=t.Text(member);
        var draft=new TicketDraft(TicketProduct,1,values.ToImmutableDictionary(),[]);_=Parse(draft);return draft;
    }
    public static TicketFieldException Invalid(string field,string message)=>new("gas_custody_input",message,null,field);
}
