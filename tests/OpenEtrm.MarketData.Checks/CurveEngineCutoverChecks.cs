using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class CurveEngineCutoverChecks
{
    public static void Run()
    {
        var f=new Fixture();var snapshot=f.Snapshot();var model=f.Model();
        var request=new CurveRequest(f.C("local_power"),f.K(5));
        var context=new ResolvedCalculationContext(snapshot.Id,"synthetic-direct-definition-compilation",
            snapshot.ValuationAt,snapshot.ValuationAt,snapshot.ValuationAt,snapshot.MarketCutoff);
        var definitions=model.Compile(snapshot,[request],false,context);
        var point=definitions.Points[(request,InputUse.Variable)];
        var plan=ConnectedCurves.Compile(definitions.Definitions,[point],context,new Visible());
        var scenario=ConnectedScenario.Base(plan);
        var direct=definitions.Definitions.SelectMany(d=>d.Points).Single(p=>p.Expression is ConnectedSource source&&source.Input.Series==f.S(1));
        Equal(scenario.ResolveTarget(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(direct.Key.Curve,direct.Contract!.Key)).Length,1);
        Equal(scenario.Work.EvaluatedNodes,0L);
        Equal(scenario.Price(point).ToUnitPriceExact().Value.ToString(),"29.1");
        Equal(scenario.Work.EvaluatedNodes,9L);
        var build=model.Build(snapshot,[request]);
        Equal(build.Graph.Work.EvaluatedNodes,9L);
        Equal(build.Values.Single().Price.Value.ToString(),"29.1");
        Equal(build.Values.Single().Lineage.Count,6);
        var sideContext=new SideValuationContext(build,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
        Equal(sideContext.ScenarioId!,build.Graph.Id);
        Equal(sideContext.CurveNumericalPolicy,ConnectedCurves.NumericalPolicy);
        Equal(sideContext.ExactPrice(request),build.Graph.Price(definitions.Points[(request,InputUse.Variable)]));
        var gas=definitions.Definitions.SelectMany(d=>d.Points)
            .Single(p=>p.Expression is ConnectedSource s&&s.Input.Series==f.S(1));
        var child=scenario.WithShifts([new(CurveShockCoordinate.MarketQuote,
            new PointCurveTarget(gas.Key.Curve,[gas.Key.PointId]),gas.Unit,ExactDecimal.Parse("0.1"),CurveShift.ParentAdditive)]);
        Equal(child.Price(point).ToUnitPriceExact().Value.ToString(),"29.8");
        Equal(child.Work.EvaluatedNodes,5L);
        Equal(scenario.Price(point).ToUnitPriceExact().Value.ToString(),"29.1");

        // Defining a graph must not evaluate a derived quote. Its domain is
        // checked only when the active scenario requests that exact value.
        var bounded=f.Model(quotes:f.Quotes.Select(q=>q.Key==f.R("power_out")?
            q with{Maximum=ExactDecimal.Parse("20")}:q).ToImmutableArray());
        var deferred=bounded.Compile(snapshot,[request],false,context);
        var deferredPoint=deferred.Points[(request,InputUse.Variable)];
        var deferredPlan=ConnectedCurves.Compile(deferred.Definitions,[deferredPoint],context,new Visible());
        try{deferredPlan.Evaluate(deferredPoint);throw new Exception("A derived quote outside its declared domain was accepted.");}
        catch(MarketDataException error)when(error.Code=="price_domain"){}
        Console.WriteLine("PASS curve cutover: direct compilation, one graph evaluator, exact typed basis and affected-only shocks");
    }
    private static void Equal<T>(T actual,T expected) where T:notnull
    {if(!actual.Equals(expected))throw new Exception($"Expected {expected}; got {actual}.");}
    private sealed class Visible : ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey curve,ResolvedCalculationContext context)=>throw new NotSupportedException();
        public void RequireVisible(CurveKey curve,ResolvedCalculationContext context){}
    }
}
