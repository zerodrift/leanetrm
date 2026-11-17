using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

internal static class GasTransportFinancialTicketChecks
{
    static readonly Guid Id=Guid.Parse("00000000-0000-0000-0000-000000000301");
    static readonly Guid Catalog=Guid.Parse("00000000-0000-0000-0000-000000000302");
    static readonly Guid ValuationRevision=Guid.Parse("00000000-0000-0000-0000-000000000303");
    static readonly Guid Capacity=Guid.Parse("00000000-0000-0000-0000-000000000304");
    static TicketDraft Draft()=>new("physical_gas_transport",1,new Dictionary<string,string>{
        ["profile"]="physical_gas_transport_direct_rights_v1",["business_key"]="Synthetic Transport agreement",["entitlement"]=Id.ToString(),["capacity_version"]=Capacity.ToString(),
        ["perspective"]="customer",["valuation_definition"]=Id.ToString(),["valuation_reference_bundle"]=$"{Catalog}@{ValuationRevision}",["package_quantity"]="whole_transport_package",["currency"]="synthetic_currency",
        ["rights_valuation_policy"]="signed_current_whole_remaining_rights_pv_excluding_fees_and_owned_gas_v1",
        ["fee_profile"]="unconditional_one_off_fixed_amount_at_booking_v1",["fixed_fee_amount"]="12.50",["fee_direction"]="pay",["fee_due_at"]="2031-01-02T10:00:00Z",
        ["expiry_policy"]="fixed_window_capacity_rights_expire_at_through_v1",["evidence"]="Synthetic signed contract reference",
        ["book"]=Id.ToString(),["legal_entity"]=$"{Id}@1",["counterparty"]=$"{Id}@2"
    }.ToImmutableDictionary(),[]);
    static GasTransportFinancialBooking Map(TicketDraft draft)=>GasTransportFinancialTicket.Request(draft,Id,Id,"current",Id,Id);
    public static void Run()
    {
        void Check(bool value,string message){if(!value)throw new Exception(message);}
        void Reject(TicketDraft draft,string field)
        {
            try{Map(draft);}catch(TicketFieldException error)when(error.Field==field){return;}
            throw new Exception($"Expected explicit financial Transport field refusal: {field}");
        }
        var draft=Draft();var description=GasTransportFinancialTicket.Describe(draft);
        var schedules=new BookedScheduleReader(BookedScheduleSources.Create(null!));
        var listed=new System.Text.Json.Nodes.JsonObject{["instrumentType"]=GasTransportFinancialTicket.Product,["sideSchedules"]=true};
        TradingBlotter.Listed(listed,null,schedules.Covers);
        Check(!schedules.Covers(GasTransportFinancialTicket.Product)&&listed["sideSchedules"]!.GetValue<bool>()==false,
            "Ordinary Blotter listing must not expose an unregistered Transport Side schedule capability.");
        var sourceRows=new[]{Id,Capacity}.Select(definition=>JsonSerializer.SerializeToElement(new{definitionRevisionId=definition,referenceCatalogId=Catalog,referenceRevisionId=ValuationRevision,
            label="Synthetic whole-rights source",quantity="whole_transport_package",quantityLabel="One whole Transport package",currency="synthetic_currency",currencyLabel="USD"})).ToImmutableArray();
        var choiceRequest=new TicketFieldRequest(new(Id,"physical_gas_fixed_quantity_v4","current"),draft,null,"valuation_definition");
        var definitions=GasTransportFinancialTicketStore.ValuationChoices(choiceRequest,sourceRows);
        Check(definitions.Choices.Length==2&&definitions.StartingValue is null&&definitions.Choices.Select(choice=>choice.Label).Distinct().Count()==2&&
            definitions.Choices.All(choice=>choice.Label.StartsWith("Synthetic whole-rights source · ",StringComparison.Ordinal)),
            "Colliding valuation-definition labels must each identify their exact source without selecting a financial default.");
        Check(GasTransportFinancialTicketStore.ValuationChoices(choiceRequest,[sourceRows[0]]).Choices.Single().Label=="Synthetic whole-rights source",
            "A sole clear valuation source must retain its published human label.");
        var sourceContext=new LifecycleReadContext(Id,DateTimeOffset.Parse("2031-01-02T16:00:00Z"),new(2031,1,2),Id);
        var sourceKnown=DateTimeOffset.Parse("2031-01-04T10:00:00Z");
        JsonElement FinancialCapacity(Guid? target=null,string? at=null,string? effective=null,bool future=false)=>JsonSerializer.SerializeToElement(new{
            contextId=Id,asOf=at??"2031-01-02T16:00:00Z",knownAt="2031-01-04T10:00:00Z",effectiveOn="2031-01-02",authorityUntil="2032-01-01T00:00:00Z",
            capacities=new[]{new{label="Synthetic exact financial capacity",asOf=at??"2031-01-02T16:00:00Z",hasFutureHeads=future,
                entitlement=new{catalog_id=Catalog,id=target??Id},version=future?null:new{catalog_id=Catalog,entitlement_id=target??Id,id=Capacity,revision=1,effective_at=effective??"2031-01-02T15:00:00Z"}}}});
        var capacities=GasTransportFinancialCapacity.Require(FinancialCapacity(),Catalog,sourceContext,sourceKnown,Id);
        Check(capacities.Length==1&&capacities[0].GetProperty("version").GetProperty("id").GetGuid()==Capacity,
            "Financial capacity discovery must keep its exact 16:00 source instead of reading later operating-day capacity.");
        Check(GasTransportFinancialCapacity.Require(FinancialCapacity(future:true),Catalog,sourceContext,sourceKnown,Id).Single().GetProperty("version").ValueKind==JsonValueKind.Null,
            "A latest future capacity definition must remain unavailable without an old-version fallback.");
        void RefuseCapacity(JsonElement envelope,DateTimeOffset? known=null)
        {
            try{GasTransportFinancialCapacity.Require(envelope,Catalog,sourceContext,known??sourceKnown,Id);}
            catch(MarketDataException error)when(error.Code=="gas_transport_financial_source"){return;}
            throw new Exception("Financial capacity discovery accepted another target, clock or knowledge source.");
        }
        RefuseCapacity(FinancialCapacity(Capacity));
        RefuseCapacity(FinancialCapacity(at:"2031-01-02T23:59:59.999999Z"));
        RefuseCapacity(FinancialCapacity(effective:"2031-01-02T16:00:00.000001Z"));
        RefuseCapacity(FinancialCapacity(),sourceKnown.AddTicks(-10));
        JsonElement CapacityFacts(string at,bool future=false)=>JsonSerializer.SerializeToElement(new{asOf=at,entitlement=new{through_at="2031-01-03T10:00:00Z"},version=future?null:new{id=Capacity}});
        Check(GasTransportFinancialTicketStore.CapacityBookable(CapacityFacts("2031-01-03T09:59:59.999999Z"))&&
            !GasTransportFinancialTicketStore.CapacityBookable(CapacityFacts("2031-01-03T10:00:00Z"))&&
            !GasTransportFinancialTicketStore.CapacityBookable(CapacityFacts("2031-01-02T10:00:00Z",true)),
            "New Transport booking choices must omit expired and future-effective capacity definitions using the exact selected source instant.");
        Check(description.Product==GasTransportFinancialTicket.Product&&description.Sides.IsEmpty&&description.TradeFields.SingleOrDefault(f=>f.Id=="package_quantity") is {Type:"choice"},
            "Transport entry must describe one exact whole-package basis choice without gas sides or editable numeric scaling.");
        Check(description.TradeFields.Where(f=>f.Id is "profile" or "rights_valuation_policy" or "fee_profile" or "expiry_policy").All(f=>f.Required&&f.Type=="choice")&&
            description.TradeFields.Count(f=>f.Id is "profile" or "rights_valuation_policy" or "fee_profile" or "expiry_policy")==4,
            "Every admitted financial law must be an explicit required choice, including a sole installed policy.");
        Check(description.TradeFields.Single(f=>f.Id=="capacity_version").DependsOn.Contains("entitlement")&&
            description.TradeFields.Single(f=>f.Id=="package_quantity").DependsOn.Contains("valuation_reference_bundle"),
            "Operating capacity and valuation package references must retain independent exact dependencies.");
        var booking=Map(draft);
        Check(booking.EntitlementId==Id&&booking.CapacityVersionId==Capacity&&booking.ValuationReferenceCatalogId==Catalog&&booking.ValuationReferenceRevisionId==ValuationRevision&&
            booking.PackageQuantity=="whole_transport_package"&&booking.DefinitionRevisionId==Id,"Operating capacity and selected valuation definition/reference must not be forced into one bundle.");
        Check(GasTransportFinancialTicket.ValuationSource(draft)==new CatalogueSource(Id,Catalog,ValuationRevision),"The explicit valuation definition must be retained independently of the gas source.");
        try{GasTransportFinancialTicket.Request(draft,Id,Id,"current",Id,Capacity);throw new Exception("A different valuation definition replaced the explicit choice.");}catch(TicketFieldException error)when(error.Field=="valuation_definition"){}
        Check(booking.FixedFeeAmount=="12.5"&&booking.FeeDirection=="pay"&&booking.Perspective=="customer","Absolute fee and direction must survive independently of perspective.");
        var provider=Map(draft with{Trade=draft.Trade.SetItem("perspective","provider").SetItem("fee_direction","pay")});
        Check(provider.FixedFeeAmount=="12.5"&&provider.FeeDirection=="pay"&&provider.PackageQuantity==booking.PackageQuantity,"Provider perspective must not reverse the chosen fee or resize the whole rights package.");
        foreach(var field in description.TradeFields)Reject(draft with{Trade=draft.Trade.Remove(field.Id)},field.Id);
        foreach(var(field,value)in new[]{("profile","unsupported"),("entitlement",Guid.Empty.ToString()),("capacity_version","unknown"),("valuation_reference_bundle",Id.ToString()),
            ("rights_valuation_policy","infer_gas_price"),("fee_profile","fee_on_flow"),("expiry_policy","calendar_zero"),("fee_direction","inferred"),("fixed_fee_amount","-12.5"),
            ("fee_due_at","none"),("fee_due_at","2031-01-02T10:00:00.00000001Z"),("legal_entity",$"{Id}@0"),("business_key","bad\nkey"),("evidence",new string('x',2001))})
            Reject(draft with{Trade=draft.Trade.SetItem(field,value)},field);
        foreach(var field in new[]{"quantity","gas_quantity","rights_mark_amount","owned_account"})Reject(draft with{Trade=draft.Trade.Add(field,"1")},field);
        var zero=Map(draft with{Trade=draft.Trade.SetItem("fixed_fee_amount","0").SetItem("fee_due_at","none")});
        Check(zero.FixedFeeAmount=="0"&&zero.FeeDueAt is null&&zero.FeeDirection=="pay"&&description.TradeFields.Single(f=>f.Id=="fee_due_at").AllowNotApplicable,
            "A zero fee must be explicitly entered with an explicit no-date choice, without a zero cash default.");
        Reject(draft with{Trade=draft.Trade.SetItem("fixed_fee_amount","0")},"fee_due_at");
        try{GasTransportFinancialTicket.Describe(draft with{Product="physical_gas"});throw new Exception("A descriptor accepted another product.");}catch(TicketFieldException){}
        Check(GasTicketStore.Description(GasTransportFinancialTicket.Product,draft)==description,"Shared description dispatch must retain the exact Transport product.");
        var native=JsonSerializer.SerializeToElement(new{implementation="physical_gas_transport_direct_rights_v1",businessKey=booking.BusinessKey,evidence=booking.Evidence,
            bookId=booking.BookId,legalEntityId=booking.LegalEntityId,legalEntityRevision=booking.LegalEntityRevision,counterpartyId=booking.CounterpartyId,counterpartyRevision=booking.CounterpartyRevision,
            terms=new{profile=booking.Profile,definitionRevisionId=booking.DefinitionRevisionId,entitlementId=booking.EntitlementId,valuationReferenceCatalogId=booking.ValuationReferenceCatalogId,valuationReferenceRevisionId=booking.ValuationReferenceRevisionId,
                operationalReferenceCatalogId=Id,operationalReferenceRevisionId=Id,packageQuantity=booking.PackageQuantity,currency=booking.Currency,perspective=booking.Perspective,rightsValuationPolicy=booking.RightsValuationPolicy,
                feeProfile=booking.FeeProfile,fixedFeeAmount=booking.FixedFeeAmount,feeDirection=booking.FeeDirection,feeDueAt=booking.FeeDueAt,expiryPolicy=booking.ExpiryPolicy},
            financialState=new{capacityVersionId=Capacity,evidence="A later capacity adoption is not booking evidence"}});
        Check(Map(GasTransportFinancialTicket.Reopen(native))==booking,"Reopen must preserve current exact capacity, independent valuation bundle, policies and original booking evidence.");
        var future=JsonSerializer.SerializeToElement(new{implementation="physical_gas_transport_direct_rights_v1",financialState=(object?)null});
        try{GasTransportFinancialTicket.Reopen(future);throw new Exception("A future financial state invented a capacity version.");}catch(MarketDataException){}
        Console.WriteLine("PASS financial Transport ticket: explicit whole package, independent references and fees, strict policies, exact precision and retained reopen");
    }
}
