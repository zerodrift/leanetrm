using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Curves;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Publications;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.CustomCurves;

internal static class ContractPriceTermsChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string name,Action action){action();count++;Console.WriteLine("PASS contract price terms: "+name);}
        void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());
        MarketContract Contract(int month)=>new(new(revision,Guid.NewGuid()),ContractKind.Forward,new(2041,month,1),new DateOnly(2041,month,1).AddMonths(1),new(2040,12,20,19,30,0,TimeSpan.Zero),null);
        var january=Contract(1);var february=Contract(2);var janSource=Contract(1);var febSource=Contract(2);
        Check("selected same-period rule materializes exact contract identities independently of source ordering",()=>
        {
            var result=ContractPriceMappings.Resolve(ContractPriceMappings.SameDeliveryPeriod,[january,february],[febSource,janSource],[]);
            if(!result.SequenceEqual(new[]{new ConstituentMapping(january.Key,janSource.Key),new ConstituentMapping(february.Key,febSource.Key)}))throw new Exception("Mapping did not retain dated identities.");
        });
        Check("same-period mapping rejects ambiguous and missing source coverage",()=>
        {
            Reject("contract_price_mapping",()=>ContractPriceMappings.Resolve(ContractPriceMappings.SameDeliveryPeriod,[january],[janSource,Contract(1)],[]));
            Reject("contract_price_mapping",()=>ContractPriceMappings.Resolve(ContractPriceMappings.SameDeliveryPeriod,[january,february],[janSource],[]));
        });
        Check("explicit sampling maps retain different target and source periods",()=>
        {
            var pair=new ConstituentMapping(january.Key,janSource.Key);
            if(ContractPriceMappings.Resolve(ContractPriceMappings.ExplicitContracts,[january],[janSource],[pair]).Single()!=pair)throw new Exception("Explicit map changed.");
            if(ContractPriceMappings.Resolve(ContractPriceMappings.ExplicitContracts,[january],[febSource],[new(january.Key,febSource.Key)]).Single().Source!=febSource.Key)throw new Exception("Shifted mapping changed.");
        });
        Check("missing convention and incomplete explicit maps do not fall back",()=>
        {
            Reject("contract_price_mapping",()=>ContractPriceMappings.Resolve("",[january],[janSource],[]));
            Reject("contract_price_mapping",()=>ContractPriceMappings.Resolve(ContractPriceMappings.ExplicitContracts,[january],[janSource],[]));
            Reject("contract_price_mapping",()=>ContractPriceMappings.Resolve(ContractPriceMappings.SameDeliveryPeriod,[january],[janSource],[new(january.Key,janSource.Key)]));
        });
        Check("resolved mappings feed the existing recipe contract-weight representation",()=>
        {
            var maps=ContractPriceMappings.Resolve(ContractPriceMappings.SameDeliveryPeriod,[january,february],[janSource,febSource],[]);
            var weights=ContractPriceMappings.RecipeWeights(maps,"base",ExactDecimal.Parse("-1"));
            if(weights.Length!=2||weights[1].OutputPosition!=2||weights[1].ContractId!=febSource.Key.Id||weights[0].SourcePort!="base"||weights[0].Weight!="-1")throw new Exception("Canvas weights lost mapping semantics.");
        });
        var f=new IndexFixingChecks.FixingFixture(monthly:true,delayedMonthlyPublication:true);
        var commodity=f.Base.Quantities.Single(q=>q.Key==f.Quote.Quantity).Commodity;
        var publication=new PricePublicationDefinition(new(f.Base.Revision.CatalogId,Guid.NewGuid(),1),f.Base.Reference,
            new(new(f.Base.Revision.CatalogId,Guid.NewGuid(),1),"Synthetic publisher"),"Synthetic monthly publication",[commodity],
            [new(f.Base.R("index"),commodity,f.Base.R("location"),f.Quote.Key,PriceUnit.FromQuote(f.Quote),"SYNTH","monthly")],
            f.Base.R("calendar"),f.Base.R("clock"),DatedGasPublication.MonthlyImplementation);
        var terms=new ContractPriceTerms(ContractPriceTerms.PublishedIndex,publication.Key,f.Base.R("index"),ContractPriceTerms.SingleAssessment,ContractPriceTerms.RequireFinal,null);
        var request=f.Projections[0].Market;
        var bindings=new Dictionary<CurveRequest,ContractPriceTermsBinding>{{request,new(terms,publication,commodity,f.Fixings[0])}};
        ConnectedCurvePlan Compile(MarketSnapshot snapshot,IReadOnlyDictionary<CurveRequest,ContractPriceTermsBinding>? selected=null)
        {
            var context=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-contract-terms",snapshot.ValuationAt,snapshot.ValuationAt,snapshot.ValuationAt,snapshot.MarketCutoff);
            var graph=f.Model.CompileContractTerms(snapshot,[request],selected??bindings,false,context);
            return ConnectedCurves.Compile(graph.Definitions,[graph.Points[(request,InputUse.Variable)]],context,new Visible());
        }
        Check("publication actual enters the shared graph as original frozen fixing and removes forecast risk",()=>
        {
            var graph=Compile(f.Snapshot(f.Fixings[0].FixingAt,1));
            if(graph.Evaluate(graph.Points.Keys.Single()).ToExactString()!="10"||graph.Primitives.Any(p=>p.Source.Input.Use==InputUse.Variable))throw new Exception("Fixing did not replace forecast.");
        });
        Check("publication date gap and missing due fixing refuse without stale fallback",()=>
        {
            Reject("source_expired",()=>Compile(f.Snapshot(f.Projections[0].Contract.Expiry)));
            Reject("index_fixing_missing",()=>Compile(f.Snapshot(f.Fixings[0].FixingAt)));
            var live=Compile(f.Snapshot(f.Projections[0].Contract.Expiry.AddHours(-1)));
            if(live.Evaluate(live.Points.Keys.Single()).ToExactString()!="11")throw new Exception("Live projection changed.");
        });
        Check("publication membership and commodity are exact financial prerequisites",()=>
        {
            Reject("contract_price_terms",()=>Compile(f.Snapshot(f.Fixings[0].FixingAt,1),new Dictionary<CurveRequest,ContractPriceTermsBinding>{{request,bindings[request] with{Commodity=f.Base.R("other_commodity")}}}));
            Reject("contract_price_terms",()=>Compile(f.Snapshot(f.Fixings[0].FixingAt,1),new Dictionary<CurveRequest,ContractPriceTermsBinding>{{request,bindings[request] with{Terms=terms with{Publication=new(publication.Key.CatalogId,Guid.NewGuid(),1)}}}}));
        });
        Check("an authored shifted source map keeps target coverage and the actual source fixing in one recipe graph",()=>
        {
            var daily=new IndexFixingChecks.FixingFixture();var contracts=daily.Projections.Select(p=>p.Contract).ToImmutableArray();
            var report=publication with{Key=new(daily.Base.Revision.CatalogId,Guid.NewGuid(),1),References=daily.Base.Reference,
                Publisher=new(new(daily.Base.Revision.CatalogId,Guid.NewGuid(),1),"Synthetic publisher"),Commodities=[daily.Base.R("gas")],
                Indices=[new(daily.Base.R("index"),daily.Base.R("gas"),daily.Base.R("location"),daily.Quote.Key,PriceUnit.FromQuote(daily.Quote),"DAILY","daily")],
                Calendar=daily.Base.R("calendar"),Clock=daily.Base.R("clock"),Implementation=DatedGasPublication.DailyImplementation};
            var selectedTerms=terms with{Publication=report.Key,Index=daily.Base.R("index")};
            var map=ContractPriceMappings.Resolve(ContractPriceMappings.ExplicitContracts,contracts,contracts,
                [new(contracts[0].Key,contracts[1].Key),new(contracts[1].Key,contracts[0].Key),new(contracts[2].Key,contracts[2].Key)]);
            var set=new ContractPriceTermsSet(selectedTerms,[],ContractPriceMappings.ExplicitContracts,map);
            RecipeNode Node(string id,string kind,(string,string)[]? p=null,(string,string)[]? ports=null)=>new(id,kind,
                (p??[]).ToImmutableDictionary(x=>x.Item1,x=>x.Item2),(ports??[]).ToImmutableDictionary(x=>x.Item1,x=>x.Item2));
            var definition=new RecipeDefinition("mapped_fixing","Synthetic mapped fixing",[
                Node("source","source",[("curve",daily.Curve.Key.Code)]) with{ContractTerms=set},
                new("select","weighted_contracts",ImmutableDictionary<string,string>.Empty.Add("aggregation",RecipeEngine.SignedSum),
                    ImmutableDictionary<string,string>.Empty.Add("source_a","source"),[new(1,"source_a",contracts[0].Key.Id,"1")]),
                Node("dates","dates",[("mode","context")]),Node("sample","sample",ports:[("selection","select"),("dates","dates")]),Node("out","output",ports:[("values","sample")])],"out");
            var snapshot=daily.Snapshot(daily.Fixings[1].FixingAt,2);var date=DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime);
            var context=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-shifted-recipe",snapshot.ValuationAt,snapshot.ValuationAt,snapshot.ValuationAt,snapshot.MarketCutoff);
            var inputs=new RecipeInputs(date,snapshot.ValuationAt,ImmutableDictionary<string,RecipeSource>.Empty.Add("source",new(daily.Curve.Key,PriceUnit.FromQuote(daily.Quote),
                contracts.Select(c=>new RecipeContract(c,snapshot.ValuationAt.AddYears(-1))).ToImmutableArray())),ImmutableDictionary<string,ImmutableArray<DateOnly>>.Empty.Add("dates",[date]));
            var sample=new ConnectedRecipeSample(daily.Curve.Key,date,contracts[0].Key,daily.Model,snapshot,false)
                {MappedContract=contracts[1].Key,PriceTerms=new(selectedTerms,report,daily.Base.R("gas"),daily.Fixings[1])};
            var graph=RecipeEngine.Connect(definition,inputs,new(daily.Base.Revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version),[sample],context);
            var plan=ConnectedCurves.Compile(graph.Definitions,[graph.Outputs.Single().Point],context,new Visible());
            var output=plan.Points[graph.Outputs.Single().Point];
            if(output.DeliveryFrom!=contracts[0].DeliveryFrom||output.DeliveryTo!=contracts[0].DeliveryTo||plan.Evaluate(output.Key).ToExactString()!="20"||
                plan.Primitives.Single().Source.Request.Contract!=contracts[1].Key||plan.Primitives.Single().Source.Input.Series!=daily.Series[1].Key)
                throw new Exception("Target period or actual source fixing was relabeled.");
            var formula=RecipeFormula.Format(definition);var parsed=RecipeFormula.Compile(formula,definition.Code,definition.Name);
            if(!definition.SameGraph(parsed))throw new Exception("Formula discarded contract terms or mapping identity.");
            Reject("recipe_input",()=>RecipeEngine.Connect(definition,inputs,new(daily.Base.Revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version),[sample with{MappedContract=contracts[0].Key}],context));
            Check("parent-child-grandchild source terms propagate live shocks and freeze the same final publication",()=>
            {
                ConnectedRecipeGraph Child(ConnectedRecipeGraph parent,string code,string weight,ResolvedCalculationContext at)
                {
                    var reference=new PublishedRecipeReference(parent.Identity.CatalogId,parent.Identity.CurveId,parent.Identity.Version,"out");
                    var child=new RecipeDefinition(code,"Synthetic nested contract terms",[
                        Node("parent","published_source",[("selection","all_output_points_v1")]) with{PublishedSource=reference},
                        new("weighted","weighted_panels",ImmutableDictionary<string,string>.Empty.Add("aggregation",RecipeEngine.SignedSum).Add("alignment",RecipeEngine.ExactDeliveryAlignment),
                            ImmutableDictionary<string,string>.Empty.Add("source_a","parent"),PanelWeights:[new("source_a",weight)]),
                        Node("out","output",ports:[("values","weighted")])],"out");
                    return RecipeEngine.Connect(child,new(DateOnly.FromDateTime(at.ValuationAt.UtcDateTime),at.ValuationAt,
                        ImmutableDictionary<string,RecipeSource>.Empty,ImmutableDictionary<string,ImmutableArray<DateOnly>>.Empty),
                        new(daily.Base.Revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version),[],at,
                        publishedSources:new Dictionary<string,ConnectedRecipeGraph>{{"parent",parent}});
                }
                var finalChild=Child(graph,"final_child","2",context);var finalGrandchild=Child(finalChild,"final_grandchild","3",context);
                var finalPlan=ConnectedCurves.Compile(finalGrandchild.Definitions,[finalGrandchild.Outputs.Single().Point],context,new Visible());
                var finalScenario=ConnectedScenario.Base(finalPlan);var final=finalGrandchild.Result(finalScenario);
                if(final.Points.Single().Price.ToExactString()!="120"||!final.Factors.IsEmpty||finalPlan.Primitives.Any(p=>p.Source.Input.Use==InputUse.Variable)||
                    final.Inputs.Any(i=>!i.Fixed)||finalPlan.Primitives.Single().Source.Observation.Role!=ObservationRole.IndexFixing)
                    throw new Exception("Nested synthetic outputs retained live risk after their exact publication fixed.");
                var liveSnapshot=daily.Snapshot(contracts[0].Expiry.AddHours(-1));var liveDate=DateOnly.FromDateTime(liveSnapshot.ValuationAt.UtcDateTime);
                var liveContext=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-nested-live",liveSnapshot.ValuationAt,liveSnapshot.ValuationAt,liveSnapshot.ValuationAt,liveSnapshot.MarketCutoff);
                var liveInputs=inputs with{ContextDate=liveDate,ValuationAt=liveSnapshot.ValuationAt,Dates=inputs.Dates.SetItem("dates",[liveDate])};
                var liveParent=RecipeEngine.Connect(definition,liveInputs,new(daily.Base.Revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version),
                    [sample with{Date=liveDate,Snapshot=liveSnapshot}],liveContext);
                var liveChild=Child(liveParent,"live_child","2",liveContext);var liveGrandchild=Child(liveChild,"live_grandchild","3",liveContext);
                var livePlan=ConnectedCurves.Compile(liveGrandchild.Definitions,[liveGrandchild.Outputs.Single().Point],liveContext,new Visible());
                var liveScenario=ConnectedScenario.Base(livePlan);var before=liveGrandchild.Result(liveScenario);
                var shifted=liveScenario.WithShifts([new(CurveShockCoordinate.MarketQuote,new ParallelCurveTarget(daily.Curve.Key),PriceUnit.FromQuote(daily.Quote),ExactDecimal.Parse("1"),CurveShift.ParentAdditive)]);
                if(before.Points.Single().Price.ToExactString()!="132"||before.Factors.Length!=1||liveGrandchild.Result(shifted).Points.Single().Price.ToExactString()!="138")
                    throw new Exception("The live source shock did not propagate through both exact published synthetic parents.");
                if(liveGrandchild.Result(liveScenario).Points.Single().Price.ToExactString()!="132")throw new Exception("Scenario mutated nested base graph.");
            });
        });
        Check("official exchange final settlement never becomes a last ordinary mark",()=>
        {
            var fixture=new Fixture();var model=fixture.Model();var contract=fixture.Contracts[0];var request=new CurveRequest(fixture.C("future"),contract.Key);
            var snapshot=fixture.Snapshot() with{ValuationAt=contract.Expiry,MarketCutoff=contract.Expiry};
            var selected=new Dictionary<CurveRequest,ContractPriceTermsBinding>{{request,new(new(ContractPriceTerms.ExchangeFinal,null,null,ContractPriceTerms.SingleAssessment,ContractPriceTerms.RequireFinal,null),null,fixture.R("gas"),null)}};
            var context=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-exchange-final",snapshot.ValuationAt,snapshot.ValuationAt,snapshot.ValuationAt,snapshot.MarketCutoff);
            Reject("missing_observation",()=>model.CompileContractTerms(snapshot,[request],selected,false,context));
            var original=snapshot.Selections.Single(o=>o.Input.Series==fixture.S(1));
            var actual=original with{Input=new(fixture.S(1),InputUse.FrozenFact),Observation=original.Observation with{Key=new(fixture.Revision.CatalogId,Guid.NewGuid()),Role=ObservationRole.FinalContractSettlement,ObservedAt=contract.Expiry,Value=ExactDecimal.Parse("4")}};
            var export=model.CompileContractTerms(snapshot with{Selections=snapshot.Selections.Add(actual)},[request],selected,false,context);
            var plan=ConnectedCurves.Compile(export.Definitions,[export.Points[(request,InputUse.Variable)]],context,new Visible());
            if(plan.Evaluate(export.Points[(request,InputUse.Variable)]).ToExactString()!="4"||plan.Primitives.Single().Source.Observation.Role!=ObservationRole.FinalContractSettlement)
                throw new Exception("Official final settlement source was lost.");
        });
        return count;
    }
    sealed class Visible:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey curve,ResolvedCalculationContext context)=>throw new NotSupportedException();
        public void RequireVisible(CurveKey curve,ResolvedCalculationContext context){}
    }
}
