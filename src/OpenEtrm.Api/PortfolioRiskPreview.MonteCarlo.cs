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
public sealed record PortfolioMonteCarloRequest(PortfolioHistoryRequest History,MonteCarloRiskPolicy Policy,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<CurveRiskAxisRequest> Factors=default);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitMonteCarloRiskJob(Guid RequestId,PortfolioMonteCarloRequest Request);

public sealed partial class PortfolioRiskPreview
{
    internal static int MonteCarloPathCapacity(long width,long rows,long nodes,int factors)
    {
        var capacity=(long)MonteCarloRisk.MaximumPaths;
        if(width>0)capacity=Math.Min(capacity,RiskEngine.MaxJointResultValues/width-1);
        if(rows>0)capacity=Math.Min(capacity,CloseRiskAdmission.MaximumRowScenarios/rows-1);
        if(nodes>0)capacity=Math.Min(capacity,PortfolioJointCalculation.MaximumOptionTreeNodes/nodes-1);
        if(factors>0)capacity=Math.Min(capacity,MonteCarloRisk.MaximumShockValues/factors);
        return (int)Math.Max(0,capacity);
    }
    private static object MonteCarloCapabilities(PortfolioValuation.Prepared portfolio)
    {
        var width=portfolio.Markets.Sum(m=>RiskEngine.ScenarioResultWidth(m.Connected,RiskDetail.Holdings));
        var nodes=portfolio.Options?.Bindings.Values.Where(b=>b.Receipt.Mode==OpenEtrm.MarketData.Trading.FuturesOptionPricing.SolvedMode)
            .Sum(b=>checked((long)b.Policy.Steps*(b.Policy.Steps+1)/2))??0;
        var capacity=MonteCarloPathCapacity(width,portfolio.Ppa?.Rows??0,nodes,portfolio.Factors.Length);
        return new{
        profile=new{convention=MonteCarloRiskPolicy.Version1,numericalPolicy=MonteCarloRiskPolicy.Numerics,
            estimator=ParametricRiskPolicy.Sample,centering=ParametricRiskPolicy.Centered,horizon=ParametricRiskPolicy.Session,
            quantilePolicy=HistoricalRiskPolicy.Quantile,tailPolicy=HistoricalRiskPolicy.Tail,valuationPolicy=HistoricalRiskPolicy.Valuation,
            distribution=MonteCarloRiskPolicy.Gaussian,randomGenerator=MonteCarloRiskPolicy.Generator,shockPolicy=MonteCarloRiskPolicy.ShockRounding,
            samplingUncertainty=MonteCarloRiskPolicy.Uncertainty},
        defaults=new{confidence="0.99",paths=Math.Max(2,Math.Min(4096,capacity)),seed="1",drift=ParametricRiskPolicy.Zero},
        drifts=new{zero=ParametricRiskPolicy.Zero,sample=ParametricRiskPolicy.Mean},
        limits=new{maxFactors=MonteCarloRisk.MaximumFactors,maxPaths=MonteCarloRisk.MaximumPaths,scopeMaxPaths=capacity,maxShockValues=MonteCarloRisk.MaximumShockValues,
            maxProducts=ParametricRisk.MaximumProducts,maxExposures=HistoricalRisk.MaximumExposures,maxResultValues=HistoricalRisk.MaximumValues}};
    }
    internal static PortfolioRiskRequest MonteCarloRequest(PortfolioMonteCarloRequest body)
    {
        if(body?.History?.Scope?.Context is not {} context)throw new MarketDataException("risk_monte_carlo_scope","Choose the captured history and retained holdings.");
        return new(context,body.History.Scope.BookIds,body.History.Scope.PortfolioIds,[],Convention,RiskEngine.ExactNumericalPolicy,
            body.History.PricingPolicy,"holdings",[],body.History.Scope.TradeIds,MonteCarlo:body);
    }
    internal static void ValidateMonteCarlo(PortfolioRiskRequest outer,PortfolioMonteCarloRequest body)
    {
        MonteCarloRisk.ValidatePolicy(body.Policy);
        if(body.History?.Scope?.Context is not {} context||context!=outer.Context||body.History.ExpectedManifestId?.Length!=64||
            JsonSerializer.Serialize(body.History.PricingPolicy,RiskJobs.Json)!=JsonSerializer.Serialize(outer.PricingPolicy,RiskJobs.Json)||
            outer.Parametric is not null||outer.Historical is not null||outer.Joint is not null||outer.Detail!="holdings"||
            outer.Bumps.IsDefault||!outer.Bumps.IsEmpty||outer.Stress.IsDefault||!outer.Stress.IsEmpty||
            !body.Factors.IsDefault&&(body.Factors.Length is <1 or >MonteCarloRisk.MaximumFactors||body.Factors.Any(f=>f is null))||
            (outer.TradeIds is {} a?string.Join(',',a.Order()):null)!=(body.History.Scope.TradeIds is {} b?string.Join(',',b.Order()):null))
            throw new MarketDataException("risk_monte_carlo_scope","Monte Carlo requires one complete captured holdings scope and an exclusive unstressed full-PV method envelope.");
    }
    private static void RequireMonteCarloWork(PortfolioValuation.Prepared portfolio,AlignedFactorHistory history,PortfolioMonteCarloRequest request)
    {
        ParametricRisk.Admit(history.Columns.Length,history.Samples.Length);
        var holdings=HistoricalHoldings(portfolio);
        if(holdings.Length is <1 or >HistoricalRisk.MaximumExposures||holdings.Distinct().Count()!=holdings.Length)
            throw new MarketDataException("risk_monte_carlo_exposures","The complete native holding set requires 1–4096 distinct identities.");
        var scenarios=history.Available&&portfolio.Excluded.IsEmpty?1L+request.Policy.Paths:1;
        var width=portfolio.Markets.Sum(m=>RiskEngine.ScenarioResultWidth(m.Connected,RiskDetail.Holdings));
        var rows=portfolio.Ppa is {} ppa?checked(ppa.Rows*scenarios):0;
        var nodes=portfolio.Options?.Bindings.Values.Where(b=>b.Receipt.Mode==OpenEtrm.MarketData.Trading.FuturesOptionPricing.SolvedMode)
            .Sum(b=>checked((long)b.Policy.Steps*(b.Policy.Steps+1)/2*scenarios))??0;
        RiskValuationAdmission.Require(rows,nodes,checked(width*scenarios),history.Columns.Length,1);
    }
    private static PortfolioJointRisk.Resolved ResolveMonteCarlo(MonteCarloGeneration generation,ImmutableArray<PortfolioRiskAxis> axes,CancellationToken ct)
    {
        var samples=generation.Samples.Select(row=>
        {
            ct.ThrowIfCancellationRequested();
            return new PortfolioJointRisk.Sample(row.Id,"Simulation "+(row.Index+1),
                [..axes.Select((axis,i)=>new PortfolioCurveShift(axis,row.Changes[i])).Where(s=>s.Size!=default)]);
        }).ToImmutableArray();
        var id=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{generation.HistoryId,generation.Policy},RiskJobs.Json)));
        var set=new PortfolioScenarioSetRequest(id,MonteCarloRiskPolicy.Version1,ConnectedRiskScenarioSet.Independent,
            [..samples.Select(row=>new PortfolioScenarioRequest(row.Id,row.Label,[..row.Shifts.Select(s=>new CurveShiftRequest(s.Axis.Request,s.Size.ToString(),CurveShift.ParentAdditive))]))]);
        return new(new(PortfolioJointRiskRequest.Version1,set,null),samples,[],[]);
    }
    private static object CalculateMonteCarlo(Prepared prepared,CancellationToken ct)
    {
        var spec=prepared.Request.MonteCarlo!;var history=prepared.History!;var generation=prepared.Simulation!;
        var eligible=generation.Available&&prepared.Portfolio.Excluded.IsEmpty;var before=prepared.Portfolio.Options?.TreePrices??0;
        var values=eligible?EvaluateFullPvSamples(prepared,MonteCarloRiskPolicy.Version1,ct):
            new FullPvSamples([],[],PortfolioValuation.Calculate(prepared.Portfolio,[],[],RiskDetail.Holdings,ct));
        var result=eligible?MonteCarloRisk.Calculate(generation,values.Exposures,values.Observations,ct):
            new MonteCarloRiskReport(spec.Policy,history.Id,0,[],[],false,
                [..new[]{generation.Available?null:"insufficient_common_history",prepared.Portfolio.Excluded.IsEmpty?null:"incomplete_portfolio"}.OfType<string>()]);
        var report=JsonSerializer.SerializeToNode(PresentSensitivity(prepared with{Bumps=[]},values.Nominal,before),RiskJobs.Json)!.AsObject();report.Remove("factors");
        var markets=prepared.Portfolio.Markets.Select(m=>m.Data).ToImmutableArray();
        var evidence=prepared.Portfolio.Positions.ToDictionary(p=>(p.TradeId,p.BindingId));
        var studyId=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{
            prepared.Portfolio.Context,prepared.Portfolio.SnapshotId,prepared.Portfolio.Policy,prepared.Portfolio.Positions,
            prepared.Portfolio.ConnectedMarkets,prepared.Portfolio.OptionCalibrations,history.Id,simulationPolicy=spec.Policy},RiskJobs.Json)));
        report["monteCarlo"]=JsonSerializer.SerializeToNode(new{studyId,request=spec,history,generation,result,
            factors=history.Columns.Select((c,i)=>new{c.Id,factor=Factor(markets,prepared.HistoricalAxes[i])}),
            books=prepared.Books.Where(b=>prepared.BookIds.Contains(b.Id("id"))).Select(b=>new{id=b.Id("id"),name=b.Text("name")}),
            exposures=values.Exposures.Select(e=>new{e.Id,e.BookId,e.TradeId,e.BindingId,e.Measure,e.Currency,e.BaseValue,
                evidence[(e.TradeId,e.BindingId)].TradeReference,evidence[(e.TradeId,e.BindingId)].InstrumentReference,evidence[(e.TradeId,e.BindingId)].InstrumentType}),
            partitions=result.Partitions.Select(p=>new{p.Measure,measureLabel=MeasureLabel(p.Measure),currencyReference=p.Currency,currency=Currency(markets,p.Currency)}),
            matrix=new{columns=values.Exposures.Select(e=>e.Id),rows=values.Observations.Select(row=>
            {
                var byId=row.Values.ToDictionary(v=>v.ExposureId,StringComparer.Ordinal);
                return new{row.Id,scenarioValues=values.Exposures.Select(e=>byId[e.Id].Value),
                    losses=values.Exposures.Select(e=>ExactMoney.Parse(e.BaseValue,e.Currency).Subtract(ExactMoney.Parse(byId[e.Id].Value,e.Currency)).ToExactString())};
            })}},RiskJobs.Json);
        if(System.Text.Encoding.UTF8.GetByteCount(report.ToJsonString(RiskJobs.Json))>30_000_000)
            throw new MarketDataException("risk_result_limit","Reduce the simulation count or scope: the complete report exceeds 30 MB.");
        ct.ThrowIfCancellationRequested();return report;
    }
}
