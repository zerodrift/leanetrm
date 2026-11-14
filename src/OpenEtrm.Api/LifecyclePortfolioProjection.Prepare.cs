using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
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
        if(opening.Policy.FuturesConvention!=FuturesMarginValuationPolicy.Convention)
            throw Error("projection_futures_state","Economic risk requires the explicit native margin remaining-value profile.");
        var sources=ImmutableArray.CreateBuilder<(PortfolioProductSource Source,ImmutableArray<ILifecycleProjection> Components,ImmutableDictionary<ReferenceKey,ISideDiscounting> Curves)>();
        var productEvidence=ImmutableArray.CreateBuilder<JsonElement>();var receipts=ImmutableArray.CreateBuilder<FuturesOptionCalibrationReceipt>();
        foreach(var group in opening.Sources.GroupBy(s=>s.Snapshot.Definitions).OrderBy(g=>g.Key.RevisionId))
        {
            var first=group.First();
            if(group.Any(s=>!s.Snapshot.HasSameInputs(first.Snapshot)))throw Error("projection_source","A market partition requires one exact source certificate and input selection.");
            var currencies=group.SelectMany(s=>s.Portfolio.Instruments).Select(i=>i.Currency).Distinct().ToArray();
            var curves=ImmutableDictionary.CreateBuilder<ReferenceKey,ISideDiscounting>();
            foreach(var currency in currencies)
            {
                var mapping=await SwapStore.CurrencyDefault(connection,database.Manifest.CatalogId,first.Data,first.Frame,currency,ct)??
                    throw Error("projection_cash_policy","The selected exact-instant economic profile requires an explicit captured currency curve.");
                curves.Add(currency,new LifecycleCurrencyDiscounting(mapping,first.Snapshot,request.RateTimingPolicy,ClockChange.FrozenInputs));
            }
            var components=ImmutableArray.CreateBuilder<ILifecycleProjection>();
            foreach(var source in group)
            {
                var setup=source.Portfolio.Setup with{InstrumentDiscounting=source.Portfolio.Instruments.ToImmutableDictionary(i=>i.Id,i=>curves[i.Currency])};
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
                foreach(var position in source.Portfolio.Positions)
                {
                    var instrument=source.Portfolio.Instruments.Single(i=>i.Id==position.ValuationId);
                    var owner=new AccountingPositionKey(position.Owner,position.BindingId,position.BookId);
                    var key=new MeasureBinding(position.Owner,position.BindingId);
                    switch(instrument)
                    {
                        case CashPaymentInstrument cash:
                            if(setup.BindingStates.GetValueOrDefault(key) is not RecordedCashValuation paid)
                                throw Error("projection_cash_state","Every native cash membership requires its complete recorded payment history.");
                            components.Add(new CashLifecycleProjection(owner,cash,paid,curves[cash.Currency],source.Snapshot));
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
                            components.Add(new FuturesLifecycleProjection(trade,owner.BindingId,actual.State,actual.History,setup,market.Market,source.Data.Model,source.Snapshot,known));
                            productEvidence.Add(JsonSerializer.SerializeToElement(known,RiskJobs.Json));break;
                        }
                        case FuturesOptionInstrument option:
                        {
                            var native=source.Options.Single(p=>p.Trade==owner.TradeId&&p.OptionBinding==owner.BindingId);
                            var known=await FuturesOptionKnownHistoryStore.Read(connection,database,source.Data,source.Frame,opening.Context,native,ct);
                            if(known.Events.Any(e=>e.EffectiveAt>source.Snapshot.ValuationAt&&e.Kind is "exercise" or "assignment" or "expiry"))
                                throw Error("projection_option_pending","Prepare the accepted future option transitions and linked native futures before sampling.");
                            var profile=await OptionProfile(connection,database,source,opening.Context,native,ct);
                            var margin=FuturesMarginStore.ReadPolicy(profile.GetProperty("policy"),profile.GetProperty("occurrences"));
                            var selected=profile.GetProperty("profile");var contract=new ContractKey(source.Snapshot.Definitions,selected.Id("underlying_contract_id"));
                            var mark=new CurveRequest(new(source.Snapshot.Definitions,selected.Text("future_mark_curve")),contract);
                            var expiryMarket=new CurveRequest(new(source.Snapshot.Definitions,selected.Text("expiry_curve")),contract);
                            var future=source.Data.Model.BindFutures(owner.BindingId,mark,source.Data.FuturesSeries.Single(s=>s.Key==option.Series.UnderlyingSeries).SettlementMethod,
                                FuturesMarketBinding.Implementation);
                            var r=source.Data.References["futures_option_rules"].Single(r=>r.Text("code")==option.Series.AutomaticExerciseRule);
                            var rule=new FuturesOptionAutomaticRule(new(source.Data.ReferenceRevision,r.Text("code")),Integer(r,"version"),r.Text("evaluation"),r.Number("in_the_money_threshold"),r.Text("at_the_money_call"),r.Text("at_the_money_put"));
                            var instruction=known.Events.Where(e=>e.Kind is "exercise_instruction" or "abandon_instruction").OrderBy(e=>e.PublicationOrder).LastOrDefault();
                            var choice=instruction is null?null:new KnownFuturesOptionInstruction(instruction.EventId,instruction.Kind=="exercise_instruction"?"exercise":"abandon",instruction.EffectiveAt,instruction.KnownAt,known.SourceContextId);
                            var trade=source.Trades.Single(t=>t.Id==owner.TradeId);
                            components.Add(new FuturesOptionLifecycleProjection(trade,owner.BindingId,setup,future,margin,rule,choice,expiryMarket,mark,source.Data.Model,source.Snapshot));
                            productEvidence.Add(JsonSerializer.SerializeToElement(new{profile,known},RiskJobs.Json));break;
                        }
                        case SidedInstrument:
                            throw Error("projection_swap_source","Prepare the exact native period profiles and contractual swap price source before sampling.");
                        default:throw Error("projection_product_unavailable","This held instrument has no qualified economic lifecycle projection adapter.");
                    }
                }
            }
            sources.Add((first,components.ToImmutable(),curves.ToImmutable()));
        }
        var projections=ImmutableArray.CreateBuilder<PreparedLifecycleProjection>();var markets=ImmutableArray.CreateBuilder<PortfolioValuation.MarketPartition>();
        foreach(var (source,components,curves) in sources)
        {
            var portfolios=components.SelectMany(c=>c.Opening.Residuals).ToImmutableArray();
            var dependencies=components.SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
            var retained=retainedGraphs?.SingleOrDefault(g=>g.RevisionId==source.Snapshot.Definitions.RevisionId);
            if(retainedGraphs is not null&&retained is null)throw Error("risk_graph_manifest","Retain every complete economic market partition.");
            var connected=await CustomCurves.PrepareMarket(connection,database,source.Data,source.Frame,source.Snapshot,opening.Context,
                ValuationPortfolio.DistinctInstruments(portfolios),dependencies,CustomCurves.MarketPreparation.ScenarioUniverse,ct,retained);
            var projection=LifecycleProjection.Prepare(connected.Valuation,connected.Bindings,connected.Authority,horizon.Horizon,request.Policy,components,[],curves,ct);
            projections.Add(projection);
            markets.Add(new(source.Data,source.Frame,source.Snapshot,RiskEngine.PrepareConnected(connected.Valuation,connected.Bindings,portfolios,connected.Authority,ct),
                [..portfolios.Select(_=>"economic_opening_present_value")],connected.Evidence,connected.Preparation){EconomicFactors=projection.Factors});
        }
        if(retainedGraphs is not null&&retainedGraphs.Value.Length!=markets.Count)throw Error("risk_graph_manifest","The retained economic market set differs from its complete prepared source.");
        return new(request,horizon.Evidence,projections.ToImmutable(),opening with{Markets=markets.ToImmutable()},productEvidence.ToImmutable(),receipts.ToImmutable());
    }
    private static async Task<JsonElement> OptionProfile(NpgsqlConnection connection,PreviewDatabase database,PortfolioProductSource source,ScheduleContext context,FuturesOptionPosition option,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"""
            SELECT jsonb_build_object('profile',to_jsonb(s),'policy',to_jsonb(p),'occurrences',
              (SELECT jsonb_agg(to_jsonb(o) ORDER BY o.at,o.phase) FROM settlement_data.futures_margin_policy_occurrences o
               WHERE(o.catalog_id,o.policy_id,o.policy_revision)=(p.catalog_id,p.id,p.revision)))::text
            FROM(SELECT s.* FROM lifecycle.option_profiles s JOIN trading.knowledge_receipts k
              ON(k.catalog_id,k.publication_order)=(s.catalog_id,s.publication_order)
              JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(s.catalog_id,$4)
              WHERE(s.catalog_id,s.trade_id,s.option_revision,s.action)=($1,$2,$3,'option.expiry')
                AND s.publication_order<=x.known_through AND k.known_from<=$5 ORDER BY s.revision DESC LIMIT 1)s
            JOIN settlement_data.futures_margin_policies p ON(p.catalog_id,p.id,p.revision)=(s.catalog_id,s.margin_policy_id,s.margin_policy_revision)
            """,[database.Manifest.CatalogId,option.Trade,option.Revision,context.ContextId!.Value,source.Snapshot.Knowledge!.KnownAt],ct);
        if(rows.Length!=1)throw Error("projection_option_policy","Save an exact expiry source and linked-future margin profile for every held option.");
        return rows[0];
    }
    private static int Integer(JsonElement row,string name)=>int.Parse(row.Text(name),CultureInfo.InvariantCulture);
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
