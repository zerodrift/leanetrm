using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasInstantIngressChecks
{
    internal static void Run()
    {
        var id=Guid.NewGuid();
        JsonElement Reservation(string at)=>JsonSerializer.SerializeToElement(new{lotId=id,businessKey="synthetic_reserve",quantity="1",effectiveAt=at});
        var valid=GasOwnedOperations.Selection("reserve",Reservation("2032-01-15T10:00:00.1234560+00:00"));
        if(valid.Text("effectiveAt")!="2032-01-15T10:00:00.123456Z")throw new Exception("Exact microseconds with a redundant zero digit must retain their original value.");
        foreach(var fraction in new[]{"0000001","00000001","12345601"})
        {
            try{GasOwnedOperations.Selection("reserve",Reservation("2032-01-15T10:00:00."+fraction+"Z"));}
            catch(MarketDataException){continue;}
            throw new Exception("A nonzero sub-microsecond stock instruction cannot be truncated into accepted time.");
        }
        Console.WriteLine("PASS shared gas instant ingress: original-text precision, exact UTC normalization and nonzero sub-microsecond refusal");
    }
}
