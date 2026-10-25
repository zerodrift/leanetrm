using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Curves;
using OpenEtrm.MarketData.CustomCurves;

internal static class CurveLookupChecks
{
    internal sealed class Market
    {
        public readonly Fixture F=new();
        public readonly CurveModel Model;
        public readonly MarketSnapshot Snapshot;
        public readonly Curve Curve;
        public readonly ImmutableArray<MarketContract> Contracts;
        public Market()
        {
            var f=F;var from=new DateOnly(2041,1,1);
            Contracts=[new(new(f.Revision,Guid.NewGuid()),ContractKind.Forward,from,from.AddDays(1),Fixture.Utc("2041-01-01T12:00:00Z"),null),
                new(new(f.Revision,Guid.NewGuid()),ContractKind.Forward,from.AddDays(3),from.AddDays(4),Fixture.Utc("2041-01-04T12:00:00Z"),null)];
            var series=Contracts.Select(c=>new MarketSeries(new(f.Revision,Guid.NewGuid()),c.Key,f.Quotes[0].Key,null)).ToImmutableArray();
            Curve=new(f.C("lookup"),f.Quotes[0].Key,f.R("location"),f.R("uniform"),new(SettlementKind.Index,f.R("index"),null),
                CurveRule.Direct,[..Contracts.Select(c=>c.Key)],[],ObservationRole.TraderMark,f.R("source"),null);
            Model=new(f.Revision,f.Reference,[f.Quantities[0]],[f.Quotes[0]],Contracts,[Curve],[],[],series);
            Snapshot=f.Snapshot() with{Selections=[..series.Select((s,n)=>new SelectedObservation(new(s.Key,InputUse.Variable),
                new(new(f.Revision.CatalogId,Guid.NewGuid()),s.Key,f.R("source"),ObservationRole.TraderMark,PublicationState.Final,
                    f.Snapshot().ValuationAt,ExactDecimal.Parse(n.ToString()))))]};
        }
    }
    public static int Run()
    {
        var m=new Market();var start=m.Contracts[0].DeliveryFrom;
        var settings=new CurveLookupSettings(CurveLookupMethod.Linear,CurveLookupAxis.DeliveryStart,10,"reject","reject",CurveLookupSettings.Version);
        var curve=AssetCurve.FromNative(m.Model,m.Snapshot,m.Curve.Key,settings);
        var batch=curve.Prepare([new AssetPointDate(start.AddDays(1)),new AssetPointDate(start.AddDays(1)),new AssetContract(m.Contracts[0].Key)]);
        var values=batch.Evaluate(batch.Base);
        if(values[0].Value.Numerator!="1"||values[0].Value.Denominator!="3"||!ReferenceEquals(values[0],values[1])||values[2].Value.Numerator!="0")
            throw new Exception("Independent exact price oracle: 0+(1-0)/3 = 1/3; exact endpoints and duplicates stay exact.");
        var right=batch.Base.Plan.Primitives.Single(p=>p.Source.Request.Contract==m.Contracts[1].Key);
        var changed=batch.Base.WithShifts([new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(right.Source.Request.Curve,
            [batch.Base.Plan.Points.Values.Single(p=>p.Expression is ConnectedSource s&&s.Request==right.Source.Request).Key.PointId]),right.Unit,ExactDecimal.Parse("0.3"),CurveShift.ParentAdditive)]);
        if(batch.Evaluate(changed)[0].Value.ExactTimes(new(ExactDecimal.Parse("3"),right.Unit.Quantity)).ToMoney().Amount.ToString()!="1.3")
            throw new Exception("Independent scenario oracle: three units gain 0.3 when only the right endpoint rises 0.3.");
        if(values[0].Observations.Count!=2||values[0].Inputs.Length!=2)throw new Exception("Interpolation must retain both exact inputs and observations.");
        var root=batch.Base.Plan.Points.Values.Single(p=>p.PointDate==start.AddDays(1)).Key;
        var outputShift=batch.Base.WithShifts([new(CurveShockCoordinate.ComputedOutput,new PointCurveTarget(root.Curve,[root.PointId]),right.Unit,ExactDecimal.Parse("1"),CurveShift.ParentAdditive)]);
        if(batch.Evaluate(outputShift)[0].Value.ExactTimes(new(ExactDecimal.Parse("3"),right.Unit.Quantity)).ToMoney().Amount.ToString()!="4")
            throw new Exception("Computed output shock oracle: three units at 1/3 + 1 = 4.");
        Reject(()=>curve.Evaluate(new AssetPointDate(start.AddDays(-1))));
        Reject(()=>curve.Evaluate(new AssetPointDate(start.AddDays(4))));
        var bucket=AssetCurve.FromNative(m.Model,m.Snapshot,m.Curve.Key,settings with{Method=CurveLookupMethod.ContainingDelivery});
        if(bucket.Evaluate(new AssetDeliveryDate(start)).Value.Numerator!="0")throw new Exception("Containing delivery lookup failed.");
        Reject(()=>bucket.Evaluate(new AssetDeliveryDate(start.AddDays(1))));
        foreach(var (method,expected) in new[]{(CurveLookupMethod.Previous,"0"),(CurveLookupMethod.Next,"1")})
            if(AssetCurve.FromNative(m.Model,m.Snapshot,m.Curve.Key,settings with{Method=method}).Evaluate(new AssetPointDate(start.AddDays(1))).Value.Numerator!=expected)
                throw new Exception("Explicit step lookup chose the wrong endpoint.");
        Reject(()=>AssetCurve.FromNative(m.Model,m.Snapshot,m.Curve.Key,settings with{MaxGapDays=2}).Evaluate(new AssetPointDate(start.AddDays(1))));
        Console.WriteLine("PASS asset curves: exact contracts, delivery gaps, explicit point axis, rational interpolation, steps, batch duplicates and connected shocks");
        var explicitGrid=new CurveLookupGrid<int>([new(1,start,start.AddDays(1),start.AddDays(10)),new(2,start.AddDays(3),start.AddDays(4),start.AddDays(12))],settings with{Axis=CurveLookupAxis.ExplicitDate});
        if(!explicitGrid.Select(start.AddDays(11),false).Select(w=>w.Weight.ToString()).SequenceEqual(["1","1"]))
            throw new Exception("The explicit point date must determine the bracket independently of delivery dates.");
        var overlapping=new CurveLookupGrid<int>([new(1,start,start.AddDays(3)),new(2,start.AddDays(1),start.AddDays(4))],settings with{Method=CurveLookupMethod.ContainingDelivery});
        Reject(()=>overlapping.Select(start.AddDays(2),true));
        RecipeLookup(m,settings);
        return 2;
    }
    static void RecipeLookup(Market m,CurveLookupSettings settings)
    {
        RecipeNode Node(string id,string kind,(string,string)[]? parameters=null,(string,string)[]? ports=null)=>
            new(id,kind,(parameters??[]).ToImmutableDictionary(p=>p.Item1,p=>p.Item2),(ports??[]).ToImmutableDictionary(p=>p.Item1,p=>p.Item2));
        var start=m.Contracts[0].DeliveryFrom;var at=m.Snapshot.ValuationAt;var day=DateOnly.FromDateTime(at.UtcDateTime);
        var definition=new RecipeDefinition("lookup_recipe","Synthetic interpolated delivery",[
            Node("source","source",[("curve",m.Curve.Key.Code)]),
            Node("select","select",[("mode","positions"),("first","1"),("count","2"),("binding","context")],[("source","source")]),
            Node("dates","dates",[("mode","context")]),
            Node("sample","sample",ports:[("selection","select"),("dates","dates")]),
            Node("targets","dates",[("mode","delivery_range"),("from",start.AddDays(1).ToString("yyyy-MM-dd")),("to",start.AddDays(2).ToString("yyyy-MM-dd"))]),
            Node("lookup","lookup",[("axis","delivery_start"),("method","linear_price_actual_days_v1"),("maxGapDays","10"),("leftBoundary","reject"),("rightBoundary","reject"),("implementation",CurveLookupSettings.Version),("output","daily_delivery_v1")],[("values","sample"),("dates","targets")]),
            Node("out","output",ports:[("values","lookup")])],"out");
        var inputs=new RecipeInputs(day,at,ImmutableDictionary<string,RecipeSource>.Empty.Add("source",new(m.Curve.Key,
            m.Model.Build(m.Snapshot,[new(m.Curve.Key,m.Contracts[0].Key)]).Values.Single().Price.Unit,
            [..m.Contracts.Select(c=>new RecipeContract(c,at.AddYears(-1)))])),
            ImmutableDictionary<string,ImmutableArray<DateOnly>>.Empty.Add("dates",[day]).Add("targets",[start.AddDays(1)]));
        var context=new ResolvedCalculationContext(m.Snapshot.Id,"lookup_test",at,m.Snapshot.MarketCutoff,at,m.Snapshot.MarketCutoff);
        var graph=RecipeEngine.Connect(definition,inputs,new(m.F.Revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version),
            [..m.Contracts.Select(c=>new ConnectedRecipeSample(m.Curve.Key,day,c.Key,m.Model,m.Snapshot,false))],context);
        var curve=AssetCurve.FromRecipe(graph,settings with{Method=CurveLookupMethod.ContainingDelivery});
        var value=curve.Evaluate(new AssetDeliveryDate(start.AddDays(1))).Value;
        if(value.Numerator!="1"||value.Denominator!="3")throw new Exception("Recipe and facade must retain the same exact interpolation oracle and target delivery period.");
        Reject(()=>RecipeEngine.Connect(definition,inputs with{Dates=inputs.Dates.SetItem("targets",[start])},
            graph.Identity,[..m.Contracts.Select(c=>new ConnectedRecipeSample(m.Curve.Key,day,c.Key,m.Model,m.Snapshot,false))],context));
        Console.WriteLine("PASS custom lookup: exact rational output, declared daily delivery and immutable date-policy evidence");
    }
    static void Reject(Action action){try{action();}catch(MarketDataException){return;}throw new Exception("An unsupported or uncovered curve query succeeded.");}
}
