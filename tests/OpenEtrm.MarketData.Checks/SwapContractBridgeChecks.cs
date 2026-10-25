using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class SwapContractBridgeChecks
{
    public static int Run()
    {
        var currency=new IndexFixingChecks.FixingFixture(monthly:true).Swap().Currency;
        ExactMoney M(string x)=>ExactMoney.Parse(x,currency);
        SwapLifecycleValue Value(string remaining,string cash,string realized)=>new(M(remaining),M("0"),M("0"),M("0"),M(realized),M(remaining).Add(M(cash)).Subtract(M(realized)),M(cash),M("0"),M("0"),M("0"),[]);
        var fresh=SwapContractBridge.Calculate(null,Value("80","20","30"),null,M("20"));
        Equal(fresh.NewTradeEconomics,"100");Equal(fresh.ContinuingEconomics,"0");Equal(fresh.Total.EconomicPnl,"100");
        // Prior terms gain 15 at closing; an independently priced amendment costs 30.
        var amended=SwapContractBridge.Calculate(Value("100","0","0"),Value("65","20","40"),Value("95","20","40"),M("20"));
        Equal(amended.ContinuingEconomics,"15");Equal(amended.ContractualAmendment,"-30");Equal(amended.Total.EconomicPnl,"-15");
        Equal(amended.Total.RealizedTransfer,"40");Equal(amended.Total.UnrealizedMovement,"-55");
        // Clearing/netting an obligation is a balance/cash exchange, with no earnings.
        var paid=SwapContractBridge.Calculate(Value("100","0","100"),Value("0","100","100"),null,M("100"));
        Equal(paid.Total.EconomicPnl,"0");Equal(paid.ContinuingEconomics,"0");
        Console.WriteLine("PASS closing-context contractual changes: independent new-trade/amendment/continuing economics and payment neutrality");return 10;
    }
    static void Equal(ExactMoney actual,string expected){if(actual.ToExactString()!=expected)throw new Exception($"Expected {expected}, got {actual.ToExactString()}");}
}
