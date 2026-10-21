using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record QuoteBumpRequest(Guid SeriesId,string? Quote,string? Size,string? Convention);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record QuoteShiftRequest(Guid SeriesId,string? Quote,string? Size,string? Convention);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesRiskCatalogRequest(FuturesReadRequest Context,ImmutableArray<Guid> BookIds,ImmutableArray<Guid> PortfolioIds);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesRiskRequest(FuturesReadRequest Context,ImmutableArray<Guid> BookIds,ImmutableArray<Guid> PortfolioIds,
    ImmutableArray<QuoteBumpRequest> Bumps,string? Convention,string? NumericalPolicy,string? Detail=null,
    ImmutableArray<QuoteShiftRequest> Stress=default);

public sealed class FuturesRiskPreview(FuturesStore store)
{
    private static object PriceUnitData(PriceUnit unit)=>new{unit.Currency,unit.Quantity,multiplier=unit.Multiplier.ToString()};
    private static string Unit(PreviewData data,PriceUnit unit)
    {
        var quantity=data.References["quantity_specifications"].Single(q=>q.Text("code")==unit.Quantity.Code);
        var symbol=data.References["units"].Single(u=>u.Text("code")==quantity.Text("unit_code")).Text("symbol");
        return Presentation.PriceUnitLabel.Format(unit,Currency(data,unit.Currency),symbol);
    }
    private static string Currency(PreviewData data,ReferenceKey currency)=>data.References["currencies"].Single(c=>c.Text("code")==currency.Code).Text("display_code");
    private static object Factor(PreviewData data,QuoteFactor factor)
    {
        var contract=data.Contracts.Single(c=>c.Key==factor.Contract);
        return new{instrumentId=contract.FuturesSeries is {} series?(Guid?)ContractIdentity.Futures(series):null,seriesId=factor.Input.Series.Id,quote=factor.Quote.Code,unit=Unit(data,factor.Unit),priceUnit=PriceUnitData(factor.Unit),
            curves=factor.Curves.Select(c=>c.Code),contractId=contract.Key.Id,deliveryFrom=contract.DeliveryFrom.ToString("yyyy-MM-dd"),
            deliveryTo=contract.DeliveryTo.ToString("yyyy-MM-dd"),value=factor.BaseValue.ToString(),observation=factor.Observation,
            source=factor.Source,role=factor.Role.ToString()};
    }
    public async Task<object> Catalog(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesRiskCatalogRequest body,CancellationToken ct)
    {
        var snapshot=data.Snapshot(frame);var books=await store.Books(connection,ct);
        var resolved=await store.ReadTrades(connection,data,books,body.BookIds,body.PortfolioIds,ct);
        var sets=resolved.Trades.IsEmpty?[]:FuturesRisk.Factors(resolved.Trades,snapshot,data.Model,FuturesRisk.Implementation,ct);
        var inputs=sets.SelectMany(s=>s.Factors).Select(f=>f.Input).ToHashSet();
        var available=data.Model.QuoteFactors(snapshot);
        return new{factors=available.Where(f=>inputs.Contains(f.Input)).Select(f=>Factor(data,f)),
            books=sets.Select(s=>new{s.BookId,factors=s.Factors.Select(f=>f.Input.Series.Id)}),
            availableFactors=available.Select(f=>Factor(data,f)),bumpConvention=QuoteBump.Convention,stressConvention=QuoteShift.Convention,
            convention=FuturesRisk.Implementation,numericalPolicy=RiskEngine.NumericalPolicy,measure="execution_pnl"};
    }
    public async Task<object> Calculate(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesRiskRequest body,CancellationToken ct)
        =>Calculate(await Prepare(connection,data,frame,body,ct),ct);

    internal sealed record Prepared(PreviewData Data,InputFrame Frame,FuturesRiskRequest Request,RiskDetail Detail,
        MarketSnapshot Snapshot,ImmutableArray<QuoteFactor> Available,ImmutableArray<QuoteBump> Bumps,
        ImmutableArray<QuoteShift> Stress,ImmutableArray<System.Text.Json.JsonElement> Books,FuturesStore.ResolvedTrades Resolved);

    internal async Task<Prepared> Prepare(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesRiskRequest body,CancellationToken ct)
    {
        var detail=body.Detail switch{null or "holdings"=>RiskDetail.Holdings,"books"=>RiskDetail.Books,_=>throw new MarketDataException("risk_detail","Choose holdings or books for the result detail.")};
        if(body.Convention!=FuturesRisk.Implementation || body.NumericalPolicy!=RiskEngine.NumericalPolicy ||
            body.Bumps.IsDefaultOrEmpty || body.Bumps.Length>(detail==RiskDetail.Books?4096:16) || body.Bumps.Any(b=>b is null))
            throw new MarketDataException("risk_convention","Select an explicit supported risk convention, numerical policy and bounded quote bumps (up to 4096 for book totals or 16 for holding details).");
        var snapshot=data.Snapshot(frame);var available=data.Model.QuoteFactors(snapshot);
        var bumps=body.Bumps.Select(b=>
        {
            var factor=available.SingleOrDefault(f=>f.Input.Series.Id==b.SeriesId && f.Quote.Code==b.Quote)
                ??throw new MarketDataException("risk_factor","Choose an exact available live quote input in this market context.");
            var size=ExactDecimal.Parse(b.Size??"");
            if(b.Convention!=QuoteBump.Convention||size.CompareTo(default)<=0)
                throw new MarketDataException("risk_bump","Choose the explicit symmetric convention and a positive absolute bump.");
            return new QuoteBump(factor.Input,factor.Quote,factor.Unit,size,b.Convention);
        }).ToImmutableArray();
        if(!body.Stress.IsDefault&&(body.Stress.Length>16||body.Stress.Any(s=>s is null)))
            throw new MarketDataException("risk_stress","Select at most sixteen explicit parent-relative quote shifts.");
        var stress=(body.Stress.IsDefault?ImmutableArray<QuoteShiftRequest>.Empty:body.Stress).Select(s=>
        {
            var factor=available.SingleOrDefault(f=>f.Input.Series.Id==s.SeriesId&&f.Quote.Code==s.Quote)
                ??throw new MarketDataException("risk_factor","Choose an exact available live quote input in this market context.");
            var size=ExactDecimal.Parse(s.Size??"");
            if(s.Convention!=QuoteShift.Convention||size==default)
                throw new MarketDataException("risk_stress","Select the explicit additive parent-relative convention and a nonzero signed shift.");
            return new QuoteShift(factor.Input,factor.Quote,factor.Unit,size,s.Convention);
        }).ToImmutableArray();
        var books=await store.Books(connection,ct);
        var resolved=await store.ReadTrades(connection,data,books,body.BookIds,body.PortfolioIds,ct);
        if(resolved.Trades.IsEmpty)throw new MarketDataException("risk_position","Select live holdings before running risk.");
        return new(data,frame,body,detail,snapshot,available,bumps,stress,books,resolved);
    }

    internal static object Calculate(Prepared prepared,CancellationToken ct)
    {
        var (data,frame,body,detail,snapshot,available,bumps,stress,books,resolved)=prepared;
        var byInstrument=resolved.MarketBindings.Values.DistinctBy(s=>s.Instrument.Id).ToDictionary(s=>s.Instrument.Id);
        var run=FuturesRisk.Calculate(resolved.Trades,snapshot,data.Model,bumps,body.Convention!,body.NumericalPolicy!,ct,detail,stress);
        string Book(Guid id)=>books.Single(b=>b.Id("id")==id).Text("name");
        object Values(Money b,Money u,Money d,Money up,Money down,RiskDerivative delta,RiskDerivative gamma)=>new{
            @base=b.Amount.ToString(),up=u.Amount.ToString(),down=d.Amount.ToString(),upPnl=up.Amount.ToString(),downPnl=down.Amount.ToString(),
            delta=delta.Value.ToString(),gamma=gamma.Value.ToString(),currency=Currency(data,b.Currency),currencyReference=b.Currency,
            deltaUnit=$"{Currency(data,b.Currency)} / ({Unit(data,delta.Unit.Factor)})",gammaUnit=$"{Currency(data,b.Currency)} / ({Unit(data,gamma.Unit.Factor)})²"};
        return new{measure="execution_pnl",detail=run.Detail.ToString(),convention=FuturesRisk.Implementation,run.NumericalPolicy,run.Work,run.SnapshotId,
            scenario=new{run.Scenario.Id,run.Scenario.ParentId,run.Scenario.Depth,run.Scenario.IsBase,
                convention=QuoteShift.Convention,stress=body.Stress.IsDefault?ImmutableArray<QuoteShiftRequest>.Empty:body.Stress},
            heldContracts=resolved.MarketBindings.Values.Select(s=>s.Instrument.SeriesKey).Distinct().Count(),
            inputs=new{snapshot.Definitions,snapshot.ValuationAt,snapshot.MarketCutoff,curveNumericalPolicy=CurveModel.NumericalPolicy,
                selectionPolicy=snapshot.SelectionPolicy.ToString(),observations=snapshot.Selections.Select(s=>new{s.Input,s.Observation.Key,s.Observation.Series,
                    s.Observation.Source,role=s.Observation.Role.ToString(),state=s.Observation.State.ToString(),s.Observation.ObservedAt,value=s.Observation.Value.ToString()}),
                positions=resolved.Rows.Select(r=>new{booking=TradeParties.State(r.Id("catalog_id"),r),tradeId=r.Id("trade_id"),tradeReference=TradingReference.Trade(r),instrumentReference=TradingReference.Instrument(r),bindingId=r.Id("binding_id"),bookId=r.Id("book_id"),instrumentId=r.Id("instrument_id"),pricingBindingId=r.Id("side_id"),
                    eventId=r.Id("event_id"),eventSchema=r.Text("event_schema"),revision=r.Text("revision"),revisionEffectiveAt=r.Text("revision_effective_at"),lots=r.Text("lots"),executionPrice=r.Text("execution_price"),executedAt=r.Text("effective_at"),settlement=r.Text("settlement_state"),
                    tradePublication=r.Text("trade_publication"),bindingPublication=r.Text("binding_publication")}),
                marketBindings=resolved.MarketBindings.Values.Select(s=>new{s.Id,s.Market,contract=s.Contract.Key,series=s.Series.Key,
                    s.Contract.DeliveryFrom,s.Contract.DeliveryTo,s.Series.Location,s.Series.DeliveryProfile,
                    contractSize=s.LotSize.Amount.ToString(),nominalSize=s.Series.ContractSize.ToString(),lotFactor=s.Series.LotResolution?.Factor.ToString(),lotResolution=s.Series.LotResolution?.Implementation,lotQuantity=s.Series.LotQuantity,lotBasis=s.Series.LotBasis.ToString(),
                    quote=s.Quote.Key,priceUnit=PriceUnitData(PriceUnit.FromQuote(s.Quote)),tradingTick=s.Quote.TradingTick.ToString(),
                    clearingIncrement=s.Quote.ClearingIncrement.ToString(),minimumPrice=s.Quote.Minimum.ToString(),maximumPrice=s.Quote.Maximum.ToString(),
                    s.SettlementMethod,firstTrading=s.Series.FirstTradingAt,s.EligibleFrom,s.Contract.Expiry,convention=s.Convention})},
            modelContracts=run.Contracts.Select(c=>new{c.Id,c.ModelVersion,c.Dependencies,c.Quantity,c.Currency}),
            factors=run.Factors.Select(f=>new{factor=Factor(data,available.Single(a=>a.Input==f.Bump.Input)),bump=f.Bump.Size.ToString(),bumpConvention=f.Bump.ConventionId,
                f.UpWork,f.DownWork,totals=f.Totals.Select(t=>new{bookId=t.BookId,book=Book(t.BookId),values=Values(t.Base,t.Up,t.Down,t.UpPnl,t.DownPnl,t.Delta,t.Gamma)}),
                positions=f.Positions.Select(p=>new{p.TradeId,p.BindingId,p.BookId,book=Book(p.BookId),p.ValuationId,
                    contractId=byInstrument[p.ValuationId].Contract.Key.Id,curve=byInstrument[p.ValuationId].Market.Curve.Code,
                    deliveryFrom=byInstrument[p.ValuationId].Contract.DeliveryFrom.ToString("yyyy-MM-dd"),deliveryTo=byInstrument[p.ValuationId].Contract.DeliveryTo.ToString("yyyy-MM-dd"),
                    values=Values(p.Base,p.Up,p.Down,p.UpPnl,p.DownPnl,p.Delta,p.Gamma)})})};
    }
}
