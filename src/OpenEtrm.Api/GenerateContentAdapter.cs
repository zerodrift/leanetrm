using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenEtrm.Api;

// Gemini's REST streaming API, text and client function tools only. No explicit cache resources
// (and their storage charges) are created. Thought signatures remain opaque, in memory only.
sealed class GenerateContentAdapter(HttpMessageHandler handler,ModelTransportLimits limits):ProviderStream(handler,limits)
{
    public const string Mapping="generate_content_v1";
    public static readonly Uri Origin=new("https://generativelanguage.googleapis.com");
    public const string Prefix="/v1beta/models/",Suffix=":streamGenerateContent?alt=sse",Protocol="generateContent";
    public static readonly Uri EndpointTemplate=new(Origin,Prefix+"{model}"+Suffix);
    static readonly ImmutableArray<string> Efforts=["minimal","low","medium","high"];
    public static void Validate(ModelRequest request,string key)=>ModelAdapters.ValidateRequest(request,key,Efforts);
    protected override HttpRequestMessage Compose(ModelRequest request,string key)
    {
        Validate(request,key);
        var endpoint=new Uri(Origin,Prefix+Uri.EscapeDataString(request.Model.Identifier)+Suffix);
        if(endpoint.Scheme!=Origin.Scheme||endpoint.Host!=Origin.Host||endpoint.Port!=Origin.Port||endpoint.AbsolutePath!=Prefix+request.Model.Identifier+":streamGenerateContent")
            throw new ModelCallException("model_identifier_invalid",ModelCatalog.Refusal("model_identifier_invalid"));
        var contents=new JsonArray();ModelReply? previous=null;
        foreach(var message in request.Messages)
        {
            switch(message)
            {
                case ModelUserText{Text.Length:>0} user:contents.Add(new JsonObject{["role"]="user",["parts"]=new JsonArray(new JsonObject{["text"]=user.Text})});previous=null;break;
                case ModelReply reply when reply.Protocol==Protocol&&reply.Model==request.Model.Identifier:
                    contents.Add(JsonNode.Parse(reply.Content.GetRawText()));previous=reply;break;
                case ModelToolResults{Results.IsDefaultOrEmpty:false} results when previous is not null:
                    var calls=previous.Content.GetProperty("parts").EnumerateArray().Where(p=>p.TryGetProperty("functionCall",out _)).Select(p=>p.GetProperty("functionCall")).ToArray();
                    if(results.Results.Length!=calls.Length)throw new ArgumentException("Every function request must have one result.");
                    var parts=new JsonArray();
                    for(var i=0;i<calls.Length;i++)
                    {
                        var result=results.Results[i];var call=calls[i];
                        if(result.RequestId!=RequestId(i))throw new ArgumentException("Tool results must match the preceding reply in order.");
                        var function=new JsonObject{["name"]=call.GetProperty("name").GetString(),["response"]=new JsonObject{[result.Failed?"error":"output"]=result.Content}};
                        if(call.TryGetProperty("id",out var id))function["id"]=JsonNode.Parse(id.GetRawText());
                        parts.Add(new JsonObject{["functionResponse"]=function});
                    }
                    contents.Add(new JsonObject{["role"]="user",["parts"]=parts});previous=null;break;
                default:throw new ArgumentException("A message must carry content and a reply and its results must belong to this model.");
            }
        }
        var body=new JsonObject
        {
            ["contents"]=contents,["systemInstruction"]=new JsonObject{["parts"]=new JsonArray(new JsonObject{["text"]=request.System})},
            ["generationConfig"]=new JsonObject{["candidateCount"]=1,["maxOutputTokens"]=request.Parameters.MaxOutputTokens,["responseModalities"]=new JsonArray("TEXT"),
                ["thinkingConfig"]=new JsonObject{["thinkingLevel"]=request.Parameters.Reasoning!.ToUpperInvariant(),["includeThoughts"]=false}},
            ["serviceTier"]="standard",["store"]=false
        };
        if(!request.Tools.IsEmpty)
        {
            body["tools"]=new JsonArray(new JsonObject{["functionDeclarations"]=new JsonArray(request.Tools.Select(t=>(JsonNode?)new JsonObject
                {["name"]=t.Name,["description"]=t.Description,["parametersJsonSchema"]=JsonNode.Parse(t.InputSchema.GetRawText())}).ToArray())});
            body["toolConfig"]=new JsonObject{["functionCallingConfig"]=new JsonObject{["mode"]="AUTO"}};
        }
        return Post(endpoint,body,"x-goog-api-key",key);
    }
    static string RequestId(int i)=>"function-"+i.ToString(CultureInfo.InvariantCulture);
    protected override ProviderParser Parser(ModelRequest request)=>new GenerateParser(request);
    sealed class GenerateParser(ModelRequest request):ProviderParser(request,GenerateContentAdapter.Mapping)
    {
        readonly JsonArray parts=new();
        JsonElement usage;
        string? finish;
        bool validUsage=true;
        readonly Dictionary<string,long> counts=[];
        public override ModelEvent[] Apply(string name,JsonElement e)
        {
            if(e.TryGetProperty("error",out var error))throw new ModelStreamException(HttpFailure((int)(Count(error,"code")??0)));
            if(Text(e,"modelVersion") is {} model)Identify(model);
            if(Served is null)throw Unreadable();
            usage=Get(e,"usageMetadata").Clone();
            validUsage&=Standard(usage,"serviceTier","standard");
            foreach(var field in new[]{"promptTokenCount","cachedContentTokenCount","candidatesTokenCount","thoughtsTokenCount","totalTokenCount"})
                if(Get(usage,field).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                {
                    if(Count(usage,field) is not {} count){validUsage=false;continue;}
                    if(counts.TryGetValue(field,out var previous)&&count<previous)validUsage=false;
                    counts[field]=count;
                }
            if(Get(e,"promptFeedback") is {} feedback&&Text(feedback,"blockReason") is not null)
            {finish="SAFETY";return [];}
            var candidates=Get(e,"candidates");
            if(candidates.ValueKind==JsonValueKind.Undefined)return [];
            if(candidates.ValueKind!=JsonValueKind.Array||candidates.GetArrayLength()>1)throw Unreadable();
            if(candidates.GetArrayLength()==0)return [];
            var candidate=candidates[0];
            if(Count(candidate,"index")!=0||finish is not null)throw Unreadable();
            var content=Get(candidate,"content");var incoming=Get(content,"parts");
            List<ModelEvent> events=[];
            if(incoming.ValueKind!=JsonValueKind.Undefined)
            {
                if(incoming.ValueKind!=JsonValueKind.Array||Text(content,"role")!="model")throw Unreadable();
                foreach(var part in incoming.EnumerateArray())
                {
                    if(part.ValueKind!=JsonValueKind.Object)throw Unreadable();
                    // Part is a protobuf union: never let a text/signature branch hide a tool or
                    // an unsupported executable/media payload that a later pass could interpret.
                    var payloads=part.EnumerateObject().Count(p=>p.Name is not ("thought" or "thoughtSignature"));
                    if(payloads>1||payloads==1&&!part.TryGetProperty("text",out _)&&!part.TryGetProperty("functionCall",out _))throw Unreadable();
                    if(part.TryGetProperty("text",out var textValue)&&textValue.ValueKind!=JsonValueKind.String
                        ||part.TryGetProperty("functionCall",out var callValue)&&callValue.ValueKind!=JsonValueKind.Object
                        ||part.TryGetProperty("thoughtSignature",out var signatureValue)&&(signatureValue.ValueKind!=JsonValueKind.String||signatureValue.GetString()!.Length==0))throw Unreadable();
                    if(Get(part,"thought").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False))throw Unreadable();
                    if(Get(part,"thought").ValueKind==JsonValueKind.True&&part.TryGetProperty("functionCall",out _))throw Unreadable();
                    var copy=JsonNode.Parse(part.GetRawText())!;
                    if(Get(part,"thought") is {ValueKind:JsonValueKind.True})
                    {
                        if(Text(part,"thoughtSignature") is not {Length:>0})continue;
                        copy["text"]="";
                    }
                    else if(Text(part,"text") is {} text)
                    {if(text.Length>0)events.Add(new ModelTextDelta(text));}
                    else if(Get(part,"functionCall") is {ValueKind:JsonValueKind.Object} function)
                    {
                        if(Text(function,"name") is not {} tool||!Request.Tools.Any(t=>t.Name==tool)||Get(function,"args").ValueKind!=JsonValueKind.Object)throw Unreadable();
                    }
                    else if(Text(part,"thoughtSignature") is null)throw Unreadable();
                    parts.Add(copy);
                }
            }
            finish=Text(candidate,"finishReason");
            return [..events];
        }
        public override ModelEvent End()
        {
            if(finish is null||Served is null)return base.End();
            FinalUsage=Normalize();
            var requests=ImmutableArray.CreateBuilder<ModelToolRequest>();
            foreach(var part in parts)
                if(part?["functionCall"] is {} call)
                    requests.Add(new(RequestId(requests.Count),call["name"]!.GetValue<string>(),JsonSerializer.SerializeToElement(call["args"])));
            var stop=finish switch
            {
                "STOP"=>requests.Count>0?ModelStop.ToolUse:ModelStop.EndTurn,"MAX_TOKENS"=>ModelStop.OutputLimit,
                "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII"=>ModelStop.Refusal,_=>(ModelStop?)null
            };
            if(stop is null)return base.End();
            var replay=new JsonObject{["role"]="model",["parts"]=parts.DeepClone()};
            return new ModelCompleted(stop.Value,stop==ModelStop.ToolUse?requests.ToImmutable():[],
                stop==ModelStop.ToolUse?new ModelReply(Protocol,Request.Model.Identifier,JsonSerializer.SerializeToElement(replay)):null,FinalUsage,Served);
        }
        bool Modality(string field,long? expected)
        {
            var value=Get(usage,field);
            if(value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)return true;
            if(value.ValueKind!=JsonValueKind.Array||expected is null)return false;
            decimal sum=0;
            foreach(var detail in value.EnumerateArray())
            {
                if(Text(detail,"modality")!="TEXT"||Count(detail,"tokenCount") is not {} count||!Named(detail,"modality","tokenCount"))return false;
                sum+=count;
            }
            return sum==expected;
        }
        ModelUsage Normalize()
        {
            var prompt=Count(usage,"promptTokenCount");var cache=Count(usage,"cachedContentTokenCount");
            var candidates=Count(usage,"candidatesTokenCount");var thoughts=Count(usage,"thoughtsTokenCount");var total=Count(usage,"totalTokenCount");
            var uncached=prompt is {} p&&cache is {} c&&c<=p?p-c:(long?)null;
            var output=candidates is {} o&&thoughts is {} t&&(decimal)o+t<=long.MaxValue?o+t:(long?)null;
            var recognized=validUsage&&uncached is not null&&output is not null&&total is not null&&prompt is not null&&(decimal)prompt+output==total
                &&Named(usage,"promptTokenCount","cachedContentTokenCount","candidatesTokenCount","thoughtsTokenCount","totalTokenCount","serviceTier","promptTokensDetails","cacheTokensDetails","candidatesTokensDetails","toolUsePromptTokensDetails")
                &&Standard(usage,"serviceTier","standard")
                &&Modality("promptTokensDetails",prompt)&&Modality("cacheTokensDetails",cache)
                &&Modality("candidatesTokensDetails",candidates)&&Modality("toolUsePromptTokensDetails",0);
            return new(Mapping,recognized?UsageStatus.Recognized:UsageStatus.Unrecognized,Tokens(uncached),Tokens(cache),TokenCount.NotBilled,Tokens(output),Tokens(thoughts));
        }
    }
}
