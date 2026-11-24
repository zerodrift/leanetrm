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
    }
}
