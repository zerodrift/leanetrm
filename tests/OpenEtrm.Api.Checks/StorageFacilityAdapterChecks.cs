using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Physical;

internal static class StorageFacilityAdapterChecks
{
    static readonly Guid Catalog=Guid.Parse("61111111-1111-1111-1111-111111111111"),Reference=Guid.Parse("62222222-2222-2222-2222-222222222222"),
        ReferenceVersion=Guid.Parse("63333333-3333-3333-3333-333333333333"),Facility=Guid.Parse("64444444-4444-4444-4444-444444444444"),
        Version=Guid.Parse("65555555-5555-5555-5555-555555555555"),Port=Guid.Parse("66666666-6666-6666-6666-666666666666"),
        Opening=Guid.Parse("67777777-7777-7777-7777-777777777777"),Activity=Guid.Parse("68888888-8888-8888-8888-888888888888"),
        Coverage=Guid.Parse("69999999-9999-9999-9999-999999999999");
    const string From="2032-03-14T08:00:00Z",Through="2032-03-14T09:00:00Z";
    static JsonElement E(JsonNode value)=>JsonSerializer.SerializeToElement(value);
    static JsonObject Object(object value)=>JsonSerializer.SerializeToNode(value)!.AsObject();
    static JsonObject Define()=>Object(new{kind="define",facilityId=(Guid?)null,expectedStateVersion=0,expectedPreviousId=(Guid?)null,
        referenceCatalogId=Reference,referenceRevisionId=ReferenceVersion,facilityCode="synthetic_storage",commodity="synthetic_gas",quantity="synthetic_native_quantity",clock="synthetic_clock",
        minimum="0.000",maximum="1000",implementation=StorageFacilityRules.Version,inventoryBasis=StorageFacilityRules.TotalPhysical,
        rateBasis=StorageFacilityRules.ElapsedHour,simultaneousFlow=StorageFacilityRules.SeparateDirections,lossTreatment=StorageFacilityRules.NoLoss,
        ports=new[]{new{pipeline="synthetic_pipeline",zone="synthetic_zone",point="synthetic_z",product="synthetic_gas",direction="inject",contractDirection=1},
            new{pipeline="synthetic_pipeline",zone="synthetic_zone",point="synthetic_a",product="synthetic_gas",direction="withdraw",contractDirection=-1}},
        ratchets=new[]{new{lower="0",upper="500",injectionPerHour="100",withdrawalPerHour="100"},new{lower="500",upper="1000",injectionPerHour="25",withdrawalPerHour="100"}},
        evidence="Synthetic physical rule evidence"});
    static JsonObject Plan()=>Object(new{kind="plan",facilityId=Facility,expectedStateVersion=12,expectedPreviousId=(Guid?)null,
        versionId=Version,openingRevisionId=Opening,from=From,through=Through,coverageStatement=StorageFacilityCoverage.ChosenPlan,
        activities=new[]{new{portId=Port,direction="inject",from=From,through=Through,quantity="10.000"},
            new{portId=Port,direction="inject",from=From,through="2032-03-14T08:20:00Z",quantity="1"}},evidence="Synthetic chosen plan"});
    static JsonObject NativeVersion(Guid id,string effective="2032-03-01T00:00:00Z")=>Object(new{id,catalog_id=Catalog,facility_id=Facility,revision=1,
        reference_catalog_id=Reference,reference_revision_id=ReferenceVersion,quantity_code="synthetic_native_quantity",minimum="0",maximum="1000",
        implementation=StorageFacilityRules.Version,inventory_basis=StorageFacilityRules.TotalPhysical,rate_basis=StorageFacilityRules.ElapsedHour,
        simultaneous_flow=StorageFacilityRules.SeparateDirections,loss_treatment=StorageFacilityRules.NoLoss,effective_at=effective});
    static JsonObject NativePort()=>Object(new{id=Port,catalog_id=Catalog,version_id=Version,direction="inject"});
    static JsonArray Bands()=>new(Object(new{ordinal=0,lower="0",upper="500",injectionPerHour="100",withdrawalPerHour="100"}),
        Object(new{ordinal=1,lower="500",upper="1000",injectionPerHour="25",withdrawalPerHour="100"}));
    static JsonObject NativeCoverage()=>Object(new{id=Coverage,facility_id=Facility,business_key="synthetic_window",revision=1,
        version_id=Version,opening_revision_id=(Guid?)Opening,from_at=From,through_at=Through,statement=StorageFacilityCoverage.CompleteActual,
        activityRevisionIds=new[]{Activity}});
    static JsonObject Facts()=>new(){
        ["asOf"]="2032-03-14T20:00:00Z",["knownAt"]="2032-03-15T00:00:00Z",
        ["facility"]=Object(new{id=Facility,catalog_id=Catalog}),["stateVersion"]=12,["version"]=NativeVersion(Version),
        ["knownRuleVersions"]=new JsonArray(Object(new{id=Version,effective_at="2032-03-01T00:00:00Z",revision=1})),
        ["ports"]=new JsonArray(NativePort()),["ratchets"]=Bands(),
        ["versionBundles"]=new JsonArray(new JsonObject{["version"]=NativeVersion(Version),["ports"]=new JsonArray(NativePort()),["ratchets"]=Bands()}),
        ["opening"]=Object(new{id=Opening,facility_id=Facility,business_key="synthetic_level",revision=1,observed_at=From,quantity="100"}),
        ["openings"]=new JsonArray(Object(new{id=Opening,facility_id=Facility,business_key="synthetic_level",revision=1,observed_at=From,quantity="100"})),
        ["activities"]=new JsonArray(Object(new{id=Activity,facility_id=Facility,business_key="synthetic_flow",revision=1,version_id=Version,port_id=Port,
            from_at=From,through_at=Through,direction="inject",quantity="100",source_revision_id=(Guid?)null,ledger_id=(Guid?)null,sourceCorrected=false})),
        ["coverage"]=NativeCoverage(),["coverages"]=new JsonArray(NativeCoverage()),["plan"]=null,["linkedSourceCorrection"]=false};

    internal static void Run()
    {
        var operation=Guid.NewGuid();var definition=Define();
        var futureActual=Facts();futureActual["asOf"]="2032-03-14T16:00:00Z";
        var futureWindow=NativeCoverage();futureWindow["through_at"]="2032-03-14T18:00:00Z";
        futureActual["coverage"]=futureWindow.DeepClone();futureActual["coverages"]=new JsonArray(futureWindow.DeepClone());
        Unassessed(futureActual,futureWindow,"effective");
        if(futureActual["coverage"]!["through_at"]!.GetValue<string>()!="2032-03-14T18:00:00Z"||futureActual["activities"]![0]!["quantity"]!.GetValue<string>()!="100")
            throw new Exception("Refusing future measured coverage must retain its raw reporting and activity evidence.");
        var futureKnowledge=Facts();futureKnowledge["knownAt"]="2032-03-14T16:00:00Z";
        futureKnowledge["coverage"]=futureWindow.DeepClone();futureKnowledge["coverages"]=new JsonArray(futureWindow.DeepClone());Unassessed(futureKnowledge,futureWindow,"knowledge");
        var missingEffective=Facts();missingEffective.Remove("asOf");Unassessed(missingEffective,NativeCoverage());
        var missingKnown=Facts();missingKnown.Remove("knownAt");Unassessed(missingKnown,NativeCoverage());
        var atWindowEnd=Facts();atWindowEnd["asOf"]=Through;atWindowEnd["knownAt"]=Through;
        if(StorageFacilityAssessments.Measured(E(atWindowEnd),E(NativeCoverage())).Assessment?.ClosingLevel?.ToExactString()!="200")
            throw new Exception("An observed window ending exactly at both explicit source horizons must remain assessable.");
        var prospective=Facts();prospective["asOf"]="2032-03-14T08:30:00Z";prospective["knownAt"]="2032-03-14T08:30:00Z";
        prospective["activities"]=new JsonArray();
        if(StorageFacilityAssessments.Plan(E(prospective),StorageFacilityCommands.Normalize(operation,E(Plan()))).RequireFeasible().ClosingLevel?.ToExactString()!="111")
            throw new Exception("A chosen conditional plan must retain its prospective horizon beyond known actual evidence.");
        var spaced=Object(new{kind="opening",facilityId=Facility,expectedStateVersion=12,expectedPreviousId=(Guid?)null,
            businessKey=" synthetic_level ",observedAt=From,quantity="10.000",evidence=" Synthetic dated evidence "});
        var nativeStatusCommand=Object(new{kind="opening",facilityId=Facility,expectedStateVersion=12,expectedPreviousId=(Guid?)null,
            businessKey="synthetic_level",observedAt="2032-03-14T08:00:00.000000Z",quantity="10",evidence="Synthetic dated evidence"});
        var reviewed=StorageFacilityCommands.Normalize(operation,E(spaced));
        Same(E(nativeStatusCommand),reviewed,"A reviewed command differs from the native-style recovered status after ASCII-space canonicalization.");
        Same(reviewed,StorageFacilityCommands.Normalize(operation,E(nativeStatusCommand)),"A native-style recovered command changed its reviewed identity.");
        var unicodeEvidence=Define();unicodeEvidence["evidence"]="\u00a0Synthetic dated evidence\u00a0";
        if(StorageFacilityCommands.Normalize(operation,E(unicodeEvidence)).GetProperty("evidence").GetString()!=unicodeEvidence["evidence"]!.GetValue<string>())
            throw new Exception("Evidence canonicalization applied a Unicode trim not present in the native ASCII-space contract.");
        var beforeRules=Facts();beforeRules["version"]!["effective_at"]="2032-03-14T08:30:00Z";
        beforeRules["versionBundles"]![0]!["version"]!["effective_at"]="2032-03-14T08:30:00Z";
        beforeRules["knownRuleVersions"]![0]!["effective_at"]="2032-03-14T08:30:00Z";
        Reject(()=>StorageFacilityAssessments.Plan(E(beforeRules),StorageFacilityCommands.Normalize(operation,E(Plan()))),"storage_facility_rule_transition");
        Unassessed(beforeRules,NativeCoverage());
        var canonical=StorageFacilityCommands.Normalize(operation,E(definition));
        var repeatedDefinition=StorageFacilityCommands.Normalize(Guid.NewGuid(),E(Define()));
        if(canonical.GetProperty("facilityId").GetGuid()!=repeatedDefinition.GetProperty("facilityId").GetGuid())
            throw new Exception("A fresh command for the same physical facility business key must retain its stable identity so an identical fact can be acknowledged.");
        if(canonical.GetProperty("facilityId").GetGuid()==Guid.Empty||canonical.GetProperty("minimum").GetString()!="0")
            throw new Exception("Only technical new-facility identity and exact decimal formatting may be supplied by normalization.");
        var reversed=Define();reversed["ports"]=new JsonArray(reversed["ports"]!.AsArray().Reverse().Select(p=>p!.DeepClone()).ToArray());
        Same(canonical,StorageFacilityCommands.Normalize(operation,E(reversed)),"Unordered port entry changed exact command identity.");
        Same(canonical,StorageFacilityCommands.Normalize(operation,canonical),"A reviewed canonical definition changed on its exact retry.");
        var missing=Define();missing.Remove("lossTreatment");Reject(()=>StorageFacilityCommands.Normalize(operation,E(missing)));
        var unknown=Define();unknown["passed"]=true;Reject(()=>StorageFacilityCommands.Normalize(operation,E(unknown)));
        var forgedFacility=Define();forgedFacility["facilityId"]=Guid.NewGuid();Reject(()=>StorageFacilityCommands.Normalize(operation,E(forgedFacility)));
        var unsupportedText=Define();unsupportedText["evidence"]="Synthetic\0evidence";Reject(()=>StorageFacilityCommands.Normalize(operation,E(unsupportedText)));
        var tooLong=Define();tooLong["evidence"]=new string('a',501);Reject(()=>StorageFacilityCommands.Normalize(operation,E(tooLong)));
        var badCode=Define();badCode["commodity"]="Uninstalled Code";Reject(()=>StorageFacilityCommands.Normalize(operation,E(badCode)));
        var wrongId=JsonNode.Parse(canonical.GetRawText())!.AsObject();wrongId["ports"]![0]!["portId"]=Guid.NewGuid();Reject(()=>StorageFacilityCommands.Normalize(operation,E(wrongId)));
        var candidate=StorageFacilityAssessments.Definition(operation,canonical);StorageFacilityOperations.ValidateRules(candidate);
        var gapped=Define();gapped["ratchets"]![1]!["lower"]="501";
        Reject(()=>StorageFacilityOperations.ValidateRules(StorageFacilityAssessments.Definition(operation,StorageFacilityCommands.Normalize(operation,E(gapped)))));

        var plan=Plan();var normalized=StorageFacilityCommands.Normalize(operation,E(plan));
        if(normalized.GetProperty("planId").GetGuid()!=operation)throw new Exception("A plan requires its retained operation-scoped technical coverage identity.");
        var other=Plan();other["activities"]=new JsonArray(other["activities"]!.AsArray().Reverse().Select(a=>a!.DeepClone()).ToArray());
        other["from"]="2032-03-14T08:00:00.0000000+00:00";other["activities"]![0]!["quantity"]="1.000";
        Same(normalized,StorageFacilityCommands.Normalize(operation,E(other)),"Reordering movements or equivalent UTC/decimal notation changed an exact plan retry.");
        Same(normalized,StorageFacilityCommands.Normalize(operation,normalized),"A reviewed canonical plan changed on retry.");
        var repeated=Plan();repeated["activities"]!.AsArray().Add(repeated["activities"]![0]!.DeepClone());
        var repeatedCanonical=StorageFacilityCommands.Normalize(operation,E(repeated));
        var reverseRepeated=JsonNode.Parse(repeatedCanonical.GetRawText())!.AsObject();
        reverseRepeated["activities"]=new JsonArray(reverseRepeated["activities"]!.AsArray().Reverse().Select(a=>a!.DeepClone()).ToArray());
        Same(repeatedCanonical,StorageFacilityCommands.Normalize(operation,E(reverseRepeated)),"Identical flows lost multiplicity or stable technical identities on reordered retry.");
        var absent=Plan();absent.Remove("coverageStatement");Reject(()=>StorageFacilityCommands.Normalize(operation,E(absent)));
        var partialTick=Plan();partialTick["from"]="2032-03-14T08:00:00.0000001Z";Reject(()=>StorageFacilityCommands.Normalize(operation,E(partialTick)));
        var coverageCommand=Object(new{kind="coverage",facilityId=Facility,expectedStateVersion=12,businessKey="synthetic_window",expectedPreviousId=(Guid?)null,
            versionId=Version,openingRevisionId=(Guid?)Opening,from=From,through=Through,statement=StorageFacilityCoverage.CompleteActual,
            activityRevisionIds=new[]{new{id=Activity}},evidence="Synthetic complete observations"});
        var covered=StorageFacilityCommands.Normalize(operation,E(coverageCommand));
        if(covered.GetProperty("activityRevisionIds")[0].GetGuid()!=Activity)throw new Exception("Renderer coverage rows must become exact native revision identities.");
        var both=Object(new{kind="activity",facilityId=Facility,expectedStateVersion=12,businessKey="synthetic_flow",expectedPreviousId=(Guid?)null,
            versionId=Version,portId=Port,from=From,through=Through,direction="inject",quantity="1",sourceRevisionId=Activity,evidence="Synthetic activity"});
        Reject(()=>StorageFacilityCommands.Normalize(operation,E(both)));
        both["quantity"]=null;both["sourceRevisionId"]=null;Reject(()=>StorageFacilityCommands.Normalize(operation,E(both)));
        both["quantity"]="-1";Reject(()=>StorageFacilityCommands.Normalize(operation,E(both)));
        both["quantity"]="1";both["businessKey"]=new string('a',129);Reject(()=>StorageFacilityCommands.Normalize(operation,E(both)));

        var facts=Facts();var assessed=StorageFacilityAssessments.Plan(E(facts),normalized);assessed.RequireFeasible();
        if(assessed.ClosingLevel?.ToExactString()!="111"||assessed.Intervals[0].Closing.Numerator!="313"||assessed.Intervals[0].Closing.Denominator!="3")
            throw new Exception("Plan adaptation lost independent total111 or exact intermediate313/3 quantity.");
        var witness=StorageFacilityAssessments.Witness(assessed);
        if(witness.GetProperty("coverageRevision").GetGuid()!=operation||witness.GetProperty("closing").GetProperty("n").GetString()!="111"||
            witness.GetProperty("intervals")[0].GetProperty("closing").GetProperty("d").GetString()!="3"||witness.GetProperty("constraint").GetString()!="within_limit")
            throw new Exception("The native witness must retain exact plan coverage and rational operands without rounded crossing instants.");
        var stale=Facts();stale["stateVersion"]=13;Reject(()=>StorageFacilityAssessments.Plan(E(stale),normalized),"storage_facility_stale");
        var knownFuture=Facts();knownFuture["knownRuleVersions"]!.AsArray().Add(Object(new{id=Guid.NewGuid(),effective_at="2032-03-14T08:30:00Z",revision=2}));
        Reject(()=>StorageFacilityAssessments.Plan(E(knownFuture),normalized),"storage_facility_rule_transition");
        var changedAtStart=Facts();changedAtStart["knownRuleVersions"]!.AsArray().Add(Object(new{id=Guid.NewGuid(),effective_at=From,revision=2}));
        Reject(()=>StorageFacilityAssessments.Plan(E(changedAtStart),normalized),"storage_facility_rule_transition");Unassessed(changedAtStart,NativeCoverage());
        var changedAtEnd=Facts();changedAtEnd["knownRuleVersions"]!.AsArray().Add(Object(new{id=Guid.NewGuid(),effective_at=Through,revision=2}));
        if(StorageFacilityAssessments.Plan(E(changedAtEnd),normalized).ClosingLevel?.ToExactString()!="111")
            throw new Exception("A rule beginning at the exclusive window end incorrectly changed the selected plan.");
        var absentRuleKnowledge=Facts();absentRuleKnowledge.Remove("knownRuleVersions");
        Reject(()=>StorageFacilityAssessments.Plan(E(absentRuleKnowledge),normalized));
        var wrongPort=Plan();wrongPort["activities"]![0]!["direction"]="withdraw";
        Reject(()=>StorageFacilityAssessments.Plan(E(facts),StorageFacilityCommands.Normalize(operation,E(wrongPort))),"storage_facility_port");

        // A later current rule must never replace the version pinned by earlier
        // measured coverage. Under the old high band this100/hour flow breaches.
        var measured=Facts();measured["opening"]!["quantity"]="450";measured["openings"]![0]!["quantity"]="450";
        measured["asOf"]="2032-03-17T20:00:00Z";measured["knownAt"]="2032-03-18T00:00:00Z";
        var newer=NativeVersion(Guid.NewGuid(),"2032-03-16T00:00:00Z");newer["revision"]=2;newer["maximum"]="2000";
        var newerPort=NativePort();newerPort["id"]=Guid.NewGuid();newerPort["version_id"]=newer["id"]!.DeepClone();
        measured["ports"]=new JsonArray(newerPort.DeepClone());
        measured["ratchets"]=new JsonArray(Object(new{ordinal=0,lower="0",upper="2000",injectionPerHour="1000",withdrawalPerHour="1000"}));
        measured["version"]=newer.DeepClone();measured["versionBundles"]!.AsArray().Add(new JsonObject{["version"]=newer,
            ["ports"]=new JsonArray(newerPort),["ratchets"]=measured["ratchets"]!.DeepClone()});
        measured["knownRuleVersions"]!.AsArray().Add(Object(new{id=newer["id"]!.GetValue<Guid>(),effective_at="2032-03-16T00:00:00Z",revision=2}));
        var result=StorageFacilityAssessments.Measured(E(measured),E(NativeCoverage()));
        if(result.Constraint!=FacilityConstraintState.Breached||result.Assessment?.ClosingLevel?.ToExactString()!="550"||
            !result.Assessment.Violations.Contains("injection_rate"))throw new Exception("Measured assessment silently used current instead of pinned historical rules.");
        var changedOpening=Facts();changedOpening["openings"]![0]!["id"]=Guid.NewGuid();Unassessed(changedOpening,NativeCoverage());
        var changedFlow=Facts();changedFlow["activities"]![0]!["id"]=Guid.NewGuid();Unassessed(changedFlow,NativeCoverage());
        var addedFlow=Facts();var extraFlow=addedFlow["activities"]![0]!.DeepClone();extraFlow["id"]=Guid.NewGuid();extraFlow["business_key"]="synthetic_new_flow";
        addedFlow["activities"]!.AsArray().Add(extraFlow);Unassessed(addedFlow,NativeCoverage());
        var changedSource=Facts();changedSource["linkedSourceCorrection"]=true;changedSource["activities"]![0]!["sourceCorrected"]=true;Unassessed(changedSource,NativeCoverage());
        var unrelatedCorrection=Facts();unrelatedCorrection["linkedSourceCorrection"]=true;
        var outside=unrelatedCorrection["activities"]![0]!.DeepClone();outside["id"]=Guid.NewGuid();outside["business_key"]="synthetic_other_window";
        outside["from_at"]="2032-03-13T08:00:00Z";outside["through_at"]="2032-03-13T09:00:00Z";outside["sourceCorrected"]=true;
        unrelatedCorrection["activities"]!.AsArray().Add(outside);
        var unaffected=StorageFacilityAssessments.Measured(E(unrelatedCorrection),E(NativeCoverage()));
        if(unaffected.Constraint!=FacilityConstraintState.WithinLimit||unaffected.Assessment?.ClosingLevel?.ToExactString()!="200")
            throw new Exception("A corrected linked flow outside the selected reporting window invalidated its unrelated exact evidence.");
        var missingBundle=Facts();missingBundle["versionBundles"]=new JsonArray();Unassessed(missingBundle,NativeCoverage());
        Unassessed(absentRuleKnowledge,NativeCoverage());
        var noOpening=NativeCoverage();noOpening["opening_revision_id"]=null;Unassessed(Facts(),noOpening);
        var incomplete=NativeCoverage();incomplete["statement"]=StorageFacilityCoverage.IncompleteActual;
        var breachedOpening=Facts();breachedOpening["opening"]!["quantity"]="-5";breachedOpening["openings"]![0]!["quantity"]="-5";
        var incompleteResult=StorageFacilityAssessments.Measured(E(breachedOpening),E(incomplete));
        if(incompleteResult.Constraint!=FacilityConstraintState.Unassessed||incompleteResult.Assessment?.ClosingLevel is not null||
            incompleteResult.Assessment?.Violations.Contains("inventory_minimum")!=true)
            throw new Exception("Incomplete measured coverage lost known opening breach or invented a closing amount.");
        Console.WriteLine("PASS storage API adapters: canonical typed retries, exact version/opening/activity pins, independent rational plan witness and readable unassessed corrections");
    }

    static void Unassessed(JsonObject facts,JsonObject coverage,string? reasonContains=null)
    {
        var source=E(facts);var original=source.GetRawText();
        var result=StorageFacilityAssessments.Measured(source,E(coverage));
        if(result.Constraint!=FacilityConstraintState.Unassessed||result.Assessment?.ClosingLevel is not null||string.IsNullOrWhiteSpace(result.UnavailableReason))
            throw new Exception("Missing or revised physical evidence must remain explicitly unassessed, with a readable reason and no successful old closing value.");
        if(reasonContains is not null&&!result.UnavailableReason.Contains(reasonContains,StringComparison.OrdinalIgnoreCase))
            throw new Exception("The unassessed explanation lost its explicit source horizon.");
        if(source.GetRawText()!=original)throw new Exception("An unassessed projection changed the retained raw physical facts.");
    }
    static void Same(JsonElement expected,JsonElement actual,string message)
    {if(!JsonElement.DeepEquals(expected,actual))throw new Exception(message);}
    static void Reject(Action work,string code="storage_facility_input")
    {try{work();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
