using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class GasTransportCapacityAssessments
{
    internal static GasTransportCapacityProfile Definition(Guid operation,JsonElement command)
    {
        if(command.Text("kind")!="define"||operation==Guid.Empty)throw Invalid();
        var reference=new ReferenceRevision(command.Id("referenceCatalogId"),command.Id("referenceRevisionId"));
        ReferenceKey Key(string code)=>new(reference,code);
        var unit=Key(command.Text("quantity"));
        return new(operation,command.Text("implementation"),unit,At(command,"from"),At(command,"through"),
            command.Text("rateBasis"),command.Text("flowShape"),command.Text("agreementBasis"),command.Text("deliveryBasis"),
            [..Rows(command,"paths").Select(p=>new GasTransportCapacityPath(p.Id("pathId"),Key(p.Text("receiptPoint")),Key(p.Text("deliveryPoint"))))],
            Limits(command.GetProperty("agreement"),unit,"maximumPerHour"),
            [..Rows(command,"receipts").Select(p=>new GasTransportPointCapacity(Key(p.Text("point")),Limits(p.GetProperty("limits"),unit,"maximumPerHour")))],
            [..Rows(command,"deliveries").Select(p=>new GasTransportPointCapacity(Key(p.Text("point")),Limits(p.GetProperty("limits"),unit,"maximumPerHour")))],
            [..Rows(command,"paths").Select(p=>new GasTransportRouteCapacity(p.Id("pathId"),Limits(p.GetProperty("limits"),unit,"maximumPerHour")))]);
    }
    internal static GasTransportCapacityAssessment Current(JsonElement facts)=>Source(()=>{
        RequireEffectiveHeads(facts);
        var profile=Profile(facts);var plans=Rows(facts,"plans");
        if(plans.Select(p=>p.Id("id")).Distinct().Count()!=plans.Length||plans.Select(p=>p.Text("business_key")).Distinct(StringComparer.Ordinal).Count()!=plans.Length)throw Invalid();
        var ids=plans.Select(p=>p.Id("id")).ToHashSet();
        var claims=Rows(facts,"claims");if(claims.Any(c=>!ids.Contains(c.Id("plan_id"))))throw Invalid();
        return GasTransportCapacity.Assess(profile,[..claims.Select(c=>NativeUse(c,profile))]);
    });
    internal static GasTransportCapacityAssessment Revision(Guid operation,JsonElement command,JsonElement facts)=>Source(()=>{
        RequireEffectiveHeads(facts);
        var current=Profile(facts);var version=facts.GetProperty("version");
        if(command.Text("kind")!="define"||command.Id("entitlementId")!=facts.GetProperty("entitlement").Id("id")||
            command.OptionalId("expectedPreviousId")!=current.VersionId||command.GetProperty("expectedStateVersion").GetInt32()!=facts.GetProperty("stateVersion").GetInt32())throw Stale();
        _=Current(facts);
        var proposed=Definition(operation,command);
        if(proposed.Quantity!=current.Quantity||proposed.From!=current.From||proposed.Through!=current.Through||
            proposed.Implementation!=current.Implementation||proposed.RateBasis!=current.RateBasis||proposed.FlowShape!=current.FlowShape||
            proposed.AgreementBasis!=current.AgreementBasis||proposed.DeliveryBasis!=current.DeliveryBasis||
            command.Text("businessKey")!=facts.GetProperty("entitlement").Text("business_key")||command.Text("product")!=version.Text("product_code"))throw Immutable();
        foreach(var (field,column) in new[]{("bookId","book_id"),("legalEntityId","legal_entity_id"),("counterpartyId","counterparty_id")})
            if(command.Id(field)!=version.Id(column))throw Immutable();
        foreach(var (field,column) in new[]{("legalEntityRevision","legal_entity_revision"),("counterpartyRevision","counterparty_revision")})
            if(command.GetProperty(field).GetInt32()!=version.GetProperty(column).GetInt32())throw Immutable();
        var paths=Rows(facts,"paths").ToDictionary(p=>p.Id("id"));var selected=Rows(command,"paths");
        if(selected.Length!=paths.Count||selected.Select(p=>p.Id("pathId")).Distinct().Count()!=selected.Length)throw Immutable();
        foreach(var path in selected)
        {
            if(!paths.TryGetValue(path.Id("pathId"),out var saved))throw Immutable();
            foreach(var (field,column) in new[]{("pathKey","path_key"),("pipeline","pipeline_code"),("receiptZone","receipt_zone_code"),
                ("receiptPoint","receipt_point_code"),("deliveryZone","delivery_zone_code"),("deliveryPoint","delivery_point_code"),("product","product_code")})
                if(path.Text(field)!=saved.Text(column))throw Immutable();
        }
        // A definition changes limits, never the accepted claims. Keep every
        // plan's exact identity/quantity and expose any new breach without
        // requiring feasibility or silently curtailing the chosen plans.
        return GasTransportCapacity.Assess(proposed,[..Rows(facts,"claims").Select(c=>NativeUse(c,current))]);
    });
    internal static GasTransportCapacityAssessment Plan(JsonElement facts,JsonElement command)=>Source(()=>{
        var policy=AdmissionPolicy(command);
        if(policy==GasTransportCapacityPlanAdmission.EmptyRelease&&(command.OptionalId("expectedPreviousId") is null||Rows(command,"claims").Length!=0))throw Admission();
        RequireEffectiveHeads(facts);
        var profile=Profile(facts);
        if(command.Text("kind")!="plan"||command.Id("entitlementId")!=facts.GetProperty("entitlement").Id("id")||
            command.Id("versionId")!=profile.VersionId||command.GetProperty("expectedStateVersion").GetInt32()!=facts.GetProperty("stateVersion").GetInt32())throw Stale();
        var plans=Rows(facts,"plans");var own=plans.Where(p=>p.Text("business_key")==command.Text("businessKey")).ToArray();
        if(own.Length>1)throw Invalid();
        var previous=own.Length==0?(Guid?)null:own[0].Id("id");
        if(command.OptionalId("expectedPreviousId")!=previous)throw Stale();
        // Validate complete native membership even when the proposed plan will
        // replace one head. Orphan/duplicate claims must never be hidden by it.
        _=Current(facts);
        var retained=Rows(facts,"claims").Where(c=>c.Id("plan_id")!=previous).Select(c=>NativeUse(c,profile));
        var chosen=Rows(command,"claims").Select(c=>Use(c,profile,"claimId","pathId","receiptFrom","receiptTo","receiptQuantity","deliveryFrom","deliveryTo","deliveryQuantity"));
        return GasTransportCapacity.Assess(profile,[..retained.Concat(chosen)]);
    });
    internal static JsonElement Witness(GasTransportCapacityAssessment assessment,string admissionPolicy)
    {
        if(admissionPolicy is not(GasTransportCapacityPlanAdmission.FeasibleReplacement or GasTransportCapacityPlanAdmission.EmptyRelease))throw Admission();
        if(admissionPolicy==GasTransportCapacityPlanAdmission.FeasibleReplacement)assessment.RequireFeasible();
        return JsonSerializer.SerializeToElement(new{implementation="gas_transport_capacity_witness_v1",versionId=assessment.VersionId,admissionPolicy,
            intervals=assessment.Intervals.Select(i=>new{axis=i.Axis,point=i.Point?.Code,pathId=i.RouteVersionId,from=LifecycleNative.Instant(i.From),
                through=LifecycleNative.Instant(i.Through),claims=i.Uses.Order().ToArray(),used=Ratio(i.UsedPerHour),maximum=Ratio(i.MaximumPerHour),withinLimit=i.WithinLimit})});
    }
    static object Ratio(ExactQuantity value)=>new{n=value.Numerator,d=value.Denominator};
    static string AdmissionPolicy(JsonElement command)
    {
        if(!command.TryGetProperty("admissionPolicy",out var value)||value.ValueKind!=JsonValueKind.String||
            value.GetString() is not(GasTransportCapacityPlanAdmission.FeasibleReplacement or GasTransportCapacityPlanAdmission.EmptyRelease))throw Admission();
        return value.GetString()!;
    }
    static void RequireEffectiveHeads(JsonElement facts)
    {
        if(facts.GetProperty("hasFutureHeads").GetBoolean())
            throw new MarketDataException("gas_transport_capacity_future_source","A latest known capacity or plan revision is not effective in this business context. Select a later business context before assessment or new changes.");
    }
    static GasTransportCapacityProfile Profile(JsonElement facts)
    {
        var version=facts.GetProperty("version");
        if(version.Id("entitlement_id")!=facts.GetProperty("entitlement").Id("id"))throw Invalid();
        var reference=new ReferenceRevision(version.Id("reference_catalog_id"),version.Id("reference_revision_id"));
        ReferenceKey Key(string code)=>new(reference,code);
        var unit=Key(version.Text("quantity_code"));
        return new(version.Id("id"),version.Text("implementation"),unit,At(version,"from_at"),At(version,"through_at"),
            version.Text("rate_basis"),version.Text("flow_shape"),version.Text("agreement_basis"),version.Text("delivery_basis"),
            [..Rows(facts,"paths").Select(p=>new GasTransportCapacityPath(p.Id("id"),Key(p.Text("receipt_point_code")),Key(p.Text("delivery_point_code"))))],
            Limits(facts.GetProperty("agreement"),unit,"maximum_per_hour"),
            [..Rows(facts,"receipts").Select(p=>new GasTransportPointCapacity(Key(p.Text("point_code")),Limits(p.GetProperty("limits"),unit,"maximum_per_hour")))],
            [..Rows(facts,"deliveries").Select(p=>new GasTransportPointCapacity(Key(p.Text("point_code")),Limits(p.GetProperty("limits"),unit,"maximum_per_hour")))],
            [..Rows(facts,"paths").Select(p=>new GasTransportRouteCapacity(p.Id("id"),Limits(p.GetProperty("limits"),unit,"maximum_per_hour")))]);
    }
    static GasTransportCapacityUse NativeUse(JsonElement row,GasTransportCapacityProfile profile)=>
        Use(row,profile,"id","path_id","receipt_from","receipt_to","receipt_quantity","delivery_from","delivery_to","delivery_quantity");
    static GasTransportCapacityUse Use(JsonElement row,GasTransportCapacityProfile profile,string id,string path,string rf,string rt,string rq,string df,string dt,string dq)
    {
        var selected=profile.Paths.SingleOrDefault(p=>p.RouteVersionId==row.Id(path))??throw Invalid();
        return new(row.Id(id),selected.RouteVersionId,selected.ReceiptPoint,selected.DeliveryPoint,At(row,rf),At(row,rt),Q(row,rq,profile.Quantity),At(row,df),At(row,dt),Q(row,dq,profile.Quantity));
    }
    static ImmutableArray<GasTransportCapacitySlice> Limits(JsonElement rows,ReferenceKey unit,string maximum)
    {if(rows.ValueKind!=JsonValueKind.Array)throw Invalid();return[..rows.EnumerateArray().Select(r=>new GasTransportCapacitySlice(At(r,"from"),At(r,"through"),Q(r,maximum,unit)))];}
    static Quantity Q(JsonElement row,string field,ReferenceKey unit)=>new(ExactDecimal.Parse(row.Text(field)),unit);
    static DateTimeOffset At(JsonElement row,string field)=>TradingInstant.Parse(row.Text(field),"gas_transport_capacity_source");
    static ImmutableArray<JsonElement> Rows(JsonElement row,string field)
    {var values=row.GetProperty(field);if(values.ValueKind!=JsonValueKind.Array)throw Invalid();return[..values.EnumerateArray()];}
    static T Source<T>(Func<T> read)
    {
        try{return read();}
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {throw new MarketDataException("gas_transport_capacity_source","Retain the complete exact capacity version, eligible paths and all chosen-plan heads under the certified context.",error);}
    }
    static MarketDataException Stale()=>new("gas_transport_capacity_stale","The capacity or selected plan source changed. Refresh the complete entitlement and review again.");
    static MarketDataException Immutable()=>new("gas_transport_capacity_input","Capacity revisions may change complete dated limits and evidence only; retain exact entitlement ownership, reference basis, service interval, laws and path geometry.");
    static MarketDataException Admission()=>new("gas_transport_capacity_input","Select the explicit installed plan admission policy. Empty release requires one existing plan head and no replacement claims.");
    static MarketDataException Invalid()=>new("gas_transport_capacity_source","Retain the complete exact capacity version, eligible paths and all chosen-plan heads under the certified context.");
}
