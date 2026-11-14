using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class LifecycleScheduleChecks
{
    internal static void Run()
    {
        var target=new LifecycleTarget("listed_option",Guid.NewGuid(),Guid.NewGuid());
        var context=new LifecycleReadContext(Guid.NewGuid(),DateTimeOffset.Parse("2026-11-02T16:00:00Z"),new(2026,11,2),Guid.NewGuid());
        var policy=Guid.NewGuid();var occurrence=Guid.NewGuid();var operation=Guid.NewGuid();
        var definition=new LifecycleScheduleDefinition(Guid.NewGuid(),target,"option.expiry",occurrence,policy,2,
            LifecycleScheduleDefinition.CurrentKnowledge,LifecycleScheduleDefinition.SourceKnownSettings,LifecycleScheduleDefinition.SourceOnly);
        var offer=new LifecycleActionOffer(target,"option.expiry","Determine expiry",occurrence,context.AsOf,null,true,null,1,0)
            {PolicyId=policy,PolicyRevision=2,Selection=JsonSerializer.SerializeToElement(new{evidenceId=target.ScopeId,expectedEventVersion=0,profileRevision=3,lots=(string?)null})};
        var request=LifecycleScheduledSelection.Create(definition,context,operation,[offer],true);
        if(request.OperationId!=operation||request.PolicyId!=policy||request.PolicyRevision!=2||!request.Automatic||request.Context!=context||
            request.Selection.GetRawText()!=offer.Selection.Value.GetRawText())throw new Exception("The source-derived scheduled request changed its selected native contract.");
        Reject(()=>LifecycleScheduledSelection.Create(definition,context,operation,[offer with{RequiredInputs=["evidenceId"]}],true));
        Reject(()=>LifecycleScheduledSelection.Create(definition,context,operation,[offer with{PolicyRevision=1}],true));
        Reject(()=>LifecycleScheduledSelection.Create(definition,context,operation,[offer,offer],true));
        Reject(()=>LifecycleScheduledSelection.Create(definition,context,operation,[],true));
        var future=offer with{Target=target with{Handler="futures"},Action="futures.margin",PolicyId=null,PolicyRevision=null,RequiredInputs=["policyId","correctionDue"],
            Selection=JsonSerializer.SerializeToElement(new{expectedRevision=1,expectedMarginRevision=1,throughOccurrence=occurrence,correctionDue=Array.Empty<object>()})};
        var futureRequest=LifecycleScheduledSelection.Create(definition with{Target=future.Target,Action="futures.margin"},context,operation,[future],true);
        if(futureRequest.Selection.GetProperty("correctionDue").GetArrayLength()!=0)throw new Exception("A scheduled source-only request invented correction due times.");
        var swap=offer with{Target=target with{Handler="financial_swap"},Action="swap.determine",RequiredInputs=["correctionDueAt"],Selection=JsonSerializer.SerializeToElement(new{determination=new{correctionDueAt=(string?)null}})};
        _=LifecycleScheduledSelection.Create(definition with{Target=swap.Target,Action="swap.determine"},context,operation,[swap],false);
        Reject(()=>LifecycleScheduledSelection.Create(definition with{Target=swap.Target,Action="swap.determine"},context,operation,[swap with{
            Selection=JsonSerializer.SerializeToElement(new{determination=new{correctionDueAt="2026-11-03T16:00:00Z"}})}],true));
        Console.WriteLine("PASS lifecycle schedule source contract: exact native offer and policy, no observed facts or correction timing invented, ambiguous and missing targets refuse");
    }
    private static void Reject(Action action){try{action();}catch(MarketDataException e)when(e.Code=="lifecycle_schedule_source"){return;}throw new Exception("Expected lifecycle schedule source refusal.");}
}
