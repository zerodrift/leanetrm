using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Risk;
using System.Collections.Immutable;

internal static class CustomProjectionChecks
{
    public static int Run()
    {
        var passed = 0;
        void Check(string name, Action run) { run(); passed++; Console.WriteLine("PASS custom projection: " + name); }
        void Reject(Action run)
        { try { run(); } catch (MarketDataException error) when (error.Code == "custom_projection") { return; } throw new Exception("Expected custom_projection"); }
        var fixture = new IndexFixingChecks.FixingFixture(monthly: true);
        var original = fixture.Floating();
        var binding = new CustomCurveProjectionBinding(fixture.Base.Revision.CatalogId, Guid.NewGuid(), 1,
            "mean", 0, "single_output_position_v1", "published_custom_curve_projection_v1");
        Check("explicit exact version and coordinate participate in economic identity", () =>
        {
            var first = original.WithCustomProjection(binding);
            var next = original.WithCustomProjection(binding with { Version = 2 });
            var coordinate = original.WithCustomProjection(binding with { Position = 1 });
            if (DefinitionIdentity.For(original).Version != "FloatingSide_v4" || DefinitionIdentity.For(first).Version != "FloatingSide_v5" ||
                DefinitionIdentity.For(first) == DefinitionIdentity.For(next) || DefinitionIdentity.For(first) == DefinitionIdentity.For(coordinate) ||
                DefinitionIdentity.For(original) == DefinitionIdentity.For(first)) throw new Exception("Custom projection identity was lost.");
            if (original.CustomProjection is not null || first.CustomProjection != binding || first.Fixings != original.Fixings ||
                first.GenerateSchedule().Payments.Single().Prices.Single().Lookup!.CustomProjection != binding)
                throw new Exception("A projected binding changed its contractual actual fixing rules or was lost from the schedule.");
        });
        Check("equivalent independently retained descriptors have the same canonical economics", () =>
        {
            var copy = new CustomCurveProjectionBinding(binding.CatalogId, binding.CurveId, binding.Version,
                binding.OutputNode, binding.Position, binding.Selection, binding.Implementation);
            if (DefinitionIdentity.For(original.WithCustomProjection(copy)) != DefinitionIdentity.For(original.WithCustomProjection(binding)))
                throw new Exception("Equivalent custom projections produced different economics.");
            var market = original.Projections.Single().Market;
            if (binding.Request(market) == market || binding.Request(market) != copy.Request(market) ||
                binding.Request(market) == (binding with { Version = 2 }).Request(market) ||
                binding.Request(market) == (binding with { Position = 1 }).Request(market) ||
                original.WithCustomProjection(binding).Dependencies.Single() != binding.Request(market))
                throw new Exception("Distinct custom definitions collided in the declared pricing inputs.");
        });
        Check("missing versions coordinates and policies never acquire defaults", () =>
        {
            foreach (var invalid in new[] { binding with { CatalogId = Guid.Empty }, binding with { CurveId = Guid.Empty },
                binding with { Version = 0 }, binding with { OutputNode = "" }, binding with { Position = -1 },
                binding with { Selection = "" }, binding with { Implementation = "" }, binding with { Version = 65 } })
                Reject(() => original.WithCustomProjection(invalid));
            Reject(() => original.WithCustomProjection(null!));
        });
        Check("daily mappings and cross-catalog projection aliases require a separately supported contract", () =>
        {
            Reject(() => new IndexFixingChecks.FixingFixture().Floating().WithCustomProjection(binding));
            Reject(() => original.WithCustomProjection(binding with { CatalogId = Guid.NewGuid() }));
        });
        Check("daily delivery mapping declares every reset without changing the actual fixing schedule",()=>
        {
            var daily=new IndexFixingChecks.FixingFixture();
            var mapped=binding with{CatalogId=daily.Base.Revision.CatalogId,Position=null,Selection="exact_delivery_interval_v1"};
            var side=daily.Floating().WithCustomProjection(mapped);
            if(side.Dependencies.Length!=3||side.GenerateSchedule().Payments.Single().Prices.Any(p=>p.Lookup?.CustomProjection!=mapped)||side.Fixings!=daily.Fixings)
                throw new Exception("Daily mapped projections lost their exact per-reset dependencies or fixing rules.");
            Reject(()=>daily.Floating().WithCustomProjection(mapped with{Position=0}));
            Reject(()=>original.WithCustomProjection(mapped));
            Reject(()=>original.WithCustomProjection(binding with{Position=null}));
        });
        Check("actual fixing takes precedence over the custom projection and needs no connected input", () =>
        {
            var side = original.WithCustomProjection(binding);
            var snapshot = fixture.Snapshot(fixture.Fixings[0].FixingAt, actuals: 1);
            if (!side.DependenciesAt(snapshot).IsEmpty) throw new Exception("A fixed reset retained a projected dependency.");
            var market = fixture.Model.Build(snapshot, []);
            var value = side.PV(new(market, new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted)));
            if (value.PresentValue.Numerator != "10" || value.PresentValue.Denominator != "1" ||
                value.Payments.Single().Prices.Single().State != SchedulePriceState.Actual)
                throw new Exception("A custom forecast replaced its exact published actual.");
        });
        Check("an unfixed custom side refuses an ordinary projection as a fallback", () =>
        {
            var snapshot = fixture.Snapshot(fixture.Fixings[0].FixingAt.AddDays(-2));
            var market = fixture.Model.Build(snapshot, original.Dependencies);
            Reject(() => original.WithCustomProjection(binding).PV(new(market,
                new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted))));
        });
        Check("partly fixed daily panels prepare only unresolved forecasts while retaining full averaging weights",()=>
        {
            var daily=new IndexFixingChecks.FixingFixture();
            var snapshot=daily.Snapshot(daily.Fixings[1].FixingAt,2);
            var reference=binding with{CatalogId=daily.Base.Revision.CatalogId,Position=null,Selection=CustomCurveProjectionBinding.DeliverySelection};
            var side=daily.Floating().WithCustomProjection(reference);
            RecipeNode Node(string id,string kind,(string,string)[]? parameters=null,(string,string)[]? ports=null)=>new(id,kind,
                (parameters??[]).ToImmutableDictionary(p=>p.Item1,p=>p.Item2),(ports??[]).ToImmutableDictionary(p=>p.Item1,p=>p.Item2));
            var graphDefinition=new RecipeDefinition("daily_panel","Synthetic daily panel",[
                Node("source","source",[("curve",daily.Curve.Key.Code)]),
                Node("mapping","weighted_contracts",[("aggregation",RecipeEngine.SignedSum)],[("source_a","source")]) with{
                    ContractWeights=daily.Projections.Select((p,i)=>new RecipeContractWeight(3-i,"source_a",p.Contract.Key.Id,"2")).ToImmutableArray()},
                Node("dates","dates",[("mode","context")]),Node("sample","sample",ports:[("selection","mapping"),("dates","dates")]),
                Node("output","output",ports:[("values","sample")])],"output");
            var day=DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime);
            var inputs=new RecipeInputs(day,snapshot.ValuationAt,
                ImmutableDictionary<string,RecipeSource>.Empty.Add("source",new(daily.Curve.Key,PriceUnit.FromQuote(daily.Quote),
                    daily.Projections.Select(p=>new RecipeContract(p.Contract,snapshot.ValuationAt.AddYears(-1))).ToImmutableArray())),
                ImmutableDictionary<string,ImmutableArray<DateOnly>>.Empty.Add("dates",[day]));
            var context=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-partly-fixed",snapshot.ValuationAt,snapshot.ValuationAt,snapshot.ValuationAt,snapshot.MarketCutoff);
            var remaining=daily.Projections[^1];
            var demand=new RecipeOutputDemand([new(remaining.Contract.DeliveryFrom,remaining.Contract.DeliveryTo)],
                ImmutableDictionary<DateOnly,DateTimeOffset>.Empty.Add(day,snapshot.ValuationAt));
            var graph=RecipeEngine.Connect(graphDefinition,inputs,new(reference.CatalogId,reference.CurveId,1,RecipeEngine.Version),
                [new(daily.Curve.Key,day,remaining.Contract.Key,daily.Model,snapshot,false)],context,demand:demand);
            if(graph.Outputs.Length!=1||graph.Outputs[0].Position!=1)throw new Exception("Consumed output identity was renumbered.");
            // A native current forecast and a published historical mean may share
            // the exact source curve without demanding the parent's dates locally.
            var blendSnapshot=daily.Snapshot(daily.Fixings[0].FixingAt.AddDays(-1));
            var blendDay=DateOnly.FromDateTime(blendSnapshot.ValuationAt.UtcDateTime);
            var blendContext=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-mixed-daily",blendSnapshot.ValuationAt,blendSnapshot.ValuationAt,blendSnapshot.ValuationAt,blendSnapshot.MarketCutoff);
            var blendInputs=inputs with{ContextDate=blendDay,ValuationAt=blendSnapshot.ValuationAt,Dates=inputs.Dates.SetItem("dates",[blendDay])};
            var blendDemand=demand with{SamplingInstants=ImmutableDictionary<DateOnly,DateTimeOffset>.Empty.Add(blendDay,blendSnapshot.ValuationAt)};
            var earlier=blendSnapshot with{Id=Guid.NewGuid(),ValuationAt=blendSnapshot.ValuationAt.AddDays(-1),MarketCutoff=blendSnapshot.MarketCutoff.AddDays(-1)};
            var historical=graphDefinition with{Output="out",Nodes=graphDefinition.Nodes.Where(n=>n.Id!="output").Select(n=>n.Kind=="weighted_contracts"?n with{
                    ContractWeights=n.ContractWeights.Select((w,i)=>w with{OutputPosition=i+1}).ToImmutableArray()}:n)
                .Append(Node("mean","mean",[("axis","dates")],[("values","sample")]))
                .Append(Node("out","output",ports:[("values","mean")])).ToImmutableArray()};
            var parentInputs=blendInputs with{Dates=blendInputs.Dates.SetItem("dates",[blendDay.AddDays(-1),blendDay])};
            var parentSamples=daily.Projections.SelectMany(p=>new[]{
                new ConnectedRecipeSample(daily.Curve.Key,blendDay.AddDays(-1),p.Contract.Key,daily.Model,earlier,true),
                new ConnectedRecipeSample(daily.Curve.Key,blendDay,p.Contract.Key,daily.Model,blendSnapshot,false)}).ToImmutableArray();
            var parent=RecipeEngine.Connect(historical,parentInputs,new(reference.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version),parentSamples,blendContext);
            var mixed=graphDefinition with{Nodes=graphDefinition.Nodes.Where(n=>n.Id!="output").Append(
                new RecipeNode("parent","published_source",ImmutableDictionary<string,string>.Empty.Add("selection","all_output_points_v1"),[],
                    PublishedSource:new(parent.Identity.CatalogId,parent.Identity.CurveId,1,"out"))).Append(
                new RecipeNode("blend","weighted_panels",ImmutableDictionary<string,string>.Empty.Add("aggregation",RecipeEngine.WeightedMean).Add("alignment","exact_delivery_intervals_v1"),
                    ImmutableDictionary<string,string>.Empty.Add("current","sample").Add("history","parent"),PanelWeights:[new("current","1"),new("history","1")])).Append(
                Node("output","output",ports:[("values","blend")])).ToImmutableArray()};
            var mixedGraph=RecipeEngine.Connect(mixed,blendInputs,new(reference.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version),
                [new(daily.Curve.Key,blendDay,remaining.Contract.Key,daily.Model,blendSnapshot,false)],blendContext,
                publishedSources:ImmutableDictionary<string,ConnectedRecipeGraph>.Empty.Add("parent",parent),demand:blendDemand);
            var mixedPlan=ConnectedCurves.Compile(mixedGraph.Definitions,mixedGraph.Outputs.Select(o=>o.Point).ToImmutableArray(),blendContext,new Visible());
            if(mixedGraph.Outputs.Single().Position!=3||mixedGraph.Result(ConnectedScenario.Base(mixedPlan)).Points.Single().Price.ToUnitPriceExact().Value.ToString()!="66")
                throw new Exception("A mixed historical/current daily panel lost its exact selected forecast.");
            var widerPlan=ConnectedCurves.Compile(mixedGraph.Definitions,mixedGraph.Outputs.Select(o=>o.Point).Concat(parent.Outputs.Select(o=>o.Point)).ToImmutableArray(),blendContext,new Visible());
            if(mixedGraph.Result(ConnectedScenario.Base(widerPlan)).Inputs.Length!=2)
                throw new Exception("A recipe result leaked inputs from other roots of its portfolio plan.");
            var missingPeriod=historical with{Nodes=historical.Nodes.Select(n=>n.Kind=="weighted_contracts"?n with{ContractWeights=n.ContractWeights.RemoveAt(0)}:n).ToImmutableArray()};
            var incomplete=RecipeEngine.Connect(missingPeriod,parentInputs,parent.Identity with{CurveId=Guid.NewGuid()},parentSamples,blendContext);
            var mismatched=mixed with{Nodes=mixed.Nodes.Select(n=>n.Id=="parent"?n with{PublishedSource=new(incomplete.Identity.CatalogId,incomplete.Identity.CurveId,1,"out")}:n).ToImmutableArray()};
            try{
                RecipeEngine.Connect(mismatched,blendInputs,mixedGraph.Identity, [new(daily.Curve.Key,blendDay,remaining.Contract.Key,daily.Model,blendSnapshot,false)],blendContext,
                    publishedSources:ImmutableDictionary<string,ConnectedRecipeGraph>.Empty.Add("parent",incomplete),demand:blendDemand);
                throw new Exception("Complete panel mismatch was hidden by partial consumer demand.");
            }catch(MarketDataException error)when(error.Code=="recipe_input"){}
            var plan=ConnectedCurves.Compile(graph.Definitions,graph.Outputs.Select(o=>o.Point).ToImmutableArray(),context,new Visible());
            var mapping=ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(reference.Request(remaining.Market),graph.Outputs[0].Point);
            var swap=daily.Swap(side);
            var holding=new OpenEtrm.MarketData.Risk.RiskPosition(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),swap.Id,
                new(ExactDecimal.Parse("3"),daily.Quote.Quantity),new(new Money(default,daily.Quote.Currency),daily.Quote.Quantity),new(default,daily.Quote.Currency));
            var portfolio=new ValuationPortfolio([swap],[holding],new(ValuationMeasure.PresentValue,
                ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
            var prepared=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(ConnectedScenario.Base(plan),snapshot),mapping,[portfolio],authority:daily.Model.Build(snapshot,[]));
            var bump=new ConnectedRiskBump(CurveShockCoordinate.MarketQuote,new ContractCurveTarget(daily.Curve.Key,remaining.Contract.Key),
                PriceUnit.FromQuote(daily.Quote),ExactDecimal.Parse("0.1"),ConnectedRiskBump.Convention);
            var result=OpenEtrm.MarketData.Risk.RiskEngine.CalculateExact(prepared,[bump],OpenEtrm.MarketData.Risk.RiskEngine.ExactNumericalPolicy);
            var values=result.Groups.Single().Factors.Single().Totals.Single().Values;
            // (10 + 20 + 2*33)/3 - 15, scaled by external quantity 3.
            if(values.Base.ToMoney().Amount.ToString()!="51"||values.Delta.Value.Numerator!="2"||values.Gamma.Value.Numerator!="0")
                throw new Exception("Actual precedence, the full denominator or remaining forecast risk changed.");
        });
        return passed;
    }
    private sealed class Visible:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey logical,ResolvedCalculationContext context)=>throw new Exception("Unexpected logical binding");
        public void RequireVisible(CurveKey exact,ResolvedCalculationContext context){}
    }
}
