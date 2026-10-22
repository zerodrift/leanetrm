using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.Shared.Temporal;

internal static class ConnectedRecipeChecks
{
    public static void Run()
    {
        var f=new Fixture();var model=f.Model();var current=f.Snapshot();
        var context=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-connected-recipe",current.ValuationAt,
            current.ValuationAt,current.ValuationAt,current.MarketCutoff);
        var request=new CurveRequest(f.C("local_power"),f.K(5));
        var builds=Enumerable.Range(0,3).Select(i=>
        {
            var at=current.ValuationAt.AddDays(i-2);
            var snapshot=current with{Id=Guid.NewGuid(),ValuationAt=at,MarketCutoff=at,
                Selections=current.Selections.Select(s=>s with{Observation=s.Observation with
                {Key=new(f.Revision.CatalogId,Guid.NewGuid()),ObservedAt=at,Value=s.Input.Series==f.S(1)?ExactDecimal.Parse((i+1).ToString()):s.Observation.Value}}).ToImmutableArray()};
            return model.Build(snapshot,[request]);
        }).ToImmutableArray();
        var bindings=builds.Select((b,i)=>new ConnectedRecipeSample(request.Curve,DateOnly.FromDateTime(b.Snapshot.ValuationAt.UtcDateTime),request.Contract,b,i<2)).ToImmutableArray();
        var sourceSamples=builds.Select((b,i)=>new RecipeSample(bindings[i].Date,b.Snapshot.ValuationAt,request.Contract,b.Values.Single().Price,
            b.Values.Single().Lineage.ToImmutableArray(),i<2?[]:model.QuoteFactors(b.Snapshot),i<2)).ToImmutableArray();
        RecipeNode Node(string id,string kind,(string,string)[]? parameters=null,(string,string)[]? ports=null)=>new(id,kind,
            (parameters??[]).ToImmutableDictionary(p=>p.Item1,p=>p.Item2),(ports??[]).ToImmutableDictionary(p=>p.Item1,p=>p.Item2));
        var definition=new RecipeDefinition("power_average","Synthetic connected history",[
            Node("source","source",[("curve","local_power")]),
            Node("select","select",[("mode","positions"),("first","1"),("count","1"),("binding","each_sample")],[("source","source")]),
            Node("dates","dates",[("mode","prior_calendar")]),
            Node("sample","sample",ports:[("selection","select"),("dates","dates")]),
            Node("mean","mean",[("axis","dates")],[("values","sample")]),
            Node("out","output",ports:[("values","mean")])],"out");
        var inputs=new RecipeInputs(DateOnly.FromDateTime(current.ValuationAt.UtcDateTime),current.ValuationAt,
            ImmutableDictionary<string,RecipeSource>.Empty.Add("source",new(request.Curve,sourceSamples[0].Price.Unit,
                [new(f.Contracts.Single(c=>c.Key==request.Contract),current.ValuationAt.AddYears(-1))],sourceSamples)),
            ImmutableDictionary<string,ImmutableArray<DateOnly>>.Empty.Add("dates",bindings.Select(b=>b.Date).ToImmutableArray()));
        var identity=new ConnectedRecipeIdentity(f.Revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version);
        var connected=RecipeEngine.Connect(definition,inputs,identity,bindings,context);
        var plan=ConnectedCurves.Compile(connected.Definitions,connected.Outputs.Select(o=>o.Point).ToImmutableArray(),context,new Visible());
        var baseline=ConnectedScenario.Base(plan);var result=connected.Result(baseline).Points.Single().Price;
        Equal("A saved recipe preserves the exact average of its typed source graph",result.Numerator,"221");
        Equal("The base recipe denominator is exact",result.Denominator,"10");
        var live=plan.Points.Values.Single(p=>p.Expression is ConnectedSource s&&s.Input.Series==f.S(1)&&s.Input.Use==InputUse.Variable);
        var shifted=baseline.WithShifts([new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(live.Key.Curve,[live.Key.PointId]),live.Unit,
            ExactDecimal.Parse("1"),CurveShift.ParentAdditive)]);
        var shocked=connected.Result(shifted).Points.Single().Price;
        Equal("Only the current sample moves through the retained quantity ratio",shocked.Numerator,"733");
        Equal("A repeated rational denominator is retained under shocks",shocked.Denominator,"30");
        Equal("The parent calculation remains unchanged",connected.Result(baseline).Points.Single().Price,result);
        Equal("Historical observations of the same series remain distinct",plan.Primitives.Count(p=>p.Source.Input.Series==f.S(1)),3);
        Equal("Only one observation of that series is live",plan.Primitives.Count(p=>p.Source.Input.Series==f.S(1)&&p.Source.Input.Use==InputUse.Variable),1);
        var reordered=RecipeEngine.Connect(definition,inputs,identity,bindings.Reverse().ToImmutableArray(),context);
        Equal("Input collection order does not change graph identity",ConnectedCurves.Compile(reordered.Definitions,reordered.Outputs.Select(o=>o.Point).ToImmutableArray(),context,new Visible()).ManifestId,plan.ManifestId);
        var sourceExport=model.ExportConnected(builds[^1],false,context);
        var merged=ConnectedCurves.MergeDefinitions(connected.Definitions.Concat(sourceExport.Definitions));
        Equal("A recipe and ordinary pricing share the same current source points",merged.Sum(d=>d.Points.Length),connected.Definitions.Sum(d=>d.Points.Length));
        var changed=sourceExport.Definitions.Select(d=>d with{Points=d.Points.Select(p=>p.Expression is ConnectedSource s&&s.Input.Series==f.S(1)?
            p with{Expression=s with{Observation=s.Observation with{Value=ExactDecimal.Parse("4")}}}:p).ToImmutableArray()});
        Reject("Conflicting source values cannot reuse the same exact point",()=>ConnectedCurves.MergeDefinitions(connected.Definitions.Concat(changed)));
        Reject("Missing original sample build",()=>RecipeEngine.Connect(definition,inputs,identity,bindings.RemoveAt(0),context));
        Reject("Current prices cannot be silently frozen",()=>RecipeEngine.Connect(definition,inputs,identity,bindings.SetItem(2,bindings[2] with{FreezeHistorical=true}),context));
        Reject("Retained engine identity is explicit",()=>RecipeEngine.Connect(definition,inputs,identity with{EngineVersion="unknown"},bindings,context));
        var sql=definition with{Nodes=definition.Nodes.Select(n=>n.Id=="mean"?n with{Kind="sql_transform",Parameters=ImmutableDictionary<string,string>.Empty.Add("sqlTransformCode","sql_mean_dates_v1")}:n).ToImmutableArray()};
        Reject("Panel-only SQL is not silently projected as a connected pricing rule",()=>RecipeEngine.Connect(sql,inputs,identity,bindings,context));
        Equal("A scenario trace reports effective live input values",connected.Result(shifted).Inputs.Single(s=>!s.Fixed).Factors.Single(factor=>factor.Input.Series==f.S(1)).BaseValue,ExactDecimal.Parse("4"));
        var numericPoint=plan.Points.Values.Single(p=>p.Expression is ConnectedBuiltCurve b&&b.Rule==CurveRule.ScaleAdd&&!b.Sample.FreezeHistorical);
        var numeric=(ConnectedBuiltCurve)numericPoint.Expression;
        var alternative=numericPoint with{Key=new(numericPoint.Key.Curve,Guid.NewGuid()),Expression=numeric with
        {
            Multiplier=new(ExactDecimal.Parse("8"),numeric.Multiplier!.Specification),
            MultiplierInput=numeric.MultiplierInput! with{Observation=numeric.MultiplierInput.Observation with
                {Key=new(f.Revision.CatalogId,Guid.NewGuid()),Value=ExactDecimal.Parse("8")}}
        }};
        Reject("One current numeric series cannot select two observations in a combined graph",()=>ConnectedCurves.Compile(
            ConnectedCurves.MergeDefinitions(connected.Definitions.Append(new(alternative.Key.Curve,[alternative]))),
            connected.Outputs.Select(o=>o.Point).Append(alternative.Key).ToImmutableArray(),context,new Visible()));
        var business=definition with{Nodes=definition.Nodes.Select(n=>n.Id=="dates"?n with{Parameters=n.Parameters.SetItem("mode","prior_business").SetItem("calendar","publication_days").SetItem("count","3").SetItem("includeContext","true")}:n).ToImmutableArray()};
        Reject("Business-date recipes require their exact resolved calendar evidence",()=>RecipeEngine.Connect(business,inputs,identity,bindings,context));
        var calendar=new ConnectedRecipeCalendarPin("dates",f.R("publication_days"),DateSet.Create(new(2040,12,1),new(2041,1,1),bindings.Select(b=>b.Date)));
        var businessGraph=RecipeEngine.Connect(business,inputs,identity,bindings,context,references:new([calendar],[]));
        var scheduleId=Guid.NewGuid();
        var scheduled=definition with{Nodes=definition.Nodes.Select(n=>n.Id=="dates"?n with{Parameters=n.Parameters.SetItem("mode","schedule").SetItem("schedule",scheduleId.ToString("D")+":1").SetItem("first","2").SetItem("count","1")}:n).ToImmutableArray()};
        var scheduledInputs=inputs with{Dates=inputs.Dates.SetItem("dates",[bindings[^1].Date])};
        var occurrences=Enumerable.Range(0,3).Select(i=>bindings[^1].Date.AddMonths(i-2)).Select(d=>new MonthlyOccurrence(new(d.Year,d.Month,1),d,d)).ToImmutableArray();
        var schedule=new ConnectedRecipeSchedulePin("dates",identity.CatalogId,scheduleId,1,"date_schedule_v1",context.KnownAt.AddDays(-1),1,"absolute_index",0,2,1,occurrences,null);
        Reject("Schedule recipes require their exact resolved publication evidence",()=>RecipeEngine.Connect(scheduled,scheduledInputs,identity,bindings,context));
        var scheduledGraph=RecipeEngine.Connect(scheduled,scheduledInputs,identity,bindings,context,references:new([],[schedule]));
        Reject("A schedule publication after the selected knowledge cutoff is unavailable",()=>RecipeEngine.Connect(scheduled,scheduledInputs,identity,bindings,context,references:new([],[schedule with{KnownFrom=context.KnownAt.AddTicks(1)}])));
        Reject("Schedule pins must reproduce the exact selected occurrences",()=>RecipeEngine.Connect(scheduled,scheduledInputs,identity,bindings,context,references:new([],[schedule with{Occurrences=occurrences.SetItem(2,occurrences[2] with{Date=occurrences[2].Date.AddDays(-1)})}])));
        var source=inputs.Sources["source"];
        var conflictingContract=source.Contracts[0] with{Contract=source.Contracts[0].Contract with{Expiry=source.Contracts[0].Contract.Expiry.AddDays(1)}};
        Reject("Sample selectors cannot contradict the source build's exact contract metadata",()=>RecipeEngine.Connect(definition,
            inputs with{Sources=inputs.Sources.SetItem("source",source with{Contracts=[conflictingContract]})},identity,bindings,context));
        var incomplete=source.Samples.SetItem(2,source.Samples[2] with{Factors=source.Samples[2].Factors.RemoveAt(0)});
        Reject("Connected preview lineage must retain every actual live source factor",()=>RecipeEngine.Connect(definition,
            inputs with{Sources=inputs.Sources.SetItem("source",source with{Samples=incomplete})},identity,bindings,context));
        CurveShift OutputShift(CurvePointKey point,ExactDecimal size)=>new(CurveShockCoordinate.ComputedOutput,
            new PointCurveTarget(point.Curve,[point.PointId]),plan.GetPoint(point).Unit,size,CurveShift.ParentAdditive);
        Reject("A computed source output cannot exceed its declared quote domain",()=>baseline.WithShifts([
            OutputShift(numericPoint.Key,ExactDecimal.Parse("1000"))]).Price(numericPoint.Key));
        Reject("An out-of-domain source output cannot hide inside a valid downstream average",()=>connected.Result(baseline.WithShifts([
            OutputShift(sourceExport.Points[(request,InputUse.Variable)],ExactDecimal.Parse("1000"))])));
        var nearLimit=baseline.WithShifts([OutputShift(numericPoint.Key,ExactDecimal.Parse("970"))]);
        Equal("A valid output-coordinate shift retains its declared working price",nearLimit.Price(numericPoint.Key).Numerator,"9971");
        Reject("Inherited output shifts remain subject to their domain after an inner input shock",()=>nearLimit.WithShifts([
            new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(live.Key.Curve,[live.Key.PointId]),live.Unit,ExactDecimal.Parse("1"),CurveShift.ParentAdditive)]).Price(numericPoint.Key));
        var rescued=baseline.WithShifts([OutputShift(numericPoint.Key,ExactDecimal.Parse("-970"))]).WithShifts([
            new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(live.Key.Curve,[live.Key.PointId]),live.Unit,ExactDecimal.Parse("150"),CurveShift.ParentAdditive)]);
        Equal("Inherited output offsets are applied before validating the effective quote domain",rescued.Price(numericPoint.Key).Numerator,"1071");
        Equal("The effective quoted value retains its exact denominator",rescued.Price(numericPoint.Key).Denominator,"10");
        var recipeOutput=connected.Outputs.Single().Point;
        Equal("A recipe output has no inferred source quote domain",baseline.WithShifts([OutputShift(recipeOutput,ExactDecimal.Parse("2000"))]).Price(recipeOutput).Numerator,"20221");
        Console.WriteLine("PASS connected recipes: retained source graph, typed ratio, historical freeze, exact means and scenario reuse");
    }
    private static void Equal<T>(string label,T actual,T expected) where T:notnull
    {if(!actual.Equals(expected))throw new Exception($"{label}: expected {expected}, got {actual}");}
    private static void Reject(string label,Action action)
    {try{action();}catch(MarketDataException){return;}throw new Exception(label+" was accepted.");}
    private sealed class Visible:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey curve,ResolvedCalculationContext context)=>throw new NotSupportedException();
        public void RequireVisible(CurveKey curve,ResolvedCalculationContext context){}
    }
}
