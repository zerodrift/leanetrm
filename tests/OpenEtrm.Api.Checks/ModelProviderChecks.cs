using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenEtrm.Api;

// Conformance of the Messages adapter (docs/ai-assistant.md §6.3) against a local fake provider that
// asserts every request and replays hand-written synthetic recordings. No socket is opened and no
// check calls a real provider.
static class ModelProviderChecks
{
    const string Key="synthetic-messages-key-checks-only-7Q4x";
    const string Model="synthetic-messages-model";
    static readonly ModelTransportLimits Limits=new(TimeSpan.FromSeconds(10),1<<20);
    // Values that must reach no event: the key, message and request identifiers, reasoning and
    // continuation data, and text an error body echoes.
    static readonly string[] Withheld=[Key,"7Q4x","msg_synthetic","req_synthetic","SYNTHETIC-REASONING","U3ludGhldGlj","SYNTHETIC-ECHO"];

    public static async Task Run()
    {
        await TextCall();
        await CachedToolLoop();
        await ReasoningStaysInTheReply();
        await UsageMapping();
        await StopReasons();
        await ProviderErrors();
        await MalformedStreams();
        await TransportAndCancellation();
        await ServedModel();
        await RefusedBeforeSending();
    }

    static async Task TextCall()
    {
        var fake=new FakeProvider(Sse(Text()));
        var (text,end)=await Call(fake,Request());
        Require(fake.Bodies.Count==1&&Same(fake.Bodies[0],"""
            {"model":"synthetic-messages-model","max_tokens":512,"stream":true,
             "system":[{"type":"text","text":"Synthetic preamble."}],
             "messages":[{"role":"user","content":[{"type":"text","text":"Synthetic question?"}]}],
             "thinking":{"type":"adaptive","display":"omitted"},"output_config":{"effort":"medium"},
             "service_tier":"standard_only","inference_geo":"global"}
            """),"a call sends every behavior and billing parameter explicitly, with adaptive thinking omitted from the reply");
        Require(string.Concat(text)=="Synthetic answer."&&text.Count==2,"text arrives as its deltas");
        var completed=Require<ModelCompleted>(end,"a normal stream completes");
        Require(completed is {Stop:ModelStop.EndTurn,ToolRequests.IsEmpty:true,Reply:null,Served:Model},"an ended turn names the served model and has no tool requests or reply");
        Usage(completed.Usage,UsageStatus.Recognized,21,0,0,9,0,"final usage comes from the last message_delta");
        var crlf=new FakeProvider(Sse(Text().ReplaceLineEndings("\r\n")));
        var (again,crlfEnd)=await Call(crlf,Request());
        Require(string.Concat(again)=="Synthetic answer."&&crlfEnd is ModelCompleted{Stop:ModelStop.EndTurn},"a recording with CRLF line endings completes identically");
        var future=new FakeProvider(Sse(Text().Replace("event: ping","event: synthetic_future_event\ndata: {\"type\":\"synthetic_future_event\"}\n\nevent: ping")));
        Require((await Call(future,Request())).End is ModelCompleted{Stop:ModelStop.EndTurn},"an unknown event type is ignored, as the versioning policy requires");
        Console.WriteLine("PASS Messages text call: explicit request, text deltas, final usage, CRLF and unknown events");
    }

    static async Task CachedToolLoop()
    {
        var fake=new FakeProvider(Sse(ToolRecording),Sse(AfterToolRecording));
        var first=await Call(fake,Request(caching:true,tools:[ReadTrades]));
        Require(Same(fake.Bodies[0],"""
            {"model":"synthetic-messages-model","max_tokens":512,"stream":true,
             "system":[{"type":"text","text":"Synthetic preamble.","cache_control":{"type":"ephemeral","ttl":"5m"}}],
             "messages":[{"role":"user","content":[{"type":"text","text":"Synthetic question?","cache_control":{"type":"ephemeral","ttl":"5m"}}]}],
             "tools":[{"name":"read_trades","description":"Reads the synthetic trades of one book.",
               "input_schema":{"type":"object","properties":{"book":{"type":"string"},"limit":{"type":"integer"}},"required":["book"],"additionalProperties":false}}],
             "tool_choice":{"type":"auto","disable_parallel_tool_use":false},
             "thinking":{"type":"adaptive","display":"omitted"},"output_config":{"effort":"medium"},
             "service_tier":"standard_only","inference_geo":"global"}
            """),"caching places explicit five-minute breakpoints on the system block and the last message block; tools are offered with explicit automatic choice");
        Require(string.Concat(first.Text)=="Reading the book.","text before a tool request streams");
        var stopped=Require<ModelCompleted>(first.End,"a tool stop completes");
        Require(stopped is {Stop:ModelStop.ToolUse,Reply:not null}&&stopped.ToolRequests.Length==2,"a tool stop carries every tool request and the reply to replay");
        Require(stopped.ToolRequests[0] is {Id:"toolu_synthetic_01",Name:"read_trades"}&&Same(stopped.ToolRequests[0].Input,"""{"book":"SYN-1 é","limit":5}""")
            &&stopped.ToolRequests[1] is {Id:"toolu_synthetic_02",Name:"read_trades"}&&Same(stopped.ToolRequests[1].Input,"""{"book":"SYN-2"}"""),
            "tool input assembles from partial JSON, including a split escape");
        Usage(stopped.Usage,UsageStatus.Recognized,3,4800,1200,95,40,"cache read, cache write and reasoning map from their final fields");

        var results=new ModelToolResults([new("toolu_synthetic_01","Synthetic book SYN-1 has two trades.",false),new("toolu_synthetic_02","Synthetic book SYN-2 is not readable.",true)]);
        var second=await Call(fake,Request(caching:true,tools:[ReadTrades],messages:[new ModelUserText("Synthetic question?"),stopped.Reply!,results]));
        var replayed=JsonDocument.Parse(fake.Bodies[1]).RootElement.GetProperty("messages");
        Require(Same(replayed,"""
            [{"role":"user","content":[{"type":"text","text":"Synthetic question?"}]},
             {"role":"assistant","content":[
               {"type":"thinking","thinking":"","signature":"U3ludGhldGljU2lnbmF0dXJlAAEC+/9=="},
               {"type":"redacted_thinking","data":"U3ludGhldGljUmVkYWN0ZWQBAgM+/9=="},
               {"type":"text","text":"Reading the book."},
               {"type":"tool_use","id":"toolu_synthetic_01","name":"read_trades","input":{"book":"SYN-1 é","limit":5}},
               {"type":"tool_use","id":"toolu_synthetic_02","name":"read_trades","input":{"book":"SYN-2"},"synthetic_extension":{"kept":true}}]},
             {"role":"user","content":[
               {"type":"tool_result","tool_use_id":"toolu_synthetic_01","content":"Synthetic book SYN-1 has two trades.","is_error":false},
               {"type":"tool_result","tool_use_id":"toolu_synthetic_02","content":"Synthetic book SYN-2 is not readable.","is_error":true,"cache_control":{"type":"ephemeral","ttl":"5m"}}]}]
            """),"the second step replays each block as received, a field this mapping does not name included, answers each tool request and moves the breakpoint to the last block");
        var content=replayed[1].GetProperty("content");
        Require(content[0].GetProperty("signature").GetString()=="U3ludGhldGljU2lnbmF0dXJlAAEC+/9=="&&content[1].GetProperty("data").GetString()=="U3ludGhldGljUmVkYWN0ZWQBAgM+/9==",
            "a signature and redacted reasoning echo byte for byte");
        Require(string.Concat(second.Text)=="Synthetic book SYN-1 has two trades."&&second.End is ModelCompleted{Stop:ModelStop.EndTurn,Reply:null},"the loop's second step ends the turn");
        Usage(((ModelCompleted)second.End).Usage,UsageStatus.Recognized,40,6000,0,30,0,"each step reports its own usage");
        Require(fake.Drained,"the loop sends exactly two requests");
        Console.WriteLine("PASS Messages tool loop: breakpoints, tool requests, byte-for-byte continuation and tool results");
    }

    static async Task ReasoningStaysInTheReply()
    {
        // A provider that streams reasoning text although omission was requested.
        var fake=new FakeProvider(Sse(ToolRecording.Replace("\"thinking_delta\",\"thinking\":\"\"","\"thinking_delta\",\"thinking\":\"SYNTHETIC-REASONING plan\"")),Sse(AfterToolRecording));
        var stopped=Require<ModelCompleted>((await Call(fake,Request(tools:[ReadTrades]))).End,"streamed reasoning still completes");
        Require(stopped.Reply!.ToString()=="ModelReply","a reply prints no content");
        await Call(fake,Request(tools:[ReadTrades],messages:[new ModelUserText("Synthetic question?"),stopped.Reply,
            new ModelToolResults([new("toolu_synthetic_01","none",false),new("toolu_synthetic_02","none",false)])]));
        Require(!fake.Bodies[1].Contains("SYNTHETIC-REASONING",StringComparison.Ordinal)
            &&JsonDocument.Parse(fake.Bodies[1]).RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("thinking").GetString()=="",
            "reasoning text is discarded; only the signed block is echoed");
        Console.WriteLine("PASS reasoning and continuation data reach no event and reasoning text is never echoed");
    }

    static async Task UsageMapping()
    {
        async Task<ModelEvent> End(string recording)=>(await Call(new FakeProvider(Sse(recording)),Request())).End;
        async Task Expect(string recording,UsageStatus status,string message,long? reasoning=0)
        {
            var completed=Require<ModelCompleted>(await End(recording),message);
            Require(completed.Usage.Status==status&&(status!=UsageStatus.Recognized||completed.Usage.Reasoning.Count==reasoning),message);
        }
        await Expect(Text(StartUsage.Replace("\"input_tokens\":21,",""),FinalUsage.Replace("\"input_tokens\":21,","")),UsageStatus.Unrecognized,"input that no event reports is unrecognized");
        await Expect(Text(StartUsage.Replace("\"cache_read_input_tokens\":0","\"cache_read_input_tokens\":null"),FinalUsage.Replace("\"cache_read_input_tokens\":0","\"cache_read_input_tokens\":null")),
            UsageStatus.Unrecognized,"a priced count that every event leaves null is unrecognized");
        await Expect(Text(finalUsage:FinalUsage.Replace("\"output_tokens\":9","\"output_tokens\":9.5")),UsageStatus.Unrecognized,"a fractional count is unrecognized");
        await Expect(Text(finalUsage:FinalUsage.Replace("\"cache_creation_input_tokens\":0","\"cache_creation_input_tokens\":-1")),UsageStatus.Unrecognized,"a negative count is unrecognized");
        await Expect(Text(finalUsage:FinalUsage.Replace("\"thinking_tokens\":0","\"thinking_tokens\":10")),UsageStatus.Unrecognized,"reasoning above output is unrecognized");
        await Expect(Text(finalUsage:FinalUsage.Replace(",\"output_tokens_details\":{\"thinking_tokens\":0}","")),UsageStatus.Recognized,"reasoning without its breakdown is unknown and never priced",reasoning:null);
        await Expect(Text(finalUsage:FinalUsage.Replace("}}","},\"server_tool_use\":{\"web_search_requests\":0,\"web_fetch_requests\":0}}")),UsageStatus.Recognized,"an unnamed field holding only zeros is recognized");
        await Expect(Text(finalUsage:FinalUsage.Replace("}}","},\"server_tool_use\":{\"web_search_requests\":1}}")),UsageStatus.Unrecognized,"an unnamed nonzero count is unrecognized");
        await Expect(Text(StartUsage.Replace("\"speed\":\"standard\"","\"speed\":\"standard\",\"synthetic_modifier\":\"x\"")),UsageStatus.Unrecognized,"an unnamed value is unrecognized");
        await Expect(Text(StartUsage.Replace("\"service_tier\":\"standard\"","\"service_tier\":\"priority\"")),UsageStatus.Unrecognized,"another service tier is unrecognized");
        await Expect(Text(StartUsage.Replace("\"service_tier\":\"standard\",","")),UsageStatus.Recognized,"an unreported service tier is the standard one requested");
        await Expect(Text(StartUsage.Replace("\"inference_geo\":\"global\"","\"inference_geo\":\"us\"")),UsageStatus.Unrecognized,"another inference region is unrecognized");
        await Expect(Text(StartUsage.Replace("\"speed\":\"standard\"","\"speed\":\"fast\"")),UsageStatus.Unrecognized,"fast mode is unrecognized");
        await Expect(Text(StartUsage.Replace("\"ephemeral_1h_input_tokens\":0","\"ephemeral_1h_input_tokens\":64")),UsageStatus.Unrecognized,"a one-hour cache write is unrecognized");
        await Expect(Text(finalUsage:FinalUsage.Replace("{\"thinking_tokens\":0}","{\"thinking_tokens\":0,\"synthetic_tokens\":3}")),UsageStatus.Unrecognized,"an unnamed nonzero output breakdown is unrecognized");
        await Expect(Text(finalUsage:FinalUsage.Replace("}}","},\"synthetic_fraction\":1e-30}")),UsageStatus.Unrecognized,"an unnamed count is zero only when it is exactly zero");
        // The caching guide places input and cache counts in message_start when streaming; the streaming
        // guide's basic example reports output alone in message_delta and no cache counts anywhere.
        var located=Require<ModelCompleted>(await End(Text(finalUsage:"""{"output_tokens":9,"output_tokens_details":{"thinking_tokens":0}}""")),"output-only final usage completes");
        Usage(located.Usage,UsageStatus.Recognized,21,0,0,9,0,"input and cache counts that message_delta omits are message_start's");
        await Expect(Text("""{"input_tokens":25,"output_tokens":1}""","""{"output_tokens":15}"""),UsageStatus.Unrecognized,
            "the streaming guide's basic example reports no cache counts, so its usage is unrecognized rather than zero");
        var cumulative=Require<ModelCompleted>(await End(Deltas("""{"input_tokens":21,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":4}""",FinalUsage)),
            "two message_delta events complete");
        Usage(cumulative.Usage,UsageStatus.Recognized,21,0,0,9,0,"the last message_delta's cumulative counts are final");
        await Expect(Deltas("""{"output_tokens":500}""",FinalUsage),UsageStatus.Unrecognized,"a cumulative count that falls is unrecognized");
        await Expect(Text(finalUsage:FinalUsage.Replace("\"input_tokens\":21","\"input_tokens\":3")),UsageStatus.Unrecognized,"a count below message_start's is unrecognized");
        await Expect(Deltas(FinalUsage,null),UsageStatus.Unrecognized,"a last message_delta without usage reports no final output");
        await Expect(Text(StartUsage.Replace("\"output_tokens\":1","\"output_tokens\":50")),UsageStatus.Unrecognized,"a final output below message_start's is unrecognized");
        await Expect(Deltas(FinalUsage.Replace("\"output_tokens\":9","\"output_tokens\":4").Replace("\"thinking_tokens\":0","\"thinking_tokens\":3"),
            FinalUsage.Replace(",\"output_tokens_details\":{\"thinking_tokens\":0}","")),UsageStatus.Recognized,"reasoning comes from the last message_delta alone",reasoning:null);
        // A usage block that message_start leaves out reports nothing, as one a message_delta leaves out does.
        var unstarted=Text().Replace(",\"usage\":"+StartUsage,"");
        Require(Require<ModelCompleted>(await End(unstarted),"a message_start without usage completes").Usage.Status==UsageStatus.Recognized,
            "counts that only the last message_delta reports are enough");
        await Expect(unstarted.Replace(FinalUsage,"""{"output_tokens":9,"output_tokens_details":{"thinking_tokens":0}}"""),UsageStatus.Unrecognized,
            "without message_start's usage, input and cache counts that no message_delta reports are unrecognized");
        Console.WriteLine("PASS messages_v1 usage: priced counts required, cumulative and located as documented, reasoning informational, modifiers and unnamed fields checked");
    }

    static async Task StopReasons()
    {
        async Task<(List<string> Text,ModelEvent End)> Run(string recording)=>await Call(new FakeProvider(Sse(recording)),Request(tools:[ReadTrades]));
        var cut=Require<ModelCompleted>((await Run(TruncatedToolRecording)).End,"an output limit completes");
        Require(cut is {Stop:ModelStop.OutputLimit,ToolRequests.IsEmpty:true,Reply:null},"a tool request cut off by the output limit is not returned");
        Usage(cut.Usage,UsageStatus.Recognized,25,0,0,512,0,"a limited call keeps its final usage");
        Require((await Run(Text(stop:"model_context_window_exceeded"))).End is ModelCompleted{Stop:ModelStop.ContextWindow},"a filled context window is its own stop");
        var midstream=await Run(Text(stop:"refusal"));
        Require(string.Concat(midstream.Text)=="Synthetic answer."&&midstream.End is ModelCompleted{Stop:ModelStop.Refusal,Usage.Status:UsageStatus.Recognized},
            "a refusal after output is billed as reported");
        Require((await Run(RefusalRecording)).End is ModelCompleted{Stop:ModelStop.Refusal,Usage.Status:UsageStatus.Unrecognized},
            "a refusal before any content is unrecognized, since its billing depends on the vendor's category list");
        var thought=Event("content_block_start","""{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""")
            +Event("content_block_delta","""{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"U3ludGhldGljU2lnbmF0dXJl"}}""")
            +Event("content_block_stop","""{"type":"content_block_stop","index":0}""");
        var empty=Event("content_block_start","""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""")+Event("content_block_stop","""{"type":"content_block_stop","index":0}""");
        var silent=Event("content_block_start","""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""")
            +Event("content_block_delta","""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":""}}""")+Event("content_block_stop","""{"type":"content_block_stop","index":0}""");
        var tool=Event("content_block_start","""{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_synthetic_04","name":"read_trades","input":{}}}""")
            +Event("content_block_stop","""{"type":"content_block_stop","index":0}""");
        foreach(var (blocks,what,status) in new[]{(thought,"reasoning alone",UsageStatus.Unrecognized),(empty,"an empty text block",UsageStatus.Unrecognized),
            (silent,"a text block whose only delta is empty",UsageStatus.Unrecognized),(tool,"a tool request",UsageStatus.Recognized)})
            Require((await Run(RefusalRecording.Replace("event: message_delta",blocks+"event: message_delta"))).End is ModelCompleted{Stop:ModelStop.Refusal} refused&&refused.Usage.Status==status,
                $"a refusal after {what} is {status}, since output is text or a tool request");
        var bare=Require<ModelCompleted>((await Run(TruncatedToolRecording.Replace(Line(TruncatedToolRecording,"event: content_block_delta")+"\n\n","")
            .Replace("\"max_tokens\"","\"tool_use\""))).End,"a tool stop without input deltas completes");
        Require(bare is {Stop:ModelStop.ToolUse}&&Same(bare.ToolRequests.Single().Input,"{}"),"a tool whose deltas carry nothing takes no input");
        foreach(var stop in new[]{"stop_sequence","pause_turn","synthetic_future_stop"})
            Require((await Run(Text(stop:stop))).End is ModelFailed{Failure:ModelFailure.Transport,Usage.Status:UsageStatus.Recognized},$"an unexpected stop reason fails as unreadable but keeps final usage: {stop}");
        Require((await Run(Text(stop:"tool_use"))).End is ModelFailed{Failure:ModelFailure.Transport},"a tool stop without a tool request fails");
        Require((await Run(ToolRecording.Replace("\"stop_reason\":\"tool_use\"","\"stop_reason\":\"end_turn\""))).End is ModelFailed{Failure:ModelFailure.Transport},
            "an ended turn holding a tool request fails");
        Require((await Run(ToolRecording.Replace("{\\\"book\\\":\\\"SYN-2\\\"}","[\\\"SYN-2\\\"]"))).End is ModelFailed{Failure:ModelFailure.Transport},
            "tool input that is not an object fails");
        Require((await Run(ToolRecording.Replace("{\\\"book\\\":\\\"SYN-2\\\"}","{\\\"book\\\":\\\"SYN-2\\\",\\\"book\\\":\\\"SYN-3\\\"}"))).End is ModelFailed{Failure:ModelFailure.Transport},
            "tool input that repeats a name fails rather than choosing one value");
        Require((await Run(ToolRecording.Replace("{\\\"book\\\":\\\"SYN-2\\\"}","{\\\"book\\\":\\\"\\\\ud800\\\"}"))).End is ModelFailed{Failure:ModelFailure.Transport},
            "tool input holding an escaped lone surrogate fails");
        Console.WriteLine("PASS Messages stop reasons: limits, context window, refusals before and after output, unexpected stops");
    }

    static async Task ProviderErrors()
    {
        var errorBody="""{"type":"error","error":{"type":"TYPE","message":"MESSAGE"},"request_id":"req_synthetic_01"}""";
        string Body(string type,string message="SYNTHETIC-ECHO Synthetic question? sent with key synthetic-...7Q4x")=>errorBody.Replace("TYPE",type).Replace("MESSAGE",message);
        foreach(var (status,type,failure) in new[]{(400,"invalid_request_error",ModelFailure.InvalidRequest),(401,"authentication_error",ModelFailure.Authentication),
            (402,"billing_error",ModelFailure.Authentication),(403,"permission_error",ModelFailure.Authentication),(404,"not_found_error",ModelFailure.InvalidRequest),
            (409,"conflict_error",ModelFailure.InvalidRequest),(413,"request_too_large",ModelFailure.InvalidRequest),(418,"synthetic_error",ModelFailure.InvalidRequest),
            (429,"rate_limit_error",ModelFailure.RateLimited),(500,"api_error",ModelFailure.Overloaded),(503,"synthetic_error",ModelFailure.Overloaded),
            (504,"timeout_error",ModelFailure.Overloaded),(529,"overloaded_error",ModelFailure.Overloaded),(204,"synthetic_error",ModelFailure.Transport),
            (302,"synthetic_error",ModelFailure.Transport)})
        {
            var (text,end)=await Call(new FakeProvider(Status(status,Body(type))),Request());
            Require(text.Count==0&&end is ModelFailed{Usage.Status:UsageStatus.Unknown,Served:null} failed&&failed.Failure==failure,
                $"HTTP {status} is {failure} with unknown usage, since no status is documented as not billed");
        }
        var tooLong=(await Call(new FakeProvider(Status(400,Body("invalid_request_error","prompt is too long: 1000600 tokens > 1000000 maximum"))),Request())).End;
        Require(tooLong is ModelFailed{Failure:ModelFailure.ContextWindow,Usage.Status:UsageStatus.Unknown},"a prompt longer than the context window is its own failure");
        // A 400's body only refines it: one that cannot be read as the documented error keeps the status's classification.
        var quick=new ModelTransportLimits(TimeSpan.FromMilliseconds(300),Limits.ResponseBytes);
        foreach(var (name,body,limits) in new (string,Func<Stream>,ModelTransportLimits)[]{
            ("malformed JSON",()=>new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"error\",\"error\":")),Limits),
            ("an escaped lone surrogate",()=>new MemoryStream(Encoding.UTF8.GetBytes(Body("invalid_request_error","prompt is too long \\ud800"))),Limits),
            ("invalid UTF-8",()=>new MemoryStream([..Encoding.UTF8.GetBytes(Body("invalid_request_error","prompt is too long")),0xFF]),Limits),
            ("a stalled body",()=>new StallingStream(Encoding.UTF8.GetBytes(Body("invalid_request_error","prompt is too long")[..40])),quick),
            ("a lost connection",()=>new StallingStream(Encoding.UTF8.GetBytes(Body("invalid_request_error","prompt is too long")[..40]),new IOException("synthetic reset")),Limits)})
        {
            var end=(await Within(Call(new FakeProvider(_=>Task.FromResult(Reply(HttpStatusCode.BadRequest,"application/json",body()))),Request(),limits),"a 400 with "+name+" ends")).End;
            Require(end is ModelFailed{Failure:ModelFailure.InvalidRequest,Usage.Status:UsageStatus.Unknown},$"a 400 with {name} is an invalid request, whatever its body says");
        }
        using(var waiting=new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            var stalled=new FakeProvider(_=>Task.FromResult(Reply(HttpStatusCode.BadRequest,"application/json",new StallingStream(Encoding.UTF8.GetBytes(Body("invalid_request_error")[..40])))));
            Require((await Within(Call(stalled,Request(),ct:waiting.Token),"a call cancelled during a 400 body ends")).End is ModelFailed{Failure:ModelFailure.Cancelled},
                "cancellation while a 400 body is read ends cancelled");
        }
        foreach(var (type,failure) in new[]{("authentication_error",ModelFailure.Authentication),("billing_error",ModelFailure.Authentication),
            ("permission_error",ModelFailure.Authentication),("rate_limit_error",ModelFailure.RateLimited),("overloaded_error",ModelFailure.Overloaded),
            ("api_error",ModelFailure.Overloaded),("timeout_error",ModelFailure.Overloaded),("invalid_request_error",ModelFailure.InvalidRequest),
            ("not_found_error",ModelFailure.InvalidRequest),("conflict_error",ModelFailure.InvalidRequest),("request_too_large",ModelFailure.InvalidRequest),
            ("synthetic_future_error",ModelFailure.Transport)})
        {
            var recording=Text().Replace("event: content_block_stop","event: error\ndata: "+Body(type)+"\n\nevent: content_block_stop");
            var (text,end)=await Call(new FakeProvider(Sse(recording)),Request());
            Require(string.Concat(text)=="Synthetic answer."&&end is ModelFailed{Usage.Status:UsageStatus.Unknown,Served:Model} failed&&failed.Failure==failure,
                $"a streamed {type} after text is {failure} and leaves usage unknown");
        }
        var output=new StringWriter();var (stdout,stderr)=(Console.Out,Console.Error);
        Console.SetOut(output);Console.SetError(output);
        try{await Call(new FakeProvider(Status(401,Body("authentication_error"))),Request());await Call(new FakeProvider(Sse(Text().Replace("event: content_block_stop","event: error\ndata: "+Body("api_error")+"\n\nevent: content_block_stop"))),Request());}
        finally{Console.SetOut(stdout);Console.SetError(stderr);}
        Require(output.ToString()=="","an error body is never logged");
        Console.WriteLine("PASS Messages errors: statuses and streamed errors classify, echoed request text and key appear nowhere");
    }

    static async Task MalformedStreams()
    {
        var start=Line(Text(),"event: message_start");
        foreach(var (name,recording) in new[]{
            ("truncated JSON",Text().Replace("{\"type\":\"text_delta\",\"text\":\"answer.\"}}","{\"type\":\"text_delta\",")),
            ("an event name that differs from its type",Text().Replace("event: content_block_stop","event: content_block_delta")),
            ("a delta for a block never started",Text().Replace(Line(Text(),"event: content_block_start")+"\n\n","")),
            ("an unknown content block",Text().Replace("{\"type\":\"text\",\"text\":\"\"}","{\"type\":\"server_tool_use\",\"id\":\"srvtoolu_synthetic\",\"name\":\"web_search\",\"input\":{}}")),
            ("a fallback block that was never requested",Text().Replace("{\"type\":\"text\",\"text\":\"\"}","{\"type\":\"fallback\"}")),
            ("an unknown delta",Text().Replace("{\"type\":\"text_delta\",\"text\":\"answer.\"}","{\"type\":\"citations_delta\",\"citation\":{}}")),
            ("a delta of another block kind",Text().Replace("{\"type\":\"text_delta\",\"text\":\"answer.\"}","{\"type\":\"input_json_delta\",\"partial_json\":\"{}\"}")),
            ("a block starting with content",Text().Replace("{\"type\":\"text\",\"text\":\"\"}","{\"type\":\"text\",\"text\":\"Unstreamed \"}")),
            ("a skipped block index",Text().Replace("\"index\":0","\"index\":1")),
            ("a message_delta inside a block",Text().Replace(Line(Text(),"event: content_block_stop")+"\n\n","")),
            ("a stop without message_delta",Text().Replace(Line(Text(),"event: message_delta")+"\n\n","")),
            ("a stream ending before message_stop",Text().Replace("\n\n"+Line(Text(),"event: message_stop"),"")),
            ("content before message_start",Text().Replace(start+"\n\n","")+"\n\n"+start),
            ("a second message_start",Text().Replace("event: ping\ndata: {\"type\": \"ping\"}",start)),
            ("a message_start with content",Text().Replace("\"content\":[]","\"content\":[{\"type\":\"text\",\"text\":\"x\"}]")),
            ("data that is not an object",Text().Replace("data: {\"type\": \"ping\"}","data: 42")),
            ("an event that repeats a name",Text().Replace("{\"type\":\"text_delta\",\"text\":\"answer.\"}","{\"type\":\"text_delta\",\"text\":\"answer.\",\"text\":\"other\"}")),
            ("a thinking block without its signature",ToolRecording.Replace(Line(ToolRecording,"event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\"")+"\n\n","")),
            ("a block after message_delta",Text().Replace("event: message_stop",Event("content_block_start","""{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""")+"event: message_stop")),
            ("a tool starting with input",ToolRecording.Replace("\"id\":\"toolu_synthetic_01\",\"name\":\"read_trades\",\"input\":{}","\"id\":\"toolu_synthetic_01\",\"name\":\"read_trades\",\"input\":{\"book\":\"A\"}")),
            ("an escaped lone surrogate in a value",Text().Replace("\"text\":\"answer.\"","\"text\":\"answer\\ud800\"")),
            ("an escaped lone surrogate in a name",Text().Replace("data: {\"type\": \"ping\"}","data: {\"type\":\"ping\",\"\\ud800\":0}")),
            ("a message_delta usage that is not an object",Text(finalUsage:"42")),
            ("a message_start usage that is not an object",Text(startUsage:"\"none\""))})
        {
            var (_,end)=await Call(new FakeProvider(Sse(recording)),Request(tools:[ReadTrades]));
            Require(end is ModelFailed{Failure:ModelFailure.Transport,Usage.Status:UsageStatus.Unknown},"a malformed stream fails as unreadable with unknown usage: "+name);
        }
        var invalid=Encoding.UTF8.GetBytes(Text().Replace("answer.","answer\u0001")+"\n\n");invalid[Array.IndexOf(invalid,(byte)1)]=0xFF;
        Require((await Call(new FakeProvider(_=>Task.FromResult(Reply(HttpStatusCode.OK,"text/event-stream",new MemoryStream(invalid)))),Request())).End is ModelFailed{Failure:ModelFailure.Transport},
            "invalid UTF-8 fails as unreadable");
        Require((await Call(new FakeProvider(_=>Task.FromResult(Reply(HttpStatusCode.OK,"application/json",new MemoryStream(Encoding.UTF8.GetBytes("{}"))))),Request())).End is ModelFailed{Failure:ModelFailure.Transport},
            "a success that is not an event stream fails as unreadable");
        var encoded=Reply(HttpStatusCode.OK,"text/event-stream",new MemoryStream(Encoding.UTF8.GetBytes(Text()+"\n\n")));
        encoded.Content.Headers.ContentEncoding.Add("gzip");
        Require((await Call(new FakeProvider(_=>Task.FromResult(encoded)),Request())).End is ModelFailed{Failure:ModelFailure.Transport},
            "an encoded event stream, which was never requested, fails as unreadable");
        Console.WriteLine("PASS Messages malformed streams fail as unreadable with unknown usage");
    }

    static async Task TransportAndCancellation()
    {
        var recording=Encoding.UTF8.GetBytes(Text()+"\n\n");
        var exact=new ModelTransportLimits(Limits.Idle,recording.Length);
        Require((await Call(new FakeProvider(Sse(Text())),Request(),exact)).End is ModelCompleted,"a response of exactly the byte bound completes");
        Require((await Call(new FakeProvider(Sse(Text())),Request(),exact with{ResponseBytes=recording.Length-1})).End is ModelFailed{Failure:ModelFailure.ResponseTooLarge,Usage.Status:UsageStatus.Unknown},
            "one byte past the bound is too large");
        Require((await Call(new FakeProvider(Status(400,"{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\""+new string('x',4096)+"\"}}")),Request(),new(Limits.Idle,1024))).End
            is ModelFailed{Failure:ModelFailure.ResponseTooLarge},"an error body past the bound is too large");

        Require((await Call(new FakeProvider(_=>throw new HttpRequestException(HttpRequestError.ConnectionError,"synthetic refusal")),Request())).End
            is ModelFailed{Failure:ModelFailure.Transport,Usage:{Status:UsageStatus.Recognized,UncachedInput.Source:TokenSource.NotBilled,Output.Count:0}},
            "a connection never established sends nothing, so its input is known zero");
        foreach(var error in new[]{HttpRequestError.NameResolutionError,HttpRequestError.SecureConnectionError})
            Require((await Call(new FakeProvider(_=>throw new HttpRequestException(error,"synthetic")),Request())).End is ModelFailed{Usage.UncachedInput.Source:TokenSource.NotBilled},
                $"{error} sends nothing");
        Require((await Call(new FakeProvider(_=>throw new HttpRequestException(HttpRequestError.ResponseEnded,"synthetic")),Request())).End
            is ModelFailed{Failure:ModelFailure.Transport,Usage.Status:UsageStatus.Unknown},"a failure after sending leaves usage unknown");
        var prefix=Encoding.UTF8.GetBytes(Text()[..Text().IndexOf("event: content_block_stop",StringComparison.Ordinal)]);
        var reset=await Call(new FakeProvider(_=>Task.FromResult(Reply(HttpStatusCode.OK,"text/event-stream",new StallingStream(prefix,new IOException("synthetic reset"))))),Request());
        Require(string.Concat(reset.Text)=="Synthetic answer."&&reset.End is ModelFailed{Failure:ModelFailure.Transport,Usage.Status:UsageStatus.Unknown,Served:Model},
            "a connection lost mid-stream keeps the streamed text and leaves usage unknown");
        var quick=new ModelTransportLimits(TimeSpan.FromMilliseconds(300),Limits.ResponseBytes);
        var stalled=await Within(Call(new FakeProvider(_=>Task.FromResult(Reply(HttpStatusCode.OK,"text/event-stream",new StallingStream(prefix)))),Request(),quick),"a stalled body ends");
        Require(string.Concat(stalled.Text)=="Synthetic answer."&&stalled.End is ModelFailed{Failure:ModelFailure.Transport,Usage.Status:UsageStatus.Unknown},"a stalled body fails after the idle timeout");
        var silent=await Within(Call(new FakeProvider(async ct=>{await Task.Delay(Timeout.Infinite,ct);throw new InvalidOperationException();}),Request(),quick),"a call whose headers never arrive ends");
        Require(silent.End is ModelFailed{Failure:ModelFailure.Transport,Usage.Status:UsageStatus.Unknown},"headers that never arrive fail after the idle timeout");

        using var stop=new CancellationTokenSource();
        var fake=new FakeProvider(_=>Task.FromResult(Reply(HttpStatusCode.OK,"text/event-stream",new StallingStream(prefix))));
        var events=new List<ModelEvent>();
        await foreach(var e in new MessagesAdapter(fake,Limits).Stream(Request(),Key,stop.Token)){events.Add(e);if(e is ModelTextDelta)stop.Cancel();}
        Require(events[^1] is ModelFailed{Failure:ModelFailure.Cancelled,Usage.Status:UsageStatus.Unknown,Served:Model}&&events.Count(e=>e is ModelTextDelta)==1,
            "a call cancelled between events ends cancelled with unknown usage and yields no further text");
        foreach(var (name,script) in new (string,Func<CancellationToken,Task<HttpResponseMessage>>)[]{
            ("a pending read",_=>Task.FromResult(Reply(HttpStatusCode.OK,"text/event-stream",new StallingStream(prefix)))),
            ("the header wait",async ct=>{await Task.Delay(Timeout.Infinite,ct);throw new InvalidOperationException();})})
        {
            using var waiting=new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var end=(await Within(Call(new FakeProvider(script),Request(),ct:waiting.Token),"a call cancelled during "+name+" ends")).End;
            Require(end is ModelFailed{Failure:ModelFailure.Cancelled,Usage.Status:UsageStatus.Unknown},$"cancellation during {name} ends cancelled, not as a transport failure");
        }
        var before=new FakeProvider(Sse(Text()));
        Require((await Call(before,Request(),ct:new CancellationToken(true))).End is ModelFailed{Failure:ModelFailure.Cancelled,Usage.UncachedInput.Source:TokenSource.NotBilled}&&before.Bodies.Count==0,
            "a call cancelled before sending sends nothing");
        Console.WriteLine("PASS Messages transport: byte bound, nothing-sent failures, lost connections, idle timeouts and cancellation");
    }

    static async Task ServedModel()
    {
        var other=await Call(new FakeProvider(Sse(Text().Replace("\"model\":\"synthetic-messages-model\"","\"model\":\"synthetic-other-model\""))),Request());
        Require(other.Text.Count==0&&other.End is ModelFailed{Failure:ModelFailure.ModelMismatch,Usage.Status:UsageStatus.Unrecognized,Served:"synthetic-other-model"},
            "a served model neither requested nor accepted ends the call before any text, with unrecognized usage");
        var alias=await Call(new FakeProvider(Sse(Text().Replace("\"model\":\"synthetic-messages-model\"","\"model\":\"synthetic-messages-model-20261020\""))),
            Request(accepted:["synthetic-messages-model-20261020"]));
        Require(alias.End is ModelCompleted{Served:"synthetic-messages-model-20261020",Usage.Status:UsageStatus.Recognized},"an accepted served identifier completes and is reported");
        Console.WriteLine("PASS Messages served model: a mismatch publishes nothing and an accepted identifier is reported");
    }

    static async Task RefusedBeforeSending()
    {
        async Task Refused(string code,ModelRequest request,string key=Key)
        {
            var fake=new FakeProvider();
            try{await foreach(var _ in new MessagesAdapter(fake,Limits).Stream(request,key,CancellationToken.None)){}}
            catch(ModelCallException e) when(e.Code==code&&!Withheld.Any(w=>e.Message.Contains(w,StringComparison.Ordinal))){Require(fake.Bodies.Count==0,code+" sends nothing");return;}
            throw new Exception("Accepted a request that must be refused with "+code);
        }
        await Refused("parameter_required",Request(reasoning:null));
        await Refused("parameter_required",Request(reasoning:"extreme"));
        await Refused("model_identifier_invalid",Request(model:"synthetic model"));
        await Refused("model_identifier_invalid",Request(model:"-synthetic"));
        await Refused("credential_unavailable",Request(),"short");
        await Refused("credential_unavailable",Request(),Key+"\r\nx-injected: 1");
        var replied=Require<ModelCompleted>((await Call(new FakeProvider(Sse(ToolRecording)),Request(tools:[ReadTrades]))).End,"a reply for replay").Reply!;
        foreach(var (name,request) in new[]{("a reply replayed to another model",Request(model:"synthetic-other-model",messages:[new ModelUserText("q"),replied,new ModelToolResults([new("toolu_synthetic_01","r",false)])])),
            ("a conversation ending with a reply",Request(messages:[new ModelUserText("q"),replied])),("no output tokens",Request(maxTokens:0)),
            ("an unset list of accepted identifiers",Request() with{Model=new(Model,default)}),("an unset tool list",Request() with{Tools=default}),
            ("an empty system instruction",Request() with{System=""}),("empty user text",Request(messages:[new ModelUserText("")])),
            ("no tool results",Request(caching:true,messages:[new ModelUserText("q"),replied,new ModelToolResults([])]))})
        {
            var fake=new FakeProvider();
            try{await foreach(var _ in new MessagesAdapter(fake,Limits).Stream(request,Key,CancellationToken.None)){}throw new Exception("Accepted "+name);}
            catch(ArgumentException){Require(fake.Bodies.Count==0,name+" sends nothing");}
        }
        Console.WriteLine("PASS Messages refusals before sending: reasoning, identifiers, keys, request shape and replies are checked first");
    }

    // A local fake provider: each request must reach exactly the documented endpoint with only the
    // documented headers, and is answered by the next scripted response.
    sealed class FakeProvider(params Func<CancellationToken,Task<HttpResponseMessage>>[] script):HttpMessageHandler
    {
        readonly Queue<Func<CancellationToken,Task<HttpResponseMessage>>> pending=new(script);
        public readonly List<string> Bodies=[];
        public bool Drained=>pending.Count==0;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Require(request.Method==HttpMethod.Post&&request.RequestUri?.AbsoluteUri=="https://api.anthropic.com/v1/messages","a request reaches only the documented endpoint");
            Require(request.Headers.Select(h=>(h.Key.ToLowerInvariant(),string.Join(",",h.Value))).Order().SequenceEqual([("anthropic-version","2023-06-01"),("x-api-key",Key)]),
                "a request carries only the documented headers, with the key in its own header");
            Require(request.Content?.Headers.ContentType?.MediaType=="application/json","a request body is JSON");
            var body=await request.Content!.ReadAsStringAsync(ct);
            Require(!body.Contains("7Q4x",StringComparison.Ordinal),"no request body carries the key");
            Bodies.Add(body);
            Require(pending.Count>0,"the adapter sends no request beyond the script");
            return await pending.Dequeue()(ct);
        }
    }

    // A body that delivers its first bytes, then fails or waits like a stalled provider.
    sealed class StallingStream(byte[] prefix,Exception? failure=null):Stream
    {
        int offset;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
        {
            if(offset<prefix.Length){var count=Math.Min(buffer.Length,prefix.Length-offset);prefix.AsMemory(offset,count).CopyTo(buffer);offset+=count;return count;}
            if(failure is not null)throw failure;
            await Task.Delay(Timeout.Infinite,ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken ct)=>ReadAsync(buffer.AsMemory(offset,count),ct).AsTask();
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
    }

    static async Task<(List<string> Text,ModelEvent End)> Call(FakeProvider fake,ModelRequest request,ModelTransportLimits? limits=null,CancellationToken ct=default)
    {
        var events=new List<ModelEvent>();
        await foreach(var e in new MessagesAdapter(fake,limits??Limits).Stream(request,Key,ct))events.Add(e);
        Require(events.Count>0&&events.SkipLast(1).All(e=>e is ModelTextDelta)&&events[^1] is ModelCompleted or ModelFailed,"a call streams text deltas, then exactly one completion or failure");
        var printed=string.Join("\n",events.Select(e=>e.ToString()).Concat(events.OfType<ModelCompleted>().SelectMany(c=>c.ToolRequests.Select(r=>r.Input.GetRawText()))));
        Require(!Withheld.Any(w=>printed.Contains(w,StringComparison.Ordinal)),"no event carries the key, an identifier, reasoning, continuation data or error text");
        return (events.OfType<ModelTextDelta>().Select(d=>d.Text).ToList(),events[^1]);
    }

    // An idle timeout that never fires fails here rather than hanging the suite.
    static async Task<T> Within<T>(Task<T> call,string message)=>
        await Task.WhenAny(call,Task.Delay(TimeSpan.FromSeconds(10)))==call?await call:throw new Exception(message+" within its idle timeout");

    static ModelRequest Request(bool caching=false,ImmutableArray<ModelTool> tools=default,ImmutableArray<ModelMessage> messages=default,string? reasoning="medium",
        string model=Model,ImmutableArray<string> accepted=default,int maxTokens=512)=>
        new(new(model,accepted.IsDefault?[]:accepted),"Synthetic preamble.",messages.IsDefault?[new ModelUserText("Synthetic question?")]:messages,tools.IsDefault?[]:tools,
            new(maxTokens,reasoning,caching));

    static readonly ModelTool ReadTrades=new("read_trades","Reads the synthetic trades of one book.",
        JsonDocument.Parse("""{"type":"object","properties":{"book":{"type":"string"},"limit":{"type":"integer"}},"required":["book"],"additionalProperties":false}""").RootElement.Clone());

    static Func<CancellationToken,Task<HttpResponseMessage>> Sse(string recording)=>
        _=>Task.FromResult(Reply(HttpStatusCode.OK,"text/event-stream",new MemoryStream(Encoding.UTF8.GetBytes(recording+"\n\n"))));
    static Func<CancellationToken,Task<HttpResponseMessage>> Status(int status,string body)=>
        _=>Task.FromResult(Reply((HttpStatusCode)status,"application/json",new MemoryStream(Encoding.UTF8.GetBytes(body))));
    static HttpResponseMessage Reply(HttpStatusCode status,string type,Stream body)
    {
        var content=new StreamContent(body);
        content.Headers.ContentType=new MediaTypeHeaderValue(type);
        return new(status){Content=content};
    }

    static string Event(string name,string data)=>$"event: {name}\ndata: {data}\n\n";
    // The text recording with an earlier message_delta reporting first, before the one that carries the
    // stop reason and last, which is null for a message_delta without usage.
    static string Deltas(string first,string? last)=>Text(finalUsage:last??"null").Replace("event: message_delta\n",
        Event("message_delta","{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":null,\"stop_sequence\":null},\"usage\":"+first+"}")+"event: message_delta\n");

    static string Line(string recording,string prefix)
    {
        var at=recording.IndexOf(prefix,StringComparison.Ordinal);
        var end=recording.IndexOf("\n\n",at,StringComparison.Ordinal);
        return recording[at..(end<0?recording.Length:end)];
    }
    static bool Same(string json,string expected)=>Same(JsonDocument.Parse(json).RootElement,expected);
    static bool Same(JsonElement actual,string expected)=>JsonElement.DeepEquals(actual,JsonDocument.Parse(expected).RootElement);
    static void Usage(ModelUsage usage,UsageStatus status,long? input,long? read,long? write,long? output,long? reasoning,string message)=>
        Require(usage.Mapping=="messages_v1"&&usage.Status==status&&usage.UncachedInput.Count==input&&usage.CacheRead.Count==read&&usage.CacheWrite.Count==write
            &&usage.Output.Count==output&&usage.Reasoning.Count==reasoning
            &&new[]{usage.UncachedInput,usage.CacheRead,usage.CacheWrite,usage.Output,usage.Reasoning}.All(c=>c.Source==(c.Count is null?TokenSource.Unknown:TokenSource.Reported)),message);
    static T Require<T>(ModelEvent e,string message)=>e is T value?value:throw new Exception(message+": got "+e.GetType().Name);
    static void Require(bool accepted,string message){if(!accepted)throw new Exception(message);}

    const string StartUsage="""{"input_tokens":21,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0},"output_tokens":1,"service_tier":"standard","inference_geo":"global","speed":"standard"}""";
    const string FinalUsage="""{"input_tokens":21,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":9,"output_tokens_details":{"thinking_tokens":0}}""";
    static string Text(string startUsage=StartUsage,string finalUsage=FinalUsage,string stop="end_turn")=>TextRecording
        .Replace("START_USAGE",startUsage).Replace("FINAL_USAGE",finalUsage).Replace("STOP_REASON",stop);

    // Recordings are synthetic and follow the documented event flow; each ends at message_stop.
    const string TextRecording="""
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_synthetic_text","type":"message","role":"assistant","model":"synthetic-messages-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":START_USAGE}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: ping
        data: {"type": "ping"}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Synthetic "}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"answer."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"STOP_REASON","stop_sequence":null,"stop_details":null},"usage":FINAL_USAGE}

        event: message_stop
        data: {"type":"message_stop"}
        """;

    const string ToolRecording="""
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_synthetic_tool","type":"message","role":"assistant","model":"synthetic-messages-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":3,"cache_creation_input_tokens":1200,"cache_read_input_tokens":4800,"cache_creation":{"ephemeral_5m_input_tokens":1200,"ephemeral_1h_input_tokens":0},"output_tokens":1,"service_tier":"standard","inference_geo":"global"}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"U3ludGhldGljU2lnbmF0dXJlAAEC+/9=="}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"redacted_thinking","data":"U3ludGhldGljUmVkYWN0ZWQBAgM+/9=="}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: content_block_start
        data: {"type":"content_block_start","index":2,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"Reading the book."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":2}

        event: content_block_start
        data: {"type":"content_block_start","index":3,"content_block":{"type":"tool_use","id":"toolu_synthetic_01","name":"read_trades","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":"{\"book\": \"SYN-1 \\u0"}}

        event: ping
        data: {"type": "ping"}

        event: content_block_delta
        data: {"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":"0e9\", \"limit\": 5}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":3}

        event: content_block_start
        data: {"type":"content_block_start","index":4,"content_block":{"type":"tool_use","id":"toolu_synthetic_02","name":"read_trades","input":{},"synthetic_extension":{"kept":true}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":4,"delta":{"type":"input_json_delta","partial_json":"{\"book\":\"SYN-2\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":4}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null,"stop_details":null},"usage":{"input_tokens":3,"cache_creation_input_tokens":1200,"cache_read_input_tokens":4800,"output_tokens":95,"output_tokens_details":{"thinking_tokens":40}}}

        event: message_stop
        data: {"type":"message_stop"}
        """;

    const string AfterToolRecording="""
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_synthetic_after_tool","type":"message","role":"assistant","model":"synthetic-messages-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":40,"cache_creation_input_tokens":0,"cache_read_input_tokens":6000,"output_tokens":1,"service_tier":"standard","inference_geo":"global","speed":"standard"}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Synthetic book SYN-1 has two trades."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":40,"cache_creation_input_tokens":0,"cache_read_input_tokens":6000,"output_tokens":30,"output_tokens_details":{"thinking_tokens":0}}}

        event: message_stop
        data: {"type":"message_stop"}
        """;

    const string TruncatedToolRecording="""
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_synthetic_cut","type":"message","role":"assistant","model":"synthetic-messages-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":25,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1,"service_tier":"standard","inference_geo":"global"}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_synthetic_03","name":"read_trades","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"book\":\"SY"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"max_tokens","stop_sequence":null},"usage":{"input_tokens":25,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":512,"output_tokens_details":{"thinking_tokens":0}}}

        event: message_stop
        data: {"type":"message_stop"}
        """;

    const string RefusalRecording="""
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_synthetic_refusal","type":"message","role":"assistant","model":"synthetic-messages-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":412,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0,"service_tier":"standard","inference_geo":"global"}}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"refusal","stop_sequence":null,"stop_details":{"type":"refusal","category":"cyber","explanation":"Synthetic refusal."}},"usage":{"input_tokens":412,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0,"output_tokens_details":{"thinking_tokens":0}}}

        event: message_stop
        data: {"type":"message_stop"}
        """;
}
