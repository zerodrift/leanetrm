using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasParkLoanTicketChecks
{
    static readonly Guid Id=Guid.Parse("00000000-0000-0000-0000-000000000101");
    static TicketDraft Draft()=>new(GasParkLoanTicket.Product,1,new Dictionary<string,string>{
        ["book"]=Id.ToString(),["legal_entity"]=$"{Id}@1",["counterparty"]=$"{Id}@2",
        ["profile"]="physical_gas_park_loan_v1",["service_kind"]="loan",["perspective"]="customer",
        ["owned_account"]=Id.ToString(),["claim_account"]="none",["principal_quantity"]="720",
        ["advance_at"]="2027-01-05T09:00:00Z",["return_at"]="2027-01-20T09:00:00Z",
        ["title_policy"]="fungible_gas_title_transfer_return_v1",["return_policy"]="same_quantity_partial_returns_v1",
        ["return_timing"]="accepted_advance_then_returns_v1",["carrying"]="nominal_mark_v1",
        ["fixed_fee_amount"]="12.50",["fee_direction"]="receive",["fee_due_at"]="2027-01-22T09:00:00Z"
    }.ToImmutableDictionary(),[]);
    static GasParkLoanBooking Map(TicketDraft draft)=>GasParkLoanTicket.Request(draft,Id,Id,"current",Id,Id);
    static readonly string[] BalanceFields=["fee_rate_amount","fee_basis","fee_time_basis","fee_start_policy","fee_end_policy","fee_overdue_policy","fee_projection_policy","fee_unadvanced_policy","fee_determination_policy","fee_accrual_through_at","fee_rounding_increment","fee_rounding_tie_rule","fee_rounding_stage"];
    static TicketDraft BalanceDraft()=>Draft() with{Trade=Draft().Trade.Remove("fixed_fee_amount").SetItems(new Dictionary<string,string>{
        ["profile"]="physical_gas_park_loan_balance_time_v1",["fee_rate_amount"]="0.12500",
        ["fee_basis"]="accepted_outstanding_principal_v1",["fee_time_basis"]="elapsed_utc_hour_v1",
        ["fee_start_policy"]="accepted_advance_v1",["fee_end_policy"]="contractual_accrual_cutoff_v1",
        ["fee_overdue_policy"]="accrue_outstanding_until_cutoff_v1",["fee_projection_policy"]="remaining_return_at_fee_cutoff_v1",
        ["fee_unadvanced_policy"]="unadvanced_plan_from_later_of_earliest_or_evaluation_v1",["fee_determination_policy"]="rounded_cumulative_difference_v1",
        ["fee_accrual_through_at"]="2027-01-21T09:00:00Z",["fee_rounding_increment"]="0.0100",
        ["fee_rounding_tie_rule"]="to_even",["fee_rounding_stage"]="payment_total"
    })};
    public static void Run()
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        void Reject(TicketDraft draft,string field)
        {
            try{Map(draft);}catch(TicketFieldException e)when(e.Field==field){return;}
            throw new Exception($"Expected explicit field refusal: {field}");
        }
        var d=Draft();
        try{GasTicketStore.Description(GasTicket.Description.Product,d);throw new Exception("A description accepted another product's draft.");}catch(MarketDataException e)when(e.Code=="ticket_input"){}
        Check(GasParkLoanTicket.Description.Product=="physical_gas_park_loan"&&GasParkLoanTicket.Description.Sides.IsEmpty,"A separate profile must use managed terms without a pretend physical Side composer.");
        Check(!GasParkLoanTicket.Description.TradeFields.Any(f=>f.Id=="fixed_fee_amount"||BalanceFields.Contains(f.Id)),"An unselected profile must not assume either fee law.");
        var commonJson=JsonSerializer.SerializeToElement(GasParkLoanTicket.Description,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Check(commonJson.TryGetProperty("selection",out var commonSelection)&&commonSelection.EnumerateObject().Count()==0,"An unselected refreshing description must attest its empty selector values.");
        var mapped=Map(d);
        Check(mapped.Quantity=="720"&&mapped.FixedFeeAmount=="12.5"&&mapped.FeeDirection=="receive","Absolute fixed fee must not be scaled by principal or direction inferred from commercial role.");
        Check(mapped.ClaimAccountId is null,"Explicit none retains no provider claim account.");
        Reject(d with{Trade=d.Trade.Remove("profile")},"profile");
        Reject(d with{Trade=d.Trade.Remove("carrying")},"carrying");
        Reject(d with{Trade=d.Trade.Remove("fee_direction")},"fee_direction");
        Reject(d with{Trade=d.Trade.SetItem("fixed_fee_amount","-12.5")},"fixed_fee_amount");
        Reject(d with{Trade=d.Trade.SetItem("advance_at","2027-01-05T09:00:00")},"advance_at");
        Reject(d with{Trade=d.Trade.SetItem("return_at","2027-01-04T09:00:00Z")},"return_at");
        Reject(d with{Trade=d.Trade.SetItem("perspective","provider")},"claim_account");
        Reject(d with{Trade=d.Trade.SetItem("claim_account",Id.ToString())},"claim_account");
        Reject(d with{Trade=d.Trade.SetItem("fee_due_at","none")},"fee_due_at");
        Reject(d with{Trade=d.Trade.SetItem("fee_due_at","2027-01-05T08:59:59.999999Z")},"fee_due_at");
        var zero=Map(d with{Trade=d.Trade.SetItem("fixed_fee_amount","0").SetItem("fee_due_at","none").SetItem("fee_direction","pay")});
        Check(zero.FeeDueAt is null&&zero.FeeDirection=="pay","Zero fee preserves the explicit direction and not-applicable date.");
        Reject(d with{Trade=d.Trade.SetItem("inferred_price","3")},"inferred_price");
        var balance=BalanceDraft();var accrued=Map(balance);
        var fixedDescription=GasParkLoanTicket.Describe(d);var balanceDescription=GasParkLoanTicket.Describe(balance);
        Check(JsonSerializer.SerializeToElement(fixedDescription,new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("selection").GetProperty("profile").GetString()==d.Trade["profile"]&&JsonSerializer.SerializeToElement(balanceDescription,new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("selection").GetProperty("profile").GetString()==balance.Trade["profile"],"Each selected description must attest the exact requested profile value.");
        Check(fixedDescription.TradeFields.Any(f=>f.Id=="fixed_fee_amount")&&!fixedDescription.TradeFields.Any(f=>BalanceFields.Contains(f.Id))&&fixedDescription.TradeFields.Single(f=>f.Id=="fee_due_at").AllowNotApplicable,"Fixed entry keeps only its operative fee fields and explicit no-date choice.");
        Check(!balanceDescription.TradeFields.Any(f=>f.Id=="fixed_fee_amount")&&BalanceFields.All(id=>balanceDescription.TradeFields.Any(f=>f.Id==id))&&!balanceDescription.TradeFields.Single(f=>f.Id=="fee_due_at").AllowNotApplicable,"Balance-time entry exposes every explicit rate law with a required due time and no fixed-fee input.");
        Check(balanceDescription.TradeFields.Single(f=>f.Id=="profile").RefreshDescription,"The backend marks the profile selector for description refresh.");
        try{GasParkLoanTicket.Describe(balance with{Trade=balance.Trade.SetItem("profile","future_profile")});throw new Exception("Unknown profile unexpectedly described.");}catch(TicketFieldException e)when(e.Field=="profile"){}
        var json=JsonSerializer.SerializeToElement(accrued,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Check(accrued.Profile=="physical_gas_park_loan_balance_time_v1"&&accrued.FixedFeeAmount=="0"&&json.GetProperty("balanceFee").GetProperty("rateAmount").GetString()=="0.125","The explicit balance-time profile retains its exact absolute rate without an inert fixed-fee input.");
        Check(json.GetProperty("balanceFee").GetProperty("roundingIncrement").GetString()=="0.01"&&json.GetProperty("balanceFee").GetProperty("roundingTieRule").GetString()=="to_even","Explicit payment-total rounding terms retain their exact values.");
        Check(!JsonSerializer.SerializeToElement(mapped,new JsonSerializerOptions(JsonSerializerDefaults.Web)).TryGetProperty("balanceFee",out _),"The fixed-profile serialized booking contract remains unchanged.");
        foreach(var field in BalanceFields)Reject(balance with{Trade=balance.Trade.Remove(field)},field);
        Reject(balance with{Trade=balance.Trade.SetItem("fixed_fee_amount","0")},"fixed_fee_amount");
        Reject(d with{Trade=d.Trade.SetItem("fee_rate_amount","0.1")},"fee_rate_amount");
        Reject(balance with{Trade=balance.Trade.SetItem("fee_rate_amount","-0.1")},"fee_rate_amount");
        Reject(balance with{Trade=balance.Trade.SetItem("fee_rounding_increment","0")},"fee_rounding_increment");
        Reject(balance with{Trade=balance.Trade.SetItem("fee_rounding_tie_rule","default")},"fee_rounding_tie_rule");
        Reject(balance with{Trade=balance.Trade.SetItem("fee_accrual_through_at","2027-01-19T09:00:00Z")},"fee_accrual_through_at");
        Reject(balance with{Trade=balance.Trade.SetItem("fee_due_at","2027-01-21T08:59:59.999999Z")},"fee_due_at");
        Reject(balance with{Trade=balance.Trade.SetItem("fee_due_at","none")},"fee_due_at");
        Check(Map(balance with{Trade=balance.Trade.SetItem("fee_rate_amount","0")}).FeeDueAt is not null,"A zero rate retains its explicitly selected fee due time.");
        var retained=JsonSerializer.SerializeToElement(new{implementation=accrued.Profile,bookId=Id,legalEntityId=Id,legalEntityRevision=1,counterpartyId=Id,counterpartyRevision=2,
            ownedAccountId=Id,claimAccountId=(Guid?)null,quantity="720",terms=new{accrued.ServiceKind,accrued.Perspective,accrued.AdvanceAt,accrued.ReturnAt,
                accrued.TitlePolicy,accrued.ReturnPolicy,accrued.ReturnTiming,accrued.Carrying,accrued.FeeDirection,accrued.FeeDueAt,accrued.BalanceFee}},new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var reopened=GasParkLoanTicket.Reopen(retained);
        Check(Map(reopened)==accrued&&!reopened.Trade.ContainsKey("fixed_fee_amount"),"Retained balance-time terms reopen exactly without inventing a fixed-fee field.");
        Console.WriteLine("PASS Park and Loan explicit ticket terms and independent fixed fee");
    }
}
