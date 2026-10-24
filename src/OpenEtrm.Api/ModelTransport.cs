using System.Net;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;

namespace OpenEtrm.Api;

// How every provider adapter sends and reads (docs/ai-assistant.md §5.2, §7.5): through one handler that
// ignores proxies and follows no redirect; a response body held to the runtime set's byte bound and idle
// timeout; server-sent events parsed by the platform's reader with strict UTF-8, so a malformed byte is
// never replaced; and JSON whose repeated names are refused rather than resolved.
static class ModelTransport
{
    static readonly UTF8Encoding Strict=new(false,true);
    static readonly JsonDocumentOptions Unambiguous=new(){AllowDuplicateProperties=false};

    // Proxy settings in the environment are ignored, redirects are not followed, certificates are validated as
    // usual and no tracing header is added. With a check endpoint, each request goes to that origin instead,
    // keeping its adapter's path.
    public static HttpMessageHandler Handler(Uri? checkEndpoint)
    {
        var sockets=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,
            ConnectTimeout=RuntimeSet.ProviderConnect,ActivityHeadersPropagator=null};
        return checkEndpoint is null?sockets:new CheckEndpointHandler(checkEndpoint,sockets);
    }

    // The one provider endpoint override, which only owned checks set, so they can reach a fake provider in either mode
    // (docs/ai-assistant.md §5.2, §11): an http or https origin whose host is a loopback IP literal, written exactly as
    // the origin it parses to. Anything else stops the API from starting.
    public static Uri? CheckEndpoint(string? configured)
    {
        if(configured is null)return null;
        return Uri.TryCreate(configured,UriKind.Absolute,out var origin)&&origin.Scheme is "http" or "https"&&origin.UserInfo.Length==0
            &&origin.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6&&IPAddress.TryParse(origin.DnsSafeHost,out var address)
            &&IPAddress.IsLoopback(address)&&origin.GetLeftPart(UriPartial.Authority)==configured
            ?origin:throw new InvalidOperationException("A provider endpoint override must be a loopback IP-literal origin.");
    }

    sealed class CheckEndpointHandler(Uri origin,HttpMessageHandler inner):DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            request.RequestUri=new Uri(origin,request.RequestUri!.PathAndQuery);
            return base.SendAsync(request,ct);
        }
    }

    // Every name and string is decoded once here, so an escaped lone surrogate is malformed JSON
    // rather than a failure wherever the value is later read.
    public static JsonDocument Parse(string json)
    {
        JsonDocument? document=null;
        try
        {
            document=JsonDocument.Parse(json,Unambiguous);
            Decode(document.RootElement);
            return document;
        }
        catch(InvalidOperationException)
        {
            document?.Dispose();
            throw new JsonException("The provider sent text that is not valid UTF-16.");
        }
    }

    static void Decode(JsonElement value)
    {
        switch(value.ValueKind)
        {
            case JsonValueKind.Object:foreach(var property in value.EnumerateObject()){_=property.Name;Decode(property.Value);}break;
            case JsonValueKind.Array:foreach(var item in value.EnumerateArray())Decode(item);break;
            case JsonValueKind.String:_=value.GetString();break;
        }
    }

    public static async Task<Stream> Body(HttpResponseMessage response,ModelTransportLimits limits,CancellationToken ct)=>
        new BoundedStream(await response.Content.ReadAsStreamAsync(ct),limits);

    public static IAsyncEnumerator<SseItem<string>> Events(Stream body,CancellationToken ct)=>
        SseParser.Create(body,(_,data)=>Strict.GetString(data)).EnumerateAsync(ct).GetAsyncEnumerator(ct);

    public static async Task<string> Text(Stream body,CancellationToken ct)
    {
        using var reader=new StreamReader(body,Strict,false);
        return await reader.ReadToEndAsync(ct);
    }

    // Reads at most the byte bound and waits at most the idle timeout for each read. Past the bound
    // the call ends as too large; a read past the timeout is cancelled, which the adapter tells apart
    // from the caller's cancellation.
    sealed class BoundedStream(Stream inner,ModelTransportLimits limits):Stream
    {
        long total;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
        {
            // One byte beyond the bound is requested, so a body of exactly the bound still ends cleanly.
            var room=(int)Math.Min(buffer.Length,limits.ResponseBytes-total+1);
            using var idle=CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(limits.Idle);
            var read=await inner.ReadAsync(buffer[..room],idle.Token);
            total+=read;
            return total>limits.ResponseBytes?throw new ModelStreamException(ModelFailure.ResponseTooLarge):read;
        }

        public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken ct)=>ReadAsync(buffer.AsMemory(offset,count),ct).AsTask();
        // A synchronous read could not honor the idle timeout.
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override bool CanRead=>true;
        public override bool CanSeek=>false;
        public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();
        public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
    }
}

// A provider outcome found while reading a response; the adapter turns it into the call's ending.
sealed class ModelStreamException(ModelFailure failure):Exception(failure.ToString())
{
    public ModelFailure Failure {get;}=failure;
}
