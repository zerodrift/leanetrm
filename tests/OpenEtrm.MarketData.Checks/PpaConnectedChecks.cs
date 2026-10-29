using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

internal static class PpaConnectedChecks
{
    internal static int Run()
    {
        var f=new PpaAcceptanceFixture();var instrument=f.Physical();var market=f.Market(f.From.AddDays(-1));
        var inputs=f.Prepare(market,[instrument]);var coverage=new DeliveryCoverage(f.From,f.To);
        var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new TradeInstrument(Guid.NewGuid(),instrument,new(ExactDecimal.Parse("10"),instrument.UnitQuantity.Specification),coverage:coverage)]);
        var portfolio=ValuationPortfolio.FromTrades([trade],new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)){Interval=inputs});
        var dependencies=portfolio.Declare(market.Snapshot).SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
        var export=market.Model.Compile(market.Snapshot,dependencies,false,market.Build.Graph.Plan.Context);
        var bindings=dependencies.ToImmutableDictionary(r=>r,r=>export.Points[(r,InputUse.Variable)]);
        var request=dependencies.Single();var original=bindings[request];
        var source=export.Definitions.SelectMany(d=>d.Points).Single(p=>p.Key==original);
        var alias=source with{Key=new(new(f.Definitions,"synthetic_connected_energy"),Guid.NewGuid()),
            Expression=new ConnectedWeightedSum([new(CurvePointReference.Exact(original),ExactDecimal.Parse("1"))])};
        var primitive=(ConnectedSource)source.Expression;var earlier=market.Snapshot.ValuationAt.AddDays(-1);
        var observation=primitive.Observation with{Key=new(f.Definitions.CatalogId,Guid.NewGuid()),ObservedAt=earlier};
        var historical=market.Snapshot with{Id=Guid.NewGuid(),ValuationAt=earlier,MarketCutoff=earlier,Selections=[new(primitive.Input,observation)]};
        var historyCurve=new CurveKey(f.Definitions,"synthetic_historical_energy");
        var history=source with{Key=new(historyCurve,Guid.NewGuid()),Expression=primitive with{
            Request=primitive.Request with{Curve=historyCurve},Observation=observation,Input=primitive.Input with{Use=InputUse.FrozenFact},
            Sample=new(historical.Id,earlier,earlier,historical.SelectionPolicy,InputUse.Variable,true)}};
        var graph=ConnectedScenario.Base(ConnectedCurves.Compile(export.Definitions.Add(new(alias.Key.Curve,[alias])).Add(new(historyCurve,[history])),
            [alias.Key,history.Key],market.Build.Graph.Plan.Context,new Bindings()));
        try
        {
            ConnectedValuation.Prepare(graph,market.Snapshot.WithKnowledge(Guid.NewGuid(),market.Snapshot.Knowledge!.KnownAt),[historical]);
            throw new Exception("A graph cannot admit the same quote pins under a different resolved knowledge context.");
        }
        catch(MarketDataException error)when(error.Code=="instrument_context"){}
        // A mixed graph's historical samples are already admitted. Interval sides
        // must reuse that valuation, including when constructing their contexts.
        var prepared=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(graph,market.Snapshot,[historical]),bindings.SetItem(request,alias.Key),[portfolio],market.Build);
        foreach(var coordinate in new[]{CurveShockCoordinate.MarketQuote,CurveShockCoordinate.ComputedOutput})
        {
            var bump=new ConnectedRiskBump(coordinate,new ContractCurveTarget(coordinate==CurveShockCoordinate.MarketQuote?request.Curve:alias.Key.Curve,request.Contract),f.EnergyPrice,ExactDecimal.Parse("1"),ConnectedRiskBump.Convention);
            var run=RiskEngine.CalculateExact(prepared,[bump],RiskEngine.ExactNumericalPolicy);
            if(run.Work.RowEvaluations!=4*721||run.Work.ScheduleGraphEvaluatedNodes<=0||run.Work.ScheduleGraphReusedReads<=0)
                throw new Exception("Base values need both side schedules; each price shock must evaluate only the affected monthly side and report its graph reuse.");
            var value=run.Groups.Single().Factors.Single().Positions.Single().Values;
            // November has 721 actual hours. (42-40)*721*10, then +/-1*721*10.
            if(value.Base.ToExactString()!="14420"||value.UpPnl.ToExactString()!="7210"||value.DownPnl.ToExactString()!="-7210"||value.Gamma.Value.ToExactString()!="0")
                throw new Exception("PPA must price and shock its independent schedules under the shared connected scenario.");
            var child=RiskEngine.WithShifts(prepared,[new(coordinate,bump.Target,bump.Unit,ExactDecimal.Parse("2"),CurveShift.ParentAdditive)]);
            var stressed=RiskEngine.CalculateExact(child,[bump],RiskEngine.ExactNumericalPolicy).Groups.Single().Factors.Single().Positions.Single().Values;
            if(stressed.Base.ToExactString()!="28840"||stressed.UpPnl!=value.UpPnl)throw new Exception("Nested PPA scenarios lost their parent or quantity basis.");
        }
        Console.WriteLine("PASS PPA connected quote/output shocks, nested scenarios, native hours and one-time trade scaling");return 1;
    }
    private sealed class Bindings:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext context)=>throw new Exception("Exact references required.");
        public void RequireVisible(CurveKey key,ResolvedCalculationContext context){}
    }
}
