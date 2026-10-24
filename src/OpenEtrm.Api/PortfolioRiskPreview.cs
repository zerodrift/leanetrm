using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioRiskRequest(FuturesReadRequest Context,ImmutableArray<Guid> BookIds,ImmutableArray<Guid> PortfolioIds,
    ImmutableArray<CurveBumpRequest> Bumps,string? Convention,string? NumericalPolicy,PortfolioValuationPolicy PricingPolicy,
    string? Detail=null,ImmutableArray<CurveShiftRequest> Stress=default);

// Transport and presentation over the shared instrument valuation operation.
// Instrument materialization and pricing remain outside this report adapter.
public sealed class PortfolioRiskPreview(PreviewDatabase database)
{
    public const string Convention="portfolio_instrument_repricing_v1";
    private static readonly AmountFormat Display=new(12,CashTieRule.ToEven,false,true);
    private static PortfolioValuationPolicy PricingPolicy=>new(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,
        FuturesRisk.Implementation,SwapStore.Convention,RateConventions.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1);
    internal sealed record Prepared(PortfolioRiskRequest Request,RiskDetail Detail,
        ImmutableArray<Guid> BookIds,ImmutableArray<JsonElement> Books,PortfolioValuation.Prepared Portfolio,
        ImmutableArray<PortfolioCurveBump> Bumps,ImmutableArray<PortfolioCurveShift> Stress,
        ImmutableArray<(PreviewData Data,InputFrame Frame)> SupplementalMarkets);

    public async Task<object> Catalog(NpgsqlConnection connection,PreviewData data,InputFrame frame,PortfolioRiskCatalogRequest body,CancellationToken ct)
    {
        var (portfolio,_,_)=await PreparePortfolio(connection,data,frame,body.BookIds,body.PortfolioIds,PricingPolicy,ct);
        var markets=portfolio.Markets.Select(m=>m.Data).ToImmutableArray();
        var available=portfolio.Markets.SelectMany(m=>m.Data.Model.QuoteFactors(m.Snapshot)).ToImmutableArray();
        var axes=CurveRiskAxes.Catalog(portfolio,ct);
        var targets=axes.Select(a=>Target(markets,a)).Concat(axes.Where(a=>a.Target is ParallelCurveTarget).Select(a=>
            (object)new{id=a.Id+":bucket",label=a.Label.Replace("all retained contracts","delivery bucket",StringComparison.Ordinal)
                    .Replace("all retained outputs","delivery bucket",StringComparison.Ordinal),
                coordinate=CurveRiskAxes.Coordinate(a.Coordinate),kind="delivery_bucket",unit=Unit(markets,a.Unit),
                factor=new CurveRiskAxisRequest(CurveRiskAxes.Coordinate(a.Coordinate),new("delivery_bucket",a.Target.Curve,
                    Convention:DeliveryBucketCurveTarget.WholeDelivery))})).ToArray();
        return new{factors=portfolio.Factors.Select(f=>Factor(markets,CurveRiskAxes.Quote(portfolio,f,ct))),
            availableFactors=available.Select(f=>Factor(markets,CurveRiskAxes.Quote(portfolio,f,ct))),scenarioTargets=targets,
            books=portfolio.Positions.GroupBy(p=>p.BookId).Select(g=>new{bookId=g.Key}),excludedPositions=portfolio.Excluded,
            measures=portfolio.Positions.Select(p=>p.Measure).Distinct().Order().Select(m=>new{measure=m,measureLabel=MeasureLabel(m)}),
            bumpConvention=ConnectedRiskBump.Convention,stressConvention=CurveShift.ParentAdditive,convention=Convention,
            numericalPolicy=RiskEngine.ExactNumericalPolicy,pricingPolicy=PricingPolicy};
    }

    // A queued run retains its connected graphs and the positions its submission left out (D217), and
    // prepares exactly the positions its submission prepared.
    internal readonly record struct RetainedRun(ImmutableArray<ConnectedMarketEvidence> Graphs,ImmutableArray<ExcludedPosition> Excluded);
    private async Task<(PortfolioValuation.Prepared Portfolio,ImmutableArray<Guid> BookIds,ImmutableArray<JsonElement> Books)> PreparePortfolio(
        NpgsqlConnection connection,PreviewData data,InputFrame frame,ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,
        PortfolioValuationPolicy policy,CancellationToken ct,ImmutableArray<(PreviewData Data,InputFrame Frame)> supplemental=default,RetainedRun? retained=null)
    {
        PortfolioValuation.ValidatePolicy(policy);
        if(frame.ContextId is not {} contextId||bookIds.IsDefault||portfolioIds.IsDefault)
            throw new MarketDataException("risk_context","Select the shared financial context and explicit book and portfolio scopes.");
        var context=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,contextId,ct)).Context;
        var books=await new FuturesStore(database.Manifest).Books(connection,ct);
        FuturesStore.ValidateScope(books,bookIds,portfolioIds);
        var selected=books.Where(b=>(bookIds.IsEmpty||bookIds.Contains(b.Id("id")))&&
            (portfolioIds.IsEmpty||portfolioIds.Contains(b.Id("portfolio_id")))).Select(b=>b.Id("id")).Order().ToImmutableArray();
        if(selected.IsEmpty)throw new MarketDataException("risk_position","Select visible books before running risk.");
        var valuation=new PortfolioValuation(database);var snapshot=data.Snapshot(frame);
        var portfolio=retained is {} run?
            await valuation.Prepare(connection,data,frame,snapshot,context,selected,policy,supplemental,run.Excluded,ct,run.Graphs):
            await valuation.PrepareValued(connection,data,frame,snapshot,context,selected,policy,supplemental,ct);
        return (portfolio,selected,books);
    }

    public async Task<object> Calculate(NpgsqlConnection connection,PreviewData data,InputFrame frame,PortfolioRiskRequest body,CancellationToken ct)
        =>Calculate(await Prepare(connection,data,frame,body,ct),ct);

    internal async Task<Prepared> Prepare(NpgsqlConnection connection,PreviewData data,InputFrame frame,PortfolioRiskRequest body,CancellationToken ct,
        ImmutableArray<(PreviewData Data,InputFrame Frame)> supplemental=default,RetainedRun? retained=null)
    {
        var detail=body.Detail switch{null or "holdings"=>RiskDetail.Holdings,"books"=>RiskDetail.Books,
            _=>throw new MarketDataException("risk_detail","Choose holdings or books for the result detail.")};
        if(body.Convention!=Convention||body.NumericalPolicy!=RiskEngine.ExactNumericalPolicy||body.Bumps.IsDefaultOrEmpty||
            body.Bumps.Length>(detail==RiskDetail.Books?4096:16)||body.Bumps.Any(b=>b is null))
            throw new MarketDataException("risk_convention","Select the supported portfolio convention, exact numerical policy and bounded explicit curve-coordinate bumps.");
        if(!body.Stress.IsDefault&&(body.Stress.Length>16||body.Stress.Any(s=>s is null)))
            throw new MarketDataException("risk_stress","Select at most sixteen explicit parent-relative curve shifts.");
        var (portfolio,bookIds,books)=await PreparePortfolio(connection,data,frame,body.BookIds,body.PortfolioIds,body.PricingPolicy,ct,supplemental,retained);
        var bumps=body.Bumps.Select(b=>
        {
            var axis=CurveRiskAxes.Resolve(portfolio,b.Factor,ct);var size=ExactDecimal.Parse(b.Size??"");
            if(b.Convention!=ConnectedRiskBump.Convention||size.CompareTo(default)<=0)
                throw new MarketDataException("risk_bump","Choose the explicit symmetric convention and a positive absolute bump.");
            return new PortfolioCurveBump(axis,size);
        }).ToImmutableArray();
        if(bumps.Select(b=>b.Axis.Id).Distinct().Count()!=bumps.Length)
            throw new MarketDataException("risk_bump","Select each exact factor once per risk run.");
        var stress=(body.Stress.IsDefault?ImmutableArray<CurveShiftRequest>.Empty:body.Stress).Select(s=>
        {
            var axis=CurveRiskAxes.Resolve(portfolio,s.Factor,ct);var size=ExactDecimal.Parse(s.Size??"");
            if(s.Convention!=CurveShift.ParentAdditive||size==default)
                throw new MarketDataException("risk_stress","Choose the explicit additive convention and a nonzero signed shift.");
            return new PortfolioCurveShift(axis,size);
        }).ToImmutableArray();
        var canonical=body with{
            Bumps=bumps.Select(b=>new CurveBumpRequest(b.Axis.Request,b.Size.ToString(),ConnectedRiskBump.Convention)).ToImmutableArray(),
            Stress=stress.Select(s=>new CurveShiftRequest(s.Axis.Request,s.Size.ToString(),CurveShift.ParentAdditive)).ToImmutableArray()};
        return new(canonical,detail,bookIds,books,portfolio,bumps,stress,
            portfolio.Markets.Where(m=>m.Data.DefinitionRevision!=data.DefinitionRevision).Select(m=>(m.Data,m.Frame)).ToImmutableArray());
    }

    internal static object Calculate(Prepared prepared,CancellationToken ct)
    {
        var run=PortfolioValuation.Calculate(prepared.Portfolio,prepared.Bumps,prepared.Stress,prepared.Detail,ct);
        var markets=prepared.Portfolio.Markets.Select(m=>m.Data).ToImmutableArray();
        var positions=run.Positions.ToDictionary(p=>(p.TradeId,p.BindingId));
        string Book(Guid id)=>prepared.Books.Single(b=>b.Id("id")==id).Text("name");
        object Amounts(ExactDealRiskValues value,Func<ExactMoney,string> format)=>new{
            @base=format(value.Base),up=format(value.Up),down=format(value.Down),upPnl=format(value.UpPnl),downPnl=format(value.DownPnl),
            delta=format(value.Delta.Value),gamma=format(value.Gamma.Value)};
        object Values(ExactDealRiskValues v)=>new{
            @base=Exact(v.Base),up=Exact(v.Up),down=Exact(v.Down),upPnl=Exact(v.UpPnl),downPnl=Exact(v.DownPnl),
            delta=Exact(v.Delta.Value),gamma=Exact(v.Gamma.Value),display=Amounts(v,Displayed),
            currency=Currency(markets,v.Base.Currency),currencyReference=v.Base.Currency,
            deltaUnit=$"{Currency(markets,v.Base.Currency)} / ({Unit(markets,v.Delta.Unit.Factor)})",
            gammaUnit=$"{Currency(markets,v.Base.Currency)} / ({Unit(markets,v.Gamma.Unit.Factor)})²"};
        return new{detail=run.Detail.ToString(),convention=Convention,numericalPolicy=RiskEngine.ExactNumericalPolicy,
            engineVersion=RiskJobs.EngineVersion,displayPolicy=new{implementation="risk_decimal_display_v1",decimalPlaces=12,tieRule="to_even"},
            run.Work,run.SnapshotId,run.ConnectedScenarios,scenario=Scenario(prepared),heldInstruments=run.Positions.Select(p=>p.InstrumentId).Distinct().Count(),
            excludedPositions=prepared.Portfolio.Excluded,
            inputs=new{pricingPolicy=run.Policy,markets=prepared.Portfolio.Markets.Select(m=>new{definitions=m.Snapshot.Definitions,
                snapshotId=m.Snapshot.Id,m.Snapshot.ValuationAt,m.Snapshot.MarketCutoff,curveNumericalPolicy=CurveModel.NumericalPolicy,
                selectionPolicy=m.Snapshot.SelectionPolicy.ToString(),observations=m.Snapshot.Selections.Select(s=>new{s.Input,s.Observation.Key,
                    s.Observation.Series,s.Observation.Source,role=s.Observation.Role.ToString(),state=s.Observation.State.ToString(),
                    s.Observation.ObservedAt,value=s.Observation.Value.ToString()})}),positions=run.Positions,run.DiscountInputs,connectedMarkets=prepared.Portfolio.ConnectedMarkets},
            modelContracts=run.ModelContracts.Select(c=>new{c.Id,c.ModelVersion,c.Dependencies,c.Quantity,c.Currency}),
            factors=prepared.Bumps.Select(b=>new{
                factor=Factor(markets,run.Factors.Single(f=>f.Axis.Id==b.Axis.Id).Axis),
                bump=b.Size.ToString(),bumpConvention=ConnectedRiskBump.Convention,
                totals=run.Risk.Where(r=>r.Factor==b.Axis.Id).Select(r=>new{r.Measure,measureLabel=MeasureLabel(r.Measure),r.BookId,book=Book(r.BookId),
                    values=Values(new(run.Totals.Single(t=>t.Measure==r.Measure&&t.BookId==r.BookId&&t.Value.Currency==r.Currency).Value,
                        r.Up,r.Down,r.UpPnl,r.DownPnl,r.Delta,r.Gamma))}),
                positions=run.HoldingRisk.Where(r=>r.Factor==b.Axis.Id).Select(r=>
                {
                    var p=r.Position;var evidence=positions[(p.TradeId,p.BindingId)];
                    return new{p.TradeId,p.BindingId,p.BookId,book=Book(p.BookId),p.ValuationId,evidence.TradeReference,evidence.InstrumentReference,
                        evidence.InstrumentType,r.Measure,measureLabel=MeasureLabel(r.Measure),values=Values(p.Values)};
                })})};
    }

    private static string Exact(ExactMoney value)
    {
        try{return value.ToMoney().Amount.ToString();}
        catch(MarketDataException e) when(e.Code is "precision_loss" or "numeric_range"){return value.Numerator+"/"+value.Denominator;}
    }
    private static string Displayed(ExactMoney value)=>Display.FormatFraction(BigInteger.Parse(value.Numerator,CultureInfo.InvariantCulture),BigInteger.Parse(value.Denominator,CultureInfo.InvariantCulture));
    private static string MeasureLabel(string measure)=>measure switch{
        PortfolioValuation.FuturesMeasure=>"Futures execution P&L",PortfolioValuation.SwapMeasure=>"Present value",
        _=>throw new MarketDataException("valuation_measure","The report requires an explicit valuation measure label.")};
    private static string Currency(ImmutableArray<PreviewData> markets,ReferenceKey key)=>Reference(markets,"currencies",key).Text("display_code");
    private static JsonElement Reference(ImmutableArray<PreviewData> markets,string table,ReferenceKey key)=>
        markets.First(m=>m.ReferenceRevision==key.Revision).References[table].Single(r=>r.Text("code")==key.Code);
    private static string Unit(ImmutableArray<PreviewData> markets,PriceUnit unit)
    {
        var quantity=Reference(markets,"quantity_specifications",unit.Quantity);
        var symbol=markets.First(m=>m.ReferenceRevision==unit.Quantity.Revision).References["units"].Single(u=>u.Text("code")==quantity.Text("unit_code")).Text("symbol");
        return Presentation.PriceUnitLabel.Format(unit,Currency(markets,unit.Currency),symbol);
    }
    private static object Target(ImmutableArray<PreviewData> markets,PortfolioRiskAxis axis)=>new{
        axis.Id,axis.Label,coordinate=CurveRiskAxes.Coordinate(axis.Coordinate),kind=axis.Request.Target.Kind,
        unit=Unit(markets,axis.Unit),factor=axis.Request};
    private static object Factor(ImmutableArray<PreviewData> markets,PortfolioRiskAxis axis)
    {
        var quote=axis.Quote;
        var contract=quote is null?null:markets.SelectMany(m=>m.Contracts).Single(c=>c.Key==quote.Contract);
        return new{axis.Id,axis.Label,coordinate=CurveRiskAxes.Coordinate(axis.Coordinate),kind=axis.Request.Target.Kind,
            factor=axis.Request,seriesId=quote?.Input.Series.Id,quote=quote?.Quote.Code,unit=Unit(markets,axis.Unit),
            priceUnit=new{axis.Unit.Currency,axis.Unit.Quantity,multiplier=axis.Unit.Multiplier.ToString()},curves=new[]{axis.Target.Curve.Code},
            contractId=contract?.Key.Id,deliveryFrom=contract?.DeliveryFrom.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),deliveryTo=contract?.DeliveryTo.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            value=quote?.BaseValue.ToString(),observation=quote?.Observation,source=quote?.Source,role=quote?.Role.ToString()};
    }
    private static object Scenario(Prepared prepared)
    {
        string Digest(object value)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,RiskJobs.Json)));
        var id=Digest(new{version="portfolio_curve_scenario_v1",markets=prepared.Portfolio.Markets.OrderBy(m=>m.Data.DefinitionRevision.RevisionId)
            .Select(m=>new{m.Data.DefinitionRevision,Id=m.ScenarioId})});
        string? parent=null;
        foreach(var shift in prepared.Stress){parent=id;id=Digest(new{version="portfolio_curve_scenario_v1",parent,shift});}
        return new{id,parentId=parent,depth=prepared.Stress.Length,isBase=prepared.Stress.IsEmpty,
            convention=CurveShift.ParentAdditive,stress=prepared.Request.Stress.IsDefault?ImmutableArray<CurveShiftRequest>.Empty:prepared.Request.Stress};
    }
}
