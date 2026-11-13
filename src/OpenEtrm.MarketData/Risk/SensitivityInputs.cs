using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;

namespace OpenEtrm.MarketData.Risk;

public sealed record ScenarioFactorUnit(string Dimension,PriceUnit? Price)
{
    public static ScenarioFactorUnit ForPrice(PriceUnit price)=>new("price",price);
    public static ScenarioFactorUnit AnnualizedVolatility{get;}=new("annualized_decimal_volatility",null);
}
internal readonly record struct SensitivityCoordinate(string Family,int Node,Guid Input);

// Axes produce typed edits. Sampling, PV, differentiation and stability are shared.
public abstract record SensitivityInput
{
    public abstract ExactDecimal Size{get;}
    public abstract ScenarioFactorUnit Unit{get;}
    internal abstract bool Valid{get;}
    internal abstract HashSet<SensitivityCoordinate> Coordinates(PreparedConnectedPortfolioRisk prepared,CancellationToken cancellation);
    internal abstract (ImmutableArray<CurveShift> Prices,ImmutableArray<VolatilityShift> Volatility) Edits(ExactDecimal size);
    public static implicit operator SensitivityInput(ConnectedRiskBump bump)=>new PriceSensitivityInput(bump);
}
public sealed record PriceSensitivityInput(ConnectedRiskBump Bump):SensitivityInput
{
    public override ExactDecimal Size=>Bump.Size;
    public override ScenarioFactorUnit Unit=>ScenarioFactorUnit.ForPrice(Bump.Unit);
    internal override bool Valid=>Bump is not null&&Bump.Target is not null&&Bump.Unit is not null&&Bump.Size.CompareTo(default)>0&&Bump.ConventionId==ConnectedRiskBump.Convention;
    internal override HashSet<SensitivityCoordinate> Coordinates(PreparedConnectedPortfolioRisk prepared,CancellationToken cancellation)=>
        prepared.Scenario.ResolveTarget(Bump.Coordinate,Bump.Target,cancellation).Select(p=>new SensitivityCoordinate("price",prepared.Scenario.Plan.IndexOf(p.Key),Guid.Empty)).ToHashSet();
    internal override (ImmutableArray<CurveShift>,ImmutableArray<VolatilityShift>) Edits(ExactDecimal size)=>
        ([new(Bump.Coordinate,Bump.Target,Bump.Unit,size,CurveShift.ParentAdditive)],[]);
}
public sealed record VolatilityWeight(Guid InputId,ExactDecimal Weight);
public sealed record VolatilitySensitivityInput(ImmutableArray<VolatilityWeight> Targets,ExactDecimal BumpSize,string Policy):SensitivityInput
{
    public const string Convention="weighted_annualized_volatility_axis_v1";
    public override ExactDecimal Size=>BumpSize;
    public override ScenarioFactorUnit Unit=>ScenarioFactorUnit.AnnualizedVolatility;
    internal override bool Valid=>Policy==Convention&&Size.CompareTo(default)>0&&!Targets.IsDefaultOrEmpty&&
        Targets.Length<=RiskEngine.MaxJointScenarioShifts&&Targets.All(t=>t is not null&&t.InputId!=Guid.Empty&&t.Weight!=default)&&
        Targets.Select(t=>t.InputId).Distinct().Count()==Targets.Length;
    internal override HashSet<SensitivityCoordinate> Coordinates(PreparedConnectedPortfolioRisk prepared,CancellationToken cancellation)
    {
        var declared=prepared.Contracts.SelectMany(g=>g).SelectMany(c=>c.Volatility).Select(v=>v.Id).ToHashSet();
        if(Targets.Any(t=>!declared.Contains(t.InputId)))throw new MarketDataException("risk_dependency","The selected volatility axis is outside the declared input closure.");
        cancellation.ThrowIfCancellationRequested();
        return Targets.Select(t=>new SensitivityCoordinate("volatility",0,t.InputId)).ToHashSet();
    }
    internal override (ImmutableArray<CurveShift>,ImmutableArray<VolatilityShift>) Edits(ExactDecimal size)=>
        ([],[..Targets.Select(t=>new VolatilityShift(t.InputId,size*t.Weight,VolatilityShift.ParentAdditive))]);
}
