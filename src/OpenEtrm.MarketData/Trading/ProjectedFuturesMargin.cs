using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// A path-local economic basis, never an accepted native determination. Native
// planners and writers accept FuturesMarginState/FuturesSettlementFact only.
public sealed record ProjectedFuturesMarginState(Guid TradeId,Guid BindingId,Guid? BookId,Quantity Quantity,ReferenceKey Series,
    ExactUnitPrice Basis,DateTimeOffset BasisAt,bool Final,Guid SourceContextId,DateTimeOffset KnownThrough):IBindingValuationState;

public sealed partial record FuturesContract
{
    private void RequireProjected(ProjectedFuturesMarginState state)
    {
        if(state.TradeId==Guid.Empty||state.BindingId==Guid.Empty||state.BookId is null||state.BookId==Guid.Empty||state.SourceContextId==Guid.Empty||
            state.Series!=SeriesKey||state.Quantity is null||state.Quantity.Specification!=UnitQuantity.Specification||state.Quantity.Amount==default||
            !state.Quantity.Amount.IsMultipleOf(LotSize.Amount)||state.Basis is null||state.Basis.Unit.Currency!=Currency||
            state.Basis.Unit.Quantity!=UnitQuantity.Specification||state.Basis.Unit.Multiplier!=Series.Multiplier||
            !TradingInstant.IsSupported(state.BasisAt)||!TradingInstant.IsSupported(state.KnownThrough)||
            state.Final&&(Series.SettlementMethod!="cash"||state.BasisAt<Series.LastTradingAt))
            throw new MarketDataException("projection_futures_state","Supply the exact path-local held future, quotation, basis and source pins.");
    }
    private void RequireProjectedContext(ProjectedFuturesMarginState state,UnitValuationContext context)
    {
        RequireProjected(state);
        if(!context.IsLifecycleProjection||context.Snapshot.Knowledge is not {} knowledge||state.SourceContextId!=knowledge.ContextId||
            state.KnownThrough!=knowledge.KnownAt||state.BasisAt>context.CalculationAt)
            throw new MarketDataException("projection_futures_state","A hypothetical basis requires its exact projection path and certified opening inputs.");
    }
}

public sealed class FuturesLifecycleProjection:ILifecycleProjection
{
    private readonly Trade trade;
    private readonly TradeInstrument holding;
    private readonly FuturesMarketBinding market;
    private readonly FuturesMarginPolicy policy;
    private readonly ValuationSetup setup;
    private readonly MarketSnapshot source;
    public CurveRequest SettlementProjection{get;}
    public string Implementation=>"futures_margin_economic_projection_v1";
    public AccountingPositionKey Owner=>new(trade.Id,holding.Id,trade.BookId);
    public ReferenceKey Currency=>market.Instrument.Currency;
    public ImmutableArray<CurveRequest> Dependencies{get;}
    public LifecycleProjectionState Opening{get;}
    public ImmutableArray<LifecycleProjectionEvent> Events{get;}
    public FuturesLifecycleProjection(Trade trade,Guid bindingId,FuturesMarginState state,FuturesMarginHistory history,
        ValuationSetup setup,CurveRequest settlementProjection,CurveModel model,MarketSnapshot source)
    {
        this.trade=trade;holding=trade.Instruments.SingleOrDefault(b=>b.Id==bindingId)??throw Error("Select the exact held future.");
        market=FuturesExecution.Require(holding).MarketBinding;policy=history.Terms.Policy;this.source=source;
        var terms=market.Instrument.ProposeMarginTerms(trade,bindingId,policy,model,source);
        if(history.Terms!=terms||history.SourceContextId!=source.Knowledge?.ContextId||history.KnownThrough!=source.Knowledge.KnownAt||
            state.TradeId!=trade.Id||state.BindingId!=bindingId||state.BookId!=trade.BookId||state.Quantity!=holding.Quantity||
            state.SourceContextId!=source.Knowledge.ContextId||state.KnownThrough!=source.Knowledge.KnownAt||setup.Measure!=ValuationMeasure.PresentValue||
            !setup.BindingStates.TryGetValue(new(trade.Id,bindingId),out var supplied)||!ReferenceEquals(supplied,state))
            throw Error("Projection must retain the exact adopted native history, held quantity, accepted state and opening setup.");
        var execution=FuturesExecution.Require(holding);
        var past=policy.Occurrences.Where(o=>(o.At>execution.ExecutedAt||o.At==execution.ExecutedAt&&policy.Opening==FuturesMarginOpening.IncludeOpeningInstant)&&
            o.At<=source.ValuationAt).OrderBy(o=>o.At).ThenBy(o=>o.Phase).ToArray();
        if(history.Determinations.IsDefault||history.Determinations.Length!=past.Length||
            !past.Select(o=>o.Id).ToHashSet().SetEquals(history.Determinations.Select(d=>d.OccurrenceId)))
            throw Error("Every required opening margin occurrence needs its accepted native source history before economic projection.");
        if(past.Length>0)
        {
            var accepted=market.Instrument.PlanMarginTrajectory(trade,bindingId,policy,source,past[^1].Id,
                history.Determinations.ToImmutableDictionary(d=>d.OccurrenceId,d=>d.Evidence),history,ImmutableDictionary<Guid,DateTimeOffset>.Empty);
            if(accepted.Steps.Any(s=>!s.NoChange)||accepted.Basis.Price!=state.SettledBasis||accepted.Basis.At!=state.SettledAt||accepted.Basis.Status!=state.Status)
                throw Error("The complete native prefix and accepted opening basis must reconcile before projection.");
        }
        else if(state.SettledBasis!=ExactUnitPrice.FromPrice(execution.ExecutionPrice)||state.SettledAt!=execution.ExecutedAt||
            state.Status!=FuturesMarginStatus.Open||state.LatestSettlement is not null)
            throw Error("An inception must retain its exact original execution basis without invented accepted margin.");
        this.setup=setup;
        if(settlementProjection.Contract!=policy.Contract||model.AssetDefinition(settlementProjection.Curve).Rule!=CurveRule.Direct||
            PriceUnit.FromQuote(model.QuoteFor(settlementProjection))!=PriceUnit.FromQuote(market.Quote))
            throw Error("Select the exact future contract and native quotation for its explicit scenario-event market mapping.");
        SettlementProjection=settlementProjection;
        Dependencies=[..market.Dependencies.Append(settlementProjection).Distinct()];
        Opening=new([ValuationPortfolio.FromTrades([new(trade.Id,trade.BookId,[holding],trade.Booking)],setup)],[]);
        Events=state.Status==FuturesMarginStatus.FinalMarginDetermined?[]:[..policy.Occurrences.Where(o=>o.At>source.ValuationAt)
            .OrderBy(o=>o.At).ThenBy(o=>o.Phase).Select((o,n)=>new LifecycleProjectionEvent(o.Id,o.At,
                policy.Occurrences.Where(p=>p.At>source.ValuationAt&&(p.At<o.At||p.At==o.At&&p.Phase<o.Phase)).Select(p=>p.Id).ToImmutableArray(),
                o.Phase==FuturesMarginPhase.Final?"final_margin":"daily_margin"))];
    }
    public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
    {
        var selected=policy.Occurrences.Single(o=>o.Id==occurrence.Id);
        if(selected.Phase==FuturesMarginPhase.Final&&market.SettlementMethod!="cash")
            throw new MarketDataException("futures_delivery_transition","Physical final settlement requires its qualified projected delivery adapter.");
        var basis=state.Residuals.Single().Setup.BindingStates[new(trade.Id,holding.Id)] switch{
            FuturesMarginState actual=>actual.SettledBasis,ProjectedFuturesMarginState projected=>projected.Basis,
            _=>throw Error("The future has no qualified actual or path-local settlement basis.")};
        var fact=context.Fix(new(SettlementProjection,
            selected.Phase==FuturesMarginPhase.Final?ObservationRole.FinalContractSettlement:ObservationRole.DailySettlement,selected.At));
        var(_,cash,_)=FuturesMargin.Difference(basis,fact.Price,holding.Quantity,policy.Rounding,policy.RoundingScope);
        var projectedState=new ProjectedFuturesMarginState(trade.Id,holding.Id,trade.BookId,holding.Quantity,market.Series.Key,fact.Price,selected.At,
            selected.Phase==FuturesMarginPhase.Final,source.Knowledge!.ContextId,source.Knowledge.KnownAt);
        var projectedSetup=setup with{BindingStates=setup.BindingStates.SetItem(new(trade.Id,holding.Id),projectedState)};
        return state with{Residuals=[ValuationPortfolio.FromTrades([new(trade.Id,trade.BookId,[holding],trade.Booking)],projectedSetup)],
            Cash=cash.Amount==default?state.Cash:state.Cash.Add(new(occurrence.Id,occurrence.At,selected.DueAt,ExactMoney.FromMoney(cash)))};
    }
    private static MarketDataException Error(string message)=>new("projection_futures_state",message);
}
