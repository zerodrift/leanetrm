using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasParkLoanBooking(Guid OperationId,Guid SnapshotId,Guid ContextId,Guid DefinitionRevisionId,
    string Convention,string KnowledgeMode,string Profile,Guid BookId,Guid LegalEntityId,int LegalEntityRevision,
    Guid CounterpartyId,int CounterpartyRevision,Guid OwnedAccountId,[property:JsonRequired]Guid? ClaimAccountId,
    string ServiceKind,string Perspective,string Quantity,DateTimeOffset AdvanceAt,DateTimeOffset ReturnAt,
    string TitlePolicy,string ReturnPolicy,string ReturnTiming,string Carrying,string FixedFeeAmount,string FeeDirection,
    [property:JsonRequired]DateTimeOffset? FeeDueAt,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]GasParkLoanBalanceFeeBooking? BalanceFee=null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasParkLoanBalanceFeeBooking(string RateAmount,string Basis,string TimeBasis,string StartPolicy,
    string EndPolicy,string OverduePolicy,string ProjectionPolicy,string UnadvancedPolicy,string DeterminationPolicy,
    DateTimeOffset AccrualThroughAt,string RoundingIncrement,string RoundingTieRule,string RoundingStage);

public static class GasParkLoanTicket
{
    public const string Product="physical_gas_park_loan";
    public const string Measure="scheduled_plan_present_value_v1";
    public const string NoAccount="none";
    static TicketField Field(string id,string label,string meaning,string type="choice",string? choices=null,params string[] depends)=>
        new(id,label,meaning,type,null,"editable",true,null,type=="choice"?choices??id:null,[..depends],null,null,null);
    public static TicketDescription Description{get;}=Build([]);
    static readonly TicketDescription FixedDescription=Build([
        Field("fixed_fee_amount","Fixed fee","Absolute fee independent of principal quantity. Fee cash is determined on accepted advance; payment before that event is not supported.","decimal"),
        Field("fee_due_at","Fee due","Due no earlier than planned advance, or explicitly not applicable when the fee is zero.","datetime") with{AllowNotApplicable=true}
    ],GasParkAndLoanTerms.Version);
    static readonly TicketDescription BalanceDescription=Build([
        Field("fee_rate_amount","Fee rate","Absolute currency amount per native principal quantity per elapsed hour; direction is selected independently.","decimal"),
        Field("fee_basis","Fee balance basis","Accepted principal still outstanding after accepted returns."),
        Field("fee_time_basis","Fee elapsed time","Actual elapsed UTC hours, without a calendar-day substitution."),
        Field("fee_start_policy","Fee accrual start","Actual accrual begins only at the accepted advance."),
        Field("fee_end_policy","Fee accrual end","The explicitly dated cutoff ends fee accrual, not the principal obligation."),
        Field("fee_overdue_policy","Overdue fee treatment","Outstanding principal continues accruing through the selected cutoff after return due."),
        Field("fee_projection_policy","Remaining-principal plan","For remaining value, assume the outstanding principal returns at the fee cutoff; contractual return due stays unchanged."),
        Field("fee_unadvanced_policy","Unadvanced fee plan","Before principal return due, the unadvanced plan starts at the later of earliest advance or evaluation time."),
        Field("fee_determination_policy","Fee determination","Each explicitly reviewed fee determination records the difference between rounded cumulative fee totals; accepted returns partition the balance."),
        Field("fee_accrual_through_at","Fee accrual cutoff","Explicit end of fee accrual, no earlier than contractual principal return due.","datetime"),
        Field("fee_rounding_increment","Fee rounding increment","Positive currency increment for the cumulative payment total.","decimal"),
        Field("fee_rounding_tie_rule","Fee rounding ties","Explicit treatment of exact half-increment ties."),
        Field("fee_rounding_stage","Fee rounding stage","Round cumulative payment totals, preserving exact elapsed-balance arithmetic before that boundary."),
        Field("fee_due_at","Fee due","Explicit payment due time at or after the fee accrual cutoff, including a zero-rate agreement.","datetime")
    ],GasParkLoanBalanceFeeTerms.Version);
    static TicketDescription Build(ImmutableArray<TicketField> feeFields,string? profile=null)=>TicketDescriptions.Validate(new TicketDescription(Product,1,"flow",
    [
        Field("profile","Contract profile","The installed title-transfer agreement and its qualified financial behavior.") with{RefreshDescription=true},
        Field("service_kind","Service","The commercial service being booked."),
        Field("perspective","Our perspective","Our customer or provider role under the selected service."),
        Field("owned_account","Inventory account","Exact title account defining point, quantity specification, currency and mark.",depends:["book","legal_entity","counterparty"]),
        Field("claim_account","Return claim account","An exact compatible operator account for a principal giver; explicitly not applicable for a receiver.",depends:["owned_account","service_kind","perspective","counterparty"]),
        Field("principal_quantity","Principal quantity","Absolute quantity advanced and owed back in the selected account's exact basis.","decimal"),
        Field("advance_at","Advance time","Earliest accepted title-transfer advance under this profile.","datetime"),
        Field("return_at","Return due","Due time for the principal; passing it does not settle the obligation.","datetime"),
        Field("title_policy","Title treatment","Explicit ownership treatment at accepted advance."),
        Field("return_policy","Return quantity rule","Equal quantity at the same point and basis, with partial returns."),
        Field("return_timing","Return timing","Returns follow the accepted advance; late performance remains recorded."),
        Field("carrying","Principal carrying","Explicit nominal carrying under the one-exact-mark profile."),
        ..feeFields,
        Field("fee_direction","Fee direction","Whether our party pays or receives the independently stated fee."),
        Field("book","Book","The book owning the agreement.",choices:"books"),
        Field("legal_entity","Legal entity","Our exact legal entity revision.",choices:"legal_entities"),
        Field("counterparty","Counterparty","The other exact legal party revision.",choices:"counterparties")
    ],[],[],[new("principal","Principal","quantity","quantity",[]),new("present_value","Remaining plan PV",null,"money",[])],["term"],[],["new","booked"])
    {Selection=profile is null?ImmutableDictionary<string,string>.Empty:ImmutableDictionary<string,string>.Empty.Add("profile",profile)});

    public static TicketDescription Describe(TicketDraft? draft)
    {
        if(GasParkCustodyTicket.IsSelected(draft))return GasParkCustodyTicket.Describe(draft);
        if(draft is not null&&(draft.Product!=Product||draft.Version!=1||draft.Trade is null||draft.Sides.IsDefault||!draft.Sides.IsEmpty))
            throw Invalid("profile","Supply the installed Park and Loan ticket.");
        return draft?.Trade.GetValueOrDefault("profile") switch
        {
            null=>Description,
            GasParkAndLoanTerms.Version=>FixedDescription,
            GasParkLoanBalanceFeeTerms.Version=>BalanceDescription,
            _=>throw Invalid("profile","Select a supported agreement profile explicitly.")
        };
    }

    public static ImmutableArray<TicketChoice>? StaticChoices(string field)=>field switch
    {
        "profile"=>[new(GasParkAndLoanTerms.Version,"Title transfer; fixed fee determined on advance"),new(GasParkLoanBalanceFeeTerms.Version,"Title transfer; outstanding principal × elapsed-hour fee"),new(GasParkCustodyTerms.Version,"Park custody; customer retains title and fee is independent")],
        "service_kind"=>[new("park","Park"),new("loan","Loan")],
        "perspective"=>[new("customer","Customer"),new("provider","Provider")],
        "title_policy"=>[new(GasCommodityLoan.TitleTransfer,"Transfer title on accepted advance")],
        "return_policy"=>[new(GasParkAndLoanTerms.EqualQuantity,"Equal quantity at the same point and basis")],
        "return_timing"=>[new(GasParkAndLoanTerms.ReturnsAfterAdvance,"After accepted advance; overdue principal remains owed")],
        "carrying"=>[new("nominal_mark_v1","Nominal principal at the exact saved mark")],
        "fee_direction"=>[new("pay","Pay"),new("receive","Receive")],
        "fee_basis"=>[new(GasParkLoanBalanceFeeTerms.AcceptedOutstanding,"Accepted outstanding principal")],
        "fee_time_basis"=>[new(GasParkLoanBalanceFeeTerms.ElapsedHour,"Elapsed UTC hours")],
        "fee_start_policy"=>[new(GasParkLoanBalanceFeeTerms.AcceptedAdvance,"Accepted advance")],
        "fee_end_policy"=>[new(GasParkLoanBalanceFeeTerms.ContractualCutoff,"Contractual fee cutoff")],
        "fee_overdue_policy"=>[new(GasParkLoanBalanceFeeTerms.AccrueOverdue,"Accrue outstanding principal until cutoff")],
        "fee_projection_policy"=>[new(GasParkLoanBalanceFeeTerms.ReturnAtCutoff,"Remaining principal returned at fee cutoff")],
        "fee_unadvanced_policy"=>[new(GasParkLoanBalanceFeeTerms.UnadvancedAtEvaluation,"Later of earliest advance or evaluation time, before return due")],
        "fee_determination_policy"=>[new(GasParkLoanBalanceFeeTerms.CumulativeDifference,"Rounded cumulative total differences")],
        "fee_rounding_tie_rule"=>[new("to_even","To even"),new("away_from_zero","Away from zero")],
        "fee_rounding_stage"=>[new("payment_total","Cumulative payment total")],
        _=>null
    };

    public static GasParkLoanBooking Request(TicketDraft draft,Guid snapshot,Guid context,string knowledgeMode,Guid operation,Guid definition)
    {
        if(draft is null||draft.Product!=Product||draft.Version!=1||draft.Trade is null||draft.Sides.IsDefault||!draft.Sides.IsEmpty)
            throw Invalid("profile","Supply the installed Park and Loan ticket and its explicit agreement terms.");
        var v=draft.Trade;
        if(!v.TryGetValue("profile",out var selectedProfile)||string.IsNullOrWhiteSpace(selectedProfile))throw Invalid("profile","Select an agreement profile explicitly.");
        var description=Describe(draft);
        foreach(var pair in v)
        {
            if(!description.TradeFields.Any(f=>f.Id==pair.Key))throw Invalid(pair.Key,"The selected agreement profile has no such editable field.");
            if(string.IsNullOrWhiteSpace(pair.Value))throw Invalid(pair.Key,"Select an explicit value.");
        }
        foreach(var field in description.TradeFields)
        {
            if(!v.ContainsKey(field.Id))throw Invalid(field.Id,$"Select {field.Label.ToLowerInvariant()} explicitly.");
            if(StaticChoices(field.Id) is {} choices&&!choices.Any(c=>c.Value==v[field.Id]))
                throw Invalid(field.Id,$"Select a supported {field.Label.ToLowerInvariant()}.");
        }
        if(snapshot==Guid.Empty||context==Guid.Empty||definition==Guid.Empty||knowledgeMode!="current")
            throw Invalid("profile","Book or preview under an explicit current certified source context.");
        var (entity,entityRevision)=Party("legal_entity");var (party,partyRevision)=Party("counterparty");
        var quantity=Number("principal_quantity");
        if(quantity.CompareTo(default)<=0)throw Invalid("principal_quantity","Principal must be positive in the selected exact quantity basis.");
        var advance=Instant("advance_at");var due=Instant("return_at");
        if(due<=advance)throw Invalid("return_at","Return due must follow the advance time.");
        var receiver=v["service_kind"]=="loan"&&v["perspective"]=="customer"||v["service_kind"]=="park"&&v["perspective"]=="provider";
        Guid? claim=v["claim_account"]==NoAccount?null:Id("claim_account");
        if(receiver!=(claim is null))throw Invalid("claim_account",receiver?"Select Not applicable for a principal receiver.":"A principal giver requires an exact return claim account.");
        ExactDecimal fee=default;DateTimeOffset? feeDue;GasParkLoanBalanceFeeBooking? balanceFee=null;
        if(v["profile"]==GasParkAndLoanTerms.Version)
        {
            fee=Number("fixed_fee_amount");
            if(fee.CompareTo(default)<0)throw Invalid("fixed_fee_amount","Enter the absolute fee and select its direction separately.");
            feeDue=v["fee_due_at"]=="none"?null:Instant("fee_due_at");
            if((fee==default)!=(feeDue is null))throw Invalid("fee_due_at","Select a due time for a nonzero fee, or Not applicable for a zero fee.");
            if(feeDue is {} feeAt&&feeAt<advance)throw Invalid("fee_due_at","This profile determines fee cash on accepted advance; choose a due time no earlier than planned advance.");
        }
        else
        {
            var rate=Number("fee_rate_amount");var increment=Number("fee_rounding_increment");
            if(rate.CompareTo(default)<0)throw Invalid("fee_rate_amount","Enter an absolute nonnegative fee rate and select direction separately.");
            if(increment.CompareTo(default)<=0)throw Invalid("fee_rounding_increment","Select a positive currency rounding increment.");
            var cutoff=Instant("fee_accrual_through_at");var feeAt=Instant("fee_due_at");feeDue=feeAt;
            if(cutoff<due)throw Invalid("fee_accrual_through_at","Fee accrual cutoff must be no earlier than contractual principal return due.");
            if(feeAt<cutoff)throw Invalid("fee_due_at","Fee due must be no earlier than the explicit accrual cutoff.");
            balanceFee=new(rate.ToString(),v["fee_basis"],v["fee_time_basis"],v["fee_start_policy"],v["fee_end_policy"],v["fee_overdue_policy"],v["fee_projection_policy"],v["fee_unadvanced_policy"],v["fee_determination_policy"],cutoff,increment.ToString(),v["fee_rounding_tie_rule"],v["fee_rounding_stage"]);
        }
        return new(operation,snapshot,context,definition,PhysicalGasStore.Convention,knowledgeMode,v["profile"],Id("book"),entity,entityRevision,party,partyRevision,
            Id("owned_account"),claim,v["service_kind"],v["perspective"],quantity.ToString(),advance,due,v["title_policy"],v["return_policy"],v["return_timing"],v["carrying"],
            (v["fee_direction"]=="pay"?-fee:fee).ToString(),v["fee_direction"],feeDue,balanceFee);
        Guid Id(string field)=>Guid.TryParse(v[field],out var id)&&id!=Guid.Empty?id:throw Invalid(field,"Select an exact identity.");
        (Guid,int) Party(string field)
        {
            var parts=v[field].Split('@');
            return parts.Length==2&&Guid.TryParse(parts[0],out var id)&&id!=Guid.Empty&&int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var revision)&&revision>0?
                (id,revision):throw Invalid(field,"Select an exact party and revision.");
        }
        ExactDecimal Number(string field)
        {
            try{return ExactDecimal.Parse(v[field]);}
            catch(MarketDataException e){throw Invalid(field,e.Message);}
        }
        DateTimeOffset Instant(string field)
        {
            try{return TradingInstant.Parse(v[field],"gas_park_loan_input");}
            catch(MarketDataException e){throw Invalid(field,e.Message);}
        }
    }

    public static TicketDraft Reopen(JsonElement read)
    {
        var t=read.GetProperty("terms");
        var profile=read.TryGetProperty("implementation",out var implementation)&&implementation.ValueKind==JsonValueKind.String?implementation.GetString():null;
        if(profile is not(GasParkAndLoanTerms.Version or GasParkLoanBalanceFeeTerms.Version))throw Invalid("profile","The retained agreement must identify its installed fee profile.");
        var values=new Dictionary<string,string>{
            ["book"]=read.Id("bookId").ToString(),["legal_entity"]=$"{read.Id("legalEntityId")}@{read.GetProperty("legalEntityRevision").GetInt32()}",
            ["counterparty"]=$"{read.Id("counterpartyId")}@{read.GetProperty("counterpartyRevision").GetInt32()}",
            ["profile"]=profile,["service_kind"]=t.Text("serviceKind"),["perspective"]=t.Text("perspective"),
            ["owned_account"]=read.Id("ownedAccountId").ToString(),["claim_account"]=read.OptionalId("claimAccountId")?.ToString()??NoAccount,
            ["principal_quantity"]=read.Text("quantity"),["advance_at"]=t.Text("advanceAt"),["return_at"]=t.Text("returnAt"),
            ["title_policy"]=t.Text("titlePolicy"),["return_policy"]=t.Text("returnPolicy"),["return_timing"]=t.Text("returnTiming"),["carrying"]=t.Text("carrying"),
            ["fee_direction"]=t.Text("feeDirection")
        };
        if(profile==GasParkAndLoanTerms.Version)
        {
            var fee=ExactDecimal.Parse(t.Text("fixedFeeAmount"));
            values["fixed_fee_amount"]=(fee.CompareTo(default)<0?-fee:fee).ToString();
            values["fee_due_at"]=t.OptionalText("feeDueAt")??"none";
        }
        else
        {
            if(!t.TryGetProperty("balanceFee",out var fee)||fee.ValueKind!=JsonValueKind.Object)
                throw Invalid("profile","The retained balance-time agreement is missing its explicit fee contract.");
            foreach(var (field,member) in new[]{("fee_rate_amount","rateAmount"),("fee_basis","basis"),("fee_time_basis","timeBasis"),
                ("fee_start_policy","startPolicy"),("fee_end_policy","endPolicy"),("fee_overdue_policy","overduePolicy"),
                ("fee_projection_policy","projectionPolicy"),("fee_unadvanced_policy","unadvancedPolicy"),("fee_determination_policy","determinationPolicy"),
                ("fee_accrual_through_at","accrualThroughAt"),("fee_rounding_increment","roundingIncrement"),("fee_rounding_tie_rule","roundingTieRule"),("fee_rounding_stage","roundingStage")})
                values[field]=fee.TryGetProperty(member,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()!:throw Invalid(field,"The retained fee contract is missing this explicit term.");
            values["fee_due_at"]=t.OptionalText("feeDueAt")??throw Invalid("fee_due_at","The retained balance-time agreement is missing its explicit fee due time.");
        }
        return new(Product,1,values.ToImmutableDictionary(),[]);
    }
    public static TicketFieldException Invalid(string field,string message)=>new("gas_park_loan_input",message,null,field);
}
