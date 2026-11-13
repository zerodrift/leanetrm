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
        RecipeContract Future(int year,int month,DateTimeOffset expiry)=>new(new(new(revision,Guid.NewGuid()),ContractKind.Future,
            new(year,month,1),new DateOnly(year,month,1).AddMonths(1),expiry,null),new(2026,1,1,0,0,0,TimeSpan.Zero));
        DateTimeOffset Expiry(int year,int month,int day)=>new(year,month,day,19,30,0,TimeSpan.Zero);
        var source=new RecipeSource(new(revision,"synthetic_crude_future"),new(new(reference,"usd"),new(reference,"barrel"),ExactDecimal.Parse("1")),[
            Future(2026,12,Expiry(2026,11,20)),Future(2027,1,Expiry(2026,12,21)),Future(2027,2,Expiry(2027,1,20)),
            Future(2027,3,Expiry(2027,2,22)),Future(2027,4,Expiry(2027,3,22))]);
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
        Console.WriteLine("PASS contract observation recipes: calendar and trade-month windows, expiry-day roll and explicit refusals");
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
