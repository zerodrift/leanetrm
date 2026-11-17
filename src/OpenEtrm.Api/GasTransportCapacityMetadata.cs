using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record GasTransportCapacityChoices(ImmutableArray<GasFormChoice> References,ImmutableArray<GasFormChoice> Books,
    ImmutableArray<GasFormChoice> LegalEntities,ImmutableArray<GasFormChoice> Counterparties,ImmutableArray<GasFormChoice> Products,
    ImmutableArray<GasFormChoice> Quantities,ImmutableArray<GasFormChoice> Pipelines,ImmutableArray<GasFormChoice> ReceiptEndpoints,
    ImmutableArray<GasFormChoice> DeliveryEndpoints,ImmutableArray<GasFormChoice> Points);
internal sealed record GasTransportCapacityForms(ImmutableDictionary<string,FacilityOperationForm> Forms,
    ImmutableDictionary<string,FacilityOperationForm> Configuration);

internal static class GasTransportCapacityMetadata
{
    internal static GasTransportCapacityForms Build(JsonElement? facts,GasTransportCapacityChoices choices)
    {
        try{return BuildCore(facts,choices);}
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {throw new MarketDataException("gas_transport_capacity_source","Retain complete exact entitlement versions and immutable path geometry before describing capacity operations.",error);}
    }

    static GasTransportCapacityForms BuildCore(JsonElement? facts,GasTransportCapacityChoices choices)
    {
        GasFormField Text(string name,string label)=>new(name,label,"text",false,[],[],[]);
        GasFormField Instant(string name,string label)=>new(name,label,"instant",false,[],[],[]);
        GasFormField Select(string name,string label,ImmutableArray<GasFormChoice> values)=>new(name,label,"select",false,values,[],[]);
        GasFormField Reference(string name,string label,bool nullable,ImmutableArray<string> members,ImmutableArray<GasFormChoice> values)=>new(name,label,"reference",nullable,values,[],members);
        GasFormField Array(string name,string label,int minimum,ImmutableArray<GasFormField> fields)=>new(name,label,"array",false,[],fields,[]){MinItems=minimum};
        GasFormField Limits(string name,string label)=>Array(name,label,1,[Instant("from","Limit starts"),Instant("through","Limit ends"),Text("maximumPerHour","Maximum native quantity per elapsed hour")]);
        GasFormField Policy(string name,string label,string value,string explanation)=>Select(name,label,[new(value,explanation,value)]);
        var evidence=Text("evidence","Capacity terms or chosen-plan evidence");
        var pins=new Dictionary<string,object?>{{"kind","define"},{"entitlementId",null},{"expectedStateVersion",0},{"expectedPreviousId",null}};
        ImmutableArray<GasFormField> defineFields=[
            Text("businessKey","Entitlement business reference"),Reference("reference","Exact reference bundle",false,["referenceCatalogId","referenceRevisionId"],choices.References),
            Select("bookId","Owning book",choices.Books),Reference("legalEntity","Legal entity version",false,["legalEntityId","legalEntityRevision"],choices.LegalEntities),
            Reference("counterparty","Counterparty version",false,["counterpartyId","counterpartyRevision"],choices.Counterparties),
            Select("product","Gas product",choices.Products),Select("quantity","Native quantity basis",choices.Quantities),
            Instant("from","Entitlement service starts"),Instant("through","Entitlement service ends"),
            Policy("implementation","Capacity profile",GasTransportCapacityProfile.Version,"Gross uniform commercial entitlement capacity"),
            Policy("rateBasis","Rate basis",GasTransportCapacityProfile.ElapsedHour,"Native quantity per actual elapsed hour"),
            Policy("flowShape","Stage flow shape",GasTransportCapacityProfile.UniformStages,"Uniform flow within each separately entered receipt and delivery interval"),
            Policy("agreementBasis","Agreement, receipt and path basis",GasTransportCapacityProfile.GrossReceipts,"Gross receipt quantity without netting"),
            Policy("deliveryBasis","Delivery-point basis",GasTransportCapacityProfile.GrossDeliveries,"Gross delivery quantity without netting"),
            Limits("agreement","Complete dated agreement capacity"),
            Array("paths","Eligible direct paths and complete dated limits",1,[Text("pathKey","Path business reference"),Select("pipeline","Pipeline",choices.Pipelines),Select("product","Gas product",choices.Products),
                Reference("receipt","Exact receipt zone and point",false,["receiptZone","receiptPoint"],choices.ReceiptEndpoints),
                Reference("delivery","Exact delivery zone and point",false,["deliveryZone","deliveryPoint"],choices.DeliveryEndpoints),Limits("limits","Complete dated path capacity")]),
            Array("receipts","Complete receipt-point capacities",1,[Select("point","Receipt point",choices.Points),Limits("limits","Complete dated receipt capacity")]),
            Array("deliveries","Complete delivery-point capacities",1,[Select("point","Delivery point",choices.Points),Limits("limits","Complete dated delivery capacity")]),evidence];
        var forms=ImmutableDictionary.CreateBuilder<string,FacilityOperationForm>();
        var label="Publish Transport entitlement capacity";
        if(facts is {} source)
        {
            var entitlement=source.GetProperty("entitlement");var version=source.GetProperty("version");var id=entitlement.Id("id");
            if(id==Guid.Empty||version.Id("entitlement_id")!=id||version.Id("id")==Guid.Empty)throw Source();
            var head=source.GetProperty("stateVersion").GetInt32();if(head<1)throw Source();
            pins["entitlementId"]=id;pins["expectedStateVersion"]=head;pins["expectedPreviousId"]=version.Id("id");
            pins["businessKey"]=GasOperationText.Key(entitlement.Text("business_key"),"gas_transport_capacity_source");
            foreach(var (field,column) in new[]{("referenceCatalogId","reference_catalog_id"),("referenceRevisionId","reference_revision_id"),
                ("bookId","book_id"),("legalEntityId","legal_entity_id"),("counterpartyId","counterparty_id")})
            {var value=version.Id(column);if(value==Guid.Empty)throw Source();pins[field]=value;}
            foreach(var (field,column) in new[]{("legalEntityRevision","legal_entity_revision"),("counterpartyRevision","counterparty_revision")})
            {var value=version.GetProperty(column).GetInt32();if(value<1)throw Source();pins[field]=value;}
            foreach(var (field,column) in new[]{("product","product_code"),("quantity","quantity_code"),("implementation","implementation"),("rateBasis","rate_basis"),
                ("flowShape","flow_shape"),("agreementBasis","agreement_basis"),("deliveryBasis","delivery_basis")})pins[field]=version.Text(column);
            pins["from"]=LifecycleNative.Instant(TradingInstant.Parse(version.Text("from_at"),"gas_transport_capacity_source"));
            pins["through"]=LifecycleNative.Instant(TradingInstant.Parse(version.Text("through_at"),"gas_transport_capacity_source"));
            var paths=Rows(source,"paths");var receipts=Rows(source,"receipts");var deliveries=Rows(source,"deliveries");var plans=Rows(source,"plans");
            if(paths.Length==0||receipts.Length==0||deliveries.Length==0||paths.Select(p=>p.Id("id")).Distinct().Count()!=paths.Length||
                plans.Select(p=>p.Id("id")).Distinct().Count()!=plans.Length||plans.Select(p=>p.Text("business_key")).Distinct(StringComparer.Ordinal).Count()!=plans.Length)throw Source();
            object Path(JsonElement row)=>new{pathId=row.Id("id"),pathKey=row.Text("path_key"),pipeline=row.Text("pipeline_code"),
                receiptZone=row.Text("receipt_zone_code"),receiptPoint=row.Text("receipt_point_code"),deliveryZone=row.Text("delivery_zone_code"),
                deliveryPoint=row.Text("delivery_point_code"),product=row.Text("product_code")};
            var pathChoices=paths.Select(row=>new GasFormChoice(row.Id("id").ToString(),row.Text("path_key")+" · "+row.Text("receipt_point_code")+" → "+row.Text("delivery_point_code"),Path(row))).ToImmutableArray();
            ImmutableArray<GasFormChoice> Points(JsonElement[] rows)=>[..rows.Select(row=>new GasFormChoice(row.Text("point_code"),row.Text("point_code"),row.Text("point_code")))];
            pins["paths"]=paths.Select(Path).ToArray();pins["receipts"]=receipts.Select(row=>new{point=row.Text("point_code")}).ToArray();
            pins["deliveries"]=deliveries.Select(row=>new{point=row.Text("point_code")}).ToArray();
            defineFields=[Limits("agreement","Complete revised agreement capacity"),Array("paths","Complete revised path capacities",1,[
                Reference("path","Exact immutable path",false,["pathId","pathKey","pipeline","receiptZone","receiptPoint","deliveryZone","deliveryPoint","product"],pathChoices),Limits("limits","Complete revised path capacity")]),
                Array("receipts","Complete revised receipt-point capacities",1,[Select("point","Retained receipt point",Points(receipts)),Limits("limits","Complete revised receipt capacity")]),
                Array("deliveries","Complete revised delivery-point capacities",1,[Select("point","Retained delivery point",Points(deliveries)),Limits("limits","Complete revised delivery capacity")]),evidence];
            label="Revise complete dated Transport capacities";
            var previous=plans.Select(row=>new GasFormChoice(row.Id("id").ToString(),row.Text("business_key")+" · revision "+row.GetProperty("revision").GetInt32(),new{expectedPreviousId=row.Id("id")})).ToImmutableArray();
            forms.Add("plan",new("Review a complete feasible capacity replacement","RecordGasOperations",Json(new{kind="plan",entitlementId=id,expectedStateVersion=head,versionId=version.Id("id"),
                admissionPolicy=GasTransportCapacityPlanAdmission.FeasibleReplacement}),[
                Text("businessKey","Chosen-plan business reference"),Reference("previous","Previous plan head (explicitly none for a new plan)",true,["expectedPreviousId"],previous),
                Array("claims","Complete replacement claims (empty releases this plan)",0,[
                    Select("pathId","Exact eligible path",[..paths.Select(row=>new GasFormChoice(row.Id("id").ToString(),row.Text("path_key"),row.Id("id")))]),
                    Instant("receiptFrom","Receipt starts"),Instant("receiptTo","Receipt ends"),Text("receiptQuantity","Gross receipt quantity"),
                    Instant("deliveryFrom","Delivery starts"),Instant("deliveryTo","Delivery ends"),Text("deliveryQuantity","Gross delivery quantity")]),evidence]));
            forms.Add("release",new("Release all claims of this plan","RecordGasOperations",Json(new{kind="plan",entitlementId=id,expectedStateVersion=head,versionId=version.Id("id"),
                admissionPolicy=GasTransportCapacityPlanAdmission.EmptyRelease,claims=System.Array.Empty<object>()}),[
                Reference("plan","Exact existing plan and current head",false,["businessKey","expectedPreviousId"],
                    [..plans.Select(row=>new GasFormChoice(row.Id("id").ToString(),row.Text("business_key")+" · revision "+row.GetProperty("revision").GetInt32(),
                        new{businessKey=row.Text("business_key"),expectedPreviousId=row.Id("id")}))]),evidence]));
        }
        return new(forms.ToImmutable(),ImmutableDictionary<string,FacilityOperationForm>.Empty.Add("define",new(label,"ConfigureGasServices",Json(pins),defineFields)));
    }
    static JsonElement[] Rows(JsonElement source,string field)
    {var rows=source.GetProperty(field);if(rows.ValueKind!=JsonValueKind.Array)throw Source();return[..rows.EnumerateArray()];}
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,GasOwnedOperations.Json);
    static MarketDataException Source()=>new("gas_transport_capacity_source","Retain complete exact entitlement versions and immutable path geometry before describing capacity operations.");
}
