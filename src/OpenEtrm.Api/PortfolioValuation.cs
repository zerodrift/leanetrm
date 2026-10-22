using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;
using System.Globalization;
using System.Text.Json;

namespace OpenEtrm.Api;

// An operation selects every policy explicitly. Generated closes use the same
// authorized, retained trade projection as an interactive book calculation.
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioValuationPolicy(string CurveNumericalPolicy,string RiskNumericalPolicy,
    string FuturesConvention,string SwapConvention,string RateNumericalPolicy,string DiscountInterpolationPolicy,
    string BumpConvention,string BumpBasis,int BumpTicks);
public sealed record PortfolioPositionEvidence(Guid TradeId,int Revision,Guid BindingId,Guid InstrumentId,
    Guid ValuationId,Guid BookId,string Measure,Guid DefinitionRevisionId,int PublicationOrder,Quantity Quantity)
{
    public string TradeReference{get;init;}="";
    public string InstrumentReference{get;init;}="";
    public string InstrumentType{get;init;}="";
}
public sealed record PortfolioFactor(QuoteFactor Factor,ExactDecimal BumpSize);
public sealed record PortfolioTotal(string Measure,Guid BookId,ExactMoney Value);
public sealed record PortfolioFactorRisk(string Measure,Guid BookId,ReferenceKey Currency,InputKey Factor,
    ExactMoney Up,ExactMoney Down,ExactMoney UpPnl,ExactMoney DownPnl,ExactRiskDerivative Delta,ExactRiskDerivative Gamma);
public sealed record PortfolioDiscountEvidence(Guid InstrumentId,SidePaymentKey Payment,PaymentDiscount Input);
public sealed record PortfolioMarketScenario(DefinitionRevision Definitions,QuoteScenario Scenario);
public sealed record PortfolioConnectedScenario(DefinitionRevision Definitions,string ManifestId,string Id,string? ParentId,int Depth,GraphWork Work,ConnectedPreparationWork Preparation);
public sealed record PortfolioHoldingValue(string Measure,ExactRiskPositionValue Position);
public sealed record PortfolioHoldingRisk(string Measure,InputKey Factor,ExactRiskPositionResult Position);
public sealed record PortfolioValuationResult(Guid SnapshotId,ScheduleContext Context,PortfolioValuationPolicy Policy,
    ImmutableArray<UnitPricingContract> ModelContracts,ImmutableArray<PortfolioPositionEvidence> Positions,
    ImmutableArray<PortfolioFactor> Factors,ImmutableArray<PortfolioTotal> Totals,
    ImmutableArray<PortfolioFactorRisk> Risk,RiskWork Work)
{
    public ImmutableArray<PortfolioDiscountEvidence> DiscountInputs{get;init;}=[];
    public ImmutableArray<PortfolioMarketScenario> Markets{get;init;}=[];
    public ImmutableArray<PortfolioConnectedScenario> ConnectedScenarios{get;init;}=[];
    public ImmutableArray<PortfolioHoldingValue> BasePositions{get;init;}=[];
    public ImmutableArray<PortfolioHoldingRisk> HoldingRisk{get;init;}=[];
    public RiskDetail Detail{get;init;}=RiskDetail.Books;
}

public sealed class PortfolioValuation(PreviewDatabase database)
{
    public const string FuturesMeasure="futures_execution_pnl";
    public const string SwapMeasure="swap_present_value";
    public sealed record MarketPartition(PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot,PreparedPortfolioRisk? Risk,
        ImmutableArray<string> Measures)
    {
        public PreparedConnectedPortfolioRisk? Connected{get;init;}
        public ConnectedMarketEvidence? Evidence{get;init;}
        public ConnectedPreparationWork? Preparation{get;init;}
        public ImmutableArray<QuoteFactor> Factors=>Connected?.Factors??Risk!.Factors;
        public string ScenarioId=>Connected?.Valuation.Scenario.Id??Risk!.Scenario.Id;
    }
    public sealed record Prepared(Guid SnapshotId,ScheduleContext Context,PortfolioValuationPolicy Policy,
        ImmutableArray<MarketPartition> Markets,ImmutableArray<PortfolioPositionEvidence> Positions,
        ImmutableArray<PortfolioDiscountEvidence> DiscountInputs)
    {
        public ScenarioWork MaterializationWork{get;init;}=new(0,0,0,0);
        public int MaterializationScenarioCount{get;init;}
        public ImmutableArray<QuoteFactor> Factors=>Markets.SelectMany(m=>m.Factors).OrderBy(f=>f.Input.Series.Revision.RevisionId)
            .ThenBy(f=>f.Input.Series.Id).ToImmutableArray();
        public ImmutableArray<ConnectedMarketEvidence> ConnectedMarkets=>Markets.Where(m=>m.Evidence is not null).Select(m=>m.Evidence!).ToImmutableArray();
    }
    public static void ValidatePolicy(PortfolioValuationPolicy policy)
    {
        if(policy is null||policy.CurveNumericalPolicy!=CurveModel.NumericalPolicy||policy.RiskNumericalPolicy!=RiskEngine.ExactNumericalPolicy||
            policy.FuturesConvention!=FuturesRisk.Implementation||policy.SwapConvention!=SwapStore.Convention||
            policy.RateNumericalPolicy!=ZeroRateCurve.Policy||policy.DiscountInterpolationPolicy!=DiscountInterpolation.Policy||
            policy.BumpConvention!=QuoteBump.Convention||policy.BumpBasis!="trading_tick"||policy.BumpTicks is <1 or >100)
            throw new MarketDataException("portfolio_policy","Select the supported exact valuation policies and one to 100 symmetric trading ticks explicitly.");
    }
    public async Task<PortfolioValuationResult> Calculate(NpgsqlConnection connection,PreviewData data,MarketSnapshot snapshot,
        ScheduleContext context,ImmutableArray<Guid> bookIds,PortfolioValuationPolicy policy,CancellationToken ct)
    {
        var exact=PinnedContext.Exact(context);
        await RequireValuationContext(connection,exact,snapshot.ValuationAt,ct);
        var frame=new InputFrame(snapshot.Id,"Portfolio inputs",snapshot.ValuationAt,
            snapshot.Selections.Select(s=>s.Observation.Key.Id).ToImmutableArray(),ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,
            exact.ContextId,snapshot.Selections.Where(s=>s.Input.Use==InputUse.FrozenFact).Select(s=>s.Observation.Key.Id).ToImmutableArray());
        var prepared=await Prepare(connection,data,frame,snapshot,exact,bookIds,policy,[],ct);
        var bumps=prepared.Factors.Select(f=>new QuoteBump(f.Input,f.Quote,f.Unit,
            f.TradingTick*ExactDecimal.Parse(policy.BumpTicks.ToString(CultureInfo.InvariantCulture)),policy.BumpConvention)).ToImmutableArray();
        var result=Calculate(prepared,bumps,[],RiskDetail.Books,ct);
        await RequireValuationContext(connection,exact,snapshot.ValuationAt,ct);
        return result;
    }
    private async Task RequireValuationContext(NpgsqlConnection connection,ScheduleContext context,DateTimeOffset at,CancellationToken ct)
    {
        await using var guard=ScheduleStore.Command(connection,"SELECT security.require_bound_market_context($1,$2,$3)",database.Manifest.CatalogId,context.ContextId!.Value,at);
        await guard.ExecuteNonQueryAsync(ct);
    }
    public async Task<Prepared> Prepare(NpgsqlConnection connection,PreviewData data,InputFrame frame,MarketSnapshot snapshot,
        ScheduleContext context,ImmutableArray<Guid> bookIds,PortfolioValuationPolicy policy,
        ImmutableArray<(PreviewData Data,InputFrame Frame)> supplemental,CancellationToken ct,
        ImmutableArray<ConnectedMarketEvidence>? retainedGraphs=null)
    {
        ValidatePolicy(policy);ct.ThrowIfCancellationRequested();
        var exact=PinnedContext.Exact(context);
        await new KnowledgeStore(database.Manifest.CatalogId).Retained(connection,exact,ct);
        if(bookIds.IsDefaultOrEmpty||bookIds.Length>128||bookIds.Distinct().Count()!=bookIds.Length||bookIds.Any(id=>id==Guid.Empty)||
            snapshot.Definitions!=data.DefinitionRevision||frame.ContextId!=exact.ContextId||frame.Id!=snapshot.Id||frame.At!=snapshot.ValuationAt||
            DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime)!=PublishedSchedules.ContextDate(exact))
            throw new MarketDataException("portfolio_context","Select explicit books and exact market frames under one certified effective context.");
        if(!data.Snapshot(frame).HasSameInputs(snapshot))
            throw new MarketDataException("portfolio_context","Every selected observation and use must match the exact authorized snapshot inputs.");
        var futuresStore=new FuturesStore(database.Manifest);
        var books=await futuresStore.Books(connection,ct);
        var futures=await futuresStore.ReadTrades(connection,data,books,bookIds,[],ct);
        var swaps=await new SwapStore(database).ReadRiskHoldings(connection,data,frame,bookIds,ct,snapshot,supplemental);
        var materialized=new List<(PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot,ValuationPortfolio Portfolio,string Measure)>();
        var materializationWork=new ScenarioWork(0,0,0,0);var materializationScenarios=0;
        var evidence=ImmutableArray.CreateBuilder<PortfolioPositionEvidence>();
        if(!futures.Trades.IsEmpty)
        {
            var (market,portfolio)=FuturesRisk.PreparePortfolio(futures.Trades,snapshot,data.Model,policy.FuturesConvention,ct);
            materializationWork=new(market.EvaluatedConstituents,market.ReusedConstituents,0,0);materializationScenarios++;
            materialized.Add((data,frame,snapshot,portfolio,FuturesMeasure));
            var rows=futures.Rows.ToDictionary(r=>(r.Id("trade_id"),r.Id("binding_id")));
            foreach(var position in portfolio.Positions)
            {
                var row=rows[(position.TradeId,position.BindingId)];
                evidence.Add(Evidence(row,position,FuturesMeasure,"binding_publication","future"));
            }
        }
        foreach(var marketHoldings in swaps.GroupBy(h=>h.Snapshot.Definitions).OrderBy(g=>g.Key.RevisionId))
        {
            var instruments=marketHoldings.DistinctBy(h=>h.Instrument.Id).ToImmutableArray();
            var positions=marketHoldings.Select(h=>new RiskPosition(h.Row.Id("trade_id"),h.Row.Id("binding_id"),h.Row.Id("book_id"),h.Instrument.Id,
                h.Quantity,new(new Money(default,h.Instrument.Currency),h.Quantity.Specification),new(default,h.Instrument.Currency))).ToImmutableArray();
            var portfolio=new ValuationPortfolio(instruments.Select(h=>(Instrument)h.Instrument).ToImmutableArray(),positions,
                new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
                {InstrumentDiscounting=instruments.ToImmutableDictionary(h=>h.Instrument.Id,h=>h.Discounting)});
            var source=instruments[0];materialized.Add((source.Data,source.Frame,source.Snapshot,portfolio,SwapMeasure));
            var rows=marketHoldings.ToDictionary(h=>h.Row.Id("trade_id"),h=>h.Row);
            foreach(var position in positions)
                evidence.Add(Evidence(rows[position.TradeId],position,SwapMeasure,"publication_order","financial_swap"));
        }
        if(materialized.Count==0)throw new MarketDataException("risk_position","The selected books have no visible supported holdings.");
        if(evidence.Select(p=>(p.TradeId,p.BindingId)).Distinct().Count()!=evidence.Count||
            materialized.SelectMany(p=>p.Portfolio.Instruments).Select(i=>i.Id).Distinct().Count()!=materialized.Sum(p=>p.Portfolio.Instruments.Length))
            throw new MarketDataException("risk_contract","Every held binding and unit instrument must belong to exactly one market and valuation measure.");
        var markets=ImmutableArray.CreateBuilder<MarketPartition>();
        foreach(var group in materialized.GroupBy(m=>m.Snapshot.Definitions).OrderBy(g=>g.Key.RevisionId))
        {
            var first=group.First();
            var portfolios=group.Select(m=>m.Portfolio).ToImmutableArray();
            var instruments=portfolios.SelectMany(p=>p.Instruments).ToImmutableArray();
            var dependencies=portfolios.SelectMany(p=>p.Declare(first.Snapshot)).SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
            var hasCustom=instruments.OfType<FinancialSwapInstrument>().Any(i=>i.Floating.CustomProjection is {} binding&&
                i.Floating.Projections.Any(p=>dependencies.Contains(binding.Request(p.Market))));
            if(hasCustom)
            {
                var retained=retainedGraphs?.SingleOrDefault(g=>g.RevisionId==first.Snapshot.Definitions.RevisionId);
                if(retainedGraphs is not null&&retained is null)throw new MarketDataException("risk_graph_manifest","The job omitted a required connected market manifest.");
                var connected=await CustomCurves.PrepareMarket(connection,database,first.Data,first.Frame,first.Snapshot,exact,instruments,dependencies,ct,retained);
                markets.Add(new(first.Data,first.Frame,first.Snapshot,null,group.Select(m=>m.Measure).ToImmutableArray())
                    {Connected=RiskEngine.PrepareConnected(connected.Valuation,connected.Bindings,portfolios,connected.Authority,ct),Evidence=connected.Evidence,Preparation=connected.Preparation});
            }
            else markets.Add(new(first.Data,first.Frame,first.Snapshot,RiskEngine.Prepare(first.Data.Model,first.Snapshot,portfolios,ct),group.Select(m=>m.Measure).ToImmutableArray()));
        }
        if(retainedGraphs is not null&&retainedGraphs.Value.Length!=markets.Count(m=>m.Connected is not null))
            throw new MarketDataException("risk_graph_manifest","The admitted graph set differs from the held projection set.");
        var discountInputs=swaps.DistinctBy(h=>h.Instrument.Id).SelectMany(h=>((CurrencyDefaultDiscounting)h.Discounting).Inputs
            .OrderBy(p=>p.Key.SideId).ThenBy(p=>p.Key.Ordinal).Select(p=>new PortfolioDiscountEvidence(h.Instrument.Id,p.Key,p.Value))).ToImmutableArray();
        return new(snapshot.Id,exact,policy,markets.ToImmutable(),evidence.ToImmutable(),discountInputs)
            {MaterializationWork=materializationWork,MaterializationScenarioCount=materializationScenarios};
    }
    public static PortfolioValuationResult Calculate(Prepared prepared,ImmutableArray<QuoteBump> bumps,
        ImmutableArray<QuoteShift> stress,RiskDetail detail,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();ValidatePolicy(prepared.Policy);
        if(bumps.IsDefault||stress.IsDefault||!Enum.IsDefined(detail)||prepared.Markets.IsDefaultOrEmpty)
            throw new MarketDataException("risk_contract","Supply explicit scenarios, detail and complete prepared market partitions.");
        if(bumps.Any(b=>b is null||b.ConventionId!=QuoteBump.Convention||b.Size.CompareTo(default)<=0)||
            stress.Any(s=>s is null||s.ConventionId!=QuoteShift.Convention||s.Size==default))
            throw new MarketDataException("risk_bump","Select explicit supported shock conventions and positive bumps or nonzero signed stress shifts.");
        var available=prepared.Markets.SelectMany(m=>m.Data.Model.QuoteFactors(m.Snapshot)).ToDictionary(f=>f.Input);
        if(bumps.Select(b=>b.Input).Distinct().Count()!=bumps.Length||bumps.Any(b=>!available.TryGetValue(b.Input,out var f)||f.Quote!=b.Quote||f.Unit!=b.Unit)||
            stress.Any(s=>!available.TryGetValue(s.Input,out var f)||f.Quote!=s.Quote||f.Unit!=s.Unit))
            throw new MarketDataException("risk_factor","Every selected shock must match an exact pinned live market factor.");
        var runs=prepared.Markets.Select(m=>Run(m,bumps,stress,prepared.Policy.RiskNumericalPolicy,detail,ct)).ToImmutableArray();
        var groups=runs.SelectMany(r=>r.Groups.Select((g,n)=>(Measure:r.Market.Measures[n],Group:g))).ToImmutableArray();
        var totals=groups.SelectMany(g=>g.Group.BaseValues.Select(v=>new PortfolioTotal(g.Measure,v.BookId,v.Value)))
            .GroupBy(v=>(v.Measure,v.BookId,v.Value.Currency)).Select(g=>new PortfolioTotal(g.Key.Measure,g.Key.BookId,g.Select(v=>v.Value).Aggregate((a,b)=>a.Add(b)))).ToImmutableArray();
        var holdingRisk=ImmutableArray.CreateBuilder<PortfolioHoldingRisk>();
        var totalsRisk=ImmutableArray.CreateBuilder<PortfolioFactorRisk>();
        foreach(var bump in bumps)
        {
            ct.ThrowIfCancellationRequested();
            var contributions=new List<(string Measure,Guid Book,ExactDealRiskValues Values)>();
            foreach(var (measure,group) in groups)
            {
                var factor=group.Factors.SingleOrDefault(f=>f.Bump.Input==bump.Input);
                if(factor is null)
                {
                    contributions.AddRange(group.BaseValues.Select(v=>(measure,v.BookId,Unchanged(v.Value,bump))));
                    holdingRisk.AddRange(group.BasePositions.Select(p=>new PortfolioHoldingRisk(measure,bump.Input,
                        new(p.TradeId,p.BindingId,p.BookId,p.ValuationId,Unchanged(p.Value,bump)))));
                }
                else
                {
                    contributions.AddRange(factor.Totals.Select(t=>(measure,t.BookId,t.Values)));
                    holdingRisk.AddRange(factor.Positions.Select(p=>new PortfolioHoldingRisk(measure,bump.Input,p)));
                }
            }
            foreach(var group in contributions.GroupBy(v=>(v.Measure,v.Book,v.Values.Base.Currency)))
            {
                var value=group.Select(v=>v.Values).Aggregate(Add);
                totalsRisk.Add(new(group.Key.Measure,group.Key.Book,group.Key.Currency,bump.Input,value.Up,value.Down,value.UpPnl,value.DownPnl,value.Delta,value.Gamma));
            }
        }
        var work=runs.Select(r=>r.Work).ToArray();
        var combined=new RiskWork(work.Sum(w=>w.PositionCount),work.Sum(w=>w.UniqueValuations),work.Sum(w=>w.UniqueMarketContracts),1+2*bumps.Length,
            work.Sum(w=>w.UnitPricerCalls),work.Sum(w=>w.RebuiltCurveNodes)+prepared.MaterializationWork.RebuiltCurveNodes,
            work.Sum(w=>w.ReusedCurveNodes)+prepared.MaterializationWork.ReusedCurveNodes)
            {QuantityApplications=work.Sum(w=>w.QuantityApplications),PreparationScenarioCount=work.Sum(w=>w.PreparationScenarioCount)+prepared.MaterializationScenarioCount};
        var finalFactors=available.Values.Select(f=>f with{BaseValue=f.BaseValue+stress.Where(s=>s.Input==f.Input)
            .Aggregate(default(ExactDecimal),(sum,s)=>sum+s.Size)}).ToDictionary(f=>f.Input);
        ct.ThrowIfCancellationRequested();
        return new(prepared.SnapshotId,prepared.Context,prepared.Policy,groups.SelectMany(g=>g.Group.Contracts).ToImmutableArray(),prepared.Positions,
            bumps.Select(b=>new PortfolioFactor(finalFactors[b.Input],b.Size)).ToImmutableArray(),totals,totalsRisk.ToImmutable(),combined)
        {
            DiscountInputs=prepared.DiscountInputs,Detail=detail,
            Markets=runs.Where(r=>r.Scenario is not null).Select(r=>new PortfolioMarketScenario(r.Market.Snapshot.Definitions,r.Scenario!)).ToImmutableArray(),
            ConnectedScenarios=runs.Where(r=>r.Connected is not null).Select(r=>r.Connected!).ToImmutableArray(),
            BasePositions=groups.SelectMany(g=>g.Group.BasePositions.Select(p=>new PortfolioHoldingValue(g.Measure,p))).ToImmutableArray(),HoldingRisk=holdingRisk.ToImmutable()
        };
    }
    private sealed record GroupResult(ImmutableArray<UnitPricingContract> Contracts,ImmutableArray<BookCurrencyValue> BaseValues,
        ImmutableArray<ExactRiskPositionValue> BasePositions,ImmutableArray<ExactBookRiskFactor> Factors);
    private sealed record MarketResult(MarketPartition Market,ImmutableArray<GroupResult> Groups,RiskWork Work,
        QuoteScenario? Scenario,PortfolioConnectedScenario? Connected);
    internal static ImmutableDictionary<InputKey,CurveShockTarget> QuoteTargets(ConnectedScenario scenario)=>scenario.Plan.Points.Values
        .Where(p=>p.Expression is ConnectedSource{Input.Use:InputUse.Variable})
        .OrderBy(p=>p.Key.Curve.Revision.RevisionId).ThenBy(p=>p.Key.Curve.Code,StringComparer.Ordinal).ThenBy(p=>p.Key.PointId).GroupBy(p=>((ConnectedSource)p.Expression).Input)
        .ToImmutableDictionary(g=>g.Key,g=>(CurveShockTarget)new PointCurveTarget(
            g.First().Key.Curve,[g.First().Key.PointId]));
    private static MarketResult Run(MarketPartition market,ImmutableArray<QuoteBump> bumps,ImmutableArray<QuoteShift> stress,
        string policy,RiskDetail detail,CancellationToken ct)
    {
        if(market.Connected is not {} connected)
        {
            var risk=market.Risk!;var local=market.Data.Model.QuoteFactors(market.Snapshot).Select(f=>f.Input).ToHashSet();
            foreach(var shift in stress)if(local.Contains(shift.Input))risk=RiskEngine.WithShifts(risk,[shift],ct);
            var run=RiskEngine.CalculateExact(risk,bumps.Where(b=>local.Contains(b.Input)).ToImmutableArray(),policy,ct,detail);
            return new(market,run.Groups.Select(g=>new GroupResult(g.Contracts,g.BaseValues,g.BasePositions,g.Factors)).ToImmutableArray(),run.Work,risk.Scenario,null);
        }
        var localInputs=market.Data.Model.QuoteFactors(market.Snapshot).Select(f=>f.Input).ToHashSet();
        var validation=market.Data.Model.Build(market.Snapshot,[],ct);
        foreach(var shift in stress.Where(s=>localInputs.Contains(s.Input)))validation=market.Data.Model.WithShifts(validation,[shift],ct);
        var localBumps=bumps.Where(b=>localInputs.Contains(b.Input)).ToImmutableArray();
        if(!localBumps.IsEmpty)foreach(var pair in market.Data.Model.Scenarios(validation,localBumps,ct))ct.ThrowIfCancellationRequested();
        var inputs=connected.Factors.Select(f=>f.Input).ToHashSet();
        var targets=QuoteTargets(connected.Valuation.Scenario);
        foreach(var shift in stress.Where(s=>inputs.Contains(s.Input)))
            connected=RiskEngine.WithShifts(connected,[new(CurveShockCoordinate.MarketQuote,targets[shift.Input],shift.Unit,shift.Size,CurveShift.ParentAdditive)],ct);
        var selected=bumps.Where(b=>inputs.Contains(b.Input)).ToImmutableArray();
        var connectedRun=RiskEngine.CalculateExact(connected,selected.Select(b=>new ConnectedRiskBump(CurveShockCoordinate.MarketQuote,
            targets[b.Input],b.Unit,b.Size,ConnectedRiskBump.Convention)).ToImmutableArray(),policy,ct,detail);
        var groups=connectedRun.Groups.Select(g=>new GroupResult(g.Contracts,g.BaseValues,g.BasePositions,
            g.Factors.Select((f,n)=>new ExactBookRiskFactor(selected[n],f.Totals,
                new(0,0,f.UpWork.UnitPricerCalls,f.UpWork.ReusedUnitValues),new(0,0,f.DownWork.UnitPricerCalls,f.DownWork.ReusedUnitValues)){Positions=f.Positions}).ToImmutableArray())).ToImmutableArray();
        var w=connectedRun.Work;var scenario=connected.Valuation.Scenario;
        // Graph reads and affected nodes have their own counters; they are not
        // interchangeable with legacy distinct curve-build reuse counts.
        var work=new RiskWork(w.PositionCount,w.UniqueValuations,w.UniqueMarketContracts,w.ScenarioCount,w.UnitPricerCalls,0,0)
            {QuantityApplications=w.QuantityApplications,PreparationScenarioCount=w.PreparationScenarioCount};
        return new(market,groups,work,null,new(market.Snapshot.Definitions,scenario.Plan.ManifestId,scenario.Id,scenario.ParentId,scenario.Depth,connectedRun.GraphWork,market.Preparation!));
    }
    private static ExactDealRiskValues Unchanged(ExactMoney value,QuoteBump bump)
    {
        var zero=value.Subtract(value);
        return new(value,value,value,zero,zero,new(zero,new(value.Currency,bump.Unit,1)),new(zero,new(value.Currency,bump.Unit,2)));
    }
    private static ExactDealRiskValues Add(ExactDealRiskValues a,ExactDealRiskValues b)=>new(a.Base.Add(b.Base),a.Up.Add(b.Up),a.Down.Add(b.Down),
        a.UpPnl.Add(b.UpPnl),a.DownPnl.Add(b.DownPnl),new(a.Delta.Value.Add(b.Delta.Value),a.Delta.Unit),new(a.Gamma.Value.Add(b.Gamma.Value),a.Gamma.Unit));
    private static PortfolioPositionEvidence Evidence(JsonElement row,RiskPosition position,string measure,string publication,string instrumentType)=>new(
        position.TradeId,int.Parse(row.Text("revision"),CultureInfo.InvariantCulture),position.BindingId,row.Id("instrument_id"),position.ValuationId,
        position.BookId,measure,row.Id("definition_revision_id"),int.Parse(row.Text(publication),CultureInfo.InvariantCulture),position.Quantity)
        {TradeReference=TradingReference.Trade(row),InstrumentReference=TradingReference.Instrument(row),InstrumentType=instrumentType};
}
