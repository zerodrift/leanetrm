using System.Text;
using System.Text.Json;

namespace OpenEtrm.Api;

internal sealed record AsyncReadFrame(string Protocol,Guid ChannelId,Guid RequestId,string Operation,string State,Guid? SnapshotId,ReadOnlyMemory<byte>? Result,AsyncReadError? Error);

internal static class JsonReadTransport
{
    static readonly HashSet<string> posts=new(StringComparer.Ordinal)
    {
        "/evaluate","/history","/market/curve-shapes","/trading/directory","/trading/freshness","/trading/positions","/trading/workspace","/trading/blotter",
        "/futures/editor","/swaps/editor","/options/strategy/analyze","/portfolio/risk/catalog","/portfolio/risk","/portfolio/risk/scope","/portfolio/risk/history"
    };
    internal static bool Compress(HttpContext context)
    {
        var request=context.Request;
        if(!request.Path.StartsWithSegments("/api",out var path)&&!request.Path.StartsWithSegments("/examples/exchange/api",out path))return false;
        if(HttpMethods.IsPost(request.Method))return posts.Contains(path.Value??"");
        return HttpMethods.IsGet(request.Method)&&(path.Equals("/async-reads/events",StringComparison.Ordinal)||path.Equals("/workspace",StringComparison.Ordinal)||
            path.Equals("/workspace/catalog",StringComparison.Ordinal)||path.Equals("/workspace/navigation",StringComparison.Ordinal));
    }
    static ReadOnlyMemory<byte> EncodeEvent(string? name,object? body)
    {
        if(name is null)return ": ping\n\n"u8.ToArray();
        return Encoding.UTF8.GetBytes($"event: {name}\ndata: {JsonSerializer.Serialize(body,JsonSerializerOptions.Web)}\n\n");
    }
    internal static async Task WriteEvent(Stream stream,string? name,object? body,CancellationToken ct)
    {
        if(body is not AsyncReadFrame frame)
        {
            await stream.WriteAsync(EncodeEvent(name,body),ct);return;
        }
        if(name!="response")throw new InvalidOperationException("A read response requires its correlated response event.");
        var header=JsonSerializer.SerializeToUtf8Bytes(new{protocol=frame.Protocol,channelId=frame.ChannelId,requestId=frame.RequestId,
            operation=frame.Operation,state=frame.State,snapshotId=frame.SnapshotId},JsonSerializerOptions.Web);
        await stream.WriteAsync("event: response\ndata: "u8.ToArray(),ct);
        await stream.WriteAsync(header.AsMemory(0,header.Length-1),ct);
        await stream.WriteAsync(",\"result\":"u8.ToArray(),ct);
        await Chunks(frame.Result??"null"u8.ToArray());
        await stream.WriteAsync(",\"error\":"u8.ToArray(),ct);
        await Chunks(JsonSerializer.SerializeToUtf8Bytes(frame.Error,JsonSerializerOptions.Web));
        await stream.WriteAsync("}\n\n"u8.ToArray(),ct);

        async Task Chunks(ReadOnlyMemory<byte> bytes)
        {
            const int chunk=16*1024;
            while(!bytes.IsEmpty)
            {
                ct.ThrowIfCancellationRequested();
                var length=Math.Min(chunk,bytes.Length);
                await stream.WriteAsync(bytes[..length],ct);bytes=bytes[length..];
            }
        }
    }
}
