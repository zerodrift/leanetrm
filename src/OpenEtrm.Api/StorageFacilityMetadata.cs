using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData.Physical;

namespace OpenEtrm.Api;

internal sealed record FacilityOperationForm(string Label,string Permission,JsonElement Base,ImmutableArray<GasFormField> Fields);
internal sealed record StorageFacilityChoices(ImmutableArray<GasFormChoice> Facilities,ImmutableArray<GasFormChoice> Quantities,
    ImmutableArray<GasFormChoice> Clocks,ImmutableArray<GasFormChoice> Connections,ImmutableArray<GasFormChoice> Sources)
{public bool SourcesTruncated{get;init;}}
internal sealed record StorageFacilityForms(ImmutableDictionary<string,FacilityOperationForm> Forms,
    ImmutableDictionary<string,FacilityOperationForm> Configuration);

// Descriptions select exact native references. The browser never interprets a
// physical policy, derives inventory or substitutes an opening observation.
internal static class StorageFacilityMetadata
{
    internal static StorageFacilityForms Build(JsonElement? facts,StorageFacilityChoices choices)
    {
        var facility=facts?.GetProperty("facility").Id("id");
        var version=facts?.GetProperty("version");
        var head=facts?.GetProperty("stateVersion").GetInt32()??0;
        GasFormField Text(string name,string label,bool nullable=false)=>new(name,label,"text",nullable,[],[],[]);
        GasFormField Instant(string name,string label)=>new(name,label,"instant",false,[],[],[]);
        GasFormField Select(string name,string label,ImmutableArray<GasFormChoice> values)=>new(name,label,"select",false,values,[],[]);
        GasFormField Policy(string name,string label,string value,string explanation)=>Select(name,label,[new(value,explanation,value)]);
        GasFormField Reference(string name,string label,bool nullable,ImmutableArray<string> members,ImmutableArray<GasFormChoice> values)=>new(name,label,"reference",nullable,values,[],members);
        GasFormField Array(string name,string label,int minimum,ImmutableArray<GasFormField> fields)=>new(name,label,"array",false,[],fields,[]){MinItems=minimum};
        JsonElement Pins(string kind,params (string Key,object? Value)[] extra)
        {
            var value=new Dictionary<string,object?>{{"kind",kind},{"facilityId",facility},{"expectedStateVersion",head}};
            foreach(var pair in extra)value.Add(pair.Key,pair.Value);
            return JsonSerializer.SerializeToElement(value,GasOwnedOperations.Json);
        }
        var define=new FacilityOperationForm("Publish physical facility definition","ConfigureFacilityLimits",
            Pins("define",("expectedPreviousId",version?.Id("id"))),[
                Reference("facility","Facility and commodity at exact reference version",false,["referenceCatalogId","referenceRevisionId","facilityCode","commodity"],choices.Facilities),
                Select("quantity","Native quantity basis",choices.Quantities),Select("clock","Facility operating clock",choices.Clocks),
                Text("minimum","Minimum total physical inventory"),Text("maximum","Maximum total physical inventory"),
                Policy("implementation","Physical rule profile",StorageFacilityRules.Version,"Total physical inventory with absolute step ratchets"),
                Policy("inventoryBasis","Inventory basis",StorageFacilityRules.TotalPhysical,"Total physical inventory; title tracked separately"),
                Policy("rateBasis","Rate basis",StorageFacilityRules.ElapsedHour,"Native quantity per actual elapsed hour"),
                Policy("simultaneousFlow","Simultaneous flow",StorageFacilityRules.SeparateDirections,"Separate positive injection and withdrawal"),
                Policy("lossTreatment","Loss treatment",StorageFacilityRules.NoLoss,"No implicit loss or conversion"),
                Array("ports","Physical pipeline ports",1,[Reference("connection","Exact pipeline, zone and point",false,["pipeline","zone","point","product"],choices.Connections),
                    Select("direction","Facility movement direction",[new("inject","Injection","inject"),new("withdraw","Withdrawal","withdraw")]),
                    Select("contractDirection","Linked contract's physical direction",[new("receive","Receive at the point",1),new("deliver","Deliver at the point",-1)])]),
                Array("ratchets","Ordered inventory ratchet bands",1,[Text("lower","Lower inventory bound"),Text("upper","Upper inventory bound"),Text("injectionPerHour","Maximum injection per elapsed hour"),Text("withdrawalPerHour","Maximum withdrawal per elapsed hour")]),
                Text("evidence","Definition evidence")]);
        var forms=ImmutableDictionary.CreateBuilder<string,FacilityOperationForm>();
        if(facts is {} source&&version is {} saved)
        {
            ImmutableArray<JsonElement> Rows(string key)=>[..source.GetProperty(key).EnumerateArray()];
            ImmutableArray<GasFormChoice> Heads(string key)=>[..Rows(key).Select(row=>new GasFormChoice(row.Id("id").ToString(),row.Text("business_key")+" · revision "+row.GetProperty("revision").GetInt32(),new{expectedPreviousId=row.Id("id")}))];
            var openings=Rows("openings");var activities=Rows("activities");
            var openingChoices=openings.Select(row=>new GasFormChoice(row.Id("id").ToString(),row.Text("business_key")+" · "+row.Text("quantity"),new{openingRevisionId=row.Id("id")})).ToImmutableArray();
            var currentPorts=Rows("ports");
            if(!source.TryGetProperty("versionBundles",out var retainedBundles)||retainedBundles.ValueKind!=JsonValueKind.Array)
                throw new OpenEtrm.MarketData.MarketDataException("storage_facility_source","Retain the complete exact physical definition bundles before describing operations.");
            var bundles=retainedBundles.EnumerateArray().ToArray();
            if(bundles.Count(bundle=>bundle.GetProperty("version").Id("id")==saved.Id("id"))!=1)
                throw new OpenEtrm.MarketData.MarketDataException("storage_facility_source","The current physical definition requires one exact retained rule and port bundle.");
            var portChoices=bundles.SelectMany(bundle=>bundle.GetProperty("ports").EnumerateArray().Select(row=>new GasFormChoice(row.Id("id").ToString(),
                row.Text("pipeline_code")+" / "+row.Text("point_code")+" · "+row.Text("direction")+" · physical revision "+bundle.GetProperty("version").GetProperty("revision").GetInt32(),
                new{versionId=bundle.GetProperty("version").Id("id"),portId=row.Id("id"),direction=row.Text("direction")}))).ToImmutableArray();
            var plannedPorts=currentPorts.Select(row=>new GasFormChoice(row.Id("id").ToString(),row.Text("pipeline_code")+" / "+row.Text("point_code")+" · "+row.Text("direction"),
                new{portId=row.Id("id"),direction=row.Text("direction")})).ToImmutableArray();
            var ruleChoices=bundles.Select(bundle=>new GasFormChoice(bundle.GetProperty("version").Id("id").ToString(),
                "Physical definition revision "+bundle.GetProperty("version").GetProperty("revision").GetInt32(),new{versionId=bundle.GetProperty("version").Id("id")})).ToImmutableArray();
            GasFormField Previous(string key)=>Reference("previous","Previous source revision (explicitly none for a new fact)",true,["expectedPreviousId"],Heads(key));
            var evidence=Text("evidence","Source evidence");
            forms.Add("opening",new("Record dated opening physical evidence","RecordFacilityQuantities",Pins("opening"),[
                Text("businessKey","Opening business reference"),Previous("openings"),Instant("observedAt","Opening observation time"),Text("quantity","Observed physical inventory"),evidence]));
            forms.Add("activity",new("Record measured physical activity","RecordFacilityQuantities",Pins("activity"),[
                Text("businessKey","Activity business reference"),Previous("activities"),Reference("port","Exact physical port",false,["versionId","portId","direction"],portChoices),
                Instant("from","Activity starts"),Instant("through","Activity ends"),Text("quantity","Observed quantity (or explicitly none for a linked source)",true),
                Reference("source","Existing reported flow (or explicitly none)",true,["sourceRevisionId"],choices.Sources),evidence]));
            forms.Add("coverage",new("Record measured reporting coverage","RecordFacilityQuantities",Pins("coverage"),[
                Text("businessKey","Coverage business reference"),Previous("coverages"),Reference("rules","Exact physical rule version",false,["versionId"],ruleChoices),Reference("opening","Dated opening evidence",true,["openingRevisionId"],openingChoices),
                Instant("from","Reporting window starts"),Instant("through","Reporting window ends"),
                Select("statement","Reporting completeness",[new("complete","All physical activity in this window is reported",StorageFacilityCoverage.CompleteActual),new("incomplete","Reporting is incomplete",StorageFacilityCoverage.IncompleteActual)]),
                Array("activityRevisionIds","Exact measured activity revisions",0,[Select("id","Reported activity",[..activities.Select(row=>new GasFormChoice(row.Id("id").ToString(),row.Text("business_key")+" · "+row.Text("quantity"),row.Id("id")))])]),evidence]));
            var plan=source.GetProperty("plan");
            forms.Add("plan",new("Review and admit a complete chosen plan","RecordFacilityQuantities",Pins("plan",("versionId",saved.Id("id")),("expectedPreviousId",plan.ValueKind==JsonValueKind.Null?null:plan.Id("id"))),[
                Reference("opening","Dated opening evidence",false,["openingRevisionId"],openingChoices),Instant("from","Plan starts"),Instant("through","Plan ends"),
                Policy("coverageStatement","Chosen plan coverage",StorageFacilityCoverage.ChosenPlan,"Listed activities are complete; gaps are idle"),
                Array("activities","Chosen physical activity",0,[Reference("port","Exact physical port",false,["portId","direction"],plannedPorts),Instant("from","Starts"),Instant("through","Ends"),Text("quantity","Planned quantity")]),evidence]));
        }
        return new(forms.ToImmutable(),ImmutableDictionary<string,FacilityOperationForm>.Empty.Add("define",define));
    }
}
