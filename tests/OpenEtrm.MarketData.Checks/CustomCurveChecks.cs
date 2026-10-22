using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;

internal static class CustomCurveChecks
{
    public static void Run()
    {
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string code)=>new(reference,code);
        var unit=new PriceUnit(R("usd"),R("gas_quantity"),ExactDecimal.Parse("1"));var curve=new CurveKey(revision,"gas");
        DateTimeOffset At(int day,int hour=16)=>new(2026,1,day,hour,0,0,TimeSpan.Zero);
        var contracts=ImmutableArray.Create(new MarketContract(new(revision,Guid.NewGuid()),ContractKind.Forward,new(2026,2,1),new(2026,3,1),At(28,12),null),
            new MarketContract(new(revision,Guid.NewGuid()),ContractKind.Forward,new(2026,3,1),new(2026,4,1),new(2026,2,28,12,0,0,TimeSpan.Zero),null));
        RecipeNode Node(string id,string kind,(string,string)[]? parameters=null,(string,string)[]? ports=null)=>new(id,kind,
            (parameters??[]).ToImmutableDictionary(x=>x.Item1,x=>x.Item2),(ports??[]).ToImmutableDictionary(x=>x.Item1,x=>x.Item2));
        var definition=new RecipeDefinition("ng_average","Synthetic rolling average",[
            Node("source","source",[("curve","gas")]),
            Node("select","select",[("mode","positions"),("first","1"),("count","1"),("binding","each_sample")],[("source","source")]),
            Node("dates","dates",[("mode","prior_calendar")]),
            Node("sample","sample",ports:[("selection","select"),("dates","dates")]),
            Node("mean","mean",[("axis","dates")],[("values","sample")]),Node("out","output",ports:[("values","mean")])],"out");
        var context=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-recipe-cases",At(28),At(28),At(28),At(28));
        (RecipeInputs Inputs,ImmutableArray<ConnectedRecipeSample> Samples) Supply(ImmutableArray<MarketContract> members,ImmutableArray<DateOnly> dates,
            Func<DateTimeOffset,MarketContract,string> price,bool daily=false)
        {
            var quote=new QuoteSpecification(R("gas_quote"),QuoteKind.Outright,unit.Currency,unit.Quantity,unit.Multiplier,
                ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.001"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
            var series=members.Select(m=>new MarketSeries(new(revision,Guid.NewGuid()),m.Key,quote.Key,null)).ToImmutableArray();
            var model=new CurveModel(revision,reference,[new(unit.Quantity,R("gas"),R("gas_energy"),R("energy"),R("dimension"))],[quote],members,
                [new(curve,quote.Key,R("location"),R("uniform"),new(SettlementKind.Index,R("index"),null),CurveRule.Direct,members.Select(m=>m.Key).ToImmutableArray(),[],ObservationRole.TraderMark,R("source"),null)],[],[],series);
            var samples=ImmutableArray.CreateBuilder<ConnectedRecipeSample>();
            foreach(var date in daily?ImmutableArray.Create(new DateOnly(2026,1,28)):dates)
            {
                var at=At(date.Day);var snapshot=new MarketSnapshot(Guid.NewGuid(),revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
                    series.Where(s=>members.Single(m=>m.Key==s.Contract).Expiry>at).Select(s=>new SelectedObservation(new(s.Key,InputUse.Variable),
                        new(new(revision.CatalogId,Guid.NewGuid()),s.Key,R("source"),ObservationRole.TraderMark,PublicationState.Final,at,
                            ExactDecimal.Parse(price(at,members.Single(m=>m.Key==s.Contract)))))).ToImmutableArray());
                foreach(var member in members.Where(m=>m.Expiry>at))samples.Add(new(curve,date,member.Key,model,snapshot,at<context.ValuationAt));
            }
            return(new(new(2026,1,28),At(28),ImmutableDictionary<string,RecipeSource>.Empty.Add("source",new(curve,unit,members.Select(m=>new RecipeContract(m,At(1))).ToImmutableArray())),
                ImmutableDictionary<string,ImmutableArray<DateOnly>>.Empty.Add("dates",dates)),samples.ToImmutable());
        }
        var supplied=Supply(contracts,[new(2026,1,26),new(2026,1,27),new(2026,1,28)],(at,k)=>k==contracts[0]?new[]{"3","3.1","3.2"}[at.Day-26]:new[]{"3.6","3.8","4"}[at.Day-26]);
        var identity=new ConnectedRecipeIdentity(revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version);
        (ConnectedRecipeGraph Graph,ConnectedScenario Scenario) Compile(RecipeDefinition recipe,RecipeInputs inputs,ImmutableArray<ConnectedRecipeSample> samples,CancellationToken ct=default)
        {
            var graph=RecipeEngine.Connect(recipe,inputs,identity,samples,context,ct);
            return(graph,ConnectedScenario.Base(ConnectedCurves.Compile(graph.Definitions,graph.Outputs.Select(o=>o.Point).ToImmutableArray(),context,new Visible(),ct)));
        }
        RecipeResult Result(RecipeDefinition recipe,RecipeInputs inputs,ImmutableArray<ConnectedRecipeSample> samples)
        {var prepared=Compile(recipe,inputs,samples);return prepared.Graph.Result(prepared.Scenario);}
        void Equal(string label,string actual,string expected){if(actual!=expected)throw new Exception($"{label}: expected {expected}, got {actual}");}
        void Reject(string label,Action action){try{action();}catch(MarketDataException){return;}throw new Exception(label+" was accepted.");}
        var result=Result(definition,supplied.Inputs,supplied.Samples);
        Equal("Historical prompt numerator",result.Points.Single().Price.Numerator,"101");Equal("Historical prompt denominator",result.Points.Single().Price.Denominator,"30");
        if(result.Inputs.Length!=3||result.Factors.Length!=1||result.Inputs.Count(s=>s.Fixed)!=2)throw new Exception("Only the current selected contract is live; historical selections remain frozen.");
        var locked=definition with{Nodes=definition.Nodes.Select(n=>n.Id=="select"?n with{Parameters=n.Parameters.SetItem("binding","context")}:n).ToImmutableArray()};
        var lockedResult=Result(locked,supplied.Inputs,supplied.Samples).Points.Single().Price;
        Equal("Locked contract numerator",lockedResult.Numerator,"19");Equal("Locked contract denominator",lockedResult.Denominator,"5");
        Reject("Missing historical prompt",()=>Result(definition,supplied.Inputs,supplied.Samples.Where(s=>!(s.Contract==contracts[0].Key&&s.Date.Day==27)).ToImmutableArray()));
        Reject("Repeated exact sample",()=>Result(definition,supplied.Inputs,supplied.Samples.Add(supplied.Samples[0])));
        Reject("Graph cycle",()=>RecipeEngine.Validate(definition with{Nodes=definition.Nodes.SetItem(0,Node("source","source",ports:[("cycle","out")]))}));
        Reject("Wrong port type",()=>RecipeEngine.Validate(definition with{Nodes=definition.Nodes.SetItem(1,definition.Nodes[1] with{Inputs=definition.Nodes[1].Inputs.SetItem("source","dates")})}));
        var source=supplied.Inputs.Sources["source"];
        Reject("Duplicate source contract",()=>Result(definition,supplied.Inputs with{
            Sources=supplied.Inputs.Sources.SetItem("source",source with{Contracts=source.Contracts.Add(source.Contracts[0])})},supplied.Samples));
        Reject("Silently ignored mean parameter",()=>RecipeEngine.Validate(definition with{Nodes=definition.Nodes.Select(n=>n.Kind=="mean"?
            n with{Parameters=n.Parameters.Add("weights","hours")}:n).ToImmutableArray()}));
        Reject("Unit mismatch",()=>Result(definition,supplied.Inputs with{Sources=supplied.Inputs.Sources.SetItem("source",supplied.Inputs.Sources["source"] with{Unit=new(unit.Currency,unit.Quantity,ExactDecimal.Parse("100"))})},supplied.Samples));
        Reject("Historical sample relabelled",()=>Result(definition,supplied.Inputs,supplied.Samples.SetItem(0,supplied.Samples[0] with{Date=new(2026,1,28)})));
        using(var cancelled=new CancellationTokenSource())
        {cancelled.Cancel();try{Compile(definition,supplied.Inputs,supplied.Samples,cancelled.Token);throw new Exception("Expected cancellation");}catch(OperationCanceledException){}}
        var days=Enumerable.Range(28,4).Select(d=>new DateOnly(2026,1,d)).ToImmutableArray();
        var daily=days.Select(d=>new MarketContract(new(revision,Guid.NewGuid()),ContractKind.Forward,d,d.AddDays(1),new(2026,2,1,0,0,0,TimeSpan.Zero),null)).ToImmutableArray();
        var balmo=definition with{Code="ng_balmo",Nodes=definition.Nodes.Select(n=>n.Id switch
        {
            "select"=>n with{Parameters=n.Parameters.SetItem("mode","daily_delivery").SetItem("binding","context").Remove("first").Remove("count")},
            "dates"=>n with{Parameters=n.Parameters.SetItem("mode","balance_month")},"mean"=>n with{Parameters=n.Parameters.SetItem("axis","contracts")},_=>n
        }).ToImmutableArray()};
        var dailyInputs=Supply(daily,days,(_,k)=>((k.DeliveryFrom.Day-27)*10).ToString(System.Globalization.CultureInfo.InvariantCulture),true);
        var prepared=Compile(balmo,dailyInputs.Inputs,dailyInputs.Samples);var remaining=prepared.Graph.Result(prepared.Scenario);
        Equal("Inclusive BALMO",remaining.Points.Single().Price.Numerator,"25");
        if(remaining.Inputs.Length!=4||remaining.Factors.Length!=4)throw new Exception("Four live delivery contracts share the same exact snapshot.");
        var parallel=new CurveShift(CurveShockCoordinate.MarketQuote,new ParallelCurveTarget(curve),unit,ExactDecimal.Parse("1"),CurveShift.ParentAdditive);
        Equal("Parallel BALMO shock",prepared.Graph.Result(prepared.Scenario.WithShifts([parallel])).Points.Single().Price.Numerator,"26");
        var one=prepared.Graph.Result(prepared.Scenario.WithShifts([parallel with{Target=new ContractCurveTarget(curve,daily[0].Key)}])).Points.Single().Price;
        Equal("One daily factor numerator",one.Numerator,"101");Equal("One daily factor denominator",one.Denominator,"4");
        Reject("Missing weekend sample",()=>Result(balmo,dailyInputs.Inputs,dailyInputs.Samples.RemoveAt(2)));
        Reject("Daily delivery sampling requires the exact valuation snapshot instant",()=>Result(balmo,dailyInputs.Inputs,
            dailyInputs.Samples.Select(s=>s with{FreezeHistorical=true,Snapshot=s.Snapshot with{ValuationAt=s.Snapshot.ValuationAt.AddHours(-1),
                MarketCutoff=s.Snapshot.MarketCutoff.AddHours(-1),Selections=s.Snapshot.Selections.Select(selected=>selected with{
                    Observation=selected.Observation with{ObservedAt=selected.Observation.ObservedAt.AddHours(-1)}}).ToImmutableArray()}}).ToImmutableArray()));
        var monthEndContext=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-month-end-recipe",At(31),At(31),At(31),At(31));
        var monthEndInputs=dailyInputs.Inputs with{ContextDate=new(2026,1,31),ValuationAt=At(31),
            Dates=dailyInputs.Inputs.Dates.SetItem("dates",[new(2026,1,31)])};
        var monthEndSample=dailyInputs.Samples[^1] with{Date=new(2026,1,31),Snapshot=dailyInputs.Samples[^1].Snapshot with{
            Id=Guid.NewGuid(),ValuationAt=At(31),MarketCutoff=At(31),Selections=dailyInputs.Samples[^1].Snapshot.Selections.Select(s=>s with{
                Observation=s.Observation with{Key=new(revision.CatalogId,Guid.NewGuid()),ObservedAt=At(31)}}).ToImmutableArray()}};
        var monthEndGraph=RecipeEngine.Connect(balmo,monthEndInputs,identity,[monthEndSample],monthEndContext);
        var monthEnd=monthEndGraph.Result(ConnectedScenario.Base(ConnectedCurves.Compile(monthEndGraph.Definitions,
            monthEndGraph.Outputs.Select(o=>o.Point).ToImmutableArray(),monthEndContext,new Visible())));
        Equal("Month-end retains one daily forward",monthEnd.Points.Single().Price.Numerator,"40");
        if(monthEnd.Inputs.Length!=1||monthEnd.Factors.Length!=1)throw new Exception("Month-end BALMO must retain exactly its final delivery-day input and factor.");
        var formula=RecipeFormula.Format(definition);var compiled=RecipeFormula.Compile(formula,definition.Code,definition.Name);
        Equal("Formula retains result",Result(compiled,supplied.Inputs,supplied.Samples).Points.Single().Price.Numerator,"101");
        Reject("Arbitrary formula code",()=>RecipeFormula.Compile("out = system(command=\"anything\")",definition.Code,definition.Name));
        Reject("Repeated formula argument",()=>RecipeFormula.Compile("out = source(curve=\"a\",curve=\"b\")",definition.Code,definition.Name));
        Reject("Removed SQL engine",()=>RecipeEngine.Validate(definition with{Nodes=definition.Nodes.Select(n=>n.Id=="mean"?n with{Kind="sql_transform",Parameters=ImmutableDictionary<string,string>.Empty.Add("sqlTransformCode","sql_mean_dates_v1")}:n).ToImmutableArray()}));
        Console.WriteLine("PASS custom curves: one connected engine for exact rolling/locked means, inclusive BALMO, live scenarios, units, strict gaps, cancellation and typed formulas");
    }
    private sealed class Visible:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext context)=>throw new NotSupportedException();
        public void RequireVisible(CurveKey key,ResolvedCalculationContext context){}
    }
}
