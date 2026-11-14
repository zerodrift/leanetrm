using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SwapDeterminationActionSelection(SwapSettlementPreviewRequest Preview,[property:JsonRequired]int ExpectedVersion,string DuePolicy,
    string RecognitionPolicy,string CorrectionPolicy,[property:JsonRequired]string? CorrectionDueAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SwapPaymentActionSelection([property:JsonRequired]int ExpectedRevision,[property:JsonRequired]int ExpectedVersion,Guid DeterminationId,string Amount,ReferenceKey Currency,
    string EffectiveAt,string Reference,[property:JsonRequired]Guid? ReversesId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SwapActionSelection(Guid SnapshotId,Guid ProfileId,int ProfileRevision,SwapDeterminationActionSelection? Determination,SwapPaymentActionSelection? Payment);
internal sealed record LifecycleCombinedAdmission(ICommitAdmission First,ICommitAdmission Last):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {await First.RequireCommit(connection,ct);await Last.RequireCommit(connection,ct);}
}
internal sealed record SwapLifecycleInvocation(JsonElement Body,DateTimeOffset AuthorityUntil);

internal sealed class SwapLifecycleActions(PreviewDatabase database,Func<Guid,InputFrame> frames):ILifecycleActionHandler
{
    public string Kind=>"financial_swap";
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","AmendTrade"];
    private Guid Catalog=>database.Manifest.CatalogId;
    private SwapActionSelection Read(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,Kind);
        SwapActionSelection selection;
        try{selection=request.Selection.Deserialize<SwapActionSelection>(LifecycleNative.Json)??throw new JsonException();}
        catch(JsonException error){throw new MarketDataException("lifecycle_input","Select an exact existing swap determination or observed payment command.",error);}
        if(selection.SnapshotId==Guid.Empty||selection.ProfileId==Guid.Empty||selection.ProfileRevision<1||request.Action is not("swap.determine" or "swap.payment")||
            (request.Action=="swap.determine")!=(selection.Determination is not null)||(request.Action=="swap.payment")!=(selection.Payment is not null))
            throw new MarketDataException("lifecycle_input","Choose one supported swap action with its closed native selection.");
        if(selection.Determination is {} selected&&(selected.Preview is null||selected.Preview.Context is null))
            throw new MarketDataException("lifecycle_context","Supply the exact native swap context.");
        if(selection.Determination is {} determination&&(determination.Preview.Context.SnapshotId!=selection.SnapshotId||determination.Preview.Context.Convention!=SwapStore.Convention||
            determination.Preview.Context.KnowledgeMode!="current"||determination.Preview.Context.ContextId is not null||determination.Preview.TradeId!=request.Target.ScopeId))
            throw new MarketDataException("lifecycle_context","Retain the host's exact current swap frame and target.");
        return selection;
    }
    private void RequireFrame(LifecycleActionRequest request,SwapActionSelection selection,bool writing)
    {
        var frame=frames(selection.SnapshotId);
        if((writing?frame.ContextId is not null:frame.ContextId is {} retained&&retained!=request.Context.ContextId)||frame.At!=request.Context.AsOf||frame.BusinessDate!=request.Context.BusinessDate||request.Context.SnapshotId!=selection.SnapshotId)
            throw new MarketDataException("historical_read_only","Swap actions require the exact explicitly selected host working frame.");
    }
    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var selection=Read(request);RequireFrame(request,selection,false);
        var until=await LifecycleNative.ActionAuthority(connection,database,request.Action,LifecycleNative.Body(request,selection),ct);
        var source=await LifecycleNative.Source(connection,database,request.Context,ct);
        var profile=await CheckProfile(connection,request,selection,ct);
        await CheckPolicy(connection,request,ct);
        if(request.Action=="swap.payment")
        {
            var payment=selection.Payment!;
            var paymentFrame=frames(selection.SnapshotId) with{ContextId=null};
            var paymentView=TradingView.Reread(paymentFrame,source);
            await using(var save=ScheduleStore.Command(connection,"SAVEPOINT lifecycle_payment_preview"))await save.ExecuteNonQueryAsync(ct);
            JsonElement state;
            try{state=(JsonElement)await new TradeReads(database).ReadBound(connection,paymentView,async()=>(object)await new SwapStore(database).SettlementState(connection,request.Target.ScopeId,ct),ct);}
            finally
            {
                await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT lifecycle_payment_preview; RELEASE SAVEPOINT lifecycle_payment_preview");
                await restore.ExecuteNonQueryAsync(CancellationToken.None);
            }
            var determination=state.GetProperty("determinations").EnumerateArray().SingleOrDefault(d=>d.Id("id")==payment.DeterminationId);
            var occurrence=determination.ValueKind==JsonValueKind.Undefined?default:state.GetProperty("occurrences").EnumerateArray().Single(o=>o.Id("id")==determination.Id("occurrence_id"));
            if(state.GetProperty("version").GetInt32()!=payment.ExpectedVersion||determination.ValueKind==JsonValueKind.Undefined||
                occurrence.GetProperty("membership_ordinal").GetInt32()!=profile.GetProperty("membership_ordinal").GetInt32()||
                occurrence.Text("delivery_from")!=profile.Text("delivery_from")||occurrence.Text("delivery_to")!=profile.Text("delivery_to"))
                throw new MarketDataException("lifecycle_profile_revision","Select an observed payment of this profile's exact retained occurrence and source version.");
            var detail=JsonSerializer.SerializeToElement(payment,LifecycleNative.Json);
            var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payment,LifecycleNative.Json)));
            return new(new(request.Target,request.Action,Label(request.Action,profile),profile.Id("occurrence_id"),request.Context.AsOf,null,true,null,payment.ExpectedRevision,payment.ExpectedVersion),hash,detail,Catalog,until);
        }
        var frame=frames(selection.SnapshotId) with{ContextId=null};var view=TradingView.Reread(frame,source);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT lifecycle_swap_preview"))await save.ExecuteNonQueryAsync(ct);
        JsonElement value;
        try
        {
            var native=await new TradeReads(database).Read(connection,view,data=>new SwapStore(database).SettlementPreview(connection,data,view.CalculationFrame,selection.Determination!.Preview,ct),ct);
            value=JsonSerializer.SerializeToElement(native,LifecycleNative.Json);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT lifecycle_swap_preview; RELEASE SAVEPOINT lifecycle_swap_preview");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        return new(new(request.Target,request.Action,Label(request.Action,profile),profile.Id("occurrence_id"),request.Context.AsOf,null,true,null,
            selection.Determination!.Preview.ExpectedRevision,value.GetProperty("expectedVersion").GetInt32()),value.Text("previewHash"),value,Catalog,until);
    }
    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var selection=Read(request);var body=LifecycleNative.Body(request,selection);var until=await LifecycleNative.ActionAuthority(connection,database,request.Action,body,ct);
        await using(var status=ScheduleStore.Command(connection,"SELECT lifecycle.command_status($1,$2,$3,$4::jsonb,'AmendTrade',$5)::text",Catalog,request.OperationId,request.Action,body.GetRawText(),until))
            if(await status.ExecuteScalarAsync(ct) is string prior)return new(JsonSerializer.Deserialize<JsonElement>(prior),Admission(request,body,until));
        RequireFrame(request,selection,true);
        var source=await LifecycleNative.Source(connection,database,request.Context,ct);
        await CheckPolicy(connection,request,ct);
        await CheckProfile(connection,request,selection,ct);
        var store=new SwapStore(database);var frame=frames(selection.SnapshotId);JsonElement result;
        if(selection.Determination is {} determination)
        {
            var native=new SwapSettlementDetermineRequest(determination.Preview,request.OperationId,determination.ExpectedVersion,request.PreviewHash,
                determination.DuePolicy,determination.RecognitionPolicy,determination.CorrectionPolicy,determination.CorrectionDueAt);
            result=JsonSerializer.SerializeToElement(await store.DetermineSettlement(connection,frame,native,source,new(body,until),ct),LifecycleNative.Json);
        }
        else
        {
            var payment=selection.Payment!;
            var reviewed=await Preview(connection,request,ct);
            if(reviewed.Hash!=request.PreviewHash)throw new MarketDataException("lifecycle_stale_review","Review the exact observed payment before recording it.");
            var native=new SwapCashRequest(new(selection.SnapshotId,SwapStore.Convention,"current"),request.Target.ScopeId,request.OperationId,
                payment.ExpectedRevision,payment.ExpectedVersion,payment.DeterminationId,payment.Amount,payment.Currency,payment.EffectiveAt,payment.Reference,payment.ReversesId);
            result=JsonSerializer.SerializeToElement(await store.RecordSwapCash(connection,frame,native,source,new(body,until),ct),LifecycleNative.Json);
        }
        return new(result,Admission(request,body,until));
    }
    private LifecycleNativeAdmission Admission(LifecycleActionRequest request,JsonElement body,DateTimeOffset until)=>new(Catalog,request.OperationId,request.Action,body,"AmendTrade",until);
    private Task<JsonElement> CheckPolicy(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)=>LifecycleNative.Read(connection,
        "SELECT lifecycle.check_action_policy($1,$2,$3,$4,$5,$6)::text",ct,Catalog,request.PolicyId,request.PolicyRevision,request.Action,request.Context.AsOf,request.Automatic);
    private async Task<JsonElement> CheckProfile(NpgsqlConnection connection,LifecycleActionRequest request,SwapActionSelection selection,CancellationToken ct)
    {
        var p=await LifecycleNative.Read(connection,"SELECT lifecycle.read_swap_profile($1,$2,$3,$4,$5,$6,$7,$8)::text",ct,Catalog,request.Target.ScopeId,request.Action,selection.ProfileId,
            selection.ProfileRevision,request.Context.ContextId,request.Context.AsOf,request.Context.BusinessDate);
        var currency=new ReferenceKey(new(p.Id("reference_catalog_id"),p.Id("reference_revision_id")),p.Text("currency_code"));
        var revision=selection.Determination?.Preview.ExpectedRevision??selection.Payment!.ExpectedRevision;
        if(p.Id("policy_id")!=request.PolicyId||p.GetProperty("policy_revision").GetInt32()!=request.PolicyRevision||p.Id("binding_id")!=request.Target.BindingId||
            p.GetProperty("origin_revision").GetInt32()!=revision||(selection.Determination?.Preview.Currency??selection.Payment!.Currency)!=currency)
            throw new MarketDataException("lifecycle_profile_revision","Use the exact configured native swap owner, revision, policy and currency.");
        if(selection.Determination is {} d&&(d.Preview.MembershipOrdinal!=p.GetProperty("membership_ordinal").GetInt32()||
            d.Preview.PaymentOrdinal!=p.GetProperty("payment_ordinal").GetInt32()||d.Preview.DecimalPlaces!=p.GetProperty("decimal_places").GetInt32()||
            d.Preview.RoundingPolicy!=p.Text("rounding_policy")||d.DuePolicy!=p.Text("due_policy")||d.RecognitionPolicy!=p.Text("recognition_policy")||d.CorrectionPolicy!=p.Text("correction_policy")))
            throw new MarketDataException("lifecycle_profile_revision","Retain the configured exact payment occurrence and financial conventions.");
        return p;
    }
    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,LifecycleDiscoveryAccess access,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"""
          SELECT row_to_json(v)::text FROM(SELECT DISTINCT ON(r.trade_id) r.trade_id,r.binding_id,r.revision,r.effective_at,r.book_id,
          b.name book_name,tr.reference_number trade_reference,settlement_data.swap_state(r.catalog_id,r.trade_id) AS state
          FROM trading.swap_trade_revisions r JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id)
          JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
          WHERE r.catalog_id=$1 AND($2::uuid IS NULL OR r.trade_id=$2) ORDER BY r.trade_id,r.revision DESC)v
          """,[Catalog,target is null?DBNull.Value:target.ScopeId],ct);
        var offers=ImmutableArray.CreateBuilder<LifecycleActionOffer>();
        foreach(var r in rows.Where(r=>target is null||target.BindingId==r.Id("binding_id")))
        foreach(var action in new[]{"swap.determine","swap.payment"})
        {
            var profiles=await PreviewDatabase.Rows(connection,"SELECT row_to_json(v)::text FROM(SELECT DISTINCT ON(p.id) p.*,s.from_at,s.until_at,lifecycle.swap_profile_occurrence(p.catalog_id,p.id,p.revision) occurrence_id FROM lifecycle.swap_profiles p JOIN lifecycle.action_policies s ON(s.catalog_id,s.id,s.revision)=(p.catalog_id,p.policy_id,p.policy_revision) WHERE(p.catalog_id,p.trade_id,p.action)=($1,$2,$3) ORDER BY p.id,p.revision DESC)v",[Catalog,r.Id("trade_id"),action],ct);
            foreach(var profile in profiles.IsEmpty?new JsonElement?[]{null}:profiles.Select(p=>(JsonElement?)p))
            {
            var reason=profile is null?"lifecycle_configuration_required":context.SnapshotId is null?"lifecycle_selection_required":null;
            if(reason is null&&(context.AsOf<profile!.Value.Instant("from_at")||context.AsOf>=profile.Value.Instant("until_at")))reason="lifecycle_action_window";
            var revision=int.Parse(r.Text("revision"),System.Globalization.CultureInfo.InvariantCulture);
            var version=int.Parse(r.GetProperty("state").Text("version"),System.Globalization.CultureInfo.InvariantCulture);
            JsonElement? selected=null;
            var choices=ImmutableDictionary<string,ImmutableArray<LifecycleInputChoice>>.Empty;
            if(profile is {} p&&context.SnapshotId is {} snapshot)
            {
                var currency=new ReferenceKey(new(p.Id("reference_catalog_id"),p.Id("reference_revision_id")),p.Text("currency_code"));
                var profileRevision=int.Parse(p.Text("revision"),System.Globalization.CultureInfo.InvariantCulture);
                var preview=new SwapSettlementPreviewRequest(new(snapshot,SwapStore.Convention,"current"),r.Id("trade_id"),revision,
                    int.Parse(p.Text("membership_ordinal"),System.Globalization.CultureInfo.InvariantCulture),int.Parse(p.Text("payment_ordinal"),System.Globalization.CultureInfo.InvariantCulture),currency,
                    int.Parse(p.Text("decimal_places"),System.Globalization.CultureInfo.InvariantCulture),p.Text("rounding_policy"));
                selected=JsonSerializer.SerializeToElement(action=="swap.determine"?
                    new SwapActionSelection(snapshot,p.Id("id"),profileRevision,new(preview,version,p.Text("due_policy"),p.Text("recognition_policy"),p.Text("correction_policy"),null),null):
                    new SwapActionSelection(snapshot,p.Id("id"),profileRevision,null,new(revision,version,Guid.Empty,"",currency,"","",null)),LifecycleNative.Json);
                if(action=="swap.payment")
                {
                    var state=r.GetProperty("state");
                    var occurrenceIds=state.GetProperty("occurrences").EnumerateArray().Where(o=>o.Text("membership_ordinal")==p.Text("membership_ordinal")&&
                        o.Text("delivery_from")==p.Text("delivery_from")&&o.Text("delivery_to")==p.Text("delivery_to")).Select(o=>o.Id("id")).ToHashSet();
                    var contributions=state.GetProperty("determinations").EnumerateArray().Where(d=>occurrenceIds.Contains(d.Id("occurrence_id"))).Select(d=>
                    {
                        var amount=ExactMoney.Parse(d.Text("contribution"),currency);
                        var paid=state.GetProperty("allocations").EnumerateArray().Where(a=>a.Id("determination_id")==d.Id("id"))
                            .Aggregate(amount.Subtract(amount),(total,a)=>total.Add(ExactMoney.Parse(a.Text("amount"),currency)));
                        return new LifecycleInputChoice(d.Id("id").ToString(),$"{p.Text("delivery_from")} to {p.Text("delivery_to")} · contribution {d.Text("ordinal")} · {amount.Subtract(paid).ToExactString()} {currency.Code.ToUpperInvariant()} outstanding");
                    }).ToImmutableArray();
                    choices=choices.Add("determinationId",contributions);
                    var reversalChoices=state.GetProperty("payments").EnumerateArray().Where(payment=>payment.OptionalId("reverses_id") is null&&
                        !state.GetProperty("payments").EnumerateArray().Any(other=>other.OptionalId("reverses_id")==payment.Id("id")))
                        .Select(payment=>(Payment:payment,Allocations:state.GetProperty("allocations").EnumerateArray().Where(a=>a.Id("payment_id")==payment.Id("id")).ToArray()))
                        .Where(value=>value.Allocations.Length==1&&state.GetProperty("determinations").EnumerateArray().Any(d=>
                            d.Id("id")==value.Allocations[0].Id("determination_id")&&occurrenceIds.Contains(d.Id("occurrence_id"))))
                        .Select(value=>new LifecycleInputChoice(JsonSerializer.Serialize(new{determinationId=value.Allocations[0].Id("determination_id"),
                            amount=ExactMoney.Parse(value.Allocations[0].Text("amount"),currency).Scale(ExactDecimal.Parse("-1")).ToExactString(),reversesId=value.Payment.Id("id")},LifecycleNative.Json),
                            $"{value.Payment.Text("reference")} · {value.Payment.Text("amount")} {currency.Code.ToUpperInvariant()} · {value.Payment.Text("cash_at")}"))
                        .ToImmutableArray();
                    choices=choices.Add("paymentReversal",reversalChoices);
                }
            }
            offers.Add(new LifecycleActionOffer(new(Kind,r.Id("trade_id"),r.Id("binding_id")),action,Label(action,profile),profile?.Id("occurrence_id")??r.Id("binding_id"),
                context.AsOf,null,reason is null,reason,revision,version)
            {
                ScheduleSettings=action=="swap.determine"&&profile is {} schedule?new(revision,null,schedule.Id("id"),int.Parse(schedule.Text("revision"),System.Globalization.CultureInfo.InvariantCulture),null,null):null,
                Subject=new(TradingReference.Trade(r),"Financial swap",r.Id("book_id"),r.Text("book_name")),
                PolicyId=profile?.Id("policy_id"),PolicyRevision=profile is {} policy?int.Parse(policy.Text("policy_revision"),System.Globalization.CultureInfo.InvariantCulture):null,
                Selection=selected,RequiredInputs=action=="swap.determine"?["correctionDueAt"]:["determinationId","amount","effectiveAt","reference"],InputChoices=choices
            });
            }
        }
        return offers.ToImmutable();
    }
    private static string Label(string action,JsonElement? profile)
    {
        var name=action=="swap.determine"?"Determine final payment":"Record observed payment";
        return profile is {} p?$"{name} · Side {int.Parse(p.Text("membership_ordinal"),System.Globalization.CultureInfo.InvariantCulture)+1} · {p.Text("delivery_from")} to {p.Text("delivery_to")}":name;
    }
}
