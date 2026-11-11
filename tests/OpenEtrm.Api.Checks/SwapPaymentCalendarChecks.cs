using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class SwapPaymentCalendarChecks
{
    internal static void Run()
    {
        if(SwapStore.PaymentCalendarCode(null) is not null)
            throw new Exception("A payment rule without a calendar acquired one.");
        if(SwapStore.PaymentCalendarCode("test_business")!="test_business")
            throw new Exception("A chosen payment calendar was not used exactly.");
        foreach(var blank in new[]{""," "})
        {
            try{SwapStore.PaymentCalendarCode(blank);}
            catch(MarketDataException e) when(e.Code=="payment_calendar"&&e.Message.Contains("payment calendar",StringComparison.Ordinal)){continue;}
            throw new Exception("An unchosen payment calendar was not refused with a reason that names it.");
        }
        Console.WriteLine("PASS swap payment calendar: an unchosen calendar is refused by name, not as a missing side reference");
    }
}
