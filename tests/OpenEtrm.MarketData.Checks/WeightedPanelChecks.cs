using System.Collections.Immutable;
using System.Text.Json;
using System.Security.Cryptography;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;

internal static class WeightedPanelChecks
{
    public static void Run(ConnectedRecipeGraph average,RecipeDefinition definition,RecipeInputs inputs,
        ImmutableArray<ConnectedRecipeSample> samples,ResolvedCalculationContext context,SeriesKey liveSeries,Fixture fixture)
    {
        var declaration=average.Definitions.SelectMany(d=>d.Points).Select(p=>p.Expression).OfType<ConnectedRecipeProjection>().Single().Declaration;
        // The previous persisted declaration shape is an independent compatibility oracle.
        var previous=new{Adapter="saved_recipe_graph_v1",declaration.Identity,definition.Code,definition.Name,definition.Output,
            Nodes=definition.Nodes.OrderBy(n=>n.Id,StringComparer.Ordinal).Select(n=>new{n.Id,n.Kind,
                ContractWeights=n.ContractWeights.IsDefault?[]:n.ContractWeights.OrderBy(w=>w.OutputPosition).ThenBy(w=>w.SourcePort,StringComparer.Ordinal).ThenBy(w=>w.ContractId).ToArray(),
                Parameters=n.Parameters.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>new{p.Key,p.Value}).ToArray(),
                Inputs=n.Inputs.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>new{p.Key,p.Value}).ToArray(),n.PublishedSource}).ToArray(),
            Sources=declaration.Sources.OrderBy(v=>v.Node,StringComparer.Ordinal).ToArray(),Dates=declaration.Dates.OrderBy(v=>v.Node,StringComparer.Ordinal).ToArray(),
            PublishedSources=Array.Empty<ConnectedPublishedRecipeReference>(),References=new{Calendars=Array.Empty<object>(),Schedules=Array.Empty<object>()}};
        Equal(declaration.ManifestId,Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(previous))));
        var shorter=RecipeEngine.Connect(definition,inputs with{Dates=inputs.Dates.SetItem("dates",inputs.Dates["dates"].RemoveAt(0))},
            average.Identity with{CurveId=Guid.NewGuid()},samples,context);
        RecipeNode Source(string id,ConnectedRecipeGraph graph)=>new(id,"published_source",
            ImmutableDictionary<string,string>.Empty.Add("selection","all_output_points_v1"),[],
            PublishedSource:new(graph.Identity.CatalogId,graph.Identity.CurveId,graph.Identity.Version,"out"));
        var combination=new RecipeNode("blend","weighted_panels",ImmutableDictionary<string,string>.Empty
            .Add("alignment","exact_delivery_intervals_v1").Add("aggregation",RecipeEngine.WeightedMean),
            ImmutableDictionary<string,string>.Empty.Add("source_a","longer").Add("source_b","shorter"),
            PanelWeights:[new("source_a","7"),new("source_b","3")]);
        var recipe=new RecipeDefinition("computed_blend","Synthetic computed blend",[Source("longer",average),Source("shorter",shorter),
            combination,new("out","output",[],ImmutableDictionary<string,string>.Empty.Add("values","blend"))],"out");
        var bindings=ImmutableDictionary<string,ConnectedRecipeGraph>.Empty.Add("longer",average).Add("shorter",shorter);
        var noNative=new RecipeInputs(inputs.ContextDate,inputs.ValuationAt,[],[]);
        ConnectedRecipeGraph Connect(RecipeDefinition selected,IReadOnlyDictionary<string,ConnectedRecipeGraph>? parents=null)=>
            RecipeEngine.Connect(selected,noNative,average.Identity with{CurveId=Guid.NewGuid()},[],context,publishedSources:parents??bindings);
        var graph=Connect(recipe);
        ConnectedScenario Scenario(ConnectedRecipeGraph selected)=>ConnectedScenario.Base(ConnectedCurves.Compile(selected.Definitions,
            selected.Outputs.Select(o=>o.Point).ToImmutableArray(),context,new Visible()));
        var basis=Scenario(graph);var output=graph.Outputs.Single().Point;
        Price(basis.Price(output),"463","20");
        var market=basis.Plan.Points.Values.Single(p=>p.Expression is ConnectedSource s&&s.Input.Series==liveSeries&&s.Input.Use==InputUse.Variable);
        var quote=new CurveShift(CurveShockCoordinate.MarketQuote,new PointCurveTarget(market.Key.Curve,[market.Key.PointId]),market.Unit,
            ExactDecimal.Parse("1"),CurveShift.ParentAdditive);
        // The live observation contributes 7/3 to the three-day mean and 7/2 to
        // the two-day mean; the normalized blend therefore moves by 161/60.
        Price(basis.WithShifts([quote]).Price(output),"155","6");
        var outer=basis.WithShifts([new(CurveShockCoordinate.ComputedOutput,new ParallelCurveTarget(average.Curve),
            basis.Plan.GetPoint(output).Unit,ExactDecimal.Parse("2"),CurveShift.ParentAdditive)]);
        Price(outer.Price(output),"491","20");Price(outer.WithShifts([quote]).Price(output),"817","30");
        Price(basis.Price(output),"463","20");Price(outer.Price(output),"491","20");
        var signed=recipe with{Nodes=recipe.Nodes.SetItem(2,combination with{Parameters=combination.Parameters.SetItem("aggregation",RecipeEngine.SignedSum),
            PanelWeights=[new("source_a","0.7"),new("source_b","-0.3")]})};
        var spread=Connect(signed);Price(Scenario(spread).Price(spread.Outputs.Single().Point),"779","100");
        foreach(var invalid in new[]{combination with{PanelWeights=default},combination with{PanelWeights=[new("source_a","1")]},
            combination with{PanelWeights=[new("source_a","1"),new("source_a","2")]},
            combination with{PanelWeights=[new("source_a","-1"),new("source_b","2")]},
            combination with{PanelWeights=[new("source_a","0"),new("source_b","0")]},
            combination with{Parameters=combination.Parameters.Remove("alignment")},
            combination with{Parameters=combination.Parameters.SetItem("alignment","guess_by_position")}})
            Reject(()=>Connect(recipe with{Nodes=recipe.Nodes.SetItem(2,invalid)}));
        var raw=definition with{Nodes=definition.Nodes.Where(n=>n.Id!="mean").Select(n=>n.Id=="out"?n with{Inputs=n.Inputs.SetItem("values","sample")}:n).ToImmutableArray()};
        var rawGraph=RecipeEngine.Connect(raw,inputs,average.Identity with{CurveId=Guid.NewGuid()},samples,context);
        Reject(()=>Connect(recipe with{Nodes=recipe.Nodes.SetItem(1,Source("shorter",rawGraph))},bindings.SetItem("shorter",rawGraph)));
        var gas=fixture.C("base_forward");var gasContract=fixture.Contracts.Single(c=>c.Key==fixture.K(2));
        var gasInputs=inputs with{Sources=ImmutableDictionary<string,RecipeSource>.Empty.Add("source",new(gas,PriceUnit.FromQuote(fixture.Quotes[0]),[new(gasContract,context.ValuationAt.AddYears(-1))]))};
        var gasParent=RecipeEngine.Connect(definition with{Nodes=definition.Nodes.Select(n=>n.Kind=="source"?n with{Parameters=n.Parameters.SetItem("curve",gas.Code)}:n).ToImmutableArray()},
            gasInputs,average.Identity with{CurveId=Guid.NewGuid()},samples.Select(s=>s with{Source=gas,Contract=gasContract.Key}).ToImmutableArray(),context);
        Reject(()=>Connect(recipe with{Nodes=recipe.Nodes.SetItem(1,Source("shorter",gasParent))},bindings.SetItem("shorter",gasParent)));
        var thirds=Connect(recipe with{Nodes=recipe.Nodes.SetItem(2,combination with{PanelWeights=[new("source_a","1"),new("source_b","2")]})});
        Price(thirds.Result(Scenario(thirds)).Points.Single().Price,"733","30");
        var composition=new RecipeCompositionRequest("bounded_blend","Synthetic bounded blend",RecipeEngine.WeightedMean,RecipeEngine.ExactDeliveryAlignment,
            Enumerable.Repeat(new RecipeCompositionInput(new(average.Identity.CatalogId,average.Identity.CurveId,1,"out"),"1"),30).ToImmutableArray());
        Equal(RecipeComposition.Build(composition).Nodes.Length,32);
        Reject(()=>RecipeComposition.Build(composition with{Inputs=composition.Inputs.Add(composition.Inputs[0])}));
        var formula=RecipeFormula.Format(recipe);
        Equal(RecipeFormula.Format(RecipeFormula.Compile(formula,recipe.Code,recipe.Name)),formula);
        // Independent retained frames can agree on every consumed observation
        // without sharing their frame identity.
        var independent=samples.Select(s=>s.FreezeHistorical?s with{Snapshot=s.Snapshot with{Id=Guid.NewGuid()}}:s).ToImmutableArray();
        var independentParent=RecipeEngine.Connect(definition,inputs,average.Identity with{CurveId=Guid.NewGuid()},independent,context);
        var independentBlend=Connect(recipe with{Nodes=recipe.Nodes.SetItem(1,Source("shorter",independentParent))},bindings.SetItem("shorter",independentParent));
        Price(independentBlend.Result(Scenario(independentBlend)).Points.Single().Price,"221","10");
        RecipeDefinition Repeated(int depth)
        {
            var nodes=ImmutableArray.CreateBuilder<RecipeNode>();nodes.Add(Source("longer",average));var prior="longer";
            for(var i=0;i<depth;i++){
                var id="blend_"+i;nodes.Add(combination with{Id=id,Inputs=ImmutableDictionary<string,string>.Empty.Add("source_a",prior).Add("source_b",prior),PanelWeights=[new("source_a","1"),new("source_b","1")]});prior=id;
            }
            nodes.Add(new("out","output",[],ImmutableDictionary<string,string>.Empty.Add("values",prior)));
            return recipe with{Nodes=nodes.ToImmutable()};
        }
        var oneParent=ImmutableDictionary<string,ConnectedRecipeGraph>.Empty.Add("longer",average);
        var shared=Connect(Repeated(8),oneParent);Price(shared.Result(Scenario(shared)).Points.Single().Price,"221","10");
        Reject(()=>Connect(Repeated(17),oneParent));
        Console.WriteLine("PASS computed blends: exact weights, differing history windows, quote/output nested shocks, explicit alignment and ambiguity refusal");
    }
    static void Price(ExactUnitPrice value,string numerator,string denominator){Equal(value.Numerator,numerator);Equal(value.Denominator,denominator);}
    static void Equal<T>(T actual,T expected)where T:notnull{if(!actual.Equals(expected))throw new Exception($"Expected {expected}, got {actual}.");}
    static void Reject(Action run){try{run();}catch(MarketDataException){return;}throw new Exception("An invalid computed blend was accepted.");}
    sealed class Visible:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext selected)=>throw new NotSupportedException();
        public void RequireVisible(CurveKey key,ResolvedCalculationContext selected){}
    }
}
