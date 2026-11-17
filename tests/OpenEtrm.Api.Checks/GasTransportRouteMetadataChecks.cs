using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;

internal static class GasTransportRouteMetadataChecks
{
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,GasOwnedOperations.Json);
    static GasTransportRouteChoices Choices(JsonObject source)=>new([..source["sourceCapacities"]!.AsArray().Select(c=>GasTransportRouteAdapterChecks.E(c!))]);
    static GasFormField Field(FacilityOperationForm form,string name)=>form.Fields.Single(f=>f.Name==name);
    static void CheckFields(ImmutableArray<GasFormField> fields)
    {
        var names=fields.SelectMany(f=>f.Kind=="reference"?f.Members:[f.Name]).ToArray();
        if(names.Distinct(StringComparer.Ordinal).Count()!=names.Length)throw new Exception("Route reference fields must flatten to disjoint explicit wire members.");
        foreach(var field in fields)
        {
            if(field.Kind=="reference")foreach(var choice in field.Choices)
                if(!Json(choice.Value).EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(field.Members.Order()))
                    throw new Exception("Every route reference choice must attest exactly its declared tuple.");
            if(field.Kind is "array" or "object")CheckFields(field.Fields);
        }
    }
    internal static void Run()
    {
        var command=GasTransportRouteCommands.Normalize(GasTransportRouteAdapterChecks.Operation,GasTransportRouteAdapterChecks.E(GasTransportRouteAdapterChecks.Define()));
        var source=GasTransportRouteAdapterChecks.Facts(command);var choices=Choices(source);
        var initial=GasTransportRouteMetadata.Build(null,choices);
        if(initial.Forms.Count!=0||initial.Configuration.Count!=1)throw new Exception("The directory must describe an explicit immutable graph, without inventing an adopted route.");
        var define=initial.Configuration["define"];CheckFields(define.Fields);
        if(define.Permission!="ConfigureGasServices"||define.Base.GetProperty("routeId").ValueKind!=JsonValueKind.Null||
            define.Base.GetProperty("expectedStateVersion").GetInt32()!=0||Field(define,"scope").Choices.Length!=1)
            throw new Exception("Graph authoring must retain a single exact owner/reference/native-basis tuple selected from actual authorized capacity sources.");
        foreach(var field in new[]{"implementation","fractionBasis","flowShape","transitLaw","nodeBalanceLaw","junctionPolicy","membershipPolicy"})
            if(define.Base.TryGetProperty(field,out _)||Field(define,field).Choices.Length!=1)
                throw new Exception("Every installed route law remains explicitly selected instead of silently populated.");
        var segments=Field(define,"segments");var fraction=segments.Fields.Single(f=>f.Name=="fuelFraction");
        if(fraction.Kind!="object"||fraction.Fields.Any(f=>f.Kind!="text")||fraction.Fields.Select(f=>f.Name).Order().SequenceEqual(new[]{"d","n"})==false||
            segments.Fields.Single(f=>f.Name=="transitMicroseconds").Kind!="text")
            throw new Exception("Fractions and full long microsecond lags must remain exact text inputs, not binary floating-point or safe-number editors.");
        if(segments.Fields.Single(f=>f.Name=="path").Choices.Length!=7||Field(define,"nodes").Fields.Single(f=>f.Name=="endpoint").Choices.Length!=6)
            throw new Exception("Describe actual immutable paths and endpoint tuples without manufacturing a receipt-by-delivery Cartesian product.");
        var retained=GasTransportRouteMetadata.Build(GasTransportRouteAdapterChecks.E(source),choices);
        if(retained.Configuration.Count!=0||!retained.Forms.ContainsKey("plan")||retained.Forms.ContainsKey("release"))
            throw new Exception("An existing graph is immutable; current eligible sources may be explicitly adopted and absent own plans cannot be released.");
        var plan=retained.Forms["plan"];CheckFields(plan.Fields);
        if(plan.Base.Id("routeId")!=command.Id("routeId")||plan.Base.Id("definitionId")!=GasTransportRouteAdapterChecks.Operation||
            Field(plan,"sources").Fields.Single().Choices.Length!=4||Field(plan,"sourcePlans").Fields.Single().Choices.Length!=4||
            Field(plan,"flows").Fields.Any(f=>f.Name.Contains("quantity",StringComparison.OrdinalIgnoreCase)||f.Name.Contains("From",StringComparison.Ordinal)))
            throw new Exception("Plan metadata must pin the route and expose opaque source heads/claims without editable source quantities or intervals.");
        var previous=GasTransportRouteAdapterChecks.Id(40);source["plans"]=new JsonArray(GasTransportRouteAdapterChecks.Obj(new{id=previous,
            stream_id=GasTransportRouteCommands.LogicalPlan(command.Id("routeId"),"synthetic old route plan"),business_key="synthetic old route plan",revision=1,
            previous_id=(Guid?)null,definition_id=GasTransportRouteAdapterChecks.Operation,kind="plan",effective_at="2032-03-14T15:00:00Z"}));
        // A retained old generation being stale must not suppress re-adoption
        // from the newly selected complete current sources.
        source["plans"]![0]!["assessment"]=null;
        var reAdopt=GasTransportRouteMetadata.Build(GasTransportRouteAdapterChecks.E(source),choices);
        if(!reAdopt.Forms.ContainsKey("plan")||!reAdopt.Forms.ContainsKey("release"))throw new Exception("Stale retained assessment and current source eligibility are separate contracts.");
        var release=reAdopt.Forms["release"];CheckFields(release.Fields);
        if(release.Base.Text("kind")!="release"||release.Base.Text("admissionPolicy")!=GasTransportRouteCommands.Release||release.Fields.Any(f=>f.Name is "sources" or "sourcePlans" or "flows")||
            !Field(release,"plan").Members.Order().SequenceEqual(new[]{"businessKey","expectedPreviousId"}))
            throw new Exception("Own-membership release must select the exact existing stream/head without a new source assessment or flow inputs.");
        var future=source.DeepClone().AsObject();future["sourceCapacities"]![0]!["hasFutureHeads"]=true;
        var unavailable=GasTransportRouteMetadata.Build(GasTransportRouteAdapterChecks.E(source),Choices(future));
        if(unavailable.Forms.ContainsKey("plan")||!unavailable.Forms.ContainsKey("release"))throw new Exception("A future capacity source blocks adoption while own release remains available.");
        var occupied=source.DeepClone().AsObject();occupied["sourceCapacities"]![0]!["sourcePlans"]![0]!["membershipState"]="occupied_v1";
        unavailable=GasTransportRouteMetadata.Build(GasTransportRouteAdapterChecks.E(source),Choices(occupied));
        if(unavailable.Forms.ContainsKey("plan")||!unavailable.Forms.ContainsKey("release"))throw new Exception("Foreign stream occupancy cannot become an assumed available source, and cannot prevent releasing own membership.");
        var malformed=source.DeepClone().AsObject();malformed["sourceCapacities"]![0]!["sourcePlans"]![0]!.AsObject().Remove("membershipState");
        GasTransportRouteAdapterChecks.Reject(()=>GasTransportRouteMetadata.Build(GasTransportRouteAdapterChecks.E(source),Choices(malformed)),"gas_transport_route_source");
        var contradiction=source.DeepClone().AsObject();contradiction["sourceCapacities"]![0]!["sourcePlans"]![0]!["membershipState"]="owned_by_this_route_v1";
        GasTransportRouteAdapterChecks.Reject(()=>GasTransportRouteMetadata.Build(GasTransportRouteAdapterChecks.E(source),Choices(contradiction)),"gas_transport_route_source");
        var futureOwn=source.DeepClone().AsObject();futureOwn["hasFutureHeads"]=true;
        var a=GasTransportRouteAdapterChecks.Id(60);var b=GasTransportRouteAdapterChecks.Id(61);
        var streamA=GasTransportRouteCommands.LogicalPlan(command.Id("routeId"),"synthetic future own A");
        var streamB=GasTransportRouteCommands.LogicalPlan(command.Id("routeId"),"synthetic current own B");
        futureOwn["plans"]=new JsonArray(GasTransportRouteAdapterChecks.Obj(new{id=a,stream_id=streamA,business_key="synthetic future own A",revision=1,previous_id=(Guid?)null,
            definition_id=GasTransportRouteAdapterChecks.Operation,kind="plan",effective_at="2032-03-15T00:00:00Z"}),
            GasTransportRouteAdapterChecks.Obj(new{id=b,stream_id=streamB,business_key="synthetic current own B",revision=1,previous_id=(Guid?)null,
                definition_id=GasTransportRouteAdapterChecks.Operation,kind="plan",effective_at="2032-03-14T15:00:00Z"}));
        var memberships=new JsonArray();var ordinal=0;
        foreach(var capacity in futureOwn["sourceCapacities"]!.AsArray())
        {
            var first=capacity!["sourcePlans"]![0]!["firstPlanRevisionId"]!.GetValue<Guid>();
            capacity["sourcePlans"]![0]!["membershipState"]="occupied_v1";
            memberships.Add(GasTransportRouteAdapterChecks.Obj(new{first_plan_revision_id=first,stream_id=streamA,route_plan_id=a}));
            var other=GasTransportRouteAdapterChecks.Id(700+ordinal);
            capacity["plans"]!.AsArray().Add(GasTransportRouteAdapterChecks.Obj(new{id=other,business_key="synthetic independent capacity B "+ordinal,revision=1,previous_id=(Guid?)null,
                version_id=capacity["version"]!["id"]!.GetValue<Guid>()}));
            capacity["sourcePlans"]!.AsArray().Add(GasTransportRouteAdapterChecks.Obj(new{firstPlanRevisionId=other,planRevisionId=other,membershipGeneration=1,membershipState="owned_by_this_route_v1"}));
            var claimIndex=0;
            foreach(var claim in capacity["claims"]!.AsArray().ToArray())
            {var independent=claim!.DeepClone();independent["id"]=GasTransportRouteAdapterChecks.Id(800+ordinal*10+claimIndex++);independent["plan_id"]=other;capacity["claims"]!.AsArray().Add(independent);}
            memberships.Add(GasTransportRouteAdapterChecks.Obj(new{first_plan_revision_id=other,stream_id=streamB,route_plan_id=b}));ordinal++;
        }
        futureOwn["memberships"]=memberships;
        var futureOwnForms=GasTransportRouteMetadata.Build(GasTransportRouteAdapterChecks.E(futureOwn),Choices(futureOwn));
        if(futureOwnForms.Forms.ContainsKey("plan")||!futureOwnForms.Forms.TryGetValue("release",out var ownRelease)||Field(ownRelease,"plan").Choices.Length!=1||
            Json(Field(ownRelease,"plan").Choices[0].Value).Id("expectedPreviousId")!=b)
            throw new Exception("A known future own A reserves its sources without preventing current own B from releasing its independent memberships.");
    }
}
