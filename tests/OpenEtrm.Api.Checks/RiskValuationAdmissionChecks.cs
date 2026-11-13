using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class RiskValuationAdmissionChecks
{
    internal static void Run()
    {
        RiskValuationAdmission.Require(700_000_001,40_000_000,100_000,2,1);
        Reject("risk_size",()=>RiskValuationAdmission.Require(700_000_001,0,1,2,3));
        Reject("risk_size",()=>RiskValuationAdmission.Require(1,40_000_000,1,2,3));
        Reject("risk_scenario_capacity",()=>RiskValuationAdmission.Require(1,1,100_000,2,3));
        // Each source partition fits; the two-close operation must still refuse
        // before any valuation when their combined result work exceeds capacity.
        RiskValuationAdmission.Require([new(1,1,150_000)],2);
        Reject("risk_scenario_capacity",()=>RiskValuationAdmission.Require([new(1,1,150_000),new(1,1,150_000)],2));
        Reject("risk_size",()=>RiskValuationAdmission.Require([new(-1,1,1),new(1,1,1)],2));
        Console.WriteLine("PASS risk admission: complete FD passes bound PPA rows, option rollback nodes and retained value work before pricing");
    }
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
