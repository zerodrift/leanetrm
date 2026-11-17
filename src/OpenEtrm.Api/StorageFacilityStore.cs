using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Physical;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FacilityOperationsReadRequest([property:JsonRequired]FacilityQuantityContext Context,Guid FacilityId,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]string? SourceSearch=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FacilityOperationsWriteRequest([property:JsonRequired]FacilityQuantityContext Context,string OwnerId,Guid OperationId,string EffectiveAt,[property:JsonRequired]JsonElement Command);

internal sealed record StorageFacilityAdmission(Guid Catalog,string? Kind,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(connection,"SELECT security.require_storage_facility_commit($1,$2,$3)",Catalog,(object?)Kind??DBNull.Value,Until);
        await q.ExecuteNonQueryAsync(ct);
    }
}

public sealed partial class StorageFacilityStore(PreviewDatabase database)
{
    private readonly FacilityQuantityStore contexts=new(database);
    private Guid Catalog=>database.Manifest.CatalogId;
    internal static string Permission(JsonElement command)
    {
        if(command.ValueKind!=JsonValueKind.Object||!command.TryGetProperty("kind",out var kind)||kind.ValueKind!=JsonValueKind.String)throw Invalid();
        return kind.GetString() switch{
            "define"=>"ConfigureFacilityLimits","opening" or "activity" or "coverage" or "plan"=>"RecordFacilityQuantities",
            _=>throw Invalid()};
    }
    private static MarketDataException Invalid()=>new("storage_facility_input","Supply the exact facility, complete explicit physical command, evidence and business context.");
    private static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,GasOwnedOperations.Json);
    private Task<JsonElement> Native(NpgsqlConnection c,string sql,CancellationToken ct,params object[] args)=>LifecycleNative.Read(c,sql,ct,args);
    private Task<JsonElement> Facts(NpgsqlConnection c,Guid facility,CancellationToken ct)=>Native(c,"SELECT security.read_storage_facility($1,$2)::text",ct,Catalog,facility);
    private Task<JsonElement> Outcome(NpgsqlConnection c,Guid operation,CancellationToken ct)=>Native(c,"SELECT security.storage_facility_status($1,$2)::text",ct,Catalog,operation);
    private async Task<StorageFacilityAdmission> Capture(NpgsqlConnection c,string? kind,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.storage_facility_authority($1,$2)",Catalog,(object?)kind??DBNull.Value);
        var until=new DateTimeOffset((DateTime)(await q.ExecuteScalarAsync(ct))!);
        var admission=new StorageFacilityAdmission(Catalog,kind,until);
        database.RegisterCommitAdmission(c,admission);return admission;
    }
    public async Task<object> Directory(NpgsqlConnection c,FacilityDirectoryRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        var scope=await contexts.Bind(c,request.Context,selected,ct);await Capture(c,null,ct);
        var directory=await Native(c,"SELECT security.storage_facility_directory($1)::text",ct,Catalog);
        var choices=await Choices(c,null,scope.Context,"",ct);var forms=StorageFacilityMetadata.Build(null,choices);
        return new{ownerId=await contexts.Owner(c,ct),context=scope.Context,at=scope.At,facilities=directory.EnumerateArray().Take(200).ToArray(),
            hasMore=directory.GetArrayLength()>200,forms=forms.Forms,configuration=forms.Configuration,
            canRecord=scope.Working&&await PreviewDatabase.HasPermission(c,"RecordFacilityQuantities",ct),
            canConfigure=scope.Working&&await PreviewDatabase.HasPermission(c,"ConfigureFacilityLimits",ct),historical=!scope.Working,
            overviewSections=Array.Empty<object>(),tables=Array.Empty<object>()};
    }
    public async Task<object> Read(NpgsqlConnection c,FacilityOperationsReadRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        if(request.FacilityId==Guid.Empty)throw Invalid();
        var scope=await contexts.Bind(c,request.Context,selected,ct);await Capture(c,null,ct);
        var facts=await Facts(c,request.FacilityId,ct);
        RequireSource(facts,scope.Context,scope.At,request.FacilityId);
        var query=SourceQuery(request.SourceSearch);
        var choices=await Choices(c,facts,scope.Context,query,ct);var forms=StorageFacilityMetadata.Build(facts,choices);
        var coverage=facts.GetProperty("coverage");
        var measured=coverage.ValueKind==JsonValueKind.Null?null:StorageFacilityAssessments.Measured(facts,coverage);
        var sections=Overview(facts,measured);var tables=ReadTables(facts,measured?.Assessment);
        var links=facts.GetProperty("activities").EnumerateArray().Where(a=>a.OptionalId("ledger_id") is not null)
            .Select(a=>new{ledgerId=a.Id("ledger_id"),label=a.Text("business_key")}).DistinctBy(a=>a.ledgerId).ToArray();
        return new{facilityId=request.FacilityId,sourceSearch=query,sourceChoicesTruncated=choices.SourcesTruncated,ownerId=await contexts.Owner(c,ct),context=scope.Context,at=scope.At,facts,assessments=new{measured},forms=forms.Forms,configuration=forms.Configuration,
            canRecord=scope.Working&&await PreviewDatabase.HasPermission(c,"RecordFacilityQuantities",ct),
            canConfigure=scope.Working&&await PreviewDatabase.HasPermission(c,"ConfigureFacilityLimits",ct),historical=!scope.Working,
            overviewSections=sections,tables,allocationLinks=links};
    }
    internal static void RequireSource(JsonElement facts,ScheduleContext context,DateTimeOffset at,Guid facility)
    {
        if(facility==Guid.Empty||facts.GetProperty("facility").Id("id")!=facility||facts.GetProperty("version").Id("facility_id")!=facility)
            throw new MarketDataException("storage_facility_source","Retain the requested physical facility and its exact version membership.");
        if(facts.Id("contextId")!=context.ContextId||TradingInstant.Parse(facts.Text("asOf"),"storage_facility_context")!=at||
            context.KnownAt is null||TradingInstant.Parse(facts.Text("knownAt"),"storage_facility_context")!=TradingInstant.Parse(context.KnownAt,"storage_facility_context"))
            throw new MarketDataException("storage_facility_context","The facility read does not match the exact business and knowledge context.");
    }
    internal static string SourceQuery(string? query)
    {
        if(query is null)return "";
        if(query.Length>200||query.Contains('\0'))throw new MarketDataException("storage_facility_input","Find reported sources with at most 200 characters of point, pipeline, operating date or exact source identity.");
        // Echo the submitted presentation scope exactly. Native lookup may
        // normalize whitespace internally; it does not change the chosen fact.
        return query;
    }
    private sealed record Prepared(JsonElement Command,ScheduleContext Context,DateTimeOffset At,DateTimeOffset EffectiveAt,
        JsonElement? Witness,StorageFacilityAssessment? Assessment);
    private async Task<Prepared> Prepare(NpgsqlConnection c,FacilityOperationsWriteRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        if(request.Context is null)throw Invalid();
        if(selected is not null||request.Context.KnowledgeMode!="current"||request.Context.Retained is not null)
            throw new MarketDataException("historical_read_only","Return to working data before preparing physical facility changes.");
        var command=StorageFacilityCommands.Normalize(request.OperationId,request.Command);
        var at=TradingInstant.Parse(request.EffectiveAt,"storage_facility_input");
        if(DateOnly.FromDateTime(at.UtcDateTime)!=DateWorkspace.Date(request.Context.EffectiveOn))throw Invalid();
        // Capture first: rolling back the source-binding savepoint must not erase
        // the certificate supplied to the unbound native producer.
        var scope=await contexts.ResolveContext(c,request.Context,selected,ct);
        JsonElement? witness=null;StorageFacilityAssessment? assessment=null;
        await using(var save=ScheduleStore.Command(c,"SAVEPOINT storage_facility_inputs"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await contexts.BindResolved(c,scope,ct);
            if(command.Text("kind")=="define")StorageFacilityOperations.ValidateRules(StorageFacilityAssessments.Definition(request.OperationId,command));
            if(command.Text("kind")=="plan")
            {
                var facts=await Facts(c,command.Id("facilityId"),ct);RequireSource(facts,scope.Context,scope.At,command.Id("facilityId"));
                assessment=StorageFacilityAssessments.Plan(facts,command).RequireFeasible();
                witness=StorageFacilityAssessments.Witness(assessment);
            }
        }
        finally
        {
            await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT storage_facility_inputs;RELEASE SAVEPOINT storage_facility_inputs");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        return new(command,scope.Context,scope.At,at,witness,assessment);
    }
    private Task<JsonElement> RecordNative(NpgsqlConnection c,Guid operation,JsonElement command,DateTimeOffset at,JsonElement? witness,Guid? source,CancellationToken ct)=>
        Native(c,"SELECT security.record_storage_facility($1,$2,$3::jsonb,$4,$5::jsonb,$6::uuid)::text",ct,Catalog,operation,command.GetRawText(),at,
            witness is {} value?value.GetRawText():DBNull.Value,(object?)source??DBNull.Value);
    public async Task<object> Preview(NpgsqlConnection c,FacilityOperationsWriteRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        _=Permission(request.Command);
        if(request.Context is null)throw Invalid();
        await contexts.RequireOwner(c,request.OwnerId,ct);await Capture(c,request.Command.Text("kind"),ct);
        var prepared=await Prepare(c,request,selected,ct);
        await using(var save=ScheduleStore.Command(c,"SAVEPOINT storage_facility_preview"))await save.ExecuteNonQueryAsync(ct);
        try{_=await RecordNative(c,request.OperationId,prepared.Command,prepared.EffectiveAt,prepared.Witness,prepared.Context.ContextId,ct);}
        finally
        {
            await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT storage_facility_preview;RELEASE SAVEPOINT storage_facility_preview");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        return Json(new{ownerId=request.OwnerId,operationId=request.OperationId,facilityId=prepared.Command.Id("facilityId"),kind=prepared.Command.Text("kind"),
            context=prepared.Context,at=prepared.At,command=prepared.Command,effectiveAt=prepared.EffectiveAt,
            overviewSections=new[]{new{label="Physical operation review",values=new[]{new{label="Operation",value=prepared.Command.Text("kind")},
                new{label="Assessment",value=prepared.Assessment?.Constraint.ToString()??"Native source and command validation passed"},
                new{label="Financial effect",value="Physical evidence only; title and cash remain independent"}}}},
            tables=AssessmentTables(prepared.Assessment)});
    }
    public async Task<JsonElement> Record(NpgsqlConnection c,FacilityOperationsWriteRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        await contexts.RequireOwner(c,request.OwnerId,ct);
        var command=StorageFacilityCommands.Normalize(request.OperationId,request.Command);
        await Capture(c,command.Text("kind"),ct);
        var at=TradingInstant.Parse(request.EffectiveAt,"storage_facility_input");
        // An accepted original outcome is acknowledged before consulting newer
        // source heads. The producer compares its canonical original payload.
        var prior=await Outcome(c,request.OperationId,ct);
        if(prior.GetProperty("found").GetBoolean())return Receipt(request.OperationId,command,await RecordNative(c,request.OperationId,command,at,null,null,ct));
        var prepared=await Prepare(c,request,selected,ct);
        var outcome=await RecordNative(c,request.OperationId,prepared.Command,prepared.EffectiveAt,prepared.Witness,prepared.Context.ContextId,ct);
        return Receipt(request.OperationId,prepared.Command,outcome);
    }
    private static JsonElement Receipt(Guid operation,JsonElement command,JsonElement outcome)=>Json(new{
        operationId=operation,facilityId=command.Id("facilityId"),kind=command.Text("kind"),outcome});
    public async Task<JsonElement> Status(NpgsqlConnection c,FacilityStatusRequest request,CancellationToken ct)
    {
        await contexts.RequireOwner(c,request.OwnerId,ct);
        if(request.OperationId==Guid.Empty)throw Invalid();
        var value=await Outcome(c,request.OperationId,ct);
        if(!value.GetProperty("found").GetBoolean())return value;
        await Capture(c,value.Text("kind"),ct);
        return Json(new{operationId=request.OperationId,facilityId=value.Id("facilityId"),kind=value.Text("kind"),outcome=value.GetProperty("outcome"),
            command=StorageFacilityCommands.Normalize(request.OperationId,value.GetProperty("command")),effectiveAt=TradingInstant.Parse(value.Text("effectiveAt"),"storage_facility_context")});
    }
    internal static object[] AssessmentTables(StorageFacilityAssessment? assessment)=>assessment is null?[]:[new{
        label="Physical inventory and gross rates",columns=new[]{"From","Through","Opening","Closing","Injection / hour","Withdrawal / hour","Assessment"},
        rows=assessment.Intervals.Select(row=>new[]{row.From.ToString("O"),row.To.ToString("O"),row.Opening.ToExactString(),row.Closing.ToExactString(),
            row.InjectionPerHour.ToExactString(),row.WithdrawalPerHour.ToExactString(),row.Violations.IsEmpty?"Within limits":Breaches(row.Violations) }).ToArray(),instantColumns=new[]{0,1}}];
    private static string Breaches(ImmutableArray<string> violations)=>string.Join(", ",violations.Select(code=>code switch{
        "inventory_minimum"=>"Inventory below minimum","inventory_maximum"=>"Inventory above maximum",
        "injection_rate"=>"Injection rate exceeded","withdrawal_rate"=>"Withdrawal rate exceeded",
        "simultaneous_flow"=>"Simultaneous injection and withdrawal are not permitted",
        _=>throw new MarketDataException("storage_facility_source","The selected assessment has an unsupported physical constraint.")}));
    private static object[] Overview(JsonElement facts,StorageFacilityAssessmentRead? measured)
    {
        var version=facts.GetProperty("version");
        return [new{label="Current physical definition",values=new[]{new{label="Quantity basis",value=version.Text("quantity_code")},
            new{label="Physical inventory range",value=version.Text("minimum")+" to "+version.Text("maximum")},new{label="Operating clock",value=version.Text("clock_code")}}},
            new{label="Measured physical inventory",values=new[]{new{label="Assessment",value=measured?.Constraint.ToString()??"Unassessed"},
                new{label="Closing level for reporting window",value=measured?.Assessment?.ClosingLevel?.ToExactString()??"Unavailable"},
                new{label="Known breaches",value=measured?.Assessment is {} a&&!a.Violations.IsEmpty?Breaches(a.Violations):measured?.Constraint==FacilityConstraintState.WithinLimit?"None in assessed window":"Assessment incomplete"},
                new{label="Source state",value=measured?.UnavailableReason??(facts.GetProperty("linkedSourceCorrection").GetBoolean()?"Source reconciliation required":"Exact retained source")},
                new{label="Chosen plan",value=facts.GetProperty("plan").ValueKind==JsonValueKind.Null?"No chosen plan recorded":facts.GetProperty("requiresReassessment").GetBoolean()?"Reassessment required":"Retained independently from actual activity"}}}];
    }
    public static string Refusal(string code)=>code switch{
        "storage_facility_stale"=>"The facility source changed. Refresh its exact state and review the operation again.",
        "storage_facility_conflict"=>"This request or source revision has different terms. Reconcile the original request or review a new revision.",
        "storage_facility_denied"=>"This operation requires current authority over the complete physical facility and exact references.",
        "storage_facility_rule_transition" or "storage_facility_scope_change"=>"The selected physical basis or rule changes across this window. Select a supported exact window; an explicit continuity operation is required.",
        "storage_facility_witness"=>"The physical plan does not retain a complete valid assessment of the selected facility, sources and ratchets.",
        "storage_facility_flow_source"=>"Select one compatible reported flow revision with its exact physical point, direction, basis and interval.",
        "storage_facility_observation_time"=>"Measured evidence must end by the operation's effective time and selected knowledge cutoff. Record future activity as a chosen plan.",
        _=>"Supply complete explicit physical terms, exact source versions, intervals and evidence. The operation was refused."};
}
