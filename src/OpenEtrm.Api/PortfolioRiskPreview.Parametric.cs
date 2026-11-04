using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ParametricFiniteDifference(string Convention,int TradingTicks,RiskToleranceRequest DeltaTolerance)
{
    public const string Version1="native_tick_central_half_nominal_double_v1";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioParametricRequest(PortfolioHistoryRequest History,ParametricRiskPolicy Policy,ParametricFiniteDifference FiniteDifference);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitParametricRiskJob(Guid RequestId,PortfolioParametricRequest Request);
// Only reference/clock/session choices and exact source keys are retained here.
// The worker obtains original values and certified knowledge through its fenced read.
public sealed record RetainedFactorHistory(Guid ClockCaptureId,string ExpectedManifestId,RiskHistoryForecast Forecast,RiskHistoryPolicy Policy,
    RiskHistoryCalendar Calendar,RiskHistoryClock Clock,ImmutableArray<Guid> Observations)
{
    internal static RetainedFactorHistory Capture(AlignedFactorHistory history,Guid capture)=>new(capture,history.Id,history.Forecast,history.Policy,history.Calendar,history.Clock,
        [..history.Evidence.Select(e=>e.Observation.Key.Id).Order()]);
}

public sealed partial class PortfolioRiskPreview
{
    internal Task<Guid> CaptureHistoryClock(AlignedFactorHistory history,CancellationToken ct)=>database.CaptureHistoryClock(history,ct);
    private static object ParametricCapabilities()=>new{
        profile=new{convention=ParametricRiskPolicy.Version1,numericalPolicy=ParametricRiskPolicy.Numerics,estimator=ParametricRiskPolicy.Sample,
            centering=ParametricRiskPolicy.Centered,horizon=ParametricRiskPolicy.Session,tail=ParametricRiskPolicy.Signed},
        defaults=new{confidence="0.99",drift=ParametricRiskPolicy.Zero,tradingTicks=1,deltaTolerance=new{absolute="0",relative="0.0001"}},
        drifts=new{zero=ParametricRiskPolicy.Zero,sample=ParametricRiskPolicy.Mean},finiteDifference=ParametricFiniteDifference.Version1,
        limits=new{maxFactors=ParametricRisk.MaximumFactors,maxProducts=ParametricRisk.MaximumProducts,maxExposures=ParametricRisk.MaximumExposures}};
    internal static PortfolioRiskRequest StatisticalRequest(PortfolioParametricRequest body)
    {
        if(body?.History?.Scope?.Context is not {} context)throw new MarketDataException("risk_parametric_scope","Choose the exact captured history and retained trade scope.");
        return new(context,body.History.Scope.BookIds,body.History.Scope.PortfolioIds,[],Convention,RiskEngine.ExactNumericalPolicy,
            body.History.PricingPolicy,"books",[],body.History.Scope.TradeIds,Parametric:body);
    }
    private static void ValidateParametric(PortfolioRiskRequest outer,PortfolioParametricRequest body)
    {
        ParametricRisk.ValidatePolicy(body.Policy);
        if(body.History?.Scope?.Context is not {} context||context!=outer.Context||body.History.ExpectedManifestId?.Length!=64||
            JsonSerializer.Serialize(body.History.PricingPolicy,RiskJobs.Json)!=JsonSerializer.Serialize(outer.PricingPolicy,RiskJobs.Json)||outer.Joint is not null||outer.Detail!="books"||outer.Stress.IsDefault||!outer.Stress.IsEmpty||
            (outer.TradeIds is {} a?string.Join(',',a.Order()):null)!=(body.History.Scope.TradeIds is {} b?string.Join(',',b.Order()):null))
            throw new MarketDataException("risk_parametric_scope","Parametric risk uses the exact captured history, common retained holdings and an unstressed books-detail portfolio.");
        ValidateParametricFiniteDifference(body.FiniteDifference);
    }
    private static void ValidateParametricFiniteDifference(ParametricFiniteDifference fd)
    {
        if(fd is null||fd.Convention!=ParametricFiniteDifference.Version1||fd.TradingTicks is <1 or >1000||fd.DeltaTolerance is null||
            ExactDecimal.Parse(fd.DeltaTolerance.Absolute??"").CompareTo(default)<0||ExactDecimal.Parse(fd.DeltaTolerance.Relative??"").CompareTo(default)<0)
            throw new MarketDataException("risk_parametric_delta","Choose explicit 1–1000 trading ticks and nonnegative native absolute/relative delta agreement tolerances.");
    }
    private static ImmutableArray<CurveBumpRequest> ParametricBumps(PortfolioValuation.Prepared portfolio,AlignedFactorHistory history,ParametricFiniteDifference policy,CancellationToken ct)
    {
        return [..history.Columns.Select(c=>new CurveBumpRequest(CurveRiskAxes.Quote(portfolio,c.Mapping.Factor,ct).Request,
            ParametricRisk.ResolveStep(c.Mapping.Factor.TradingTick,policy.TradingTicks).ToString(),ConnectedRiskBump.Convention))];
    }
    private static void RequireParametricWork(PortfolioValuation.Prepared portfolio,ImmutableArray<PortfolioCurveBump> bumps)
    {
        var scenarios=1+2L*bumps.Length;
        var rows=portfolio.Ppa is {} ppa?checked(ppa.Rows*scenarios):0;
        var width=portfolio.Markets.Sum(m=>RiskEngine.ScenarioResultWidth(m.Connected,RiskDetail.Books));
        var nodes=portfolio.Options?.Bindings.Values.Where(b=>b.Receipt.Mode==OpenEtrm.MarketData.Trading.FuturesOptionPricing.SolvedMode)
            .Sum(b=>checked((long)b.Policy.Steps*(b.Policy.Steps+1)/2*scenarios))??0;
        RiskValuationAdmission.Require(rows,nodes,checked(width*scenarios),bumps.Length,3);
    }
    private static async Task<AlignedFactorHistory> ReplayHistory(NpgsqlConnection connection,PortfolioValuation.Prepared portfolio,InputFrame frame,
        PortfolioHistoryRequest request,RetainedFactorHistory plan,ImmutableArray<RiskHistoryObservation> evidence,CancellationToken ct,bool historical=false)
    {
        bool Same<T>(T a,T b)=>JsonSerializer.Serialize(a,RiskJobs.Json)==JsonSerializer.Serialize(b,RiskJobs.Json);
        if(plan.ExpectedManifestId!=request.ExpectedManifestId||plan.Forecast.ContextId!=frame.ContextId||plan.Forecast.ValuationAt!=frame.At||
            plan.Forecast.KnownAt!=SchedulePreview.ParseAt(portfolio.Context.KnownAt)||!Same(plan.Policy,request.Policy)||plan.Calendar.Reference!=request.Calendar||
            plan.Calendar.From!=DateWorkspace.Date(request.From)||plan.Calendar.To!=DateWorkspace.Date(request.To)||plan.Clock.CloseTime!=request.CloseTime||
            plan.Clock.AmbiguousTime!=request.AmbiguousTime||plan.Clock.InvalidTime!=request.InvalidTime||plan.Observations.IsDefault||
            !plan.Observations.Order().SequenceEqual(evidence.Select(e=>e.Observation.Key.Id).Order()))
            throw new MarketDataException("risk_parametric_history","The retained history inputs differ from their captured statistical request.");
        var expectedMarkets=portfolio.Markets.Select(m=>new RiskHistoryMarket(m.Snapshot.Definitions,m.Snapshot.Id,m.Connected.Scenario.Plan.ManifestId)).OrderBy(m=>m.Definitions.RevisionId).ToImmutableArray();
        if(!Same(expectedMarkets,plan.Forecast.Markets.OrderBy(m=>m.Definitions.RevisionId).ToImmutableArray())||
            portfolio.Markets.Any(m=>m.Snapshot.MarketCutoff!=plan.Forecast.MarketCutoff)||plan.Forecast.CatalogId!=portfolio.Markets[0].Snapshot.Definitions.CatalogId)
            throw new MarketDataException("risk_parametric_history","Statistical history and current PV must retain exactly the same market snapshots and model graphs.");
        // The immutable frame's market prefix was certified by retain_parametric.
        // Replaying it does not grant the worker raw market-context reads.
        var data=HistoryReferences(portfolio).SingleOrDefault(d=>d.ReferenceRevision==plan.Calendar.Reference.Revision)
            ??throw new PreviewDeniedException();
        var row=data.References["calendars"].Single(c=>c.Text("code")==plan.Calendar.Reference.Code);
        if(plan.Clock.Reference!=new ReferenceKey(data.ReferenceRevision,row.Text("clock_code")))throw new MarketDataException("risk_parametric_history","The retained calendar and clock references differ.");
        var stored=await new ScheduleStore(plan.Forecast.CatalogId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId)
            .Calendar(connection,new(data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId,plan.Calendar.Reference.Code),ct);
        var cropped=stored with{CoverageFrom=plan.Calendar.From,CoverageTo=plan.Calendar.To,Days=[..stored.Days.Where(d=>d.Date>=plan.Calendar.From&&d.Date<plan.Calendar.To)]};
        if(!DateWorkspace.Calendar(cropped).Dates.Order().SequenceEqual(plan.Calendar.BusinessDates.Order()))
            throw new MarketDataException("risk_parametric_history","The retained calendar sessions differ from their exact reference version.");
        var factors=portfolio.Factors.Select(f=>new RiskHistoryFactor(f,portfolio.Markets.Single(m=>m.Snapshot.Definitions==f.Input.Series.Revision)
            .Snapshot.Selections.Single(s=>s.Observation.Key==f.Observation).Observation.State)).ToImmutableArray();
        if(historical)HistoricalRisk.Admit(factors.Length,plan.Calendar.BusinessDates.Length-1);
        else ParametricRisk.Admit(factors.Length,plan.Calendar.BusinessDates.Length-1);
        var result=FactorHistory.Align(plan.Forecast,plan.Policy,plan.Calendar,plan.Clock,factors,evidence,ct);
        if(result.Id!=plan.ExpectedManifestId)throw new MarketDataException("risk_history_manifest","The pinned historical observations no longer reproduce their retained manifest.");
        return result;
    }
    // One statistical evaluation: its history, the half/nominal/double valuation passes, their delta estimates and the Gaussian report.
    private sealed record ParametricRun(PortfolioParametricRequest Spec,AlignedFactorHistory History,ParametricRiskReport Result,bool Evaluated,int TreesBefore,
        PortfolioValuationResult Nominal,PortfolioValuationResult? Half,PortfolioValuationResult? Twice,List<object> Estimates);
    private static ParametricRun EvaluateParametric(Prepared prepared,CancellationToken ct)
    {
        var spec=prepared.Request.Parametric!;var history=prepared.History??throw new MarketDataException("risk_parametric_history","Missing prepared calibration history.");
        var before=prepared.Portfolio.Options?.TreePrices??0;
        PortfolioValuationResult Run(ExactDecimal multiplier)=>PortfolioValuation.Calculate(prepared.Portfolio,
            [..prepared.Bumps.Select(b=>b with{Size=b.Size*multiplier})],[],RiskDetail.Books,ct);
        var evaluate=history.Available&&prepared.Portfolio.Excluded.IsEmpty;
        var nominal=evaluate?Run(ExactDecimal.Parse("1")):PortfolioValuation.Calculate(prepared.Portfolio,[],[],RiskDetail.Books,ct);
        var half=evaluate?Run(ExactDecimal.Parse("0.5")):null;var twice=evaluate?Run(ExactDecimal.Parse("2")):null;
        var tolerance=new FiniteDifferenceTolerance(ExactDecimal.Parse(spec.FiniteDifference.DeltaTolerance.Absolute),ExactDecimal.Parse(spec.FiniteDifference.DeltaTolerance.Relative));
        var deltas=ImmutableArray.CreateBuilder<ParametricExposure>();var estimates=new List<object>();
        if(evaluate)foreach(var total in nominal.Totals)
        {
            var values=ImmutableArray.CreateBuilder<ParametricDelta>();
            for(var i=0;i<history.Columns.Length;i++)
            {
                var bump=prepared.Bumps[i];
                PortfolioFactorRisk Value(PortfolioValuationResult run)=>run.Risk.Single(v=>v.Factor==bump.Axis.Id&&v.BookId==total.BookId&&v.Measure==total.Measure&&v.Currency==total.Value.Currency);
                var lo=Value(half!);var mid=Value(nominal);var hi=Value(twice!);
                var assessed=RiskEngine.AssessFiniteDifference(lo.Delta.Value,mid.Delta.Value,hi.Delta.Value,tolerance);
                values.Add(new(history.Columns[i].Id,bump.Axis.Unit,mid.Delta.Value.ToExactString(),assessed.Stable));
                estimates.Add(new{columnId=history.Columns[i].Id,total.BookId,total.Measure,currency=total.Value.Currency,
                    half=lo.Delta.Value.ToExactString(),nominal=mid.Delta.Value.ToExactString(),@double=hi.Delta.Value.ToExactString(),assessed.Stable,
                    gamma=mid.Gamma.Value.ToExactString(),bump=bump.Size.ToString(),unit=PriceUnitReference(bump.Axis.Unit)});
            }
            var id=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{total.BookId,total.Measure,total.Value.Currency},RiskJobs.Json)));
            deltas.Add(new(id,total.BookId,total.Measure,total.Value.Currency,values.ToImmutable()));
        }
        var result=ParametricRisk.Calculate(history,spec.Policy,deltas.ToImmutable(),prepared.Portfolio.Excluded.IsEmpty,ct);
        return new(spec,history,result,evaluate,before,nominal,half,twice,estimates);
    }
    private static object CalculateParametric(Prepared prepared,CancellationToken ct)
    {
        var (spec,history,result,evaluate,before,nominal,half,twice,estimates)=EvaluateParametric(prepared,ct);
        var presentation=prepared with{Bumps=evaluate?prepared.Bumps:[]};
        var report=JsonSerializer.SerializeToNode(PresentSensitivity(presentation,nominal,before),RiskJobs.Json)!.AsObject();report.Remove("factors");
        if(evaluate)
        {
            // Per-pass receipts include the shared preparation. Count that once,
            // retain unique identities from nominal, and sum executed valuation work.
            var work=StatisticalWork(prepared.Portfolio,nominal.Work,[half!.Work,nominal.Work,twice!.Work]);
            report["work"]=JsonSerializer.SerializeToNode(work,RiskJobs.Json);
        }
        var markets=prepared.Portfolio.Markets.Select(m=>m.Data).ToImmutableArray();
        report["parametric"]=JsonSerializer.SerializeToNode(new{result,request=spec,history,
            exposures=nominal.Totals.Select(t=>new{t.BookId,t.Measure,currency=t.Value.Currency}),
            factors=history.Columns.Select(c=>new{c.Id,factor=Factor(markets,CurveRiskAxes.Quote(prepared.Portfolio,c.Mapping.Factor,ct))}),
            books=prepared.Books.Where(b=>prepared.BookIds.Contains(b.Id("id"))).Select(b=>new{id=b.Id("id"),name=b.Text("name")}),
            partitions=result.Partitions.Select(p=>new{p.Measure,measureLabel=MeasureLabel(p.Measure),currencyReference=p.Currency,currency=Currency(markets,p.Currency)}),
            finiteDifference=new{spec.FiniteDifference.Convention,spec.FiniteDifference.TradingTicks,spec.FiniteDifference.DeltaTolerance,evaluated=evaluate,estimates},
            passes=evaluate?new[]{half!.Work,nominal.Work,twice!.Work}:new[]{nominal.Work}},RiskJobs.Json);
        if(System.Text.Encoding.UTF8.GetByteCount(report.ToJsonString(RiskJobs.Json))>30_000_000)
            throw new MarketDataException("risk_result_limit","Reduce the explicit statistical scope or window: the complete report exceeds 30 MB.");
        return report;
    }
    private static RiskWork StatisticalWork(PortfolioValuation.Prepared preparation,RiskWork basis,ImmutableArray<RiskWork> passes)=>basis with{
                ScenarioCount=passes.Sum(w=>w.ScenarioCount),UnitPricerCalls=passes.Sum(w=>w.UnitPricerCalls),
                RebuiltCurveNodes=passes.Sum(w=>w.RebuiltCurveNodes)-(passes.Length-1)*preparation.MaterializationWork.RebuiltCurveNodes,
                ReusedCurveNodes=passes.Sum(w=>w.ReusedCurveNodes)-(passes.Length-1)*preparation.MaterializationWork.ReusedCurveNodes,
                PreparationScenarioCount=passes.Sum(w=>w.PreparationScenarioCount)-(passes.Length-1)*preparation.MaterializationScenarioCount,
                QuantityApplications=passes.Sum(w=>w.QuantityApplications),CurveGraphEvaluatedNodes=passes.Sum(w=>w.CurveGraphEvaluatedNodes),
                CurveGraphReusedReads=passes.Sum(w=>w.CurveGraphReusedReads),VolumeComputations=passes.Sum(w=>w.VolumeComputations),
                UniqueSideCalculations=passes.Sum(w=>w.UniqueSideCalculations),RowEvaluations=passes.Sum(w=>w.RowEvaluations),
                ScheduleGraphEvaluatedNodes=passes.Sum(w=>w.ScheduleGraphEvaluatedNodes),ScheduleGraphReusedReads=passes.Sum(w=>w.ScheduleGraphReusedReads),
                ResolvedCurveMonths=passes.Sum(w=>w.ResolvedCurveMonths),ReusedCurveMonths=passes.Sum(w=>w.ReusedCurveMonths),
                CalibratedBlocks=passes.Sum(w=>w.CalibratedBlocks),ReusedBlocks=passes.Sum(w=>w.ReusedBlocks)};
    // The parametric summary is the Risk page's calculation, projected to its exact estimates, coverage and contributions.
    // Partitions name books by id and factors by their position in `factors`, so each label appears once.
    public async Task<object> ParametricSummary(NpgsqlConnection connection,PreviewData data,InputFrame frame,PortfolioParametricRequest body,CancellationToken ct)
    {
        var history=await CapturedHistory(connection,data,frame,body?.History,"risk_parametric_scope",ct);
        var prepared=await Prepare(connection,data,frame,StatisticalRequest(body! with{History=history}),ct);
        var run=EvaluateParametric(prepared,ct);var manifest=run.History;var portfolio=prepared.Portfolio;
        var markets=portfolio.Markets.Select(m=>m.Data).ToImmutableArray();
        var axes=manifest.Columns.Select(c=>CurveRiskAxes.Quote(portfolio,c.Mapping.Factor,ct)).ToImmutableArray();
        var column=manifest.Columns.Select((c,i)=>(c.Id,i)).ToDictionary(c=>c.Id,c=>c.i,StringComparer.Ordinal);
        SignedRatio Size(string value)=>SignedRatio.TryParse(value,out var r)?r.CompareTo(SignedRatio.Zero)<0?SignedRatio.Zero-r:r:
            throw new MarketDataException("risk_parametric_numeric","Expected plain backend Gaussian output.");
        return Shown(new{asOf=frame.At,contextId=portfolio.Context.ContextId,historyId=manifest.Id,run.Result.Available,run.Result.UnavailableReasons,
            policy=run.Spec.Policy,history=new{run.Spec.History.Calendar,run.Spec.History.From,run.Spec.History.To,run.Spec.History.Policy},
            coverage=SummaryCoverage(manifest,portfolio),
            excludedPositions=portfolio.Excluded,
            books=prepared.Books.Where(b=>prepared.BookIds.Contains(b.Id("id"))).Select(b=>new{id=b.Id("id"),name=b.Text("name")}),
            factors=SummaryFactors(manifest,axes,markets),
            calibration=new{run.Result.Calibration.Samples,run.Result.Calibration.Rank,run.Result.Calibration.Conditioning,run.Result.Calibration.CorrelationCondition},
            partitions=run.Result.Partitions.Select(p=>new{p.Measure,measureLabel=MeasureLabel(p.Measure),currency=Currency(markets,p.Currency),
                p.Mean,p.StandardDeviation,p.ValueAtRisk,p.ExpectedShortfall,
                books=p.Books.Select(b=>new{bookId=Guid.Parse(b.Id),b.ValueAtRisk,b.ExpectedShortfall}),
                factors=p.Factors.OrderByDescending(f=>Size(f.ValueAtRisk)).Select(f=>new{factor=column[f.Id],f.ValueAtRisk,f.ExpectedShortfall})})});
    }
}
