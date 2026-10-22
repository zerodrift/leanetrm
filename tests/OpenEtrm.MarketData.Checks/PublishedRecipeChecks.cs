using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;

internal static class PublishedRecipeChecks
{
    public static void Run(ConnectedRecipeGraph parent,ResolvedCalculationContext context,SeriesKey selectedSeries)
    {
        RecipeNode Node(string id,string kind,ImmutableDictionary<string,string>? inputs=null)=>new(id,kind,[],inputs??[]);
        var reference=new PublishedRecipeReference(parent.Identity.CatalogId,parent.Identity.CurveId,parent.Identity.Version,"out");
        var source=Node("upstream","published_source") with{Parameters=ImmutableDictionary<string,string>.Empty.Add("selection","all_output_points_v1"),PublishedSource=reference};
        var definition=new RecipeDefinition("published_wrapper","Synthetic published wrapper",[source,
            Node("result","output",ImmutableDictionary<string,string>.Empty.Add("values","upstream"))],"result");
        var inputs=new RecipeInputs(DateOnly.FromDateTime(context.ValuationAt.UtcDateTime),context.ValuationAt,[],[]);
        var identity=new ConnectedRecipeIdentity(parent.Identity.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version);
        ConnectedRecipeGraph Connect(ConnectedRecipeGraph upstream,ConnectedRecipeIdentity selected)=>RecipeEngine.Connect(definition,inputs,selected,[],context,
            publishedSources:ImmutableDictionary<string,ConnectedRecipeGraph>.Empty.Add("upstream",upstream));
        var child=Connect(parent,identity);
        ConnectedScenario Scenario(params ConnectedRecipeGraph[] graphs)
        {
            var definitions=ConnectedCurves.MergeDefinitions(graphs.SelectMany(g=>g.Definitions));
            return ConnectedScenario.Base(ConnectedCurves.Compile(definitions,graphs.SelectMany(g=>g.Outputs.Select(o=>o.Point)).ToImmutableArray(),context,new Visible()));
        }
        var basis=Scenario(child);var output=child.Outputs.Single().Point;
        Equal(basis.Price(output).Numerator,"221");Equal(basis.Price(output).Denominator,"10");
        Equal(child.Result(basis).Inputs.Length,3);
        var live=basis.Plan.Points.Values.Single(p=>p.Expression is ConnectedSource s&&s.Input.Series==selectedSeries&&s.Input.Use==InputUse.Variable);
        var quote=new CurveShift(CurveShockCoordinate.MarketQuote,new PointCurveTarget(live.Key.Curve,[live.Key.PointId]),live.Unit,ExactDecimal.Parse("1"),CurveShift.ParentAdditive);
        var outer=basis.WithShifts([new(CurveShockCoordinate.ComputedOutput,new ParallelCurveTarget(parent.Curve),basis.Plan.GetPoint(parent.Outputs.Single().Point).Unit,ExactDecimal.Parse("2"),CurveShift.ParentAdditive)]);
        Equal(outer.Price(output).Numerator,"241");Equal(outer.Price(output).Denominator,"10");
        var inner=outer.WithShifts([quote]);Equal(inner.Price(output).Numerator,"793");Equal(inner.Price(output).Denominator,"30");
        Equal(basis.Price(output).Numerator,"221");Equal(outer.Price(output).Numerator,"241");
        var sibling=Connect(parent,identity with{CurveId=Guid.NewGuid()});var shared=Scenario(child,sibling);
        Equal(shared.Plan.Primitives.Length,basis.Plan.Primitives.Length);
        Equal(shared.Price(sibling.Outputs.Single().Point),basis.Price(output));
        var forged=child.Definitions.Select(d=>d with{Points=d.Points.Select(p=>p.Expression is ConnectedRecipeProjection projection&&projection.Declaration.Identity==identity?
            p with{Expression=projection with{Declaration=projection.Declaration with{PublishedSources=projection.Declaration.PublishedSources.Select(s=>s with{ManifestId=new string('a',64)}).ToImmutableArray()}}}:p).ToImmutableArray()}).ToImmutableArray();
        Reject(()=>ConnectedCurves.Compile(forged,child.Outputs.Select(o=>o.Point).ToImmutableArray(),context,new Visible()));
        var wrongOutput=child.Definitions.Select(d=>d with{Points=d.Points.Select(p=>p.Expression is ConnectedRecipeProjection projection&&projection.Declaration.Identity==identity?
            p with{Expression=projection with{Declaration=projection.Declaration with{
                Definition=definition with{Nodes=definition.Nodes.SetItem(0,source with{PublishedSource=reference with{OutputNode="another_output"}})},
                PublishedSources=projection.Declaration.PublishedSources.Select(s=>s with{Reference=s.Reference with{OutputNode="another_output"}}).ToImmutableArray()}}}:p).ToImmutableArray()}).ToImmutableArray();
        Reject(()=>ConnectedCurves.Compile(wrongOutput,child.Outputs.Select(o=>o.Point).ToImmutableArray(),context,new Visible()));
        var formatted=RecipeFormula.Format(definition);
        Equal(RecipeFormula.Format(RecipeFormula.Compile(formatted,definition.Code,definition.Name)),formatted);
        Reject(()=>RecipeEngine.Connect(definition,inputs,identity,[],context));
        Reject(()=>RecipeEngine.Connect(definition,inputs,parent.Identity,[],context,publishedSources:ImmutableDictionary<string,ConnectedRecipeGraph>.Empty.Add("upstream",parent)));
        Reject(()=>RecipeEngine.Connect(definition,inputs,identity,[],new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-other-context",context.EffectiveAt,context.KnownAt,context.ValuationAt,context.MarketAsOf),
            publishedSources:ImmutableDictionary<string,ConnectedRecipeGraph>.Empty.Add("upstream",parent)));
        var chain=parent;
        for(var depth=1;depth<=32;depth++)
        {
            var next=definition with{Nodes=definition.Nodes.SetItem(0,source with{PublishedSource=new(chain.Identity.CatalogId,chain.Identity.CurveId,chain.Identity.Version,depth==1?"out":"result")})};
            chain=RecipeEngine.Connect(next,inputs,identity with{CurveId=Guid.NewGuid()},[],context,
                publishedSources:ImmutableDictionary<string,ConnectedRecipeGraph>.Empty.Add("upstream",chain));
            Equal(chain.PublishedDepth,depth);
        }
        var beyond=definition with{Nodes=definition.Nodes.SetItem(0,source with{PublishedSource=new(chain.Identity.CatalogId,chain.Identity.CurveId,chain.Identity.Version,"result")})};
        Reject(()=>RecipeEngine.Connect(beyond,inputs,identity with{CurveId=Guid.NewGuid()},[],context,
            publishedSources:ImmutableDictionary<string,ConnectedRecipeGraph>.Empty.Add("upstream",chain)));
        Console.WriteLine("PASS published recipes: exact connected output imports, nested upstream/output shocks, shared ancestors, typed formula identity and context/cycle refusal");
    }
    static void Equal<T>(T actual,T expected) where T:notnull
    {if(!actual.Equals(expected))throw new Exception($"Expected {expected}; got {actual}.");}
    static void Reject(Action action)
    {try{action();}catch(MarketDataException){return;}throw new Exception("An invalid published source was accepted.");}
    sealed class Visible:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext context)=>throw new NotSupportedException();
        public void RequireVisible(CurveKey key,ResolvedCalculationContext context){}
    }
}
