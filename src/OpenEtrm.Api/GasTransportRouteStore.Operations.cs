using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed partial class GasTransportRouteStore
{
    public async Task<object> Directory(NpgsqlConnection c,FacilityDirectoryRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        await Capture(c,null,ct);var scope=await Bind(c,request.Context,selected,ct);
        var directory=await Native(c,"SELECT security.gas_transport_route_directory($1)::text",ct,Catalog);
        var forms=GasTransportRouteMetadata.Build(null,await Choices(c,null,scope.Context,ct));
        // Native discovery returns its complete authorized bounded directory or
        // refuses the scope. Do not hide valid rows behind another UI cutoff.
        return new{ownerId=await Owner(c,ct),context=scope.Context,at=scope.At,routes=directory,hasMore=false,
            forms=forms.Forms,configuration=forms.Configuration,canRecord=false,
            canConfigure=scope.Working&&forms.Configuration.Count!=0&&await PreviewDatabase.HasPermission(c,"ConfigureGasServices",ct),historical=!scope.Working,
            overviewSections=Array.Empty<object>(),tables=Array.Empty<object>()};
    }

    public async Task<object> Read(NpgsqlConnection c,GasTransportRouteReadRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        if(request.RouteId==Guid.Empty)throw Invalid();
        await Capture(c,null,ct);var scope=await Bind(c,request.Context,selected,ct);
        var facts=await Facts(c,request.RouteId,ct);RequireSource(facts,scope.Context,scope.At,request.RouteId);
        var visible=facts.GetProperty("definition").ValueKind==JsonValueKind.Object;
        var forms=visible?GasTransportRouteMetadata.Build(facts,await Choices(c,facts,scope.Context,ct)):
            new GasTransportRouteForms(ImmutableDictionary<string,FacilityOperationForm>.Empty,ImmutableDictionary<string,FacilityOperationForm>.Empty);
        var assessments=visible?facts.GetProperty("plans").EnumerateArray().Select(p=>GasTransportRouteAssessments.Current(facts,p.Id("id"))).ToArray():[];
        return new{routeId=request.RouteId,ownerId=await Owner(c,ct),context=scope.Context,at=scope.At,facts,forms=forms.Forms,configuration=forms.Configuration,
            canRecord=scope.Working&&forms.Forms.Count!=0&&await PreviewDatabase.HasPermission(c,"RecordGasOperations",ct),
            canConfigure=scope.Working&&forms.Configuration.Count!=0&&await PreviewDatabase.HasPermission(c,"ConfigureGasServices",ct),historical=!scope.Working,
            overviewSections=Overview(facts,assessments),tables=visible?GasTransportRoutePresentation.GraphTables(facts).Concat(assessments.SelectMany(a=>a.Assessment is not null?
                GasTransportRoutePresentation.Tables(a.Assessment,facts,null):GasTransportRoutePresentation.RecordedTables(facts,facts.GetProperty("plans").EnumerateArray().Single(p=>p.Id("id")==a.PlanId)))).ToArray():[]};
    }

    private sealed record Prepared(JsonElement Command,ScheduleContext Context,DateTimeOffset At,DateTimeOffset EffectiveAt,
        JsonElement? Witness,GasTransportRoutePreparedAssessment? Assessment,JsonElement? Source);

    private async Task<Prepared> Prepare(NpgsqlConnection c,GasTransportRouteWriteRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        if(request.Context is null)throw Invalid();
        if(selected is not null||request.Context.KnowledgeMode!="current"||request.Context.Retained is not null)
            throw new MarketDataException("historical_read_only","Return to working data before reviewing a route change.");
        var command=GasTransportRouteCommands.Normalize(request.OperationId,request.Command);
        var at=TradingInstant.Parse(request.EffectiveAt,"gas_transport_route_input");
        if(DateOnly.FromDateTime(at.UtcDateTime)!=DateWorkspace.Date(request.Context.EffectiveOn))throw Invalid();
        var scope=await contexts.Resolve(c,request.Context,selected,"gas_transport_route_context",ct);
        JsonElement? witness=null;JsonElement? source=null;GasTransportRoutePreparedAssessment? assessment=null;
        await using(var save=ScheduleStore.Command(c,"SAVEPOINT gas_transport_route_inputs"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await contexts.BindResolved(c,scope,ct);
            if(command.Text("kind")=="define")_ =GasTransportRouteAssessments.Definition(request.OperationId,command);
            else
            {
                var facts=await Facts(c,command.Id("routeId"),ct);source=facts;RequireSource(facts,scope.Context,scope.At,command.Id("routeId"));
                if(command.Text("kind")=="release")GasTransportRouteAssessments.RequireRelease(facts,command,at);
                else
                {
                    assessment=GasTransportRouteAssessments.Plan(request.OperationId,facts,command);
                    witness=GasTransportRouteAssessments.Witness(assessment);
                }
            }
        }
        finally
        {
            await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT gas_transport_route_inputs;RELEASE SAVEPOINT gas_transport_route_inputs");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        return new(command,scope.Context,scope.At,at,witness,assessment,source);
    }

    private Task<JsonElement> RecordNative(NpgsqlConnection c,Guid operation,JsonElement command,DateTimeOffset at,JsonElement? witness,Guid? context,CancellationToken ct)=>
        Native(c,"SELECT security.record_gas_transport_route($1,$2,$3::jsonb,$4,$5::jsonb,$6::uuid)::text",ct,Catalog,operation,command.GetRawText(),at,
            witness is {} value?value.GetRawText():DBNull.Value,(object?)context??DBNull.Value);

    public async Task<object> Preview(NpgsqlConnection c,GasTransportRouteWriteRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        _=Permission(request.Command);await Capture(c,request.Command.Text("kind"),ct);await RequireOwner(c,request.OwnerId,ct);
        var command=GasTransportRouteCommands.Normalize(request.OperationId,request.Command);var at=TradingInstant.Parse(request.EffectiveAt,"gas_transport_route_input");
        var prior=await Outcome(c,request.OperationId,ct);
        var recorded=prior.GetProperty("found").GetBoolean()||await PriorFact(c,command,at,ct);
        Prepared prepared;
        if(recorded)
        {
            // Accepted facts are proved before fresh sources. An exact retry
            // retains its first outcome without publishing that effect again.
            var scope=await contexts.Resolve(c,request.Context,selected,"gas_transport_route_context",ct);
            prepared=new(command,scope.Context,scope.At,at,null,null,null);
        }
        else prepared=await Prepare(c,request,selected,ct);
        JsonElement nativeOutcome;
        await using(var save=ScheduleStore.Command(c,"SAVEPOINT gas_transport_route_preview"))await save.ExecuteNonQueryAsync(ct);
        try{nativeOutcome=await RecordNative(c,request.OperationId,prepared.Command,prepared.EffectiveAt,prepared.Witness,recorded?null:prepared.Context.ContextId,ct);}
        finally
        {
            await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT gas_transport_route_preview;RELEASE SAVEPOINT gas_transport_route_preview");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var acknowledgement=recorded||!nativeOutcome.GetProperty("changed").GetBoolean();
        return Json(new{ownerId=request.OwnerId,operationId=request.OperationId,routeId=prepared.Command.Id("routeId"),kind=prepared.Command.Text("kind"),
            context=prepared.Context,at=prepared.At,command=prepared.Command,effectiveAt=prepared.EffectiveAt,acknowledgement,
            overviewSections=new[]{new{label="Chosen Transport route review",values=new[]{new{label="Operation",value=acknowledgement?"Acknowledge an already recorded fact":prepared.Command.Text("kind") switch{
                "define"=>"Define an immutable chosen route","release"=>"Release this route plan's source memberships",_=>"Adopt complete current claims as a conserving route"}},
                new{label="Assessment",value=acknowledgement?"This fact is already recorded. Recording acknowledges its accepted outcome; current claims and memberships are unchanged.":prepared.Command.Text("kind") switch{
                    "define"=>"Complete graph and exact path references validated; no route plan or source membership is adopted.",
                    "release"=>"Only this plan's source memberships are released. Source capacity claims remain unchanged; no route flow is assessed.",
                    _=>"Every selected capacity is feasible. Fuel, loss, transit and intermediate-node quantities conserve exactly."}}}}},
            tables=acknowledgement?Array.Empty<object>():GasTransportRoutePresentation.Tables(prepared.Assessment,prepared.Source,prepared.Command)});
    }

    public async Task<JsonElement> Record(NpgsqlConnection c,GasTransportRouteWriteRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        _=Permission(request.Command);await Capture(c,request.Command.Text("kind"),ct);await RequireOwner(c,request.OwnerId,ct);
        var command=GasTransportRouteCommands.Normalize(request.OperationId,request.Command);var at=TradingInstant.Parse(request.EffectiveAt,"gas_transport_route_input");
        var prior=await Outcome(c,request.OperationId,ct);
        if(prior.GetProperty("found").GetBoolean()||await PriorFact(c,command,at,ct))
            return Receipt(request.OperationId,command,await RecordNative(c,request.OperationId,command,at,null,null,ct));
        var prepared=await Prepare(c,request,selected,ct);
        return Receipt(request.OperationId,prepared.Command,await RecordNative(c,request.OperationId,prepared.Command,prepared.EffectiveAt,prepared.Witness,prepared.Context.ContextId,ct));
    }

    private static JsonElement Receipt(Guid operation,JsonElement command,JsonElement outcome)=>Json(new{operationId=operation,routeId=command.Id("routeId"),kind=command.Text("kind"),outcome});
    private static object[] Overview(JsonElement facts,GasTransportRouteAssessmentRead[] assessments)
    {
        var rows=new List<object>{new{label="Retained chosen Transport route",values=new[]{
            new{label="Source",value=facts.GetProperty("hasFutureHeads").GetBoolean()?"A latest known route or source head is future-effective. New adoption requires a supported later context.":"Exact selected graph, capacity claims and source memberships"},
            new{label="Coverage",value="Chosen uniform planning flows with explicit fuel, loss and transit"}}}};
        foreach(var assessment in assessments)
        {
            var plan=facts.GetProperty("plans").EnumerateArray().Single(p=>p.Id("id")==assessment.PlanId);
            rows.Add(new{label=plan.Text("business_key"),values=new[]{new{label="Current assessment",value=assessment.Assessment is not null?
                "Selected capacities are feasible and the complete route conserves exactly":assessment.UnavailableReason??throw Source()}}});
        }
        return rows.ToArray();
    }
}
