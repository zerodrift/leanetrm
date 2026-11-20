using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;


namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StructuredPowerHedgeRequest(
    [property:JsonRequired] FuturesReadRequest Context,
    [property:JsonRequired] ImmutableArray<Guid> BookIds,
    [property:JsonRequired] Guid OperatingTradeId,
    [property:JsonRequired] ImmutableArray<Guid> HedgeTradeIds,
    [property:JsonRequired] ImmutableArray<CurveBumpRequest> Bumps,
    [property:JsonRequired] string Convention,
    [property:JsonRequired] string NumericalPolicy,
    [property:JsonRequired] PortfolioValuationPolicy PricingPolicy,
    [property:JsonRequired] string AggregationPolicy);

public sealed record PowerHedgeBaseValue(string Role,string Measure,ReferenceKey Currency,string Value);
public sealed record PowerHedgeChanges(string UpPnl,string DownPnl,string Delta,string Gamma);
public sealed record PowerHedgeFactor(string Factor,ReferenceKey Currency,DerivativeUnit DeltaUnit,DerivativeUnit GammaUnit,
    PowerHedgeChanges Operating,PowerHedgeChanges Hedges,PowerHedgeChanges Net);
public sealed record PowerHedgeResult(string Convention,string AggregationPolicy,Guid OperatingTradeId,
    ImmutableArray<Guid> HedgeTradeIds,ImmutableArray<PowerHedgeBaseValue> BaseValues,
    ImmutableArray<PowerHedgeFactor> PriceChanges,PortfolioValuationResult Valuation);

// Reporting over the ordinary portfolio calculation. Every position is repriced
// under the same immutable factor scenarios before signed price changes are added.
public static class StructuredPowerHedgeReport
{
    public const string Convention="operating_right_price_hedge_v1";
    public const string AggregationPolicy="shared_price_changes_v1";

    public static PowerHedgeResult Project(PortfolioValuationResult run,Guid operatingTradeId,
        ImmutableArray<Guid> hedgeTradeIds,string aggregationPolicy)
    {
        var ids=Selection(operatingTradeId,hedgeTradeIds,aggregationPolicy);
        if(run is null||run.Detail!=RiskDetail.Holdings||run.Positions.IsDefaultOrEmpty||run.BasePositions.IsDefault||
            run.HoldingRisk.IsDefault||run.Factors.IsDefaultOrEmpty||
            !run.Positions.Select(p=>p.TradeId).ToHashSet().SetEquals(ids)||
            run.Positions.Any(p=>p.Owner.Kind!=AccountingOwnerKind.Trade||p.Owner.TradeId!=p.TradeId||
                (p.TradeId==operatingTradeId?p.InstrumentType!="operating_right":p.InstrumentType is not ("future" or "financial_swap" or "heat_rate"))))
            throw Invalid();
        var positions=run.Positions.ToArray();
        var keys=positions.Select(p=>(p.Measure,p.TradeId,p.BindingId,p.BookId,p.ValuationId)).ToHashSet();
        var baseKeys=run.BasePositions.Select(p=>(p.Measure,p.Position.TradeId,p.Position.BindingId,p.Position.BookId,p.Position.ValuationId)).ToArray();
        if(keys.Count!=positions.Length||baseKeys.Distinct().Count()!=baseKeys.Length||!keys.SetEquals(baseKeys)||
            run.Factors.Select(f=>f.Axis.Id).Distinct().Count()!=run.Factors.Length)
            throw Invalid();
        var bases=run.BasePositions.ToDictionary(p=>(p.Measure,p.Position.TradeId,p.Position.BindingId,p.Position.BookId,p.Position.ValuationId));
        var expected=keys.SelectMany(k=>run.Factors.Select(f=>(k,f.Axis.Id))).ToHashSet();
        var actual=run.HoldingRisk.Select(p=>((p.Measure,p.Position.TradeId,p.Position.BindingId,p.Position.BookId,p.Position.ValuationId),p.Factor)).ToArray();
        if(actual.Distinct().Count()!=actual.Length||!expected.SetEquals(actual))throw Invalid();
        foreach(var row in run.BasePositions)
            if(row.Position.Owner.Kind!=AccountingOwnerKind.Trade||row.Position.Owner.TradeId!=row.Position.TradeId)throw Invalid();
        foreach(var row in run.HoldingRisk)
        {
            var p=row.Position;var v=p.Values;var factor=run.Factors.Single(f=>f.Axis.Id==row.Factor).Axis;
            var basis=bases[(row.Measure,p.TradeId,p.BindingId,p.BookId,p.ValuationId)].Position.Value;
            if(p.Owner.Kind!=AccountingOwnerKind.Trade||p.Owner.TradeId!=p.TradeId||v.Base!=basis||
                v.Up.Currency!=basis.Currency||v.Down.Currency!=basis.Currency||v.UpPnl.Currency!=basis.Currency||v.DownPnl.Currency!=basis.Currency||
                v.Delta.Value.Currency!=basis.Currency||v.Gamma.Value.Currency!=basis.Currency||
                v.Delta.Unit!=new DerivativeUnit(basis.Currency,factor.Unit,1)||v.Gamma.Unit!=new DerivativeUnit(basis.Currency,factor.Unit,2))
                throw Invalid();
        }
        string Role(Guid id)=>id==operatingTradeId?"operating":"hedges";
        var baseValues=run.BasePositions.GroupBy(p=>(Role:Role(p.Position.TradeId),p.Measure,p.Position.Value.Currency))
            .OrderBy(g=>g.Key.Role,StringComparer.Ordinal).ThenBy(g=>g.Key.Measure,StringComparer.Ordinal)
            .ThenBy(g=>g.Key.Currency.Code,StringComparer.Ordinal).ThenBy(g=>g.Key.Currency.Revision.RevisionId)
            .Select(g=>new PowerHedgeBaseValue(g.Key.Role,g.Key.Measure,g.Key.Currency,Sum(g.Select(p=>p.Position.Value),g.Key.Currency).ToExactString())).ToImmutableArray();
        var changes=ImmutableArray.CreateBuilder<PowerHedgeFactor>();
        foreach(var factor in run.Factors)
        foreach(var currency in run.BasePositions.Select(p=>p.Position.Value.Currency).Distinct()
            .OrderBy(c=>c.Code,StringComparer.Ordinal).ThenBy(c=>c.Revision.RevisionId))
        {
            var rows=run.HoldingRisk.Where(p=>p.Factor==factor.Axis.Id&&p.Position.Values.Base.Currency==currency).ToArray();
            changes.Add(new(factor.Axis.Id,currency,new(currency,factor.Axis.Unit,1),new(currency,factor.Axis.Unit,2),
                Changes(rows.Where(p=>p.Position.TradeId==operatingTradeId),currency),
                Changes(rows.Where(p=>p.Position.TradeId!=operatingTradeId),currency),Changes(rows,currency)));
        }
        return new(Convention,aggregationPolicy,operatingTradeId,hedgeTradeIds,baseValues,changes.ToImmutable(),run);
    }

    internal static ImmutableArray<Guid> Selection(Guid operatingTradeId,ImmutableArray<Guid> hedgeTradeIds,string aggregationPolicy)
    {
        if(aggregationPolicy!=AggregationPolicy||operatingTradeId==Guid.Empty||hedgeTradeIds.IsDefaultOrEmpty||
            hedgeTradeIds.Length>=RiskTradeScope.MaxSelected||hedgeTradeIds.Any(id=>id==Guid.Empty||id==operatingTradeId)||
            hedgeTradeIds.Distinct().Count()!=hedgeTradeIds.Length)throw Invalid();
        return [operatingTradeId,..hedgeTradeIds];
    }
    private static ExactMoney Sum(IEnumerable<ExactMoney> values,ReferenceKey currency)
        =>values.Aggregate(ExactMoney.Parse("0",currency),(sum,value)=>sum.Add(value));
    private static PowerHedgeChanges Changes(IEnumerable<PortfolioHoldingRisk> rows,ReferenceKey currency)
    {
        var values=rows.Select(r=>r.Position.Values).ToArray();
        return new(Sum(values.Select(v=>v.UpPnl),currency).ToExactString(),Sum(values.Select(v=>v.DownPnl),currency).ToExactString(),
            Sum(values.Select(v=>v.Delta.Value),currency).ToExactString(),Sum(values.Select(v=>v.Gamma.Value),currency).ToExactString());
    }
    private static MarketDataException Invalid()=>new("power_hedge_contract",
        "Select one operating-right trade, explicit financial hedges and complete holding results under shared factor scenarios and exact currency references.");
}

internal sealed class StructuredPowerHedgeService(PreviewDatabase database)
{
    internal async Task<PowerHedgeResult> Calculate(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        StructuredPowerHedgeRequest body,CancellationToken ct)
    {
        var ids=StructuredPowerHedgeReport.Selection(body.OperatingTradeId,body.HedgeTradeIds,body.AggregationPolicy);
        if(body.Context is null||body.Convention!=StructuredPowerHedgeReport.Convention||
            body.NumericalPolicy!=RiskEngine.ExactNumericalPolicy||body.BookIds.IsDefaultOrEmpty||
            body.Bumps.IsDefaultOrEmpty||body.Bumps.Length>PortfolioRiskPreview.MaxHoldingsBumps||body.Bumps.Any(b=>b is null)||frame.ContextId is not {} contextId)
            throw new MarketDataException("power_hedge_contract","Select explicit books, the shared certified context, supported numerical policy and bounded factor bumps.");
        PortfolioValuation.ValidatePolicy(body.PricingPolicy);
        var books=BookScope.Select(await BookScope.Read(connection,database.Manifest.CatalogId,ct),body.BookIds,[])
            .Select(b=>b.Id("id")).Order().ToImmutableArray();
        var selection=await RiskTradeScope.Resolve(connection,database.Manifest.CatalogId,books,ids,ct);
        var context=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(connection,contextId,ct)).Context;
        // A hedge comparison requires all selected positions to price. It never
        // publishes a net result after silently excluding a failed holding.
        var prepared=await new PortfolioValuation(database).Prepare(connection,data,frame,data.Snapshot(frame),context,books,
            body.PricingPolicy,[],[],ct,selection:selection);
        var bumps=body.Bumps.Select(b=>
        {
            if(b.Factor is null||b.Convention!=ConnectedRiskBump.Convention)
                throw new MarketDataException("risk_bump","Select the explicit symmetric shared-factor bump convention.");
            var size=ExactDecimal.Parse(b.Size??"");
            if(size.CompareTo(default)<=0)throw new MarketDataException("risk_bump","Select a positive absolute factor bump.");
            return new PortfolioCurveBump(CurveRiskAxes.Resolve(prepared,b.Factor,ct),size);
        }).ToImmutableArray();
        return StructuredPowerHedgeReport.Project(PortfolioValuation.Calculate(prepared,bumps,[],RiskDetail.Holdings,ct),
            body.OperatingTradeId,body.HedgeTradeIds,body.AggregationPolicy);
    }
}
