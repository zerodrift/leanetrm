using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;

internal static class LifecycleDeliveryChoiceChecks
{
    internal static void Run()
    {
        AgentContext();
        var schema=AssistantOperations.Schema(typeof(LifecycleDeliveryChoiceChecks).GetMethod(nameof(GasConfiguration),System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!);
        if(schema["body"]!["properties"]!["selection"]?["contracts"]?["projection_profile"]?["properties"]?["performances"] is null)
            throw new Exception("Gas configuration must advertise its closed native action-specific inputs to the assistant.");
        JsonElement Row(object value)=>JsonSerializer.SerializeToElement(value);
        var future=Row(new{settlement_method="physical",definition_revision_id="definition",contract_id="contract",book_id="book",
            legal_entity_id="owner",legal_entity_revision=2,reference_catalog_id="reference",reference_revision_id="version",
            quantity_code="gas",location_code="hub",currency_code="usd",quote_multiplier="10"});
        var account=Row(new{id="eligible",kind="owned_inventory",book_id="book",legal_entity_id="owner",legal_entity_revision=2,
            reference_catalog_id="reference",reference_revision_id="version",quantity_code="gas",location_code="hub",currency_code="usd",quote_multiplier="10.0"});
        var accounts=ImmutableArray.CreateBuilder<JsonElement>();accounts.Add(account);
        foreach(var (key,value) in new[]{("book_id","other_book"),("legal_entity_id","other_owner"),("reference_revision_id","other_version"),
            ("quantity_code","power"),("location_code","other_hub"),("currency_code","eur"),("quote_multiplier","1"),("kind","operator_balance")})
        {
            var wrong=JsonNode.Parse(account.GetRawText())!;wrong[key]=value;wrong["id"]=key;accounts.Add(JsonSerializer.SerializeToElement(wrong));
        }
        var profile=Row(new{profile=new{id="saved",revision=3,definition_revision_id="definition",contract_id="contract"},gas=new{account_id="eligible"}});
        var wrongProfile=Row(new{profile=new{id="other",revision=1,definition_revision_id="definition",contract_id="other_contract"},gas=new{account_id="eligible"}});
        var selected=LifecyclePhysicalSettings.ForFuture(future,[profile,wrongProfile],accounts.ToImmutable());
        if(selected.GetProperty("deliveryAccounts").EnumerateArray().Single().Text("id")!="eligible"||
            selected.GetProperty("deliveryProfiles").EnumerateArray().Single().GetProperty("profile").Text("id")!="saved")
            throw new Exception("Physical continuation choices must retain the exact held book, legal owner, reference, units, location, currency and quotation scale.");
        var cash=JsonNode.Parse(future.GetRawText())!;cash["settlement_method"]="cash";
        var noDelivery=LifecyclePhysicalSettings.ForFuture(JsonSerializer.SerializeToElement(cash),[profile],accounts.ToImmutable());
        if(noDelivery.GetProperty("deliveryAccounts").GetArrayLength()!=0||noDelivery.GetProperty("deliveryProfiles").GetArrayLength()!=0)
            throw new Exception("Cash-settled futures cannot offer physical ownership accounts.");
        Console.WriteLine("PASS physical continuation choices retain exact legal owner, book, specification and market scale without selecting financial terms");
    }
    private static void GasConfiguration(GasOwnedConfigurationRequest body){}
    private static void AgentContext()
    {
        var catalog=Guid.NewGuid();var contextId=Guid.NewGuid();
        var manifest=new PreviewManifest("local-synthetic-preview",catalog,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),[],null);
        var frame=new InputFrame(Guid.NewGuid(),"Synthetic close",DateTimeOffset.Parse("2026-11-02T15:00:00Z"),new(2026,11,2),[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty);
        var pin=TurnPin.From(JsonSerializer.SerializeToElement(new{mode="current",contextId,cutoff="2026-11-03T00:00:00Z",frameId=frame.Id}),
            JsonDocument.Parse(TurnPin.Record(frame)).RootElement,catalog,manifest);
        var operations=AssistantOperations.ForPage("lifecycle").ToDictionary(o=>o.Path);
        foreach(var path in new[]{"lifecycle/gas/read","lifecycle/gas/configure","lifecycle/futures/delivery/configure","lifecycle/futures/adopt","lifecycle/futures/policies/save"})
        {
            if(!operations.TryGetValue(path,out var operation)||operation.RequiresApproval!=(path!="lifecycle/gas/read")||
                operation.CurrentOnly!=(path!="lifecycle/gas/read"))throw new Exception("The assistant must use the same authorized native lifecycle operation: "+path);
            var body=JsonSerializer.SerializeToNode(new{context=new{contextId,asOf=frame.At,businessDate="2026-11-02"}});
            AssistantWorkspace.RequireLifecycleContext(operation,new(body,[]),pin);
            foreach(var (name,value) in new[]{("contextId",Guid.NewGuid().ToString()),("asOf","2026-11-02T16:00:00Z"),("businessDate","2026-11-03")})
            {
                var changed=body!.DeepClone();changed["context"]![name]=value;
                try{AssistantWorkspace.RequireLifecycleContext(operation,new(changed,[]),pin);throw new Exception("An assistant lifecycle operation replaced the selected source "+name);}
                catch(ToolRefusal error)when(error.Code=="operation_context_mismatch"){}
            }
        }
    }
}
