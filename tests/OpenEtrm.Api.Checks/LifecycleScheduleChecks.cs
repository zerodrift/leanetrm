using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.Automation;
using OpenEtrm.MarketData;

internal static class LifecycleScheduleChecks
{
    internal static void Run()
    {
        var target=new LifecycleTarget("listed_option",Guid.NewGuid(),Guid.NewGuid());
        var context=new LifecycleReadContext(Guid.NewGuid(),DateTimeOffset.Parse("2026-11-02T16:00:00Z"),new(2026,11,2),Guid.NewGuid());
        var policy=Guid.NewGuid();var occurrence=Guid.NewGuid();var operation=Guid.NewGuid();
        var definition=new LifecycleScheduleDefinition(Guid.NewGuid(),target,"option.expiry",occurrence,policy,2,
            LifecycleScheduleDefinition.CurrentKnowledge,LifecycleScheduleDefinition.SourceKnownSettings,LifecycleScheduleDefinition.SourceOnly,new(1,3,null,null,Guid.NewGuid(),1));
        var schedule=new JobCreate(Guid.NewGuid(),"Synthetic expiry processing",LifecycleScheduleJobs.Operation,context.AsOf,context.AsOf.AddHours(2),null,
            "UTC","run_once","forbid",30,2,null,null);
        var scheduled=new LifecycleScheduleCreate(schedule,definition);
        LifecycleScheduleJobs.Validate(scheduled);
        foreach(var changed in new[]{schedule with{Operation="worker_check"},schedule with{IntervalSeconds=86400},schedule with{ProfileId=Guid.NewGuid(),ProfileVersion=1},
            schedule with{DueAt=context.AsOf.AddTicks(1)},schedule with{TimeoutSeconds=60}})
            Reject(()=>LifecycleScheduleJobs.Validate(scheduled with{Schedule=changed}));
        Reject(()=>LifecycleScheduleJobs.Validate(scheduled with{Definition=definition with{Target=target with{Handler="gas_owned"},Action="gas.loan.borrow"}}));
        var offer=new LifecycleActionOffer(target,"option.expiry","Determine expiry",occurrence,context.AsOf,null,true,null,1,0)
            {ScheduleSettings=definition.Settings,PolicyId=policy,PolicyRevision=2,Selection=JsonSerializer.SerializeToElement(new{evidenceId=target.ScopeId,expectedEventVersion=0,profileRevision=3,lots=(string?)null})};
        var request=LifecycleScheduledSelection.Create(definition,context,operation,[offer],true);
        if(request.OperationId!=operation||request.PolicyId!=policy||request.PolicyRevision!=2||!request.Automatic||request.Context!=context||
            request.Selection.GetRawText()!=offer.Selection.Value.GetRawText())throw new Exception("The source-derived scheduled request changed its selected native contract.");
        foreach(var automatic in new[]{false,true})
            Reject(()=>LifecycleScheduledSelection.Create(definition,context,operation,[offer with{Eligible=false,Reason="closed"}],automatic));
        Reject(()=>LifecycleScheduledSelection.Create(definition,context,operation,[offer with{RequiredInputs=["evidenceId"]}],true));
        Reject(()=>LifecycleScheduledSelection.Create(definition,context,operation,[offer with{PolicyRevision=1}],true));
        Reject(()=>LifecycleScheduledSelection.Create(definition,context,operation,[offer with{ScheduleSettings=definition.Settings with{OptionProfileRevision=4}}],true));
        Reject(()=>LifecycleScheduledSelection.Create(definition,context,operation,[offer,offer],true));
        Reject(()=>LifecycleScheduledSelection.Create(definition,context,operation,[],true));
        var future=offer with{Eligible=false,Reason="lifecycle_policy_required",ScheduleSettings=new(1,null,null,null,Guid.NewGuid(),1),Target=target with{Handler="futures"},Action="futures.margin",PolicyId=null,PolicyRevision=null,RequiredInputs=["policyId","correctionDue"],
            Selection=JsonSerializer.SerializeToElement(new{expectedRevision=1,expectedMarginRevision=1,throughOccurrence=occurrence,correctionDue=Array.Empty<object>()})};
        var futureRequest=LifecycleScheduledSelection.Create(definition with{Target=future.Target,Action="futures.margin",Settings=future.ScheduleSettings!},context,operation,[future],true);
        if(futureRequest.Selection.GetProperty("correctionDue").GetArrayLength()!=0)throw new Exception("A scheduled source-only request invented correction due times.");
        var swap=offer with{ScheduleSettings=new(1,null,Guid.NewGuid(),2,null,null),Target=target with{Handler="financial_swap"},Action="swap.determine",RequiredInputs=["correctionDueAt"],Selection=JsonSerializer.SerializeToElement(new{determination=new{correctionDueAt=(string?)null}})};
        _=LifecycleScheduledSelection.Create(definition with{Target=swap.Target,Action="swap.determine",Settings=swap.ScheduleSettings!},context,operation,[swap],false);
        Reject(()=>LifecycleScheduledSelection.Create(definition with{Target=swap.Target,Action="swap.determine",Settings=swap.ScheduleSettings!},context,operation,[swap with{
            Selection=JsonSerializer.SerializeToElement(new{determination=new{correctionDueAt="2026-11-03T16:00:00Z"}})}],true));
        if(LifecycleScheduleJobs.FailureCode(new Npgsql.PostgresException("futures_margin_correction_due","ERROR","ERROR","P0001"))!="futures_margin_correction_due"||
            LifecycleScheduleJobs.FailureCode(new OpenEtrm.Shared.Temporal.TemporalException("temporal_context","Synthetic missing context"))!="temporal_context"||
            LifecycleScheduleJobs.FailureCode(new PreviewDeniedException())!="lifecycle_schedule_denied"||
            LifecycleScheduleJobs.FailureCode(new Npgsql.NpgsqlException("Synthetic connection loss")) is not null||
            LifecycleScheduleJobs.FailureCode(new OperationCanceledException()) is not null)
            throw new Exception("Known lifecycle input refusals must be retained while connection or cancellation failures remain unresolved for native reconciliation.");
        Console.WriteLine("PASS lifecycle schedule source contract: exact native offer and policy, no observed facts or correction timing invented, ambiguous and missing targets refuse");
    }
    private static void Reject(Action action){try{action();}catch(MarketDataException e)when(e.Code=="lifecycle_schedule_source"){return;}throw new Exception("Expected lifecycle schedule source refusal.");}
}
