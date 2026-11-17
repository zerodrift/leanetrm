using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class DeliveryNominationBatchChecks
{
    internal static void Run()
    {
        var context=new FuturesReadRequest(Guid.NewGuid(),PhysicalGasStore.Convention,"current");
        var details=new GasNominationDetails(Guid.NewGuid(),Guid.NewGuid(),"synthetic_batch_request",null,"60.00","2032-01-01T12:00:00Z","portal","Synthetic nomination","Synthetic operator evidence");
        var entry=new DeliveryNominationBatchEntry(Guid.NewGuid(),Guid.NewGuid(),1,"2032-01-02",details);
        var input=new DeliveryNominationBatchInput(context,Guid.NewGuid(),DeliveryNominationBatchContracts.Profile,[entry]);
        Refuse(input with{Profile=""});
        Refuse(input with{AllocationPlanId=Guid.Empty});
        Refuse(input with{Context=context with{KnowledgeMode="retained",ContextId=Guid.NewGuid()}});
        Refuse(input with{Entries=[]});
        Refuse(input with{Entries=default});
        Refuse(input with{Entries=[entry,entry]});
        Refuse(input with{Entries=[entry with{Details=details with{Quantity="-1"}}]});
        Refuse(input with{Entries=[entry with{Details=details with{Quantity=null!}}]});
        Refuse(input with{Entries=[entry with{Details=details with{SubmittedAt=null!}}]});
        Refuse(input with{Entries=[entry with{Details=details with{SubmittedAt="2032-01-01T12:00:00"}}]});
        Refuse(input with{Entries=[entry with{TradeId=Guid.Empty}]});
        Refuse(input with{Entries=[entry with{ExpectedRevision=0}]});
        var normalized=DeliveryNominationBatchContracts.Normalize(input);
        if(normalized.Entries.Single().Details.Quantity!="60")throw new Exception("Reviewed quantity notation must preserve exact numeric value in canonical form.");
        var second=entry with{ObligationId=Guid.NewGuid(),Details=details with{ExternalReference="synthetic_second_request"}};
        var forward=DeliveryNominationBatchContracts.Normalize(input with{Entries=[entry,second]});
        var reverse=DeliveryNominationBatchContracts.Normalize(input with{Entries=[second,entry]});
        if(!forward.Entries.SequenceEqual(reverse.Entries))throw new Exception("Entry order must not change the canonical batch or its child identities.");
        var operation=Guid.NewGuid();
        if(DeliveryNominationBatchContracts.Child(operation,0)!=DeliveryNominationBatchContracts.Child(operation,0)||
           DeliveryNominationBatchContracts.Child(operation,0)==DeliveryNominationBatchContracts.Child(operation,1))throw new Exception("Each accepted batch member needs a stable distinct child operation identity.");
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json=JsonSerializer.SerializeToNode(input,options)!.AsObject();json["unsupported"]="value";
        try{JsonSerializer.Deserialize<DeliveryNominationBatchInput>(json.ToJsonString(),options);throw new Exception("The batch input accepted an unknown business term.");}catch(JsonException){}
        Console.WriteLine("PASS delivery nomination batch: explicit profile/context, exact quantities, closed members, canonical scope and stable child operations");
    }
    private static void Refuse(DeliveryNominationBatchInput input)
    {try{DeliveryNominationBatchContracts.Normalize(input);}catch(MarketDataException e)when(e.Code=="delivery_nomination_batch_input"){return;}throw new Exception("An incomplete or ambiguous nomination batch was accepted.");}
}
