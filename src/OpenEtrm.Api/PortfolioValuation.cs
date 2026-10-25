using OpenEtrm.Shared.Calculation;
using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Power;
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
    public DeliveryCoverage? Coverage{get;init;}
    public string TradeReference{get;init;}="";
    public string InstrumentReference{get;init;}="";
    public string InstrumentType{get;init;}="";
}
// D218: a held position a portfolio risk run leaves out because its base valuation at the run's close
// fails for a position-specific reason. The run lists it, and no holding row or total includes it.
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record ExcludedPosition(Guid TradeId,Guid BindingId,string TradeReference,string InstrumentType,string Reason)
{
    // The held rows a run keeps. Every listed position must be exactly one of the held rows.
    internal static ImmutableArray<JsonElement> Keep(ImmutableArray<JsonElement> held,ImmutableArray<ExcludedPosition> excluded)
    {
        var kept=held.RemoveAll(row=>excluded.Any(e=>e.TradeId==row.Id("trade_id")&&e.BindingId==row.Id("binding_id")&&e.TradeReference==TradingReference.Trade(row)));
        return held.Length-kept.Length==excluded.Length?kept:
            throw new MarketDataException("risk_contract","Every position a risk run leaves out must be a distinct held position in its scope.");
    }
}
public sealed record PortfolioFactor(PortfolioRiskAxis Axis,ExactDecimal BumpSize);
public sealed record PortfolioTotal(string Measure,Guid BookId,ExactMoney Value);
public sealed record PortfolioFactorRisk(string Measure,Guid BookId,ReferenceKey Currency,string Factor,
    ExactMoney Up,ExactMoney Down,ExactMoney UpPnl,ExactMoney DownPnl,ExactRiskDerivative Delta,ExactRiskDerivative Gamma);
public sealed record PortfolioDiscountEvidence(Guid InstrumentId,SidePaymentKey Payment,PaymentDiscount Input)
{public bool Ppa{get;init;} public Guid? SettlementDeterminationId{get;init;}}
public sealed record PortfolioConnectedScenario(DefinitionRevision Definitions,string ManifestId,string Id,string? ParentId,int Depth,GraphWork Work,ConnectedPreparationWork Preparation);
public sealed record PortfolioHoldingValue(string Measure,ExactRiskPositionValue Position);
public sealed record PortfolioHoldingRisk(string Measure,string Factor,ExactRiskPositionResult Position);
public sealed record PortfolioValuationResult(Guid SnapshotId,ScheduleContext Context,PortfolioValuationPolicy Policy,
    ImmutableArray<UnitPricingContract> ModelContracts,ImmutableArray<PortfolioPositionEvidence> Positions,
    ImmutableArray<PortfolioFactor> Factors,ImmutableArray<PortfolioTotal> Totals,
    ImmutableArray<PortfolioFactorRisk> Risk,RiskWork Work)
{
    public ImmutableArray<PortfolioDiscountEvidence> DiscountInputs{get;init;}=[];
    public ImmutableArray<PortfolioConnectedScenario> ConnectedScenarios{get;init;}=[];
    public ImmutableArray<PortfolioHoldingValue> BasePositions{get;init;}=[];
    public ImmutableArray<PortfolioHoldingRisk> HoldingRisk{get;init;}=[];
    public RiskDetail Detail{get;init;}=RiskDetail.Books;
}

public sealed class PortfolioValuation(PreviewDatabase database)
{
    public const string PpaMeasure="ppa_value_excluding_overdue";
    public const string FuturesMeasure="futures_execution_pnl";
    public const string SwapMeasure="swap_present_value";
    private const string FutureType="future",SwapType="financial_swap";
    public sealed record MarketPartition(PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot,
        PreparedConnectedPortfolioRisk Connected,ImmutableArray<string> Measures,
        ConnectedMarketEvidence Evidence,ConnectedPreparationWork Preparation)
    {
        public ImmutableArray<QuoteFactor> Factors=>Connected.Factors;
        public string ScenarioId=>Connected.Scenario.Id;
    }
    public sealed record Prepared(Guid SnapshotId,ScheduleContext Context,PortfolioValuationPolicy Policy,
        ImmutableArray<MarketPartition> Markets,ImmutableArray<PortfolioPositionEvidence> Positions,
        ImmutableArray<PortfolioDiscountEvidence> DiscountInputs,ImmutableArray<ExcludedPosition> Excluded)
    {
        public ScenarioWork MaterializationWork{get;init;}=new(0,0,0,0);
        public int MaterializationScenarioCount{get;init;}
        public ImmutableArray<QuoteFactor> Factors=>Markets.SelectMany(m=>m.Factors).OrderBy(f=>f.Input.Series.Revision.RevisionId)
            .ThenBy(f=>f.Input.Series.Id).ToImmutableArray();
        public ImmutableArray<ConnectedMarketEvidence> ConnectedMarkets=>Markets.Select(m=>m.Evidence).ToImmutableArray();
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
        var prepared=await Prepare(connection,data,frame,snapshot,exact,bookIds,policy,[],excluded:[],ct);
        var bumps=prepared.Factors.Select(f=>new PortfolioCurveBump(CurveRiskAxes.Quote(prepared,f,ct),
            f.TradingTick*ExactDecimal.Parse(policy.BumpTicks.ToString(CultureInfo.InvariantCulture)))).ToImmutableArray();
        var result=Calculate(prepared,bumps,[],RiskDetail.Books,ct);
        await RequireValuationContext(connection,exact,snapshot.ValuationAt,ct);
        return result;
    }
    private async Task RequireValuationContext(NpgsqlConnection connection,ScheduleContext context,DateTimeOffset at,CancellationToken ct)
    {
        await using var guard=ScheduleStore.Command(connection,"SELECT security.require_bound_market_context($1,$2,$3)",database.Manifest.CatalogId,context.ContextId!.Value,at);
        await guard.ExecuteNonQueryAsync(ct);
    }
    // D218: a portfolio risk run values every held position the selected close can value. One preparation
    // normally values them all. Only when it fails for a position-specific reason are the positions whose
    // base valuation fails left out, with the reasons the Blotter reports for them, and the rest prepared;
    // a failure that remains fails the run.
    public async Task<Prepared> PrepareValued(NpgsqlConnection connection,PreviewData data,InputFrame frame,MarketSnapshot snapshot,
        ScheduleContext context,ImmutableArray<Guid> bookIds,PortfolioValuationPolicy policy,
        ImmutableArray<(PreviewData Data,InputFrame Frame)> supplemental,CancellationToken ct)
    {
        try{return await Prepare(connection,data,frame,snapshot,context,bookIds,policy,supplemental,[],ct);}
        catch(MarketDataException error) when(ValuationStatus.PositionSpecific(error)){}
        return await Prepare(connection,data,frame,snapshot,context,bookIds,policy,supplemental,
            await Unvalued(connection,data,frame,snapshot,bookIds,ct),ct);
    }
    // The held futures and swaps the Blotter reports unvalued at the selected close, by trade reference.
    private async Task<ImmutableArray<ExcludedPosition>> Unvalued(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        MarketSnapshot snapshot,ImmutableArray<Guid> bookIds,CancellationToken ct)
    {
        var futuresStore=new FuturesStore(database.Manifest);
        var futures=await futuresStore.ReadTrades(connection,data,await futuresStore.Books(connection,ct),bookIds,[],ct);
        var markets=ValuationStatus.BuildValued(held=>data.Model.Build(snapshot,held),[..futures.Trades.SelectMany(t=>t.Instruments.Select(p=>p.MarketBinding.Market)).Distinct()]).Unvalued;
        var rows=futures.Rows.ToDictionary(r=>(r.Id("trade_id"),r.Id("binding_id")));
        var swaps=await new SwapStore(database).Holdings(connection,data,frame,bookIds,[],ct);
        return [..futures.Trades.SelectMany(t=>t.Instruments.Where(p=>markets.ContainsKey(p.MarketBinding.Market)).Select(p=>
                new ExcludedPosition(t.Id,p.Id,TradingReference.Trade(rows[(t.Id,p.Id)]),FutureType,markets[p.MarketBinding.Market])))
            .Concat(swaps.Unvalued.Select(u=>new ExcludedPosition(u.Row.Id("trade_id"),u.Row.Id("binding_id"),TradingReference.Trade(u.Row),SwapType,u.Reason)))
            .OrderBy(e=>e.TradeReference,StringComparer.Ordinal).ThenBy(e=>e.BindingId)];
    }
    // Prepares every held position in scope except exactly the excluded ones. Any failure fails it.
    public async Task<Prepared> Prepare(NpgsqlConnection connection,PreviewData data,InputFrame frame,MarketSnapshot snapshot,
        ScheduleContext context,ImmutableArray<Guid> bookIds,PortfolioValuationPolicy policy,
        ImmutableArray<(PreviewData Data,InputFrame Frame)> supplemental,ImmutableArray<ExcludedPosition> excluded,CancellationToken ct,
        ImmutableArray<ConnectedMarketEvidence>? retainedGraphs=null)
    {
        ValidatePolicy(policy);ct.ThrowIfCancellationRequested();
        if(excluded.IsDefault||excluded.Any(e=>e is null||e.InstrumentType is not(FutureType or SwapType)||!ValuationStatus.PositionSpecific(e.Reason)))
            throw new MarketDataException("risk_contract","A risk run leaves out only held positions, each with a position-specific valuation reason.");
        var exact=PinnedContext.Exact(context);
        await new KnowledgeStore(database.Manifest.CatalogId).Retained(connection,exact,ct);
        if(bookIds.IsDefaultOrEmpty||bookIds.Length>128||bookIds.Distinct().Count()!=bookIds.Length||bookIds.Any(id=>id==Guid.Empty)||
            snapshot.Definitions!=data.DefinitionRevision||frame.ContextId!=exact.ContextId||frame.Id!=snapshot.Id||frame.At!=snapshot.ValuationAt||
            DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime)!=PublishedSchedules.ContextDate(exact))
            throw new MarketDataException("portfolio_context","Select explicit books and exact market frames under one certified effective context.");
        if(!data.Snapshot(frame).HasSameInputs(snapshot))
            throw new MarketDataException("portfolio_context","Every selected observation and use must match the exact authorized snapshot inputs.");
        await using(var ppa=ScheduleStore.Command(connection,"SELECT security.ppa_scope_has_positions($1,$2::uuid[])",database.Manifest.CatalogId,bookIds.ToArray()))
            if((bool)(await ppa.ExecuteScalarAsync(ct))!)
                throw new MarketDataException("portfolio_ppa_unsupported","The complete books contain PPA positions outside the connected portfolio consumer.");
        var futuresStore=new FuturesStore(database.Manifest);
        var books=await futuresStore.Books(connection,ct);
        var futures=await futuresStore.ReadTrades(connection,data,books,bookIds,[],ct);
        var swaps=await new SwapStore(database).ReadRiskHoldings(connection,data,frame,bookIds,[..excluded.Where(e=>e.InstrumentType==SwapType)],ct,snapshot,supplemental);
        var kept=ExcludedPosition.Keep(futures.Rows,[..excluded.Where(e=>e.InstrumentType==FutureType)]).Select(r=>(r.Id("trade_id"),r.Id("binding_id"))).ToHashSet();
        var trades=futures.Trades.Where(t=>t.Instruments.Any(p=>kept.Contains((t.Id,p.Id)))).Select(t=>t.Instruments.All(p=>kept.Contains((t.Id,p.Id)))?t:
            new FuturesTrade(t.Id,t.BookId,[..t.Instruments.Where(p=>kept.Contains((t.Id,p.Id)))],t.Booking)).ToImmutableArray();
        var materialized=new List<(PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot,ValuationPortfolio Portfolio,string Measure)>();
        var materializationWork=new ScenarioWork(0,0,0,0);var materializationScenarios=0;
        var evidence=ImmutableArray.CreateBuilder<PortfolioPositionEvidence>();
        if(!trades.IsEmpty)
        {
            var (market,portfolio)=FuturesRisk.PreparePortfolio(trades,snapshot,data.Model,policy.FuturesConvention,ct);
            materializationWork=new(market.EvaluatedConstituents,market.ReusedConstituents,0,0);materializationScenarios++;
            materialized.Add((data,frame,snapshot,portfolio,FuturesMeasure));
            var rows=futures.Rows.ToDictionary(r=>(r.Id("trade_id"),r.Id("binding_id")));
            foreach(var position in portfolio.Positions)
            {
                var row=rows[(position.TradeId,position.BindingId)];
                evidence.Add(Evidence(row,position,FuturesMeasure,"binding_publication",FutureType));
            }
        }
        foreach(var marketHoldings in swaps.GroupBy(h=>h.Snapshot.Definitions).OrderBy(g=>g.Key.RevisionId))
        {
            var instruments=marketHoldings.DistinctBy(h=>h.Instrument.Id).ToImmutableArray();
            var positions=marketHoldings.Select(h=>new RiskPosition(h.Row.Id("trade_id"),h.Row.Id("binding_id"),h.Row.Id("book_id"),h.Instrument.Id,
                h.Quantity,new(new Money(default,h.Instrument.Currency),h.Quantity.Specification),new(default,h.Instrument.Currency))).ToImmutableArray();
            var portfolio=new ValuationPortfolio(instruments.Select(h=>(Instrument)h.Instrument).ToImmutableArray(),positions,
                new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
                {InstrumentDiscounting=marketHoldings.Where(h=>h.Settlement is null).DistinctBy(h=>h.Instrument.Id).ToImmutableDictionary(h=>h.Instrument.Id,h=>h.Discounting),
                 Settlements=marketHoldings.Where(h=>h.Settlement is not null).ToImmutableDictionary(h=>new MeasureBinding(h.Row.Id("trade_id"),h.Row.Id("binding_id")),h=>new SwapBindingValuation(h.Settlement!,h.Discounting))});
            var source=instruments[0];materialized.Add((source.Data,source.Frame,source.Snapshot,portfolio,SwapMeasure));
            var rows=marketHoldings.ToDictionary(h=>h.Row.Id("trade_id"),h=>h.Row);
            foreach(var position in positions)
                evidence.Add(Evidence(rows[position.TradeId],position,SwapMeasure,"publication_order",SwapType));
        }
        if(materialized.Count==0)throw new MarketDataException("risk_position",excluded.IsEmpty?"The selected books have no visible supported holdings.":
            "The selected close cannot value any held position in the selected books.");
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
            var retained=retainedGraphs?.SingleOrDefault(g=>g.RevisionId==first.Snapshot.Definitions.RevisionId);
            if(retainedGraphs is not null&&retained is null)throw new MarketDataException("risk_graph_manifest","The job omitted a required connected market manifest.");
            var connected=await CustomCurves.PrepareMarket(connection,database,first.Data,first.Frame,first.Snapshot,exact,instruments,dependencies,ct,retained);
            markets.Add(new(first.Data,first.Frame,first.Snapshot,
                RiskEngine.PrepareConnected(connected.Valuation,connected.Bindings,portfolios,connected.Authority,ct),
                group.Select(m=>m.Measure).ToImmutableArray(),connected.Evidence,connected.Preparation));
        }
        if(retainedGraphs is not null&&retainedGraphs.Value.Length!=markets.Count)
            throw new MarketDataException("risk_graph_manifest","The admitted graph set differs from the held projection set.");
        var discountInputs=SwapStore.DiscountEvidence(swaps);
        return new(snapshot.Id,exact,policy,markets.ToImmutable(),evidence.ToImmutable(),discountInputs,excluded)
            {MaterializationWork=materializationWork,MaterializationScenarioCount=materializationScenarios};
    }
    public static PortfolioValuationResult Calculate(Prepared prepared,ImmutableArray<PortfolioCurveBump> bumps,
        ImmutableArray<PortfolioCurveShift> stress,RiskDetail detail,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();ValidatePolicy(prepared.Policy);
        if(bumps.IsDefault||stress.IsDefault||!Enum.IsDefined(detail)||prepared.Markets.IsDefaultOrEmpty)
            throw new MarketDataException("risk_contract","Supply explicit scenarios, detail and complete prepared market partitions.");
        if(bumps.Any(b=>b is null||b.Axis is null||b.Size.CompareTo(default)<=0)||
            stress.Any(s=>s is null||s.Axis is null||s.Size==default)||bumps.Select(b=>b.Axis.Id).Distinct().Count()!=bumps.Length)
            throw new MarketDataException("risk_bump","Select unique exact curve coordinates with positive bumps or nonzero signed stress shifts.");
        PortfolioRiskAxis Canonical(PortfolioRiskAxis axis)
        {
            var resolved=CurveRiskAxes.Resolve(prepared,axis.Request,ct);
            if(resolved.Id!=axis.Id||resolved.Market!=axis.Market||resolved.Unit!=axis.Unit||!resolved.Points.SequenceEqual(axis.Points))
                throw new MarketDataException("risk_factor","Every selected coordinate must match the exact prepared financial market.");
            return resolved;
        }
        bumps=bumps.Select(b=>b with{Axis=Canonical(b.Axis)}).ToImmutableArray();
        stress=stress.Select(s=>s with{Axis=Canonical(s.Axis)}).ToImmutableArray();
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
                var factor=group.Factors.SingleOrDefault(f=>f.AxisId==bump.Axis.Id);
                if(factor is null)
                {
                    contributions.AddRange(group.BaseValues.Select(v=>(measure,v.BookId,Unchanged(v.Value,bump))));
                    holdingRisk.AddRange(group.BasePositions.Select(p=>new PortfolioHoldingRisk(measure,bump.Axis.Id,
                        new(p.TradeId,p.BindingId,p.BookId,p.ValuationId,Unchanged(p.Value,bump)))));
                }
                else
                {
                    contributions.AddRange(factor.Totals.Select(t=>(measure,t.BookId,t.Values)));
                    holdingRisk.AddRange(factor.Positions.Select(p=>new PortfolioHoldingRisk(measure,bump.Axis.Id,p)));
                }
            }
            foreach(var group in contributions.GroupBy(v=>(v.Measure,v.Book,v.Values.Base.Currency)))
            {
                var value=group.Select(v=>v.Values).Aggregate(Add);
                totalsRisk.Add(new(group.Key.Measure,group.Key.Book,group.Key.Currency,bump.Axis.Id,value.Up,value.Down,value.UpPnl,value.DownPnl,value.Delta,value.Gamma));
            }
        }
        var work=runs.Select(r=>r.Work).ToArray();
        var combined=new RiskWork(work.Sum(w=>w.PositionCount),work.Sum(w=>w.UniqueValuations),work.Sum(w=>w.UniqueMarketContracts),1+2*bumps.Length,
            work.Sum(w=>w.UnitPricerCalls),work.Sum(w=>w.RebuiltCurveNodes)+prepared.MaterializationWork.RebuiltCurveNodes,
            work.Sum(w=>w.ReusedCurveNodes)+prepared.MaterializationWork.ReusedCurveNodes)
            {QuantityApplications=work.Sum(w=>w.QuantityApplications),PreparationScenarioCount=work.Sum(w=>w.PreparationScenarioCount)+prepared.MaterializationScenarioCount};
        ct.ThrowIfCancellationRequested();
        return new(prepared.SnapshotId,prepared.Context,prepared.Policy,ModelContracts(groups.SelectMany(g=>g.Group.Contracts)),prepared.Positions,
            bumps.Select(b=>new PortfolioFactor(runs.Single(r=>r.Market.Snapshot.Definitions==b.Axis.Market).Axes[b.Axis.Id],b.Size)).ToImmutableArray(),totals,totalsRisk.ToImmutable(),combined)
        {
            DiscountInputs=prepared.DiscountInputs,Detail=detail,
            ConnectedScenarios=runs.Select(r=>r.Connected).ToImmutableArray(),
            BasePositions=groups.SelectMany(g=>g.Group.BasePositions.Select(p=>new PortfolioHoldingValue(g.Measure,p))).ToImmutableArray(),HoldingRisk=holdingRisk.ToImmutable()
        };
    }
    internal static ImmutableArray<UnitPricingContract> ModelContracts(IEnumerable<UnitPricingContract> contracts) =>
        contracts.GroupBy(c=>c.Id).Select(g=>
        {
            var first=g.First();
            if(g.Any(c=>c.ModelVersion!=first.ModelVersion||c.Quantity!=first.Quantity||c.Currency!=first.Currency))
                throw new MarketDataException("portfolio_model_receipt","One instrument receipt requires consistent model, quantity and currency lineage across every binding scope.");
            return first with{Dependencies=g.SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray()};
        }).ToImmutableArray();

    private sealed record GroupResult(ImmutableArray<UnitPricingContract> Contracts,ImmutableArray<BookCurrencyValue> BaseValues,
        ImmutableArray<ExactRiskPositionValue> BasePositions,ImmutableArray<FactorResult> Factors);
    private sealed record FactorResult(string AxisId,ImmutableArray<ExactRiskBookTotal> Totals,ImmutableArray<ExactRiskPositionResult> Positions);
    private sealed record MarketResult(MarketPartition Market,ImmutableArray<GroupResult> Groups,RiskWork Work,
        PortfolioConnectedScenario Connected,ImmutableDictionary<string,PortfolioRiskAxis> Axes);
    internal static ImmutableDictionary<InputKey,CurveShockTarget> QuoteTargets(ConnectedScenario scenario)=>scenario.Plan.Points.Values
        .Where(p=>p.Expression is ConnectedSource{Input.Use:InputUse.Variable})
        .OrderBy(p=>p.Key.Curve.Revision.RevisionId).ThenBy(p=>p.Key.Curve.Code,StringComparer.Ordinal).ThenBy(p=>p.Key.PointId).GroupBy(p=>((ConnectedSource)p.Expression).Input)
        .ToImmutableDictionary(g=>g.Key,g=>(CurveShockTarget)new PointCurveTarget(
            g.First().Key.Curve,[g.First().Key.PointId]));
    private static MarketResult Run(MarketPartition market,ImmutableArray<PortfolioCurveBump> bumps,ImmutableArray<PortfolioCurveShift> stress,
        string policy,RiskDetail detail,CancellationToken ct)
    {
        var connected=market.Connected;
        foreach(var shift in stress.Where(s=>s.Axis.Market==market.Snapshot.Definitions))
            connected=RiskEngine.WithShifts(connected,[new(shift.Axis.Coordinate,shift.Axis.Target,shift.Axis.Unit,shift.Size,CurveShift.ParentAdditive)],ct);
        var selected=bumps.Where(b=>b.Axis.Market==market.Snapshot.Definitions).ToImmutableArray();
        var scenario=connected.Scenario;
        var axes=selected.ToImmutableDictionary(b=>b.Axis.Id,b=>b.Axis.Quote is {} quote?
            b.Axis with{Quote=quote with{BaseValue=scenario.Price(b.Axis.Points.First(p=>scenario.Plan.Points[p].Expression is ConnectedSource source&&source.Input==quote.Input),ct).ToUnitPriceExact().Value}}:b.Axis);
        var run=RiskEngine.CalculateExact(connected,selected.Select(b=>new ConnectedRiskBump(b.Axis.Coordinate,
            b.Axis.Target,b.Axis.Unit,b.Size,ConnectedRiskBump.Convention)).ToImmutableArray(),policy,ct,detail);
        var groups=run.Groups.Select(g=>new GroupResult(g.Contracts,g.BaseValues,g.BasePositions,
            g.Factors.Select((f,n)=>new FactorResult(selected[n].Axis.Id,f.Totals,f.Positions)).ToImmutableArray())).ToImmutableArray();
        var w=run.Work;
        // Graph reads and affected nodes have distinct counters from materialized
        // curve receipts; report the actual graph work in ConnectedScenarios.
        var work=new RiskWork(w.PositionCount,w.UniqueValuations,w.UniqueMarketContracts,w.ScenarioCount,w.UnitPricerCalls,0,0)
            {QuantityApplications=w.QuantityApplications,PreparationScenarioCount=w.PreparationScenarioCount};
        return new(market,groups,work,new(market.Snapshot.Definitions,scenario.Plan.ManifestId,scenario.Id,scenario.ParentId,scenario.Depth,run.GraphWork,market.Preparation),axes);
    }
    private static ExactDealRiskValues Unchanged(ExactMoney value,PortfolioCurveBump bump)
    {
        var zero=value.Subtract(value);
        return new(value,value,value,zero,zero,new(zero,new(value.Currency,bump.Axis.Unit,1)),new(zero,new(value.Currency,bump.Axis.Unit,2)));
    }
    private static ExactDealRiskValues Add(ExactDealRiskValues a,ExactDealRiskValues b)=>new(a.Base.Add(b.Base),a.Up.Add(b.Up),a.Down.Add(b.Down),
        a.UpPnl.Add(b.UpPnl),a.DownPnl.Add(b.DownPnl),new(a.Delta.Value.Add(b.Delta.Value),a.Delta.Unit),new(a.Gamma.Value.Add(b.Gamma.Value),a.Gamma.Unit));
    private static PortfolioPositionEvidence Evidence(JsonElement row,RiskPosition position,string measure,string publication,string instrumentType)=>new(
        position.TradeId,int.Parse(row.Text("revision"),CultureInfo.InvariantCulture),position.BindingId,row.Id("instrument_id"),position.ValuationId,
        position.BookId,measure,row.Id("definition_revision_id"),int.Parse(row.Text(publication),CultureInfo.InvariantCulture),position.Quantity)
        {TradeReference=TradingReference.Trade(row),InstrumentReference=TradingReference.Instrument(row),InstrumentType=instrumentType};
}
