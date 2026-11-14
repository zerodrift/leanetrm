using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Calculation;

namespace OpenEtrm.MarketData.Risk;

public sealed record FiniteDifferenceTolerance(ExactDecimal Absolute,ExactDecimal Relative);
public sealed record ConnectedSensitivityAxis(string Id,SensitivityInput Bump,
    FiniteDifferenceTolerance DeltaTolerance,FiniteDifferenceTolerance GammaTolerance);
public sealed record ConnectedMixedSensitivity(string Id,string FirstAxis,string SecondAxis,FiniteDifferenceTolerance Tolerance);
public sealed record ConnectedFiniteDifferenceRequest(string Convention,ImmutableArray<ConnectedSensitivityAxis> Axes,
    ImmutableArray<ConnectedMixedSensitivity> Mixed)
{
    public ImmutableArray<ConnectedRiskScenario> AdditionalScenarios{get;init;}=[];
    public const string HalfNominalDouble="exact_central_half_nominal_double_v1";
    public int SampleCount=>CountSamples(Axes.Length,Mixed.Length,AdditionalScenarios.Length);
    public static int CountSamples(int axes,int mixed,int additional=0)
    {
        if(axes<0||axes>RiskEngine.MaxSensitivityAxes||mixed<0||mixed>RiskEngine.MaxMixedSensitivities||additional<0||additional>RiskEngine.MaxJointScenarios)
            throw new MarketDataException("risk_sensitivity","Choose bounded explicit axes, mixed pairs and additional scenarios.");
        return checked(6*axes+12*mixed+additional);
    }
}
public sealed record ExactFiniteDifferenceEstimate(ExactMoney Half,ExactMoney Nominal,ExactMoney Double,bool Stable);
public sealed record DerivativeBookValue(Guid BookId,ExactFiniteDifferenceEstimate Estimate);
public sealed record DerivativePositionValue(Guid TradeId,Guid BindingId,Guid BookId,Guid ValuationId,ExactFiniteDifferenceEstimate Estimate)
{public AccountingOwnerKey Owner{get;init;}=new(AccountingOwnerKind.Trade,TradeId);}
public sealed record ConnectedDerivativeGroup(int Group,ImmutableArray<DerivativeBookValue> Totals,ImmutableArray<DerivativePositionValue> Positions);
public sealed record ConnectedDerivativeResult(string Id,string Formula,ImmutableArray<string> Axes,ImmutableArray<ScenarioFactorUnit> FactorUnits,
    ImmutableArray<ConnectedDerivativeGroup> Groups);
public sealed record ConnectedFiniteDifferenceRun(ConnectedFiniteDifferenceRequest Request,ConnectedScenarioRiskRun Repricing,
    ImmutableArray<ConnectedDerivativeResult> Derivatives);

public static partial class RiskEngine
{
    public const int MaxSensitivityAxes=16,MaxMixedSensitivities=16;
    public const string CentralDelta="central_first_difference_v1",CentralGamma="central_diagonal_second_difference_v1",
        CentralMixedGamma="central_mixed_second_difference_v1";

    public static ConnectedFiniteDifferenceRun CalculateFiniteDifferences(PreparedConnectedPortfolioRisk prepared,
        ConnectedFiniteDifferenceRequest request,string numericalPolicy,CancellationToken cancellation=default,RiskDetail detail=RiskDetail.Books)
    {
        cancellation.ThrowIfCancellationRequested();
        if(prepared is null)throw Error("instrument_context","Supply the exact prepared connected portfolio.");
        if(request is null||request.Convention!=ConnectedFiniteDifferenceRequest.HalfNominalDouble||
            request.AdditionalScenarios.IsDefault||request.Axes.IsDefault||request.Axes.IsEmpty&&request.AdditionalScenarios.IsEmpty||request.Axes.Length>MaxSensitivityAxes||request.Mixed.IsDefault||request.Mixed.Length>MaxMixedSensitivities||
            request.Axes.Any(a=>a is null||!ScenarioName(a.Id)||a.Bump is null||!a.Bump.Valid||!ValidTolerance(a.DeltaTolerance)||!ValidTolerance(a.GammaTolerance))||
            request.Axes.Select(a=>a.Id).Distinct(StringComparer.Ordinal).Count()!=request.Axes.Length||
            request.Mixed.Any(p=>p is null||!ScenarioName(p.Id)||!ValidTolerance(p.Tolerance))||
            request.Mixed.Select(p=>p.Id).Distinct(StringComparer.Ordinal).Count()!=request.Mixed.Length)
            throw Error("risk_sensitivity","Select explicit symmetric axes or additional named scenarios, up to 16 mixed pairs and nonnegative absolute/relative tolerances.");
        if(!request.Axes.IsEmpty||!request.Mixed.IsEmpty)RequireStaticDerivatives(prepared.UnitContracts);
        var axes=request.Axes;var pairs=request.Mixed;
        var axisIndex=axes.Select((a,n)=>(a.Id,n)).ToDictionary(a=>a.Id,a=>a.n,StringComparer.Ordinal);
        var nodes=axes.Select(a=>a.Bump.Coordinates(prepared,cancellation)).ToArray();
        var pairAxes=pairs.Select(p=>
        {
            if(!axisIndex.TryGetValue(p.FirstAxis??"",out var first)||!axisIndex.TryGetValue(p.SecondAxis??"",out var second)||
                first==second||nodes[first].Overlaps(nodes[second]))
                throw Error("risk_sensitivity","A mixed derivative requires two selected axes with disjoint resolved coordinates.");
            return(First:first,Second:second);
        }).ToArray();
        if(pairAxes.Select(p=>p.First<p.Second?p:(p.Second,p.First)).Distinct().Count()!=pairAxes.Length)
            throw Error("risk_sensitivity","Select each unordered mixed pair only once.");
        var two=ExactDecimal.Parse("2");var four=ExactDecimal.Parse("4");
        var steps=axes.Select(a=>new[]{a.Bump.Size.DivideExact(two),a.Bump.Size,a.Bump.Size*two}).ToArray();
        var samples=ImmutableArray.CreateBuilder<ConnectedRiskScenario>(request.SampleCount);
        var singleSamples=new int[axes.Length,3,2];var mixedSamples=new int[pairs.Length,3,4];
        (ImmutableArray<CurveShift> Prices,ImmutableArray<VolatilityShift> Volatility) Shift(int axis,int step,bool positive)=>
            axes[axis].Bump.Edits(positive?steps[axis][step]:-steps[axis][step]);
        int Add(string id,params (ImmutableArray<CurveShift> Prices,ImmutableArray<VolatilityShift> Volatility)[] edits)
        {
            var index=samples.Count;
            samples.Add(new(id,id,[..edits.SelectMany(e=>e.Prices)]){Volatility=[..edits.SelectMany(e=>e.Volatility)]});
            return index;
        }
        for(var axis=0;axis<axes.Length;axis++)
            for(var step=0;step<3;step++)
                for(var sign=0;sign<2;sign++)
                    singleSamples[axis,step,sign]=Add($"axis/{axis}/{step}/{sign}",Shift(axis,step,sign==0));
        for(var pair=0;pair<pairs.Length;pair++)
            for(var step=0;step<3;step++)
                for(var corner=0;corner<4;corner++)
                    mixedSamples[pair,step,corner]=Add($"mixed/{pair}/{step}/{corner}",
                        Shift(pairAxes[pair].First,step,corner<2),Shift(pairAxes[pair].Second,step,corner%2==0));
        samples.AddRange(request.AdditionalScenarios);
        var run=CalculateScenarios(prepared,new("finite_differences",ConnectedFiniteDifferenceRequest.HalfNominalDouble,
            ConnectedRiskScenarioSet.Independent,samples.ToImmutable()),numericalPolicy,cancellation,detail);
        var derivatives=ImmutableArray.CreateBuilder<ConnectedDerivativeResult>();
        ConnectedDerivativeResult Measure(string id,string formula,int first,int second,int pair,FiniteDifferenceTolerance tolerance)
        {
            ExactFiniteDifferenceEstimate Estimate(int group,int index,bool position)
            {
                ExactMoney Value(int sample)=>position?run.Scenarios[sample].Groups[group].Positions[index].Values.Value:
                    run.Scenarios[sample].Groups[group].Totals[index].Values.Value;
                var basis=position?run.Groups[group].Positions[index].Value:run.Groups[group].Values[index].Value;
                var estimates=new ExactMoney[3];
                for(var step=0;step<3;step++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if(formula==CentralMixedGamma)
                        estimates[step]=Value(mixedSamples[pair,step,0]).Subtract(Value(mixedSamples[pair,step,1]))
                            .Subtract(Value(mixedSamples[pair,step,2])).Add(Value(mixedSamples[pair,step,3]))
                            .DivideBy(four).DivideBy(steps[first][step]).DivideBy(steps[second][step]);
                    else
                    {
                        var up=Value(singleSamples[first,step,0]);var down=Value(singleSamples[first,step,1]);
                        estimates[step]=formula==CentralDelta?up.Subtract(down).DivideBy(two).DivideBy(steps[first][step]):
                            up.Add(down).Subtract(basis.Scale(two)).DivideBy(steps[first][step]).DivideBy(steps[first][step]);
                    }
                }
                return AssessFiniteDifference(estimates[0],estimates[1],estimates[2],tolerance);
            }
            var groups=run.Groups.Select((g,n)=>new ConnectedDerivativeGroup(n,
                g.Values.Select((v,i)=>new DerivativeBookValue(v.BookId,Estimate(n,i,false))).ToImmutableArray(),
                g.Positions.Select((v,i)=>new DerivativePositionValue(v.TradeId,v.BindingId,v.BookId,v.ValuationId,Estimate(n,i,true)){Owner=v.Owner}).ToImmutableArray())).ToImmutableArray();
            return new(id,formula,second<0?[axes[first].Id]:[axes[first].Id,axes[second].Id],
                formula==CentralDelta?[axes[first].Bump.Unit]:[axes[first].Bump.Unit,axes[second<0?first:second].Bump.Unit],groups);
        }
        for(var axis=0;axis<axes.Length;axis++)
        {
            derivatives.Add(Measure(axes[axis].Id,CentralDelta,axis,-1,-1,axes[axis].DeltaTolerance));
            derivatives.Add(Measure(axes[axis].Id,CentralGamma,axis,-1,-1,axes[axis].GammaTolerance));
        }
        for(var pair=0;pair<pairs.Length;pair++)
            derivatives.Add(Measure(pairs[pair].Id,CentralMixedGamma,pairAxes[pair].First,pairAxes[pair].Second,pair,pairs[pair].Tolerance));
        cancellation.ThrowIfCancellationRequested();return new(request,run,derivatives.ToImmutable());
    }
    private static bool ValidTolerance(FiniteDifferenceTolerance? tolerance)=>tolerance is not null&&
        tolerance.Absolute.CompareTo(default)>=0&&tolerance.Relative.CompareTo(default)>=0;
    public static ExactFiniteDifferenceEstimate AssessFiniteDifference(ExactMoney half,ExactMoney nominal,ExactMoney twice,FiniteDifferenceTolerance tolerance)
    {
        if(!ValidTolerance(tolerance))throw Error("risk_sensitivity","Select explicit nonnegative absolute and relative tolerances.");
        var halfAgrees=WithinTolerance(half,nominal,tolerance);var doubleAgrees=WithinTolerance(twice,nominal,tolerance);
        return new(half,nominal,twice,halfAgrees&&doubleAgrees);
    }
    private static bool WithinTolerance(ExactMoney estimate,ExactMoney nominal,FiniteDifferenceTolerance tolerance)
    {
        ExactMoney Abs(ExactMoney amount)=>amount.CompareTo(ExactMoney.FromMoney(new(default,amount.Currency)))<0?amount.Scale(ExactDecimal.Parse("-1")):amount;
        var limit=ExactMoney.FromMoney(new(tolerance.Absolute,nominal.Currency)).Add(Abs(nominal).Scale(tolerance.Relative));
        return Abs(estimate.Subtract(nominal)).CompareTo(limit)<=0;
    }
}
