using System.Buffers;
using System.Text;
using System.Text.Json;

namespace OpenEtrm.Api;

internal sealed record AsyncReadFrame(string Protocol,Guid ChannelId,Guid RequestId,string Operation,string State,Guid? SnapshotId,JsonElement? Result,AsyncReadError? Error);

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
    internal static ReadOnlyMemory<byte> EncodeEvent(string? name,object? body)
    {
        if(name is null)return ": ping\n\n"u8.ToArray();
        var buffer=new ArrayBufferWriter<byte>();buffer.Write(Encoding.UTF8.GetBytes($"event: {name}\ndata: "));
        using(var writer=new Utf8JsonWriter(buffer))
        {
            if(body is AsyncReadFrame frame)
            {
                writer.WriteStartObject();writer.WriteString("protocol",frame.Protocol);writer.WriteString("channelId",frame.ChannelId);
                writer.WriteString("requestId",frame.RequestId);writer.WriteString("operation",frame.Operation);writer.WriteString("state",frame.State);
                if(frame.SnapshotId is {} snapshot)writer.WriteString("snapshotId",snapshot);else writer.WriteNull("snapshotId");
                writer.WritePropertyName("result");
                // Completion owns compact validated JSON. Preserve it without walking
                // and serializing the result document again at stream publication.
                if(frame.Result is {} result)writer.WriteRawValue(result.GetRawText(),skipInputValidation:true);else writer.WriteNullValue();
                writer.WritePropertyName("error");JsonSerializer.Serialize(writer,frame.Error,JsonSerializerOptions.Web);writer.WriteEndObject();
            }
            else JsonSerializer.Serialize(writer,body,JsonSerializerOptions.Web);
        }
        buffer.Write("\n\n"u8);return buffer.WrittenMemory;
    }
}
