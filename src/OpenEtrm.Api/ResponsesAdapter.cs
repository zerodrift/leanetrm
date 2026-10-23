using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenEtrm.Api;

// Shared Responses wire parser, separate vendor usage mappings. Standard/global, stateless,
// client function tools only; reasoning summaries are not requested or published.
sealed class ResponsesAdapter(string vendor,HttpMessageHandler handler,ModelTransportLimits limits):ProviderStream(handler,limits)
{
    public static readonly Uri OpenAiEndpoint=new("https://api.openai.com/v1/responses"),XaiEndpoint=new("https://api.x.ai/v1/responses"),MetaEndpoint=new("https://api.meta.ai/v1/responses");
    internal static readonly ImmutableArray<string> OpenAiEfforts=["low","medium","high","xhigh","max"],XaiEfforts=["low","medium","high","xhigh"];
    internal static readonly ImmutableArray<string> MetaEfforts=["minimal","low","medium","high","xhigh","max"];
    public static void Validate(ModelRequest request,string key)
    {
        ModelAdapters.ValidateRequest(request,key,request.Model.Vendor switch{"openai"=>OpenAiEfforts,"xai"=>XaiEfforts,"meta"=>MetaEfforts,_=>throw new ArgumentException("Unsupported Responses vendor.")});
        if(request.Model.Vendor=="meta"&&request.Parameters.MaxOutputTokens<16)throw new ModelCallException("parameter_required","This protocol requires at least 16 output tokens.");
    }
    protected override HttpRequestMessage Compose(ModelRequest request,string key)
    {
        Validate(request,key);
        var input=new JsonArray();
        foreach(var message in request.Messages)
        {
            switch(message)
            {
                case ModelUserText{Text.Length:>0} text:input.Add(new JsonObject{["role"]="user",["content"]=text.Text});break;
                case ModelReply reply when reply.Protocol=="responses:"+vendor&&reply.Model==request.Model.Identifier:
                    foreach(var item in reply.Content.EnumerateArray())input.Add(JsonNode.Parse(item.GetRawText()));break;
                case ModelToolResults{Results.IsDefaultOrEmpty:false} results:
                    foreach(var result in results.Results)input.Add(new JsonObject{["type"]="function_call_output",["call_id"]=result.RequestId,["output"]=result.Content});break;
                default:throw new ArgumentException("A reply must belong to this provider and model, and messages must carry content.");
            }
        }
        var body=new JsonObject
        {
            ["model"]=request.Model.Identifier,["instructions"]=request.System,["input"]=input,["stream"]=true,["store"]=false,
            ["background"]=false,["truncation"]="disabled",["max_output_tokens"]=request.Parameters.MaxOutputTokens,["reasoning"]=new JsonObject{["effort"]=request.Parameters.Reasoning},
            ["service_tier"]="default",["include"]=new JsonArray("reasoning.encrypted_content")
        };
        // The OpenAI mapping fixes the billed cache lifetime to the shortest supported TTL. The
        // agent's explicit breakpoint switch applies only to Messages; implicit caching is automatic.
        if(vendor=="openai")body["prompt_cache_options"]=new JsonObject{["mode"]="implicit",["ttl"]="30m"};
        if(!request.Tools.IsEmpty)
        {
            body["tools"]=new JsonArray(request.Tools.Select(t=>(JsonNode?)new JsonObject
                {["type"]="function",["name"]=t.Name,["description"]=t.Description,["parameters"]=JsonNode.Parse(t.InputSchema.GetRawText()),["strict"]=false}).ToArray());
            body["tool_choice"]="auto";body["parallel_tool_calls"]=true;
        }
        return Post(vendor switch{"openai"=>OpenAiEndpoint,"xai"=>XaiEndpoint,"meta"=>MetaEndpoint,_=>throw new ArgumentException("Unsupported Responses vendor.")},body,"Authorization",key);
    }
    protected override ProviderParser Parser(ModelRequest request)=>new ResponsesParser(request,vendor);

    sealed class ResponsesParser(ModelRequest request,string vendor):ProviderParser(request,ModelAdapters.Mapping(vendor)!)
    {
        bool started,billingRecognized=true;
        string? responseId;
        readonly StringBuilder streamed=new();
        public override ModelEvent[] Apply(string name,JsonElement e)
        {
            if(Text(e,"type")!=name)throw Unreadable();
            var response=Get(e,"response");
            if(response.ValueKind==JsonValueKind.Object)
            {
                billingRecognized&=Standard(response,"service_tier","default")&&Standard(response,"inference_geo","global")&&Standard(response,"speed","standard");
                Identify(Text(response,"model"));
                if(Text(response,"id") is not {Length:>0} id||responseId is not null&&responseId!=id)throw Unreadable();
                responseId=id;
            }
            switch(name)
            {
                case "response.created":
                    if(started||Served is null)throw Unreadable();started=true;return [];
                case "response.output_text.delta":
                    if(!started||Text(e,"delta") is not {} text)throw Unreadable();
                    streamed.Append(text);return text.Length==0?[]:[new ModelTextDelta(text)];
                case "response.completed" or "response.incomplete":
                    if(!started||Served is null||Text(response,"status")!=(name=="response.completed"?"completed":"incomplete"))throw Unreadable();
                    return [Complete(response,name=="response.incomplete")];
                case "response.failed":
                    if(!started||Text(response,"status")!="failed")throw Unreadable();
                    FinalUsage=Get(response,"usage").ValueKind==JsonValueKind.Object?Normalize(response):null;
                    return [new ModelFailed(Error(Text(Get(response,"error"),"code")),FinalUsage??ModelUsage.Unknown(Mapping),Served)];
                case "error":throw new ModelStreamException(Error(Text(e,"code")));
                default:
                    // Future notifications carry no authority or output. All executable items come
                    // from the terminal output array; argument fragments are never executed.
                    return [];
            }
        }
        static ModelFailure Error(string? code)=>code switch
        {
            "context_length_exceeded"=>ModelFailure.ContextWindow,"rate_limit_exceeded"=>ModelFailure.RateLimited,
            "server_error"=>ModelFailure.Overloaded,"invalid_api_key"=>ModelFailure.Authentication,_=>ModelFailure.Transport
        };
        ModelEvent Complete(JsonElement response,bool incomplete)
        {
            var usage=Normalize(response);
            FinalUsage=usage;
            if(Get(response,"output") is not {ValueKind:JsonValueKind.Array} output)throw Unreadable();
            var requests=ImmutableArray.CreateBuilder<ModelToolRequest>();
            var replay=new JsonArray();
            var text=new StringBuilder();var refusal=false;
            foreach(var item in output.EnumerateArray())
            {
                if(item.ValueKind!=JsonValueKind.Object)throw Unreadable();
                var node=JsonNode.Parse(item.GetRawText())!;
                switch(Text(item,"type"))
                {
                    case "reasoning":
                        // Only ciphertext can continue a reasoning item without exposing reasoning.
                        if(Text(item,"encrypted_content") is not {Length:>0})throw Unreadable();
                        node["summary"]=new JsonArray();node.AsObject().Remove("content");break;
                    case "message":
                        if(Text(item,"role")!="assistant"||Get(item,"content") is not {ValueKind:JsonValueKind.Array} content)throw Unreadable();
                        foreach(var part in content.EnumerateArray())
                        {
                            if(Text(part,"type")=="output_text"&&Text(part,"text") is {} value)text.Append(value);
                            else if(Text(part,"type")=="refusal")refusal=true;
                            else throw Unreadable();
                        }
                        break;
                    case "function_call":
                        if(incomplete)break;
                        if(Get(item,"status").ValueKind is not JsonValueKind.Undefined&&Text(item,"status")!="completed")throw Unreadable();
                        if(Text(item,"call_id") is not {Length:>0} id||Text(item,"name") is not {} tool
                            ||!Request.Tools.Any(t=>t.Name==tool)||requests.Any(t=>t.Id==id)||Text(item,"arguments") is not {} arguments)throw Unreadable();
                        using(var parsed=ModelTransport.Parse(arguments))
                        {
                            if(parsed.RootElement.ValueKind!=JsonValueKind.Object)throw Unreadable();
                            requests.Add(new(id,tool,parsed.RootElement.Clone()));
                        }
                        break;
                    default:throw Unreadable();
                }
                replay.Add(node);
            }
            if(text.ToString()!=streamed.ToString())throw Unreadable();
            ModelStop stop;
            if(incomplete)stop=Text(Get(response,"incomplete_details"),"reason") switch
            {
                "max_output_tokens"=>ModelStop.OutputLimit,"content_filter"=>ModelStop.Refusal,"context_length_exceeded"=>ModelStop.ContextWindow,_=>throw Unreadable()
            };
            else stop=refusal?ModelStop.Refusal:requests.Count>0?ModelStop.ToolUse:ModelStop.EndTurn;
            return new ModelCompleted(stop,stop==ModelStop.ToolUse?requests.ToImmutable():[],
                stop==ModelStop.ToolUse?new ModelReply("responses:"+vendor,Request.Model.Identifier,JsonSerializer.SerializeToElement(replay)):null,usage,Served!);
        }
        ModelUsage Normalize(JsonElement response)
        {
            var u=Get(response,"usage");var input=Count(u,"input_tokens");var output=Count(u,"output_tokens");var total=Count(u,"total_tokens");
            var details=Get(u,"input_tokens_details");var read=Count(details,"cached_tokens");
            // xAI reports no separately billed cache writes. OpenAI reports and bills them separately.
            var write=vendor=="openai"?Count(details,"cache_write_tokens"):0;
            var outDetails=Get(u,"output_tokens_details");var reasoning=Count(outDetails,"reasoning_tokens");
            var uncached=input is {} i&&read is {} r&&write is {} w&&(decimal)r+w<=i?i-r-w:(long?)null;
            var recognized=billingRecognized&&uncached is not null&&output is not null&&total is not null&&input is not null&&(decimal)input+output==total
                &&!(reasoning>output)&&Named(u,"input_tokens","input_tokens_details","output_tokens","output_tokens_details","total_tokens")
                &&Named(details,vendor=="openai"?["cached_tokens","cache_write_tokens"]:["cached_tokens"])
                &&(outDetails.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null||Named(outDetails,"reasoning_tokens"))
                &&(Get(outDetails,"reasoning_tokens").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null||reasoning is not null)
                &&Standard(response,"service_tier","default");
            return new(Mapping,recognized?UsageStatus.Recognized:UsageStatus.Unrecognized,Tokens(uncached),Tokens(read),
                vendor=="openai"?Tokens(write):TokenCount.NotBilled,Tokens(output),Tokens(reasoning));
        }
    }
}
