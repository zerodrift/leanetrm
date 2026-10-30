using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class PositionQualificationChecks
{
    internal static async Task Run()
    {
        var positions=Enumerable.Range(0,64).ToImmutableArray();var calls=0;
        var healthy=await ValuationStatus.Qualify(positions,block=>{calls++;return Task.CompletedTask;},default);
        if(calls!=1||!healthy.IsEmpty)throw new Exception("Healthy held bindings must qualify in one shared base valuation, not one valuation per position.");
        calls=0;
        var missing=await ValuationStatus.Qualify(positions,block=>
        {
            calls++;
            if(block.Contains(17))throw new MarketDataException("index_fixing_missing","Synthetic due fixing unavailable.");
            return Task.CompletedTask;
        },default);
        if(missing.Length!=1||missing[0]!=(17,"index_fixing_missing")||calls>14)
            throw new Exception("One unavailable binding must be isolated without valuing each healthy binding separately.");
        var mixed=await ValuationStatus.Qualify(positions,block=>
        {
            if(block.Contains(17))throw new MarketDataException("index_fixing_missing","Synthetic fixing unavailable.");
            if(block.Contains(47))throw new MarketDataException("missing_observation","Synthetic price unavailable.");
            return Task.CompletedTask;
        },default);
        if(!mixed.SequenceEqual(new[]{(17,"index_fixing_missing"),(47,"missing_observation")}))
            throw new Exception("Each excluded held binding must keep its own exact failure reason.");
        foreach(var code in new[]{"contract_observation_source","preview_denied","discount_curve"})
        {
            calls=0;
            await Reject(code,()=>ValuationStatus.Qualify(positions,block=>{calls++;throw new MarketDataException(code,"Synthetic global failure.");},default));
            if(calls!=1)throw new Exception("A malformed definition or authority failure was isolated as a position price failure.");
        }
        await Reject("missing_observation",()=>ValuationStatus.Qualify(positions,block=>
        {
            if(block.Contains(0)&&block.Contains(1))throw new MarketDataException("missing_observation","Synthetic interacting failure.");
            return Task.CompletedTask;
        },default));
        await Reject("missing_observation",()=>ValuationStatus.Qualify(positions,block=>
        {
            if(block.Contains(17))throw new MarketDataException("index_fixing_missing","Synthetic unavailable binding.");
            if(block.Contains(0)&&block.Contains(63))throw new MarketDataException("missing_observation","Synthetic survivor interaction.");
            return Task.CompletedTask;
        },default));
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        try{await ValuationStatus.Qualify(positions,_=>throw new Exception("Cancelled qualification executed work."),cancellation.Token);}
        catch(OperationCanceledException){Console.WriteLine("PASS position qualification: shared healthy base valuation, bounded failed-binding isolation and strict global failures");return;}
        throw new Exception("Cancelled qualification was accepted.");
    }
    private static async Task Reject<T>(string code,Func<Task<T>> action)
    {
        try{await action();}
        catch(MarketDataException error)when(error.Code==code){return;}
        throw new Exception("Expected qualification refusal "+code);
    }
}
