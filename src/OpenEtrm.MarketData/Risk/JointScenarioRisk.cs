using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Calculation;

namespace OpenEtrm.MarketData.Risk;

public sealed record ConnectedRiskScenario(string Id,string Label,ImmutableArray<CurveShift> Shifts);
public sealed record ConnectedRiskScenarioSet(string Id,string Version,string Convention,ImmutableArray<ConnectedRiskScenario> Scenarios)
{
    public const string Independent="independent_joint_absolute_curve_scenarios_v1";
}
public sealed record ExactScenarioValues(ExactMoney Base,ExactMoney Value,ExactMoney Pnl,ExactMoney Loss);
public sealed record ScenarioBookValue(Guid BookId,ExactScenarioValues Values);
public sealed record ScenarioPositionValue(Guid TradeId,Guid BindingId,Guid BookId,Guid ValuationId,ExactScenarioValues Values);
public sealed record ConnectedScenarioGroupValue(int Group,ImmutableArray<ScenarioBookValue> Totals,
    ImmutableArray<ScenarioPositionValue> Positions,int UnitPricerCalls,int ReusedUnitValues);
public sealed record ConnectedRiskScenarioResult(string Id,ConnectedScenarioReceipt Scenario,
    ImmutableArray<ConnectedScenarioGroupValue> Groups,GraphWork GraphWork);
public sealed record ConnectedScenarioBaseGroup(ValuationMeasure Measure,ImmutableArray<UnitPricingContract> Contracts,
    ImmutableArray<BookCurrencyValue> Values,ImmutableArray<ExactRiskPositionValue> Positions,
    ImmutableArray<BookValueMeasures> Measures,ConnectedRiskWork Work);
public sealed record ConnectedScenarioRiskRun(Guid SnapshotId,string NumericalPolicy,RiskDetail Detail,
    ConnectedRiskScenarioSet Set,ConnectedScenarioReceipt BaseScenario,ImmutableArray<ConnectedScenarioReceipt> BaseLayers,ImmutableArray<ConnectedScenarioBaseGroup> Groups,
    ImmutableArray<ConnectedRiskScenarioResult> Scenarios,ConnectedRiskWork Work,GraphWork GraphWork);

public static partial class RiskEngine
{
    public const int MaxJointScenarios=512,MaxJointScenarioShifts=64,MaxJointResultValues=250000;

    // A complete run is returned only after every sibling has succeeded. Results
    // retain resolved replay evidence, never per-scenario graph/pricing sessions.
    public static ConnectedScenarioRiskRun CalculateScenarios(PreparedConnectedPortfolioRisk prepared,
        ConnectedRiskScenarioSet set,string numericalPolicy,CancellationToken cancellation=default,RiskDetail detail=RiskDetail.Books)
    {
        cancellation.ThrowIfCancellationRequested();
        if(prepared is null)throw Error("instrument_context","Supply the exact prepared connected portfolio.");
        if(numericalPolicy!=ExactNumericalPolicy)throw Error("risk_policy","Select exact rational portfolio repricing explicitly.");
        if(!Enum.IsDefined(detail))throw Error("risk_detail","Choose holding details or book totals explicitly.");
        if(set is null||!ScenarioName(set.Id)||!ScenarioName(set.Version)||set.Convention!=ConnectedRiskScenarioSet.Independent||
            set.Scenarios.IsDefaultOrEmpty||set.Scenarios.Length>MaxJointScenarios||
            set.Scenarios.Any(s=>s is null||!ScenarioName(s.Id)||string.IsNullOrWhiteSpace(s.Label)||s.Label.Length>160||
                s.Shifts.IsDefaultOrEmpty||s.Shifts.Length>MaxJointScenarioShifts)||
            set.Scenarios.Select(s=>s.Id).Distinct(StringComparer.Ordinal).Count()!=set.Scenarios.Length)
            throw Error("risk_scenarios","Supply a versioned named set of one to 512 distinct independent scenarios, each with one to 64 explicit shifts.");
        var calculation=new PortfolioRiskCalculation(prepared.Market,prepared.Portfolios,prepared.Contracts,detail,cancellation);
        var width=calculation.Bases.Sum(b=>(long)b.Totals.Length+(detail==RiskDetail.Holdings?b.Positions.Length:0));
        if(width*(1+set.Scenarios.Length)>MaxJointResultValues)
            throw Error("risk_scenario_capacity","The selected scenario/detail matrix exceeds 250000 retained base and scenario value tuples; reduce the scenario set or select book detail.");
        var results=ImmutableArray.CreateBuilder<ConnectedRiskScenarioResult>(set.Scenarios.Length);
        var graph=default(GraphWork);
        foreach(var sample in set.Scenarios)
        {
            cancellation.ThrowIfCancellationRequested();
            // Every sample starts from the selected parent, never the last sample.
            var scenario=prepared.Scenario.WithShifts(sample.Shifts,cancellation);
            var valuation=prepared.Valuation.WithScenario(scenario);
            var market=ConnectedValuation.WithScenario(prepared.Market,valuation,
                prepared.IntervalBasis?.BindConnected(valuation,prepared.Bindings,cancellation));
            var values=calculation.Evaluate(market,cancellation);
            var groups=prepared.Portfolios.Select((portfolio,n)=>new ConnectedScenarioGroupValue(n,
                calculation.Bases[n].Totals.Select((b,i)=>new ScenarioBookValue(b.BookId,ScenarioValues(b.Value,values[n].Totals[i].Value))).ToImmutableArray(),
                detail==RiskDetail.Books?[]:portfolio.Positions.Select((p,i)=>new ScenarioPositionValue(p.TradeId,p.BindingId,p.BookId,p.ValuationId,
                    ScenarioValues(calculation.Bases[n].Positions[i],values[n].Positions[i]))).ToImmutableArray(),
                values[n].UnitValuations,prepared.Contracts[n].Length-values[n].UnitValuations)).ToImmutableArray();
            var work=scenario.Work;graph=AddGraphWork(graph,work);
            results.Add(new(sample.Id,Receipt(scenario),groups,work));
        }
        var preparation=0;
        var layers=new List<ConnectedScenarioReceipt>();
        for(var current=prepared.Scenario;current is not null;current=current.Parent)
        {
            graph=AddGraphWork(graph,current.Work);layers.Add(Receipt(current));
            if(current.Parent is not null)preparation++;
        }
        layers.Reverse();
        var bases=prepared.Portfolios.Select((p,n)=>new ConnectedScenarioBaseGroup(p.Setup.Measure,prepared.Contracts[n],calculation.Bases[n].Totals,
            BasePositions(p,calculation.Bases[n],detail),calculation.Bases[n].BaseMeasures,ConnectedWork(prepared,calculation,1+set.Scenarios.Length,preparation,[n]))).ToImmutableArray();
        cancellation.ThrowIfCancellationRequested();
        return new(prepared.Valuation.Snapshot.Id,numericalPolicy,detail,set,Receipt(prepared.Scenario),[..layers],bases,results.MoveToImmutable(),
            ConnectedWork(prepared,calculation,1+set.Scenarios.Length,preparation,Enumerable.Range(0,prepared.Portfolios.Length)),graph);
    }
    private static ExactScenarioValues ScenarioValues(ExactMoney basis,ExactMoney value)=>new(basis,value,value.Subtract(basis),basis.Subtract(value));
    private static bool ScenarioName(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=128;
}
