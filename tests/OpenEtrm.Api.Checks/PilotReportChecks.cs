using OpenEtrm.Api.Presentation;
using OpenEtrm.MarketData;

internal static class PilotReportChecks
{
    public static void Run()
    {
        var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        var quote=new PriceUnit(new(reference,"currency"),new(reference,"quantity"),ExactDecimal.Parse("0.01"));
        if(PriceUnitLabel.Format(quote,"USD","MMBtu")!="0.01 USD / MMBtu")
            throw new Exception("A quote multiplier must scale the cash numerator, including in report labels.");
        Console.WriteLine("PASS report quote labels retain the cash-numerator multiplier");
    }
}
