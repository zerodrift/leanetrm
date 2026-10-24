using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenEtrm.Api;

static class AdditionalProviderChecks
{
    const string Key="synthetic-provider-key-7Q4x",Model="synthetic-model";
    static readonly ModelTransportLimits Limits=new(TimeSpan.FromSeconds(2),100000);
    static JsonElement Json(string text)=>JsonDocument.Parse(text).RootElement.Clone();
    static readonly ModelTool Tool=new("read_synthetic","Read a synthetic record",Json("""{"type":"object","properties":{},"additionalProperties":false}"""));
    static ModelRequest Request(string vendor,ImmutableArray<ModelMessage> messages=default)=>new(new(vendor,Model,[]),"Synthetic instruction",
        messages.IsDefault?[new ModelUserText("Synthetic question")]:messages,[Tool],new(512,"medium",false));
    static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
    static string Event(string name,object data)=>$"event: {name}\ndata: {JsonSerializer.Serialize(data)}\n\n";
    static object Usage(string vendor)=>vendor=="google"
        ?new{promptTokenCount=100,cachedContentTokenCount=20,candidatesTokenCount=7,thoughtsTokenCount=3,totalTokenCount=110}
        :Json(vendor=="openai"?"""{"input_tokens":100,"input_tokens_details":{"cached_tokens":20,"cache_write_tokens":10},"output_tokens":10,"output_tokens_details":{"reasoning_tokens":3},"total_tokens":110}"""
            :"""{"input_tokens":100,"input_tokens_details":{"cached_tokens":20},"output_tokens":10,"output_tokens_details":{"reasoning_tokens":3},"total_tokens":110}""");
    static object[] Output(bool tools)=>tools
        ?[new{type="reasoning",id="opaque-item",encrypted_content="SYNTHETIC-CIPHER",summary=Array.Empty<object>()},
          new{type="function_call",id="fc-1",call_id="call-1",name="read_synthetic",arguments="{}",status="completed"}]
        :[new{type="message",id="msg-1",role="assistant",status="completed",content=new[]{new{type="output_text",text="Ready",annotations=Array.Empty<object>()}}}];
    static string Response(string vendor,bool tools=false,string model=Model,string status="completed")=>
        Event("response.created",new{type="response.created",response=new{id="resp-1",model,status="in_progress"}})+
        Event("response.output_text.delta",new{type="response.output_text.delta",item_id="msg-1",output_index=0,content_index=0,delta=tools?"":"Ready"})+
        Event("response."+status,new{type="response."+status,response=new{id="resp-1",model,status,service_tier="default",output=Output(tools),usage=Usage(vendor),
            incomplete_details=status=="incomplete"?new{reason="max_output_tokens"}:null}});
    static string Google(bool tools=false,string model=Model)=>Event("message",new{modelVersion=model,candidates=new[]{new{index=0,content=new{role="model",parts=tools
        ?new object[]{new{thought=true,text="SYNTHETIC-REASONING",thoughtSignature="SYNTHETIC-SIGNATURE"},new{functionCall=new{name="read_synthetic",args=new{}},thoughtSignature="SYNTHETIC-CIPHER"}}
        :new object[]{new{text="Ready"}}},finishReason="STOP"}},usageMetadata=Usage("google")});
    static async Task<List<ModelEvent>> Run(Fake fake,ModelRequest request,ModelTransportLimits? limits=null,CancellationToken ct=default)
    {
        List<ModelEvent> events=[];
        await foreach(var e in new ModelAdapters(fake,limits??Limits).Stream(request,Key,ct))events.Add(e);
        Check(events.Count>0&&events.SkipLast(1).All(e=>e is ModelTextDelta)&&events[^1] is ModelCompleted or ModelFailed,"exactly one ending");
        Check(!string.Join('\n',events).Contains("SYNTHETIC-",StringComparison.Ordinal),"reasoning, ciphertext and error bodies stay private");
        return events;
    }
    public static async Task Run()
    {
        foreach(var vendor in new[]{"openai","xai","google","meta"})
        {
            var wire=vendor=="google"?Google():Response(vendor);
            var fake=new Fake(vendor,wire);
            var events=await Run(fake,Request(vendor));
            Check(string.Concat(events.OfType<ModelTextDelta>().Select(e=>e.Text))=="Ready","text streamed");
            Check(events[^1] is ModelCompleted{Stop:ModelStop.EndTurn,Usage.Status:UsageStatus.Recognized},vendor+" recognized completion");
            var done=(ModelCompleted)events[^1];
            Check(done.Usage.UncachedInput.Count==(vendor=="openai"?70:80)&&done.Usage.CacheRead.Count==20
                &&done.Usage.CacheWrite.Count==(vendor=="openai"?10:0)&&done.Usage.Output.Count==10&&done.Usage.Reasoning.Count==3,"disjoint billable usage");
            var body=Json(fake.Bodies[0]);
            Check(vendor=="google"?body.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("includeThoughts").GetBoolean()==false
                :!body.GetProperty("store").GetBoolean()&&body.GetProperty("service_tier").GetString()=="default","explicit privacy and billing parameters");
            var loop=new Fake(vendor,vendor=="google"?Google(true):Response(vendor,true),wire);
            var toolEnd=(ModelCompleted)(await Run(loop,Request(vendor)))[^1];
            Check(toolEnd.Stop==ModelStop.ToolUse&&toolEnd.ToolRequests.Length==1&&toolEnd.Reply is not null,"tool stop has replay and request");
            var tool=toolEnd.ToolRequests[0];
            Check(tool.Name=="read_synthetic"&&tool.Input.GetRawText()=="{}","complete tool JSON");
            await Run(loop,Request(vendor,[new ModelUserText("Synthetic question"),toolEnd.Reply!,new ModelToolResults([new(tool.Id,"Synthetic result",false)])]));
            Check(loop.Bodies[1].Contains("SYNTHETIC-CIPHER",StringComparison.Ordinal)&&!loop.Bodies[1].Contains("SYNTHETIC-REASONING",StringComparison.Ordinal),"opaque continuation preserved, reasoning discarded");
            Check(loop.Bodies[1].Contains("Synthetic result",StringComparison.Ordinal),"tool result returned");
            var mismatch=await Run(new Fake(vendor,wire.Replace(Model,"unexpected-model",StringComparison.Ordinal)),Request(vendor));
            Check(mismatch.Count==1&&mismatch[0] is ModelFailed{Failure:ModelFailure.ModelMismatch,Usage.Status:UsageStatus.Unrecognized},"mismatch rejected before text");
            foreach(var invalid in new[]{"\"output_tokens\":10","\"cached_tokens\":20","\"total_tokens\":110"})
            {
                if(vendor=="google")break;
                var changed=wire.Replace(invalid,invalid[..invalid.IndexOf(':')]+":-1",StringComparison.Ordinal);
                Check((await Run(new Fake(vendor,changed),Request(vendor)))[^1] is ModelCompleted{Usage.Status:UsageStatus.Unrecognized},"invalid count is not priced");
            }
            var unrecognized=wire.Replace(vendor=="google"?"\"totalTokenCount\":110":"\"total_tokens\":110",
                vendor=="google"?"\"totalTokenCount\":110,\"newCharge\":1":"\"total_tokens\":110,\"new_charge\":1",StringComparison.Ordinal);
            Check((await Run(new Fake(vendor,unrecognized),Request(vendor)))[^1] is ModelCompleted{Usage.Status:UsageStatus.Unrecognized},"new billing field cannot be ignored");
            Check((await Run(new Fake(vendor,"data: {broken}\n\n"),Request(vendor)))[^1] is ModelFailed{Failure:ModelFailure.Transport},"malformed event");
            Check((await Run(new Fake(vendor,wire),Request(vendor),new(TimeSpan.FromSeconds(2),10)))[^1] is ModelFailed{Failure:ModelFailure.ResponseTooLarge},"bounded response");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            var untouched=new Fake(vendor,wire);
            Check((await Run(untouched,Request(vendor),ct:cancelled.Token))[^1] is ModelFailed{Failure:ModelFailure.Cancelled,Usage.Status:UsageStatus.Recognized}&&untouched.Bodies.Count==0,"pre-send cancellation bills nothing");
            foreach(var (status,failure) in new[]{(401,ModelFailure.Authentication),(429,ModelFailure.RateLimited),(503,ModelFailure.Overloaded),(400,ModelFailure.InvalidRequest),(302,ModelFailure.Transport)})
                Check((await Run(new Fake(vendor,"SYNTHETIC-ECHO "+Key,status),Request(vendor)))[^1] is ModelFailed f&&f.Failure==failure&&f.Usage.Status==UsageStatus.Unknown,"HTTP failure classified without echo");
            var limited=vendor=="google"?Google(true).Replace("\"finishReason\":\"STOP\"","\"finishReason\":\"MAX_TOKENS\"",StringComparison.Ordinal):Response(vendor,true,status:"incomplete");
            Check((await Run(new Fake(vendor,limited),Request(vendor)))[^1] is ModelCompleted{Stop:ModelStop.OutputLimit,ToolRequests.IsEmpty:true,Reply:null},"an output limit never releases tool work");
            var partial=vendor=="google"?Google().Replace("\"finishReason\":\"STOP\"","\"unused\":null",StringComparison.Ordinal):wire[..wire.IndexOf("event: response.completed",StringComparison.Ordinal)];
            Check((await Run(new Fake(vendor,partial),Request(vendor)))[^1] is ModelFailed{Failure:ModelFailure.Transport,Usage.Status:UsageStatus.Unknown},"EOF without a terminal result is interrupted usage");
            using var abort=new CancellationTokenSource();
            List<ModelEvent> aborted=[];
            await foreach(var e in new ModelAdapters(new Fake(vendor,wire),Limits).Stream(Request(vendor),Key,abort.Token))
            {aborted.Add(e);if(e is ModelTextDelta)abort.Cancel();}
            Check(aborted[^1] is ModelFailed{Failure:ModelFailure.Cancelled,Usage.Status:UsageStatus.Unknown},"in-flight cancellation cannot price an unfinished call");
            Console.WriteLine("PASS "+vendor+" request, text, replay, usage, errors, mismatch, bounds and cancellation");
        }
        var mixed=Google().Replace("\"text\":\"Ready\"", "\"text\":\"Ready\",\"functionCall\":{\"name\":\"unoffered\",\"args\":{}}",StringComparison.Ordinal);
        Check((await Run(new Fake("google",mixed),Request("google")))[^1] is ModelFailed{Failure:ModelFailure.Transport},"mixed part cannot bypass tool validation");
        var cacheBreakdown=Response("openai",true).Replace("\"cache_write_tokens\":10",
            "\"cache_write_tokens\":10,\"cache_write_tokens_details\":{\"ttl_30m_tokens\":10,\"ttl_12h_tokens\":0}",StringComparison.Ordinal);
        var cachedTool=(ModelCompleted)(await Run(new Fake("openai",cacheBreakdown),Request("openai")))[^1];
        Check(cachedTool is {Stop:ModelStop.ToolUse,Usage.Status:UsageStatus.Recognized}
            &&cachedTool.Usage.UncachedInput.Count==70&&cachedTool.Usage.CacheRead.Count==20
            &&cachedTool.Usage.CacheWrite.Count==10&&cachedTool.Usage.Output.Count==10,"supported cache lifetime breakdown retains disjoint usage and tool continuation");
        foreach(var details in new[]{"null","[]","{}","{\"ttl_30m_tokens\":10}",
            "{\"ttl_30m_tokens\":9,\"ttl_12h_tokens\":0}","{\"ttl_30m_tokens\":9,\"ttl_12h_tokens\":1}",
            "{\"ttl_30m_tokens\":10,\"ttl_12h_tokens\":-1}","{\"ttl_30m_tokens\":10,\"ttl_12h_tokens\":0,\"new_lifetime\":1}"})
        {
            var invalid=Response("openai").Replace("\"cache_write_tokens\":10","\"cache_write_tokens\":10,\"cache_write_tokens_details\":"+details,StringComparison.Ordinal);
            Check((await Run(new Fake("openai",invalid),Request("openai")))[^1] is ModelCompleted{Usage.Status:UsageStatus.Unrecognized},"unsupported or malformed cache lifetimes are not priced");
        }
        var interim=Event("message",new{modelVersion=Model,usageMetadata=new{promptTokenCount=-1}})+Google();
        Check((await Run(new Fake("google",interim),Request("google")))[^1] is ModelCompleted{Usage.Status:UsageStatus.Unrecognized},"invalid interim counter stays unrecognized");
        var switched=Response("openai");
        var pos=switched.LastIndexOf("\"model\":\""+Model+"\"",StringComparison.Ordinal);
        switched=switched[..pos]+switched[pos..].Replace(Model,"wrong-terminal-model",StringComparison.Ordinal);
        Check((await Run(new Fake("openai",switched),Request("openai")))[^1] is ModelFailed{Failure:ModelFailure.ModelMismatch,Served:"wrong-terminal-model"},"mismatch retains the offending identifier for durable close");
        Check((await Run(new Fake("openai",Response("openai",true).Replace("\"status\":\"completed\"}","\"status\":\"in_progress\"}",StringComparison.Ordinal)),Request("openai")))[^1] is ModelFailed{Failure:ModelFailure.Transport},"unfinished function call is never executable");
        Check((await Run(new Fake("openai",Response("openai").Replace("\"service_tier\":\"default\"","\"service_tier\":0",StringComparison.Ordinal)),Request("openai")))[^1] is ModelCompleted{Usage.Status:UsageStatus.Unrecognized},"invalid known billing modifier is not a standard tier");
        Check((await Run(new Fake("openai","{\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"SYNTHETIC-ECHO\"}}",400),Request("openai")))[^1] is ModelFailed{Failure:ModelFailure.ContextWindow},"bounded HTTP context error classification");
        var modality=Google().Replace("\"totalTokenCount\":110", "\"totalTokenCount\":110,\"promptTokensDetails\":[{\"modality\":\"TEXT\",\"tokenCount\":100}]",StringComparison.Ordinal);
        Check((await Run(new Fake("google",modality),Request("google")))[^1] is ModelCompleted{Usage.Status:UsageStatus.Recognized},"known text modality breakdown is informational");
        Check((await Run(new Fake("openai",Response("openai").Replace("\"reasoning_tokens\":3","\"reasoning_tokens\":-1",StringComparison.Ordinal)),Request("openai")))[^1] is ModelCompleted{Usage.Status:UsageStatus.Unrecognized},"a malformed informational counter is not absent");
        foreach(var malformed in new[]{"\"bad\"","null","[]"})
        {
            var hidden=Google(true).Replace("\"functionCall\":{\"name\":\"read_synthetic\",\"args\":{}}","\"functionCall\":"+malformed,StringComparison.Ordinal);
            Check((await Run(new Fake("google",hidden),Request("google")))[^1] is ModelFailed{Failure:ModelFailure.Transport},"a signature cannot hide malformed function data");
        }
        var badText=Google().Replace("\"text\":\"Ready\"","\"text\":42,\"thoughtSignature\":\"opaque\"",StringComparison.Ordinal);
        Check((await Run(new Fake("google",badText),Request("google")))[^1] is ModelFailed{Failure:ModelFailure.Transport},"a signature cannot hide malformed text");
        var earlyTier=Response("openai").Replace("\"status\":\"in_progress\"}","\"status\":\"in_progress\",\"service_tier\":\"priority\"}",StringComparison.Ordinal);
        Check((await Run(new Fake("openai",earlyTier),Request("openai")))[^1] is ModelCompleted{Usage.Status:UsageStatus.Unrecognized},"a later standard tier cannot erase an earlier premium tier");
        var unoffered=Response("openai",true).Replace("\"name\":\"read_synthetic\"","\"name\":\"unoffered\"",StringComparison.Ordinal);
        Check((await Run(new Fake("openai",unoffered),Request("openai")))[^1] is ModelFailed{Failure:ModelFailure.Transport,Usage.Status:UsageStatus.Recognized},"a failed protocol result preserves final reported usage");
        Check((await Run(new Fake("openai",Response("openai",status:"failed")),Request("openai")))[^1] is ModelFailed{Usage.Status:UsageStatus.Recognized},"a terminal provider failure preserves its complete final usage");
        Check((await Run(new Fake("openai",Response("openai",status:"failed").Replace("\"status\":\"failed\"","\"status\":\"in_progress\"",StringComparison.Ordinal)),Request("openai")))[^1] is ModelFailed{Usage.Status:UsageStatus.Unknown},"a failure notification cannot certify nonterminal usage");
        var rejected=new Fake("google",Google());
        try{await Run(rejected,Request("google") with{Model=new("google","../escape",[])});throw new Exception("invalid identifier accepted");}
        catch(ModelCallException e){Check(e.Code=="model_identifier_invalid"&&rejected.Bodies.Count==0,"path injection refused before send");}
    }
    sealed class Fake(string vendor,params string[] recordings):HttpMessageHandler
    {
        readonly Queue<string> responses=new(recordings);
        public List<string> Bodies {get;}=[];
        readonly int status=200;
        public Fake(string vendor,string recording,int status):this(vendor,recording){this.status=status;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var expected=vendor switch{"openai"=>"https://api.openai.com/v1/responses","xai"=>"https://api.x.ai/v1/responses","meta"=>"https://api.meta.ai/v1/responses",_=>"https://generativelanguage.googleapis.com/v1beta/models/"+Model+":streamGenerateContent?alt=sse"};
            Check(request.RequestUri!.ToString()==expected,"fixed provider endpoint");
            Check(vendor=="google"?request.Headers.GetValues("x-goog-api-key").Single()==Key:request.Headers.Authorization?.Parameter==Key,"credential in header");
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new((HttpStatusCode)status){Content=new StringContent(responses.Dequeue(),Encoding.UTF8,status==200?"text/event-stream":"application/json")};
        }
    }
}
