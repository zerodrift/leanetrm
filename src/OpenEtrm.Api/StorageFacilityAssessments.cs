using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Physical;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record StorageFacilityAssessmentRead(FacilityConstraintState Constraint,StorageFacilityAssessment? Assessment,string? UnavailableReason);

// Native reads already resolve knowledge and business visibility. Keep their
// exact version bundles and evidence heads intact when adapting to the assessor.
internal static class StorageFacilityAssessments
{
    internal static StorageFacilityRules Definition(Guid operationId,JsonElement command)
    {
        if(command.Text("kind")!="define"||operationId==Guid.Empty)throw Invalid();
        var unit=new ReferenceKey(new(command.Id("referenceCatalogId"),command.Id("referenceRevisionId")),command.Text("quantity"));
        // This identity exists only to validate a candidate. Native admission
        // assigns the accepted immutable version and returns it in its receipt.
        return new(StorageFacilityCommands.TechnicalId("storage_facility_definition_preview_v1",operationId),command.Text("implementation"),unit,
            [..Rows(command,"ports").Select(p=>p.Id("portId"))],Q(command,"minimum",unit),Q(command,"maximum",unit),
            Bands(command.GetProperty("ratchets"),unit),command.Text("inventoryBasis"),command.Text("rateBasis"),command.Text("simultaneousFlow"),command.Text("lossTreatment"));
    }

    internal static StorageFacilityAssessment Plan(JsonElement facts,JsonElement command)
    {
        var facility=facts.GetProperty("facility").Id("id");
        if(command.Text("kind")!="plan"||command.Id("facilityId")!=facility||
            command.GetProperty("expectedStateVersion").GetInt32()!=facts.GetProperty("stateVersion").GetInt32()||
            command.Id("versionId")!=facts.GetProperty("version").Id("id")||
            NullableId(command,"expectedPreviousId")!=NullableRowId(facts.GetProperty("plan")))throw Stale();
        var bundle=Bundle(facts,command.Id("versionId"))??throw Stale();
        var rules=Rules(bundle,facility);
        var from=At(command,"from");var through=At(command,"through");
        if(RuleWindow(facts,bundle.GetProperty("version"),from,through) is {} unavailable)
            throw new MarketDataException("storage_facility_rule_transition",unavailable);
        var openingRow=Rows(facts,"openings").SingleOrDefault(o=>o.Id("id")==command.Id("openingRevisionId"));
        if(openingRow.ValueKind==JsonValueKind.Undefined)throw Stale();
        var opening=Opening(openingRow,facility,rules.Quantity);
        var movements=Rows(command,"activities").Select(row=>Movement(row,bundle,facility,rules.Quantity,true)).ToImmutableArray();
        return StorageFacilityOperations.Assess(rules,opening,from,through,movements,
            new(command.Id("planId"),from,through,"planned",command.Text("coverageStatement")));
    }

    internal static JsonElement Witness(StorageFacilityAssessment assessment)
    {
        assessment.RequireFeasible();
        return JsonSerializer.SerializeToElement(new{implementation="storage_assessment_witness_v1",rulesVersion=assessment.RulesVersion,
            openingRevision=assessment.OpeningRevision,coverageRevision=assessment.CoverageRevision,from=LifecycleNative.Instant(assessment.From),
            through=LifecycleNative.Instant(assessment.Through),constraint="within_limit",closing=Ratio(assessment.ClosingLevel!),
            intervals=assessment.Intervals.Select(i=>new{from=LifecycleNative.Instant(i.From),through=LifecycleNative.Instant(i.To),
                activities=i.Activities.Order().ToArray(),opening=Ratio(i.Opening),closing=Ratio(i.Closing),
                injectionPerHour=Ratio(i.InjectionPerHour),withdrawalPerHour=Ratio(i.WithdrawalPerHour),bands=i.Bands})});
    }

    internal static StorageFacilityAssessmentRead Measured(JsonElement facts,JsonElement coverage)
    {
        try{return Measure(facts,coverage);}
        catch(MarketDataException error){return Unavailable(error.Message);}
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException)
        {return Unavailable("The selected physical evidence is incomplete or no longer has an exact authorized rule bundle. Reload the facility evidence.");}
    }

    private static StorageFacilityAssessmentRead Measure(JsonElement facts,JsonElement coverage)
    {
        var facility=facts.GetProperty("facility").Id("id");
        if(coverage.ValueKind!=JsonValueKind.Object||coverage.Id("facility_id")!=facility||
            !Rows(facts,"coverages").Any(c=>c.Id("id")==coverage.Id("id")))
            return Unavailable("The selected reporting coverage is no longer a current source revision. Select its current head and reassess.");
        var bundle=Bundle(facts,coverage.Id("version_id"));
        if(bundle is null)return Unavailable("The exact rule version pinned by this reporting coverage is unavailable in this context.");
        var rules=Rules(bundle.Value,facility);var from=At(coverage,"from_at");var through=At(coverage,"through_at");
        if(!facts.TryGetProperty("asOf",out var effective)||effective.ValueKind!=JsonValueKind.String||
            !facts.TryGetProperty("knownAt",out var known)||known.ValueKind!=JsonValueKind.String)
            return Unavailable("Measured assessment requires its exact effective time and certified knowledge cutoff.");
        if(through>At(facts,"asOf"))return Unavailable("The observed reporting window ends after the selected effective time. Future activity requires an explicit chosen plan.");
        if(through>At(facts,"knownAt"))return Unavailable("The observed reporting window ends after the certified knowledge cutoff. No closing physical level is assessed for that future window.");
        if(RuleWindow(facts,bundle.Value.GetProperty("version"),from,through) is {} unavailable)return Unavailable(unavailable);
        var openingId=NullableId(coverage,"opening_revision_id");
        StorageFacilityOpening? opening=null;
        if(openingId is {} pinned)
        {
            var row=Rows(facts,"openings").SingleOrDefault(o=>o.Id("id")==pinned);
            if(row.ValueKind==JsonValueKind.Undefined)return Unavailable("The pinned opening observation has been revised or is unavailable. Select its current source revision.");
            opening=Opening(row,facility,rules.Quantity);
        }
        var manifest=Rows(coverage,"activityRevisionIds").Select(r=>r.GetGuid()).ToArray();
        if(manifest.Length!=manifest.Distinct().Count())return Unavailable("Reporting coverage contains duplicate activity revisions.");
        var relevant=Rows(facts,"activities").Where(a=>At(a,"from_at")<through&&At(a,"through_at")>from).ToArray();
        if(relevant.Any(a=>a.GetProperty("sourceCorrected").GetBoolean()))
            return Unavailable("A linked reported flow in this reporting window has a newer source correction. Review its activity and reporting coverage before reassessment.");
        // Any newly reported, corrected, or straddling flow invalidates the old
        // complete manifest. Never clip a flow or retain an old successful sum.
        if(relevant.Any(a=>a.Id("facility_id")!=facility||a.Id("version_id")!=rules.VersionId||At(a,"from_at")<from||At(a,"through_at")>through)||
            !manifest.Order().SequenceEqual(relevant.Select(a=>a.Id("id")).Order()))
            return Unavailable("Reported activity heads differ from the exact coverage manifest or extend outside its rule window. Review current activity and record coverage again.");
        var movements=relevant.Select(a=>Movement(a,bundle.Value,facility,rules.Quantity,false)).ToImmutableArray();
        var assessment=StorageFacilityOperations.Assess(rules,opening,from,through,movements,
            new(coverage.Id("id"),from,through,"measured",coverage.Text("statement")));
        return new(assessment.Constraint,assessment,assessment.Constraint==FacilityConstraintState.Unassessed?
            opening is null?"Select dated opening evidence before assessing total physical inventory.":"Reporting coverage is explicitly incomplete; no closing inventory is assessed.":null);
    }

    private static StorageFacilityRules Rules(JsonElement bundle,Guid facility)
    {
        var version=bundle.GetProperty("version");
        if(version.Id("facility_id")!=facility)throw Invalid();
        var unit=new ReferenceKey(new(version.Id("reference_catalog_id"),version.Id("reference_revision_id")),version.Text("quantity_code"));
        var ports=Rows(bundle,"ports");
        if(ports.Any(p=>p.Id("version_id")!=version.Id("id")))throw Invalid();
        var rules=new StorageFacilityRules(version.Id("id"),version.Text("implementation"),unit,[..ports.Select(p=>p.Id("id"))],
            Q(version,"minimum",unit),Q(version,"maximum",unit),Bands(bundle.GetProperty("ratchets"),unit),version.Text("inventory_basis"),
            version.Text("rate_basis"),version.Text("simultaneous_flow"),version.Text("loss_treatment"));
        StorageFacilityOperations.ValidateRules(rules);return rules;
    }

    private static StorageFacilityOpening Opening(JsonElement row,Guid facility,ReferenceKey unit)
    {
        if(row.Id("facility_id")!=facility)throw Invalid();
        return new(row.Id("id"),At(row,"observed_at"),Q(row,"quantity",unit));
    }

    private static StorageFacilityMovement Movement(JsonElement row,JsonElement bundle,Guid facility,ReferenceKey unit,bool planned)
    {
        var portId=row.Id(planned?"portId":"port_id");
        var port=Rows(bundle,"ports").SingleOrDefault(p=>p.Id("id")==portId);
        if(port.ValueKind==JsonValueKind.Undefined||port.Text("direction")!=row.Text("direction")||
            !planned&&(row.Id("facility_id")!=facility||row.Id("version_id")!=bundle.GetProperty("version").Id("id")))
            throw new MarketDataException("storage_facility_port","Select the exact port and its declared movement direction from the pinned facility rule version.");
        return new(row.Id(planned?"revisionId":"id"),portId,At(row,planned?"from":"from_at"),At(row,planned?"through":"through_at"),row.Text("direction"),Q(row,"quantity",unit));
    }

    private static ImmutableArray<StorageRatchetBand> Bands(JsonElement rows,ReferenceKey unit)=>
        [..rows.EnumerateArray().Select(row=>new StorageRatchetBand(Q(row,"lower",unit),Q(row,"upper",unit),Q(row,"injectionPerHour",unit),Q(row,"withdrawalPerHour",unit)))];
    private static JsonElement? Bundle(JsonElement facts,Guid id)
    {
        var rows=Rows(facts,"versionBundles").Where(b=>b.GetProperty("version").Id("id")==id).ToArray();
        return rows.Length==1?rows[0]:null;
    }
    private static string? RuleWindow(JsonElement facts,JsonElement selected,DateTimeOffset from,DateTimeOffset through)
    {
        if(!facts.TryGetProperty("knownRuleVersions",out var known)||known.ValueKind!=JsonValueKind.Array)throw Invalid();
        var rows=known.EnumerateArray().ToArray();
        if(rows.Length==0||rows.Any(r=>r.ValueKind!=JsonValueKind.Object||!r.TryGetProperty("revision",out var revision)||
            revision.ValueKind!=JsonValueKind.Number||!revision.TryGetInt32(out var n)||n<=0)||
            rows.Select(r=>r.Id("id")).Distinct().Count()!=rows.Length)throw Invalid();
        var pinned=rows.SingleOrDefault(r=>r.Id("id")==selected.Id("id"));
        if(pinned.ValueKind==JsonValueKind.Undefined||At(pinned,"effective_at")!=At(selected,"effective_at"))throw Invalid();
        if(At(selected,"effective_at")>from)
            return "The selected physical rules are not yet applicable when this window starts. Select a window within their physical applicability period.";
        if(rows.Any(r=>r.GetProperty("revision").GetInt32()>pinned.GetProperty("revision").GetInt32()&&At(r,"effective_at")<through))
            return "A source-known superseding physical rule applies before this window ends. Record a window under one exact applicable rule version.";
        return null;
    }
    private static JsonElement[] Rows(JsonElement value,string name)=>[..value.GetProperty(name).EnumerateArray()];
    private static Guid? NullableId(JsonElement value,string name)=>value.GetProperty(name).ValueKind==JsonValueKind.Null?null:value.Id(name);
    private static Guid? NullableRowId(JsonElement row)=>row.ValueKind==JsonValueKind.Null?null:row.Id("id");
    private static Quantity Q(JsonElement value,string key,ReferenceKey unit)
    {
        var number=value.GetProperty(key);
        return new(ExactDecimal.Parse(number.ValueKind==JsonValueKind.String?number.GetString()!:number.GetRawText()),unit);
    }
    // PostgreSQL JSON timestamps may use +00:00; both native and command UTC
    // forms retain the same exact microsecond geometry.
    private static DateTimeOffset At(JsonElement row,string key)=>TradingInstant.Parse(row.Text(key),"storage_facility_input");
    private static object Ratio(ExactQuantity value)=>new{n=value.Numerator,d=value.Denominator};
    private static StorageFacilityAssessmentRead Unavailable(string reason)=>new(FacilityConstraintState.Unassessed,null,reason);
    private static MarketDataException Stale()=>new("storage_facility_stale","The facility source, exact rule version, opening observation or previous plan changed. Reload and review the current plan inputs.");
    private static MarketDataException Invalid()=>new("storage_facility_input","The exact facility rule bundle, native quantity, ports and physical evidence must agree.");
}
