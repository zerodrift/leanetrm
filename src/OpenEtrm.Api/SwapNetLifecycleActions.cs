using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SwapNetActionTerms(SwapNetPreviewRequest Preview,string EffectiveAt,string Reference);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SwapNetActionSelection(Guid SnapshotId,Guid ProfileId,int ProfileRevision,SwapNetActionTerms Net);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapNetLifecycleProfileSave(Guid OperationId,Guid ProfileId,Guid TradeId,[property:JsonRequired]int ExpectedRevision,
    int OriginRevision,string Action,Guid PolicyId,int PolicyRevision,Guid ReferenceCatalogId,Guid ReferenceRevisionId,string Currency,
    [property:JsonRequired]int DecimalPlaces,string RoundingPolicy,string DuePolicy,string RecognitionPolicy,string CorrectionPolicy,string NetPolicy);

// The net action owns orchestration only. PrepareNet and the existing native
// cash producer retain the same gross allocation, reversal and rounding rules.
internal sealed class SwapNetLifecycleActions(PreviewDatabase database,Func<Guid,InputFrame> frames):ILifecycleActionHandler
{
    public string Kind=>"financial_swap_net";
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","AmendTrade"];
    private Guid Catalog=>database.Manifest.CatalogId;
    private static int Int(JsonElement value,string key)=>int.Parse(value.Text(key),System.Globalization.CultureInfo.InvariantCulture);
    private static SwapNetActionSelection Read(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,"financial_swap_net");
        SwapNetActionSelection s;
        try{s=request.Selection.Deserialize<SwapNetActionSelection>(LifecycleNative.Json)??throw new JsonException();}
        catch(JsonException error){throw new MarketDataException("lifecycle_input","Select an explicit gross net settlement or its exact original reversal.",error);}
        if(request.Action!="swap.net"||s.SnapshotId==Guid.Empty||s.ProfileId==Guid.Empty||s.ProfileRevision<1||s.Net is not{Preview:{Context:{}} p}||
            p.Context.SnapshotId!=s.SnapshotId||p.Context.Convention!=SwapStore.Convention||p.Context.KnowledgeMode!="current"||p.Context.ContextId is not null||p.TradeId!=request.Target.ScopeId)
            throw new MarketDataException("lifecycle_context","Retain one exact current swap frame, target and saved netting profile.");
        _=TradingInstant.Parse(s.Net.EffectiveAt,"settlement_cash_input");
        if(string.IsNullOrWhiteSpace(s.Net.Reference))throw new MarketDataException("settlement_cash_input","Enter the observed settlement reference.");
        return s;
    }
    private InputFrame Frame(LifecycleActionRequest request,SwapNetActionSelection s,bool writing)
    {
        var frame=frames(s.SnapshotId);
        if((writing?frame.ContextId is not null:frame.ContextId is {} retained&&retained!=request.Context.ContextId)||frame.At!=request.Context.AsOf||
            frame.BusinessDate!=request.Context.BusinessDate||request.Context.SnapshotId!=s.SnapshotId)
            throw new MarketDataException("historical_read_only","Retain the exact selected working frame, instant and business date.");
        return frame with{ContextId=null};
    }
    private async Task<SwapStore.NetPrepared> Prepare(NpgsqlConnection connection,LifecycleActionRequest request,
        SwapNetActionSelection s,bool writing,CancellationToken ct)
    {
        var frame=Frame(request,s,writing);
        var source=await LifecycleNative.Source(connection,database,request.Context,ct);
        var view=TradingView.Reread(frame,source);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT lifecycle_net_preview"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            return (SwapStore.NetPrepared)await new TradeReads(database).ReadBound(connection,view,async()=>
            {
                var profile=await LifecycleNative.Read(connection,"SELECT lifecycle.read_swap_net_profile($1,$2,$3,$4,$5,$6,$7)::text",ct,
                    Catalog,request.Target.ScopeId,s.ProfileId,s.ProfileRevision,request.Context.ContextId,request.Context.AsOf,request.Context.BusinessDate);
                var p=s.Net.Preview;
                if(profile.Id("binding_id")!=request.Target.BindingId||profile.Id("policy_id")!=request.PolicyId||Int(profile,"policy_revision")!=request.PolicyRevision||
                    Int(profile,"origin_revision")!=p.ExpectedRevision||profile.Text("net_policy")!=p.Policy||
                    new ReferenceKey(new(profile.Id("reference_catalog_id"),profile.Id("reference_revision_id")),profile.Text("currency_code"))!=p.Currency)
                    throw new MarketDataException("lifecycle_profile_revision","Retain the exact configured swap owner, revision, currency and netting policy.");
                _=await LifecycleNative.Read(connection,"SELECT lifecycle.check_action_policy($1,$2,$3,$4,$5,$6)::text",ct,
                    Catalog,request.PolicyId,request.PolicyRevision,request.Action,request.Context.AsOf,request.Automatic);
                return await new SwapStore(database).PrepareNet(connection,view.CalculationFrame,p,ct);
            },ct);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT lifecycle_net_preview;RELEASE SAVEPOINT lifecycle_net_preview");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
    private static string Hash(SwapStore.NetPrepared prepared,SwapNetActionSelection s,LifecycleActionRequest request)=>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{nativeHash=prepared.Hash,s.Net.EffectiveAt,s.Net.Reference,
            request.Context,request.Target,request.PolicyId,request.PolicyRevision,s.ProfileId,s.ProfileRevision},LifecycleNative.Json)));
    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var s=Read(request);
        var until=await LifecycleNative.ActionAuthority(connection,database,request.Action,LifecycleNative.Body(request,s),ct);
        var value=await Prepare(connection,request,s,false,ct);
        var details=JsonSerializer.SerializeToElement(new{settlement=value.Review,s.Net.EffectiveAt,s.Net.Reference},LifecycleNative.Json);
        return new(new(request.Target,request.Action,s.Net.Preview.ReversesId is null?"Record observed net settlement":"Reverse observed net settlement",
            s.ProfileId,request.Context.AsOf,null,true,null,s.Net.Preview.ExpectedRevision,s.Net.Preview.ExpectedVersion),Hash(value,s,request),details,Catalog,until);
    }
    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var s=Read(request);var body=LifecycleNative.Body(request,s);
        var until=await LifecycleNative.ActionAuthority(connection,database,request.Action,body,ct);
        var admission=new LifecycleNativeAdmission(Catalog,request.OperationId,request.Action,body,"AmendTrade",until);
        await using(var status=ScheduleStore.Command(connection,"SELECT lifecycle.command_status($1,$2,$3,$4::jsonb,'AmendTrade',$5)::text",
            Catalog,request.OperationId,request.Action,body.GetRawText(),until))
            if(await status.ExecuteScalarAsync(ct) is string prior)return new(JsonSerializer.Deserialize<JsonElement>(prior),admission);
        var value=await Prepare(connection,request,s,true,ct);
        if(Hash(value,s,request)!=request.PreviewHash)throw new MarketDataException("lifecycle_stale_review","Review the exact source, gross allocations and observed settlement before recording.");
        var calculation=JsonSerializer.SerializeToElement(new{contextId=value.ContextId,amount=value.Cash.ToExactString(),
            allocations=value.Allocations.Select(a=>new{determination=a.DeterminationId,amount=a.Amount.ToExactString()})},LifecycleNative.Json);
        var outcome=await LifecycleNative.Read(connection,"SELECT lifecycle.record_swap_net_action($1,$2::jsonb,$3,$4::jsonb)::text",ct,
            Catalog,body.GetRawText(),until,calculation.GetRawText());
        return new(outcome,admission);
    }
    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,LifecycleDiscoveryAccess access,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"""
            SELECT row_to_json(v)::text FROM(SELECT DISTINCT ON(r.trade_id) r.trade_id,r.binding_id,r.revision,r.book_id,b.name book_name,tr.reference_number trade_reference,
             settlement_data.swap_state(r.catalog_id,r.trade_id) state
            FROM trading.swap_trade_revisions r JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id)
            JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
            WHERE r.catalog_id=$1 AND($2::uuid IS NULL OR r.trade_id=$2) ORDER BY r.trade_id,r.revision DESC)v
            """,[Catalog,target is null?DBNull.Value:target.ScopeId],ct);
        var offers=ImmutableArray.CreateBuilder<LifecycleActionOffer>();
        foreach(var row in rows.Where(r=>target is null||r.Id("binding_id")==target.BindingId))
        {
            var profiles=await PreviewDatabase.Rows(connection,"SELECT row_to_json(v)::text FROM(SELECT DISTINCT ON(p.id)p.*,a.from_at,a.until_at FROM lifecycle.swap_net_profiles p JOIN lifecycle.action_policies a ON(a.catalog_id,a.id,a.revision)=(p.catalog_id,p.policy_id,p.policy_revision) WHERE(p.catalog_id,p.trade_id)=($1,$2) ORDER BY p.id,p.revision DESC)v",[Catalog,row.Id("trade_id")],ct);
            foreach(var profile in profiles.IsEmpty?new JsonElement?[]{null}:profiles.Select(p=>(JsonElement?)p))
            {
                var reason=profile is null?"lifecycle_configuration_required":context.SnapshotId is null?"lifecycle_selection_required":null;
                if(reason is null&&(context.AsOf<profile!.Value.Instant("from_at")||context.AsOf>=profile.Value.Instant("until_at")))reason="lifecycle_action_window";
                var state=row.GetProperty("state");var revision=int.Parse(row.Text("revision"),System.Globalization.CultureInfo.InvariantCulture);
                JsonElement? selection=null;var choices=ImmutableDictionary<string,ImmutableArray<LifecycleInputChoice>>.Empty;
                if(profile is {} p&&context.SnapshotId is {} snapshot)
                {
                    var currency=new ReferenceKey(new(p.Id("reference_catalog_id"),p.Id("reference_revision_id")),p.Text("currency_code"));
                    selection=JsonSerializer.SerializeToElement(new SwapNetActionSelection(snapshot,p.Id("id"),Int(p,"revision"),
                        new(new(new(snapshot,SwapStore.Convention,"current"),row.Id("trade_id"),revision,Int(state,"version"),currency,"",[],p.Text("net_policy"),null),"","")),LifecycleNative.Json);
                    var eligibleOccurrences=state.GetProperty("occurrences").EnumerateArray().Where(o=>
                        o.Id("reference_catalog_id")==currency.Revision.CatalogId&&o.Id("reference_revision_id")==currency.Revision.RevisionId&&o.Text("currency_code")==currency.Code&&
                        o.Text("decimal_places")==p.Text("decimal_places")&&o.Text("rounding_policy")==p.Text("rounding_policy")&&o.Text("due_policy")==p.Text("due_policy")&&
                        o.Text("recognition_policy")==p.Text("recognition_policy")&&o.Text("correction_policy")==p.Text("correction_policy")).ToDictionary(o=>o.Id("id"));
                    var eligibleDeterminations=state.GetProperty("determinations").EnumerateArray().Where(d=>eligibleOccurrences.ContainsKey(d.Id("occurrence_id"))).ToArray();
                    choices=choices.Add("netDeterminationId",[..eligibleDeterminations.Select(d=>
                    {
                        var occurrence=eligibleOccurrences[d.Id("occurrence_id")];
                        var amount=ExactMoney.Parse(d.Text("contribution"),currency);
                        var applied=state.GetProperty("allocations").EnumerateArray().Where(a=>a.Id("determination_id")==d.Id("id"))
                            .Aggregate(amount.Subtract(amount),(sum,a)=>sum.Add(ExactMoney.Parse(a.Text("amount"),currency)));
                        return new LifecycleInputChoice(d.Id("id").ToString(),$"Side {occurrence.Text("membership_ordinal")} · {occurrence.Text("delivery_from")} to {occurrence.Text("delivery_to")} · {amount.Subtract(applied).ToExactString()} {currency.Code.ToUpperInvariant()} outstanding");
                    })]);
                    var eligibleIds=eligibleDeterminations.Select(d=>d.Id("id")).ToHashSet();
                    choices=choices.Add("reversesId",[..state.GetProperty("payments").EnumerateArray().Where(payment=>payment.OptionalId("reverses_id") is null&&
                        payment.Id("reference_catalog_id")==currency.Revision.CatalogId&&payment.Id("reference_revision_id")==currency.Revision.RevisionId&&payment.Text("currency_code")==currency.Code&&
                        state.GetProperty("allocations").EnumerateArray().Where(a=>a.Id("payment_id")==payment.Id("id")).All(a=>a.Text("allocation_policy")==p.Text("net_policy")&&eligibleIds.Contains(a.Id("determination_id")))&&
                        !state.GetProperty("payments").EnumerateArray().Any(other=>other.OptionalId("reverses_id")==payment.Id("id"))&&
                        state.GetProperty("allocations").EnumerateArray().Count(a=>a.Id("payment_id")==payment.Id("id"))>=2)
                        .Select(payment=>new LifecycleInputChoice(payment.Id("id").ToString(),$"{payment.Text("reference")} · {payment.Text("amount")} {currency.Code.ToUpperInvariant()} · {payment.Text("cash_at")}"))]);
                }
                offers.Add(new LifecycleActionOffer(new(Kind,row.Id("trade_id"),row.Id("binding_id")),"swap.net","Record observed net settlement",profile?.Id("id")??row.Id("binding_id"),context.AsOf,null,reason is null,reason,revision,Int(state,"version"))
                {Subject=new(TradingReference.Trade(row),"Financial swap",row.Id("book_id"),row.Text("book_name")),PolicyId=profile?.Id("policy_id"),PolicyRevision=profile is {} configured?Int(configured,"policy_revision"):null,
                    Selection=selection,RequiredInputs=["netSettlement","effectiveAt","reference"],InputChoices=choices});
            }
        }
        return offers.ToImmutable();
    }
}
