using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.Shared.Commands;

internal static class GasTransportCapacityMetadataChecks
{
    static Guid Id(int n)=>Guid.Parse($"72222222-2222-2222-2222-{n:D12}");
    static readonly Guid Path=OperationIdentity.For("gas_transport_path_v1:synthetic direct path",Id(10));
    const string From="2032-03-14T08:00:00.000000Z",Through="2032-03-15T08:00:00.000000Z";
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,GasOwnedOperations.Json);
    static GasTransportCapacityChoices Choices()=>new(
        [new("synthetic reference","Synthetic exact reference",new{referenceCatalogId=Id(1),referenceRevisionId=Id(2)})],
        [new(Id(3).ToString(),"Synthetic book",Id(3))],
        [new(Id(4).ToString(),"Synthetic legal entity",new{legalEntityId=Id(4),legalEntityRevision=2})],
        [new(Id(5).ToString(),"Synthetic counterparty",new{counterpartyId=Id(5),counterpartyRevision=3})],
        [new("synthetic_gas","Synthetic gas","synthetic_gas")],
        [new("synthetic_quantity","Synthetic native quantity","synthetic_quantity")],
        [new("synthetic_pipeline","Synthetic pipeline","synthetic_pipeline")],
        [new("synthetic_zone:synthetic_a","Synthetic receipt A",new{receiptZone="synthetic_zone",receiptPoint="synthetic_a"}),
            new("synthetic_zone:synthetic_b","Synthetic receipt B",new{receiptZone="synthetic_zone",receiptPoint="synthetic_b"})],
        [new("synthetic_zone:synthetic_a","Synthetic delivery A",new{deliveryZone="synthetic_zone",deliveryPoint="synthetic_a"}),
            new("synthetic_zone:synthetic_b","Synthetic delivery B",new{deliveryZone="synthetic_zone",deliveryPoint="synthetic_b"})],
        [new("synthetic_a","Synthetic point A","synthetic_a"),new("synthetic_b","Synthetic point B","synthetic_b")]);
    static object[] Limits()=>[new{from=From,through=Through,maximum_per_hour="100"}];
    static JsonElement Facts()=>Json(new{entitlement=new{id=Id(10),business_key="synthetic entitlement"},stateVersion=7,
        version=new{id=Id(11),entitlement_id=Id(10),revision=2,reference_catalog_id=Id(1),reference_revision_id=Id(2),book_id=Id(3),
            legal_entity_id=Id(4),legal_entity_revision=2,counterparty_id=Id(5),counterparty_revision=3,product_code="synthetic_gas",quantity_code="synthetic_quantity",
            from_at=From,through_at=Through,implementation=GasTransportCapacityProfile.Version,rate_basis=GasTransportCapacityProfile.ElapsedHour,
            flow_shape=GasTransportCapacityProfile.UniformStages,agreement_basis=GasTransportCapacityProfile.GrossReceipts,delivery_basis=GasTransportCapacityProfile.GrossDeliveries},
        paths=new[]{new{id=Path,path_key="synthetic direct path",pipeline_code="synthetic_pipeline",receipt_zone_code="synthetic_zone",receipt_point_code="synthetic_a",
            delivery_zone_code="synthetic_zone",delivery_point_code="synthetic_b",product_code="synthetic_gas",limits=Limits()}},
        agreement=Limits(),receipts=new[]{new{point_code="synthetic_a",limits=Limits()}},deliveries=new[]{new{point_code="synthetic_b",limits=Limits()}},
        plans=new[]{new{id=Id(13),business_key="synthetic chosen plan",revision=2,previous_id=Id(14),version_id=Id(11)}},claims=Array.Empty<object>()});
    static GasFormField Field(FacilityOperationForm form,string name)=>form.Fields.Single(f=>f.Name==name);
    static void Fields(ImmutableArray<GasFormField> fields)
    {
        var members=fields.SelectMany(f=>f.Kind=="reference"?f.Members:[f.Name]).ToArray();
        if(members.Distinct(StringComparer.Ordinal).Count()!=members.Length)throw new Exception("Sibling described fields must have disjoint flattened wire members.");
        foreach(var field in fields)
        {
            if(field.Kind=="reference")
                foreach(var choice in field.Choices)
                {
                    var value=Json(choice.Value);
                    if(value.ValueKind!=JsonValueKind.Object||!value.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(field.Members.Order()))
                        throw new Exception("An opaque reference choice must contain exactly its declared flattened tuple.");
                }
            if(field.Kind=="array")Fields(field.Fields);
        }
    }
    static void Refuse(Action action)
    {
        try{action();}catch(MarketDataException error)when(error.Code=="gas_transport_capacity_source"){return;}
        throw new Exception("Incomplete immutable capacity metadata must refuse rather than substitute current references or geometry.");
    }

    internal static void Run()
    {
        var initial=GasTransportCapacityMetadata.Build(null,Choices());
        if(initial.Forms.Count!=0||initial.Configuration.Count!=1)throw new Exception("A new Transport directory must expose only explicit entitlement configuration.");
        var define=initial.Configuration["define"];
        if(define.Permission!="ConfigureGasServices"||define.Base.GetProperty("entitlementId").ValueKind!=JsonValueKind.Null||
            define.Base.GetProperty("expectedPreviousId").ValueKind!=JsonValueKind.Null||define.Base.GetProperty("expectedStateVersion").GetInt32()!=0)
            throw new Exception("Transport configuration needs its own permission and no invented accepted entitlement.");
        Fields(define.Fields);
        if(Field(define,"reference").Members.Length!=2||Field(define,"bookId").Choices.Length!=1||Field(define,"quantity").Choices.Length!=1)
            throw new Exception("Exact references, authorized book and gas quantity must remain independent explicit choices.");
        foreach(var law in new[]{"implementation","rateBasis","flowShape","agreementBasis","deliveryBasis"})
            if(define.Base.TryGetProperty(law,out _)||Field(define,law) is not{Kind:"select",Nullable:false}||Field(define,law).Choices.Length!=1)
                throw new Exception("Installed capacity laws must be explicit choices rather than assumed Base values.");
        var paths=Field(define,"paths");
        if(paths.MinItems!=1||paths.Fields.Single(f=>f.Name=="receipt").Choices.Length!=2||paths.Fields.Single(f=>f.Name=="delivery").Choices.Length!=2||
            paths.Fields.Any(f=>f.Name=="route"))throw new Exception("Independent endpoint choices must not generate Cartesian receipt/delivery routes.");
        foreach(var list in new[]{"agreement","paths","receipts","deliveries"})
            if(Field(define,list).MinItems!=1)throw new Exception("Complete dated agreement/path/point limits require explicit nonempty rows.");
        if(define.Fields.Any(f=>f.Name is "ownedAccount" or "cashAmount" or "fuel" or "facilityId"))
            throw new Exception("Capacity metadata cannot invent stock, cash, conservation or facility-total dependencies.");

        var facts=Facts();var saved=GasTransportCapacityMetadata.Build(facts,Choices());var revision=saved.Configuration["define"];
        if(!saved.Forms.TryGetValue("release",out var release)||release.Label!="Release all claims of this plan")
            throw new Exception("Capacity cleanup requires a separately selected explicit empty-release operation.");
        Fields(revision.Fields);Fields(saved.Forms["plan"].Fields);Fields(release.Fields);
        if(revision.Base.Id("entitlementId")!=Id(10)||revision.Base.Id("expectedPreviousId")!=Id(11)||revision.Base.GetProperty("expectedStateVersion").GetInt32()!=7||
            revision.Base.Id("bookId")!=Id(3)||revision.Base.Id("referenceRevisionId")!=Id(2)||revision.Base.Text("quantity")!="synthetic_quantity"||
            revision.Base.Text("from")!=From||revision.Base.Text("through")!=Through)
            throw new Exception("A capacity revision must pin immutable ownership, exact references, quantity basis, service interval and predecessor.");
        if(revision.Fields.Any(f=>f.Name is "bookId" or "reference" or "quantity" or "from" or "through" or "product" or "implementation"))
            throw new Exception("A capacity revision may change complete dated limits and evidence, not immutable scope or laws.");
        var exactPath=Field(revision,"paths").Fields.Single(f=>f.Name=="path");
        var pathValue=Json(exactPath.Choices.Single().Value);
        if(pathValue.Id("pathId")!=Path||pathValue.Text("pathKey")!="synthetic direct path"||pathValue.Text("pipeline")!="synthetic_pipeline"||
            pathValue.Text("receiptPoint")!="synthetic_a"||pathValue.Text("deliveryPoint")!="synthetic_b"||exactPath.Members.Length!=8)
            throw new Exception("A capacity revision's exact path tuple must preserve every immutable geometry field.");
        if(Field(revision,"receipts").Fields.Single(f=>f.Name=="point").Choices.Length!=1||Field(revision,"deliveries").Fields.Single(f=>f.Name=="point").Choices.Length!=1)
            throw new Exception("Revision point limits must select retained points, not unrelated current reference choices.");
        var revisionBody=JsonNode.Parse(revision.Base.GetRawText())!.AsObject();
        JsonNode EnteredLimits()=>JsonSerializer.SerializeToNode(new[]{new{from=From,through=Through,maximumPerHour="90"}})!;
        var enteredPath=JsonNode.Parse(pathValue.GetRawText())!.AsObject();enteredPath["limits"]=EnteredLimits();
        revisionBody["agreement"]=EnteredLimits();revisionBody["paths"]=new JsonArray(enteredPath);
        revisionBody["receipts"]=new JsonArray(new JsonObject{["point"]="synthetic_a",["limits"]=EnteredLimits()});
        revisionBody["deliveries"]=new JsonArray(new JsonObject{["point"]="synthetic_b",["limits"]=EnteredLimits()});
        revisionBody["evidence"]="Synthetic explicitly revised limits";
        var command=GasTransportCapacityCommands.Normalize(Id(20),Json(revisionBody));
        GasTransportCapacity.Assess(GasTransportCapacityAssessments.Definition(Id(20),command),[]).RequireFeasible();
        if(command.Id("bookId")!=Id(3)||command.GetProperty("paths")[0].Id("pathId")!=Path)
            throw new Exception("The described revision must produce the ordinary command while retaining exact immutable ownership and path identity.");
        var plan=saved.Forms["plan"];var previous=Field(plan,"previous");var claims=Field(plan,"claims");
        if(plan.Permission!="RecordGasOperations"||plan.Base.Id("versionId")!=Id(11)||plan.Base.TryGetProperty("expectedPreviousId",out _)||
            plan.Base.Text("admissionPolicy")!=GasTransportCapacityPlanAdmission.FeasibleReplacement||!plan.Label.Contains("feasible",StringComparison.OrdinalIgnoreCase)||
            previous.Kind!="reference"||!previous.Nullable||previous.Members.Single()!="expectedPreviousId"||Json(previous.Choices.Single().Value).Id("expectedPreviousId")!=Id(13)||claims.MinItems!=0)
            throw new Exception("A chosen plan explicitly selects a previous head or none, and an empty claim array releases only that plan.");
        if(claims.Fields.Single(f=>f.Name=="pathId").Choices.Single().Value.ToString()!=Path.ToString()||
            !new[]{"receiptFrom","receiptTo","receiptQuantity","deliveryFrom","deliveryTo","deliveryQuantity"}.All(name=>claims.Fields.Any(f=>f.Name==name)))
            throw new Exception("Plan claims must retain one exact path plus separate entered receipt and delivery stages.");
        var selectedPlan=Field(release,"plan");var selectedHead=Json(selectedPlan.Choices.Single().Value);
        if(release.Permission!="RecordGasOperations"||release.Base.Text("kind")!="plan"||release.Base.Text("admissionPolicy")!=GasTransportCapacityPlanAdmission.EmptyRelease||
            release.Base.Id("entitlementId")!=Id(10)||release.Base.Id("versionId")!=Id(11)||release.Base.GetProperty("expectedStateVersion").GetInt32()!=7||
            release.Base.GetProperty("claims").GetArrayLength()!=0||selectedPlan.Nullable||selectedPlan.Kind!="reference"||
            !selectedPlan.Members.Order().SequenceEqual(new[]{"businessKey","expectedPreviousId"}.Order())||selectedHead.Id("expectedPreviousId")!=Id(13)||
            selectedHead.Text("businessKey")!="synthetic chosen plan"||release.Fields.Any(f=>f.Name is "claims" or "previous" or "businessKey"))
            throw new Exception("Explicit release must select one exact existing plan identity/head and contain no editable or inferred claims.");
        var releaseBody=JsonNode.Parse(release.Base.GetRawText())!.AsObject();
        foreach(var property in selectedHead.EnumerateObject())releaseBody[property.Name]=JsonNode.Parse(property.Value.GetRawText());
        releaseBody["evidence"]="Synthetic explicit empty release";
        var releaseCommand=GasTransportCapacityCommands.Normalize(Id(21),Json(releaseBody));
        if(releaseCommand.Text("admissionPolicy")!=GasTransportCapacityPlanAdmission.EmptyRelease||releaseCommand.GetProperty("claims").GetArrayLength()!=0)
            throw new Exception("The explicit described release must produce the ordinary closed plan command without new claims.");
        var missing=JsonNode.Parse(facts.GetRawText())!.AsObject();missing["version"]!.AsObject().Remove("reference_revision_id");
        Refuse(()=>GasTransportCapacityMetadata.Build(Json(missing),Choices()));
        var parent=JsonNode.Parse(facts.GetRawText())!.AsObject();parent["version"]!["entitlement_id"]=Id(99);
        Refuse(()=>GasTransportCapacityMetadata.Build(Json(parent),Choices()));
        Console.WriteLine("PASS Transport capacity metadata: explicit independent references/laws, disjoint tuples, immutable revisions, complete dated limits and chosen-plan release");
    }
}
