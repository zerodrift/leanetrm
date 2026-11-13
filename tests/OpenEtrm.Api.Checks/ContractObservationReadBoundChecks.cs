using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.Shared.Temporal;

// A finished contract observation average reads one historical frame per past observation day. Synthetic monthly futures
// 2024-01 to 2026-06, each expiring on the 20th of the month before delivery; calendar-month averages of the prompt future.
internal static class ContractObservationReadBoundChecks
{
    internal static void Run()
    {
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string code)=>new(reference,code);
        var quote=new QuoteSpecification(R("synthetic_out"),QuoteKind.Outright,R("usd"),R("synthetic_quantity"),ExactDecimal.Parse("1"),ExactDecimal.Parse("0.01"),
            ExactDecimal.Parse("0.01"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
        var source=new RecipeSource(new(revision,"synthetic_monthly_future"),PriceUnit.FromQuote(quote),[..Enumerable.Range(0,30).Select(i=>
        {
            var from=new DateOnly(2024,1,1).AddMonths(i);var expiry=new DateTimeOffset(from.AddMonths(-1).ToDateTime(new TimeOnly(19,30)).AddDays(19),TimeSpan.Zero);
            return new RecipeContract(new(new(revision,Guid.NewGuid()),ContractKind.Future,from,from.AddMonths(1),expiry,R($"synthetic_{i}")),new(2023,1,1,0,0,0,TimeSpan.Zero));
        })]);
        (RecipeNode,RecipeSource,DateSet?) Average(string id,string from,int months)=>(new RecipeNode(id,RecipeEngine.ContractObservations,new Dictionary<string,string>{
            ["implementation"]=RecipeEngine.ObservationTermsVersion,["targetFrom"]=from,["targetCount"]=months.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["days"]=RecipeEngine.CalendarDays,["roll"]=ContractRollRule.ExpiryImplementation,["sampling"]="context_utc_time",
            ["corrections"]="exact_context_frame_history_latest_visible"}.ToImmutableDictionary(),ImmutableDictionary<string,string>.Empty.Add("source","source"),
            ObservationTerms:[new(0,"average","1",1,null,RecipeEngine.CalendarMonthPeriod)]),source,null);
        int Past(CustomCurves.ObservationPlan plan,DateOnly context)=>plan.Samples.Select(s=>s.Date).Where(d=>d<context).Distinct().Count();
        var mid2025=new DateTimeOffset(2025,6,2,12,0,0,TimeSpan.Zero);var context=new DateOnly(2025,6,2);
        // The twelve months of 2024 hold 366 days: the bound is reached, not exceeded.
        Equal("a finished leap year",Past(CustomCurves.PlanObservations([Average("year","2024-01-01",12)],mid2025),context),366);
        // Days are counted once across the definition: a second operation over days already read adds none.
        Equal("a second operation over the same days",Past(CustomCurves.PlanObservations([Average("year","2024-01-01",12),Average("june","2024-06-01",1)],mid2025),context),366);
        // Days on or after the context date read the context frame only.
        Equal("an average still in progress",Past(CustomCurves.PlanObservations([Average("year","2024-01-01",13)],new(2024,12,15,12,0,0,TimeSpan.Zero)),new(2024,12,15)),349);
        // A 367th past day refuses before any frame is read, whether one operation or two reach it.
        Refused("a finished thirteen months",()=>CustomCurves.PlanObservations([Average("year","2024-01-01",13)],mid2025));
        Refused("two operations over 397 days",()=>CustomCurves.PlanObservations([Average("year","2024-01-01",12),Average("january","2025-01-01",1)],mid2025));
        Console.WriteLine("PASS contract observation read bound: at most 366 past observation days across a definition, counted once, context days excluded");
    }
    private static void Equal(string label,int actual,int expected){if(actual!=expected)throw new Exception($"{label}: expected {expected}, got {actual}");}
    private static void Refused(string label,Action action)
    {
        try{action();}
        catch(MarketDataException error)when(error.Code=="custom_curve_definition"&&error.Message.Contains("366 past observation days",StringComparison.Ordinal)){return;}
        throw new Exception(label+" was not refused by the past observation day bound.");
    }
}
