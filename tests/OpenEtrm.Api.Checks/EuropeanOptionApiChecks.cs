using System.Text.Json;
using OpenEtrm.Api;

// What the backend says about a European listed futures option without reading a database (D446).
internal static class EuropeanOptionApiChecks
{
    private static void Check(string label,Action run){run();Console.WriteLine($"PASS {label}");}
    private static void Equal(string? actual,string expected){if(actual!=expected)throw new Exception($"Expected {expected}; got {actual??"nothing"}.");}

    internal static void Run()
    {
        Check("early exercise of a European option has its own refusal text",()=>
            Equal(FuturesOptionStore.EventRefusal("option_exercise_style"),"A European option is exercised only at expiry."));
        Check("exercise and assignment are never offered on a European holding, whatever else is true of it",HoldingActions);
    }

    // The holding's own statement of its actions, from its booked lots and its product's style and instruction terms.
    private static JsonElement Actions(string lots,string closed,string style,bool instructions=false)=>JsonSerializer.SerializeToElement(new FuturesOptionPosition(
        JsonSerializer.SerializeToElement(new{lots,closed_lots=closed,expired=false}),default,
        JsonSerializer.SerializeToElement(new{exercise_style=style,contrary_instructions_allowed=instructions})).Actions);

    private static void HoldingActions()
    {
        const string never="A European option is exercised only at expiry.";
        static string? Reason(JsonElement actions,string action)=>actions.GetProperty(action).GetProperty("reason").GetString();
        static bool Allowed(JsonElement actions,string action)=>actions.GetProperty(action).GetProperty("allowed").GetBoolean();
        foreach(var lots in new[]{"3","-3"})
        {
            var european=Actions(lots,"0","european");
            if(Allowed(european,"exercise")||Allowed(european,"assignment"))throw new Exception("A European holding offered early exercise or assignment.");
            Equal(Reason(european,"exercise"),never);Equal(Reason(european,"assignment"),never);
            Equal(Reason(european,"instruction"),FuturesOptionStore.EventRefusal("option_contrary_instructions_not_accepted")!);
        }
        // An American holding is unchanged: a long one may be exercised and a short one assigned.
        var (longAmerican,shortAmerican)=(Actions("3","0","american"),Actions("-3","0","american"));
        if(!Allowed(longAmerican,"exercise")||Allowed(longAmerican,"assignment")||Allowed(shortAmerican,"exercise")||!Allowed(shortAmerican,"assignment"))
            throw new Exception("An American holding's exercise and assignment changed.");
        // A closed holding says it is closed, whatever its style.
        Equal(Reason(Actions("3","3","european"),"exercise"),FuturesOptionStore.EventRefusal("option_holding_closed")!);
        // The lifecycle list gives the same reason for the two actions and none for expiry.
        Equal(FuturesOptionStore.EarlyExerciseRefusal("option.exercise","european"),"option_exercise_style");
        Equal(FuturesOptionStore.EarlyExerciseRefusal("option.assignment","european"),"option_exercise_style");
        foreach(var (action,style) in new[]{("option.expiry","european"),("option.exercise","american"),("option.assignment","american")})
            if(FuturesOptionStore.EarlyExerciseRefusal(action,style) is not null)throw new Exception($"{action} of a {style} holding was refused by style.");
    }
}
