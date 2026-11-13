using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.Shared.Temporal;

// The synthetic crude fixture: futures E (2026-12) to J (2027-04) expiring at 19:30 UTC, nine business days
// and three terms for delivery 2027-01 (a calendar-month average and two trade-month roll spreads).
internal static class ContractObservationRecipeChecks
{
    public static void Run()
    {
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string code)=>new(reference,code);
        var quote=new QuoteSpecification(R("crude_out"),QuoteKind.Outright,R("usd"),R("crude_quantity"),ExactDecimal.Parse("1"),ExactDecimal.Parse("0.01"),
            ExactDecimal.Parse("0.01"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
        var futures=new List<FuturesSeriesSpecification>();
        RecipeContract Future(string code,int year,int month,DateTimeOffset expiry)
        {
            var from=new DateOnly(year,month,1);
            futures.Add(new(R(code),R("crude_quantity"),R("usd"),ExactDecimal.Parse("1"),ExactDecimal.Parse("0.01"),R("location"),R("uniform"),
                from,from.AddMonths(1),expiry,R("crude_quantity"),ExactDecimal.Parse("1000"),LotBasis.FixedTotal,null,"cash"));
            return new(new(new(revision,Guid.NewGuid()),ContractKind.Future,from,from.AddMonths(1),expiry,R(code)),new(2026,1,1,0,0,0,TimeSpan.Zero));
        }
        DateTimeOffset Expiry(int year,int month,int day)=>new(year,month,day,19,30,0,TimeSpan.Zero);
        var source=new RecipeSource(new(revision,"synthetic_crude_future"),PriceUnit.FromQuote(quote),[
            Future("crude_e",2026,12,Expiry(2026,11,20)),Future("crude_f",2027,1,Expiry(2026,12,21)),Future("crude_g",2027,2,Expiry(2027,1,20)),
            Future("crude_h",2027,3,Expiry(2027,2,22)),Future("crude_j",2027,4,Expiry(2027,3,22))]);
        var calendar=DateSet.Create(new(2026,11,1),new(2027,4,1),[new(2026,12,15),new(2026,12,16),new(2026,12,17),new(2026,12,18),
            new(2027,1,18),new(2027,1,19),new(2027,1,20),new(2027,1,21),new(2027,1,22)]);
        var noon=new TimeOnly(12,0);
        var node=new RecipeNode("crude",RecipeEngine.ContractObservations,new Dictionary<string,string>{
                ["implementation"]=RecipeEngine.ObservationTermsVersion,["targetFrom"]="2027-01-01",["targetCount"]="1",["days"]=RecipeEngine.BusinessDays,
                ["roll"]=ContractRollRule.ExpiryImplementation,["sampling"]="context_utc_time",["corrections"]="exact_context_frame_history_latest_visible"}.ToImmutableDictionary(),
            ImmutableDictionary<string,string>.Empty.Add("source","source"),
            ObservationTerms:[new(0,"average","1",1,null,RecipeEngine.CalendarMonthPeriod),new(1,"roll","2",1,2,RecipeEngine.TradeMonthPeriod),
                new(2,"roll","1",1,3,RecipeEngine.TradeMonthPeriod)]);
        RecipeDefinition Definition(RecipeNode observations)=>new("crude_formula","Synthetic indexed crude",[
            new("source","source",ImmutableDictionary<string,string>.Empty.Add("curve","synthetic_crude_future"),ImmutableDictionary<string,string>.Empty),observations,
            new("out","output",ImmutableDictionary<string,string>.Empty,ImmutableDictionary<string,string>.Empty.Add("values",observations.Id))],"out");
        RecipeNode Set(RecipeNode n,string key,string value)=>n with{Parameters=n.Parameters.SetItem(key,value)};
        RecipeNode Term(RecipeNode n,int ordinal,Func<RecipeObservationTerm,RecipeObservationTerm> change)=>
            n with{ObservationTerms=n.ObservationTerms.SetItem(ordinal,change(n.ObservationTerms[ordinal]))};
        RecipeNode TargetFebruary(RecipeNode n)=>Set(n,"targetFrom","2027-02-01");
        RecipeNode RollOnly(RecipeNode n)=>n with{ObservationTerms=[..n.ObservationTerms.Skip(1).Select(t=>t with{Ordinal=t.Ordinal-1})]};

        RecipeEngine.Validate(Definition(node));
        var windows=RecipeEngine.ObservationWindows(node,source,calendar,noon);
        Equal("targets",RecipeEngine.ObservationTargets(node).Single(),new RecipeDelivery(new(2027,1,1),new(2027,2,1)));
        Equal("one window per target and term",windows.Length,3);
        Days(windows,term:0,"2027-01-18","2027-01-19","2027-01-20","2027-01-21","2027-01-22");
        Days(windows,term:1,"2026-12-15","2026-12-16","2026-12-17","2026-12-18");
        Days(windows,term:2,"2026-12-15","2026-12-16","2026-12-17","2026-12-18");
        // At 12:00 G leads on all three days; at 20:00 it has expired on 2027-01-20 and that day drops out.
        Days(RecipeEngine.ObservationWindows(TargetFebruary(RollOnly(node)),source,calendar,noon),term:0,"2027-01-18","2027-01-19","2027-01-20");
        Days(RecipeEngine.ObservationWindows(TargetFebruary(RollOnly(node)),source,calendar,new TimeOnly(20,0)),term:0,"2027-01-18","2027-01-19");
        // The last contract of the universe drops its own expiry day the same way: J leads from 2027-02-22 20:00 to 2027-03-21.
        var april=RecipeEngine.ObservationWindows(Set(Set(RollOnly(node),"targetFrom","2027-04-01"),"days",RecipeEngine.CalendarDays) with{
            ObservationTerms=[new(0,"roll","1",1,null,RecipeEngine.TradeMonthPeriod)]},source,null,new TimeOnly(20,0)).Single().Days;
        Equal("last trade month starts after the prior expiry",april[0],new DateOnly(2027,2,22));
        Equal("last trade month drops its expiry day",april[^1],new DateOnly(2027,3,21));
        Equal("last trade month day count",april.Length,28);

        void Refused(string label,RecipeNode candidate)
        {
            Code(label,"recipe_definition",()=>RecipeEngine.Validate(Definition(candidate)));
            Code(label,"recipe_definition",()=>RecipeEngine.ObservationWindows(candidate,source,calendar,noon));
        }
        foreach(var key in node.Parameters.Keys)Refused($"Missing {key}",node with{Parameters=node.Parameters.Remove(key)});
        Refused("Another implementation",Set(node,"implementation","contract_observation_terms_v2"));
        Refused("Another roll",Set(node,"roll","contract_roll_boundaries_v1"));
        Refused("Another day basis",Set(node,"days","trading_days_v1"));
        Refused("Another period",Term(node,0,t=>t with{Period="average_month_v1"}));
        Refused("A target not on day 1",Set(node,"targetFrom","2027-01-02"));
        Refused("No terms",node with{ObservationTerms=[]});
        Refused("Default terms",node with{ObservationTerms=default});
        Refused("A gap in ordinals",Term(node,2,t=>t with{Ordinal=3}));
        foreach(var weight in new[]{"0","-1","2/3"})Refused($"Weight {weight}",Term(node,1,t=>t with{Weight=weight}));
        Refused("A spread against its own position",Term(node,1,t=>t with{LessPosition=1}));
        Code("Observation terms on another kind","recipe_definition",()=>RecipeEngine.Validate(Definition(node) with{Nodes=Definition(node).Nodes.SetItem(0,
            Definition(node).Nodes[0] with{ObservationTerms=node.ObservationTerms})}));
        Code("A missing source port","recipe_definition",()=>RecipeEngine.Validate(Definition(node with{Inputs=ImmutableDictionary<string,string>.Empty})));
        Code("Business days without a calendar","recipe_definition",()=>RecipeEngine.ObservationWindows(node,source,null,noon));
        Code("Calendar days with a calendar","recipe_definition",()=>RecipeEngine.ObservationWindows(Set(node,"days",RecipeEngine.CalendarDays),source,calendar,noon));

        Code("No delivery contract","recipe_input",()=>RecipeEngine.ObservationWindows(Set(RollOnly(node),"targetFrom","2027-06-01"),source,calendar,noon));
        Code("A first delivery contract has no trade-month start","recipe_input",()=>RecipeEngine.ObservationWindows(Set(RollOnly(node),"targetFrom","2026-12-01"),source,calendar,noon));
        Code("A position beyond the universe","recipe_input",()=>RecipeEngine.ObservationWindows(Term(node,0,t=>t with{Position=5}),source,calendar,noon));
        Code("No business day in the window","recipe_input",()=>RecipeEngine.ObservationWindows(node,source,
            DateSet.Create(new(2026,11,1),new(2027,4,1),[new(2026,11,2)]),noon));

        // Prices through the connected plan. Each contract has one daily-settlement series; a day's rows share one snapshot.
        var contracts=new Dictionary<string,RecipeContract>{["F"]=source.Contracts[1],["G"]=source.Contracts[2],["H"]=source.Contracts[3]};
        var series=source.Contracts.ToDictionary(c=>c.Contract.Key,c=>new SeriesKey(revision,Guid.NewGuid()));
        var model=new CurveModel(revision,reference,[new QuantitySpecification(R("crude_quantity"),R("crude"),R("crude_volume"),R("barrel"),R("volume"))],[quote],
            source.Contracts.Select(c=>c.Contract),[new(source.Key,quote.Key,R("location"),R("uniform"),new(SettlementKind.FutureSeries,null,null),CurveRule.Direct,
                [..source.Contracts.Select(c=>c.Contract.Key)],[],ObservationRole.DailySettlement,R("source"),null)],
            futures,[],series.Select(s=>new MarketSeries(s.Value,s.Key,quote.Key,null)));
        (string Day,string Contract,string Value)[] Rows(string day,string f,string g,string h)=>[(day,"F",f),(day,"G",g),(day,"H",h)];
        var december=Rows("2026-12-15","70.00","69.40","68.80").Concat(Rows("2026-12-16","70.30","69.60","68.90"))
            .Concat(Rows("2026-12-17","69.90","69.40","68.80")).Concat(Rows("2026-12-18","70.20","69.60","69.10")).ToArray();
        (string,string,string)[] january=[("2027-01-18","G","69.60"),("2027-01-19","G","69.50"),("2027-01-20","G","69.30"),("2027-01-21","H","68.90"),("2027-01-22","H","68.70")];
        (string,string)[] marks=[("F","70.00"),("G","69.40"),("H","68.80")];
        var identity=new ConnectedRecipeIdentity(revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version);
        (ConnectedRecipeGraph Graph,ConnectedScenario Basis) Priced(string context,string time,IEnumerable<(string Day,string Contract,string Value)> history,IEnumerable<(string Contract,string Value)> live,
            Func<ImmutableDictionary<string,ImmutableArray<RecipeObservationWindow>>,ImmutableDictionary<string,ImmutableArray<RecipeObservationWindow>>>? windows=null)
        {
            var at=DateTimeOffset.Parse(context+"T"+time+"Z",CultureInfo.InvariantCulture);var day=DateOnly.FromDateTime(at.UtcDateTime);
            var resolved=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-crude-observations",at,at,at,at);
            var samples=history.GroupBy(r=>r.Day).Select(g=>(Date:DateOnly.ParseExact(g.Key,"yyyy-MM-dd",CultureInfo.InvariantCulture),Rows:g.Select(r=>(r.Contract,r.Value))))
                .Append((Date:day,Rows:live)).SelectMany(g=>
                {
                    var observed=new DateTimeOffset(g.Date.ToDateTime(TimeOnly.FromDateTime(at.UtcDateTime)),TimeSpan.Zero);
                    var snapshot=new MarketSnapshot(Guid.NewGuid(),revision,observed,observed,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
                        [..g.Rows.Select(r=>new SelectedObservation(new(series[contracts[r.Contract].Contract.Key],InputUse.Variable),new(new(revision.CatalogId,Guid.NewGuid()),
                            series[contracts[r.Contract].Contract.Key],R("source"),ObservationRole.DailySettlement,PublicationState.Final,observed,ExactDecimal.Parse(r.Value))))]);
                    return g.Rows.Select(r=>new ConnectedRecipeSample(source.Key,g.Date,contracts[r.Contract].Contract.Key,model,snapshot,observed<at));
                }).ToImmutableArray();
            var inputs=new RecipeInputs(day,at,ImmutableDictionary<string,RecipeSource>.Empty.Add("source",source),ImmutableDictionary<string,ImmutableArray<DateOnly>>.Empty)
                {Windows=ImmutableDictionary<string,ImmutableArray<RecipeObservationWindow>>.Empty.Add("crude",RecipeEngine.ObservationWindows(node,source,calendar,TimeOnly.FromDateTime(at.UtcDateTime)))};
            if(windows is not null)inputs=inputs with{Windows=windows(inputs.Windows)};
            var graph=RecipeEngine.Connect(Definition(node),inputs,identity with{CurveId=Guid.NewGuid()},samples,resolved);
            return(graph,ConnectedScenario.Base(ConnectedCurves.Compile(graph.Definitions,[..graph.Outputs.Select(o=>o.Point)],resolved,new Visible())));
        }
        ExactUnitPrice Shifted((ConnectedRecipeGraph Graph,ConnectedScenario Basis) priced,params string[] shifted)=>priced.Basis.WithShifts([..shifted.Select(name=>
        {
            var point=priced.Basis.Plan.Points.Values.Single(p=>p.Expression is ConnectedSource s&&s.Input.Series==series[contracts[name].Contract.Key]&&s.Input.Use==InputUse.Variable);
            return new CurveShift(CurveShockCoordinate.MarketQuote,new PointCurveTarget(point.Key.Curve,[point.Key.PointId]),point.Unit,ExactDecimal.Parse("1"),CurveShift.ParentAdditive);
        })]).Price(priced.Graph.Outputs.Single().Point);
        ExactUnitPrice Base((ConnectedRecipeGraph Graph,ConnectedScenario Basis) priced)=>priced.Basis.Price(priced.Graph.Outputs.Single().Point);
        bool Variable((ConnectedRecipeGraph Graph,ConnectedScenario Basis) priced,string name)=>
            priced.Basis.Plan.Primitives.Any(p=>p.Source.Input.Series==series[contracts[name].Contract.Key]&&p.Source.Input.Use==InputUse.Variable);

        // Before the pricing period every observation is projected at today's price of its selected contract.
        var before=Priced("2026-12-01","12:00:00",[],marks);
        Price("before",Base(before),"1749","25");Price("before F+1",Shifted(before,"F"),"1774","25");
        Price("before G+1",Shifted(before,"G"),"5242","75");Price("before H+1",Shifted(before,"H"),"5252","75");
        Price("before F,G,H+1",Shifted(before,"F","G","H"),"1774","25");
        // The December roll window is settled; January is still projected.
        var settledRoll=Priced("2026-12-22","12:00:00",december,marks.Skip(1));
        Price("settled roll",Base(settledRoll),"1749","25");Equal("F is settled",Variable(settledRoll,"F"),false);
        Price("settled roll G+1",Shifted(settledRoll,"G"),"1764","25");Price("settled roll H+1",Shifted(settledRoll,"H"),"1759","25");
        // Inside January: two settled days, the context day and two later days at today's prices.
        var during=Priced("2027-01-20","12:00:00",[..december,..january.Take(2)],marks.Skip(1));
        Price("during",Base(during),"3501","50");Price("during G+1",Shifted(during,"G"),"3511","50");
        // After the pricing period nothing is variable.
        var after=Priced("2027-01-25","12:00:00",[..december,..january],[]);
        Price("after",Base(after),"70","1");Equal("after G is fixed",Variable(after,"G"),false);Equal("after H is fixed",Variable(after,"H"),false);
        // Review focus 3: a context date that is no observation day adds no observation.
        var weekend=Priced("2027-01-17","12:00:00",december,marks.Skip(1));
        Price("context not an observation day",Base(weekend),"1749","25");
        Equal("observation days exclude the context date",string.Join(",",weekend.Graph.Outputs.Single().Dates.Select(d=>d.ToString("MM-dd",CultureInfo.InvariantCulture))),
            "12-15,12-16,12-17,12-18,01-18,01-19,01-20,01-21,01-22");
        Equal("five projected January leaves and sixteen settled roll leaves",weekend.Graph.Outputs.Single().SampleCount,21);
        // Review focus 1: at 20:00 G has expired on its last trading day, so 2027-01-20 observes H.
        var late=Priced("2027-01-25","20:00:00",[..december,..january,("2027-01-20","H","68.80")],[]);
        Price("sampling after expiry",Base(late),"699","10");
        Equal("January leaves after expiry",string.Join(",",late.Graph.Result(late.Basis).Inputs.Where(i=>i.Date.Year==2027).OrderBy(i=>i.Date)
            .Select(i=>i.Date.ToString("MM-dd",CultureInfo.InvariantCulture)+contracts.Single(c=>c.Value.Contract.Key==i.Contract).Key)),"01-18G,01-19G,01-20H,01-21H,01-22H");
        // Review focus 2: a past observation day without its retained sample never shrinks the average.
        Code("missing past observation","recipe_input",()=>Priced("2027-01-25","12:00:00",[..december,..january.Where(r=>r.Item1!="2027-01-19")],[]));
        // Supplied windows cover exactly the observation nodes and each node's targets and terms.
        foreach(var (label,change) in new (string,Func<ImmutableDictionary<string,ImmutableArray<RecipeObservationWindow>>,ImmutableDictionary<string,ImmutableArray<RecipeObservationWindow>>>)[]{
            ("no windows",w=>w.Clear()),("an undeclared node",w=>w.Add("other",w["crude"])),("a missing term",w=>w.SetItem("crude",w["crude"].RemoveAt(2))),
            ("a repeated term",w=>w.SetItem("crude",w["crude"].Add(w["crude"][2]))),("an empty window",w=>w.SetItem("crude",w["crude"].SetItem(0,w["crude"][0] with{Days=[]}))),
            ("another target",w=>w.SetItem("crude",w["crude"].SetItem(0,w["crude"][0] with{Target=new(new(2027,2,1),new(2027,3,1))})))})
            Code(label,"recipe_input",()=>Priced("2026-12-01","12:00:00",[],marks,change));
        Console.WriteLine("PASS contract observation recipes: calendar and trade-month windows, expiry-day roll and explicit refusals");
    }
    private static void Price(string label,ExactUnitPrice value,string numerator,string denominator)
    {if(value.Numerator!=numerator||value.Denominator!=denominator)throw new Exception($"{label}: expected {numerator}/{denominator}, got {value.Numerator}/{value.Denominator}");}
    private sealed class Visible:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey curve,ResolvedCalculationContext context)=>throw new NotSupportedException();
        public void RequireVisible(CurveKey curve,ResolvedCalculationContext context){}
    }
    private static void Days(ImmutableArray<RecipeObservationWindow> windows,int term,params string[] days)
    {
        var actual=windows.Single(w=>w.Term==term).Days.Select(d=>d.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)).ToArray();
        if(!actual.SequenceEqual(days))throw new Exception($"Term {term}: expected {string.Join(",",days)}, got {string.Join(",",actual)}");
    }
    private static void Equal<T>(string label,T actual,T expected) where T:notnull
    {if(!actual.Equals(expected))throw new Exception($"{label}: expected {expected}, got {actual}");}
    private static void Code(string label,string code,Action action)
    {
        try{action();}catch(MarketDataException error)when(error.Code==code){return;}
        catch(MarketDataException error){throw new Exception($"{label}: expected {code}, got {error.Code}: {error.Message}");}
        throw new Exception(label+" was accepted.");
    }
}
