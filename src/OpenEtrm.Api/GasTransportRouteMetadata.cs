using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record GasTransportRouteChoices(ImmutableArray<JsonElement> SourceCapacities);
internal sealed record GasTransportRouteForms(ImmutableDictionary<string,FacilityOperationForm> Forms,ImmutableDictionary<string,FacilityOperationForm> Configuration);

internal static class GasTransportRouteMetadata
{
    internal static GasTransportRouteForms Build(JsonElement? facts,GasTransportRouteChoices choices)
    {
        try{return BuildCore(facts,choices);}
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {throw new MarketDataException("gas_transport_route_source","Retain exact route geometry, current complete capacity sources and explicit membership availability before describing route operations.",error);}
    }
    static GasTransportRouteForms BuildCore(JsonElement? facts,GasTransportRouteChoices choices)
    {
        GasFormField Text(string name,string label)=>new(name,label,"text",false,[],[],[]);
        GasFormField Select(string name,string label,ImmutableArray<GasFormChoice> values)=>new(name,label,"select",false,values,[],[]);
        GasFormField Reference(string name,string label,bool nullable,ImmutableArray<string> members,ImmutableArray<GasFormChoice> values)=>new(name,label,"reference",nullable,values,[],members);
        GasFormField Array(string name,string label,int minimum,ImmutableArray<GasFormField> fields)=>new(name,label,"array",false,[],fields,[]){MinItems=minimum};
        GasFormField Policy(string name,string label,string value,string explanation)=>Select(name,label,[new(value,explanation,value)]);
        GasFormField Fraction(string name,string label)=>new(name,label,"object",false,[],[Text("n","Nonnegative numerator"),Text("d","Positive denominator")],[]);
        var evidence=Text("evidence","Route terms or chosen-plan evidence");
        var forms=ImmutableDictionary.CreateBuilder<string,FacilityOperationForm>();
        var configuration=ImmutableDictionary.CreateBuilder<string,FacilityOperationForm>();
        var capacities=choices.SourceCapacities;
        if(capacities.IsDefault||capacities.Select(c=>c.GetProperty("entitlement").Id("id")).Distinct().Count()!=capacities.Length)throw Invalid();
        foreach(var capacity in capacities)
        {
            var future=capacity.GetProperty("hasFutureHeads").GetBoolean();var version=capacity.GetProperty("version");
            if(version.ValueKind==JsonValueKind.Null){if(!future)throw Invalid();continue;}
            if(version.ValueKind!=JsonValueKind.Object||version.Id("entitlement_id")!=capacity.GetProperty("entitlement").Id("id"))throw Invalid();
            var plans=Rows(capacity,"plans");var streams=Rows(capacity,"sourcePlans");
            if(streams.Length!=plans.Length||streams.Select(s=>s.Id("firstPlanRevisionId")).Distinct().Count()!=streams.Length||
                streams.Select(s=>s.Id("planRevisionId")).Distinct().Count()!=streams.Length||streams.Any(s=>!plans.Any(p=>p.Id("id")==s.Id("planRevisionId"))))throw Invalid();
            foreach(var stream in streams)
            {
                if(stream.GetProperty("membershipGeneration").GetInt32()<0)throw Invalid();
                _=GasTransportRouteAssessments.OwnMembership(facts,stream);
            }
        }
        if(facts is null)
        {
            var effective=capacities.Where(c=>c.GetProperty("version").ValueKind==JsonValueKind.Object).ToArray();
            var scopes=effective.Select(c=>{
                var v=c.GetProperty("version");
                var value=new{bookId=v.Id("book_id"),legalEntityId=v.Id("legal_entity_id"),legalEntityRevision=v.GetProperty("legal_entity_revision").GetInt32(),
                    referenceCatalogId=v.Id("reference_catalog_id"),referenceRevisionId=v.Id("reference_revision_id"),product=v.Text("product_code"),quantity=v.Text("quantity_code")};
                return new GasFormChoice(Json(value).GetRawText(),"Exact scope of "+c.GetProperty("entitlement").Text("business_key"),value);
            }).GroupBy(c=>c.Id,StringComparer.Ordinal).Select(g=>g.First()).OrderBy(c=>c.Label,StringComparer.Ordinal).ToImmutableArray();
            var paths=effective.SelectMany(c=>Rows(c,"paths").Select(p=>new GasFormChoice(c.GetProperty("entitlement").Id("id")+"@"+p.Id("id"),
                c.GetProperty("entitlement").Text("business_key")+" · "+p.Text("path_key")+" · "+p.Text("receipt_point_code")+" → "+p.Text("delivery_point_code"),
                new{entitlementId=c.GetProperty("entitlement").Id("id"),pathId=p.Id("id")}))).ToImmutableArray();
            var endpoints=effective.SelectMany(c=>Rows(c,"paths")).SelectMany(p=>new[]{
                new{pipeline=p.Text("pipeline_code"),zone=p.Text("receipt_zone_code"),point=p.Text("receipt_point_code")},
                new{pipeline=p.Text("pipeline_code"),zone=p.Text("delivery_zone_code"),point=p.Text("delivery_point_code")}})
                .Distinct().OrderBy(p=>p.pipeline,StringComparer.Ordinal).ThenBy(p=>p.zone,StringComparer.Ordinal).ThenBy(p=>p.point,StringComparer.Ordinal)
                .Select(p=>new GasFormChoice(Json(p).GetRawText(),p.pipeline+" · "+p.zone+" · "+p.point,p)).ToImmutableArray();
            configuration.Add("define",new("Define an immutable chosen Transport route","ConfigureGasServices",Json(new{kind="define",routeId=(Guid?)null,expectedStateVersion=0,expectedPreviousId=(Guid?)null}),[
                Text("businessKey","Route business reference"),Reference("scope","Exact owner, reference and native basis",false,
                    ["bookId","legalEntityId","legalEntityRevision","referenceCatalogId","referenceRevisionId","product","quantity"],scopes),
                Policy("implementation","Route profile",GasTransportRouteProfile.Version,"Complete chosen uniform segment plan"),
                Policy("fractionBasis","Fuel and loss basis",GasTransportRouteProfile.GrossReceiptFractions,"Separate explicit fuel and loss fractions of gross receipt"),
                Policy("flowShape","Flow shape",GasTransportRouteProfile.UniformSegmentFlow,"One explicit uniform receipt and delivery stage per segment"),
                Policy("transitLaw","Transit law",GasTransportRouteProfile.FixedTransit,"Fixed nonnegative elapsed microseconds"),
                Policy("nodeBalanceLaw","Intermediate-node balance",GasTransportRouteProfile.NoNodeStorage,"Simultaneous inflow equals outflow, without node storage"),
                Policy("junctionPolicy","Handoff eligibility",GasTransportRouteCommands.Junction,"Same exact reference, pipeline, zone, point and product"),
                Policy("membershipPolicy","Source membership",GasTransportRouteCommands.Membership,"Complete logical capacity-plan stream, exclusive to one route-plan stream"),
                Array("nodes","Complete connected route nodes",2,[Text("nodeKey","Local node reference"),Select("role","Explicit node role",[
                    new(GasTransportRouteNode.Source,"External receipt",GasTransportRouteNode.Source),new(GasTransportRouteNode.Transit,"Transit without storage",GasTransportRouteNode.Transit),
                    new(GasTransportRouteNode.Sink,"External delivery",GasTransportRouteNode.Sink)]),Reference("endpoint","Exact endpoint tuple",false,["pipeline","zone","point"],endpoints)]),
                Array("segments","Complete directed acyclic route segments",1,[Text("segmentKey","Local segment reference"),Reference("path","Exact immutable entitlement path",false,["entitlementId","pathId"],paths),
                    Text("fromNodeKey","From local node reference"),Text("toNodeKey","To local node reference"),Fraction("fuelFraction","Explicit gross-receipt fuel fraction"),
                    Fraction("lossFraction","Explicit gross-receipt loss fraction"),Text("transitMicroseconds","Nonnegative elapsed transit microseconds")]),evidence]));
        }
        else
        {
            var source=facts.Value;var route=source.GetProperty("route");var routeId=route.Id("id");var definition=route.Id("definition_id");var state=source.GetProperty("stateVersion").GetInt32();
            if(routeId==Guid.Empty||definition==Guid.Empty||state<1||source.GetProperty("definition").Id("id")!=definition||source.GetProperty("definition").Id("route_id")!=routeId)throw Invalid();
            var plans=Rows(source,"plans");if(plans.Select(p=>p.Id("id")).Distinct().Count()!=plans.Length||plans.Select(p=>p.Text("business_key")).Distinct(StringComparer.Ordinal).Count()!=plans.Length)throw Invalid();
            var asOf=TradingInstant.Parse(source.Text("asOf"),"gas_transport_route_source");
            var eligiblePlans=plans.Where(p=>TradingInstant.Parse(p.Text("effective_at"),"gas_transport_route_source")<=asOf).ToArray();
            if(eligiblePlans.Length!=0)
                forms.Add("release",new("Release this route plan's source memberships","RecordGasOperations",Json(new{kind="release",routeId,definitionId=definition,expectedStateVersion=state,admissionPolicy=GasTransportRouteCommands.Release}),[
                    Reference("plan","Exact own route plan and current head",false,["businessKey","expectedPreviousId"],[..eligiblePlans.Select(p=>new GasFormChoice(p.Id("id").ToString(),
                        p.Text("business_key")+" · revision "+p.GetProperty("revision").GetInt32(),new{businessKey=p.Text("business_key"),expectedPreviousId=p.Id("id")}))]),evidence]));
            var segments=Rows(source,"segments");var needed=segments.Select(s=>s.Id("entitlement_id")).ToHashSet();
            var current=capacities.Where(c=>needed.Contains(c.GetProperty("entitlement").Id("id"))).ToArray();
            var effective=!source.GetProperty("hasFutureHeads").GetBoolean()&&current.Length==needed.Count&&current.All(c=>!c.GetProperty("hasFutureHeads").GetBoolean());
            if(effective&&current.All(c=>GasTransportCapacityAssessments.Current(c).Feasible))
            {
                var streamChoices=ImmutableArray.CreateBuilder<GasFormChoice>();var claimChoices=ImmutableArray.CreateBuilder<GasFormChoice>();var usablePaths=new HashSet<(Guid,Guid)>();
                foreach(var capacity in current)
                {
                    var entitlement=capacity.GetProperty("entitlement").Id("id");var scopePaths=segments.Where(s=>s.Id("entitlement_id")==entitlement).Select(s=>s.Id("path_id")).ToHashSet();
                    foreach(var stream in Rows(capacity,"sourcePlans").Where(s=>s.Text("membershipState")!="occupied_v1"))
                    {
                        var claims=Rows(capacity,"claims").Where(c=>c.Id("plan_id")==stream.Id("planRevisionId")).ToArray();
                        if(claims.Length==0||claims.Any(c=>!scopePaths.Contains(c.Id("path_id")))||claims.Select(c=>c.Id("path_id")).Distinct().Count()!=claims.Length)continue;
                        var head=Rows(capacity,"plans").Single(p=>p.Id("id")==stream.Id("planRevisionId"));
                        streamChoices.Add(new(stream.Id("firstPlanRevisionId").ToString(),capacity.GetProperty("entitlement").Text("business_key")+" · "+head.Text("business_key"),
                            new{entitlementId=entitlement,firstPlanRevisionId=stream.Id("firstPlanRevisionId"),planRevisionId=stream.Id("planRevisionId"),expectedMembershipGeneration=stream.GetProperty("membershipGeneration").GetInt32()}));
                        foreach(var claim in claims)
                        {
                            usablePaths.Add((entitlement,claim.Id("path_id")));var path=Rows(capacity,"paths").Single(p=>p.Id("id")==claim.Id("path_id"));
                            claimChoices.Add(new(claim.Id("id").ToString(),head.Text("business_key")+" · "+path.Text("path_key")+" · receipt "+claim.Text("receipt_quantity")+" / delivery "+claim.Text("delivery_quantity"),claim.Id("id")));
                        }
                    }
                }
                if(segments.All(s=>usablePaths.Contains((s.Id("entitlement_id"),s.Id("path_id")))))
                    forms.Add("plan",new("Adopt a complete conserving chosen route plan","RecordGasOperations",Json(new{kind="plan",routeId,definitionId=definition,expectedStateVersion=state,admissionPolicy=GasTransportRouteCommands.Adopt}),[
                        Text("businessKey","Route-plan business reference"),Reference("previous","Previous own plan head (explicitly none for a new stream)",true,["expectedPreviousId"],
                            [..eligiblePlans.Select(p=>new GasFormChoice(p.Id("id").ToString(),p.Text("business_key")+" · revision "+p.GetProperty("revision").GetInt32(),new{expectedPreviousId=p.Id("id")}))]),
                        Array("sources","Complete selected capacity source generations",1,[Reference("source","Exact capacity source",false,["entitlementId","capacityVersionId","capacityGeneration"],
                            [..current.Select(c=>new GasFormChoice(c.GetProperty("entitlement").Id("id").ToString(),c.GetProperty("entitlement").Text("business_key"),
                                new{entitlementId=c.GetProperty("entitlement").Id("id"),capacityVersionId=c.GetProperty("version").Id("id"),capacityGeneration=c.GetProperty("stateVersion").GetInt32()}))])]),
                        Array("sourcePlans","Complete selected logical capacity-plan streams",1,[Reference("plan","Exact current source stream and membership generation",false,
                            ["entitlementId","firstPlanRevisionId","planRevisionId","expectedMembershipGeneration"],streamChoices.ToImmutable())]),
                        Array("flows","One exact native claim per segment (including explicit idle zero)",1,[Select("segmentId","Exact saved route segment",
                            [..segments.Select(s=>new GasFormChoice(s.Id("id").ToString(),s.Text("segment_key"),s.Id("id")))]),Select("claimId","Exact selected native capacity claim",claimChoices.ToImmutable())]),evidence]));
            }
        }
        return new(forms.ToImmutable(),configuration.ToImmutable());
    }
    static JsonElement[] Rows(JsonElement row,string name)
    {var values=row.GetProperty(name);if(values.ValueKind!=JsonValueKind.Array)throw Invalid();return[..values.EnumerateArray()];}
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,GasOwnedOperations.Json);
    static MarketDataException Invalid()=>new("gas_transport_route_source","Retain exact route geometry, current complete capacity sources and explicit membership availability before describing route operations.");
}
