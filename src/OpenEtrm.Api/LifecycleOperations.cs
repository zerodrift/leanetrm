using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleDiscoveryRequest(LifecycleReadContext Context,LifecycleTarget? Target);
internal sealed record LifecycleDiscovery(LifecycleReadContext Context,ImmutableArray<LifecycleActionOffer> Actions,
    ImmutableArray<JsonElement> Policies,ImmutableArray<JsonElement> OptionProfiles,ImmutableArray<JsonElement> SwapProfiles,
    ImmutableArray<JsonElement> MarginPolicies,LifecycleSettingsChoices Choices,
    ImmutableArray<JsonElement> OptionSources,ImmutableArray<JsonElement> SwapOccurrences,
    ImmutableArray<JsonElement> FuturesSetup,
    [property:JsonIgnore]Guid Catalog,[property:JsonIgnore]DateTimeOffset AuthorityUntil):ICommitAdmission
{
    public ImmutableArray<JsonElement> SwapNetProfiles{get;init;}=[];
    public ImmutableArray<JsonElement> DeliveryProfiles{get;init;}=[];
    public ImmutableArray<JsonElement> DeliveryAccounts{get;init;}=[];
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT lifecycle.require_read($1,$2)",Catalog,AuthorityUntil);
        await command.ExecuteNonQueryAsync(ct);
    }

}

// UI, delegated callers and scheduler adapters use this same registry. It owns
// dispatch and request lifetime; product handlers own economics and native stores.
public sealed class LifecycleOperations
{
    private readonly PreviewDatabase database;
    private readonly LifecycleActionRegistry registry;
    private readonly Func<Guid,InputFrame> frames;
    internal LifecycleOperations(PreviewDatabase database,Func<Guid,InputFrame> frame,IEnumerable<ILifecycleActionHandler>? additional=null)
    {
        this.database=database;frames=frame;
        registry=new([new OptionLifecycleActions(database,RequireFrame),new SwapLifecycleActions(database,frame),new SwapNetLifecycleActions(database,frame),new FuturesLifecycleActions(database,RequireFrame),new GasOwnedActions(database),..additional??[]]);
    }
    private sealed record DiscoveryAdmission(DateTimeOffset Until,DateTimeOffset? GasUntil,LifecycleDiscoveryAccess Access);
    private async Task<DiscoveryAdmission> CaptureDiscovery(NpgsqlConnection connection,bool requiresGas,CancellationToken ct)
    {
        var catalog=database.Manifest.CatalogId;
        var captured=await LifecycleNative.Read(connection,"SELECT lifecycle.discovery_authority($1,$2)::text",ct,catalog,requiresGas);
        var until=captured.Instant("authorityUntil");
        database.RegisterCommitAdmission(connection,new LifecycleReadAdmission(catalog,until));
        var permissions=ImmutableHashSet.Create(StringComparer.Ordinal,"ReadMarketData","ReadTrades");
        if(captured.GetProperty("gasUntil").ValueKind!=JsonValueKind.Null)
        {
            database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(catalog,captured.Instant("gasUntil")));
            permissions=permissions.Add("ReadGasOwnedHoldings");
        }
        return new(until,captured.GetProperty("gasUntil").ValueKind==JsonValueKind.Null?null:captured.Instant("gasUntil"),new(permissions));
    }
    internal async Task<LifecycleDiscovery> View(NpgsqlConnection connection,FuturesReadRequest request,TradingReadSelection selection,CancellationToken ct)
    {
        if(request.Convention!=TradingBlotter.Convention)throw new MarketDataException("lifecycle_context","Select the shared trading workspace source convention.");
        var admission=await CaptureDiscovery(connection,false,ct);
        var view=await selection.Resolve(connection,new KnowledgeStore(database.Manifest.CatalogId),request,ct);
        return await DiscoverCore(connection,new(new(view.Context.ContextId!.Value,view.Frame.At,view.Frame.BusinessDate,view.Frame.Id),null),admission,ct);
    }
    internal async Task<LifecycleDiscovery> Discover(NpgsqlConnection connection,LifecycleDiscoveryRequest request,CancellationToken ct)
    {
        if(request.Context is null||request.Context.ContextId==Guid.Empty)throw new MarketDataException("lifecycle_context","Select an exact certified context.");
        var requiresGas=request.Target is not null&&registry.Require(request.Target.Handler).Permissions.Contains("ReadGasOwnedHoldings");
        return await DiscoverCore(connection,request,await CaptureDiscovery(connection,requiresGas,ct),ct);
    }
    private async Task<LifecycleDiscovery> DiscoverCore(NpgsqlConnection connection,LifecycleDiscoveryRequest request,DiscoveryAdmission admission,CancellationToken ct)
    {
        RequireFrame(request.Context);
        _=LifecycleNative.Instant(request.Context.AsOf);
        var until=admission.Until;
        await new LifecycleReadAdmission(database.Manifest.CatalogId,until).RequireCommit(connection,ct);
        if(admission.GasUntil is {} gasUntil)await new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,gasUntil).RequireCommit(connection,ct);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT lifecycle_discovery"))await save.ExecuteNonQueryAsync(ct);
        ImmutableArray<LifecycleActionOffer> actions;ImmutableArray<JsonElement> policies,optionProfiles,swapProfiles,swapNetProfiles,marginPolicies,optionSources,swapOccurrences,futuresSetup,deliveryProfiles,deliveryAccounts;LifecycleSettingsChoices choices;
        try
        {
            var context=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,request.Context.ContextId,ct)).Context;
            if(DateWorkspace.Date(context.EffectiveOn)!=request.Context.BusinessDate)throw new MarketDataException("lifecycle_context","Retain the selected context's business date.");
            await PinnedContext.BindExact(connection,database.Manifest.CatalogId,context,ct);
            await TradingContext.Bind(connection,database.Manifest.CatalogId,context,request.Context.AsOf,ct);
            var selected=request.Target is null?registry.Handlers:[registry.Require(request.Target.Handler)];
            var result=ImmutableArray.CreateBuilder<LifecycleActionOffer>();
            foreach(var handler in selected.Where(h=>!h.Permissions.Contains("ReadGasOwnedHoldings")||admission.Access.Allows("ReadGasOwnedHoldings")))
                result.AddRange(await handler.Discover(connection,request.Context,request.Target,admission.Access,ct));
            actions=result.ToImmutable();
            policies=await PreviewDatabase.Rows(connection,"SELECT row_to_json(p)::text FROM lifecycle.action_policies p WHERE p.catalog_id=$1 ORDER BY p.id,p.revision",[database.Manifest.CatalogId],ct);
            optionProfiles=await PreviewDatabase.Rows(connection,"SELECT row_to_json(p)::text FROM lifecycle.option_profiles p WHERE p.catalog_id=$1 ORDER BY p.trade_id,p.action,p.revision",[database.Manifest.CatalogId],ct);
            swapProfiles=await PreviewDatabase.Rows(connection,"SELECT(to_jsonb(p)||jsonb_build_object('occurrence_id',lifecycle.swap_profile_occurrence(p.catalog_id,p.id,p.revision)))::text FROM lifecycle.swap_profiles p WHERE p.catalog_id=$1 ORDER BY p.trade_id,p.action,p.revision",[database.Manifest.CatalogId],ct);
            swapNetProfiles=await PreviewDatabase.Rows(connection,"SELECT row_to_json(p)::text FROM lifecycle.swap_net_profiles p WHERE p.catalog_id=$1 ORDER BY p.trade_id,p.revision",[database.Manifest.CatalogId],ct);
            marginPolicies=await PreviewDatabase.Rows(connection,"""
                SELECT(to_jsonb(p)||jsonb_build_object('series_code',c.futures_series_code,'settlement_curve_name',v.name))::text
                FROM settlement_data.futures_margin_policies p JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(p.catalog_id,p.definition_revision_id,p.contract_id)
                JOIN market_data.curves v ON(v.catalog_id,v.revision_id,v.code)=(p.catalog_id,p.definition_revision_id,p.settlement_curve_code)
                WHERE p.catalog_id=$1 ORDER BY p.id,p.revision
                """,[database.Manifest.CatalogId],ct);
            optionSources=await PreviewDatabase.Rows(connection,"""
                SELECT row_to_json(v)::text FROM(SELECT r.trade_id,r.definition_revision_id,f.id underlying_contract_id,m.curve_code,c.name curve_name,c.quote_code,
                 c.rule,c.direct_role,c.direct_source_code,(c.rule='direct' AND c.direct_role='daily_settlement') AS expiry_eligible,
                 (c.rule='direct' AND EXISTS(SELECT 1 FROM ref_data.futures_execution_policies ep WHERE
                   (ep.catalog_id,ep.revision_id,ep.series_code,ep.implementation,ep.price_role,ep.price_source_code)=
                   (f.reference_catalog_id,f.reference_revision_id,f.futures_series_code,'contract_lot_execution_pnl_v2',c.direct_role,c.direct_source_code))) AS future_mark_eligible
                FROM trading.futures_option_trade_revisions r JOIN market_data.contracts o ON(o.catalog_id,o.revision_id,o.id)=(r.catalog_id,r.definition_revision_id,r.contract_id)
                JOIN ref_data.futures_option_series s ON(s.catalog_id,s.revision_id,s.code)=(o.reference_catalog_id,o.reference_revision_id,o.futures_option_series_code)
                JOIN market_data.contracts f ON(f.catalog_id,f.revision_id,f.futures_series_code)=(r.catalog_id,r.definition_revision_id,s.underlying_series_code) AND f.kind='future'
                JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(f.catalog_id,f.revision_id,f.id)
                JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.revision_id,m.curve_code)
                WHERE r.catalog_id=$1 ORDER BY r.trade_id,m.curve_code)v
                """,[database.Manifest.CatalogId],ct);
            swapOccurrences=await PreviewDatabase.Rows(connection,"""
                SELECT row_to_json(v)::text FROM(SELECT r.trade_id,r.revision origin_revision,r.binding_id,p.membership_ordinal,p.payment_ordinal,
                 p.delivery_from,p.delivery_to,p.payment_at,p.reference_catalog_id,p.reference_revision_id,q.currency_code,c.display_minor_units decimal_places,p.occurrence_id
                FROM(SELECT DISTINCT ON(trade_id)* FROM trading.swap_trade_revisions WHERE catalog_id=$1 ORDER BY trade_id,revision DESC)r
                JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id)
                CROSS JOIN LATERAL(
                 SELECT 0 membership_ordinal,0 payment_ordinal,s.delivery_from,s.delivery_to,s.payment_at,s.reference_catalog_id,s.reference_revision_id,s.quote_code,s.id occurrence_id
                  FROM trading.swap_fixed_sides s WHERE(s.catalog_id,s.id)=(i.catalog_id,i.fixed_side_id)
                 UNION ALL SELECT 0,0,s.delivery_from,s.delivery_to,s.payment_at,s.reference_catalog_id,s.reference_revision_id,s.quote_code,s.id FROM trading.swap_floating_sides s WHERE(s.catalog_id,s.id)=(i.catalog_id,i.first_floating_side_id)
                 UNION ALL SELECT 1,0,s.delivery_from,s.delivery_to,s.payment_at,s.reference_catalog_id,s.reference_revision_id,s.quote_code,s.id FROM trading.swap_floating_sides s WHERE(s.catalog_id,s.id)=(i.catalog_id,i.floating_side_id)
                 UNION ALL SELECT CASE s.side_id WHEN i.scheduled_first_id THEN 0 ELSE 1 END,s.ordinal,s.delivery_from,s.delivery_to,s.payment_at,s.reference_catalog_id,s.reference_revision_id,s.quote_code,s.id
                  FROM trading.swap_side_periods s WHERE s.catalog_id=i.catalog_id AND s.side_id IN(i.scheduled_first_id,i.scheduled_second_id))p
                JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(p.reference_catalog_id,p.reference_revision_id,p.quote_code)
                JOIN ref_data.currencies c ON(c.catalog_id,c.revision_id,c.code)=(q.catalog_id,q.revision_id,q.currency_code)
                ORDER BY r.trade_id,p.membership_ordinal,p.payment_ordinal)v
                """,[database.Manifest.CatalogId],ct);
            var kinds=await PreviewDatabase.Rows(connection,"SELECT row_to_json(k)::text FROM lifecycle.action_kinds k ORDER BY k.code",[],ct);
            choices=new([..kinds.Select(k=>new LifecycleActionKindChoice(k.Text("code"),k.GetProperty("automatic").GetBoolean(),k.Text("configuration_permission"),k.Text("execution_permission")))],
                ["manual","review","automatic"],OpenEtrm.MarketData.Trading.LifecycleActionPolicy.Version,["currency_minor_units_to_even_v1"],
                ["swap_due_nominal_v1"],["swap_completed_period_trading_pnl_v1"],["swap_explicit_correction_due_v1"],
                new(OpenEtrm.MarketData.Trading.FuturesMarginPolicy.DeliveryVersion,["include_opening_instant","after_opening_instant"],["to_even","away_from_zero"],
                    ["payment_total"],[OpenEtrm.MarketData.Trading.FuturesMargin.HeldBindingRounding],["daily","final"],["native_execution_inception_v1"])
                    {Delivery=new([OpenEtrm.MarketData.Gas.GasPhysicalDeliveryTerms.Version],["nominal_mark_v1","currency_curve_v1"],
                        ["delivery_cumulative_principal_total_v1"],["contractual_principal_due_v1"])}){SwapNet=[OpenEtrm.MarketData.Trading.SwapNetSettlement.Policy]};
            futuresSetup=await LifecycleFuturesSettings.Read(connection,database.Manifest.CatalogId,ct);
            (deliveryProfiles,deliveryAccounts)=admission.Access.Allows("ReadGasOwnedHoldings")?await LifecyclePhysicalSettings.Read(connection,database,request.Context,ct):([],[]);
            futuresSetup=[..futuresSetup.Select(future=>LifecyclePhysicalSettings.ForFuture(future,deliveryProfiles,deliveryAccounts))];
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT lifecycle_discovery; RELEASE SAVEPOINT lifecycle_discovery");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var value=new LifecycleDiscovery(request.Context,actions,policies,optionProfiles,swapProfiles,marginPolicies,choices,optionSources,swapOccurrences,futuresSetup,database.Manifest.CatalogId,until)
            {DeliveryProfiles=deliveryProfiles,DeliveryAccounts=deliveryAccounts,SwapNetProfiles=swapNetProfiles};
        database.RegisterCommitAdmission(connection,value);
        await value.RequireCommit(connection,ct);return value;
    }
    private ILifecycleActionHandler Handler(LifecycleActionRequest request)=>request.Target is not null&&!string.IsNullOrWhiteSpace(request.Target.Handler)?registry.Require(request.Target.Handler):
        throw new MarketDataException("lifecycle_input","Select an exact native lifecycle target.");
    private void RequireFrame(LifecycleReadContext context)
    {
        if(context.SnapshotId is not {} id)return;
        var frame=frames(id);
        if(frame.Id!=id||frame.At!=context.AsOf||frame.Time.Date!=context.BusinessDate||frame.ContextId is {} retained&&retained!=context.ContextId)
            throw new MarketDataException("lifecycle_context","Retain the exact selected host frame, instant and business date.");
    }
    internal async Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        if(request.Context is null)throw new MarketDataException("lifecycle_context","Select an exact certified context.");
        RequireFrame(request.Context);
        var value=await Handler(request).Preview(connection,request,ct);
        database.RegisterCommitAdmission(connection,value);return value;
    }
    internal async Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct)
    {
        if(request.Context is null)throw new MarketDataException("lifecycle_context","Select an exact certified context.");
        var value=await Handler(request).Execute(connection,request,ct);
        database.RegisterCommitAdmission(connection,value);return value;
    }

    public async Task<object> Discover(AgentIdentity agent,LifecycleDiscoveryRequest request,CancellationToken ct)
    {
        var result=await database.WithAgent(agent,TimeSpan.FromSeconds(RuntimeSet.MaxReadSeconds),["ReadTrades","ReadMarketData"],_=>{},
            (connection,t)=>Discover(connection,request,t),ct);
        return new{result.Context,result.Actions,result.Policies,result.OptionProfiles,result.SwapProfiles,result.SwapNetProfiles,result.MarginPolicies,result.Choices,result.OptionSources,result.SwapOccurrences,result.FuturesSetup,result.DeliveryProfiles,result.DeliveryAccounts};
    }
    public async Task<object> Preview(AgentIdentity agent,LifecycleActionRequest request,CancellationToken ct)
    {
        var result=await database.WithAgent(agent,TimeSpan.FromSeconds(RuntimeSet.MaxReadSeconds),Handler(request).Permissions,_=>{},
            (connection,t)=>Preview(connection,request,t),ct);
        return new{result.Offer,result.Hash,result.Details};
    }
    // Host calls this only after its ordinary exact-request approval admission.
    // Pending native wrappers are returned directly to the owned transaction;
    // no assistant receipt may serialize or recreate a pending admission object.
    internal async Task<object> ExecuteApproved(AgentIdentity agent,LifecycleActionRequest request,CancellationToken ct)
    {
        var result=await database.WithAgent(agent,TimeSpan.FromSeconds(RuntimeSet.MaxReadSeconds),Handler(request).Permissions,_=>{},
            (connection,t)=>Execute(connection,request,t),ct);
        return result.Outcome;
    }
}

internal static class LifecycleSettings
{
    internal static async Task<LifecycleActionWrite> Save(NpgsqlConnection connection,PreviewDatabase database,JsonElement body,string kind,CancellationToken ct)
    {
        var catalog=database.Manifest.CatalogId;
        var sql=kind switch{"policy"=>"SELECT lifecycle.save_policy($1,$2::jsonb)::text","option_profile"=>"SELECT lifecycle.save_option_profile($1,$2::jsonb)::text",
            "swap_profile"=>"SELECT lifecycle.save_swap_profile($1,$2::jsonb)::text","swap_net_profile"=>"SELECT lifecycle.save_swap_net_profile($1,$2::jsonb)::text",_=>throw new MarketDataException("lifecycle_input","Select a typed lifecycle settings operation.")};
        var until=await LifecycleNative.ActionAuthority(connection,database,kind,body,ct);
        await using var required=ScheduleStore.Command(connection,"SELECT lifecycle.action_permission($1,$2::jsonb)",kind,body.GetRawText());
        var permission=(string)(await required.ExecuteScalarAsync(ct))!;
        var value=await LifecycleNative.Read(connection,sql,ct,catalog,body.GetRawText());
        var pending=new LifecycleActionWrite(value,new LifecycleNativeAdmission(catalog,body.Id("operationId"),kind,body.Clone(),permission,until));
        database.RegisterCommitAdmission(connection,pending);return pending;
    }
}
