using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasTransportCapacityReadRequest([property:JsonRequired]FacilityQuantityContext Context,Guid EntitlementId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasTransportCapacityWriteRequest([property:JsonRequired]FacilityQuantityContext Context,[property:JsonRequired]string OwnerId,
    Guid OperationId,[property:JsonRequired]string EffectiveAt,[property:JsonRequired]JsonElement Command);

internal sealed record GasTransportCapacityAdmission(Guid Catalog,string? Kind,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection c,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.require_gas_transport_capacity_commit($1,$2,$3)",Catalog,(object?)Kind??DBNull.Value,Until);
        await q.ExecuteNonQueryAsync(ct);
    }
}

public sealed partial class GasTransportCapacityStore(PreviewDatabase database)
{
    private readonly OperationalBusinessContext contexts=new(database);
    private Guid Catalog=>database.Manifest.CatalogId;
    internal static string Permission(JsonElement command)
    {
        if(command.ValueKind!=JsonValueKind.Object||!command.TryGetProperty("kind",out var value)||value.ValueKind!=JsonValueKind.String)throw Invalid();
        return value.GetString() switch{"define"=>"ConfigureGasServices","plan"=>"RecordGasOperations",_=>throw Invalid()};
    }
    private static MarketDataException Invalid()=>new("gas_transport_capacity_input","Supply the complete explicit capacity operation, owning account and business context.");
    private static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,GasOwnedOperations.Json);
    private Task<JsonElement> Native(NpgsqlConnection c,string sql,CancellationToken ct,params object[] args)=>LifecycleNative.Read(c,sql,ct,args);
    private Task<JsonElement> Facts(NpgsqlConnection c,Guid entitlement,CancellationToken ct)=>
        Native(c,"SELECT security.read_gas_transport_capacity($1,$2)::text",ct,Catalog,entitlement);
    private Task<JsonElement> Outcome(NpgsqlConnection c,Guid operation,CancellationToken ct)=>
        Native(c,"SELECT security.gas_transport_capacity_status($1,$2)::text",ct,Catalog,operation);
    private async Task<bool> PriorFact(NpgsqlConnection c,JsonElement command,DateTimeOffset at,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.gas_transport_capacity_prior_fact($1,$2::jsonb,$3)",Catalog,command.GetRawText(),at);
        return (bool)(await q.ExecuteScalarAsync(ct))!;
    }
    private async Task Capture(NpgsqlConnection c,string? kind,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.gas_transport_capacity_authority($1,$2)",Catalog,(object?)kind??DBNull.Value);
        var until=new DateTimeOffset((DateTime)(await q.ExecuteScalarAsync(ct))!);
        database.RegisterCommitAdmission(c,new GasTransportCapacityAdmission(Catalog,kind,until));
    }
    private async Task<string> Owner(NpgsqlConnection c,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.gas_transport_capacity_owner($1)",Catalog);
        return (string)(await q.ExecuteScalarAsync(ct))!;
    }
    private async Task RequireOwner(NpgsqlConnection c,string expected,CancellationToken ct)
    {
        if(expected is null||expected!=await Owner(c,ct))
            throw new MarketDataException("gas_transport_capacity_owner_changed","The account changed. Return to the original account to reconcile this request.");
    }
    private async Task<(ScheduleContext Context,DateTimeOffset At,bool Working)> Bind(NpgsqlConnection c,FacilityQuantityContext request,ScheduleContext? selected,CancellationToken ct)
    {
        var scope=await contexts.Resolve(c,request,selected,"gas_transport_capacity_context",ct);
        await contexts.BindResolved(c,scope,ct);return scope;
    }
    public async Task<object> Directory(NpgsqlConnection c,FacilityDirectoryRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        await Capture(c,null,ct);var scope=await Bind(c,request.Context,selected,ct);
        var directory=await Native(c,"SELECT security.gas_transport_capacity_directory($1)::text",ct,Catalog);
        var forms=GasTransportCapacityMetadata.Build(null,await Choices(c,null,scope.Context,ct));
        return new{ownerId=await Owner(c,ct),context=scope.Context,at=scope.At,entitlements=directory.EnumerateArray().Take(200).ToArray(),hasMore=directory.GetArrayLength()>200,
            forms=forms.Forms,configuration=forms.Configuration,canRecord=scope.Working&&await PreviewDatabase.HasPermission(c,"RecordGasOperations",ct),
            canConfigure=scope.Working&&await PreviewDatabase.HasPermission(c,"ConfigureGasServices",ct),historical=!scope.Working,
            overviewSections=Array.Empty<object>(),tables=Array.Empty<object>()};
    }
    public async Task<object> Read(NpgsqlConnection c,GasTransportCapacityReadRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        if(request.EntitlementId==Guid.Empty)throw Invalid();
        await Capture(c,null,ct);var scope=await Bind(c,request.Context,selected,ct);
        var facts=await Facts(c,request.EntitlementId,ct);RequireSource(facts,scope.Context,scope.At,request.EntitlementId);
        var future=facts.GetProperty("hasFutureHeads").GetBoolean();
        var forms=future?new GasTransportCapacityForms(ImmutableDictionary<string,FacilityOperationForm>.Empty,ImmutableDictionary<string,FacilityOperationForm>.Empty):
            GasTransportCapacityMetadata.Build(facts,await Choices(c,facts,scope.Context,ct));
        var assessment=future?null:GasTransportCapacityAssessments.Current(facts);
        return new{entitlementId=request.EntitlementId,ownerId=await Owner(c,ct),context=scope.Context,at=scope.At,facts,forms=forms.Forms,configuration=forms.Configuration,
            canRecord=scope.Working&&!future&&await PreviewDatabase.HasPermission(c,"RecordGasOperations",ct),
            canConfigure=scope.Working&&!future&&await PreviewDatabase.HasPermission(c,"ConfigureGasServices",ct),historical=!scope.Working,
            overviewSections=Overview(facts,assessment),tables=GasTransportCapacityPresentation.Tables(assessment,facts,null)};
    }
    internal static void RequireSource(JsonElement facts,ScheduleContext context,DateTimeOffset at,Guid entitlement)
    {
        try
        {
            var future=facts.GetProperty("hasFutureHeads").GetBoolean();var version=facts.GetProperty("version");
            if(entitlement==Guid.Empty||facts.GetProperty("entitlement").Id("id")!=entitlement||
                (version.ValueKind==JsonValueKind.Null?!future:version.Id("entitlement_id")!=entitlement))throw Source();
            if(facts.Id("contextId")!=context.ContextId||TradingInstant.Parse(facts.Text("asOf"),"gas_transport_capacity_context")!=at||
                context.KnownAt is null||TradingInstant.Parse(facts.Text("knownAt"),"gas_transport_capacity_context")!=TradingInstant.Parse(context.KnownAt,"gas_transport_capacity_context"))
                throw new MarketDataException("gas_transport_capacity_context","The capacity source does not retain the exact selected business and knowledge context.");
        }
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {throw Source();}
    }
    private static MarketDataException Source()=>new("gas_transport_capacity_source","Retain the requested entitlement, exact selected version and complete certified source envelope.");
    private sealed record Prepared(JsonElement Command,ScheduleContext Context,DateTimeOffset At,DateTimeOffset EffectiveAt,
        JsonElement? Witness,GasTransportCapacityAssessment? Assessment,JsonElement? Source);
    private async Task<Prepared> Prepare(NpgsqlConnection c,GasTransportCapacityWriteRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        if(request.Context is null)throw Invalid();
        if(selected is not null||request.Context.KnowledgeMode!="current"||request.Context.Retained is not null)
            throw new MarketDataException("historical_read_only","Return to working data before reviewing a capacity change.");
        var command=GasTransportCapacityCommands.Normalize(request.OperationId,request.Command);
        var at=TradingInstant.Parse(request.EffectiveAt,"gas_transport_capacity_input");
        if(DateOnly.FromDateTime(at.UtcDateTime)!=DateWorkspace.Date(request.Context.EffectiveOn))throw Invalid();
        var scope=await contexts.Resolve(c,request.Context,selected,"gas_transport_capacity_context",ct);
        JsonElement? witness=null;JsonElement? source=null;GasTransportCapacityAssessment? assessment=null;
        await using(var save=ScheduleStore.Command(c,"SAVEPOINT gas_transport_capacity_inputs"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await contexts.BindResolved(c,scope,ct);
            if(command.Text("kind")=="define")
            {
                if(command.GetProperty("expectedStateVersion").GetInt32()==0)
                    assessment=GasTransportCapacity.Assess(GasTransportCapacityAssessments.Definition(request.OperationId,command),[]);
                else
                {
                    var facts=await Facts(c,command.Id("entitlementId"),ct);source=facts;RequireSource(facts,scope.Context,scope.At,command.Id("entitlementId"));
                    assessment=GasTransportCapacityAssessments.Revision(request.OperationId,command,facts);
                }
            }
            else
            {
                var facts=await Facts(c,command.Id("entitlementId"),ct);source=facts;RequireSource(facts,scope.Context,scope.At,command.Id("entitlementId"));
                assessment=GasTransportCapacityAssessments.Plan(facts,command);
                witness=GasTransportCapacityAssessments.Witness(assessment,command.Text("admissionPolicy"));
            }
        }
        finally
        {
            await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT gas_transport_capacity_inputs;RELEASE SAVEPOINT gas_transport_capacity_inputs");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        return new(command,scope.Context,scope.At,at,witness,assessment,source);
    }
    private Task<JsonElement> RecordNative(NpgsqlConnection c,Guid operation,JsonElement command,DateTimeOffset at,JsonElement? witness,Guid? context,CancellationToken ct)=>
        Native(c,"SELECT security.record_gas_transport_capacity($1,$2,$3::jsonb,$4,$5::jsonb,$6::uuid)::text",ct,Catalog,operation,command.GetRawText(),at,
            witness is {} value?value.GetRawText():DBNull.Value,(object?)context??DBNull.Value);
    public async Task<object> Preview(NpgsqlConnection c,GasTransportCapacityWriteRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        _=Permission(request.Command);await Capture(c,request.Command.Text("kind"),ct);await RequireOwner(c,request.OwnerId,ct);
        var command=GasTransportCapacityCommands.Normalize(request.OperationId,request.Command);
        var at=TradingInstant.Parse(request.EffectiveAt,"gas_transport_capacity_input");
        var prior=await Outcome(c,request.OperationId,ct);
        var recorded=prior.GetProperty("found").GetBoolean()||await PriorFact(c,command,at,ct);
        Prepared prepared;
        if(recorded)
        {
            // Recovery proves the accepted fact before resolving fresh business
            // sources. Its original changed:true outcome does not mean that an
            // exact retry will publish that effect again.
            var scope=await contexts.Resolve(c,request.Context,selected,"gas_transport_capacity_context",ct);
            prepared=new(command,scope.Context,scope.At,at,null,null,null);
        }
        else prepared=await Prepare(c,request,selected,ct);
        JsonElement nativeOutcome;
        await using(var save=ScheduleStore.Command(c,"SAVEPOINT gas_transport_capacity_preview"))await save.ExecuteNonQueryAsync(ct);
        try{nativeOutcome=await RecordNative(c,request.OperationId,prepared.Command,prepared.EffectiveAt,prepared.Witness,recorded?null:prepared.Context.ContextId,ct);}
        finally
        {
            await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT gas_transport_capacity_preview;RELEASE SAVEPOINT gas_transport_capacity_preview");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var acknowledgement=recorded||!nativeOutcome.GetProperty("changed").GetBoolean();
        var release=prepared.Command.Text("kind")=="plan"&&prepared.Command.Text("admissionPolicy")==GasTransportCapacityPlanAdmission.EmptyRelease;
        return Json(new{ownerId=request.OwnerId,operationId=request.OperationId,entitlementId=prepared.Command.Id("entitlementId"),kind=prepared.Command.Text("kind"),
            context=prepared.Context,at=prepared.At,command=prepared.Command,effectiveAt=prepared.EffectiveAt,acknowledgement,
            overviewSections=new[]{new{label="Transport capacity review",values=new[]{new{label="Operation",value=acknowledgement?"Acknowledge an already recorded fact":prepared.Command.Text("kind")=="define"?"Publish complete dated capacity":
                    release?"Release all claims of the selected plan":"Replace chosen capacity claims"},
                new{label="Assessment",value=acknowledgement?"This fact is already recorded. Recording acknowledges its accepted outcome; current claims and capacity are unchanged.":
                    prepared.Assessment is null?"Complete capacity definition and native source validated":prepared.Assessment.Feasible?
                    "Within every saved gross capacity limit":release?"The selected plan releases its claims; other plans retain their existing breaches":
                    "Retained chosen claims breach the revised capacity; their quantities are unchanged"}}}},
            tables=acknowledgement?Array.Empty<object>():GasTransportCapacityPresentation.Tables(prepared.Assessment,prepared.Source,prepared.Command)});
    }
    public async Task<JsonElement> Record(NpgsqlConnection c,GasTransportCapacityWriteRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        _=Permission(request.Command);await Capture(c,request.Command.Text("kind"),ct);await RequireOwner(c,request.OwnerId,ct);
        var command=GasTransportCapacityCommands.Normalize(request.OperationId,request.Command);var at=TradingInstant.Parse(request.EffectiveAt,"gas_transport_capacity_input");
        var prior=await Outcome(c,request.OperationId,ct);
        if(prior.GetProperty("found").GetBoolean())return Receipt(request.OperationId,command,await RecordNative(c,request.OperationId,command,at,null,null,ct));
        if(await PriorFact(c,command,at,ct))
            return Receipt(request.OperationId,command,await RecordNative(c,request.OperationId,command,at,null,null,ct));
        var prepared=await Prepare(c,request,selected,ct);
        return Receipt(request.OperationId,prepared.Command,await RecordNative(c,request.OperationId,prepared.Command,prepared.EffectiveAt,prepared.Witness,prepared.Context.ContextId,ct));
    }
    private static JsonElement Receipt(Guid operation,JsonElement command,JsonElement outcome)=>Json(new{operationId=operation,entitlementId=command.Id("entitlementId"),kind=command.Text("kind"),outcome});
    public async Task<JsonElement> Status(NpgsqlConnection c,FacilityStatusRequest request,CancellationToken ct)
    {
        if(request.OperationId==Guid.Empty)throw Invalid();
        // Native status captures both conditional write candidates before its
        // first scope/publication wait. Keep that original bound through owner
        // resolution, including the base-only deadline for an absent receipt.
        var value=await Outcome(c,request.OperationId,ct);
        var admission=StatusAdmission(Catalog,value);
        database.RegisterCommitAdmission(c,admission);await admission.RequireCommit(c,ct);
        await RequireOwner(c,request.OwnerId,ct);if(!value.GetProperty("found").GetBoolean())return value;
        return Json(new{operationId=request.OperationId,entitlementId=value.Id("entitlementId"),kind=value.Text("kind"),outcome=value.GetProperty("outcome"),
            command=GasTransportCapacityCommands.Normalize(request.OperationId,value.GetProperty("command")),effectiveAt=TradingInstant.Parse(value.Text("effectiveAt"),"gas_transport_capacity_context")});
    }
    internal static GasTransportCapacityAdmission StatusAdmission(Guid catalog,JsonElement status)
    {
        try
        {
            var absent=false;
            if(status.TryGetProperty("found",out var found))
            {if(found.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw Source();absent=!found.GetBoolean();}
            string? kind=absent?null:status.Text("kind");if(!absent&&kind is not("define" or "plan"))throw Source();
            return new(catalog,kind,TradingInstant.Parse(status.Text("authorityUntil"),"gas_transport_capacity_source"));
        }
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException){throw Source();}
    }
    private static object[] Overview(JsonElement facts,GasTransportCapacityAssessment? assessment)=>[new{
        label="Commercial Transport capacity",values=new[]{new{label="Source",value=facts.GetProperty("hasFutureHeads").GetBoolean()?
            "A latest known revision is future-effective. Assessment and new changes require a later business context.":"Exact selected entitlement and chosen-plan heads"},
            new{label="Capacity assessment",value=assessment is null?"Unavailable in this business context":assessment.Feasible?"All chosen claims are within the saved limits":"Chosen claims exceed a saved limit"},
            new{label="Coverage",value="Commercial gross receipt and delivery capacity"}}}];
    public static string Refusal(string code)=>code switch{
        "gas_transport_capacity_stale"=>"The entitlement capacity or chosen-plan source changed. Refresh the complete entitlement and review again.",
        "gas_transport_capacity_conflict"=>"This request or business revision has different terms. Reconcile the original request or submit a reviewed revision.",
        "gas_transport_capacity_denied"=>"This operation requires current authority over the complete entitlement, owning book and exact references.",
        "gas_transport_capacity_future_source"=>"A latest known capacity or plan revision is not yet effective in this business context. Select a later business context before changing claims.",
        "gas_transport_capacity_exceeded"=>"The chosen claims exceed an agreement, receipt, delivery or path capacity. Review the exact breached intervals.",
        "gas_transport_capacity_witness"=>"The chosen claims do not retain a complete valid assessment of their exact source and gross capacities.",
        _=>"Supply complete explicit capacity terms, exact reference versions, dated limits, chosen claims and evidence. The operation was refused."};
}
