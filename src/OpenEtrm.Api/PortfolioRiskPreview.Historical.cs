using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HistoricalParametricComparison(ParametricRiskPolicy Policy,ParametricFiniteDifference FiniteDifference);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioHistoricalRequest(PortfolioHistoryRequest History,HistoricalRiskPolicy Policy,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] HistoricalParametricComparison? Comparison=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<CurveRiskAxisRequest> Factors=default);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitHistoricalRiskJob(Guid RequestId,PortfolioHistoricalRequest Request);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioHistoricalSampleRequest(PortfolioHistoricalRequest Request,string Sample);

public sealed partial class PortfolioRiskPreview
{
    private static object HistoricalCapabilities()=>new{
        profile=new{convention=HistoricalRiskPolicy.Version1,numericalPolicy=HistoricalRiskPolicy.Numerics,horizon=HistoricalRiskPolicy.Session,
            samplePolicy=HistoricalRiskPolicy.Sampling,quantilePolicy=HistoricalRiskPolicy.Quantile,tailPolicy=HistoricalRiskPolicy.Tail,valuationPolicy=HistoricalRiskPolicy.Valuation},
        defaults=new{confidence="0.99",compareParametric=false,comparisonDrift=ParametricRiskPolicy.Mean},
        limits=new{maxFactors=HistoricalRisk.MaximumFactors,maxSamples=HistoricalRisk.MaximumSamples,
            maxExposures=HistoricalRisk.MaximumExposures,maxResultValues=HistoricalRisk.MaximumValues}};

    internal static PortfolioRiskRequest HistoricalRequest(PortfolioHistoricalRequest body)
    {
        if(body?.History?.Scope?.Context is not {} context)throw new MarketDataException("risk_historical_scope","Choose the captured history and exact retained holdings.");
        return new(context,body.History.Scope.BookIds,body.History.Scope.PortfolioIds,[],Convention,RiskEngine.ExactNumericalPolicy,
            body.History.PricingPolicy,"holdings",[],body.History.Scope.TradeIds,Historical:body);
    }
    internal static void ValidateHistorical(PortfolioRiskRequest outer,PortfolioHistoricalRequest body)
    {
        HistoricalRisk.ValidatePolicy(body.Policy);
        if(body.History?.Scope?.Context is not {} context||context!=outer.Context||body.History.ExpectedManifestId?.Length!=64||
            JsonSerializer.Serialize(body.History.PricingPolicy,RiskJobs.Json)!=JsonSerializer.Serialize(outer.PricingPolicy,RiskJobs.Json)||
            outer.Parametric is not null||outer.Joint is not null||outer.Detail!="holdings"||outer.Bumps.IsDefault||!outer.Bumps.IsEmpty||
            outer.Stress.IsDefault||!outer.Stress.IsEmpty||
            !body.Factors.IsDefault&&(body.Factors.Length is <1 or >HistoricalRisk.MaximumFactors||body.Factors.Any(f=>f is null))||
            (outer.TradeIds is {} a?string.Join(',',a.Order()):null)!=(body.History.Scope.TradeIds is {} b?string.Join(',',b.Order()):null))
            throw new MarketDataException("risk_historical_scope","Historical simulation requires one complete captured holdings scope, no parent stress or FD bumps, and its own method envelope.");
        if(body.Comparison is {} comparison)
        {
            ParametricRisk.ValidatePolicy(comparison.Policy);
            if(comparison.Policy.Confidence!=body.Policy.Confidence||comparison.Policy.Horizon!=body.Policy.Horizon||comparison.Policy.Drift!=ParametricRiskPolicy.Mean)
                throw new MarketDataException("risk_historical_comparison","The matching parametric comparison must use the same confidence and frozen horizon with explicit sample-mean drift.");
            ValidateParametricFiniteDifference(comparison.FiniteDifference);
        }
    }
    private static PortfolioJointRisk.Resolved ResolveHistorical(PortfolioValuation.Prepared portfolio,AlignedFactorHistory history,
        ImmutableArray<PortfolioRiskAxis> axes,CancellationToken ct)
    {
        HistoricalRisk.Admit(history.Columns.Length,history.Samples.Length);
        var expected=HistoricalHoldings(portfolio);
        if(expected.Length is <1 or >HistoricalRisk.MaximumExposures||expected.Distinct().Count()!=expected.Length)
            throw new MarketDataException("risk_historical_exposures","The complete native holding set must contain 1–4096 distinct position/measure/currency identities.");
        var samples=history.Samples.Select(row=>
        {
            ct.ThrowIfCancellationRequested();
            if(row.Changes.Length!=axes.Length)throw new MarketDataException("risk_historical_samples","Each captured row must contain the complete native factor vector.");
            return new PortfolioJointRisk.Sample(row.Id,row.Id,[..axes.Select((axis,i)=>new PortfolioCurveShift(axis,row.Changes[i])).Where(s=>s.Size!=default)]);
        }).ToImmutableArray();
        var transport=new PortfolioScenarioSetRequest(history.Id,HistoricalRiskPolicy.Version1,ConnectedRiskScenarioSet.Independent,
            [..samples.Select(row=>new PortfolioScenarioRequest(row.Id,row.Label,[..row.Shifts.Select(s=>new CurveShiftRequest(s.Axis.Request,s.Size.ToString(),CurveShift.ParentAdditive))]))]);
        return new(new(PortfolioJointRiskRequest.Version1,transport,null),samples,[],[]);
    }
    private static void RequireHistoricalWork(PortfolioValuation.Prepared portfolio,AlignedFactorHistory history,PortfolioHistoricalRequest request)
    {
        HistoricalRisk.Admit(history.Columns.Length,history.Samples.Length);
        var eligible=history.Available&&portfolio.Excluded.IsEmpty;
        var historicalScenarios=1L+(eligible?history.Samples.Length:0);
        var comparisonScenarios=eligible&&request.Comparison is not null?3L*(1+2L*history.Columns.Length):0;
        if(request.Comparison is not null)ParametricRisk.Admit(history.Columns.Length,history.Samples.Length);
        var scenarios=checked(historicalScenarios+comparisonScenarios);
        var width=portfolio.Markets.Sum(m=>RiskEngine.ScenarioResultWidth(m.Connected,RiskDetail.Holdings));
        var normalWidth=portfolio.Markets.Sum(m=>RiskEngine.ScenarioResultWidth(m.Connected,RiskDetail.Books));
        var rows=portfolio.Ppa is {} ppa?checked(ppa.Rows*scenarios):0;
        var nodes=portfolio.Options?.Bindings.Values.Where(b=>b.Receipt.Mode==OpenEtrm.MarketData.Trading.FuturesOptionPricing.SolvedMode)
            .Sum(b=>checked((long)b.Policy.Steps*(b.Policy.Steps+1)/2*scenarios))??0;
        RiskValuationAdmission.Require(rows,nodes,checked(width*historicalScenarios+normalWidth*comparisonScenarios),history.Columns.Length,1);
    }

    private readonly record struct HistoricalPositionKey(Guid BookId,Guid TradeId,Guid BindingId,string Measure,ReferenceKey Currency);
    private static HistoricalPositionKey[] HistoricalHoldings(PortfolioValuation.Prepared portfolio)=>
        portfolio.Markets.SelectMany(m=>m.Connected.Holdings.Select(p=>
            new HistoricalPositionKey(p.BookId,p.TradeId,p.BindingId,m.Measures[p.Group],p.Currency))).ToArray();
    private static HistoricalPositionKey HistoricalKey(string measure,ExactRiskPositionValue position)=>
        new(position.BookId,position.TradeId,position.BindingId,measure,position.Value.Currency);
    private static string HistoricalExposureId(HistoricalPositionKey key)=>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(key,RiskJobs.Json)));

    // Only called after the complete frozen historical PV run succeeded. FD steps were admitted in Prepare.
    // Coverage, history, pricing identity and authority failures must never become optional-comparison unavailability.
    internal static JsonObject HistoricalComparison(Func<object> calculate,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var compared=JsonSerializer.SerializeToNode(calculate(),RiskJobs.Json)!.AsObject();
            ct.ThrowIfCancellationRequested();
            return new(){["report"]=compared["parametric"]!.DeepClone(),["work"]=compared["work"]!.DeepClone()};
        }
        catch(MarketDataException error)when(error.Code is "risk_parametric_numeric" or "price_domain" or "option_input" or "numeric_range")
        {
            ct.ThrowIfCancellationRequested();
            return new(){["report"]=null,["work"]=null,["unavailableReason"]=JsonSerializer.SerializeToNode(new{error.Code,error.Message},RiskJobs.Json)};
        }
    }

    // One historical evaluation: the frozen base, every scenario row and the empirical report, before presentation.
    private sealed record HistoricalRun(PortfolioHistoricalRequest Spec,AlignedFactorHistory History,bool Eligible,int TreesBefore,
        ImmutableArray<HistoricalExposure> Exposures,ImmutableArray<HistoricalObservation> Observations,PortfolioValuationResult Nominal,HistoricalRiskReport Result);
    private static HistoricalRun EvaluateHistorical(Prepared prepared,CancellationToken ct)
    {
        var spec=prepared.Request.Historical!;var history=prepared.History??throw new MarketDataException("risk_historical_samples","Missing captured historical rows.");
        var eligible=history.Available&&prepared.Portfolio.Excluded.IsEmpty;var before=prepared.Portfolio.Options?.TreePrices??0;
        var exposures=ImmutableArray<HistoricalExposure>.Empty;var observations=ImmutableArray<HistoricalObservation>.Empty;
        PortfolioValuationResult nominal;HistoricalRiskReport result;
        if(eligible)
        {
            var run=PortfolioJointCalculation.Calculate(prepared.Portfolio,prepared.Joint!,[],RiskDetail.Holdings,ct,historical:true);
            // Expected native holdings come from preparation, independently of scenario output. A model cannot omit or rename one.
            var expected=HistoricalHoldings(prepared.Portfolio);
            if(expected.Distinct().Count()!=expected.Length)throw new MarketDataException("risk_historical_exposures","Prepared native holdings contain duplicate identities.");
            var basis=run.Partitions.SelectMany(p=>p.Run.Groups.SelectMany((g,n)=>g.Positions.Select(v=>(Key:HistoricalKey(p.Market.Measures[n],v),Value:v)))).ToArray();
            if(basis.Length!=expected.Length||basis.Select(v=>v.Key).Distinct().Count()!=expected.Length||!basis.Select(v=>v.Key).ToHashSet().SetEquals(expected))
                throw new MarketDataException("risk_historical_exposures","The frozen base must account for every prepared native holding exactly once.");
            var bases=basis.ToDictionary(v=>v.Key,v=>v.Value.Value);
            exposures=[..basis.Select(v=>new HistoricalExposure(HistoricalExposureId(v.Key),v.Key.BookId,v.Key.TradeId,v.Key.BindingId,v.Key.Measure,v.Key.Currency,v.Value.Value.ToExactString()))
                .OrderBy(e=>e.Id,StringComparer.Ordinal)];
            observations=[..run.Samples.Select(sample=>
            {
                ct.ThrowIfCancellationRequested();
                var cells=sample.Positions.Select(v=>
                {
                    var p=v.Position;var key=new HistoricalPositionKey(p.BookId,p.TradeId,p.BindingId,v.Measure,p.Values.Base.Currency);
                    if(!bases.TryGetValue(key,out var current)||current.ToExactString()!=p.Values.Base.ToExactString()||
                        p.Values.Value.Currency!=key.Currency||p.Values.Loss.ToExactString()!=current.Subtract(p.Values.Value).ToExactString())
                        throw new MarketDataException("risk_historical_exposures","A scenario changed its frozen base, native currency or complete position identity.");
                    return new HistoricalScenarioValue(HistoricalExposureId(key),p.Values.Value.ToExactString());
                }).ToImmutableArray();
                return new HistoricalObservation(sample.Id,cells);
            })];
            result=HistoricalRisk.Calculate(history.Id,[..history.Samples.Select(s=>s.Id)],spec.Policy,exposures,observations,ct);
            nominal=new(prepared.Portfolio.SnapshotId,prepared.Portfolio.Context,prepared.Portfolio.Policy,
                PortfolioValuation.ModelContracts(run.Partitions.SelectMany(p=>p.Run.Groups).SelectMany(g=>g.Contracts)),prepared.Portfolio.Positions,[],run.BaseTotals,[],run.Work)
            {
                Detail=RiskDetail.Holdings,BasePositions=[..basis.Select(v=>new PortfolioHoldingValue(v.Key.Measure,v.Value))],
                ConnectedScenarios=[..run.Partitions.Select(p=>new PortfolioConnectedScenario(p.Market.Snapshot.Definitions,p.Run.BaseScenario.ManifestId,p.Run.BaseScenario.Id,
                    p.Run.BaseScenario.ParentId,p.Run.BaseScenario.Depth,p.Run.GraphWork,p.Market.Preparation))],
                DiscountInputs=prepared.Portfolio.DiscountInputs,PpaReceipt=prepared.Portfolio.Ppa?.Receipt,
                PpaMeasures=[..run.Partitions.SelectMany(p=>p.Run.Groups.SelectMany((g,i)=>p.Market.Measures[i]==PortfolioValuation.PpaMeasure?g.Measures:ImmutableArray<OpenEtrm.MarketData.Trading.BookValueMeasures>.Empty))]
            };
        }
        else
        {
            nominal=PortfolioValuation.Calculate(prepared.Portfolio,[],[],RiskDetail.Holdings,ct);
            result=new(spec.Policy,history.Id,history.Samples.Length,[],false,
                [..new[]{history.Available?null:"insufficient_common_history",prepared.Portfolio.Excluded.IsEmpty?null:"incomplete_portfolio"}.OfType<string>()]);
        }
        return new(spec,history,eligible,before,exposures,observations,nominal,result);
    }
    private static string HistoricalStudyId(Prepared prepared,HistoricalRun run)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{
        prepared.Portfolio.Context,prepared.Portfolio.SnapshotId,prepared.Portfolio.Policy,prepared.Portfolio.Positions,
        prepared.Portfolio.ConnectedMarkets,prepared.Portfolio.OptionCalibrations,run.History.Id,run.Spec.Policy.Horizon,run.Spec.Policy.ValuationPolicy,run.Spec.Policy.Confidence},RiskJobs.Json)));
    // The optional matching normal comparison over the same study; null when not selected or when history is ineligible.
    private static JsonObject? RunHistoricalComparison(Prepared prepared,HistoricalRun run,string studyId,CancellationToken ct)
    {
        if(run.Spec.Comparison is not {} selected||!run.Eligible)return null;
        var normal=new PortfolioParametricRequest(run.Spec.History,selected.Policy,selected.FiniteDifference);
        var normalPrepared=prepared with{Request=StatisticalRequest(normal),Detail=RiskDetail.Books,Bumps=prepared.HistoricalComparisonBumps,Joint=null,HistoricalAxes=[]};
        var compared=HistoricalComparison(()=>CalculateParametric(normalPrepared,ct),ct);
        compared["studyId"]=studyId;compared["request"]=JsonSerializer.SerializeToNode(selected,RiskJobs.Json);return compared;
    }

    private static object CalculateHistorical(Prepared prepared,CancellationToken ct)
    {
        var run=EvaluateHistorical(prepared,ct);
        var (spec,history,eligible,before,exposures,observations,nominal,result)=run;
        var report=JsonSerializer.SerializeToNode(PresentSensitivity(prepared with{Bumps=[]},nominal,before),RiskJobs.Json)!.AsObject();
        report.Remove("factors");
        var studyId=HistoricalStudyId(prepared,run);
        var comparison=RunHistoricalComparison(prepared,run,studyId,ct);
        if(comparison is not null)
        {
            if(comparison["work"] is {} work)
                report["work"]=JsonSerializer.SerializeToNode(StatisticalWork(prepared.Portfolio,nominal.Work,[nominal.Work,work.Deserialize<RiskWork>(RiskJobs.Json)!]),RiskJobs.Json);
            else report["workAccounting"]= "completed_historical_only_comparison_attempt_unmetered_v1";
        }
        if(prepared.Portfolio.OptionWork is {} allOptionWork)
            report["optionWork"]=JsonSerializer.SerializeToNode(allOptionWork with{ScenarioTreePrices=prepared.Portfolio.Options!.TreePrices-before},RiskJobs.Json);
        var markets=prepared.Portfolio.Markets.Select(m=>m.Data).ToImmutableArray();
        var evidence=prepared.Portfolio.Positions.ToDictionary(p=>(p.TradeId,p.BindingId));
        var historical=JsonSerializer.SerializeToNode(new{studyId,result,request=spec,history,
            factors=history.Columns.Select((c,i)=>new{c.Id,factor=Factor(markets,prepared.HistoricalAxes[i])}),
            books=prepared.Books.Where(b=>prepared.BookIds.Contains(b.Id("id"))).Select(b=>new{id=b.Id("id"),name=b.Text("name")}),
            exposures=exposures.Select(e=>new{e.Id,e.BookId,e.TradeId,e.BindingId,e.Measure,e.Currency,e.BaseValue,
                evidence[(e.TradeId,e.BindingId)].TradeReference,evidence[(e.TradeId,e.BindingId)].InstrumentReference,
                evidence[(e.TradeId,e.BindingId)].InstrumentType}),
            partitions=result.Partitions.Select(p=>new{p.Measure,measureLabel=MeasureLabel(p.Measure),currencyReference=p.Currency,currency=Currency(markets,p.Currency)}),
            matrix=new{columns=exposures.Select(e=>e.Id),rows=observations.Select(row=>
            {
                var byId=row.Values.ToDictionary(v=>v.ExposureId,StringComparer.Ordinal);
                var losses=exposures.Select(e=>ExactMoney.Parse(e.BaseValue,e.Currency).Subtract(ExactMoney.Parse(byId[e.Id].Value,e.Currency))).ToArray();
                return new{row.Id,scenarioValues=exposures.Select(e=>byId[e.Id].Value),losses=losses.Select(v=>v.ToExactString())};
            })}},RiskJobs.Json)!.AsObject();
        historical["comparison"]=comparison;report["historical"]=historical;
        if(System.Text.Encoding.UTF8.GetByteCount(report.ToJsonString(RiskJobs.Json))>30_000_000)
            throw new MarketDataException("risk_result_limit","Reduce the explicit historical scope or window: the complete report exceeds 30 MB.");
        ct.ThrowIfCancellationRequested();return report;
    }

    // The assistant's bounded historical reads (assistant-workspace.md): the Risk page's calculation, projected to exact and displayed
    // estimates, allocations and the scenarios carrying the tail, then one scenario's factor moves and holding losses.
    private async Task<(Prepared Prepared,ImmutableArray<PreviewData> Markets)> PrepareSummary(NpgsqlConnection connection,PreviewData data,
        InputFrame frame,PortfolioHistoricalRequest? body,CancellationToken ct)
    {
        var history=await CapturedHistory(connection,data,frame,body?.History,"risk_historical_scope",ct);
        var prepared=await Prepare(connection,data,frame,HistoricalRequest(body! with{History=history}),ct);
        return (prepared,[..prepared.Portfolio.Markets.Select(m=>m.Data)]);
    }
    private static Dictionary<DateTimeOffset,DateOnly> SessionDates(AlignedFactorHistory history)=>history.Sessions.ToDictionary(s=>s.Close,s=>s.Date);
    // Exact report text is a plain decimal or a reduced "n/d".
    private static SignedRatio Exact(string text)=>text.Split('/') is [var n,var d]&&BigInteger.TryParse(n,CultureInfo.InvariantCulture,out var numerator)&&
        BigInteger.TryParse(d,CultureInfo.InvariantCulture,out var denominator)&&denominator>0?new(numerator,denominator):
        SignedRatio.TryParse(text,out var value)?value:throw new MarketDataException("risk_historical_numeric","Expected exact historical output.");
    public async Task<object> HistoricalSummary(NpgsqlConnection connection,PreviewData data,InputFrame frame,PortfolioHistoricalRequest body,CancellationToken ct)
    {
        var (prepared,markets)=await PrepareSummary(connection,data,frame,body,ct);
        var run=EvaluateHistorical(prepared,ct);var manifest=run.History;var portfolio=prepared.Portfolio;var dates=SessionDates(manifest);
        var samples=manifest.Samples.ToDictionary(x=>x.Id,StringComparer.Ordinal);
        var evidence=portfolio.Positions.ToDictionary(p=>(p.TradeId,p.BindingId));
        var studyId=HistoricalStudyId(prepared,run);
        var comparison=RunHistoricalComparison(prepared,run,studyId,ct);
        var normal=comparison?["report"] is {} report?report["result"].Deserialize<ParametricRiskReport>(RiskJobs.Json)
            ??throw new MarketDataException("risk_historical_comparison","The matching comparison returned no report."):null;
        return Shown(new{asOf=frame.At,contextId=portfolio.Context.ContextId,historyId=manifest.Id,studyId,run.Result.Available,run.Result.UnavailableReasons,
            policy=run.Spec.Policy,samples=run.Result.Samples,history=new{run.Spec.History.Calendar,run.Spec.History.From,run.Spec.History.To,run.Spec.History.Policy},
            coverage=SummaryCoverage(manifest,portfolio),excludedPositions=portfolio.Excluded,
            books=prepared.Books.Where(b=>prepared.BookIds.Contains(b.Id("id"))).Select(b=>new{id=b.Id("id"),name=b.Text("name")}),
            trades=run.Exposures.GroupBy(e=>e.TradeId).Select(g=>(Exposure:g.First(),Evidence:evidence[(g.Key,g.First().BindingId)]))
                .Select(t=>new{id=t.Exposure.TradeId,reference=t.Evidence.TradeReference,instrumentType=t.Evidence.InstrumentType,bookId=t.Exposure.BookId})
                .OrderBy(t=>t.reference,StringComparer.Ordinal),
            factors=SummaryFactors(manifest,prepared.HistoricalAxes,markets),
            partitions=run.Result.Partitions.Select(p=>new{p.Measure,measureLabel=MeasureLabel(p.Measure),currency=Currency(markets,p.Currency),
                p.BaseValue,p.Mean,p.ValueAtRisk,p.ExpectedShortfall,p.TailSampleMass,p.QuantileTies,
                books=p.Books.Select(b=>new{bookId=b.Id,b.ValueAtRisk,b.ExpectedShortfall}),
                trades=p.Trades.Select(t=>new{tradeId=t.Id,t.ValueAtRisk,t.ExpectedShortfall}),
                // The scenarios carrying quantile or tail weight, largest loss first.
                tail=p.Samples.Where(x=>Exact(x.VarWeight).CompareTo(SignedRatio.Zero)!=0||Exact(x.EsWeight).CompareTo(SignedRatio.Zero)!=0)
                    .OrderByDescending(x=>Exact(x.Loss)).ThenBy(x=>x.Id,StringComparer.Ordinal)
                    .Select(x=>new{sample=x.Id,from=dates[samples[x.Id].From.At],to=dates[samples[x.Id].To.At],loss=x.Loss,x.VarWeight,x.EsWeight})}),
            comparison=comparison is null?null:new{studyId,available=normal is not null,unavailableReason=comparison["unavailableReason"],
                partitions=normal?.Partitions.Select(p=>new{p.Measure,currency=Currency(markets,p.Currency),p.Mean,p.StandardDeviation,p.ValueAtRisk,p.ExpectedShortfall})}});
    }
    // The scenario and the study's availability are checked before any scenario is priced; a requested comparison is not run.
    public async Task<object> HistoricalSample(NpgsqlConnection connection,PreviewData data,InputFrame frame,PortfolioHistoricalSampleRequest body,CancellationToken ct)
    {
        if(body?.Request is not {} request)throw new MarketDataException("risk_historical_scope","Choose the captured history and exact retained holdings.");
        var (prepared,markets)=await PrepareSummary(connection,data,frame,request with{Comparison=null},ct);
        var manifest=prepared.History!;
        var change=manifest.Samples.SingleOrDefault(x=>x.Id==body.Sample)
            ??throw new MarketDataException("risk_historical_sample","Choose a scenario of this captured history.");
        if(!manifest.Available||!prepared.Portfolio.Excluded.IsEmpty)
            throw new MarketDataException("risk_historical_unavailable","Historical risk is unavailable for this study: "+string.Join("; ",
                new[]{manifest.Available?null:"insufficient common history",prepared.Portfolio.Excluded.IsEmpty?null:"incomplete portfolio"}.OfType<string>())+".");
        var run=EvaluateHistorical(prepared,ct);var dates=SessionDates(manifest);
        var values=run.Observations.Single(o=>o.Id==change.Id).Values.ToDictionary(v=>v.ExposureId,v=>v.Value,StringComparer.Ordinal);
        var evidence=prepared.Portfolio.Positions.ToDictionary(p=>(p.TradeId,p.BindingId));
        var days=manifest.DayPrices.IsDefault?null:manifest.DayPrices.ToDictionary(d=>d.At);
        return Shown(new{historyId=manifest.Id,studyId=HistoricalStudyId(prepared,run),sample=change.Id,from=dates[change.From.At],to=dates[change.To.At],
            moves=change.Changes.Select((v,i)=>new{factor=i,from=change.From.Marks[i]!.Value,to=change.To.Marks[i]!.Value,move=v,
                fromPriceType=days?[change.From.At].Choices[i].SelectedType,toPriceType=days?[change.To.At].Choices[i].SelectedType}),
            partitions=run.Result.Partitions.Select(p=>(Partition:p,Loss:p.Samples.Single(x=>x.Id==change.Id).Loss))
                .Select(p=>new{p.Partition.Measure,currency=Currency(markets,p.Partition.Currency),loss=p.Loss}),
            holdings=run.Exposures.Select(e=>
            {
                var loss=ExactMoney.Parse(e.BaseValue,e.Currency).Subtract(ExactMoney.Parse(values[e.Id],e.Currency));var held=evidence[(e.TradeId,e.BindingId)];
                return new{held.TradeReference,held.InstrumentType,e.BookId,e.BindingId,e.Measure,currency=Currency(markets,e.Currency),
                    e.BaseValue,scenarioValue=values[e.Id],loss=loss.ToExactString()};
            })});
    }
}
