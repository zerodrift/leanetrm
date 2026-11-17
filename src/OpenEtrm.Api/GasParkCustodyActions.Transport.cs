using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

internal sealed record GasParkCustodyWriteAdmission(Guid Catalog,Guid Trade,DateTimeOffset Until,DateTimeOffset? InventoryUntil):ICommitAdmission
{
    internal DateTimeOffset ReviewUntil(bool customer)=>customer?
        InventoryUntil is {} inventory?(inventory<Until?inventory:Until):throw new PreviewDeniedException():Until;
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.require_gas_park_custody_commit($1,$2,$3,$4::timestamptz)",
            Catalog,Trade,Until,(object?)InventoryUntil??DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
}

internal sealed partial class GasParkCustodyActions:ILifecycleActionHandler
{
    readonly PreviewDatabase database;
    internal GasParkCustodyActions(PreviewDatabase database)=>this.database=database;
    public string Kind=>Handler;
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","ReadGasOperations","RecordGasOperations"];

    internal static string Refusal(string code)=>code switch
    {
        "gas_custody_denied"=>"Current authority or the original admission no longer permits this custody operation.",
        "gas_custody_conflict"=>"This request or business reference belongs to different recorded terms. Recover the original request or review a new operation.",
        "gas_custody_reconciliation" or "gas_custody_reconciliation_required"=>"A pinned custody observation changed. Retain the accepted title, reservation and cash facts and use a qualified reconciliation before new performance.",
        "gas_custody_context" or "gas_custody_source" or "gas_custody_stale" or "gas_custody_policy_changed"=>"The exact custody source or selected action policy changed or is unavailable here. Reload and review the current source.",
        "gas_custody_accept" or "gas_custody_acceptance_window"=>"Select one full observed custody acceptance inside its saved park window. A deadline alone does not accept or cancel custody.",
        "gas_custody_return"=>"Select an observed partial or final return within held custody and the saved return order, using explicit original lot allocations.",
        "gas_custody_allocation" or "gas_custody_reservation" or "gas_custody_funding"=>"Select distinct identified customer lots or active custody reservations in the exact saved basis. Existing stock commitments remain protected.",
        "gas_custody_cash" or "gas_custody_cash_capacity"=>"Retain the independent signed fee and supported ordinary cash terms. Custody return does not pay or discharge fee cash.",
        "gas_custody_policy"=>"Select a saved manual or reviewed policy for this exact custody action.",
        _=>"Supply the installed custody profile, exact observations, explicit allocations and supported times. The operation was refused."
    };
    static string Command(string action)=>action switch{Accept=>"accept",Return=>"return",_=>throw Input("Select an installed custody action.")};
    static string Label(string action)=>action switch{Accept=>"Accept customer-title custody",Return=>"Record custody return",_=>throw Source()};
    static ImmutableArray<string> Fields(string action)=>action switch{
        Accept=>["expectedVersion","agreementEvidenceId","receiptEvidenceId","effectiveAt","allocations"],
        Return=>["expectedVersion","businessKey","evidenceId","quantity","effectiveAt","allocations"],_=>[]};

    async Task<GasParkCustodyWriteAdmission> CaptureWrite(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        // Optional inventory bounds are captured before a source lookup can
        // reveal the perspective. Missing authority stays missing for this call.
        var captured=await LifecycleNative.Read(connection,"""
            SELECT jsonb_build_object('until',security.gas_park_custody_authority($1,true,false),'inventoryUntil',
              CASE WHEN security.trading_access($1,'ReadGasOwnedHoldings') AND security.trading_access($1,'RecordGasOwnedHoldings')
                THEN least(security.authority_until('ReadGasOwnedHoldings'),security.authority_until('RecordGasOwnedHoldings')) ELSE NULL END)::text
            """,ct,database.Manifest.CatalogId);
        var admission=new GasParkCustodyWriteAdmission(database.Manifest.CatalogId,request.Target.ScopeId,captured.Instant("until"),
            captured.GetProperty("inventoryUntil").ValueKind==JsonValueKind.Null?null:captured.Instant("inventoryUntil"));
        database.RegisterCommitAdmission(connection,admission);return admission;
    }

    internal static async Task<JsonElement> ReadSource(NpgsqlConnection connection,PreviewDatabase database,LifecycleReadContext context,Guid trade,CancellationToken ct)
    {
        var source=await LifecycleNative.Source(connection,database,context,ct);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_custody_action_source"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await PinnedContext.BindExact(connection,database.Manifest.CatalogId,source,ct);
            await TradingContext.Bind(connection,database.Manifest.CatalogId,source,context.AsOf,ct);
            return await GasParkCustodyTicketStore.Read(connection,database,context,trade,ct);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_custody_action_source;RELEASE SAVEPOINT gas_custody_action_source");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    static void RequireSource(LifecycleActionRequest request,JsonElement source)
    {
        if(source.Id("tradeId")!=request.Target.ScopeId||source.Id("instrumentId")!=request.Target.BindingId||
            source.Id("contextId")!=request.Context.ContextId||source.Instant("asOf")!=request.Context.AsOf||
            DateWorkspace.Date(source.Text("effectiveOn"))!=request.Context.BusinessDate)throw Source();
        var policies=source.GetProperty("policies").EnumerateArray().Where(p=>p.Id("id")==request.PolicyId&&
            p.GetProperty("revision").GetInt32()==request.PolicyRevision&&p.Text("action")==request.Action&&p.Text("mode") is "manual" or "review"&&
            p.Instant("from_at")<=request.Context.AsOf&&request.Context.AsOf<p.Instant("until_at")).ToArray();
        if(policies.Length!=1)throw new MarketDataException("lifecycle_preview_stale","Choose a current saved manual or reviewed policy for this exact custody action.");
    }

    static string ReviewHash(LifecycleActionRequest request,JsonElement source)
    {
        var stable=JsonNode.Parse(source.GetRawText())!.AsObject();stable.Remove("authorityUntil");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new{request.Target,request.Action,request.PolicyId,request.PolicyRevision,request.Context,source=stable,selection=request.Selection},GasOwnedOperations.Json))));
    }

    async Task<JsonElement> Record(NpgsqlConnection connection,LifecycleActionRequest request,bool preview,CancellationToken ct)
    {
        var outcome=await LifecycleNative.Read(connection,"SELECT security.record_gas_park_custody($1,$2,$3,$4,$5::jsonb,$6,$7,$8,$9,$10)::text",ct,
            database.Manifest.CatalogId,request.OperationId,request.Target.ScopeId,Command(request.Action),request.Selection.GetRawText(),request.Context.AsOf,
            request.Context.ContextId,request.PolicyId,request.PolicyRevision,preview);
        RequireOutcomeTarget(request.Target,outcome);
        if(outcome.Id("operationId")!=request.OperationId||outcome.Text("action")!=Command(request.Action))throw Source();
        return outcome;
    }

    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        request=Normalize(request);var admission=await CaptureWrite(connection,request,ct);
        var source=await ReadSource(connection,database,request.Context,request.Target.ScopeId,ct);RequireSource(request,source);
        var outcome=await Record(connection,request,true,ct);
        var offer=new LifecycleActionOffer(request.Target,request.Action,Label(request.Action),request.Target.BindingId,request.Context.AsOf,null,true,null,1,source.GetProperty("stateVersion").GetInt32())
            {PolicyId=request.PolicyId,PolicyRevision=request.PolicyRevision,Selection=request.Selection};
        var review=new LifecycleActionReview(offer,ReviewHash(request,source),JsonSerializer.SerializeToElement(new{
            selection=request.Selection,willChange=outcome.GetProperty("changed").GetBoolean(),effects=Effects(database.Manifest.CatalogId,source,request,outcome)},GasOwnedOperations.Json),
            database.Manifest.CatalogId,admission.ReviewUntil(source.GetProperty("terms").Text("perspective")=="customer"));
        await admission.RequireCommit(connection,ct);return review;
    }

    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        request=Normalize(request);var admission=await CaptureWrite(connection,request,ct);
        var prior=await LifecycleNative.Read(connection,"SELECT coalesce(security.gas_park_custody_command_receipt($1,$2,$3,$4,$5::jsonb,$6,$7,$8,$9)::text,'null')",ct,
            database.Manifest.CatalogId,request.OperationId,request.Target.ScopeId,Command(request.Action),request.Selection.GetRawText(),request.Context.AsOf,
            request.Context.ContextId,request.PolicyId,request.PolicyRevision);
        if(prior.ValueKind!=JsonValueKind.Null)
        {
            RequireOutcomeTarget(request.Target,prior);
            if(prior.Id("operationId")!=request.OperationId||prior.Text("action")!=Command(request.Action))throw Source();
            return new(prior,admission);
        }
        var source=await ReadSource(connection,database,request.Context,request.Target.ScopeId,ct);RequireSource(request,source);
        if(ReviewHash(request,source)!=request.PreviewHash)throw new MarketDataException("lifecycle_preview_stale","The custody source, selected policy or reviewed operation changed. Review the current source again.");
        var outcome=await Record(connection,request,false,ct);
        _=Effects(database.Manifest.CatalogId,source,request,outcome);
        return new(outcome,admission);
    }

    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,LifecycleDiscoveryAccess access,CancellationToken ct)
    {
        if(!access.Allows("ReadGasOperations"))
        {
            if(target is not null)throw new PreviewDeniedException();
            return [];
        }
        ImmutableArray<JsonElement> rows;
        if(target is not null)
        {
            if(target.Handler!=Handler)throw Source();
            rows=[await GasParkCustodyTicketStore.Read(connection,database,context,target.ScopeId,ct)];
        }
        else
        {
            var directory=await LifecycleNative.Read(connection,"SELECT security.gas_park_custody_discovery_directory($1,$2,$3,$4)::text",ct,
                database.Manifest.CatalogId,context.ContextId,context.AsOf,access.Allows("ReadGasOwnedHoldings"));
            rows=[..directory.EnumerateArray()];
        }
        var result=ImmutableArray.CreateBuilder<LifecycleActionOffer>();
        foreach(var row in rows)
        {
            if(row.Id("contextId")!=context.ContextId||row.Instant("asOf")!=context.AsOf||DateWorkspace.Date(row.Text("effectiveOn"))!=context.BusinessDate||
                target is not null&&(row.Id("tradeId")!=target.ScopeId||row.Id("instrumentId")!=target.BindingId))throw Source();
            var inventory=row.GetProperty("terms").Text("perspective")=="customer";
            if(inventory&&!access.Allows("ReadGasOwnedHoldings"))throw new PreviewDeniedException();
            database.RegisterCommitAdmission(connection,new GasParkCustodyReadAdmission(database.Manifest.CatalogId,context.ContextId,context.AsOf,row.Instant("authorityUntil"),inventory));
            result.Add(Offer(row,context));
        }
        return result.ToImmutable();
    }

    internal static LifecycleActionOffer Offer(JsonElement row,LifecycleReadContext context)
    {
        var accepted=row.GetProperty("acceptance").ValueKind!=JsonValueKind.Null;var held=row.GetProperty("balances").Number("inCustody");
        var complete=accepted&&held==default;
        var action=accepted?Return:Accept;
        var hasPolicy=row.GetProperty("policies").EnumerateArray().Any(p=>p.Text("action")==action&&p.Text("mode") is "manual" or "review"&&
            p.Instant("from_at")<=context.AsOf&&context.AsOf<p.Instant("until_at"));
        string? reason=complete?"gas_custody_complete":row.GetProperty("reconciliationRequired").GetBoolean()?"gas_custody_reconciliation":!hasPolicy?"lifecycle_configuration_required":null;
        var scope=new LifecycleTarget(Handler,row.Id("tradeId"),row.Id("instrumentId"));
        return new(scope,action,complete?"Inspect completed custody and fee cash":Label(action),scope.BindingId,context.AsOf,null,reason is null,reason,1,row.GetProperty("stateVersion").GetInt32()){
            RequiredInputs=complete?[]:Fields(action),AvailableQuantity=held.ToString(),
            Subject=new("T"+row.GetProperty("tradeReference").GetInt32(),"Gas Park custody",row.Id("bookId"),row.Id("bookId").ToString())};
    }
}
