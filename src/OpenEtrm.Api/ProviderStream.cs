using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenEtrm.Api;

// Common transport for the Responses and generateContent protocols. Parsers alone decide what
// constitutes a final result. EOF, cancellation and errors cannot turn partial counters into a bill.
abstract class ProviderStream(HttpMessageHandler handler,ModelTransportLimits limits)
{
    protected abstract HttpRequestMessage Compose(ModelRequest request,string key);
    protected abstract ProviderParser Parser(ModelRequest request);
    public async IAsyncEnumerable<ModelEvent> Stream(ModelRequest request,string key,[EnumeratorCancellation] CancellationToken ct)
    {
        using var message=Compose(request,key);
        await using var call=new StreamCall(handler,limits,message,Parser(request));
        while(true)
        {
            var batch=await call.Next(ct);
            foreach(var e in batch)
            {
                yield return e;
                if(e is not ModelTextDelta)yield break;
            }
        }
    }
    protected static HttpRequestMessage Post(Uri endpoint,JsonObject body,string header,string key)
    {
        var request=new HttpRequestMessage(HttpMethod.Post,endpoint){Content=new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString()))};
        request.Content.Headers.ContentType=new("application/json");
        if(header=="Authorization")request.Headers.Authorization=new("Bearer",key);else request.Headers.Add(header,key);
        return request;
    }
    sealed class StreamCall(HttpMessageHandler handler,ModelTransportLimits limits,HttpRequestMessage message,ProviderParser parser):IAsyncDisposable
    {
        readonly HttpMessageInvoker http=new(handler,false);
        HttpResponseMessage? response;
        IAsyncEnumerator<SseItem<string>>? events;
        bool sent;
        public async Task<ModelEvent[]> Next(CancellationToken ct)
        {
            try
            {
                if(ct.IsCancellationRequested)return [Failed(ModelFailure.Cancelled)];
                if(response is null)
                {
                    sent=true;
                    using var idle=CancellationTokenSource.CreateLinkedTokenSource(ct);idle.CancelAfter(limits.Idle);
                    try{response=await http.SendAsync(message,idle.Token);}
                    catch(HttpRequestException e) when(e.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError)
                    {sent=false;return [Failed(ModelFailure.Transport)];}
                    if(response.StatusCode!=HttpStatusCode.OK)return [Failed(await Refusal(ct))];
                    if(response.Content.Headers.ContentType?.MediaType!="text/event-stream"||response.Content.Headers.ContentEncoding.Count>0)return [Failed(ModelFailure.Transport)];
                    events=ModelTransport.Events(await ModelTransport.Body(response,limits,ct),ct);
                }
                if(!await events!.MoveNextAsync())return [parser.End()];
                using var json=ModelTransport.Parse(events.Current.Data);
                if(json.RootElement.ValueKind!=JsonValueKind.Object)throw ProviderParser.Unreadable();
                return parser.Apply(events.Current.EventType,json.RootElement);
            }
            catch(ModelStreamException e){return [Failed(e.Failure)];}
            catch(JsonException){return [Failed(ModelFailure.Transport)];}
            catch(Exception e) when(e is OperationCanceledException or IOException or HttpRequestException or DecoderFallbackException)
            {return [Failed(ct.IsCancellationRequested?ModelFailure.Cancelled:ModelFailure.Transport)];}
        }
        async Task<ModelFailure> Refusal(CancellationToken ct)
        {
            var failure=ProviderParser.HttpFailure((int)response!.StatusCode);
            if(response.StatusCode!=HttpStatusCode.BadRequest)return failure;
            // Only the documented classification code is interpreted. Malformed or unreadable
            // classification leaves the HTTP status authoritative; no provider text escapes.
            try
            {
                using var body=ModelTransport.Parse(await ModelTransport.Text(await ModelTransport.Body(response,limits,ct),ct));
                if(body.RootElement.ValueKind==JsonValueKind.Object&&body.RootElement.TryGetProperty("error",out var error)
                    &&error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("code",out var code)
                    &&code.ValueKind==JsonValueKind.String&&code.GetString()=="context_length_exceeded")return ModelFailure.ContextWindow;
            }
            catch(Exception e) when(e is JsonException or DecoderFallbackException or IOException or HttpRequestException||e is OperationCanceledException&&!ct.IsCancellationRequested)
            {return failure;}
            return failure;
        }
        ModelFailed Failed(ModelFailure failure)=>new(failure,!sent?ModelUsage.NothingSent(parser.Mapping)
            :(parser.FinalUsage??ModelUsage.Unknown(parser.Mapping)) with{Status=failure==ModelFailure.ModelMismatch?UsageStatus.Unrecognized:parser.FinalUsage?.Status??UsageStatus.Unknown},parser.Served);
        public async ValueTask DisposeAsync()
        {
            if(events is not null)await events.DisposeAsync();
            response?.Dispose();message.Dispose();http.Dispose();
        }
    }
}

abstract class ProviderParser(ModelRequest request,string mapping)
{
    protected ModelRequest Request {get;}=request;
    public string Mapping {get;}=mapping;
    public ModelUsage? FinalUsage {get;protected set;}
    public string? Served {get;private set;}
    protected void Identify(string? model)
    {
        if(model is null)throw Unreadable();
        var previous=Served;
        Served=model;
        if(model!=Request.Model.Identifier&&!Request.Model.AcceptedServed.Contains(model))throw new ModelStreamException(ModelFailure.ModelMismatch);
        if(previous is not null&&previous!=model)throw Unreadable();
    }
    public abstract ModelEvent[] Apply(string name,JsonElement value);
    public virtual ModelEvent End()=>new ModelFailed(ModelFailure.Transport,FinalUsage??ModelUsage.Unknown(Mapping),Served);
    public static ModelFailure HttpFailure(int status)=>status switch
    {
        401 or 402 or 403=>ModelFailure.Authentication,429=>ModelFailure.RateLimited,>=500=>ModelFailure.Overloaded,
        >=400 and <500=>ModelFailure.InvalidRequest,_=>ModelFailure.Transport
    };
    public static ModelStreamException Unreadable()=>new(ModelFailure.Transport);
    protected static JsonElement Get(JsonElement value,string name)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var found)?found:default;
    protected static string? Text(JsonElement value,string name)=>Get(value,name) is {ValueKind:JsonValueKind.String} found?found.GetString():null;
    protected static long? Count(JsonElement value,string name)=>Get(value,name) is {ValueKind:JsonValueKind.Number} n&&n.TryGetInt64(out var count)&&count>=0?count:null;
    protected static TokenCount Tokens(long? n)=>n is {} count&&count>=0?TokenCount.Reported(count):TokenCount.Unknown;
    protected static bool Inert(JsonElement value)=>value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined=>true,
        JsonValueKind.Number=>value.GetRawText().Split('e','E')[0].All(c=>c is '0' or '.' or '-'),
        JsonValueKind.Object=>value.EnumerateObject().All(p=>Inert(p.Value)),_=>false
    };
    protected static bool Named(JsonElement value,params string[] names)=>value.ValueKind==JsonValueKind.Object&&value.EnumerateObject().All(p=>names.Contains(p.Name)||Inert(p.Value));
    protected static bool Standard(JsonElement value,string name,string expected)=>Get(value,name).ValueKind is JsonValueKind.Undefined or JsonValueKind.Null||Text(value,name)==expected;
}
