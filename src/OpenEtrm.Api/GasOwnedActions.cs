using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

internal sealed class GasOwnedActions(PreviewDatabase database):ILifecycleActionHandler
{
    public string Kind=>GasOwnedOperations.Handler;
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","ReadGasOwnedHoldings","RecordGasOwnedHoldings"];
    private (string Command,JsonElement Body) Inputs(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,Kind);
        if(!GasOwnedOperations.Commands.TryGetValue(request.Action,out var command))throw GasOwnedOperations.Error("Select a registered gas lifecycle action.");
        var body=GasOwnedOperations.Selection(command,request.Selection);
        var key=command switch{"reserve" or "dispatch" or "loss"=>"lotId","release"=>"reservationId","repay"=>"loanId","operator_settle"=>"positionId","fee_payment"=>"feeId",_=>null};
        if(request.Target.BindingId!=request.Target.ScopeId&&(key is null||body.Id(key)!=request.Target.BindingId))
            throw GasOwnedOperations.Error("The selected native gas occurrence must match its reviewed target.");
        return(command,body);
    }
    private async Task<string> Hash(NpgsqlConnection connection,LifecycleActionRequest request,string command,JsonElement body,CancellationToken ct)
    {
        await using var query=ScheduleStore.Command(connection,"SELECT security.gas_owned_review_hash($1,$2,$3,$4::jsonb,$5,$6,$7,$8,$9)",
            database.Manifest.CatalogId,request.Target.ScopeId,command,body.GetRawText(),request.Context.AsOf,request.Context.ContextId,request.PolicyId,request.PolicyRevision,request.Automatic);
        return(string)(await query.ExecuteScalarAsync(ct))!;
    }
    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var(command,body)=Inputs(request);
        var admission=await GasOwnedOperations.Admission(connection,database,command,body,request.Automatic,ct);
        await LifecycleNative.Source(connection,database,request.Context,ct);
        // The native producer is the planner's independent validation boundary.
        // Generated effect identities are intentionally absent from the review.
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_owned_review"))await save.ExecuteNonQueryAsync(ct);
        JsonElement planned;
        try
        {
            planned=await GasOwnedOperations.Record(connection,database,request.OperationId,request.Context,request.Target.ScopeId,command,body,request.PolicyId,request.PolicyRevision,request.Automatic,ct);
            await using var constraints=ScheduleStore.Command(connection,"SET CONSTRAINTS ALL IMMEDIATE");await constraints.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await using var rollback=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_owned_review;RELEASE SAVEPOINT gas_owned_review");
            await rollback.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var hash=await Hash(connection,request,command,body,ct);
        var offer=new LifecycleActionOffer(request.Target,request.Action,Label(request.Action),request.Target.BindingId,request.Context.AsOf,null,true,null,1,1)
            {PolicyId=request.PolicyId,PolicyRevision=request.PolicyRevision,Selection=body};
        var details=JsonSerializer.SerializeToElement(new{selection=body,effects=planned.TryGetProperty("effect",out var effect)?effect:(JsonElement?)null,
            willChange=planned.GetProperty("changed").GetBoolean(),acknowledgedRevision=planned.GetProperty("acknowledgedRevision").GetInt32(),currentRevision=planned.GetProperty("currentRevision").GetInt32()},GasOwnedOperations.Json);
        var review=new LifecycleActionReview(offer,hash,details,database.Manifest.CatalogId,admission.Until);
        await admission.RequireCommit(connection,ct);return review;
    }
    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var(command,body)=Inputs(request);
        var admission=await GasOwnedOperations.Admission(connection,database,command,body,request.Automatic,ct);
        if(await GasOwnedOperations.Receipt(connection,database,request.OperationId,request.Context,request.Target.ScopeId,command,body,request.PolicyId,request.PolicyRevision,request.Automatic,ct) is {} prior)
            return new(prior,admission);
        await LifecycleNative.Source(connection,database,request.Context,ct);
        if(await Hash(connection,request,command,body,ct)!=request.PreviewHash)
            throw new MarketDataException("gas_owned_preview_stale","Review the exact gas source, policy and operation inputs again.");
        var result=await GasOwnedOperations.Record(connection,database,request.OperationId,request.Context,request.Target.ScopeId,command,body,request.PolicyId,request.PolicyRevision,request.Automatic,ct);
        return new(result,admission);
    }
    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,CancellationToken ct)
    {
        if(target is null&&!await PreviewDatabase.HasPermission(connection,"ReadGasOwnedHoldings",ct))return [];
        var state=await GasOwnedOperations.Read(connection,database,context,target?.ScopeId,ct);
        var result=ImmutableArray.CreateBuilder<LifecycleActionOffer>();
        foreach(var account in state.GetProperty("accounts").EnumerateArray())
        foreach(var action in GasOwnedOperations.Commands.Keys.Order(StringComparer.Ordinal))
        {
            var operatorAccount=account.Text("kind")=="operator_balance";
            if(action.StartsWith("gas.operator.",StringComparison.Ordinal)!=operatorAccount&&action!="gas.cash.record_payment")continue;
            var scope=new LifecycleTarget(Kind,account.Id("id"),account.Id("id"));
            if(target is not null&&target.BindingId!=scope.BindingId)continue;
            var policies=state.GetProperty("policies").EnumerateArray().Where(p=>p.Id("account_id")==scope.ScopeId&&p.Text("action")==action)
                .OrderByDescending(p=>p.GetProperty("revision").GetInt32()).ToArray();
            var policy=policies.FirstOrDefault();
            var reason=policies.Length==0?"lifecycle_configuration_required":context.AsOf<policy.Instant("from_at")||context.AsOf>=policy.Instant("until_at")?"lifecycle_action_window":null;
            result.Add(new(scope,action,Label(action),scope.BindingId,context.AsOf,null,reason is null,reason,1,1){
                Subject=new(account.Text("code"),operatorAccount?"Operator gas balance":"Owned gas inventory",account.Id("book_id"),account.Text("book_name")),
                PolicyId=policies.Length==0?null:policy.Id("policy_id"),PolicyRevision=policies.Length==0?null:policy.GetProperty("policy_revision").GetInt32(),
                RequiredInputs=Fields(action)});
        }
        return result.ToImmutable();
    }
    internal static string Label(string action)=>action switch{
        "gas.owned.receive_title"=>"Accept title to gas","gas.owned.reserve"=>"Reserve owned gas","gas.owned.release"=>"Release reservation",
        "gas.owned.dispatch"=>"Record delivery from owned gas","gas.owned.loss"=>"Record evidenced gas loss","gas.operator.record"=>"Recognize operator claim or debt",
        "gas.operator.settle"=>"Settle operator claim or debt","gas.loan.borrow"=>"Accept title-transferring gas loan","gas.loan.repay"=>"Return borrowed gas",
        "gas.cash.record_payment"=>"Record gas cash payment",_=>throw GasOwnedOperations.Error("Unknown gas action.")};
    internal static ImmutableArray<string> Fields(string action)=>GasOwnedOperations.Commands[action] switch{
        "receive_title"=>["evidenceId","quantity","titleAt","availableAt","originPolicy","carrying"],
        "operator_claim"=>["evidenceId","quantity","effectiveAt","dueAt","carrying","settlementMode","cashout"],
        "borrow"=>["agreementId","evidenceId","quantity","titleAt","availableAt","returnAt","returnMarkCurve","returnMarkContractId","carrying","feeAmount","feeDueAt","lenderId","lenderRevision"],
        "reserve"=>["lotId","businessKey","quantity","effectiveAt"],"release"=>["reservationId","effectiveAt"],
        "dispatch" or "loss"=>["lotId","evidenceId","quantity","reservationId","effectiveAt"],
        "repay"=>["loanId","lotId","evidenceId","quantity","reservationId","effectiveAt"],
        "operator_settle"=>["positionId","quantity","ownedAccountId","lotId","reservationId","evidenceId","effectiveAt","availableAt","sourceObservationId"],
        "fee_payment"=>["feeId","businessKey","expectedRevision","paidAt","amount","evidence"],_=>[]};
}
