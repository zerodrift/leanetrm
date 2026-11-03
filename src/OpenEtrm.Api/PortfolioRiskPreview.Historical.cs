using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HistoricalParametricComparison(ParametricRiskPolicy Policy,ParametricFiniteDifference FiniteDifference);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioHistoricalRequest(PortfolioHistoryRequest History,HistoricalRiskPolicy Policy,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] HistoricalParametricComparison? Comparison=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<CurveRiskAxisRequest> Factors=default);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitHistoricalRiskJob(Guid RequestId,PortfolioHistoricalRequest Request);

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

    private static object CalculateHistorical(Prepared prepared,CancellationToken ct)
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
        var report=JsonSerializer.SerializeToNode(PresentSensitivity(prepared with{Bumps=[]},nominal,before),RiskJobs.Json)!.AsObject();
        report.Remove("factors");
        var studyId=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{
            prepared.Portfolio.Context,prepared.Portfolio.SnapshotId,prepared.Portfolio.Policy,prepared.Portfolio.Positions,
            prepared.Portfolio.ConnectedMarkets,prepared.Portfolio.OptionCalibrations,history.Id,spec.Policy.Horizon,spec.Policy.ValuationPolicy,spec.Policy.Confidence},RiskJobs.Json)));
        JsonNode? comparison=null;
        if(spec.Comparison is {} selected&&eligible)
        {
            var normal=new PortfolioParametricRequest(spec.History,selected.Policy,selected.FiniteDifference);
            var normalPrepared=prepared with{Request=StatisticalRequest(normal),Detail=RiskDetail.Books,Bumps=prepared.HistoricalComparisonBumps,Joint=null,HistoricalAxes=[]};
            var compared=HistoricalComparison(()=>CalculateParametric(normalPrepared,ct),ct);
            compared["studyId"]=studyId;compared["request"]=JsonSerializer.SerializeToNode(selected,RiskJobs.Json);comparison=compared;
            if(compared["work"] is {} work)
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
                evidence[(e.TradeId,e.BindingId)].InstrumentType,displayBase=Displayed(ExactMoney.Parse(e.BaseValue,e.Currency))}),
            partitions=result.Partitions.Select(p=>new{p.Measure,measureLabel=MeasureLabel(p.Measure),currencyReference=p.Currency,currency=Currency(markets,p.Currency),
                display=new{@base=Displayed(ExactMoney.Parse(p.BaseValue,p.Currency)),mean=Displayed(ExactMoney.Parse(p.Mean,p.Currency)),
                    valueAtRisk=Displayed(ExactMoney.Parse(p.ValueAtRisk,p.Currency)),expectedShortfall=Displayed(ExactMoney.Parse(p.ExpectedShortfall,p.Currency))},
                samples=p.Samples.Select(s=>new{s.Id,displayLoss=Displayed(ExactMoney.Parse(s.Loss,p.Currency))}),
                books=p.Books.Select(c=>new{c.Id,valueAtRisk=Displayed(ExactMoney.Parse(c.ValueAtRisk,p.Currency)),expectedShortfall=Displayed(ExactMoney.Parse(c.ExpectedShortfall,p.Currency))}),
                trades=p.Trades.Select(c=>new{c.Id,valueAtRisk=Displayed(ExactMoney.Parse(c.ValueAtRisk,p.Currency)),expectedShortfall=Displayed(ExactMoney.Parse(c.ExpectedShortfall,p.Currency))})}),
            matrix=new{columns=exposures.Select(e=>e.Id),rows=observations.Select(row=>
            {
                var byId=row.Values.ToDictionary(v=>v.ExposureId,StringComparer.Ordinal);
                var losses=exposures.Select(e=>ExactMoney.Parse(e.BaseValue,e.Currency).Subtract(ExactMoney.Parse(byId[e.Id].Value,e.Currency))).ToArray();
                return new{row.Id,values=exposures.Select(e=>byId[e.Id].Value),losses=losses.Select(v=>v.ToExactString()),displayLosses=losses.Select(Displayed)};
            })}},RiskJobs.Json)!.AsObject();
        historical["comparison"]=comparison;report["historical"]=historical;
        if(System.Text.Encoding.UTF8.GetByteCount(report.ToJsonString(RiskJobs.Json))>30_000_000)
            throw new MarketDataException("risk_result_limit","Reduce the explicit historical scope or window: the complete report exceeds 30 MB.");
        ct.ThrowIfCancellationRequested();return report;
    }
}
