using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

internal static class GasTransportCapacityAdapterChecks
{
    static Guid Id(int n)=>Guid.Parse($"71111111-1111-1111-1111-{n:000000000000}");
    static readonly Guid Entitlement=Id(1),Version=Id(2),Path=Id(3),Reference=Id(4),Revision=Id(5),PriorPlan=Id(6),OtherPlan=Id(7);
    const string From="2032-03-14T08:00:00Z",Through="2032-03-14T09:00:00Z";
    static JsonObject Obj(object value)=>JsonSerializer.SerializeToNode(value)!.AsObject();
    static JsonElement E(JsonNode value)=>JsonSerializer.SerializeToElement(value);
    static object[] Limits(string amount="100")=>[new{from=From,through=Through,maximumPerHour=amount}];
    static JsonObject Define()=>Obj(new{kind="define",entitlementId=(Guid?)null,expectedStateVersion=0,expectedPreviousId=(Guid?)null,
        businessKey="synthetic entitlement",referenceCatalogId=Reference,referenceRevisionId=Revision,bookId=Id(8),legalEntityId=Id(9),legalEntityRevision=1,
        counterpartyId=Id(10),counterpartyRevision=1,product="synthetic_gas",quantity="synthetic_native_quantity",from=From,through=Through,
        implementation=GasTransportCapacityProfile.Version,rateBasis=GasTransportCapacityProfile.ElapsedHour,flowShape=GasTransportCapacityProfile.UniformStages,
        agreementBasis=GasTransportCapacityProfile.GrossReceipts,deliveryBasis=GasTransportCapacityProfile.GrossDeliveries,agreement=Limits(),
        paths=new[]{new{pathKey="synthetic direct path",pipeline="synthetic_pipeline",receiptZone="synthetic_zone",receiptPoint="synthetic_receipt",
            deliveryZone="synthetic_zone",deliveryPoint="synthetic_delivery",product="synthetic_gas",limits=Limits()}},
        receipts=new[]{new{point="synthetic_receipt",limits=Limits()}},deliveries=new[]{new{point="synthetic_delivery",limits=Limits()}},evidence="Synthetic capacity terms"});
    static JsonObject Claim(string quantity)=>Obj(new{pathId=Path,receiptFrom=From,receiptTo=Through,receiptQuantity=quantity,
        deliveryFrom=From,deliveryTo=Through,deliveryQuantity=quantity});
    static JsonObject Plan(string quantity="40")=>new(){["kind"]="plan",["entitlementId"]=Entitlement,["expectedStateVersion"]=12,
        ["expectedPreviousId"]=PriorPlan,["businessKey"]="synthetic plan",["versionId"]=Version,["admissionPolicy"]=GasTransportCapacityPlanAdmission.FeasibleReplacement,
        ["claims"]=new JsonArray(Claim(quantity)),["evidence"]="Synthetic chosen capacity use"};
    static JsonObject NativeClaim(Guid plan,int n,string quantity)=>Obj(new{id=Id(n),plan_id=plan,path_id=Path,
        receipt_from=From,receipt_to=Through,receipt_quantity=quantity,delivery_from=From,delivery_to=Through,delivery_quantity=quantity});
    static JsonObject Facts()=>new(){["entitlement"]=Obj(new{id=Entitlement,business_key="synthetic entitlement"}),["stateVersion"]=12,["hasFutureHeads"]=false,
        ["version"]=Obj(new{id=Version,entitlement_id=Entitlement,reference_catalog_id=Reference,reference_revision_id=Revision,
            book_id=Id(8),legal_entity_id=Id(9),legal_entity_revision=1,counterparty_id=Id(10),counterparty_revision=1,product_code="synthetic_gas",
            quantity_code="synthetic_native_quantity",from_at=From,through_at=Through,implementation=GasTransportCapacityProfile.Version,
            rate_basis=GasTransportCapacityProfile.ElapsedHour,flow_shape=GasTransportCapacityProfile.UniformStages,
            agreement_basis=GasTransportCapacityProfile.GrossReceipts,delivery_basis=GasTransportCapacityProfile.GrossDeliveries}),
        ["paths"]=new JsonArray(Obj(new{id=Path,path_key="synthetic direct path",pipeline_code="synthetic_pipeline",receipt_zone_code="synthetic_zone",
            receipt_point_code="synthetic_receipt",delivery_zone_code="synthetic_zone",delivery_point_code="synthetic_delivery",product_code="synthetic_gas",limits=NativeLimits()})),
        ["agreement"]=JsonSerializer.SerializeToNode(NativeLimits()),
        ["receipts"]=new JsonArray(Obj(new{point_code="synthetic_receipt",limits=NativeLimits()})),
        ["deliveries"]=new JsonArray(Obj(new{point_code="synthetic_delivery",limits=NativeLimits()})),
        ["plans"]=new JsonArray(Obj(new{id=PriorPlan,business_key="synthetic plan",revision=1,previous_id=(Guid?)null,version_id=Version}),
            Obj(new{id=OtherPlan,business_key="synthetic other plan",revision=1,previous_id=(Guid?)null,version_id=Version})),
        ["claims"]=new JsonArray(NativeClaim(PriorPlan,20,"60"),NativeClaim(OtherPlan,21,"50"))};
    static object[] NativeLimits()=>[new{from=From,through=Through,maximum_per_hour="100"}];

    internal static void Run()
    {
        var operation=Id(30);var input=Define();var canonical=GasTransportCapacityCommands.Normalize(operation,E(input));
        var repeated=GasTransportCapacityCommands.Normalize(Id(31),E(Define()));
        if(canonical.Id("entitlementId")!=repeated.Id("entitlementId")||canonical.GetProperty("paths")[0].Id("pathId")!=repeated.GetProperty("paths")[0].Id("pathId"))
            throw new Exception("Fresh identical definition requests duplicated an entitlement or path identity.");
        Same(canonical,GasTransportCapacityCommands.Normalize(operation,canonical));
        GasTransportCapacity.Assess(GasTransportCapacityAssessments.Definition(operation,canonical),[]).RequireFeasible();
        var forged=Define();forged["entitlementId"]=Id(99);Reject(()=>GasTransportCapacityCommands.Normalize(operation,E(forged)));
        var absent=Define();absent.Remove("agreementBasis");Reject(()=>GasTransportCapacityCommands.Normalize(operation,E(absent)));
        var extra=Define();extra["inferFuel"]=true;Reject(()=>GasTransportCapacityCommands.Normalize(operation,E(extra)));
        var duplicates=canonical.GetRawText().Replace("\"kind\":\"define\"","\"kind\":\"define\",\"kind\":\"define\"",StringComparison.Ordinal);
        using(var doc=JsonDocument.Parse(duplicates))Reject(()=>GasTransportCapacityCommands.Normalize(operation,doc.RootElement));
        var submicro=Plan();submicro["claims"]![0]!["receiptFrom"]="2032-03-14T08:00:00.0000001Z";Reject(()=>GasTransportCapacityCommands.Normalize(operation,E(submicro)));
        var negative=Plan();negative["claims"]![0]!["receiptQuantity"]="-1";Reject(()=>GasTransportCapacityCommands.Normalize(operation,E(negative)));
        var tooLarge=Plan();tooLarge["claims"]![0]!["receiptQuantity"]="1000000000000000000000000";Reject(()=>GasTransportCapacityCommands.Normalize(operation,E(tooLarge)));
        var largeLimit=Define();largeLimit["agreement"]![0]!["maximumPerHour"]="1000000000000000000000000";Reject(()=>GasTransportCapacityCommands.Normalize(operation,E(largeLimit)));
        var unicode=Define();unicode["businessKey"]=string.Concat(Enumerable.Repeat("\U0001f30a",128));
        GasTransportCapacityCommands.Normalize(operation,E(unicode));unicode["businessKey"]=unicode["businessKey"]!.GetValue<string>()+"x";Reject(()=>GasTransportCapacityCommands.Normalize(operation,E(unicode)));
        var control=Define();control["businessKey"]="synthetic\nkey";Reject(()=>GasTransportCapacityCommands.Normalize(operation,E(control)));

        var missingPolicy=Plan();missingPolicy.Remove("admissionPolicy");Reject(()=>GasTransportCapacityCommands.Normalize(operation,E(missingPolicy)));
        var unsupportedPolicy=Plan();unsupportedPolicy["admissionPolicy"]="infer_safe_change";Reject(()=>GasTransportCapacityCommands.Normalize(operation,E(unsupportedPolicy)));
        unsupportedPolicy["admissionPolicy"]=null;Reject(()=>GasTransportCapacityCommands.Normalize(operation,E(unsupportedPolicy)));

        var facts=Facts();var plan=GasTransportCapacityCommands.Normalize(operation,E(Plan()));
        var result=GasTransportCapacityAssessments.Plan(E(facts),plan).RequireFeasible();
        var agreement=result.Intervals.Single(i=>i.Axis=="agreement");
        if(agreement.UsedPerHour.ToExactString()!="90"||agreement.Uses.Length!=2)
            throw new Exception("Replacing own60 by40 must preserve the other plan50, yielding exact gross90.");
        var over=GasTransportCapacityCommands.Normalize(operation,E(Plan("60")));
        if(GasTransportCapacityAssessments.Plan(E(facts),over).Feasible)throw new Exception("Other plans disappeared from the complete shared capacity scope.");
        var empty=Plan();empty["claims"]=new JsonArray();
        var released=GasTransportCapacityAssessments.Plan(E(facts),GasTransportCapacityCommands.Normalize(operation,E(empty))).RequireFeasible();
        if(released.Intervals.Single(i=>i.Axis=="agreement").UsedPerHour.ToExactString()!="50")throw new Exception("Empty replacement must release only its own claims.");
        var stale=Plan();stale["expectedStateVersion"]=11;Reject(()=>GasTransportCapacityAssessments.Plan(E(facts),GasTransportCapacityCommands.Normalize(operation,E(stale))),"gas_transport_capacity_stale");
        var wrong=Plan();wrong["expectedPreviousId"]=OtherPlan;Reject(()=>GasTransportCapacityAssessments.Plan(E(facts),GasTransportCapacityCommands.Normalize(operation,E(wrong))),"gas_transport_capacity_stale");
        var missing=Facts();missing["plans"]!.AsArray().RemoveAt(1);Reject(()=>GasTransportCapacityAssessments.Current(E(missing)),"gas_transport_capacity_source");
        var missingFuture=Facts();missingFuture.Remove("hasFutureHeads");Reject(()=>GasTransportCapacityAssessments.Plan(E(missingFuture),plan),"gas_transport_capacity_source");
        Reject(()=>GasTransportCapacityAssessments.Current(E(missingFuture)),"gas_transport_capacity_source");
        var future=Facts();future["hasFutureHeads"]=true;Reject(()=>GasTransportCapacityAssessments.Plan(E(future),plan),"gas_transport_capacity_future_source");
        var futureDefinition=Facts();futureDefinition["hasFutureHeads"]=true;futureDefinition["version"]=null;
        Reject(()=>GasTransportCapacityAssessments.Plan(E(futureDefinition),plan),"gas_transport_capacity_future_source");
        Reject(()=>GasTransportCapacityAssessments.Current(E(futureDefinition)),"gas_transport_capacity_future_source");
        var definition=Define();definition["entitlementId"]=Entitlement;definition["expectedStateVersion"]=12;definition["expectedPreviousId"]=Version;
        definition["paths"]![0]!["pathId"]=Path;
        foreach(var field in new[]{"agreement","paths","receipts","deliveries"})
        {
            var limits=field=="agreement"?definition[field]!:definition[field]![0]!["limits"]!;
            limits[0]!["maximumPerHour"]="50";
        }
        var old=E(facts);var decreased=GasTransportCapacityAssessments.Revision(Id(40),E(definition),old);
        if(decreased.Feasible||decreased.VersionId!=Id(40)||decreased.Intervals.Length!=4||decreased.Intervals.Any(i=>i.WithinLimit||i.UsedPerHour.ToExactString()!="110"||i.MaximumPerHour.ToExactString()!="50"||i.Uses.Length!=2))
            throw new Exception("A capacity decrease to50 must retain every current gross claim110 and expose its breach without clipping, removing a plan or blocking the definition assessment.");
        Same(old,E(facts));
        var changed=definition.DeepClone().AsObject();changed["expectedStateVersion"]=11;
        Reject(()=>GasTransportCapacityAssessments.Revision(Id(40),E(changed),old),"gas_transport_capacity_stale");
        changed=definition.DeepClone().AsObject();changed["entitlementId"]=Id(99);
        Reject(()=>GasTransportCapacityAssessments.Revision(Id(40),E(changed),old),"gas_transport_capacity_stale");
        changed=definition.DeepClone().AsObject();changed["expectedPreviousId"]=OtherPlan;
        Reject(()=>GasTransportCapacityAssessments.Revision(Id(40),E(changed),old),"gas_transport_capacity_stale");
        Reject(()=>GasTransportCapacityAssessments.Revision(Id(40),E(definition),E(missingFuture)),"gas_transport_capacity_source");
        Reject(()=>GasTransportCapacityAssessments.Revision(Id(40),E(definition),E(future)),"gas_transport_capacity_future_source");
        foreach(var field in new[]{"bookId","referenceRevisionId","legalEntityId","counterpartyId"})
        {
            changed=definition.DeepClone().AsObject();changed[field]=Id(99);
            Reject(()=>GasTransportCapacityAssessments.Revision(Id(40),E(changed),old));
        }
        changed=definition.DeepClone().AsObject();changed["quantity"]="other_native_basis";
        Reject(()=>GasTransportCapacityAssessments.Revision(Id(40),E(changed),old));
        changed=definition.DeepClone().AsObject();changed["paths"]![0]!["pipeline"]="other_pipeline";
        Reject(()=>GasTransportCapacityAssessments.Revision(Id(40),E(changed),old));
        changed=definition.DeepClone().AsObject();changed["paths"]![0]!["pathKey"]="other path identity";
        Reject(()=>GasTransportCapacityAssessments.Revision(Id(40),E(changed),old));
        changed=definition.DeepClone().AsObject();changed["paths"]![0]!["receiptPoint"]="other_point";
        Reject(()=>GasTransportCapacityAssessments.Revision(Id(40),E(changed),old));
        changed=definition.DeepClone().AsObject();changed["through"]="2032-03-14T10:00:00Z";
        Reject(()=>GasTransportCapacityAssessments.Revision(Id(40),E(changed),old));
        var witness=GasTransportCapacityAssessments.Witness(result,GasTransportCapacityPlanAdmission.FeasibleReplacement);
        if(witness.GetProperty("versionId").GetGuid()!=Version||witness.GetProperty("intervals").GetArrayLength()!=4)
            throw new Exception("Native witness lost the exact version or a shared capacity axis.");
        if(witness.Text("admissionPolicy")!=GasTransportCapacityPlanAdmission.FeasibleReplacement)throw new Exception("A capacity witness must retain its explicit admission law.");
        if(E(facts).GetProperty("claims")[0].Text("receipt_quantity")!="60")throw new Exception("Plan adaptation mutated retained physical evidence.");

        // A coherent capacity decrease must not trap two independent plans.
        // Ordinary replacement still requires complete feasibility; the explicit
        // empty-release policy removes only its own claims and reports the rest.
        var stressed=Facts();stressed["claims"]![1]!["receipt_quantity"]="60";stressed["claims"]![1]!["delivery_quantity"]="60";
        foreach(var field in new[]{"agreement","paths","receipts","deliveries"})
        {
            var limits=field=="agreement"?stressed[field]!:stressed[field]![0]!["limits"]!;
            limits[0]!["maximum_per_hour"]="120";
        }
        if(GasTransportCapacityAssessments.Current(E(stressed)).RequireFeasible().Intervals.Any(i=>i.UsedPerHour.ToExactString()!="120"))
            throw new Exception("The cleanup oracle begins with two independently identified60-unit plans fully admitted under120 capacity.");
        foreach(var field in new[]{"agreement","paths","receipts","deliveries"})
        {
            var limits=field=="agreement"?stressed[field]!:stressed[field]![0]!["limits"]!;
            limits[0]!["maximum_per_hour"]="50";
        }
        var unchanged=E(stressed);var ordinaryEmpty=Plan();ordinaryEmpty["claims"]=new JsonArray();
        var ordinaryAssessment=GasTransportCapacityAssessments.Plan(unchanged,GasTransportCapacityCommands.Normalize(Id(50),E(ordinaryEmpty)));
        Reject(()=>GasTransportCapacityAssessments.Witness(ordinaryAssessment,GasTransportCapacityPlanAdmission.FeasibleReplacement),"gas_transport_capacity_exceeded");
        var releaseA=ordinaryEmpty.DeepClone().AsObject();releaseA["admissionPolicy"]=GasTransportCapacityPlanAdmission.EmptyRelease;
        var canonicalRelease=GasTransportCapacityCommands.Normalize(Id(50),E(releaseA));
        var remaining=GasTransportCapacityAssessments.Plan(unchanged,canonicalRelease);
        if(remaining.Feasible||remaining.Intervals.Any(i=>i.UsedPerHour.ToExactString()!="60"||i.MaximumPerHour.ToExactString()!="50"||!i.Uses.SequenceEqual([Id(21)])))
            throw new Exception("Explicit release of A must leave B60 visible and breached under50, with no clipped or rescheduled claims.");
        var releaseWitness=GasTransportCapacityAssessments.Witness(remaining,GasTransportCapacityPlanAdmission.EmptyRelease);
        if(releaseWitness.Text("admissionPolicy")!=GasTransportCapacityPlanAdmission.EmptyRelease||releaseWitness.GetProperty("intervals").EnumerateArray().Any(i=>i.GetProperty("withinLimit").GetBoolean()))
            throw new Exception("An empty-release witness must retain computed breaches instead of inventing feasible booleans.");
        Same(unchanged,E(stressed));
        var after=stressed.DeepClone().AsObject();after["stateVersion"]=13;after["plans"]![0]!["id"]=Id(50);after["plans"]![0]!["previous_id"]=PriorPlan;
        after["plans"]![0]!["revision"]=2;after["claims"]!.AsArray().RemoveAt(0);
        var releaseB=releaseA.DeepClone().AsObject();releaseB["businessKey"]="synthetic other plan";releaseB["expectedPreviousId"]=OtherPlan;releaseB["expectedStateVersion"]=13;
        var cleared=GasTransportCapacityAssessments.Plan(E(after),GasTransportCapacityCommands.Normalize(Id(51),E(releaseB))).RequireFeasible();
        if(cleared.Intervals.Any(i=>i.UsedPerHour.ToExactString()!="0"||i.Uses.Length!=0)||after["claims"]![0]!["receipt_quantity"]!.GetValue<string>()!="60")
            throw new Exception("Releasing B after A must clear the scope while retaining the untouched original B facts.");
        var nonemptyRelease=Plan();nonemptyRelease["admissionPolicy"]=GasTransportCapacityPlanAdmission.EmptyRelease;
        Reject(()=>GasTransportCapacityCommands.Normalize(Id(52),E(nonemptyRelease)));
        Reject(()=>GasTransportCapacityAssessments.Plan(unchanged,E(nonemptyRelease)));
        var noPrevious=releaseA.DeepClone().AsObject();noPrevious["expectedPreviousId"]=null;
        Reject(()=>GasTransportCapacityCommands.Normalize(Id(52),E(noPrevious)));
        var wrongPlan=releaseA.DeepClone().AsObject();wrongPlan["expectedPreviousId"]=OtherPlan;
        Reject(()=>GasTransportCapacityAssessments.Plan(unchanged,GasTransportCapacityCommands.Normalize(Id(52),E(wrongPlan))),"gas_transport_capacity_stale");
        Reject(()=>GasTransportCapacityAssessments.Witness(remaining,"infer_safe_change"));
        Console.WriteLine("PASS Transport capacity API: explicit canonical definitions, stable identities, opaque keys, exact shared-plan replacement and four-axis witness");
    }
    static void Same(JsonElement a,JsonElement b){if(!JsonElement.DeepEquals(a,b))throw new Exception("Canonical retry changed terms.");}
    static void Reject(Action work,string code="gas_transport_capacity_input")
    {try{work();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
