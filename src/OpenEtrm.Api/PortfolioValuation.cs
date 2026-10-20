using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
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
    Guid ValuationId,Guid BookId,string Measure,Guid DefinitionRevisionId,int PublicationOrder,Quantity Quantity);
public sealed record PortfolioFactor(QuoteFactor Factor,ExactDecimal BumpSize);
public sealed record PortfolioTotal(string Measure,Guid BookId,ExactMoney Value);
public sealed record PortfolioFactorRisk(string Measure,Guid BookId,ReferenceKey Currency,InputKey Factor,
    ExactMoney Up,ExactMoney Down,ExactMoney UpPnl,ExactMoney DownPnl,ExactRiskDerivative Delta,ExactRiskDerivative Gamma);
public sealed record PortfolioDiscountEvidence(Guid InstrumentId,SidePaymentKey Payment,PaymentDiscount Input);
public sealed record PortfolioValuationResult(Guid SnapshotId,ScheduleContext Context,PortfolioValuationPolicy Policy,
    ImmutableArray<UnitPricingContract> ModelContracts,ImmutableArray<PortfolioPositionEvidence> Positions,
    ImmutableArray<PortfolioFactor> Factors,ImmutableArray<PortfolioTotal> Totals,
    ImmutableArray<PortfolioFactorRisk> Risk,RiskWork Work)
{
    public ImmutableArray<PortfolioDiscountEvidence> DiscountInputs{get;init;}=[];
}

public sealed class PortfolioValuation(PreviewDatabase database)
{
    public const string FuturesMeasure="futures_execution_pnl";
    public const string SwapMeasure="swap_present_value";
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
        ValidatePolicy(policy);ct.ThrowIfCancellationRequested();
        var exact=PinnedContext.Exact(context);
        await new KnowledgeStore(database.Manifest.CatalogId).Retained(connection,exact,ct);
        await using(var guard=ScheduleStore.Command(connection,"SELECT security.require_bound_market_context($1,$2,$3)",database.Manifest.CatalogId,exact.ContextId!.Value,snapshot.ValuationAt))
            await guard.ExecuteNonQueryAsync(ct);
        if(bookIds.IsDefaultOrEmpty||bookIds.Length>128||snapshot.Definitions!=data.DefinitionRevision||
            DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime)!=PublishedSchedules.ContextDate(exact))
            throw new MarketDataException("portfolio_context","Select explicit books and a snapshot of the exact model and certified effective date.");
        var frame=new InputFrame(snapshot.Id,"Portfolio inputs",snapshot.ValuationAt,
            snapshot.Selections.Select(s=>s.Observation.Key.Id).ToImmutableArray(),ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,
            exact.ContextId,snapshot.Selections.Where(s=>s.Input.Use==InputUse.FrozenFact).Select(s=>s.Observation.Key.Id).ToImmutableArray());
        if(!data.Snapshot(frame).HasSameInputs(snapshot))
            throw new MarketDataException("portfolio_context","Every selected observation and use must match the exact authorized snapshot inputs.");
        var futuresStore=new FuturesStore(database.Manifest);
        var books=await futuresStore.Books(connection,ct);
        var futures=await futuresStore.ReadTrades(connection,data,books,bookIds,[],ct);
        var swaps=await new SwapStore(database).ReadRiskHoldings(connection,data,frame,bookIds,ct,snapshot);
        var portfolios=ImmutableArray.CreateBuilder<ValuationPortfolio>();
        var measures=new List<string>();
        var evidence=ImmutableArray.CreateBuilder<PortfolioPositionEvidence>();
        if(!futures.Trades.IsEmpty)
        {
            var portfolio=FuturesRisk.CreatePortfolio(futures.Trades,snapshot,data.Model,policy.FuturesConvention,ct);
            portfolios.Add(portfolio);measures.Add(FuturesMeasure);
            var rows=futures.Rows.ToDictionary(r=>(r.Id("trade_id"),r.Id("binding_id")));
            foreach(var position in portfolio.Positions)
            {
                var row=rows[(position.TradeId,position.BindingId)];
                evidence.Add(Evidence(row,position, FuturesMeasure,"binding_publication"));
            }
        }
        if(!swaps.IsEmpty)
        {
            var instruments=swaps.DistinctBy(h=>h.Instrument.Id).ToImmutableArray();
            var positions=swaps.Select(h=>new RiskPosition(h.Row.Id("trade_id"),h.Row.Id("binding_id"),h.Row.Id("book_id"),h.Instrument.Id,
                h.Quantity,new(new Money(default,h.Instrument.Currency),h.Quantity.Specification),new(default,h.Instrument.Currency))).ToImmutableArray();
            portfolios.Add(new(instruments.Select(h=>(Instrument)h.Instrument).ToImmutableArray(),positions,
                new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
                {InstrumentDiscounting=instruments.ToImmutableDictionary(h=>h.Instrument.Id,h=>h.Discounting)}));
            measures.Add(SwapMeasure);
            var rows=swaps.ToDictionary(h=>h.Row.Id("trade_id"),h=>h.Row);
            foreach(var position in positions)
                evidence.Add(Evidence(rows[position.TradeId],position,SwapMeasure,"publication_order"));
        }
        if(portfolios.Count==0)throw new MarketDataException("risk_position","The selected books have no visible supported holdings.");
        var prepared=RiskEngine.Prepare(data.Model,snapshot,portfolios.ToImmutable(),ct);
        var factors=prepared.Factors.Select(f=>new PortfolioFactor(f,f.TradingTick*ExactDecimal.Parse(policy.BumpTicks.ToString(CultureInfo.InvariantCulture)))).ToImmutableArray();
        var bumps=factors.Select(f=>new QuoteBump(f.Factor.Input,f.Factor.Quote,f.Factor.Unit,f.BumpSize,policy.BumpConvention)).ToImmutableArray();
        var run=RiskEngine.CalculateExact(prepared,bumps,policy.RiskNumericalPolicy,ct);
        var totals=run.Groups.SelectMany((group,n)=>group.BaseValues.Select(v=>new PortfolioTotal(measures[n],v.BookId,v.Value))).ToImmutableArray();
        var risk=run.Groups.SelectMany((group,n)=>group.Factors.SelectMany(f=>f.Totals.Select(t=>new PortfolioFactorRisk(measures[n],t.BookId,t.Values.Base.Currency,
            f.Bump.Input,t.Values.Up,t.Values.Down,t.Values.UpPnl,t.Values.DownPnl,t.Values.Delta,t.Values.Gamma)))).ToImmutableArray();
        // A caller can still lose authority during calculation. Recheck before a
        // result may be persisted or returned; the worker adds its run fence.
        await using(var guard=ScheduleStore.Command(connection,"SELECT security.require_bound_market_context($1,$2,$3)",database.Manifest.CatalogId,exact.ContextId!.Value,snapshot.ValuationAt))
            await guard.ExecuteNonQueryAsync(ct);
        return new(snapshot.Id,exact,policy,run.Groups.SelectMany(g=>g.Contracts).ToImmutableArray(),evidence.ToImmutable(),factors,totals,risk,run.Work)
        {
            DiscountInputs=swaps.DistinctBy(h=>h.Instrument.Id).SelectMany(h=>((CurrencyDefaultDiscounting)h.Discounting).Inputs
                .OrderBy(p=>p.Key.SideId).ThenBy(p=>p.Key.Ordinal).Select(p=>new PortfolioDiscountEvidence(h.Instrument.Id,p.Key,p.Value))).ToImmutableArray()
        };
    }
    private static PortfolioPositionEvidence Evidence(JsonElement row,RiskPosition position,string measure,string publication)=>new(
        position.TradeId,int.Parse(row.Text("revision"),CultureInfo.InvariantCulture),position.BindingId,row.Id("instrument_id"),position.ValuationId,
        position.BookId,measure,row.Id("definition_revision_id"),int.Parse(row.Text(publication),CultureInfo.InvariantCulture),position.Quantity);
}
