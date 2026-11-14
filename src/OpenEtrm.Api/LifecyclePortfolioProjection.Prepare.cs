using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

internal static partial class LifecyclePortfolioProjection
{
    internal static async Task<PreparedLifecyclePortfolio> Prepare(NpgsqlConnection connection,PreviewDatabase database,
        PortfolioValuation.Prepared opening,PortfolioEconomicProjectionRequest request,ImmutableArray<ConnectedMarketEvidence>? retainedGraphs,CancellationToken ct)
    {
        var horizon=await ReadHorizon(connection,database,opening,request,ct);
        var scope=opening.OpeningScope??throw Error("projection_scope","Retain the original opening market and exact book/holding scope.");
        if(!scope.WholeBooks&&(!request.GasOwned.IsEmpty||opening.Policy.GasOwned is not null))
            throw Error("projection_gas_scope","Independent ownership projection requires the explicit complete book scope.");
        var gasPresent=scope.WholeBooks&&await GasOwnedKnownHistoryStore.RequireScope(connection,database,scope.Frame,scope.Books,opening.Policy.GasOwned is not null,ct);
        var knownGas=gasPresent?await GasOwnedKnownHistoryStore.Read(connection,database,scope.Frame,scope.Snapshot,scope.Books,ct):null;
        if(!gasPresent&&!request.GasOwned.IsEmpty)throw Error("projection_gas_policy","The selected source has no independent gas quantity owners for these account profiles.");
        if(opening.Policy.FuturesConvention!=FuturesMarginValuationPolicy.Convention)
            throw Error("projection_futures_state","Economic risk requires the explicit native margin remaining-value profile.");
        var sources=ImmutableArray.CreateBuilder<(PortfolioProductSource Source,ImmutableArray<ILifecycleProjection> Components,ImmutableDictionary<ReferenceKey,ISideDiscounting> Curves,ImmutableArray<ValuationPortfolio> Histories,ImmutableDictionary<CurveRequest,CurveKey> Officials,ImmutableArray<SchedulePriceTerm> ActivePrices)>();
        var productEvidence=ImmutableArray.CreateBuilder<JsonElement>();var finalEvidence=ImmutableArray.CreateBuilder<JsonElement>();var receipts=ImmutableArray.CreateBuilder<FuturesOptionCalibrationReceipt>();
        foreach(var group in opening.Sources.GroupBy(s=>s.Snapshot.Definitions).OrderBy(g=>g.Key.RevisionId))
        {
            var first=group.First();
            if(group.Any(s=>!s.Snapshot.HasSameInputs(first.Snapshot)))throw Error("projection_source","A market partition requires one exact source certificate and input selection.");
            var currencies=group.SelectMany(s=>s.Portfolio.Instruments).Where(i=>request.Policy.CashAccumulation==LifecycleProjectionPolicy.Accumulated||i switch{
                FinancialSwapInstrument swap=>swap.Discounting,MonthlyFinancialSwap swap=>swap.Discounting,ScheduledFinancialSwap swap=>swap.Discounting,
                CrossCommodityScheduledSwap swap=>swap.Discounting,CashPaymentInstrument cash=>cash.PaymentAt>first.Snapshot.ValuationAt,
                GasCommodityHoldingInstrument gas=>gas.Carrying==GasHoldingCarrying.CurrencyCurve&&gas.ReturnAt>first.Snapshot.ValuationAt,
                _=>true}).Select(i=>i.Currency).Distinct().ToArray();
            var curves=ImmutableDictionary.CreateBuilder<ReferenceKey,ISideDiscounting>();
            foreach(var currency in currencies)
            {
                var mapping=await SwapStore.CurrencyDefault(connection,database.Manifest.CatalogId,first.Data,first.Frame,currency,ct)??
                    throw Error("projection_cash_policy","The selected exact-instant economic profile requires an explicit captured currency curve.");
                curves.Add(currency,new LifecycleCurrencyDiscounting(mapping,first.Snapshot,request.RateTimingPolicy,ClockChange.FrozenInputs));
            }
            var components=ImmutableArray.CreateBuilder<ILifecycleProjection>();var histories=ImmutableArray.CreateBuilder<ValuationPortfolio>();var officials=ImmutableDictionary.CreateBuilder<CurveRequest,CurveKey>();var activePrices=ImmutableArray.CreateBuilder<SchedulePriceTerm>();
            foreach(var source in group)
            {
                var setup=source.Portfolio.Setup with{Discounting=null,InstrumentDiscounting=source.Portfolio.Instruments.Where(i=>curves.ContainsKey(i.Currency)).ToImmutableDictionary(i=>i.Id,i=>curves[i.Currency])};
                var calibrated=ImmutableDictionary.CreateBuilder<Guid,FuturesOptionMarketBinding>();
                if(!setup.OptionBindings.IsEmpty)
                {
                    var options=setup.OptionBindings.Values.Select(b=>b.Instrument).DistinctBy(i=>i.Id).OrderBy(i=>i.Id).ToArray();
                    var p=opening.Policy.FuturesOptions??throw Error("option_policy","Select the exact option calibration and fallback policy.");
                    var build=source.Data.Model.Build(source.Snapshot,[..options.SelectMany(o=>{
                        var r=FuturesOptionPricing.Requests(source.Data.Model,o.Series,p.ObservationRole);return new[]{r.Underlying,r.Settlement};}).Distinct()],ct);
                    foreach(var option in options)
                    {
                        var binding=FuturesOptionPricing.Prepare(option,build,source.Snapshot,curves[option.Currency],p.ObservationRole,p.Validate(),p.FallbackDeclared,ct);
                        calibrated.Add(option.Id,binding);receipts.Add(binding.Receipt);
                    }
                    setup=setup with{OptionBindings=calibrated.ToImmutable()};
                }
                histories.Add(new(source.Portfolio.Instruments,source.Portfolio.Positions,setup));
                foreach(var position in source.Portfolio.Positions)
                {
                    var instrument=source.Portfolio.Instruments.Single(i=>i.Id==position.ValuationId);
                    var owner=new AccountingPositionKey(position.Owner,position.BindingId,position.BookId);
                    var key=new MeasureBinding(position.Owner,position.BindingId);
                    switch(instrument)
                    {
                        case GasCommodityHoldingInstrument:
                            // Connected physical resources are prepared once
                            // after every exact source graph is available.
                            if(knownGas is null)throw Error("projection_gas_source","Retain complete source-known independent gas ownership.");
                            break;
                        case CashPaymentInstrument cash:
                            if(setup.BindingStates.GetValueOrDefault(key) is not RecordedCashValuation paid)
                                throw Error("projection_cash_state","Every native cash membership requires its complete recorded payment history.");
                            // The cash instrument's declared due treatment is
                            // nominal after due; no unused rate curve is read.
                            components.Add(new CashLifecycleProjection(owner,cash,paid,curves.GetValueOrDefault(cash.Currency),source.Snapshot));
                            break;
                        case FuturesContract:
                        {
                            var trade=source.Trades.SingleOrDefault(t=>t.Id==owner.TradeId)??throw Error("projection_futures_state","Retain original futures execution and booking facts.");
                            if(!opening.FuturesMargin.TryGetValue(key,out var actual))throw Error("projection_futures_state","Retain the complete native accepted margin prefix.");
                            var holding=trade.Instruments.Single(h=>h.Id==owner.BindingId);var market=FuturesExecution.Require(holding).MarketBinding;
                            if(!actual.History.Determinations.IsEmpty)
                            {
                                var past=market.Instrument.RequiredMarginOccurrences(trade,owner.BindingId,actual.History.Terms.Policy,source.Snapshot,
                                    actual.History.Determinations[^1].OccurrenceId,actual.History);
                                var current=await FuturesMarginSources.Read(connection,database,source.Data,source.Frame,opening.Context,market,actual.History.Terms.Policy,past,ct);
                                if(actual.History.Determinations.Any(d=>current[d.OccurrenceId]!=d.Evidence))
                                    throw Error("projection_margin_pending","Accept every source-known margin correction before projecting full economic risk.");
                                var closure=market.Instrument.PlanMarginTrajectory(trade,owner.BindingId,actual.History.Terms.Policy,source.Snapshot,past[^1].Id,current,
                                    actual.History,ImmutableDictionary<Guid,DateTimeOffset>.Empty);
                                if(closure.Steps.Any(s=>!s.NoChange))throw Error("projection_margin_pending","Accept every source-known margin correction before projecting full economic risk.");
                            }
                            var known=await FuturesKnownHistoryStore.Read(connection,database,source.Data,source.Frame,source.Snapshot,trade,owner.BindingId,actual,ct);
                            SelectOfficial(officials,market.Market,actual.History.Terms.Policy.SettlementCurve);
                            components.Add(new FuturesLifecycleProjection(trade,owner.BindingId,actual.State,actual.History,setup,market.Market,source.Data.Model,source.Snapshot,known));
                            productEvidence.Add(JsonSerializer.SerializeToElement(known,RiskJobs.Json));break;
                        }
                        case FuturesOptionInstrument option:
                        {
                            var native=source.Options.Single(p=>p.Trade==owner.TradeId&&p.OptionBinding==owner.BindingId);
                            var known=await FuturesOptionKnownHistoryStore.Read(connection,database,source.Data,source.Frame,opening.Context,native,ct);
                            var futureHistories=ImmutableDictionary.CreateBuilder<Guid,FuturesKnownHistory>();
                            foreach(var e in known.Events.Where(e=>e.EffectiveAt>source.Snapshot.ValuationAt&&e.Future is not null))
                                futureHistories.Add(e.EventId,await FuturesKnownHistoryStore.ReadLinkedFuture(connection,database,source.Data,source.Frame,source.Snapshot,e,ct));
                            var profile=await OptionProfile(connection,database,source,opening.Context,native,ct);
                            var margin=await FuturesPhysicalDeliveryStore.ReadPolicy(connection,database,source.Frame,
                                profile.GetProperty("policy"),profile.GetProperty("occurrences"),ct);
                            var selected=profile.GetProperty("profile");var contract=new ContractKey(source.Snapshot.Definitions,selected.Id("underlying_contract_id"));
                            var mark=new CurveRequest(new(source.Snapshot.Definitions,selected.Text("future_mark_curve")),contract);
                            var expiryMarket=new CurveRequest(new(source.Snapshot.Definitions,selected.Text("expiry_curve")),contract);
                            var future=FuturesStore.Binding(source.Data,mark.Curve.Code,mark.Contract.Id,owner.BindingId,FuturesMarketBinding.Implementation);
                            var r=source.Data.References["futures_option_rules"].Single(r=>r.Text("code")==option.Series.AutomaticExerciseRule);
                            var rule=new FuturesOptionAutomaticRule(new(source.Data.ReferenceRevision,r.Text("code")),Integer(r,"version"),r.Text("evaluation"),r.Number("in_the_money_threshold"),r.Text("at_the_money_call"),r.Text("at_the_money_put"));
                            var instruction=known.Events.Where(e=>e.Kind is "exercise_instruction" or "abandon_instruction").OrderBy(e=>e.PublicationOrder).LastOrDefault();
                            var choice=instruction is null?null:new KnownFuturesOptionInstruction(instruction.EventId,instruction.Kind=="exercise_instruction"?"exercise":"abandon",instruction.EffectiveAt,instruction.KnownAt,known.SourceContextId);
                            var trade=source.Trades.Single(t=>t.Id==owner.TradeId);SelectOfficial(officials,mark,margin.SettlementCurve);
                            foreach(var e in known.Events.Where(e=>e.EffectiveAt>source.Snapshot.ValuationAt&&e.Future is not null))
                            {
                                var heldFuture=e.Future!;var mapped=FuturesExecution.Require(heldFuture.Trade.Instruments.Single(h=>h.Id==heldFuture.BindingId)).MarketBinding.Market;
                                if(officials.TryGetValue(mapped,out var previous)&&previous!=heldFuture.MarginPolicy.SettlementCurve)
                                    throw Error("projection_input_evidence","A selected native input has conflicting official final sources.");
                                officials[mapped]=heldFuture.MarginPolicy.SettlementCurve;
                            }
                            components.Add(new FuturesOptionLifecycleProjection(trade,owner.BindingId,setup,future,margin,rule,choice,expiryMarket,mark,source.Data.Model,source.Snapshot,known,futureHistories.ToImmutable()));
                            productEvidence.Add(JsonSerializer.SerializeToElement(new{profile,known,futureHistories=futureHistories.OrderBy(p=>p.Key)},RiskJobs.Json));break;
                        }
                        case SidedInstrument swap when swap is FinancialSwapInstrument or MonthlyFinancialSwap or ScheduledFinancialSwap or CrossCommodityScheduledSwap:
                        {
                            var held=source.Swaps.Single(h=>h.Row.Id("trade_id")==owner.TradeId&&h.Row.Id("binding_id")==owner.BindingId);
                            var profiles=await SwapProfiles(connection,database,source,opening.Context,held,ct);
                            var due=profiles.Select(p=>p.Text("due_policy")).Distinct().Single();var recognition=profiles.Select(p=>p.Text("recognition_policy")).Distinct().Single();
                            var accepted=await new SwapStore(database).ProjectionSettlement(connection,held,due,recognition,ct);
                            var enabled=swap switch{FinancialSwapInstrument s=>s.Discounting,MonthlyFinancialSwap s=>s.Discounting,
                                ScheduledFinancialSwap s=>s.Discounting,CrossCommodityScheduledSwap s=>s.Discounting,_=>throw Error("projection_swap_source","Select a qualified financial swap.")};
                            ISideDiscounting discounting=enabled?curves[swap.Currency]:new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
                            var actual=new SwapBindingValuation(accepted,discounting,owner.BookId);
                            setup=setup with{BindingStates=setup.BindingStates.SetItem(key,actual)};
                            var swapSetup=setup;histories[^1]=new(source.Portfolio.Instruments,source.Portfolio.Positions,setup);
                            var required=accepted.RequiredPrices(swap);activePrices.AddRange(required);
                            var dependencies=swap.DependenciesAt(source.Snapshot);
                            var market=await CustomCurves.PrepareMarket(connection,database,source.Data,source.Frame,source.Snapshot,opening.Context,
                                [swap],dependencies,CustomCurves.MarketPreparation.ValuationDependencies,ct,activePrices:required);
                            var sourcePrices=market.Valuation.PaymentContext(market.Bindings,market.Authority,discounting,ct);
                            var mappings=SwapLifecycleProjection.ProjectedPriceMappings(swap,accepted,sourcePrices);
                            var payments=await SwapKnownPaymentStore.Read(connection,database,source.Frame,opening.Context,owner,Integer(held.Row,"revision"),ct);
                            var roundings=profiles.ToImmutableDictionary(p=>new SwapProjectionPeriod(Integer(p,"membership_ordinal"),DateWorkspace.Date(p.Text("delivery_from")),DateWorkspace.Date(p.Text("delivery_to"))),
                                p=>new SwapSettlementRounding(swap.Currency,Integer(p,"decimal_places"),CashTieRule.ToEven,p.Text("rounding_policy")));
                            components.Add(new SwapLifecycleProjection(source.Trades.Single(t=>t.Id==owner.TradeId),owner.BindingId,actual,swapSetup,sourcePrices,mappings,payments,roundings,source.Snapshot));
                            productEvidence.Add(JsonSerializer.SerializeToElement(new{owner,profiles,payments},RiskJobs.Json));break;
                        }
                        default:throw Error("projection_product_unavailable","This held instrument has no qualified economic lifecycle projection adapter.");
                    }
                }
            }
            sources.Add((first,components.ToImmutable(),curves.ToImmutable(),histories.ToImmutable(),officials.ToImmutable(),[..activePrices.Distinct()]));
        }
        if(knownGas is not null)
        {
            var preparedGas=await GasOwnedProjection.PreparePartitions(connection,database,knownGas,opening.OwnedGas,scope.Snapshot,
                horizon.Horizon.Closes.First(c=>c.Instant>scope.Frame.At).Instant,request.GasOwned,
                sources.ToImmutableDictionary(s=>s.Source.Snapshot.Definitions,s=>new GasOwnedProjectionMarket(s.Source.Snapshot,s.Curves)),ct);
            for(var index=0;index<sources.Count;index++)
            {
                var part=sources[index];sources[index]=part with{Components=part.Components.AddRange(preparedGas.Components.Where(c=>c.SourceSnapshot.Definitions==part.Source.Snapshot.Definitions))};
            }
            productEvidence.Add(JsonSerializer.SerializeToElement(new{known=knownGas,profiles=preparedGas.ProfileEvidence},RiskJobs.Json));
        }
        var projections=ImmutableArray.CreateBuilder<PreparedLifecycleProjection>();var markets=ImmutableArray.CreateBuilder<PortfolioValuation.MarketPartition>();
        foreach(var (source,components,curves,histories,officials,activePrices) in sources)
        {
            var portfolios=components.SelectMany(c=>c.Opening.Residuals).ToImmutableArray();
            var dependencies=components.SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
            var retained=retainedGraphs?.SingleOrDefault(g=>g.RevisionId==source.Snapshot.Definitions.RevisionId);
            if(retainedGraphs is not null&&retained is null)throw Error("risk_graph_manifest","Retain every complete economic market partition.");
            var connected=await CustomCurves.PrepareMarket(connection,database,source.Data,source.Frame,source.Snapshot,opening.Context,
                ValuationPortfolio.DistinctInstruments(portfolios),dependencies,CustomCurves.MarketPreparation.ScenarioUniverse,ct,retained,activePrices);
            var finals=await FinalInputs(connection,database,source,components,officials,request.Policy,horizon.Horizon.Closes.First(c=>c.Instant>source.Snapshot.ValuationAt).Instant,connected.Valuation.Scenario.Plan,ct);
            productEvidence.AddRange(finals.Evidence);finalEvidence.AddRange(finals.Evidence);
            var projection=LifecycleProjection.Prepare(connected.Valuation,connected.Bindings,connected.Authority,horizon.Horizon,request.Policy,components,[],curves,ct,finals.Inputs);
            projections.Add(projection);
            markets.Add(new(source.Data,source.Frame,source.Snapshot,RiskEngine.PrepareConnected(connected.Valuation,connected.Bindings,histories,connected.Authority,ct),
                [..histories.Select(_=>"economic_opening_present_value")],connected.Evidence,connected.Preparation){EconomicFactors=projection.Factors});
        }
        if(retainedGraphs is not null&&retainedGraphs.Value.Length!=markets.Count)throw Error("risk_graph_manifest","The retained economic market set differs from its complete prepared source.");
        return new(request,horizon.Evidence,projections.ToImmutable(),opening with{Markets=markets.ToImmutable()},productEvidence.ToImmutable(),receipts.ToImmutable(),finalEvidence.ToImmutable());
    }
    private static async Task<DateTimeOffset> ConfigurationAuthority(NpgsqlConnection connection,Guid catalog,CancellationToken ct)
    {
        await using var capture=ScheduleStore.Command(connection,"SELECT settlement_data.futures_margin_preview_authority($1)",catalog);
        return new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
    }
    private sealed record ConfigurationAdmission(Guid Catalog,Guid Trade,string Kind,Guid? Profile,int Revision,Guid Context,
        DateTimeOffset At,DateOnly Day,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var reference=ScheduleStore.Command(connection,"SELECT security.require_lifecycle_projection_profile($1,$2,$3,$4,$5,$6,$7,$8,$9)",
                Catalog,Kind,Trade,(object?)Profile??DBNull.Value,Revision,Context,At,Day,Until);
            reference.Parameters[3].NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Uuid;await reference.ExecuteNonQueryAsync(ct);
        }
    }
    private static async Task<JsonElement> OptionProfile(NpgsqlConnection connection,PreviewDatabase database,PortfolioProductSource source,ScheduleContext context,FuturesOptionPosition option,CancellationToken ct)
    {
        var until=await ConfigurationAuthority(connection,database.Manifest.CatalogId,ct);
        var rows=await PreviewDatabase.Rows(connection,"""
            SELECT jsonb_build_object('profile',to_jsonb(s),'policy',to_jsonb(p),'occurrences',
              (SELECT jsonb_agg(to_jsonb(o) ORDER BY o.at,o.phase) FROM settlement_data.futures_margin_policy_occurrences o
               WHERE(o.catalog_id,o.policy_id,o.policy_revision)=(p.catalog_id,p.id,p.revision)))::text
            FROM(SELECT s.*,k.known_from AS profile_known_at FROM lifecycle.option_profiles s JOIN trading.knowledge_receipts k
              ON(k.catalog_id,k.publication_order)=(s.catalog_id,s.publication_order)
              JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(s.catalog_id,$4)
              WHERE(s.catalog_id,s.trade_id,s.option_revision,s.action)=($1,$2,$3,'option.expiry')
                AND s.publication_order<=x.known_through AND k.known_from<=$5 ORDER BY s.revision DESC LIMIT 1)s
            JOIN settlement_data.futures_margin_policies p ON(p.catalog_id,p.id,p.revision)=(s.catalog_id,s.margin_policy_id,s.margin_policy_revision)
            """,[database.Manifest.CatalogId,option.Trade,option.Revision,context.ContextId!.Value,source.Snapshot.Knowledge!.KnownAt],ct);
        if(rows.Length!=1)throw Error("projection_option_policy","Save an exact expiry source and linked-future margin profile for every held option.");
        var admission=new ConfigurationAdmission(database.Manifest.CatalogId,option.Trade,"listed_option",null,Integer(rows[0].GetProperty("profile"),"revision"),context.ContextId!.Value,source.Frame.At,source.Frame.Time.Date,until);
        database.RegisterCommitAdmission(connection,admission);await admission.RequireCommit(connection,ct);
        return rows[0];
    }
    private static async Task<ImmutableArray<JsonElement>> SwapProfiles(NpgsqlConnection connection,PreviewDatabase database,PortfolioProductSource source,
        ScheduleContext context,SwapStore.ResolvedSwapHolding holding,CancellationToken ct)
    {
        var until=await ConfigurationAuthority(connection,database.Manifest.CatalogId,ct);
        var profiles=await PreviewDatabase.Rows(connection,"""
            SELECT to_jsonb(s)::text FROM(SELECT DISTINCT ON(s.id)s.*,k.known_from FROM lifecycle.swap_profiles s
              JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(s.catalog_id,s.publication_order)
              JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(s.catalog_id,$4)
              WHERE(s.catalog_id,s.trade_id,s.origin_revision,s.action)=($1,$2,$3,'swap.determine')
                AND s.publication_order<=x.known_through AND k.known_from<=$5 ORDER BY s.id,s.revision DESC)s
            ORDER BY s.membership_ordinal,s.payment_ordinal
            """,[database.Manifest.CatalogId,holding.Row.Id("trade_id"),Integer(holding.Row,"revision"),context.ContextId!.Value,source.Snapshot.Knowledge!.KnownAt],ct);
        var periods=holding.Instrument.Sides.SelectMany((m,n)=>((SidePaymentSchedule)m.Side.GenerateSchedule()).Payments.Select(p=>
            (Membership:n,From:p.DeliveryFrom,To:p.DeliveryTo,Ordinal:p.Key.Ordinal))).ToHashSet();
        if(profiles.IsEmpty||!periods.SetEquals(profiles.Select(p=>(Integer(p,"membership_ordinal"),DateWorkspace.Date(p.Text("delivery_from")),DateWorkspace.Date(p.Text("delivery_to")),Integer(p,"payment_ordinal"))))||
            profiles.Length!=periods.Count||profiles.Any(p=>p.Text("due_policy")!="swap_due_nominal_v1"||p.Text("recognition_policy")!="swap_completed_period_trading_pnl_v1"||
                p.Text("correction_policy")!="swap_explicit_correction_due_v1"||p.Text("rounding_policy")!=SwapSettlementRounding.Implementation||
                new ReferenceKey(new(p.Id("reference_catalog_id"),p.Id("reference_revision_id")),p.Text("currency_code"))!=holding.Instrument.Currency))
            throw Error("projection_swap_policy","Save exact settlement and rounding settings for every contractual swap payment period before economic projection.");
        foreach(var profile in profiles)
        {
            var admission=new ConfigurationAdmission(database.Manifest.CatalogId,holding.Row.Id("trade_id"),"financial_swap",profile.Id("id"),Integer(profile,"revision"),context.ContextId!.Value,source.Frame.At,source.Frame.Time.Date,until);
            database.RegisterCommitAdmission(connection,admission);await admission.RequireCommit(connection,ct);
        }
        return profiles;
    }
    private static void SelectOfficial(ImmutableDictionary<CurveRequest,CurveKey>.Builder selections,CurveRequest market,CurveKey official)
    {
        if(selections.TryGetValue(market,out var existing)&&existing!=official)
            throw Error("projection_input_evidence","A selected native market cannot have conflicting official final sources.");
        selections[market]=official;
    }
    private static int Integer(JsonElement row,string name)=>int.Parse(row.Text(name),CultureInfo.InvariantCulture);
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
