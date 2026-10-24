using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class CloseRiskChecks
{
    internal static void Run()
    {
        CloseRiskPreflightChecks.Run();
        CloseRiskAdmission.RequireRowScenarios(2_000_000_000,480);
        try
        {
            CloseRiskAdmission.RequireRowScenarios(2_000_000_001,480);
            throw new Exception("An operation above two billion declared row-scenarios was admitted.");
        }
        catch(MarketDataException e)when(e.Code=="risk_size"){}
        Console.WriteLine("PASS close risk refuses declared row-scenario work before evaluation");
    }
}
