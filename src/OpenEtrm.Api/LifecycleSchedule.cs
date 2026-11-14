using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenEtrm.Automation;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleScheduleDefinition(Guid CloseId,LifecycleTarget Target,string Action,Guid OccurrenceId,Guid PolicyId,int PolicyRevision,
    string KnowledgePolicy,string SettingsPolicy,string InputPolicy)
{
    public const string CurrentKnowledge="current_knowledge_at_execution_v1",SourceKnownSettings="source_known_exact_action_settings_v1",
        SourceOnly="source_derived_only_no_cash_corrections_v1";
    internal void Validate()
    {
        if(CloseId==Guid.Empty||Target is null||Target.ScopeId==Guid.Empty||Target.BindingId==Guid.Empty||
            Target.Handler is not("futures" or "listed_option" or "financial_swap" or "gas_owned")||string.IsNullOrWhiteSpace(Action)||
            OccurrenceId==Guid.Empty||PolicyId==Guid.Empty||PolicyRevision<1||KnowledgePolicy!=CurrentKnowledge||SettingsPolicy!=SourceKnownSettings||InputPolicy!=SourceOnly)
            throw new MarketDataException("lifecycle_schedule_source","Choose an exact recorded close, native target and occurrence, saved action policy and explicit source-only scheduling policies.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleScheduleCreate(JobCreate Schedule,LifecycleScheduleDefinition Definition);

// Only a registered handler can construct the financial selection. Scheduling
// supplies no observed exercise, assignment, title, payment or replacement price.
internal static class LifecycleScheduledSelection
{
    internal static LifecycleActionRequest Create(LifecycleScheduleDefinition definition,LifecycleReadContext context,Guid operation,
        ImmutableArray<LifecycleActionOffer> offers,bool automatic)
    {
        definition.Validate();
        if(context is null||operation==Guid.Empty||offers.IsDefault)throw Invalid();
        var matches=offers.Where(o=>o.Target==definition.Target&&o.Action==definition.Action&&o.OccurrenceId==definition.OccurrenceId).ToArray();
        if(matches.Length!=1||matches[0].Selection is not {} selection||selection.ValueKind!=JsonValueKind.Object)throw Invalid();
        var offer=matches[0];
        if(offer.PolicyId is {} policy&&(policy!=definition.PolicyId||offer.PolicyRevision!=definition.PolicyRevision))throw Invalid();
        foreach(var input in offer.RequiredInputs)
        {
            if(input=="policyId")continue;
            if(input=="correctionDue"&&selection.TryGetProperty(input,out var dues)&&dues.ValueKind==JsonValueKind.Array&&dues.GetArrayLength()==0)continue;
            if(input=="correctionDueAt"&&selection.TryGetProperty("determination",out var determination)&&
                determination.TryGetProperty(input,out var due)&&due.ValueKind==JsonValueKind.Null)continue;
            throw Invalid();
        }
        return new(operation,context,definition.Target,definition.Action,definition.PolicyId,definition.PolicyRevision,automatic,new('0',64),selection.Clone());
        static MarketDataException Invalid()=>new("lifecycle_schedule_source","This occurrence requires one exact configured native action and source-derived inputs. Observed facts and correction due times require explicit user input.");
    }
}
