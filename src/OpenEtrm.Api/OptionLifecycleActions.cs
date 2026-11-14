using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OptionActionSelection(Guid EvidenceId,string? Lots,[property:JsonRequired]int ExpectedEventVersion,int ProfileRevision);
internal sealed class OptionLifecycleActions(PreviewDatabase database,Action<LifecycleReadContext> requireFrame):ILifecycleActionHandler
{
    public string Kind=>"listed_option";
    public ImmutableArray<string> Permissions=>["ReadMarketData","ReadTrades","BookTrade"];
    private Guid Catalog=>database.Manifest.CatalogId;
    private JsonElement Body(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,Kind);
        OptionActionSelection selection;
        try{selection=request.Selection.Deserialize<OptionActionSelection>(LifecycleNative.Json)??throw new JsonException();}
        catch(JsonException error){throw new MarketDataException("lifecycle_input","Supply only explicit option evidence, lots and native versions.",error);}
        if(selection.EvidenceId==Guid.Empty||selection.ExpectedEventVersion<0||selection.ProfileRevision<1||
            request.Action is not("option.exercise" or "option.assignment" or "option.expiry" or "option.exercise_instruction" or "option.abandon_instruction"))
            throw new MarketDataException("lifecycle_input","Select an explicit qualified option action and stable event evidence.");
        if(request.Action is "option.exercise" or "option.assignment")
        {
            var lots=ExactDecimal.Parse(selection.Lots??throw new MarketDataException("option_lots","Select the explicit exercise or assignment quantity."));
            if(lots.CompareTo(default)<=0||!lots.IsMultipleOf(ExactDecimal.Parse("1")))throw new MarketDataException("option_lots","Select positive whole option lots.");
        }
        else if(selection.Lots is not null)throw new MarketDataException("lifecycle_input","Expiry and instructions use the retained remaining right; submit no replacement quantity.");
        if(request.Action=="option.expiry"&&selection.EvidenceId!=request.Target.ScopeId)
            throw new MarketDataException("lifecycle_evidence","Expiry uses the exact held right as its stable business-effect identity.");
        return LifecycleNative.Body(request,selection);
    }
    public async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var body=Body(request);var until=await LifecycleNative.ActionAuthority(connection,database,request.Action,body,ct);
        await LifecycleNative.Source(connection,database,request.Context,ct);
        var proposal=await LifecycleNative.Read(connection,"SELECT lifecycle.option_proposal($1,$2::jsonb)::text",ct,Catalog,body.GetRawText());
        var d=proposal.GetProperty("details");
        var offer=new LifecycleActionOffer(request.Target,request.Action,request.Action,d.Id("tradeId"),request.Context.AsOf,null,true,null,1,d.GetProperty("eventVersion").GetInt32());
        var review=new LifecycleActionReview(offer,proposal.Text("hash"),d.Clone(),Catalog,until);
        await review.RequireCommit(connection,ct);return review;
    }
    public async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        var body=Body(request);var until=await LifecycleNative.ActionAuthority(connection,database,request.Action,body,ct);
        await using(var status=ScheduleStore.Command(connection,"SELECT lifecycle.command_status($1,$2,$3,$4::jsonb,'BookTrade',$5)::text",Catalog,request.OperationId,request.Action,body.GetRawText(),until))
            if(await status.ExecuteScalarAsync(ct) is string prior)return new(JsonSerializer.Deserialize<JsonElement>(prior),new LifecycleNativeAdmission(Catalog,request.OperationId,request.Action,body,"BookTrade",until));
        requireFrame(request.Context);
        await LifecycleNative.Source(connection,database,request.Context,ct);
        var result=await LifecycleNative.Read(connection,"SELECT lifecycle.execute_option_action($1,$2::jsonb)::text",ct,Catalog,body.GetRawText());
        return new(result,new LifecycleNativeAdmission(Catalog,request.OperationId,request.Action,body,"BookTrade",until));
    }
    public async Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,LifecycleDiscoveryAccess access,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"""
            SELECT row_to_json(v)::text FROM(
             SELECT r.trade_id,r.option_instrument_id,r.lots,r.execution_at,s.expiry_at,s.instruction_cutoff_at,p.contrary_instructions_allowed,
              tr.reference_number trade_reference,r.book_id,b.name book_name,p.name product_name,
              clock_timestamp()>s.instruction_cutoff_at AS instructions_closed,
              abs(r.lots)-coalesce((SELECT sum(e.lots) FROM trading.futures_option_events e WHERE(e.catalog_id,e.trade_id)=(r.catalog_id,r.trade_id)
               AND e.kind IN('exercise','assignment','expiry')),0) AS open_lots,
              (SELECT count(*) FROM trading.futures_option_events e WHERE(e.catalog_id,e.trade_id)=(r.catalog_id,r.trade_id)) AS event_version
             FROM trading.futures_option_trade_revisions r JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(r.catalog_id,r.definition_revision_id,r.contract_id)
             JOIN ref_data.futures_option_series s ON(s.catalog_id,s.revision_id,s.code)=(c.reference_catalog_id,c.reference_revision_id,c.futures_option_series_code)
             JOIN ref_data.futures_option_products p ON(p.catalog_id,p.revision_id,p.code)=(s.catalog_id,s.revision_id,s.product_code)
             JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id)
             JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
             WHERE r.catalog_id=$1 AND($2::uuid IS NULL OR r.trade_id=$2))v
            """,[Catalog,target is null?DBNull.Value:target.ScopeId],ct);
        var result=ImmutableArray.CreateBuilder<LifecycleActionOffer>();
        foreach(var row in rows)
        foreach(var action in Actions(row))
        {
            var subject=new LifecycleTarget(Kind,row.Id("trade_id"),row.Id("option_instrument_id"));
            if(target is not null&&target!=subject)continue;
            var profiles=await PreviewDatabase.Rows(connection,"SELECT row_to_json(v)::text FROM(SELECT p.*,s.from_at,s.until_at,s.mode,m.delivery_profile_id IS NOT NULL AS requires_gas FROM lifecycle.option_profiles p JOIN settlement_data.futures_margin_policies m ON(m.catalog_id,m.id,m.revision)=(p.catalog_id,p.margin_policy_id,p.margin_policy_revision) JOIN lifecycle.action_policies s ON(s.catalog_id,s.id,s.revision)=(p.catalog_id,p.policy_id,p.policy_revision) WHERE(p.catalog_id,p.trade_id,p.action)=($1,$2,$3) ORDER BY p.revision DESC LIMIT 1)v",[Catalog,subject.ScopeId,action],ct);
            var reason=row.Number("open_lots").CompareTo(default)<=0?"option_holding_closed":profiles.IsEmpty?"lifecycle_configuration_required":
                profiles[0].GetProperty("requires_gas").GetBoolean()&&!access.Allows("ReadGasOwnedHoldings")?"lifecycle_gas_access_required":null;
            if(reason is null&&(context.AsOf<profiles[0].Instant("from_at")||context.AsOf>=profiles[0].Instant("until_at")))reason="lifecycle_action_window";
            var instruction=action is "option.exercise_instruction" or "option.abandon_instruction";
            var end=instruction?row.Instant("instruction_cutoff_at"):row.Instant("expiry_at");
            if(reason is null&&!WindowContains(action,context.AsOf,end))reason=instruction?"option_instruction_cutoff":"option_exercise_window";
            if(reason is null&&instruction&&row.GetProperty("instructions_closed").GetBoolean())reason="option_instruction_closed";
            var version=int.Parse(row.Text("event_version"),System.Globalization.CultureInfo.InvariantCulture);
            var configured=profiles.IsEmpty?(JsonElement?)null:profiles[0];
            result.Add(new LifecycleActionOffer(subject,action,action switch{"option.expiry"=>"Determine expiry","option.exercise"=>"Exercise","option.assignment"=>"Record assignment",
                "option.exercise_instruction"=>"Instruct exercise",_=>"Instruct abandonment"},subject.ScopeId,
                action=="option.expiry"?row.Instant("expiry_at"):row.Instant("execution_at"),action=="option.expiry"?null:end,
                reason is null,reason,1,version)
            {
                Subject=new(TradingReference.Trade(row),row.Text("product_name"),row.Id("book_id"),row.Text("book_name")),
                PolicyId=configured?.Id("policy_id"),PolicyRevision=configured is {} policy?int.Parse(policy.Text("policy_revision"),System.Globalization.CultureInfo.InvariantCulture):null,
                Selection=configured is {} profile?JsonSerializer.SerializeToElement(new OptionActionSelection(action=="option.expiry"?subject.ScopeId:Guid.Empty,null,version,
                    int.Parse(profile.Text("revision"),System.Globalization.CultureInfo.InvariantCulture)),LifecycleNative.Json):null,
                RequiredInputs=action=="option.expiry"?[]:instruction?["evidenceId"]:["evidenceId","lots"],AvailableQuantity=row.Text("open_lots")
            });
        }
        return result.ToImmutable();
    }
    private static IEnumerable<string> Actions(JsonElement row)
    {
        var positive=row.Number("lots").CompareTo(default)>0;
        yield return positive?"option.exercise":"option.assignment";yield return "option.expiry";
        if(positive&&row.GetProperty("contrary_instructions_allowed").GetBoolean())
        {yield return "option.exercise_instruction";yield return "option.abandon_instruction";}
    }
    internal static bool WindowContains(string action,DateTimeOffset at,DateTimeOffset end)=>action switch
    {
        "option.expiry"=>at>=end,
        "option.exercise_instruction" or "option.abandon_instruction"=>at<=end,
        _=>at<end
    };
}
