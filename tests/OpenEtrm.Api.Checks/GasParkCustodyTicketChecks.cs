using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

internal static class GasParkCustodyTicketChecks
{
    static readonly Guid Id=Guid.Parse("00000000-0000-0000-0000-000000000201");
    static readonly Guid Reference=Guid.Parse("00000000-0000-0000-0000-000000000202");
    static readonly Guid Revision=Guid.Parse("00000000-0000-0000-0000-000000000203");
    static TicketDraft Draft()=>new(GasParkCustodyTicket.TicketProduct,1,new Dictionary<string,string>{
        ["profile"]="physical_gas_park_custody_v1",["service_kind"]="park",["perspective"]="customer",
        ["reference_bundle"]=$"{Reference}@{Revision}",["point"]="synthetic_custody_point",["quantity_specification"]="synthetic_gas_quantity",["currency"]="synthetic_currency",
        ["owned_account"]=Id.ToString(),["custody_quantity"]="100.00",["park_at"]="2031-01-02T10:00:00Z",["return_at"]="2031-01-03T10:00:00Z",
        ["title_policy"]="customer_title_retained_v1",["return_policy"]="same_point_same_basis_partial_custody_returns_v1",
        ["return_timing"]="accepted_custody_then_returns_v1",["return_order"]="nondecreasing_accepted_custody_return_instants_v1",
        ["use_policy"]="no_use_or_sale_of_custody_v1",["loss_policy"]="unqualified_loss_requires_reconciliation_v1",
        ["physical_policy"]="custody_only_no_inferred_flow_v1",["plan_policy"]="unaccepted_custody_plan_from_later_of_earliest_or_evaluation_v1",
        ["fixed_fee_amount"]="12.50",["fee_direction"]="pay",["fee_due_at"]="2031-01-03T12:00:00Z",["fee_recognition"]="accepted_custody_v1",
        ["book"]=Id.ToString(),["legal_entity"]=$"{Id}@1",["counterparty"]=$"{Id}@2"
    }.ToImmutableDictionary(),[]);
    static GasParkCustodyBooking Map(TicketDraft draft)=>GasParkCustodyTicket.Request(draft,Id,Id,"current",Id,Id);
    public static void Run()
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        void Reject(TicketDraft draft,string field)
        {
            try{Map(draft);}catch(TicketFieldException e)when(e.Field==field){return;}
            throw new Exception($"Expected explicit custody field refusal: {field}");
        }
        var draft=Draft();var common=GasParkCustodyTicket.Describe(null);
        Check(common.Product=="physical_gas_park_loan"&&common.Sides.IsEmpty&&!common.TradeFields.Any(f=>f.Id=="fixed_fee_amount"),"An unselected custody profile must not assume a fee or create a physical Side.");
        Check(common.Selection is {Count:0}&&common.TradeFields.Single(f=>f.Id=="profile").RefreshDescription,"Custody entry must reuse the explicitly selected profile description contract.");
        var description=GasParkCustodyTicket.Describe(draft);
        Check(description.Selection?.GetValueOrDefault("profile")==GasParkCustodyTerms.Version&&description.TradeFields.Any(f=>f.Id=="fixed_fee_amount"),"The selected custody descriptor must attest its profile and expose its explicit fixed-fee terms.");
        Check(description.TradeFields.Single(f=>f.Id=="owned_account").DependsOn.Contains("perspective"),"Owned-account choices must refresh under the explicitly selected custody perspective.");
        var booking=Map(draft);
        Check(booking.Quantity=="100"&&booking.FixedFeeAmount=="12.5"&&booking.FeeDirection=="pay"&&booking.OwnedAccountId==Id,"Customer custody keeps positive quantity, absolute fee and independently selected pay direction.");
        Check(booking.ReferenceCatalogId==Reference&&booking.ReferenceRevisionId==Revision&&booking.Point=="synthetic_custody_point"&&booking.QuantitySpecification=="synthetic_gas_quantity"&&booking.Currency=="synthetic_currency","Point, native quantity and currency must retain their explicit reference bundle, independently of inventory.");
        var provider=draft with{Trade=draft.Trade.SetItem("perspective","provider").SetItem("owned_account","none").SetItem("fee_direction","receive")};
        var providerBooking=Map(provider);
        Check(providerBooking.OwnedAccountId is null&&providerBooking.FixedFeeAmount=="12.5"&&providerBooking.FeeDirection=="receive","Provider custody explicitly selects no owned inventory and keeps its fee direction separate.");
        Reject(draft with{Trade=draft.Trade.SetItem("owned_account","none")},"owned_account");
        Reject(provider with{Trade=provider.Trade.SetItem("owned_account",Id.ToString())},"owned_account");
        foreach(var field in description.TradeFields)Reject(draft with{Trade=draft.Trade.Remove(field.Id)},field.Id);
        foreach(var (field,value) in new[]{("profile","unsupported"),("service_kind","loan"),("reference_bundle",Id.ToString()),("reference_bundle",$"{Reference}@{Guid.Empty}"),
            ("custody_quantity","0"),("custody_quantity","-1"),("fixed_fee_amount","-12.5"),("fee_direction","inferred"),
            ("park_at","2031-01-02T10:00:00"),("return_at","2031-01-02T10:00:00Z"),("fee_due_at","none"),
            ("fee_due_at","2031-01-02T09:59:59Z"),("title_policy","fungible_gas_title_transfer_return_v1"),("loss_policy","no_liability"),
            ("physical_policy","infer_pipeline_flow"),("return_order","arbitrary")})Reject(draft with{Trade=draft.Trade.SetItem(field,value)},field);
        Reject(draft with{Trade=draft.Trade.Add("mark_curve","synthetic_mark")},"mark_curve");
        var zero=Map(provider with{Trade=provider.Trade.SetItem("fixed_fee_amount","0").SetItem("fee_due_at","none").SetItem("fee_direction","pay")});
        Check(zero.FeeDueAt is null&&zero.FixedFeeAmount=="0"&&zero.FeeDirection=="pay","An explicit zero fee preserves the separately selected direction and no-date choice.");
        Check(description.TradeFields.Single(f=>f.Id=="fee_due_at").AllowNotApplicable,"Zero-fee custody must use the shared explicit no-date editor.");
        Check(GasParkCustodyTicket.StaticChoices("loss_policy")!.Value.Single().Label.Contains("unqualified",StringComparison.OrdinalIgnoreCase),"Loss application is unqualified; the UI must not claim zero liability.");
        var native=JsonSerializer.SerializeToElement(new{implementation=GasParkCustodyTerms.Version,bookId=Id,legalEntityId=Id,legalEntityRevision=1,counterpartyId=Id,counterpartyRevision=2,
            ownedAccountId=(Guid?)null,quantity="100",terms=new{serviceKind="park",perspective="provider",referenceCatalogId=Reference,referenceRevisionId=Revision,
                point="synthetic_custody_point",quantity="synthetic_gas_quantity",currency="synthetic_currency",parkAt="2031-01-02T10:00:00Z",returnAt="2031-01-03T10:00:00Z",
                titlePolicy=GasParkCustodyTerms.RetainTitle,returnPolicy=GasParkCustodyTerms.SameBasis,returnTiming=GasParkCustodyTerms.ReturnsAfterAcceptance,
                returnOrder=GasParkCustodyTerms.NondecreasingReturns,usePolicy=GasParkCustodyTerms.NoUse,lossPolicy=GasParkCustodyTerms.LossUnqualified,
                physicalPolicy=GasParkCustodyTerms.SeparatePhysical,planPolicy=GasParkCustodyTerms.UnacceptedPlan,fixedFeeAmount="12.5",feeDirection="receive",
                feeDueAt="2031-01-03T12:00:00Z",feeRecognition=GasParkCustodyTerms.FeeAtAcceptance}});
        var reopened=GasParkCustodyTicket.Reopen(native);
        Check(Map(reopened)==providerBooking&&reopened.Trade["owned_account"]=="none"&&reopened.Trade["reference_bundle"]==$"{Reference}@{Revision}","Reopening provider custody must retain the exact independent terms and explicit no-account choice without a commodity mark.");
        Console.WriteLine("PASS custody ticket: explicit retained-title profile, independent references, customer/provider ownership, signed direction, fee dates and exact reopen");
    }
}
