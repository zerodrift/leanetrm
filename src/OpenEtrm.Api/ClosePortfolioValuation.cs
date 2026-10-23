using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

// Existing-close jobs retain their native quote receipt and strict single-bundle
// admission. Interactive connected portfolio scenarios have a separate adapter;
// both execute the shared Instrument → Side → Schedule valuation engine.
public sealed record CloseQuoteFactor(QuoteFactor Factor,ExactDecimal BumpSize);
public sealed record CloseFactorRisk(string Measure,Guid BookId,ReferenceKey Currency,InputKey Factor,
    ExactMoney Up,ExactMoney Down,ExactMoney UpPnl,ExactMoney DownPnl,ExactRiskDerivative Delta,ExactRiskDerivative Gamma);
public sealed record ClosePortfolioValuationResult(Guid SnapshotId,ScheduleContext Context,PortfolioValuationPolicy Policy,
    ImmutableArray<UnitPricingContract> ModelContracts,ImmutableArray<PortfolioPositionEvidence> Positions,
    ImmutableArray<CloseQuoteFactor> Factors,ImmutableArray<PortfolioTotal> Totals,
    ImmutableArray<CloseFactorRisk> Risk,RiskWork Work)
{
    public ImmutableArray<PortfolioDiscountEvidence> DiscountInputs{get;init;}=[];
    public ValuationReceipt? PpaReceipt{get;init;}
    public ImmutableArray<BookValueMeasures> PpaMeasures{get;init;}=[];
}

public sealed record ClosePortfolioPreparation(PreparedPortfolioRisk Prepared,Guid SnapshotId,ScheduleContext Context,PortfolioValuationPolicy Policy,
    ImmutableArray<string> Measures,ImmutableArray<PortfolioPositionEvidence> Positions,ImmutableArray<CloseQuoteFactor> Factors,
    ImmutableArray<PortfolioDiscountEvidence> Discounts,ValuationReceipt? PpaReceipt,long DeclaredRows,long DeclaredRowScenarios)
{
    internal ImmutableDictionary<Guid,ISideDiscounting> PpaDiscounts{get;init;}=ImmutableDictionary<Guid,ISideDiscounting>.Empty;
}

public sealed class ClosePortfolioValuation(PreviewDatabase database)
{
    public const string FuturesMeasure="futures_execution_pnl";
    public const string SwapMeasure="swap_present_value";
    public const string PpaMeasure="ppa_value_excluding_overdue";
    public static void ValidatePolicy(PortfolioValuationPolicy policy)=>PortfolioValuation.ValidatePolicy(policy);
    public async Task<ClosePortfolioValuationResult> Calculate(NpgsqlConnection connection,PreviewData data,MarketSnapshot snapshot,
        ScheduleContext context,ImmutableArray<Guid> bookIds,PortfolioValuationPolicy policy,CancellationToken ct)
    {
        var prepared=await Prepare(connection,data,snapshot,context,bookIds,policy,ct);
        var result=Evaluate(prepared,ct);
        await using var guard=ScheduleStore.Command(connection,"SELECT security.require_bound_market_context($1,$2,$3)",database.Manifest.CatalogId,context.ContextId!.Value,snapshot.ValuationAt);
        await guard.ExecuteNonQueryAsync(ct);
        return result;
    }
    public async Task<ClosePortfolioPreparation> Prepare(NpgsqlConnection connection,PreviewData data,MarketSnapshot snapshot,
        ScheduleContext context,ImmutableArray<Guid> bookIds,PortfolioValuationPolicy policy,CancellationToken ct,
        CloseBookRiskDefinition? ppaPolicy=null,InputFrame? retainedFrame=null)
    {
        ValidatePolicy(policy);ct.ThrowIfCancellationRequested();
        var exact=PinnedContext.Exact(context);
        await new KnowledgeStore(database.Manifest.CatalogId).Retained(connection,exact,ct);
        await using(var guard=ScheduleStore.Command(connection,"SELECT security.require_bound_market_context($1,$2,$3)",database.Manifest.CatalogId,exact.ContextId!.Value,snapshot.ValuationAt))
            await guard.ExecuteNonQueryAsync(ct);
        if(bookIds.IsDefaultOrEmpty||bookIds.Length>128||snapshot.Definitions!=data.DefinitionRevision||
            DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime)!=PublishedSchedules.ContextDate(exact))
            throw new MarketDataException("portfolio_context","Select explicit books and a snapshot of the exact model and certified effective date.");
        var frame=retainedFrame??new InputFrame(snapshot.Id,"Portfolio inputs",snapshot.ValuationAt,
            snapshot.Selections.Select(s=>s.Observation.Key.Id).ToImmutableArray(),ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,
            exact.ContextId,snapshot.Selections.Where(s=>s.Input.Use==InputUse.FrozenFact).Select(s=>s.Observation.Key.Id).ToImmutableArray());
        if(!data.Snapshot(frame).HasSameInputs(snapshot))
            throw new MarketDataException("portfolio_context","Every selected observation and use must match the exact authorized snapshot inputs.");
        // This consumer has no PPA valuation adapter. Trusted complete-scope
        // admission checks every retained head and its current reference access
        // before refusing; RLS-hidden positions cannot become an empty subset.
        if(ppaPolicy is null)await using(var ppa=ScheduleStore.Command(connection,
            "SELECT count(*) FROM security.ppa_scope_heads($1,'books',$2::uuid[])",database.Manifest.CatalogId,bookIds.ToArray()))
            if((long)(await ppa.ExecuteScalarAsync(ct))!>0)
                throw new MarketDataException("portfolio_ppa_unsupported","The complete selected books contain PPA positions outside this portfolio consumer's supported products.");
        if(ppaPolicy is not null)
        {
            await using var scope=ScheduleStore.Command(connection,"SELECT automation.close_risk_scope($1,$2,$3,$4::uuid[])::text",data.DefinitionRevision.CatalogId,exact.ContextId!.Value,snapshot.ValuationAt,bookIds.ToArray());
            using var scoped=JsonDocument.Parse((string)(await scope.ExecuteScalarAsync(ct))!);
            var incompatible=scoped.RootElement.EnumerateArray().Where(r=>r.OptionalId("definition_revision_id")!=data.DefinitionRevision.RevisionId||
                r.OptionalId("reference_catalog_id")!=data.ReferenceRevision.CatalogId||r.OptionalId("reference_revision_id")!=data.ReferenceRevision.RevisionId||
                r.OptionalText("implementation") is null).ToArray();
            if(incompatible.Length>0)throw CloseRiskInputsException.For(incompatible,"exact_bundle_whole_operation_v1");
        }
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
        PpaPortfolioInput? ppaInput=null;
        if(ppaPolicy is not null)
        {
            ppaInput=await PpaPortfolio.Prepare(connection,data,frame,context,bookIds,ppaPolicy,ct);
            if(ppaInput is not null){portfolios.Add(ppaInput.Portfolio);measures.Add(PpaMeasure);evidence.AddRange(ppaInput.Evidence);}
        }
        if(portfolios.Count==0)throw new MarketDataException("risk_position","The selected books have no visible supported holdings.");
        var prepared=RiskEngine.Prepare(data.Model,snapshot,portfolios.ToImmutable(),ct);
        var factors=prepared.Factors.Select(f=>new CloseQuoteFactor(f,f.TradingTick*ExactDecimal.Parse(policy.BumpTicks.ToString(CultureInfo.InvariantCulture)))).ToImmutableArray();
        var admissionWork=RiskEngine.IntervalWork(prepared);
        CloseRiskAdmission.RequireRowScenarios(admissionWork.RowScenarios,factors.Length);
        return new(prepared,snapshot.Id,exact,policy,measures.ToImmutableArray(),evidence.ToImmutable(),factors,
            swaps.DistinctBy(h=>h.Instrument.Id).SelectMany(h=>((CurrencyDefaultDiscounting)h.Discounting).Inputs
                .OrderBy(p=>p.Key.SideId).ThenBy(p=>p.Key.Ordinal).Select(p=>new PortfolioDiscountEvidence(h.Instrument.Id,p.Key,p.Value))).ToImmutableArray(),
            ppaInput?.Receipt,admissionWork.Rows,admissionWork.RowScenarios){PpaDiscounts=ppaInput?.Portfolio.Setup.InstrumentDiscounting??ImmutableDictionary<Guid,ISideDiscounting>.Empty};
    }
    public static ClosePortfolioValuationResult Evaluate(ClosePortfolioPreparation prepared,CancellationToken ct)
    {
        var bumps=prepared.Factors.Select(f=>new QuoteBump(f.Factor.Input,f.Factor.Quote,f.Factor.Unit,f.BumpSize,prepared.Policy.BumpConvention)).ToImmutableArray();
        var run=RiskEngine.CalculateExact(prepared.Prepared,bumps,prepared.Policy.RiskNumericalPolicy,ct);
        var totals=run.Groups.SelectMany((group,n)=>group.BaseValues.Select(v=>new PortfolioTotal(prepared.Measures[n],v.BookId,v.Value))).ToImmutableArray();
        var risk=run.Groups.SelectMany((group,n)=>group.Factors.SelectMany(f=>f.Totals.Select(t=>new CloseFactorRisk(prepared.Measures[n],t.BookId,t.Values.Base.Currency,
            f.Bump.Input,t.Values.Up,t.Values.Down,t.Values.UpPnl,t.Values.DownPnl,t.Values.Delta,t.Values.Gamma)))).ToImmutableArray();
        return new(prepared.SnapshotId,prepared.Context,prepared.Policy,run.Groups.SelectMany(g=>g.Contracts).GroupBy(c=>c.Id).Select(g=>g.First() with {Dependencies=g.SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray()}).ToImmutableArray(),prepared.Positions,prepared.Factors,totals,risk,run.Work)
        {
            DiscountInputs=prepared.Discounts.AddRange(PpaReads.DiscountInputs(prepared.PpaDiscounts)),PpaReceipt=prepared.PpaReceipt,
            PpaMeasures=run.Groups.Where((g,n)=>prepared.Measures[n]==PpaMeasure).SelectMany(g=>g.BaseMeasures).ToImmutableArray()
        };
    }
    private static PortfolioPositionEvidence Evidence(JsonElement row,RiskPosition position,string measure,string publication)=>new(
        position.TradeId,int.Parse(row.Text("revision"),CultureInfo.InvariantCulture),position.BindingId,row.Id("instrument_id"),position.ValuationId,
        position.BookId,measure,row.Id("definition_revision_id"),int.Parse(row.Text(publication),CultureInfo.InvariantCulture),position.Quantity);
}
