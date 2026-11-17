using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GasParkLoanAdvanceSelection(int ExpectedVersion,Guid AgreementEvidenceId,Guid EvidenceId,
    [property:JsonRequired]Guid? ClaimEvidenceId,[property:JsonRequired]Guid? LotId,[property:JsonRequired]Guid? ReservationId,DateTimeOffset EffectiveAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GasParkLoanReturnSelection(int ExpectedVersion,string BusinessKey,Guid EvidenceId,
    [property:JsonRequired]Guid? LotId,[property:JsonRequired]Guid? ReservationId,string Quantity,DateTimeOffset EffectiveAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GasParkLoanPaymentSelection(int ExpectedVersion,Guid FeeId,string BusinessKey,int ExpectedRevision,DateTimeOffset PaidAt,string Amount,string Evidence);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GasParkLoanFeeSelection(int ExpectedVersion,[property:JsonRequired]Guid? PreviousDeterminationId,
    DateTimeOffset Through,DateTimeOffset EffectiveAt,string BusinessKey,string Evidence);

internal sealed record GasParkLoanWriteAdmission(Guid Catalog,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(connection,"SELECT security.require_gas_owned_commit($1,$2,false,false)",Catalog,Until);
        await q.ExecuteNonQueryAsync(ct);
    }
}

internal sealed class GasParkLoanActions(PreviewDatabase database):ILifecycleActionHandler
{
    internal const string Handler="gas_park_loan";
    internal const string Advance="gas.park_loan.advance",Return="gas.park_loan.return",Payment="gas.park_loan.fee_payment";
    internal const string Determine="gas.park_loan.fee_determine";
    internal static string Refusal(string code)=>code switch
    {
        "gas_balance_fee_cash_range"=>"The maximum rounded cumulative fee exceeds the supported native cash amount. Select a smaller explicit rate, principal or accrual window.",
        "gas_balance_fee_window"=>"Determine an observed fee window after accepted advance and no later than the certified business/knowledge cut or contractual fee cutoff.",
        "gas_balance_fee_context" or "gas_balance_fee_source" or "gas_balance_fee_stale" or "gas_balance_fee_policy_changed"=>"The exact accepted-event, cumulative fee source or action policy changed or is unavailable at this context. Reload and review the retained source again.",
        "gas_balance_fee_terms"=>"Select the complete installed balance-time fee law, absolute rate, independent direction, bounded accrual cutoff and cumulative cash rounding.",
        "gas_balance_fee_conflict"=>"This fee request identity belongs to different accepted terms. Retry the exact original request or review a new operation.",
        "gas_park_loan_correction_required" or "gas_park_loan_counterpart"=>"This contract profile has no installed correction operation for linked principal or fees. Accepted effects remain unchanged; use a qualified reconciliation profile.",
        "gas_park_loan_fee"=>"This profile determines fee cash on accepted advance. Choose a due time no earlier than planned advance, or explicitly no date for a zero fee.",
        "gas_park_loan_stale" or "gas_park_loan_context" or "gas_park_loan_policy"=>"The agreement source or action policy changed. Reload the current state and review the operation again.",
        "gas_park_loan_conflict"=>"This request identity already belongs to different terms. Retry the exact original request or begin a new reviewed operation.",
        "gas_park_loan_account" or "gas_park_loan_scope"=>"Select the exact authorized book, legal parties, inventory and compatible return account for this agreement.",
        "gas_park_loan_advance"=>"Select one complete evidenced advance inside the saved advance window. An accepted advance cannot be repeated or replaced.",
        "gas_park_loan_return"=>"Select an evidenced partial or final return after accepted advance, within the outstanding principal and exact saved basis.",
        "gas_park_loan_payment"=>"Select the independent determined service fee and its exact signed payment allocation. Payment cannot precede that cash obligation.",
        "gas_park_loan_denied"=>"This agreement operation requires current authority over its exact source and book.",
        _=>"Supply the installed agreement profile, exact source evidence, quantities and explicit timing choices. The operation was refused."
    };
    public string Kind=>Handler;
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","ReadGasOwnedHoldings","RecordGasOwnedHoldings"];
    internal static (Guid Account,string Action) NativeAction(JsonElement source,string action)
    {
        var receiver=source.GetProperty("terms").GetProperty("principalDirection").GetInt32()==1;
        return action switch
        {
            Advance=>(source.Id("ownedAccountId"),receiver?"gas.loan.borrow":"gas.owned.dispatch"),
            Return=>(receiver?source.Id("ownedAccountId"):source.Id("claimAccountId"),receiver?"gas.loan.repay":"gas.operator.settle"),
            Payment=>(receiver?source.Id("ownedAccountId"):source.Id("claimAccountId"),"gas.cash.record_payment"),
            Determine=>(receiver?source.Id("ownedAccountId"):source.Id("claimAccountId"),Determine),
            _=>throw GasParkLoanTicket.Invalid("profile","Select an installed agreement action.")
        };
    }
    internal static JsonElement Policy(JsonElement source,string action)
    {
        var native=NativeAction(source,action);
        var rows=source.GetProperty("policies").EnumerateArray().Where(p=>p.Id("account_id")==native.Account&&p.Text("action")==native.Action).ToArray();
        return rows.Length==1?rows[0]:throw new MarketDataException("lifecycle_configuration_required","Bind an explicit native action policy before reviewing this agreement action.");
    }
    internal static async Task<JsonElement> ReadSource(NpgsqlConnection connection,PreviewDatabase database,LifecycleReadContext context,Guid trade,CancellationToken ct)
    {
        var source=await LifecycleNative.Source(connection,database,context,ct);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_park_loan_source"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await PinnedContext.BindExact(connection,database.Manifest.CatalogId,source,ct);
            await TradingContext.Bind(connection,database.Manifest.CatalogId,source,context.AsOf,ct);
            return await GasParkLoanTicketStore.Read(connection,database,context,trade,ct);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_park_loan_source;RELEASE SAVEPOINT gas_park_loan_source");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
    static string Command(string action)=>action switch{Advance=>"advance",Return=>"return",Payment=>"fee_payment",Determine=>"fee_determine",_=>throw GasParkLoanTicket.Invalid("profile","Select an installed agreement action.")};
    static string Label(string action)=>action switch{Advance=>"Accept principal advance",Return=>"Accept principal return",Payment=>"Record service fee payment",Determine=>"Determine accrued service fee",_=>throw new InvalidOperationException()};
    static ImmutableArray<string> Fields(string action)=>action switch{
        Advance=>["expectedVersion","agreementEvidenceId","evidenceId","claimEvidenceId","lotId","reservationId","effectiveAt"],
        Return=>["expectedVersion","businessKey","evidenceId","lotId","reservationId","quantity","effectiveAt"],
        Determine=>["expectedVersion","previousDeterminationId","through","effectiveAt","businessKey","evidence"],
        Payment=>["expectedVersion","feeId","businessKey","expectedRevision","paidAt","amount","evidence"],_=>[]};
    internal static LifecycleActionRequest Normalize(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,Handler);
        if(request.Automatic)throw new MarketDataException("lifecycle_policy","The installed agreement profile requires a reviewed manual action.");
        var type=request.Action switch{Advance=>typeof(GasParkLoanAdvanceSelection),Return=>typeof(GasParkLoanReturnSelection),Payment=>typeof(GasParkLoanPaymentSelection),Determine=>typeof(GasParkLoanFeeSelection),
            _=>throw GasParkLoanTicket.Invalid("profile","Select an installed agreement action.")};
        try
        {
            var selected=request.Selection.Deserialize(type,GasOwnedOperations.Json)??throw new JsonException();
            if(selected is GasParkLoanFeeSelection fee)
            {
                var key=fee.BusinessKey.Trim(' ');var evidence=fee.Evidence.Trim(' ');
                if(fee.ExpectedVersion<1||fee.PreviousDeterminationId==Guid.Empty||fee.Through>fee.EffectiveAt||
                    key.Length is <1 or >128||evidence.Length is <1 or >2000||key.Any(char.IsControl)||evidence.Contains('\0'))
                    throw GasParkLoanTicket.Invalid("profile","Retain the exact agreement head, previous determination and observed fee window, business reference and evidence.");
                selected=fee with{BusinessKey=key,Evidence=evidence};
            }
            return request with{Selection=JsonSerializer.SerializeToElement(selected,type,GasOwnedOperations.Json)};
        }
        catch(JsonException error)
        {throw new MarketDataException("gas_park_loan_input","Supply this operation's closed typed fields, exact UTC instants and explicit not-applicable selections.",error);}
    }
    async Task<GasParkLoanWriteAdmission> Admission(NpgsqlConnection c,CancellationToken ct)
    {
        await using var q=ScheduleStore.Command(c,"SELECT security.gas_park_loan_authority($1,true)",database.Manifest.CatalogId);
        var until=new DateTimeOffset((DateTime)(await q.ExecuteScalarAsync(ct))!);
        return new(database.Manifest.CatalogId,until);
    }
    async Task<JsonElement> Record(NpgsqlConnection c,LifecycleActionRequest request,bool preview,CancellationToken ct)=>
        await LifecycleNative.Read(c,"SELECT security.record_gas_park_loan($1,$2,$3,$4,$5::jsonb,$6,$7,$8)::text",ct,
            database.Manifest.CatalogId,request.OperationId,request.Target.ScopeId,Command(request.Action),request.Selection.GetRawText(),request.Context.AsOf,request.Context.ContextId,preview);
    static void RequireSource(LifecycleActionRequest request,JsonElement source)
    {
        if(source.Id("instrumentId")!=request.Target.BindingId)throw new MarketDataException("gas_park_loan_input","Retain this agreement's exact native instrument binding.");
        var policy=Policy(source,request.Action);
        if(policy.Id("policy_id")!=request.PolicyId||policy.GetProperty("policy_revision").GetInt32()!=request.PolicyRevision)
            throw new MarketDataException("lifecycle_preview_stale","Review the current native action policy.");
    }
    static string Hash(LifecycleActionRequest request,JsonElement source)
    {
        var stable=JsonSerializer.SerializeToNode(source)!.AsObject();
        stable.Remove("authorityUntil");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new{request.Target,request.Action,request.PolicyId,request.PolicyRevision,request.Context,source=stable,selection=request.Selection},GasOwnedOperations.Json))));
    }
    internal static object Effects(Guid catalog,JsonElement source,LifecycleActionRequest request,JsonElement outcome)
    {
        if(request.Action==Determine)
        {
            var principalTerms=GasParkLoanPortfolio.Terms(catalog,source);
            var state=GasParkLoanBalanceFeeAdapter.Read(principalTerms,source);
            if(request.Selection.OptionalId("previousDeterminationId")!=state.Checkpoint?.Id)
                throw new MarketDataException("lifecycle_preview_stale","Review the current cumulative fee determination.");
            var expected=GasParkLoanBalanceFee.Determine(state.Terms,state.Source,request.Selection.Instant("through"),state.Checkpoint);
            var native=ExactMoney.Parse(outcome.Text("exactNumerator")+"/"+outcome.Text("exactDenominator"),state.Terms.Currency);
            if(native!=expected.ExactTotal||outcome.Number("roundedTotal")!=expected.RoundedTotal.Amount||outcome.Number("contribution")!=expected.Contribution.Amount)
                throw new MarketDataException("gas_park_loan_context","The independent native and managed cumulative fee determinations disagree.");
            return new{accruedThrough=request.Selection.Text("through"),exactTotal=expected.ExactTotal.ToExactString(),
                previousCumulativeRoundedFee=state.CumulativeDeterminedTotal.Amount.ToString(),
                cumulativeRoundedFee=expected.RoundedTotal.Amount.ToString(),independentCash=expected.Contribution.Amount.ToString(),
                currency=state.Terms.Currency.Code,dueAt=state.Terms.DueAt,principalUnchanged=true};
        }
        var terms=source.GetProperty("terms");
        var principal=terms.GetProperty("principalDirection").GetInt32()==1?ExactDecimal.Parse("1"):ExactDecimal.Parse("-1");
        var quantity=request.Action==Advance?source.Number("quantity"):request.Action==Return?request.Selection.Number("quantity"):default;
        return new{
            performedQuantity=request.Action==Payment?(string?)null:quantity.ToString(),quantitySpecification=terms.Text("quantity"),
            stockQuantity=request.Action==Advance?(quantity*principal).ToString():request.Action==Return?(-quantity*principal).ToString():null,
            returnQuantity=request.Action==Advance?(-quantity*principal).ToString():request.Action==Return?(quantity*principal).ToString():null,
            outstandingPrincipal=request.Action==Advance?source.Text("quantity"):request.Action==Return?outcome.OptionalText("outstandingPrincipal"):null,
            feeAmount=request.Action==Advance&&source.Text("implementation")==GasParkAndLoanTerms.Version?terms.Text("fixedFeeAmount"):null,cashAmount=request.Action==Payment?request.Selection.Text("amount"):null,
            currency=terms.Text("currency"),effectiveAt=request.Selection.Text(request.Action==Payment?"paidAt":"effectiveAt"),
            returnAt=terms.Text("returnAt"),feeDueAt=terms.OptionalText("feeDueAt")
        };
    }
    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        request=Normalize(request);var admission=await Admission(connection,ct);
        var source=await ReadSource(connection,database,request.Context,request.Target.ScopeId,ct);RequireSource(request,source);
        var outcome=await Record(connection,request,true,ct);
        var offer=new LifecycleActionOffer(request.Target,request.Action,Label(request.Action),request.Target.BindingId,request.Context.AsOf,null,true,null,1,source.GetProperty("stateVersion").GetInt32())
            {PolicyId=request.PolicyId,PolicyRevision=request.PolicyRevision,Selection=request.Selection};
        var review=new LifecycleActionReview(offer,Hash(request,source),JsonSerializer.SerializeToElement(new{selection=request.Selection,effects=Effects(database.Manifest.CatalogId,source,request,outcome)},GasOwnedOperations.Json),database.Manifest.CatalogId,admission.Until);
        await admission.RequireCommit(connection,ct);return review;
    }
    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        request=Normalize(request);var admission=await Admission(connection,ct);
        var prior=await LifecycleNative.Read(connection,"SELECT coalesce(security.gas_park_loan_command_receipt($1,$2,$3,$4,$5::jsonb,$6,$7)::text,'null')",ct,
            database.Manifest.CatalogId,request.OperationId,request.Target.ScopeId,Command(request.Action),request.Selection.GetRawText(),request.Context.AsOf,request.Context.ContextId);
        if(prior.ValueKind!=JsonValueKind.Null)return new(prior,admission);
        var source=await ReadSource(connection,database,request.Context,request.Target.ScopeId,ct);RequireSource(request,source);
        if(Hash(request,source)!=request.PreviewHash)throw new MarketDataException("lifecycle_preview_stale","The agreement source or operation changed; review it again.");
        return new(await Record(connection,request,false,ct),admission);
    }
    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,LifecycleDiscoveryAccess access,CancellationToken ct)
    {
        if(!access.Allows("ReadGasOwnedHoldings"))return [];
        ImmutableArray<JsonElement> rows;
        if(target is not null)rows=[await GasParkLoanTicketStore.Read(connection,database,context,target.ScopeId,ct)];
        else
        {
            var directory=await LifecycleNative.Read(connection,"SELECT security.gas_park_loan_directory($1,$2,$3)::text",ct,database.Manifest.CatalogId,context.ContextId,context.AsOf);
            rows=[..directory.EnumerateArray()];
        }
        var result=ImmutableArray.CreateBuilder<LifecycleActionOffer>();
        foreach(var source in rows)
        {
            if(target is not null&&source.Id("instrumentId")!=target.BindingId)throw GasParkLoanTicket.Invalid("profile","Select this agreement's exact instrument binding.");
            var advanced=source.GetProperty("advance").ValueKind==JsonValueKind.Object;
            var balance=source.Text("implementation")==GasParkLoanBalanceFeeTerms.Version;
            foreach(var action in !advanced?new[]{Advance}:balance?new[]{Return,Determine,Payment}:new[]{Return,Payment})
            {
                if(action==Return&&source.GetProperty("balances").Number("outstandingPrincipal")==default)continue;
                if(action==Payment&&(balance?!source.GetProperty("fee").GetProperty("determinations").EnumerateArray().Any(row=>row.OptionalId("feeId") is not null&&row.Number("unpaid")!=default):
                    source.GetProperty("terms").Number("fixedFeeAmount")==default||source.GetProperty("fee").Number("unpaid")==default))continue;
                Guid? policyId=null;int? policyRevision=null;string? reason=null;
                try{var policy=Policy(source,action);policyId=policy.Id("policy_id");policyRevision=policy.GetProperty("policy_revision").GetInt32();}
                catch(MarketDataException e){reason=e.Code;}
                var scope=new LifecycleTarget(Handler,source.Id("tradeId"),source.Id("instrumentId"));
                result.Add(new(scope,action,Label(action),scope.BindingId,context.AsOf,null,reason is null,reason,1,source.GetProperty("stateVersion").GetInt32())
                    {PolicyId=policyId,PolicyRevision=policyRevision,RequiredInputs=Fields(action),AvailableQuantity=source.GetProperty("balances").Text("outstandingPrincipal"),
                    Subject=new("T"+source.GetProperty("tradeReference").GetInt32(),"Park and Loan",source.Id("bookId"),source.Id("bookId").ToString())});
            }
        }
        return result.ToImmutable();
    }
}
