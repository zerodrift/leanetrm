using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;
using OpenEtrm.Api;

internal static class StorageFacilityMetadataChecks
{
    internal static void Run()
    {
        var empty=new StorageFacilityChoices([],[],[],[],[]);
        var setup=StorageFacilityMetadata.Build(null,empty);
        if(setup.Forms.Count!=0||setup.Configuration.Count!=1)throw new Exception("A facility directory must not offer activity commands without an exact saved facility.");
        var define=setup.Configuration["define"];
        if(define.Permission!="ConfigureFacilityLimits"||define.Base.GetProperty("facilityId").ValueKind!=JsonValueKind.Null)throw new Exception("New physical definitions require explicit setup authority and a backend-generated technical identity.");
        var policy=define.Fields.Single(f=>f.Name=="implementation");
        if(policy.Kind!="select"||policy.Choices.Length!=1||policy.Choices[0].Value.ToString()!="storage_total_physical_step_ratchets_v1")throw new Exception("The installed physical law must be an explicit described choice.");
        if(define.Fields.Any(f=>f.Name is "initialLevel" or "ownedQuantity"))throw new Exception("Initial physical evidence cannot become per-definition stock.");
        if(define.Fields.Single(f=>f.Name=="ratchets").MinItems!=1)throw new Exception("Ratchets require an explicit nonempty ordered grid.");
        var currentVersion=new{id=Guid.NewGuid(),revision=1};
        var port=new{id=Guid.NewGuid(),pipeline_code="synthetic_pipeline",point_code="synthetic_point",direction="inject"};
        var facts=JsonSerializer.SerializeToElement(new{facility=new{id=Guid.NewGuid()},version=currentVersion,stateVersion=1,
            ports=new[]{port},versionBundles=new[]{new{version=currentVersion,ports=new[]{port}}},
            openings=Array.Empty<object>(),activities=Array.Empty<object>(),coverages=Array.Empty<object>(),plan=(object?)null});
        var saved=StorageFacilityMetadata.Build(facts,empty);
        var planPort=saved.Forms["plan"].Fields.Single(f=>f.Name=="activities").Fields.Single(f=>f.Name=="port");
        var tuple=JsonSerializer.SerializeToElement(planPort.Choices.Single().Value,GasOwnedOperations.Json);
        if(!tuple.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(planPort.Members.Order()))throw new Exception("A planned port's opaque tuple must contain exactly its declared members.");
        var rules=saved.Forms["coverage"].Fields.Single(f=>f.Name=="rules");
        if(rules.Kind!="reference"||!rules.Members.SequenceEqual(new[]{"versionId"}))throw new Exception("Measured coverage must explicitly select its exact physical rule version.");
        var incomplete=JsonNode.Parse(facts.GetRawText())!.AsObject();incomplete.Remove("versionBundles");
        var refused=false;
        try{StorageFacilityMetadata.Build(JsonSerializer.SerializeToElement(incomplete),empty);}
        catch(MarketDataException e)when(e.Code=="storage_facility_source"){refused=true;}
        if(!refused)throw new Exception("Missing exact version bundles cannot silently become current-rule metadata.");
        var sourceContext=new ScheduleContext("2032-03-14",1,"2032-03-14T20:00:00Z",Guid.NewGuid());
        var businessAt=DateTimeOffset.Parse("2032-03-14T23:59:59.999999Z");var assetId=Guid.NewGuid();
        var wrongSource=JsonSerializer.SerializeToElement(new{contextId=sourceContext.ContextId,asOf=businessAt,
            facility=new{id=Guid.NewGuid()},version=new{facility_id=assetId}});
        refused=false;
        try{StorageFacilityStore.RequireSource(wrongSource,sourceContext,businessAt,assetId);}
        catch(MarketDataException e)when(e.Code=="storage_facility_source"){refused=true;}
        if(!refused)throw new Exception("A correct context must not admit another physical facility's source.");
        var exactSource=JsonSerializer.SerializeToElement(new{contextId=sourceContext.ContextId,asOf=businessAt,knownAt=sourceContext.KnownAt,
            facility=new{id=assetId},version=new{facility_id=assetId}});
        StorageFacilityStore.RequireSource(exactSource,sourceContext,businessAt,assetId);
        var wrongCut=JsonNode.Parse(exactSource.GetRawText())!.AsObject();wrongCut["knownAt"]="2032-03-14T21:00:00Z";
        refused=false;
        try{StorageFacilityStore.RequireSource(JsonSerializer.SerializeToElement(wrongCut),sourceContext,businessAt,assetId);}
        catch(MarketDataException e)when(e.Code=="storage_facility_context"){refused=true;}
        if(!refused)throw new Exception("An exact source identity must attest the selected knowledge cutoff as well as its business time.");
        refused=false;
        try{StorageFacilityStore.Permission(default);}
        catch(MarketDataException e)when(e.Code=="storage_facility_input"){refused=true;}
        if(!refused)throw new Exception("A missing command kind must be a structured input refusal.");
        var query=" 2021-01-01 synthetic_point ";
        var request=JsonSerializer.SerializeToElement(new{context=new FacilityQuantityContext("2032-03-14","current",null,FacilityQuantityStore.Convention),facilityId=Guid.NewGuid(),sourceSearch=query},GasOwnedOperations.Json);
        var parsed=request.Deserialize<FacilityOperationsReadRequest>(GasOwnedOperations.Json)!;
        var echoed=JsonSerializer.SerializeToElement(parsed,GasOwnedOperations.Json);
        if(!echoed.TryGetProperty("sourceSearch",out var search)||search.GetString()!=query)throw new Exception("A physical source search must retain its exact requested presentation scope.");
        StorageFacilityPresentationChecks.Run();
        Console.WriteLine("PASS facility setup: exact native commands, explicit rule choices and no implicit opening stock");
    }
}
