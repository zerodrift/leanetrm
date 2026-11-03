using System.Collections.Immutable;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioScenarioRequest(string Id,string Label,ImmutableArray<CurveShiftRequest> Shifts);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioScenarioSetRequest(string Id,string Version,string Convention,ImmutableArray<PortfolioScenarioRequest> Scenarios);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RiskToleranceRequest(string Absolute,string Relative);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioSensitivityAxisRequest(string Id,CurveBumpRequest Bump,RiskToleranceRequest DeltaTolerance,RiskToleranceRequest GammaTolerance);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioMixedSensitivityRequest(string Id,string FirstAxis,string SecondAxis,RiskToleranceRequest Tolerance);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioFiniteDifferenceRequest(string Convention,ImmutableArray<PortfolioSensitivityAxisRequest> Axes,ImmutableArray<PortfolioMixedSensitivityRequest> Mixed);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioJointRiskRequest(string Convention,PortfolioScenarioSetRequest? Scenarios,PortfolioFiniteDifferenceRequest? Derivatives)
{
    public const string Version1="portfolio_joint_risk_v1";
}

internal static class PortfolioJointRisk
{
    internal const int MaxScenarios=32,MaxShifts=16;
    internal static ExactFiniteDifferenceEstimate SumEstimates(IEnumerable<ExactFiniteDifferenceEstimate> estimates,FiniteDifferenceTolerance tolerance)
    {
        var values=estimates.ToArray();
        if(values.Length==0)throw Invalid();
        return RiskEngine.AssessFiniteDifference(values.Select(v=>v.Half).Aggregate((a,b)=>a.Add(b)),
            values.Select(v=>v.Nominal).Aggregate((a,b)=>a.Add(b)),values.Select(v=>v.Double).Aggregate((a,b)=>a.Add(b)),tolerance);
    }
    internal sealed record Sample(string Id,string Label,ImmutableArray<PortfolioCurveShift> Shifts);
    internal sealed record Axis(string Id,PortfolioCurveBump Bump,FiniteDifferenceTolerance DeltaTolerance,FiniteDifferenceTolerance GammaTolerance);
    internal sealed record Resolved(PortfolioJointRiskRequest Request,ImmutableArray<Sample> Samples,ImmutableArray<Axis> Axes,
        ImmutableArray<ConnectedMixedSensitivity> Mixed)
    {
        internal bool Scenarios=>Request.Scenarios is not null;
        internal int ChildCount=>Scenarios?Samples.Length:6*Axes.Length+12*Mixed.Length;
        internal IEnumerable<PortfolioRiskAxis> Factors=>Samples.SelectMany(s=>s.Shifts).Select(s=>s.Axis).Concat(Axes.Select(a=>a.Bump.Axis));
    }
    private static MarketDataException Invalid()=>new("risk_joint_contract","Choose one versioned named scenario set or selected derivative request with explicit bounded shifts, axes and nonnegative tolerances.");
    private static bool Name(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=128;
    private static ExactDecimal Number(string? value)
    {
        try{return ExactDecimal.Parse(value??"");}
        catch(Exception error)when(error is MarketDataException or FormatException or OverflowException or ArgumentException){throw Invalid();}
    }
    private static FiniteDifferenceTolerance Tolerance(RiskToleranceRequest? value)
    {
        if(value is null)throw Invalid();
        var absolute=Number(value.Absolute);var relative=Number(value.Relative);
        if(absolute.CompareTo(default)<0||relative.CompareTo(default)<0)throw Invalid();
        return new(absolute,relative);
    }
    internal static void Validate(PortfolioJointRiskRequest request)
    {
        if(request is null||request.Convention!=PortfolioJointRiskRequest.Version1||(request.Scenarios is null)==(request.Derivatives is null))throw Invalid();
        if(request.Scenarios is {} set)
        {
            if(!Name(set.Id)||!Name(set.Version)||set.Convention!=ConnectedRiskScenarioSet.Independent||set.Scenarios.IsDefaultOrEmpty||
                set.Scenarios.Length>MaxScenarios||set.Scenarios.Any(s=>s is null||!Name(s.Id)||string.IsNullOrWhiteSpace(s.Label)||s.Label.Length>160||
                    s.Shifts.IsDefaultOrEmpty||s.Shifts.Length>MaxShifts)||set.Scenarios.Select(s=>s.Id).Distinct(StringComparer.Ordinal).Count()!=set.Scenarios.Length)throw Invalid();
            foreach(var shift in set.Scenarios.SelectMany(s=>s.Shifts))
                if(shift?.Factor?.Target is null||shift.Convention!=CurveShift.ParentAdditive||Number(shift.Size)==default)throw Invalid();
        }
        else
        {
            var derivatives=request.Derivatives!;
            if(derivatives.Convention!=ConnectedFiniteDifferenceRequest.HalfNominalDouble||derivatives.Axes.IsDefaultOrEmpty||
                derivatives.Axes.Length>RiskEngine.MaxSensitivityAxes||derivatives.Mixed.IsDefault||derivatives.Mixed.Length>RiskEngine.MaxMixedSensitivities||
                derivatives.Axes.Any(a=>a is null||!Name(a.Id)||a.Bump?.Factor?.Target is null)||
                derivatives.Axes.Select(a=>a.Id).Distinct(StringComparer.Ordinal).Count()!=derivatives.Axes.Length||
                derivatives.Mixed.Any(p=>p is null||!Name(p.Id))||derivatives.Mixed.Select(p=>p.Id).Distinct(StringComparer.Ordinal).Count()!=derivatives.Mixed.Length)throw Invalid();
            foreach(var axis in derivatives.Axes)
            {
                if(axis.Bump.Convention!=ConnectedRiskBump.Convention||Number(axis.Bump.Size).CompareTo(default)<=0)throw Invalid();
                try
                {
                    var step=Number(axis.Bump.Size);var two=ExactDecimal.Parse("2");
                    _=step.DivideExact(two);_=step*two;
                }
                catch(MarketDataException){throw Invalid();}
                _=Tolerance(axis.DeltaTolerance);_=Tolerance(axis.GammaTolerance);
            }
            var ids=derivatives.Axes.Select(a=>a.Id).ToHashSet(StringComparer.Ordinal);
            var pairs=new HashSet<(string,string)>();
            foreach(var pair in derivatives.Mixed)
            {
                _=Tolerance(pair.Tolerance);
                if(pair.FirstAxis==pair.SecondAxis||!ids.Contains(pair.FirstAxis??"")||!ids.Contains(pair.SecondAxis??"")||
                    !pairs.Add(string.CompareOrdinal(pair.FirstAxis,pair.SecondAxis)<0?(pair.FirstAxis!,pair.SecondAxis!):(pair.SecondAxis!,pair.FirstAxis!)))throw Invalid();
            }
        }
    }
    internal static Resolved Resolve(PortfolioValuation.Prepared portfolio,PortfolioJointRiskRequest request,CancellationToken ct)
    {
        Validate(request);
        PortfolioCurveShift Shift(CurveShiftRequest shift)=>new(CurveRiskAxes.Resolve(portfolio,shift.Factor,ct),Number(shift.Size));
        PortfolioCurveBump Bump(CurveBumpRequest bump)=>new(CurveRiskAxes.Resolve(portfolio,bump.Factor,ct),Number(bump.Size));
        RiskToleranceRequest Wire(FiniteDifferenceTolerance tolerance)=>new(tolerance.Absolute.ToString(),tolerance.Relative.ToString());
        if(request.Scenarios is {} set)
        {
            var samples=set.Scenarios.Select(s=>new Sample(s.Id,s.Label,[..s.Shifts.Select(Shift)])).ToImmutableArray();
            foreach(var sample in samples)RequireDisjoint(portfolio,sample.Shifts.Select(x=>x.Axis));
            var canonical=set with{Scenarios=[..samples.Select(s=>new PortfolioScenarioRequest(s.Id,s.Label,
                [..s.Shifts.Select(x=>new CurveShiftRequest(x.Axis.Request,x.Size.ToString(),CurveShift.ParentAdditive))]))]};
            return new(request with{Scenarios=canonical},samples,[],[]);
        }
        var input=request.Derivatives!;
        var axes=input.Axes.Select(a=>new Axis(a.Id,Bump(a.Bump),Tolerance(a.DeltaTolerance),Tolerance(a.GammaTolerance))).ToImmutableArray();
        var mixed=input.Mixed.Select(p=>new ConnectedMixedSensitivity(p.Id,p.FirstAxis,p.SecondAxis,Tolerance(p.Tolerance))).ToImmutableArray();
        var byId=axes.ToDictionary(a=>a.Id,StringComparer.Ordinal);
        if(axes.Select(a=>a.Bump.Axis.Id).Distinct().Count()!=axes.Length)throw Invalid();
        foreach(var pair in mixed)RequireDisjoint(portfolio,[byId[pair.FirstAxis].Bump.Axis,byId[pair.SecondAxis].Bump.Axis]);
        var canonicalDerivatives=input with{Axes=[..axes.Select(a=>new PortfolioSensitivityAxisRequest(a.Id,
            new(a.Bump.Axis.Request,a.Bump.Size.ToString(),ConnectedRiskBump.Convention),Wire(a.DeltaTolerance),Wire(a.GammaTolerance)))],
            Mixed=[..mixed.Select(p=>new PortfolioMixedSensitivityRequest(p.Id,p.FirstAxis,p.SecondAxis,Wire(p.Tolerance)))]};
        return new(request with{Derivatives=canonicalDerivatives},[],axes,mixed);
    }
    private static void RequireDisjoint(PortfolioValuation.Prepared portfolio,IEnumerable<PortfolioRiskAxis> axes)
    {
        var used=new HashSet<(DefinitionRevision,int)>();
        foreach(var axis in axes)
        {
            var plan=portfolio.Markets.Single(m=>m.Snapshot.Definitions==axis.Market).Connected.Scenario.Plan;
            foreach(var node in axis.Points.Select(plan.IndexOf).Distinct())
                if(!used.Add((axis.Market,node)))throw Invalid();
        }
    }
}
