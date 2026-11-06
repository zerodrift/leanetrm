using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;

internal static class SplicedCurveChecks
{
    internal static RecipeNode Node(string id,string kind,(string,string)[]? parameters=null,(string,string)[]? ports=null)=>new(id,kind,
        (parameters??[]).ToImmutableDictionary(p=>p.Item1,p=>p.Item2),(ports??[]).ToImmutableDictionary(p=>p.Item1,p=>p.Item2));
    internal static RecipeDefinition Projection(string code,string name,string source,int count)=>new(code,name,[
        Node("source","source",[("curve",source)]),
        Node("select","select",[("mode","positions"),("binding","context"),("first","1"),("count",count.ToString(System.Globalization.CultureInfo.InvariantCulture))],[("source","source")]),
        Node("dates","dates",[("mode","context"),("sampling","context_utc_time"),("corrections","exact_context_frame_history_latest_visible")]),
        Node("sample","sample",ports:[("selection","select"),("dates","dates")]),Node("output","output",ports:[("values","sample")])],"output");
    internal static RecipeDefinition Splice(ConnectedRecipeGraph before,ConnectedRecipeGraph after,string from,string cutover,string to)=>new("synthetic_splice","Synthetic delivery splice",[
        Node("before","published_source",[("selection","all_output_points_v1")]) with{PublishedSource=new(before.Identity.CatalogId,before.Identity.CurveId,1,"output")},
        Node("after","published_source",[("selection","all_output_points_v1")]) with{PublishedSource=new(after.Identity.CatalogId,after.Identity.CurveId,1,"output")},
        Node("splice","splice",[("from",from),("cutover",cutover),("to",to),("implementation","whole_delivery_intervals_v1")],[("before","before"),("after","after")]),
        Node("output","output",ports:[("values","splice")])],"output");
    internal static void Run()
    {
        var f=new Fixture();var quote=f.Quotes[0];var at=f.Snapshot().ValuationAt;var date=DateOnly.FromDateTime(at.UtcDateTime);
        var periods=new[]{(new DateOnly(2041,1,30),new DateOnly(2041,1,31)),(new DateOnly(2041,1,31),new DateOnly(2041,2,1)),
            (new DateOnly(2041,2,1),new DateOnly(2041,3,1)),(new DateOnly(2041,3,1),new DateOnly(2041,4,1))};
        var contracts=periods.Select((p,n)=>new MarketContract(f.K(700+n),ContractKind.Forward,p.Item1,p.Item2,at.AddYears(1),null)).ToArray();
        var curves=new[]{f.Direct("daily",contracts[0].Key,quote.Key,SettlementKind.Index) with{Constituents=[contracts[0].Key,contracts[1].Key]},
            f.Direct("monthly",contracts[2].Key,quote.Key,SettlementKind.Index) with{Constituents=[contracts[2].Key,contracts[3].Key]}};
        var series=contracts.Select((c,n)=>new MarketSeries(f.S(700+n),c.Key,quote.Key,null)).ToArray();
        var model=new CurveModel(f.Revision,f.Reference,f.Quantities,[quote],contracts,curves,[],[],series);
        var marks=new[]{"3.2","3.3","3.6","3.8"};
        var snapshot=f.Snapshot() with{Selections=[..series.Select((s,n)=>new SelectedObservation(new(s.Key,InputUse.Variable),
            new(new(f.Revision.CatalogId,Guid.NewGuid()),s.Key,f.R("source"),ObservationRole.TraderMark,PublicationState.Final,at,ExactDecimal.Parse(marks[n]))))]};
        var context=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-splice",at,at,at,at);
        var parentInputs=new Dictionary<int,RecipeInputs>();
        ConnectedRecipeGraph Parent(int offset)
        {
            var curve=curves[offset/2];var definition=Projection(curve.Key.Code+"_otc","Synthetic OTC projection",curve.Key.Code,2);
            var input=new RecipeInputs(date,at,ImmutableDictionary<string,RecipeSource>.Empty.Add("source",new(curve.Key,PriceUnit.FromQuote(quote),
                [..contracts.Skip(offset).Take(2).Select(c=>new RecipeContract(c,at.AddYears(-1)))])),ImmutableDictionary<string,ImmutableArray<DateOnly>>.Empty.Add("dates",[date]));
            parentInputs.Add(offset,input);
            return RecipeEngine.Connect(definition,input,
                new(f.Revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version),[..contracts.Skip(offset).Take(2).Select(c=>new ConnectedRecipeSample(curve.Key,date,c.Key,model,snapshot,false))],context);
        }
        var daily=Parent(0);var monthly=Parent(2);var definition=Splice(daily,monthly,"2041-01-30","2041-02-01","2041-04-01");
        var inputs=new RecipeInputs(date,at,[],[]);var identity=new ConnectedRecipeIdentity(f.Revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version);
        var parents=ImmutableDictionary<string,ConnectedRecipeGraph>.Empty.Add("before",daily).Add("after",monthly);
        ConnectedRecipeGraph Connect(RecipeDefinition selected)=>RecipeEngine.Connect(selected,inputs,identity,[],context,publishedSources:parents);
        var graph=Connect(definition);var plan=ConnectedCurves.Compile(graph.Definitions,[..graph.Outputs.Select(o=>o.Point)],context,new Visible());
        var basis=ConnectedScenario.Base(plan);var result=graph.Result(basis);
        if(!result.Points.Select(p=>p.Price.ToUnitPriceExact().Value.ToString()).SequenceEqual(marks))throw new Exception("The splice changed source prices or delivery order.");
        var points=graph.Outputs.Select(o=>plan.Points[o.Point]).ToArray();
        if(!points.Select(p=>(p.DeliveryFrom,p.DeliveryTo)).SequenceEqual(periods))throw new Exception("The splice shaped or changed native delivery intervals.");
        var quotePoint=plan.Points.Values.Single(p=>p.Expression is ConnectedSource s&&s.Input.Series==series[1].Key);
        var shifted=basis.WithShifts([new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(quotePoint.Key.Curve,[quotePoint.Key.PointId]),quotePoint.Unit,
            ExactDecimal.Parse("1"),CurveShift.ParentAdditive)]);
        if(!graph.Result(shifted).Points.Select(p=>p.Price.ToUnitPriceExact().Value.ToString()).SequenceEqual(new[]{"3.2","4.3","3.6","3.8"}))
            throw new Exception("A spliced source shock affected the wrong delivery period.");
        if(result.Inputs.Length!=4||plan.Primitives.Length!=4)throw new Exception("The splice created independently observed copies.");
        var reversed=Connect(definition with{Nodes=[..definition.Nodes.Reverse()]});
        if(ConnectedCurves.Compile(reversed.Definitions,[..reversed.Outputs.Select(o=>o.Point)],context,new Visible()).ManifestId!=plan.ManifestId)
            throw new Exception("Splice source statement order changed identity.");
        var formula=RecipeFormula.Format(definition);
        if(RecipeFormula.Format(RecipeFormula.Compile(formula,definition.Code,definition.Name))!=formula)throw new Exception("Splice formula did not round trip.");
        foreach(var settings in new[]{("from","2041-01-29"),("cutover","2041-01-31"),("to","2041-04-02")})
            Reject("recipe_input",()=>Connect(definition with{Nodes=[..definition.Nodes.Select(n=>n.Kind=="splice"?n with{Parameters=n.Parameters.SetItem(settings.Item1,settings.Item2)}:n)]}));
        Reject("recipe_definition",()=>Connect(definition with{Nodes=[..definition.Nodes.Select(n=>n.Kind=="splice"?n with{Parameters=n.Parameters.Remove("implementation")}:n)]}));
        var times=ImmutableDictionary<DateOnly,DateTimeOffset>.Empty.Add(date,at);
        RecipeTopology ParentShape(int offset,RecipeInputs? selected=null)=>RecipeEngine.Topology(
            offset==0?daily.Declaration.Definition:monthly.Declaration.Definition,selected??parentInputs[offset],ImmutableDictionary<string,RecipeTopology>.Empty,times);
        var shapes=ImmutableDictionary<string,RecipeTopology>.Empty.Add("before",ParentShape(0)).Add("after",ParentShape(2));
        var topology=RecipeEngine.Topology(definition,inputs,shapes,times);
        var demand=topology.PublishedInputs(ImmutableHashSet<RecipeDelivery>.Empty.Add(new(periods[2].Item1,periods[2].Item2)));
        if(demand.Count!=1||demand.Single().Key.CurveId!=monthly.Identity.CurveId||demand.Single().Value.Count!=1)
            throw new Exception("A requested monthly output loaded unrelated daily sources or other monthly periods.");
        var monthlyInput=parentInputs[2];var source=monthlyInput.Sources["source"];
        var wrongUnit=monthlyInput with{Sources=monthlyInput.Sources.SetItem("source",source with{Unit=new(f.R("other_currency"),source.Unit.Quantity,source.Unit.Multiplier)})};
        Reject("recipe_input",()=>RecipeEngine.Topology(definition,inputs,shapes.SetItem("after",ParentShape(2,wrongUnit)),times));
        var crossing=monthlyInput with{Sources=monthlyInput.Sources.SetItem("source",source with{
            Contracts=source.Contracts.SetItem(0,source.Contracts[0] with{Contract=source.Contracts[0].Contract with{DeliveryFrom=new(2041,1,31)}})})};
        Reject("recipe_input",()=>RecipeEngine.Topology(definition,inputs,shapes.SetItem("after",ParentShape(2,crossing)),times));
        // An unused branch retains its authorized shape, with an explicit empty
        // observation demand. Its missing prices cannot block the selected branch.
        foreach(var selectedOffset in new[]{0,2})
        {
            var unusedOffset=selectedOffset==0?2:0;var unusedGraph=unusedOffset==0?daily:monthly;
            var unused=RecipeEngine.Connect(unusedGraph.Declaration.Definition,parentInputs[unusedOffset],unusedGraph.Identity,[],context,
                demand:new(ImmutableHashSet<RecipeDelivery>.Empty,times));
            var branchParents=parents.SetItem(unusedOffset==0?"before":"after",unused);
            var only=new RecipeDelivery(periods[selectedOffset].Item1,periods[selectedOffset].Item2);
            var fullParents=RecipeEngine.Connect(definition,inputs,identity,[],context,publishedSources:parents,demand:new([only],times));
            _=ConnectedCurves.Compile(fullParents.Definitions,[..fullParents.Outputs.Select(o=>o.Point)],context,new Visible());
            var branch=RecipeEngine.Connect(definition,inputs,identity,[],context,publishedSources:branchParents,demand:new([only],times));
            var branchPlan=ConnectedCurves.Compile(branch.Definitions,[..branch.Outputs.Select(o=>o.Point)],context,new Visible());
            if(branch.Result(ConnectedScenario.Base(branchPlan)).Points.Single().Price.ToUnitPriceExact().Value.ToString()!=marks[selectedOffset]||
                branchPlan.Primitives.Length!=1||unused.Outputs.Length!=0||unused.Definitions.Length!=0)
                throw new Exception("An unused splice branch demanded observations or changed the selected branch's price.");
            if(branch.Declaration.ManifestId!=graph.Declaration.ManifestId)
                throw new Exception("Consumer demand changed the saved recipe's semantic identity.");
            var corrupted=branch.Definitions.Select(d=>d with{Points=[..d.Points.Select(p=>p.Expression is ConnectedRecipeProjection projection&&
                projection.Declaration.Identity==identity?p with{Expression=projection with{Declaration=projection.Declaration with{
                    MetadataSources=[..projection.Declaration.MetadataSources.Select(m=>m with{Definition=m.Definition with{Name="Changed retained definition"}})]}}}:p)]}).ToArray();
            Reject("connected_curve_definition",()=>ConnectedCurves.Compile(corrupted,[..branch.Outputs.Select(o=>o.Point)],context,new Visible()));
        }
        var emptyParents=parents.ToImmutableDictionary(p=>p.Key,p=>RecipeEngine.Connect(p.Value.Declaration.Definition,
            parentInputs[p.Key=="before"?0:2],p.Value.Identity,[],context,demand:new(ImmutableHashSet<RecipeDelivery>.Empty,times)));
        var metadataGraph=RecipeEngine.Connect(definition,inputs,identity,[],context,publishedSources:emptyParents,demand:new(ImmutableHashSet<RecipeDelivery>.Empty,times));
        for(var n=0;n<30;n++)
            metadataGraph=RecipeEngine.Connect(Splice(metadataGraph,metadataGraph,"2041-01-30","2041-02-01","2041-04-01"),inputs,
                identity with{CurveId=Guid.NewGuid()},[],context,publishedSources:ImmutableDictionary<string,ConnectedRecipeGraph>.Empty.Add("before",metadataGraph).Add("after",metadataGraph),
                demand:new(ImmutableHashSet<RecipeDelivery>.Empty,times));
        if(metadataGraph.Declaration.MetadataSources.Length!=32||metadataGraph.Definitions.Length!=0)
            throw new Exception("Shared metadata-only ancestors were duplicated or required numerical nodes.");
        Reject("recipe_definition",()=>RecipeEngine.Connect(Splice(metadataGraph,metadataGraph,"2041-01-30","2041-02-01","2041-04-01"),inputs,identity,[],context,
            publishedSources:ImmutableDictionary<string,ConnectedRecipeGraph>.Empty.Add("before",metadataGraph).Add("after",metadataGraph),
            demand:new(ImmutableHashSet<RecipeDelivery>.Empty,times)));
        Console.WriteLine("PASS generic delivery splice: exact intervals/prices/lineage, independent source shocks, order-independent identity, formula roundtrip and boundary/gap refusal");
    }
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
    private sealed class Visible:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey logical,ResolvedCalculationContext context)=>throw new Exception("Unexpected logical binding.");
        public void RequireVisible(CurveKey exact,ResolvedCalculationContext context){}
    }
}
