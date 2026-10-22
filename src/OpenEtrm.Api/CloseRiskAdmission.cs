using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public static class CloseRiskAdmission
{
    public const long MaximumRowScenarios=2_000_000_000;
    public static void RequireRowScenarios(long work,int factors)
    {
        if(work<0||work>MaximumRowScenarios||factors is <0 or >4096)
            throw new MarketDataException("risk_size","The declared operation exceeds two billion row-scenarios or 4,096 native factors.");
    }
}
