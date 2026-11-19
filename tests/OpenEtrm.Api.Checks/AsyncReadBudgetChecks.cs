using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;

internal static class AsyncReadBudgetChecks
{
    internal static async Task Run()
    {
        await using var channel=new AsyncReadChannel(Guid.NewGuid(),CancellationToken.None);channel.Connect();
        var emitted=0;const int total=8192;
        IEnumerable<string> Rows()
        {
            var row=new string('x',8192);
            for(var i=0;i<total;i++){emitted++;yield return row;}
        }
        var request=new AsyncReadRequest(channel.Id,Guid.NewGuid(),"curve.evaluate.v1",
            JsonSerializer.SerializeToElement(new{snapshotId=Guid.NewGuid(),curve="synthetic_power"}));
        var plan=new ReadPlan(AsyncReadContract.Decode(request),_=>Task.FromResult(new ReadOutcome(new{rows=Rows()},(_,_)=>Task.CompletedTask)));
        channel.Submit(request,plan);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response=await channel.Read(timeout.Token);
        if(response.State!="failed"||response.Error?.Code!="async_result_size"||response.Result is not null)
            throw new Exception("An oversized read must retain its structured refusal and disclose no partial result.");
        if(emitted>=total)throw new Exception($"The response budget must stop serializing an oversized read, rather than expanding all {emitted} rows first.");
        Console.WriteLine($"PASS async response budget: expansion stops at {emitted} of {total} synthetic rows with the existing structured refusal");
        await LargeDom();
        await LargeToken();
        await Cancellation();
    }
    static async Task LargeDom()
    {
        var rows=new JsonArray();var row=new string('x',8192);
        for(var i=0;i<8192;i++)rows.Add(row);
        var value=new JsonObject{["rows"]=rows};
        await Check(value);
        using var document=JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value));
        await Check(document.RootElement);
        static async Task Check(object value)
        {
            await using var channel=new AsyncReadChannel(Guid.NewGuid(),CancellationToken.None);channel.Connect();
            var request=new AsyncReadRequest(channel.Id,Guid.NewGuid(),"curve.evaluate.v1",JsonSerializer.SerializeToElement(new{snapshotId=Guid.NewGuid(),curve="synthetic_power"}));
            var plan=new ReadPlan(AsyncReadContract.Decode(request),_=>Task.FromResult(new ReadOutcome(value,(_,_)=>Task.CompletedTask)));
            var before=GC.GetTotalAllocatedBytes(true);channel.Submit(request,plan);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));var response=await channel.Read(timeout.Token);
            var allocated=GC.GetTotalAllocatedBytes(true)-before;
            if(response.Error?.Code!="async_result_size"||response.Result is not null||allocated>80L*1024*1024)
                throw new Exception($"An oversized {value.GetType().Name} must stop before buffering its complete encoded payload: {allocated} bytes.");
            Console.WriteLine($"PASS oversized {value.GetType().Name}: bounded serialization allocation {allocated} bytes");
        }
    }
    static async Task LargeToken()
    {
        await using var channel=new AsyncReadChannel(Guid.NewGuid(),CancellationToken.None);channel.Connect();
        var value=new string('x',AsyncReadChannel.MaximumResultBytes*2);
        var request=new AsyncReadRequest(channel.Id,Guid.NewGuid(),"curve.evaluate.v1",JsonSerializer.SerializeToElement(new{snapshotId=Guid.NewGuid(),curve="synthetic_power"}));
        var plan=new ReadPlan(AsyncReadContract.Decode(request),_=>Task.FromResult(new ReadOutcome(value,(_,_)=>Task.CompletedTask)));
        var before=GC.GetTotalAllocatedBytes(true);channel.Submit(request,plan);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));var response=await channel.Read(timeout.Token);
        var allocated=GC.GetTotalAllocatedBytes(true)-before;
        if(response.Error?.Code!="async_result_size"||response.Result is not null||allocated>8*1024*1024)
            throw new Exception($"A definitely oversized string must refuse before allocating its encoded token: {allocated} bytes.");
        Console.WriteLine($"PASS oversized token: refused without encoded copy; {allocated} allocated bytes");
    }
    static async Task Cancellation()
    {
        await using var channel=new AsyncReadChannel(Guid.NewGuid(),CancellationToken.None);channel.Connect();
        var request=new AsyncReadRequest(channel.Id,Guid.NewGuid(),"curve.evaluate.v1",JsonSerializer.SerializeToElement(new{snapshotId=Guid.NewGuid(),curve="synthetic_power"}));
        var emitted=0;
        IEnumerable<string> Rows()
        {
            for(var i=0;i<4096;i++)
            {
                emitted++;if(emitted==50)channel.Cancel(request.RequestId);
                yield return new string('x',512);
            }
        }
        channel.Submit(request,new(AsyncReadContract.Decode(request),_=>Task.FromResult(new ReadOutcome(new{rows=Rows()},(_,_)=>Task.CompletedTask))));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));var response=await channel.Read(timeout.Token);
        if(response.State!="cancelled"||response.Error?.Code!="request_cancelled"||response.Result is not null||emitted==4096)
            throw new Exception("Cancellation during serialization must stop expansion and publish no partial result.");
        Console.WriteLine($"PASS serialization cancellation: stopped at {emitted} synthetic rows");
    }

}
