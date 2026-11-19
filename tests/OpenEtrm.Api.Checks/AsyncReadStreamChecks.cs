using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenEtrm.Api;

internal static class AsyncReadStreamChecks
{
    internal static async Task Run()
    {
        var id=Guid.NewGuid();var snapshot=Guid.NewGuid();
        var request=new AsyncReadRequest(id,Guid.NewGuid(),"curve.evaluate.v1",JsonSerializer.SerializeToElement(new{snapshotId=snapshot,curve="synthetic"}));
        var plan=new ReadPlan(AsyncReadContract.Decode(request),_=>throw new InvalidOperationException("Transport must not reevaluate a completed result."));
        var payload=JsonSerializer.SerializeToElement(new{points=new string('x',4*1024*1024),label="synthetic λ \"\\\n\r",missing=(string?)null});
        var completed=new AsyncReadCompletion(request,plan,"completed",JsonSerializer.SerializeToUtf8Bytes(payload,AsyncReadContract.Json),null);
        using var sink=new BoundedSink(5*1024*1024);var context=new DefaultHttpContext();context.Response.Body=sink;
        var before=GC.GetTotalAllocatedBytes(true);
        await Write(context,id,completed,CancellationToken.None);
        var allocated=GC.GetTotalAllocatedBytes(true)-before;
        if(allocated>1024*1024)throw new Exception($"A completed UTF8 result must not allocate another whole response while streaming: {allocated} bytes.");
        var bytes=sink.GetBuffer().AsSpan(0,(int)sink.Length);
        var prefix="event: response\ndata: "u8.ToArray();
        if(!bytes.StartsWith(prefix)||!bytes.EndsWith("\n\n"u8))throw new Exception("A response must be one complete SSE event.");
        using(var document=JsonDocument.Parse(sink.GetBuffer().AsMemory(prefix.Length,(int)sink.Length-prefix.Length-2)))
        {
            var wire=document.RootElement;
            if(wire.GetProperty("protocol").GetString()!=AsyncReadChannel.Protocol||wire.GetProperty("channelId").GetGuid()!=id||
                wire.GetProperty("requestId").GetGuid()!=request.RequestId||wire.GetProperty("snapshotId").GetGuid()!=snapshot||
                wire.GetProperty("operation").GetString()!=request.Operation||wire.GetProperty("state").GetString()!="completed"||
                wire.GetProperty("error").ValueKind!=JsonValueKind.Null||!JsonElement.DeepEquals(wire.GetProperty("result"),payload))
                throw new Exception("Streaming must preserve the complete correlated JSON value, escaping and null fields.");
        }
        using var cancellation=new CancellationTokenSource();using var partial=new BoundedSink(5*1024*1024,cancellation);
        context=new DefaultHttpContext();context.Response.Body=partial;
        try{await Write(context,id,completed,cancellation.Token);throw new Exception("A cancelled response must stop writing.");}
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){}
        if(partial.Length!=partial.CancelledAt||partial.GetBuffer().AsSpan(0,(int)partial.Length).EndsWith("\n\n"u8))
            throw new Exception("Cancellation must neither write more bytes nor terminate a partial event as successful.");
        using var errorSink=new BoundedSink(4096);context=new DefaultHttpContext();context.Response.Body=errorSink;
        var expectedError=new AsyncReadError("synthetic_refusal","synthetic λ \"\\\n");
        var failed=completed with{State="failed",Result=null,Error=expectedError};
        await Write(context,id,failed,CancellationToken.None);
        var text=Encoding.UTF8.GetString(errorSink.GetBuffer(),0,(int)errorSink.Length);
        using(var document=JsonDocument.Parse(text[prefix.Length..^2]))
        {
            var wire=document.RootElement;
            if(wire.GetProperty("result").ValueKind!=JsonValueKind.Null||wire.GetProperty("error").GetProperty("code").GetString()!=expectedError.Code||
                wire.GetProperty("error").GetProperty("message").GetString()!=expectedError.Message)
                throw new Exception("Refusals must retain their structured error and null result.");
        }
        using var slow=new SlowSink();context=new DefaultHttpContext();context.Response.Body=slow;
        using var outer=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try{await Write(context,id,failed,outer.Token);throw new Exception("A stalled event must have a bounded write lifetime.");}
        catch(OperationCanceledException)when(!outer.IsCancellationRequested){}
        Console.WriteLine($"PASS async UTF8 response: 4MiB synthetic value, bounded writes, exact JSON, cancellation and one event deadline; streaming allocation {allocated} bytes");
        await ResultBudget();
        await NestingBudget();
    }
    static Task Write(HttpContext context,Guid id,AsyncReadCompletion response,CancellationToken ct)=>
        AsyncReads.Write(context,"response",new AsyncReadFrame(AsyncReadChannel.Protocol,id,response.Request.RequestId,
            response.Request.Operation,response.State,response.Plan.Input.SnapshotId,response.Result,response.Error),ct);
    static async Task NestingBudget()
    {
        await using var channel=new AsyncReadChannel(Guid.NewGuid(),CancellationToken.None);channel.Connect();
        foreach(var depth in new[]{64,65})
        {
            using var value=JsonDocument.Parse(new string('[',depth)+"0"+new string(']',depth),new JsonDocumentOptions{MaxDepth=65});
            var request=new AsyncReadRequest(channel.Id,Guid.NewGuid(),"curve.evaluate.v1",JsonSerializer.SerializeToElement(new{snapshotId=Guid.NewGuid(),curve="synthetic"}));
            var plan=new ReadPlan(AsyncReadContract.Decode(request),_=>Task.FromResult(new ReadOutcome(value.RootElement,(_,_)=>Task.CompletedTask)));
            channel.Submit(request,plan);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));var response=await channel.Read(timeout.Token);
            if(depth==64)
            {
                if(response.State!="completed")throw new Exception("The existing 64-level result contract must complete.");
                using var sink=new BoundedSink(4096);var context=new DefaultHttpContext();context.Response.Body=sink;
                await Write(context,channel.Id,response,CancellationToken.None);
                var prefix="event: response\ndata: "u8.Length;
                using var wire=JsonDocument.Parse(sink.GetBuffer().AsMemory(prefix,(int)sink.Length-prefix-2),new JsonDocumentOptions{MaxDepth=65});
                if(!JsonElement.DeepEquals(wire.RootElement.GetProperty("result"),value.RootElement))
                    throw new Exception("The original 64-level result must survive its extra envelope level unchanged.");
            }
            else if(response.State!="failed"||response.Result is not null||response.Error?.Code!="async_read_failed")
                throw new Exception("A result beyond the existing 64-level result limit must fail before response publication.");
        }
        Console.WriteLine("PASS async result nesting: existing 64-level result bound and extra envelope level preserved");
    }
    static async Task ResultBudget()
    {
        await using var channel=new AsyncReadChannel(Guid.NewGuid(),CancellationToken.None);channel.Connect();
        foreach(var extra in new[]{-1,0,1})
        {
            // The escaped non-ASCII character occupies six serialized bytes,
            // so a character count cannot accidentally implement this limit.
            var value=new string('x',AsyncReadChannel.MaximumResultBytes-2-6+extra)+"λ";
            var request=new AsyncReadRequest(channel.Id,Guid.NewGuid(),"curve.evaluate.v1",JsonSerializer.SerializeToElement(new{snapshotId=Guid.NewGuid(),curve="synthetic"}));
            channel.Submit(request,new(AsyncReadContract.Decode(request),_=>Task.FromResult(new ReadOutcome(value,(_,_)=>Task.CompletedTask))));
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));var response=await channel.Read(timeout.Token);
            if(extra<=0)
            {
                if(response.State!="completed"||response.Result?.Length!=AsyncReadChannel.MaximumResultBytes+extra)
                    throw new Exception("Valid near-limit results must retain their exact serialized UTF8 bytes.");
            }
            else if(response.State!="failed"||response.Error?.Code!="async_result_size"||response.Result is not null)
                throw new Exception("An over-limit result must retain its structured refusal without sending its payload.");
        }
        Console.WriteLine("PASS async result limit: exact serialized UTF8 byte boundary and structured oversize refusal");
    }
    sealed class BoundedSink(int capacity,CancellationTokenSource? cancel=null):MemoryStream(capacity)
    {
        internal long CancelledAt;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default)
        {
            ct.ThrowIfCancellationRequested();
            if(buffer.Length>64*1024)throw new Exception($"Response write exceeded 64KiB: {buffer.Length} bytes.");
            base.Write(buffer.Span);
            if(cancel is not null&&Length>=64*1024){CancelledAt=Length;cancel.Cancel();}
            return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken ct)=>WriteAsync(buffer.AsMemory(offset,count),ct).AsTask();
    }
    sealed class SlowSink:MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default)
        {await Task.Delay(TimeSpan.FromSeconds(2),ct);base.Write(buffer.Span);}
        public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken ct)=>WriteAsync(buffer.AsMemory(offset,count),ct).AsTask();
    }
}
