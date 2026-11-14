using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenEtrm.Automation;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleScheduleDefinition(Guid CloseId,LifecycleTarget Target,string Action,Guid OccurrenceId,Guid PolicyId,int PolicyRevision,
    string KnowledgePolicy,string SettingsPolicy,string InputPolicy,LifecycleScheduleSettings Settings)
{
    public const string CurrentKnowledge="current_knowledge_at_execution_v1",SourceKnownSettings="source_known_exact_action_settings_v1",
        SourceOnly="source_derived_only_no_cash_corrections_v1";
    internal void Validate()
    {
        if(CloseId==Guid.Empty||Settings is null||Target is null||Target.ScopeId==Guid.Empty||Target.BindingId==Guid.Empty||
            Target.Handler is not("futures" or "listed_option" or "financial_swap" or "gas_owned")||string.IsNullOrWhiteSpace(Action)||
            OccurrenceId==Guid.Empty||PolicyId==Guid.Empty||PolicyRevision<1||KnowledgePolicy!=CurrentKnowledge||SettingsPolicy!=SourceKnownSettings||InputPolicy!=SourceOnly)
            throw new MarketDataException("lifecycle_schedule_source","Choose an exact recorded close, native target and occurrence, saved action policy and explicit source-only scheduling policies.");
        Settings.Validate(Target.Handler);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleScheduleSettings(int TradeRevision,[property:JsonRequired]int? OptionProfileRevision,[property:JsonRequired]Guid? SwapProfileId,
    [property:JsonRequired]int? SwapProfileRevision,[property:JsonRequired]Guid? MarginPolicyId,[property:JsonRequired]int? MarginPolicyRevision)
{
    internal void Validate(string handler)
    {
        var valid=TradeRevision>0&&(handler switch{
            "futures"=>OptionProfileRevision is null&&SwapProfileId is null&&SwapProfileRevision is null&&MarginPolicyId is {} margin&&margin!=Guid.Empty&&MarginPolicyRevision>0,
            "listed_option"=>OptionProfileRevision>0&&SwapProfileId is null&&SwapProfileRevision is null&&MarginPolicyId is {} margin&&margin!=Guid.Empty&&MarginPolicyRevision>0,
            "financial_swap"=>OptionProfileRevision is null&&SwapProfileId is {} swap&&swap!=Guid.Empty&&SwapProfileRevision>0&&MarginPolicyId is null&&MarginPolicyRevision is null,
            _=>false});
        if(!valid)throw new MarketDataException("lifecycle_schedule_source","Retain the exact selected trade and product setting versions, including explicit unused choices.");
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
        // Futures deliberately require a user-selected action policy in
        // discovery. The task supplies its exact saved policy, checked again
        // natively; this is not permission to revive a closed product action.
        var needsPolicy=offer.Reason=="lifecycle_policy_required"&&offer.PolicyId is null&&offer.RequiredInputs.Contains("policyId");
        if(!offer.Eligible&&!needsPolicy||offer.ScheduleSettings!=definition.Settings)throw Invalid();
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

public sealed record LifecycleSchedulingChoices(string Operation,ImmutableArray<LifecycleInputChoice> Knowledge,ImmutableArray<LifecycleInputChoice> Settings,
    ImmutableArray<LifecycleInputChoice> Inputs,string TimeZone,string Misfire,string Overlap,int MaximumTimeoutSeconds,int MaximumAttempts,int MaximumNameLength);

public static class LifecycleScheduleJobs
{
    public const string Operation="lifecycle_action_v1";
    internal static LifecycleSchedulingChoices Choices=>new(Operation,
        [new(LifecycleScheduleDefinition.CurrentKnowledge,"Latest certified knowledge when the task runs")],
        [new(LifecycleScheduleDefinition.SourceKnownSettings,"Require the saved product versions")],
        [new(LifecycleScheduleDefinition.SourceOnly,"Use source facts; stop for extra input")],"UTC","run_once","forbid",RuntimeSet.MaxReadSeconds,3,80);
    public static string? FailureCode(Exception failure)
    {
        if(failure is MarketDataException market)return market.Code;
        if(failure is OpenEtrm.Shared.Temporal.TemporalException temporal)return temporal.Code;
        if(failure is PreviewDeniedException)return "lifecycle_schedule_denied";
        if(failure is Npgsql.PostgresException native&&native.SqlState is "P0001" or "42501")
        {
            var code=native.MessageText.Split(':',2)[0];
            if(code.Length<=80&&code.All(c=>char.IsAsciiLetterLower(c)||char.IsAsciiDigit(c)||c=='_')&&
                new[]{"lifecycle_","futures_","option_","swap_","gas_","settlement_","knowledge_","temporal_"}.Any(code.StartsWith))return code;
        }
        return null;
    }
    internal static async Task<LifecycleScheduleResult> Read(Npgsql.NpgsqlConnection connection,PreviewDatabase database,Guid job,CancellationToken ct)
    {
        var value=await LifecycleNative.Read(connection,"SELECT automation.lifecycle_job_result($1)::text",ct,job);
        var result=new LifecycleScheduleResult(job,value.Instant("authorityUntil"),value.GetProperty("outcome"));
        database.RegisterCommitAdmission(connection,result);return result;
    }
    internal static async Task<LifecycleScheduleWrite> Create(Npgsql.NpgsqlConnection connection,PreviewDatabase database,LifecycleScheduleCreate request,CancellationToken ct)
    {
        Validate(request);var body=JsonSerializer.SerializeToElement(request,LifecycleNative.Json);
        var value=await LifecycleNative.Read(connection,"SELECT automation.create_lifecycle_job($1,$2::jsonb)::text",ct,database.Manifest.CatalogId,body.GetRawText());
        var receipt=new LifecycleScheduleWrite(database.Manifest.CatalogId,body,value.Instant("authorityUntil"),
            JsonSerializer.SerializeToElement(new{operationId=request.Schedule.RequestId,job=value.GetProperty("outcome")},LifecycleNative.Json));
        database.RegisterCommitAdmission(connection,receipt);return receipt;
    }
    internal static void Validate(LifecycleScheduleCreate request)
    {
        if(request?.Schedule is null||request.Definition is null)throw Invalid();
        request.Schedule.ValidateSchedule();request.Definition.Validate();
        if(request.Schedule.Operation!=Operation||request.Schedule.TimeoutSeconds>RuntimeSet.MaxReadSeconds||request.Schedule.IntervalSeconds is not null||request.Schedule.ProfileId is not null||request.Schedule.ProfileVersion is not null||
            !TradingInstant.IsSupported(request.Schedule.DueAt)||!TradingInstant.IsSupported(request.Schedule.ExpiresAt)||
            (request.Definition.Target.Handler,request.Definition.Action) is not(("futures","futures.margin") or("listed_option","option.expiry") or("financial_swap","swap.determine")))throw Invalid();
        static MarketDataException Invalid()=>new("lifecycle_schedule_source","Choose one exact recorded close and source-derived native occurrence, with an explicit one-off execution schedule.");
    }
}

internal sealed record LifecycleScheduleWrite(Guid Catalog,JsonElement Request,DateTimeOffset AuthorityUntil,JsonElement Outcome):ICommitAdmission
{
    public async Task RequireCommit(Npgsql.NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT automation.require_lifecycle_job_command($1,$2::jsonb,$3)",Catalog,Request.GetRawText(),AuthorityUntil);
        await command.ExecuteNonQueryAsync(ct);
    }
}

internal sealed record LifecycleScheduleResult(Guid Job,DateTimeOffset AuthorityUntil,JsonElement Outcome):ICommitAdmission
{
    public async Task RequireCommit(Npgsql.NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT automation.require_lifecycle_job_result($1,$2)",Job,AuthorityUntil);
        await command.ExecuteNonQueryAsync(ct);
    }
}
