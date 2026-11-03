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
    string? Detail=null,ImmutableArray<CurveShiftRequest> Stress=default,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] ImmutableArray<Guid>? TradeIds=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] PortfolioJointRiskRequest? Joint=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] PortfolioParametricRequest? Parametric=null);

// Transport and presentation over the shared instrument valuation operation.
// Instrument materialization and pricing remain outside this report adapter.
public sealed partial class PortfolioRiskPreview(PreviewDatabase database)
{
    public const string Convention="portfolio_instrument_repricing_v1";
    // The most bumps one request takes: a holdings-detail result prices every position under each bump, a books-detail result keeps only book totals.
    public const int MaxHoldingsBumps=16,MaxBooksBumps=4096,MaxStress=16;
    public static object Limits=>new{maxHoldingsBumps=MaxHoldingsBumps,maxBooksBumps=MaxBooksBumps,maxStress=MaxStress,maxParts=RiskJobRun.MaxParts};
    private static readonly AmountFormat Display=new(12,CashTieRule.ToEven,false,true);
    private static PortfolioValuationPolicy PricingPolicy=>new(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,
        FuturesRisk.Implementation,SwapStore.Convention,RateConventions.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,"estimate_until_actual_v1",
        FuturesOptionValuationPolicy.Release1);
    internal sealed record Prepared(PortfolioRiskRequest Request,RiskDetail Detail,
        ImmutableArray<Guid> BookIds,ImmutableArray<JsonElement> Books,PortfolioValuation.Prepared Portfolio,
        ImmutableArray<PortfolioCurveBump> Bumps,ImmutableArray<PortfolioCurveShift> Stress,
        ImmutableArray<(PreviewData Data,InputFrame Frame)> SupplementalMarkets)
    {internal PortfolioJointRisk.Resolved? Joint{get;init;} internal AlignedFactorHistory? History{get;init;}}

    public async Task<object> Catalog(NpgsqlConnection connection,PreviewData data,InputFrame frame,PortfolioRiskCatalogRequest body,CancellationToken ct)
    {
        var (portfolio,_,_)=await PreparePortfolio(connection,data,frame,body.BookIds,body.PortfolioIds,PricingPolicy,ct,tradeIds:body.TradeIds);
        var markets=portfolio.Markets.Select(m=>m.Data).ToImmutableArray();
        var available=portfolio.Markets.SelectMany(m=>m.Data.Model.QuoteFactors(m.Snapshot)).ToImmutableArray();
        var axes=CurveRiskAxes.Catalog(portfolio,ct);
        var held=CurveRiskAxes.Held(portfolio,axes,ct);
        var targets=axes.Select(a=>Target(markets,a)).Concat(axes.Where(a=>a.Target is ParallelCurveTarget).Select(a=>
            (object)new{id=a.Id+":bucket",label=a.Label.Replace("all retained contracts","delivery bucket",StringComparison.Ordinal)
                    .Replace("all retained outputs","delivery bucket",StringComparison.Ordinal),
                coordinate=CurveRiskAxes.Coordinate(a.Coordinate),kind="delivery_bucket",unit=Unit(markets,a.Unit),priceUnit=PriceUnitReference(a.Unit),
                factor=new CurveRiskAxisRequest(CurveRiskAxes.Coordinate(a.Coordinate),new("delivery_bucket",a.Target.Curve,
                    Convention:DeliveryBucketCurveTarget.WholeDelivery))})).ToArray();
        return new{factors=portfolio.Factors.Select(f=>Factor(markets,CurveRiskAxes.Quote(portfolio,f,ct))),
            availableFactors=available.Select(f=>Factor(markets,CurveRiskAxes.Quote(portfolio,f,ct))),scenarioTargets=targets,
            heldContractTargets=held.Contracts,heldCurveTargets=held.Curves,
            heldContractsWithoutTarget=held.ContractsWithoutTarget,heldCurvesWithoutTarget=held.CurvesWithoutTarget,
            books=portfolio.Positions.GroupBy(p=>p.BookId).Select(g=>new{bookId=g.Key}),excludedPositions=portfolio.Excluded,
            measures=portfolio.Positions.Select(p=>p.Measure).Distinct().Order().Select(m=>new{measure=m,measureLabel=MeasureLabel(m)}),
            bumpConvention=ConnectedRiskBump.Convention,stressConvention=CurveShift.ParentAdditive,convention=Convention,
            numericalPolicy=RiskEngine.ExactNumericalPolicy,pricingPolicy=PricingPolicy,
            history=HistoryCapabilities(portfolio),limits=Limits,
            parametric=ParametricCapabilities(),
            joint=new{convention=PortfolioJointRiskRequest.Version1,scenarioConvention=ConnectedRiskScenarioSet.Independent,bumpConvention=ConnectedRiskBump.Convention,shiftConvention=CurveShift.ParentAdditive,
                derivativeConvention=ConnectedFiniteDifferenceRequest.HalfNominalDouble,maxScenarios=PortfolioJointRisk.MaxScenarios,
                maxShifts=PortfolioJointRisk.MaxShifts,maxAxes=RiskEngine.MaxSensitivityAxes,maxMixed=RiskEngine.MaxMixedSensitivities,
                maxResultValues=RiskEngine.MaxJointResultValues,maxOptionTreeNodes=PortfolioJointCalculation.MaximumOptionTreeNodes}};
    }

    // The held targets a preset shocks in the books (blotter-desk.md, Risk report inline), as the catalog offers them and in its
    // order: each with its exact price unit and that unit's label.
    internal async Task<ImmutableArray<(PortfolioRiskAxis Axis,string Unit)>> Held(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        ImmutableArray<Guid> bookIds,bool curves,CancellationToken ct)
    {
        var (portfolio,_,_)=await PreparePortfolio(connection,data,frame,bookIds,[],PricingPolicy,ct);
        var axes=CurveRiskAxes.Catalog(portfolio,ct);var held=CurveRiskAxes.Held(portfolio,axes,ct);
        var markets=portfolio.Markets.Select(m=>m.Data).ToImmutableArray();
        var chosen=(curves?held.Curves:held.Contracts).ToHashSet(StringComparer.Ordinal);
        return [..axes.Where(a=>chosen.Contains(a.Id)).Select(a=>(a,Unit(markets,a.Unit)))];
    }
    // A request for the books that bumps each factor under the conventions and policies the catalog states.
    internal static PortfolioRiskRequest Request(FuturesReadRequest context,ImmutableArray<Guid> bookIds,IEnumerable<(CurveRiskAxisRequest Factor,ExactDecimal Size)> bumps)=>
        new(context,bookIds,[],[..bumps.Select(b=>new CurveBumpRequest(b.Factor,b.Size.ToString(),ConnectedRiskBump.Convention))],
            Convention,RiskEngine.ExactNumericalPolicy,PricingPolicy,Stress:[]);

    // A queued run retains its connected graphs and the positions its submission left out (D220), and
    // prepares exactly the positions its submission prepared.
    internal readonly record struct RetainedRun(ImmutableArray<ConnectedMarketEvidence> Graphs,ImmutableArray<ExcludedPosition> Excluded);
    private async Task<(PortfolioValuation.Prepared Portfolio,ImmutableArray<Guid> BookIds,ImmutableArray<JsonElement> Books)> PreparePortfolio(
        NpgsqlConnection connection,PreviewData data,InputFrame frame,ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,
        PortfolioValuationPolicy policy,CancellationToken ct,ImmutableArray<(PreviewData Data,InputFrame Frame)> supplemental=default,RetainedRun? retained=null,
        ImmutableArray<Guid>? tradeIds=null)
    {
        PortfolioValuation.ValidatePolicy(policy);
        if(frame.ContextId is not {} contextId||bookIds.IsDefault||portfolioIds.IsDefault)
            throw new MarketDataException("risk_context","Select the shared financial context and explicit book and portfolio scopes.");
        var context=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,contextId,ct)).Context;
        var books=await BookScope.Read(connection,database.Manifest.CatalogId,ct);
        var selected=BookScope.Select(books,bookIds,portfolioIds).Select(b=>b.Id("id")).Order().ToImmutableArray();
        if(selected.IsEmpty)throw new MarketDataException("risk_position","Select visible books before running risk.");
        var trades=await RiskTradeScope.Resolve(connection,database.Manifest.CatalogId,selected,tradeIds,ct);
        var valuation=new PortfolioValuation(database);var snapshot=data.Snapshot(frame);
        var portfolio=retained is {} run?
            await valuation.Prepare(connection,data,frame,snapshot,context,selected,policy,supplemental,run.Excluded,ct,run.Graphs,selection:trades):
            await valuation.PrepareValued(connection,data,frame,snapshot,context,selected,policy,supplemental,ct,trades);
        return (portfolio,selected,books);
    }

    public async Task<object> Calculate(NpgsqlConnection connection,PreviewData data,InputFrame frame,PortfolioRiskRequest body,CancellationToken ct)
        =>Calculate(await Prepare(connection,data,frame,body,ct),ct);

    internal async Task<Prepared> Prepare(NpgsqlConnection connection,PreviewData data,InputFrame frame,PortfolioRiskRequest body,CancellationToken ct,
        ImmutableArray<(PreviewData Data,InputFrame Frame)> supplemental=default,RetainedRun? retained=null,
        RetainedFactorHistory? historyPlan=null,ImmutableArray<RiskHistoryObservation> historyEvidence=default)
    {
        var detail=body.Detail switch{null or "holdings"=>RiskDetail.Holdings,"books"=>RiskDetail.Books,
            _=>throw new MarketDataException("risk_detail","Choose holdings or books for the result detail.")};
        if(body.Joint is {} jointRequest)PortfolioJointRisk.Validate(jointRequest);
        if(body.Parametric is {} statistical)ValidateParametric(body,statistical);
        if(body.Convention!=Convention||body.NumericalPolicy!=RiskEngine.ExactNumericalPolicy||body.Bumps.IsDefault||
            (body.Joint is null?body.Parametric is null&&body.Bumps.IsEmpty||body.Bumps.Length>(detail==RiskDetail.Books?MaxBooksBumps:MaxHoldingsBumps):!body.Bumps.IsEmpty)||body.Bumps.Any(b=>b is null))
            throw new MarketDataException("risk_convention","Select the supported portfolio convention, exact numerical policy and bounded explicit curve-coordinate bumps.");
        if(!body.Stress.IsDefault&&(body.Stress.Length>MaxStress||body.Stress.Any(s=>s is null)))
            throw new MarketDataException("risk_stress","Select at most sixteen explicit parent-relative curve shifts.");
        var (portfolio,bookIds,books)=await PreparePortfolio(connection,data,frame,body.BookIds,body.PortfolioIds,body.PricingPolicy,ct,supplemental,retained,body.TradeIds);
        AlignedFactorHistory? history=null;
        if(body.Parametric is {} parametric)
        {
            if(!BookScope.Select(books,parametric.History.Scope.BookIds,parametric.History.Scope.PortfolioIds).Select(b=>b.Id("id")).Order().SequenceEqual(bookIds))
                throw new MarketDataException("risk_parametric_scope","History and current holdings must use exactly the same book scope.");
            history=historyPlan is null?await BuildHistory(connection,portfolio,frame,parametric.History,ct,statistical:true):
                await ReplayHistory(connection,portfolio,frame,parametric.History,historyPlan,historyEvidence,ct);
            var selected=ParametricBumps(portfolio,history,parametric.FiniteDifference,ct);
            if(!body.Bumps.IsEmpty&&JsonSerializer.Serialize(body.Bumps,RiskJobs.Json)!=JsonSerializer.Serialize(selected,RiskJobs.Json))
                throw new MarketDataException("risk_parametric_delta","A parametric request must shock its complete captured dated-factor universe in native coordinates.");
            body=body with{Bumps=selected};
        }
        else if(historyPlan is not null)throw new MarketDataException("risk_parametric_history","Only a statistical job may carry a retained calibration history.");
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
        var joint=body.Joint is null?null:PortfolioJointRisk.Resolve(portfolio,body.Joint,ct);
        if(body.Parametric is not null)RequireParametricWork(portfolio,bumps);
        else if(joint is null)PortfolioValuation.RequireWork(portfolio,bumps.Length);
        else PortfolioJointCalculation.RequireWork(portfolio,joint,detail);
        var canonical=body with{
            Bumps=bumps.Select(b=>new CurveBumpRequest(b.Axis.Request,b.Size.ToString(),ConnectedRiskBump.Convention)).ToImmutableArray(),
            Stress=stress.Select(s=>new CurveShiftRequest(s.Axis.Request,s.Size.ToString(),CurveShift.ParentAdditive)).ToImmutableArray(),Joint=joint?.Request};
        return new(canonical,detail,bookIds,books,portfolio,bumps,stress,
            portfolio.Markets.Where(m=>m.Data.DefinitionRevision!=data.DefinitionRevision).Select(m=>(m.Data,m.Frame)).ToImmutableArray()){Joint=joint,History=history};
    }

    internal static object Calculate(Prepared prepared,CancellationToken ct)
    {
        if(prepared.Request.Parametric is not null)return CalculateParametric(prepared,ct);
        if(prepared.Joint is not null)return CalculateJoint(prepared,ct);
        var trees=prepared.Portfolio.Options?.TreePrices??0;
        var run=PortfolioValuation.Calculate(prepared.Portfolio,prepared.Bumps,prepared.Stress,prepared.Detail,ct);
        return PresentSensitivity(prepared,run,trees);
    }
    private static object PresentSensitivity(Prepared prepared,PortfolioValuationResult run,int trees)
    {
        var optionWork=prepared.Portfolio.OptionWork is {} work?work with{ScenarioTreePrices=prepared.Portfolio.Options!.TreePrices-trees}:null;
        var markets=prepared.Portfolio.Markets.Select(m=>m.Data).ToImmutableArray();
        var positions=run.Positions.ToDictionary(p=>(p.TradeId,p.BindingId));
        string Book(Guid id)=>prepared.Books.Single(b=>b.Id("id")==id).Text("name");
        object Amounts(ExactDealRiskValues value,Func<ExactMoney,string> format)=>new{
            @base=format(value.Base),up=format(value.Up),down=format(value.Down),upPnl=format(value.UpPnl),downPnl=format(value.DownPnl),
            delta=format(value.Delta.Value),gamma=format(value.Gamma.Value)};
        object Values(ExactDealRiskValues v)=>new{
            @base=v.Base.ToExactString(),up=v.Up.ToExactString(),down=v.Down.ToExactString(),upPnl=v.UpPnl.ToExactString(),downPnl=v.DownPnl.ToExactString(),
            delta=v.Delta.Value.ToExactString(),gamma=v.Gamma.Value.ToExactString(),display=Amounts(v,Displayed),
            currency=Currency(markets,v.Base.Currency),currencyReference=v.Base.Currency,
            deltaUnit=$"{Currency(markets,v.Base.Currency)} / ({Unit(markets,v.Delta.Unit.Factor)})",
            gammaUnit=$"{Currency(markets,v.Base.Currency)} / ({Unit(markets,v.Gamma.Unit.Factor)})²"};
        return new{detail=run.Detail.ToString(),tradeIds=prepared.Request.TradeIds,convention=Convention,numericalPolicy=RiskEngine.ExactNumericalPolicy,
            engineVersion=RiskJobs.EngineVersion,displayPolicy=new{implementation="risk_decimal_display_v1",decimalPlaces=12,tieRule="to_even"},
            run.Work,run.SnapshotId,run.ConnectedScenarios,scenario=Scenario(prepared),heldInstruments=run.Positions.Select(p=>p.InstrumentId).Distinct().Count(),
            excludedPositions=prepared.Portfolio.Excluded,
            // Options: each series' calibration receipt, the policy that holds it in every scenario, and how many held positions are in the intrinsic mode.
            scenarioPolicy=prepared.Portfolio.Options is null?null:prepared.Portfolio.Policy.FuturesOptions!.ScenarioPolicy,
            optionCalibrations=prepared.Portfolio.OptionCalibrations,optionWork,
            fallbackPositions=prepared.Portfolio.OptionModes.Values.Count(mode=>mode==OpenEtrm.MarketData.Options.ImpliedVolatility.FallbackMode),
            projectionPolicies=ProjectionPolicyReceipts.For(run.ModelContracts),
            ppaMeasures=run.PpaMeasures,inputs=new{pricingPolicy=run.Policy,ppaReceipt=run.PpaReceipt is {} receipt?PpaPortfolio.Receipt(receipt):null,markets=prepared.Portfolio.Markets.Select(m=>new{definitions=m.Snapshot.Definitions,
                snapshotId=m.Snapshot.Id,m.Snapshot.ValuationAt,m.Snapshot.MarketCutoff,curveNumericalPolicy=CurveModel.NumericalPolicy,
                selectionPolicy=m.Snapshot.SelectionPolicy.ToString(),observations=m.Snapshot.Selections.Select(s=>new{s.Input,s.Observation.Key,
                    s.Observation.Series,s.Observation.Source,role=s.Observation.Role.ToString(),state=s.Observation.State.ToString(),
                    s.Observation.ObservedAt,value=s.Observation.Value.ToString()})}),positions=run.Positions,run.DiscountInputs,connectedMarkets=prepared.Portfolio.ConnectedMarkets},
            modelContracts=run.ModelContracts.Select(c=>new{c.Id,c.ModelVersion,c.Dependencies,c.Quantity,c.Currency,c.ScheduleIdentity,c.SettlementProjections}),
            factors=prepared.Bumps.Select(b=>new{
                factor=Factor(markets,run.Factors.Single(f=>f.Axis.Id==b.Axis.Id).Axis),
                bump=b.Size.ToString(),bumpConvention=ConnectedRiskBump.Convention,
                totals=run.Risk.Where(r=>r.Factor==b.Axis.Id).Select(r=>new{r.Measure,measureLabel=MeasureLabel(r.Measure),r.BookId,book=Book(r.BookId),
                    values=Values(new(run.Totals.Single(t=>t.Measure==r.Measure&&t.BookId==r.BookId&&t.Value.Currency==r.Currency).Value,
                        r.Up,r.Down,r.UpPnl,r.DownPnl,r.Delta,r.Gamma))}),
                positions=run.HoldingRisk.Where(r=>r.Factor==b.Axis.Id).Select(r=>
                {
                    var p=r.Position;var evidence=positions[(p.TradeId,p.BindingId)];
                    var position=JsonSerializer.SerializeToNode(new{p.TradeId,p.BindingId,p.BookId,book=Book(p.BookId),p.ValuationId,evidence.TradeReference,
                        evidence.InstrumentReference,evidence.InstrumentType,r.Measure,measureLabel=MeasureLabel(r.Measure),values=Values(p.Values)},RiskJobs.Json)!.AsObject();
                    // An option position states the calibration mode it was valued in; no other position has one.
                    if(prepared.Portfolio.OptionModes.TryGetValue((p.TradeId,p.BindingId),out var mode))position["calibrationMode"]=mode;
                    return position;
                })})};
    }

    private static string Displayed(ExactMoney value)=>Display.FormatFraction(BigInteger.Parse(value.Numerator,CultureInfo.InvariantCulture),BigInteger.Parse(value.Denominator,CultureInfo.InvariantCulture));
    private static string MeasureLabel(string measure)=>measure switch{
        PortfolioValuation.FuturesMeasure=>"Futures execution P&L",PortfolioValuation.SwapMeasure=>"Present value",PortfolioValuation.PpaMeasure=>"Value excluding overdue cash",
        CrudePositions.Measure=>"Crude contractual PV",LngPositions.Measure=>"LNG contractual PV",FuturesOptionPositions.Measure=>FuturesOptionPositions.MeasureLabel,
        GasPositions.Measure=>"Gas contractual PV",
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
        unit=Unit(markets,axis.Unit),priceUnit=PriceUnitReference(axis.Unit),factor=axis.Request};
    // The exact price unit a size is saved against, in the shape a definition's sizes name it.
    private static object PriceUnitReference(PriceUnit unit)=>new{unit.Currency,unit.Quantity,multiplier=unit.Multiplier.ToString()};
    private static object Factor(ImmutableArray<PreviewData> markets,PortfolioRiskAxis axis)
    {
        var quote=axis.Quote;
        var contract=quote is null?null:markets.SelectMany(m=>m.Contracts).Single(c=>c.Key==quote.Contract);
        return new{axis.Id,axis.Label,coordinate=CurveRiskAxes.Coordinate(axis.Coordinate),kind=axis.Request.Target.Kind,
            factor=axis.Request,seriesId=quote?.Input.Series.Id,quote=quote?.Quote.Code,unit=Unit(markets,axis.Unit),
            priceUnit=PriceUnitReference(axis.Unit),curves=new[]{axis.Target.Curve.Code},
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
