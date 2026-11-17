using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class LifecycleContextIngressChecks
{
    internal static void Run()
    {
        var id=Guid.NewGuid();var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string Wire(string at)=>JsonSerializer.Serialize(new{contextId=id,asOf=at,businessDate="2032-01-15",snapshotId=id},options);
        foreach(var text in new[]{"2032-01-15T10:00:00.123456Z","2032-01-15T10:00:00.123456+00:00","2032-01-15T10:00:00.1234560+00:00"})
        {
            var context=JsonSerializer.Deserialize<LifecycleReadContext>(Wire(text),options)!;
            if(context.AsOf!=DateTimeOffset.Parse("2032-01-15T10:00:00.123456Z")||context.ContextId!=id)
                throw new Exception("Lifecycle context ingress must preserve exact UTC value and compatible explicit zero-offset syntax.");
            if(JsonSerializer.SerializeToElement(context,options).GetProperty("asOf").GetString()!=JsonSerializer.SerializeToElement(context.AsOf,options).GetString())
                throw new Exception("The lifecycle ingress guard must retain ordinary DateTimeOffset response serialization.");
        }
        foreach(var text in new[]{"2032-01-15T10:00:00.0000001Z","2032-01-15T10:00:00.00000001Z","2032-01-15T10:00:00.12345601Z","2032-01-15T11:00:00+01:00"})
        {
            try{JsonSerializer.Deserialize<LifecycleReadContext>(Wire(text),options);}
            catch(MarketDataException error)when(error.Code=="lifecycle_context"){continue;}
            throw new Exception("A raw lifecycle business clock cannot lose nonzero sub-microsecond digits before source and review-hash validation.");
        }
        Console.WriteLine("PASS lifecycle context ingress: original raw clock precision, UTC compatibility and unchanged response serialization");
    }
}
