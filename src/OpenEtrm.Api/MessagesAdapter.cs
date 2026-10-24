using System.Collections.Immutable;
using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenEtrm.Api;

// The Anthropic Messages API adapter (docs/ai-assistant.md §6.1) and its usage mapping messages_v1
// (§6.2), from the vendor's documentation as read on 2026-10-20 at platform.claude.com/docs/en:
// api/messages, api/errors, api/service-tiers, api/versioning, and build-with-claude/streaming,
// thinking, effort, fast-mode, prompt-caching, context-windows, handling-stop-reasons and
// refusals-and-fallback. It takes a handler, not a client, because a client buffers the whole
// response before returning it.
public sealed class MessagesAdapter(HttpMessageHandler handler,ModelTransportLimits limits)
{
    public const string Protocol="messages",Mapping="messages_v1";
    public static readonly Uri Endpoint=new("https://api.anthropic.com/v1/messages");
    // Adaptive thinking is always requested, so every model this mapping serves takes an explicit effort.
    static readonly string[] Efforts=["low","medium","high","xhigh","max"];

    public async IAsyncEnumerable<ModelEvent> Stream(ModelRequest request,string key,[EnumeratorCancellation] CancellationToken ct)
    {
        await using var call=new Call(handler,limits,request,Compose(request,key));
        while(true)
        {
            var next=await call.Next(ct);
            if(next is null)continue;
            yield return next;
            if(next is not ModelTextDelta)yield break;
        }
    }

    // Refuses a request this adapter cannot send as given, before anything is sent: an invalid identifier,
    // a reasoning effort it does not send (parameter_required), or a key it cannot use. A malformed
    // request is a defect in its caller.
    public static void Validate(ModelRequest request,string key)
    {
        if(!ModelTarget.Valid(request.Model.Identifier))throw new ModelCallException("model_identifier_invalid",ModelCatalog.Refusal("model_identifier_invalid"));
        if(request.Parameters.Reasoning is not {} effort||!Efforts.Contains(effort))throw new ModelCallException("parameter_required","Choose one of the model's documented reasoning efforts.");
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Parameters.MaxOutputTokens,1);
        if(request.Messages.IsDefaultOrEmpty||request.Tools.IsDefault||request.Model.AcceptedServed.IsDefault||request.System.Length==0||request.Messages[^1] is ModelReply)
            throw new ArgumentException("A request has a system instruction, lists its tools and accepted identifiers, and ends with the user's text or tool results.",nameof(request));
        if(!ModelProviders.KeyFormat().IsMatch(key))throw new ModelCallException("credential_unavailable","The provider key cannot be used.");
    }

    // Every parameter whose vendor default could change behavior or billing is sent: adaptive thinking
    // with its text omitted, an explicit effort, standard-only capacity and global inference. Sampling
    // parameters are never sent: current models accept only their defaults, and earlier ones none with
    // thinking. Neither is speed: some models accept it only with a beta header, and fast mode is an
    // opt-in whose report the usage mapping checks. Nothing is sent when a value is missing.
    static HttpRequestMessage Compose(ModelRequest request,string key)
    {
        Validate(request,key);
        var (model,parameters)=(request.Model.Identifier,request.Parameters);
        var messages=new JsonArray(request.Messages.Select(m=>(JsonNode?)Message(m,model)).ToArray());
        var system=new JsonObject{["type"]="text",["text"]=request.System};
        if(parameters.Caching)
        {
            // Explicit five-minute breakpoints: one after the tools and system prompt, one after the conversation.
            system["cache_control"]=Breakpoint();
            messages[^1]!["content"]!.AsArray()[^1]!["cache_control"]=Breakpoint();
        }
        var body=new JsonObject
        {
            ["model"]=model,["max_tokens"]=parameters.MaxOutputTokens,["stream"]=true,["system"]=new JsonArray(system),["messages"]=messages,
            ["thinking"]=new JsonObject{["type"]="adaptive",["display"]="omitted"},["output_config"]=new JsonObject{["effort"]=parameters.Reasoning},
            ["service_tier"]="standard_only",["inference_geo"]="global"
        };
        if(!request.Tools.IsEmpty)
        {
            body["tools"]=new JsonArray(request.Tools.Select(t=>(JsonNode?)new JsonObject{["name"]=t.Name,["description"]=t.Description,["input_schema"]=JsonNode.Parse(t.InputSchema.GetRawText())}).ToArray());
            body["tool_choice"]=new JsonObject{["type"]="auto",["disable_parallel_tool_use"]=false};
        }
        var message=new HttpRequestMessage(HttpMethod.Post,Endpoint){Content=new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString()))};
        message.Content.Headers.ContentType=new("application/json");
        message.Headers.Add("x-api-key",key);
        message.Headers.Add("anthropic-version","2023-06-01");
        return message;
    }

    static JsonObject Breakpoint()=>new(){["type"]="ephemeral",["ttl"]="5m"};

    // The vendor requires text of at least one character and a user turn of at least one block.
    static JsonObject Message(ModelMessage message,string model)=>message switch
    {
        ModelUserText{Text.Length:>0} text=>new(){["role"]="user",["content"]=new JsonArray(new JsonObject{["type"]="text",["text"]=text.Text})},
        ModelToolResults{Results.IsDefaultOrEmpty:false} results=>new(){["role"]="user",["content"]=new JsonArray(results.Results.Select(r=>(JsonNode?)new JsonObject
            {["type"]="tool_result",["tool_use_id"]=r.RequestId,["content"]=r.Content,["is_error"]=r.Failed}).ToArray())},
        ModelReply reply when reply.Protocol==Protocol&&reply.Model==model=>new(){["role"]="assistant",["content"]=JsonNode.Parse(reply.Content.GetRawText())},
        ModelReply=>throw new ArgumentException("A reply is replayed only to the model that wrote it.",nameof(message)),
        _=>throw new ArgumentException("A message carries text, at least one tool result or a reply this adapter wrote.",nameof(message))
    };

    static ModelFailure Classified(HttpStatusCode status)=>(int)status switch
    {
        401 or 402 or 403=>ModelFailure.Authentication,
        429=>ModelFailure.RateLimited,
        >=400 and <500=>ModelFailure.InvalidRequest,
        // 500 api_error, 504 timeout_error, 529 overloaded_error and any other server status.
        >=500=>ModelFailure.Overloaded,
        // An informational, other success or redirect status is not the documented response; no redirect is followed.
        _=>ModelFailure.Transport
    };

    static ModelFailure Classified(string? type)=>type switch
    {
        "authentication_error" or "billing_error" or "permission_error"=>ModelFailure.Authentication,
        "rate_limit_error"=>ModelFailure.RateLimited,
        "overloaded_error" or "api_error" or "timeout_error"=>ModelFailure.Overloaded,
        "invalid_request_error" or "not_found_error" or "conflict_error" or "request_too_large"=>ModelFailure.InvalidRequest,
        _=>ModelFailure.Transport
    };

    // One call: sends the request, then reads its event stream into text deltas and one ending. Every
    // provider outcome becomes an event; only a defect in this program throws.
    sealed class Call(HttpMessageHandler handler,ModelTransportLimits limits,ModelRequest request,HttpRequestMessage message):IAsyncDisposable
    {
        readonly HttpMessageInvoker http=new(handler,false);
        readonly List<Block> blocks=[];
        readonly Usage usage=new();
        HttpResponseMessage? response;
        IAsyncEnumerator<SseItem<string>>? events;
        // Output is text or a tool request; reasoning alone and empty blocks are not.
        bool sent,started,delta,output;
        string? served,stop;

        Block? Open=>blocks.Count>0&&!blocks[^1].Closed?blocks[^1]:null;

        public async Task<ModelEvent?> Next(CancellationToken ct)
        {
            try
            {
                if(ct.IsCancellationRequested)return Failed(ModelFailure.Cancelled);
                if(events is null)return await Send(ct);
                return await events.MoveNextAsync()?Apply(events.Current.EventType,events.Current.Data):Failed(ModelFailure.Transport);
            }
            catch(ModelStreamException e){return Failed(e.Failure);}
            catch(Exception e) when(e is OperationCanceledException or IOException or HttpRequestException or DecoderFallbackException)
            {
                return Failed(ct.IsCancellationRequested?ModelFailure.Cancelled:ModelFailure.Transport);
            }
        }

        // Waits for the response headers no longer than the idle timeout. A connection that was never
        // established sent nothing; any other failure may have been billed.
        async Task<ModelEvent?> Send(CancellationToken ct)
        {
            sent=true;
            using(var idle=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(limits.Idle);
                try{response=await http.SendAsync(message,idle.Token);}
                catch(HttpRequestException e) when(e.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError)
                {
                    sent=false;
                    return Failed(ModelFailure.Transport);
                }
            }
            if(response.StatusCode!=HttpStatusCode.OK)return Failed(await Refusal(ct));
            if(response.Content.Headers.ContentType?.MediaType!="text/event-stream"||response.Content.Headers.ContentEncoding.Count>0)return Failed(ModelFailure.Transport);
            events=ModelTransport.Events(await ModelTransport.Body(response,limits,ct),ct);
            return null;
        }

        // An error is classified by its status. Only a 400's body is read, and only for the vendor's
        // documented sign of a prompt longer than the context window, invalid_request_error "prompt is
        // too long"; a body that cannot be read that way keeps the status's classification.
        async Task<ModelFailure> Refusal(CancellationToken ct)
        {
            var failure=Classified(response!.StatusCode);
            if(response.StatusCode!=HttpStatusCode.BadRequest)return failure;
            try
            {
                using var document=ModelTransport.Parse(await ModelTransport.Text(await ModelTransport.Body(response,limits,ct),ct));
                return document.RootElement is {ValueKind:JsonValueKind.Object} root&&root.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.Object
                    &&Is(error,"type","invalid_request_error")&&Text(error,"message") is {} text&&text.StartsWith("prompt is too long",StringComparison.Ordinal)
                    ?ModelFailure.ContextWindow:failure;
            }
            catch(Exception e) when(e is JsonException or DecoderFallbackException or IOException or HttpRequestException||e is OperationCanceledException&&!ct.IsCancellationRequested)
            {
                return failure;
            }
        }

        // The documented flow: message_start, content blocks each started, changed and stopped in index
        // order, one or more message_delta, then message_stop. Pings and event types this mapping does
        // not know are skipped, as the versioning policy requires; anything else out of place is unreadable.
        ModelEvent? Apply(string name,string data)
        {
            JsonDocument document;
            try{document=ModelTransport.Parse(data);}catch(JsonException){throw Unreadable();}
            using(document)
            {
                var e=document.RootElement;
                if(e.ValueKind!=JsonValueKind.Object||!Is(e,"type",name))throw Unreadable();
                switch(name)
                {
                    case "message_start":
                        if(started||!e.TryGetProperty("message",out var m)||m.ValueKind!=JsonValueKind.Object||!Is(m,"type","message")||!Is(m,"role","assistant")
                            ||!m.TryGetProperty("content",out var content)||content.ValueKind!=JsonValueKind.Array||content.GetArrayLength()!=0
                            ||Text(m,"model") is not {} model)
                            throw Unreadable();
                        (started,served)=(true,model);
                        usage.Start(Reported(m));
                        // The served model is checked before any content, so nothing from another model is published.
                        if(model!=request.Model.Identifier&&!request.Model.AcceptedServed.Contains(model))throw new ModelStreamException(ModelFailure.ModelMismatch);
                        return null;
                    case "content_block_start":
                        if(!started||delta||Open is not null||Index(e)!=blocks.Count||!e.TryGetProperty("content_block",out var b)||b.ValueKind!=JsonValueKind.Object)throw Unreadable();
                        // A block starts empty and fills from its deltas. Any other block type was never requested.
                        var kind=Text(b,"type");
                        if(!(kind switch
                            {
                                "text"=>Is(b,"text",""),
                                "thinking"=>Is(b,"thinking","")&&Is(b,"signature",""),
                                "redacted_thinking"=>Text(b,"data") is {Length:>0},
                                "tool_use"=>Text(b,"id") is {Length:>0}&&Text(b,"name") is {Length:>0}
                                    &&b.TryGetProperty("input",out var input)&&input.ValueKind==JsonValueKind.Object&&!input.EnumerateObject().Any(),
                                _=>false
                            }))
                            throw Unreadable();
                        output|=kind=="tool_use";
                        blocks.Add(new(kind!,b.Clone()));
                        return null;
                    case "content_block_delta":
                        if(Open is not {} block||Index(e)!=blocks.Count-1||!e.TryGetProperty("delta",out var d)||d.ValueKind!=JsonValueKind.Object)throw Unreadable();
                        switch(block.Type,Text(d,"type"))
                        {
                            case ("text","text_delta") when Text(d,"text") is {} text:
                                block.Content.Append(text);
                                if(text=="")return null;
                                output=true;
                                return new ModelTextDelta(text);
                            // Reasoning text is never requested, and any streamed anyway is discarded here.
                            case ("thinking","thinking_delta") when Text(d,"thinking") is not null:return null;
                            case ("thinking","signature_delta") when Text(d,"signature") is {} signature:block.Content.Append(signature);return null;
                            case ("tool_use","input_json_delta") when Text(d,"partial_json") is {} json:block.Content.Append(json);return null;
                            default:throw Unreadable();
                        }
                    case "content_block_stop":
                        // A thinking block ends with the signature that lets it be replayed.
                        if(Open is not {} closing||Index(e)!=blocks.Count-1||closing.Type=="thinking"&&closing.Content.Length==0)throw Unreadable();
                        closing.Closed=true;
                        return null;
                    case "message_delta":
                        if(!started||Open is not null||!e.TryGetProperty("delta",out var change)||change.ValueKind!=JsonValueKind.Object)throw Unreadable();
                        delta=true;
                        if(change.TryGetProperty("stop_reason",out var reason)&&reason.ValueKind!=JsonValueKind.Null)stop=reason.ValueKind==JsonValueKind.String?reason.GetString():throw Unreadable();
                        usage.Delta(Reported(e));
                        return null;
                    case "message_stop":
                        return delta?Ending():throw Unreadable();
                    case "error":
                        throw new ModelStreamException(Classified(e.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.Object?Text(error,"type"):null));
                    default:
                        return null;
                }
            }
        }

        // A tool stop returns every tool request with the reply to replay; an output or context limit drops
        // a tool request it may have cut off. A stop this mapping does not expect, a tool stop without a
        // tool request, an ended turn holding one and a request for a tool the request did not offer are
        // unreadable, but keep their final usage.
        ModelEvent Ending()
        {
            var final=usage.Final(refusedBeforeOutput:stop=="refusal"&&!output);
            if(blocks.Any(b=>b.Type=="tool_use"&&!request.Tools.Any(t=>t.Name==Text(b.Start,"name"))))return new ModelFailed(ModelFailure.Transport,final,served);
            var tools=blocks.Count(b=>b.Type=="tool_use");
            ModelStop? reason=stop switch
            {
                "end_turn" when tools==0=>ModelStop.EndTurn,
                "tool_use" when tools>0=>ModelStop.ToolUse,
                "max_tokens"=>ModelStop.OutputLimit,
                "model_context_window_exceeded"=>ModelStop.ContextWindow,
                "refusal"=>ModelStop.Refusal,
                _=>null
            };
            if(reason is not {} ended)return new ModelFailed(ModelFailure.Transport,final,served);
            if(ended!=ModelStop.ToolUse)return new ModelCompleted(ended,[],null,final,served!);
            // Each replayed block is its start object as received, with the content its deltas carried.
            var (requests,content)=(ImmutableArray.CreateBuilder<ModelToolRequest>(tools),new JsonArray());
            foreach(var block in blocks)
            {
                var replayed=JsonNode.Parse(block.Start.GetRawText())!;
                switch(block.Type)
                {
                    case "text":replayed["text"]=block.Content.ToString();break;
                    case "thinking":replayed["signature"]=block.Content.ToString();break;
                    case "tool_use":
                        if(Input(block) is not {} input)return new ModelFailed(ModelFailure.Transport,final,served);
                        requests.Add(new(Text(block.Start,"id")!,Text(block.Start,"name")!,input));
                        replayed["input"]=JsonNode.Parse(input.GetRawText());
                        break;
                }
                content.Add(replayed);
            }
            using var reply=JsonDocument.Parse(content.ToJsonString());
            return new ModelCompleted(ModelStop.ToolUse,requests.MoveToImmutable(),new ModelReply(Protocol,request.Model.Identifier,reply.RootElement.Clone()),final,served!);
        }

        // A tool's input is the JSON object its deltas spell; a tool whose deltas carry nothing takes none.
        static JsonElement? Input(Block block)
        {
            if(block.Content.Length==0)return block.Start.GetProperty("input").Clone();
            try
            {
                using var input=ModelTransport.Parse(block.Content.ToString());
                return input.RootElement.ValueKind==JsonValueKind.Object?input.RootElement.Clone():null;
            }
            catch(JsonException){return null;}
        }

        // Usage is unknown once anything was sent, except after a served-model mismatch, which is
        // unrecognized; nothing sent bills nothing.
        ModelFailed Failed(ModelFailure failure)=>new(failure,!sent?ModelUsage.NothingSent(Mapping)
            :ModelUsage.Unknown(Mapping) with{Status=failure==ModelFailure.ModelMismatch?UsageStatus.Unrecognized:UsageStatus.Unknown},served);

        // A usage block that an event leaves out or sets null reports nothing; anything but an object is unreadable.
        static JsonElement Reported(JsonElement e)=>!e.TryGetProperty("usage",out var usage)||usage.ValueKind==JsonValueKind.Null?default
            :usage.ValueKind==JsonValueKind.Object?usage:throw Unreadable();
        static int Index(JsonElement e)=>e.TryGetProperty("index",out var index)&&index.ValueKind==JsonValueKind.Number&&index.TryGetInt32(out var value)?value:-1;
        static string? Text(JsonElement e,string name)=>e.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
        static bool Is(JsonElement e,string name,string expected)=>Text(e,name)==expected;
        static ModelStreamException Unreadable()=>new(ModelFailure.Transport);

        public async ValueTask DisposeAsync()
        {
            if(events is not null)await events.DisposeAsync();
            response?.Dispose();
            message.Dispose();
            http.Dispose();
        }

        sealed class Block(string type,JsonElement start)
        {
            public string Type {get;}=type;
            public JsonElement Start {get;}=start;
            // Text, a signature or a tool's partial input JSON, as the block's deltas append it.
            public StringBuilder Content {get;}=new();
            public bool Closed {get;set;}
        }
    }

    // messages_v1. Uncached input (input_tokens), cache read (cache_read_input_tokens) and cache write
    // (cache_creation_input_tokens) are message_start's, where the caching guide documents them for
    // streaming, as updated by any message_delta that reports them; the counts are cumulative, so none
    // may fall. Output (output_tokens) and reasoning (output_tokens_details.thinking_tokens, a subset of
    // output) are the last message_delta's. Usage is unrecognized when a priced count is unreported or
    // not a non-negative integer, a count falls, the last message_delta reports no output, reasoning
    // exceeds output, a reported service tier, region or speed is not the standard, global or standard
    // one requested, any field this mapping does not name holds anything but nulls and zeros (a one-hour
    // cache write among them), or a refusal arrives before any output, since its billing depends on a
    // refusal category list the vendor may change. Reasoning the last message_delta does not report is
    // unknown; it is never priced, so the usage stays recognized.
    sealed class Usage
    {
        static readonly string[] Prompt=["input_tokens","cache_read_input_tokens","cache_creation_input_tokens"];
        readonly long?[] prompt=new long?[Prompt.Length];
        // streamed is the latest output count from any event; output is the last message_delta's.
        long? streamed,output,reasoning;
        bool recognizable=true;

        public void Start(JsonElement reported)
        {
            if(reported.ValueKind!=JsonValueKind.Object)return;
            for(var i=0;i<Prompt.Length;i++)prompt[i]=Count(reported,Prompt[i],null);
            streamed=Count(reported,"output_tokens",null);
            recognizable&=Named(reported,Field);
        }

        // A message_delta without usage reports no output or reasoning, so it cannot end a recognized call.
        public void Delta(JsonElement reported)
        {
            (output,reasoning)=(null,null);
            if(reported.ValueKind!=JsonValueKind.Object)return;
            for(var i=0;i<Prompt.Length;i++)
                if(Count(reported,Prompt[i],prompt[i]) is {} count)prompt[i]=count;
            output=Count(reported,"output_tokens",streamed);
            streamed=output??streamed;
            if(reported.TryGetProperty("output_tokens_details",out var details)&&details.ValueKind==JsonValueKind.Object)reasoning=Count(details,"thinking_tokens",null);
            recognizable&=Named(reported,Field);
        }

        public ModelUsage Final(bool refusedBeforeOutput)
        {
            var recognized=recognizable&&prompt.All(c=>c is not null)&&output is not null&&!(reasoning>output)&&!refusedBeforeOutput;
            return new(Mapping,recognized?UsageStatus.Recognized:UsageStatus.Unrecognized,Token(prompt[0]),Token(prompt[1]),Token(prompt[2]),Token(output),Token(reasoning));
        }

        static TokenCount Token(long? count)=>count is {} known?TokenCount.Reported(known):TokenCount.Unknown;

        // A null or absent count reports nothing; any other value must be a non-negative integer no
        // smaller than the count reported before it.
        long? Count(JsonElement reported,string name,long? earlier)
        {
            if(!reported.TryGetProperty(name,out var value)||value.ValueKind==JsonValueKind.Null)return null;
            if(value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out var count)&&count>=0&&!(count<earlier))return count;
            recognizable=false;
            return null;
        }

        static bool Field(string name,JsonElement value)=>name switch
        {
            "input_tokens" or "cache_read_input_tokens" or "cache_creation_input_tokens" or "output_tokens"=>true,
            "output_tokens_details"=>value.ValueKind==JsonValueKind.Null||Named(value,(n,v)=>n=="thinking_tokens"||Inert(v)),
            "cache_creation"=>value.ValueKind==JsonValueKind.Null||Named(value,(n,v)=>n=="ephemeral_5m_input_tokens"||Inert(v)),
            "service_tier"=>Requested(value,"standard"),
            "inference_geo"=>Requested(value,"global"),
            "speed"=>Requested(value,"standard"),
            _=>Inert(value)
        };

        static bool Named(JsonElement reported,Func<string,JsonElement,bool> field)=>reported.ValueKind==JsonValueKind.Object&&reported.EnumerateObject().All(p=>field(p.Name,p.Value));
        static bool Requested(JsonElement value,string requested)=>value.ValueKind==JsonValueKind.Null||value.ValueKind==JsonValueKind.String&&value.GetString()==requested;
        // A field this mapping does not name may hold only nulls and exact zeros: every digit of a zero is 0.
        static bool Inert(JsonElement value)=>value.ValueKind switch
        {
            JsonValueKind.Null=>true,
            JsonValueKind.Number=>value.GetRawText().Split('e','E')[0].All(c=>c is '0' or '.' or '-'),
            JsonValueKind.Object=>value.EnumerateObject().All(p=>Inert(p.Value)),
            _=>false
        };
    }
}
