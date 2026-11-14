using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioEconomicRiskRequest(PortfolioEconomicProjectionRequest Projection,LifecycleEconomicRiskPolicy Policy,
    [property:JsonRequired]PortfolioHistoryRequest? History,[property:JsonRequired]MonteCarloRiskPolicy? Simulation,
    [property:JsonRequired]ImmutableArray<CurveRiskAxisRequest> Factors);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioEconomicSourceRequest(PortfolioRiskCatalogRequest Scope,PortfolioValuationPolicy PricingPolicy,
    PortfolioEconomicProjectionRequest Projection);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioEconomicCashFundingRequest(PortfolioRiskCatalogRequest Scope,PortfolioValuationPolicy PricingPolicy,
    PortfolioEconomicProjectionRequest Projection);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioEconomicHistoryRequest(PortfolioEconomicSourceRequest Source,PortfolioHistoryRequest History);

public sealed partial class PortfolioRiskPreview
{
    public async Task<object> EconomicSetup(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        PortfolioRiskCatalogRequest request,CancellationToken ct)
    {
        if(frame.ContextId is not {} contextId||request.BookIds.IsDefault||request.PortfolioIds.IsDefault)
            throw new MarketDataException("risk_economic_scope","Select the shared opening source and explicit books.");
        var context=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,contextId,ct)).Context;
        var directory=await BookScope.Read(connection,database.Manifest.CatalogId,ct);
        var books=BookScope.Select(directory,request.BookIds,request.PortfolioIds).Select(b=>b.Id("id")).Order().ToImmutableArray();
        if(books.IsEmpty)throw new MarketDataException("risk_economic_scope","Select visible books before preparing lifecycle risk.");
        var sessions=await LifecyclePortfolioProjection.ReadSessions(connection,database,context,frame.At,ct);
        var gasChoices=request.TradeIds is null&&await PreviewDatabase.HasPermission(connection,"ReadGasOwnedHoldings",ct)?
            await GasOwnedProjection.ReadChoices(connection,database,frame,books,ct):ImmutableArray<GasOwnedProjectionAccountChoice>.Empty;
        var native=PricingPolicy with{FuturesConvention=FuturesMarginValuationPolicy.Convention,
            NativeFutures=new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version)};
        var pricing=new List<object>{new{value="native_trades",label="Native futures, options and swap holdings",policy=native}};
        if(request.TradeIds is null&&await PreviewDatabase.HasPermission(connection,"ReadGasOwnedHoldings",ct))
            pricing.Add(new{value="native_whole_books",label="All book holdings, including independently owned gas",policy=native with{
                GasOwned=new(GasOwnedValuationPolicy.Version,GasOwnedValuationPolicy.WholeBooks,GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version)}});
        object Choice(string value,string label)=>new{value,label};
        return new{context,asOf=frame.At,convention=Convention,numericalPolicy=RiskEngine.ExactNumericalPolicy,engineVersion=RiskJobs.EngineVersion,
            sessions=sessions.GetProperty("sessions"),pricing,gasAccounts=gasChoices.Select(a=>new{a.AccountId,a.Code,a.BookId,a.Book,a.EffectiveAt,
                profiles=a.Profiles.Select(p=>new{p.ProfileId,p.Revision,p.Implementation,p.QuantityDisposition,p.EffectiveAt,label=p.QuantityDisposition switch{
                    GasQuantityProjectionPolicy.Retain=>"Retain quantity obligations",
                    GasQuantityProjectionPolicy.AtDue=>"Perform configured contractual quantities when due",
                    _=>throw new MarketDataException("gas_projection_policy","This saved quantity profile has no qualified economic presentation contract.")}})}),
            projection=new{implementation=PortfolioEconomicProjectionRequest.Version2,cashFunding=true,horizonPolicy=PortfolioEconomicProjectionRequest.SourceKnownSchedule,
                rateTimingPolicy=RateDiscountTiming.Version,eventMarketPolicy=PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                policy=new{implementation=LifecycleProjectionPolicy.Version,numericalPolicy=LifecycleProjectionPolicy.Numerics,nonPriceInputs=LifecycleProjectionPolicy.HeldNonPrice},
                pricePaths=new[]{Choice(LifecycleProjectionPolicy.Hold,"Apply each market move, then hold prices"),Choice(LifecycleProjectionPolicy.Linear,"Move prices gradually until the next close")},
                cashDisposition=new[]{Choice(LifecycleProjectionPolicy.Outstanding,"Keep scheduled cash outstanding"),Choice(LifecycleProjectionPolicy.PayAtDue,"Assume scheduled cash is paid when due")},
                cashAccumulation=new[]{new{value=LifecycleProjectionPolicy.Nominal,label="Nominal cash amounts",requiresFunding=false},
                    new{value=LifecycleProjectionPolicy.Accumulated,label="Accumulate cash using selected captured currency curves",requiresFunding=true}},
                inputExpiry=new[]{Choice(LifecycleProjectionPolicy.DeclaredCurveExpiry,"Use each curve’s saved expiry treatment"),
                    Choice(LifecycleProjectionPolicy.FreezeNativeExpiry,"Hold selected native marks at expiry until final settlement")},
                physicalDelivery=new[]{Choice(LifecycleProjectionPolicy.RetainPhysical,"Keep new delivery obligations until observed performance")}},
            risk=new{quantilePolicy=HistoricalRiskPolicy.Quantile,tailPolicy=HistoricalRiskPolicy.Tail,
                methods=new[]{new{value=LifecycleEconomicRiskPolicy.Historical,label="Historical market moves",history=true,simulation=false},
                    new{value=LifecycleEconomicRiskPolicy.MonteCarlo,label="Monte Carlo market moves",history=true,simulation=true},
                    new{value=LifecycleEconomicRiskPolicy.Deterministic,label="No variable factors · exact time carry",history=false,simulation=false}},
                simulation=MonteCarloProfile(MonteCarloRiskPolicy.Economic,MonteCarloRiskPolicy.EconomicValuation,MonteCarloRiskPolicy.Uncertainty),
                drifts=new[]{Choice(ParametricRiskPolicy.Zero,"Zero mean market move"),Choice(ParametricRiskPolicy.Mean,"Historical sample mean")},
                limits=new{maxPaths=MonteCarloRisk.MaximumPaths,maxFactors=MonteCarloRisk.MaximumFactors,maxShockValues=MonteCarloRisk.MaximumShockValues}},
            scope=request with{Context=new(frame.Id,TradingBlotter.Convention,"retained",contextId),BookIds=books,PortfolioIds=[]}};
    }

    private async Task<(PreparedLifecyclePortfolio Economic,ImmutableArray<Guid> Books,ImmutableArray<JsonElement> Directory)> PrepareEconomicSource(
        NpgsqlConnection connection,PreviewData data,InputFrame frame,PortfolioEconomicSourceRequest request,CancellationToken ct)
    {
        if(request?.Scope?.Context is null||request.Projection is null)
            throw new MarketDataException("risk_economic_scope","Select the exact opening context, books, pricing and lifecycle projection policies.");
        request.Projection.Validate();
        var (opening,books,directory)=await PreparePortfolio(connection,data,frame,request.Scope.BookIds,request.Scope.PortfolioIds,request.PricingPolicy,
            ct,tradeIds:request.Scope.TradeIds);
        return (await LifecyclePortfolioProjection.Prepare(connection,database,opening,request.Projection,null,ct),books,directory);
    }

    public async Task<object> EconomicCashFunding(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        PortfolioEconomicCashFundingRequest request,CancellationToken ct)
    {
        if(request?.Scope?.Context is null||request.Projection is null)
            throw new MarketDataException("risk_economic_scope","Select the exact opening context, books, pricing and product projection policies.");
        request.Projection.ValidateCashFundingRead();
        var (opening,_,_)=await PreparePortfolio(connection,data,frame,request.Scope.BookIds,request.Scope.PortfolioIds,request.PricingPolicy,
            ct,tradeIds:request.Scope.TradeIds);
        return await LifecyclePortfolioProjection.CashFundingChoices(connection,database,opening,request.Projection,ct);
    }

    public async Task<object> EconomicSources(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        PortfolioEconomicSourceRequest request,CancellationToken ct)
    {
        var (economic,books,directory)=await PrepareEconomicSource(connection,data,frame,request,ct);
        var portfolio=economic.HistoryPortfolio;var markets=portfolio.Markets.Select(m=>m.Data).ToImmutableArray();
        return new{engineVersion=RiskJobs.EngineVersion,source=request with{Scope=request.Scope with{Context=new(frame.Id,TradingBlotter.Convention,"retained",portfolio.Context.ContextId),
                BookIds=books,PortfolioIds=[],TradeIds=request.Scope.TradeIds is {} ids?ids.Order().ToImmutableArray():null}},
            horizon=economic.HorizonEvidence,products=economic.ProductEvidence,
            books=directory.Where(b=>books.Contains(b.Id("id"))).ToImmutableArray(),
            factors=economic.Factors.Select(f=>Factor(markets,CurveRiskAxes.Quote(portfolio,f,ct))),history=HistoryCapabilities(portfolio),
            methods=economic.Factors.IsEmpty?new[]{LifecycleEconomicRiskPolicy.Deterministic}:
                [LifecycleEconomicRiskPolicy.Historical,LifecycleEconomicRiskPolicy.MonteCarlo],
            parametricAvailable=false};
    }

    public async Task<object> EconomicHistory(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        PortfolioEconomicHistoryRequest request,CancellationToken ct)
    {
        var source=request?.Source;var history=request?.History;
        if(source?.Scope?.Context is null||history?.Scope is null||history.Scope.Context!=source.Scope.Context||
            JsonSerializer.Serialize(history.PricingPolicy,RiskJobs.Json)!=JsonSerializer.Serialize(source.PricingPolicy,RiskJobs.Json)||
            (source.Scope.TradeIds is {} a?string.Join(',',a.Order()):null)!=(history.Scope.TradeIds is {} b?string.Join(',',b.Order()):null))
            throw new MarketDataException("risk_economic_scope","Calibration must name the exact selected economic source and pricing policies.");
        var (economic,books,directory)=await PrepareEconomicSource(connection,data,frame,source,ct);
        if(economic.Factors.IsEmpty)
            throw new MarketDataException("risk_economic_factors","A complete no-variable-factor scope does not use sampled calibration history.");
        if(!BookScope.Select(directory,history.Scope.BookIds,history.Scope.PortfolioIds).Select(b=>b.Id("id")).Order().SequenceEqual(books))
            throw new MarketDataException("risk_economic_scope","Calibration must cover exactly the complete projected book scope.");
        var portfolio=economic.HistoryPortfolio;
        var manifest=await BuildHistory(connection,portfolio,frame,history,ct);
        var scope=source.Scope with{Context=new(frame.Id,TradingBlotter.Convention,"retained",portfolio.Context.ContextId),BookIds=books,PortfolioIds=[],
            TradeIds=source.Scope.TradeIds is {} ids?ids.Order().ToImmutableArray():null};
        var captured=history with{Scope=scope,ExpectedManifestId=manifest.Id};
        var factors=manifest.Columns.Select(c=>CurveRiskAxes.Quote(portfolio,c.Mapping.Factor,ct)).ToImmutableArray();
        var markets=portfolio.Markets.Select(m=>m.Data).ToImmutableArray();
        var result=new{source=source with{Scope=scope},request=captured,manifest,
            factors=factors.Select(f=>f.Request),columns=manifest.Columns.Select((c,i)=>new{c.Id,factor=Factor(markets,factors[i]),state=c.Mapping.State.ToString()}),
            horizon=economic.HorizonEvidence,products=economic.ProductEvidence,
            coverage=new{manifest.Available,manifest.UnavailableReason,factors=manifest.Columns.Length,samples=manifest.Samples.Length},
            engineVersion=RiskJobs.EngineVersion};
        if(JsonSerializer.SerializeToUtf8Bytes(result,RiskJobs.Json).Length>MaximumHistoryBytes)
            throw new MarketDataException("risk_history_capacity","The complete economic history evidence exceeds the retained response capacity.");
        return result;
    }

    private async Task<Prepared> PrepareEconomic(NpgsqlConnection connection,PreviewData data,InputFrame frame,PortfolioRiskRequest outer,
        PortfolioEconomicRiskRequest request,CancellationToken ct,ImmutableArray<(PreviewData Data,InputFrame Frame)> supplemental,
        RetainedRun? retained,RetainedFactorHistory? historyPlan,ImmutableArray<RiskHistoryObservation> historyEvidence)
    {
        ValidateEconomic(outer,request);
        if(retained is {} previous&&!previous.Excluded.IsEmpty)
            throw new MarketDataException("risk_economic_scope","Economic risk requires the complete native holding scope without exclusions.");
        // Opening PV and the economic branch union have different graph roots.
        // Only the completed union is checked against a retained worker graph.
        var (opening,bookIds,books)=await PreparePortfolio(connection,data,frame,outer.BookIds,outer.PortfolioIds,outer.PricingPolicy,
            ct,supplemental,tradeIds:outer.TradeIds);
        var economic=await LifecyclePortfolioProjection.Prepare(connection,database,opening,request.Projection,retained?.Graphs,ct);
        var portfolio=economic.HistoryPortfolio;
        AlignedFactorHistory? history=null;
        if(request.Policy.Implementation==LifecycleEconomicRiskPolicy.Deterministic)
        {
            if(!economic.Factors.IsEmpty||historyPlan is not null||!historyEvidence.IsDefaultOrEmpty)
                throw new MarketDataException("risk_economic_factors","The no-variable-factor method requires complete preparation proving there are no variable factors or calibration inputs.");
        }
        else
        {
            var selected=request.History!;
            if(!BookScope.Select(books,selected.Scope.BookIds,selected.Scope.PortfolioIds).Select(b=>b.Id("id")).Order().SequenceEqual(bookIds))
                throw new MarketDataException("risk_economic_scope","Economic calibration and the complete projected holdings must use exactly the same books.");
            if(economic.Factors.IsEmpty)
                throw new MarketDataException("risk_economic_factors","This complete scope has no variable factors. Explicitly select the no-variable-factor method.");
            history=historyPlan is null?await BuildHistory(connection,portfolio,frame,selected,ct,
                statistical:request.Policy.Implementation==LifecycleEconomicRiskPolicy.MonteCarlo):
                await ReplayHistory(connection,portfolio,frame,selected,historyPlan,historyEvidence,ct,
                    historical:request.Policy.Implementation==LifecycleEconomicRiskPolicy.Historical);
            if(!history.Available)
                throw new MarketDataException("risk_economic_history","Economic risk requires complete aligned history for every transition and residual branch factor.");
        }
        var axes=history is null?ImmutableArray<PortfolioRiskAxis>.Empty:
            history.Columns.Select(c=>CurveRiskAxes.Quote(portfolio,c.Mapping.Factor,ct)).ToImmutableArray();
        var factors=axes.Select(a=>a.Request).ToImmutableArray();
        if(JsonSerializer.Serialize(request.Factors,RiskJobs.Json)!=JsonSerializer.Serialize(factors,RiskJobs.Json))
            throw new MarketDataException("risk_economic_factors","Retain every projected native factor exactly once in captured history-column order.");
        var context=new FuturesReadRequest(outer.Context.SnapshotId,TradingBlotter.Convention,"retained",portfolio.Context.ContextId);
        var canonical=outer with{Context=context,BookIds=bookIds,PortfolioIds=[],TradeIds=outer.TradeIds is {} ids?ids.Order().ToImmutableArray():null,
            Economic=request with{History=request.History is {} h?h with{Scope=h.Scope with{Context=context,BookIds=bookIds,PortfolioIds=[],
                TradeIds=outer.TradeIds is {} trades?trades.Order().ToImmutableArray():null}}:null,Factors=factors}};
        return new(canonical,RiskDetail.Holdings,bookIds,books,portfolio,[],[],
            portfolio.Markets.Where(m=>m.Data.DefinitionRevision!=data.DefinitionRevision).Select(m=>(m.Data,m.Frame)).ToImmutableArray())
            {Economic=economic,History=history,HistoricalAxes=axes};
    }

    private static object CalculateEconomic(Prepared prepared,CancellationToken ct)
    {
        var request=prepared.Request.Economic!;
        var economic=prepared.Economic??throw new MarketDataException("risk_economic_scope","Supply the complete prepared economic lifecycle scope.");
        object result=request.Policy.Implementation switch
        {
            LifecycleEconomicRiskPolicy.Deterministic=>LifecycleEconomicRisk.Deterministic(economic.Projections,request.Policy,ct),
            LifecycleEconomicRiskPolicy.Historical=>LifecycleEconomicRisk.Historical(economic.Projections,
                prepared.History??throw MissingHistory(),request.Policy,ct),
            LifecycleEconomicRiskPolicy.MonteCarlo=>LifecycleEconomicRisk.MonteCarlo(economic.Projections,
                prepared.History??throw MissingHistory(),request.Policy,request.Simulation!,ct),
            _=>throw new MarketDataException("risk_economic_policy","Select a qualified economic full-revaluation method.")
        };
        var carry=result switch{LifecycleEconomicRiskReport sampled=>sampled.NoShockTimeCarry,
            LifecycleDeterministicRiskReport deterministic=>deterministic.NoShockTimeCarry,
            _=>throw new MarketDataException("risk_economic_policy","The selected method did not produce a qualified economic report.")};
        var markets=prepared.Portfolio.Markets.Select(m=>m.Data).ToImmutableArray();
        var descriptions=LifecycleEconomicOwners.Describe(prepared.Portfolio.Positions,[..carry.Select(v=>v.Owner)],economic.AdditionalOwners);
        var holdings=carry.Select(value=>
        {
            var description=descriptions[value.Owner];
            var book=prepared.Books.Single(b=>b.Id("id")==value.Owner.BookId);
            return new{value.Owner,reference=description.Reference,instrument=description.Instrument,
                book=book.Text("name"),currency=Currency(markets,value.Currency),currencyReference=value.Currency,
                openingValue=value.OpeningValue.ToExactString(),residualValue=value.ResidualValue.ToExactString(),
                periodCash=value.PeriodCash.ToExactString(),economicPnl=value.EconomicPnl.ToExactString()};
        }).ToArray();
        return new{convention=Convention,engineVersion=RiskJobs.EngineVersion,measure=LifecycleEconomicRiskPolicy.Measure,
            context=prepared.Portfolio.Context,source=prepared.Request.Context,prepared.Request.BookIds,prepared.Request.TradeIds,pricingPolicy=prepared.Portfolio.Policy,
            request,snapshotId=prepared.Portfolio.SnapshotId,asOf=prepared.Portfolio.OpeningScope!.Frame.At,horizon=economic.HorizonEvidence,holdings,
            books=prepared.Books.Where(b=>prepared.BookIds.Contains(b.Id("id"))).Select(b=>new{id=b.Id("id"),name=b.Text("name")}),
            products=economic.ProductEvidence,optionCalibrations=economic.CalibrationReceipts,history=prepared.History,
            factors=prepared.HistoricalAxes.Select(axis=>Factor(markets,axis)),result};
        static MarketDataException MissingHistory()=>new("risk_economic_history","Supply the complete captured economic calibration history.");
    }

    internal static void ValidateEconomic(PortfolioRiskRequest outer,PortfolioEconomicRiskRequest request)
    {
        if(request is null||request.Projection is null)
            throw new MarketDataException("risk_economic_scope","Select the complete economic lifecycle risk envelope.");
        request.Projection.Validate();LifecycleEconomicRisk.ValidatePolicy(request.Policy);
        if(outer.Detail!="holdings"||outer.Convention!=Convention||outer.NumericalPolicy!=RiskEngine.ExactNumericalPolicy||
            outer.Bumps.IsDefault||!outer.Bumps.IsEmpty||outer.Stress.IsDefault||!outer.Stress.IsEmpty||
            outer.Parametric is not null||outer.Historical is not null||outer.MonteCarlo is not null||outer.Joint is not null||
            request.Factors.IsDefault||request.Factors.Length>MonteCarloRisk.MaximumFactors||request.Factors.Any(f=>f is null))
            throw Scope();
        if(request.Policy.Implementation==LifecycleEconomicRiskPolicy.Deterministic)
        {
            if(request.History is not null||request.Simulation is not null||!request.Factors.IsEmpty)throw Scope();
            return;
        }
        var history=request.History;
        if(history is null||history.Scope is null||outer.Context is null||history.Scope.Context!=outer.Context||history.ExpectedManifestId?.Length!=64||
            JsonSerializer.Serialize(history.PricingPolicy,RiskJobs.Json)!=JsonSerializer.Serialize(outer.PricingPolicy,RiskJobs.Json)||
            (outer.TradeIds is {} a?string.Join(',',a.Order()):null)!=(history.Scope.TradeIds is {} b?string.Join(',',b.Order()):null))
            throw Scope();
        if(request.Policy.Implementation==LifecycleEconomicRiskPolicy.Historical)
        {
            if(request.Simulation is not null)throw Scope();
        }
        else
        {
            var simulation=request.Simulation;
            MonteCarloRisk.ValidatePolicy(simulation!);
            if(simulation!.Convention!=MonteCarloRiskPolicy.Economic||simulation.ValuationPolicy!=MonteCarloRiskPolicy.EconomicValuation||
                simulation.Confidence!=request.Policy.Confidence)
                throw new MarketDataException("risk_economic_policy","Select the economic full-revaluation simulation and matching confidence explicitly.");
        }
        static MarketDataException Scope()=>new("risk_economic_scope","Economic risk requires one complete holdings scope, its exclusive method and explicit source and sampling choices.");
    }
}
