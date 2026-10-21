using System.Text;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;

// What a turn sends and publishes that needs no database (docs/ai-assistant.md §7.4, §7.6): answer text split into
// appends of at most 2,048 bytes, never inside a character, and the user's message, with each earlier turn as history.
static class TurnWorkerChecks
{
    public static void Run()
    {
        // One byte first, so a piece's last character would straddle the bound.
        var text="b"+string.Concat(Enumerable.Repeat("a€😀",1000));
        var chunks=TurnWorker.Chunks(text,2048).ToArray();
        Require(string.Concat(chunks)==text&&chunks.All(c=>Encoding.UTF8.GetByteCount(c)<=2048&&Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(c))==c)
            &&chunks.SkipLast(1).All(c=>Encoding.UTF8.GetByteCount(c)>2044)&&Encoding.UTF8.GetByteCount(chunks[0])==2045,
            "answer text splits into appends of at most 2,048 bytes, each whole characters and filled to within one");
        Require(TurnWorker.Chunks(new string('a',4096),2048).All(c=>c.Length==2048),"a piece fills to the bound when a character ends there");
        Require(!TurnWorker.Chunks("",2048).Any(),"no text publishes nothing");
        Console.WriteLine("PASS answer text splits into appends of at most 2,048 bytes, never inside a character");

        Require(TurnWorker.Message(JsonDocument.Parse("[]").RootElement,"Q?")=="Q?","a first turn sends its question alone");
        var earlier=JsonDocument.Parse("""
            [{"ordinal":1,"question":"A?","answer":"Yes.","incomplete":false,"state":"completed","outcome":null,
              "context":{"mode":"current","cutoff":"2020-12-18T12:00:00+00:00","frameId":"00000000-0000-0000-0000-000000000001"}},
             {"ordinal":2,"question":"B?","answer":"Part","incomplete":true,"state":"failed","outcome":"model_provider_stream_interrupted",
              "context":{"mode":"retained","cutoff":"2020-12-17T12:00:00+00:00","frameId":null}},
             {"ordinal":3,"question":"C?","answer":"","incomplete":false,"state":"failed","outcome":"ask_incomplete",
              "context":{"mode":"current","cutoff":null,"frameId":null}}]
            """).RootElement;
        Require(TurnWorker.Message(earlier,"D?")==
            "Earlier turn 1 (completed; current knowledge, cutoff 2020-12-18T12:00:00+00:00, As of frame 00000000-0000-0000-0000-000000000001):\n"+
            "Question: A?\nAnswer: Yes.\n\n"+
            "Earlier turn 2 (failed with model_provider_stream_interrupted; a retained context, cutoff 2020-12-17T12:00:00+00:00):\n"+
            "Question: B?\nAnswer (incomplete): Part\n\n"+
            "Earlier turn 3 (failed with ask_incomplete; no pinned context):\nQuestion: C?\nNo answer.\n\n"+
            "Current question: D?",
            "a follow-up sends each earlier turn as labeled history, then the question");
        Console.WriteLine("PASS a follow-up sends each earlier turn's ending, pinned context and answer, marked when incomplete or noted as missing, then the question");

        // Input is known only when nothing was sent; a stream this program cancelled reports none.
        var known=ModelUsage.NothingSent(MessagesAdapter.Mapping);
        var unknown=ModelUsage.Unknown(MessagesAdapter.Mapping);
        ModelEvent Completed(ModelStop stop,ModelUsage usage)=>new ModelCompleted(stop,[],null,usage,"model-synthetic");
        ModelEvent Failed(ModelFailure failure,ModelUsage usage)=>new ModelFailed(failure,usage,null);
        foreach(var (end,published,retriesLeft,expected) in new (ModelEvent,bool,bool,(string,string?)?)[]
        {
            (Completed(ModelStop.EndTurn,known),true,true,("completed",null)),
            (Completed(ModelStop.EndTurn,unknown),true,false,("completed",null)),
            (Completed(ModelStop.OutputLimit,known),true,true,("failed","limit_output_tokens")),
            (Completed(ModelStop.Refusal,known),false,true,("failed","model_refused")),
            (Completed(ModelStop.ContextWindow,known),false,true,("failed","context_window_exceeded")),
            (Completed(ModelStop.ToolUse,known),true,true,("failed","model_provider_transport")),
            (Failed(ModelFailure.Cancelled,unknown),true,true,("failed","limit_elapsed")),
            (Failed(ModelFailure.Overloaded,known),false,true,null),
            (Failed(ModelFailure.Transport,known),false,true,null),
            (Failed(ModelFailure.Transport,known),false,false,("failed","model_provider_transport")),
            (Failed(ModelFailure.Overloaded,unknown),false,true,("failed","model_provider_overloaded")),
            (Failed(ModelFailure.Transport,known),true,true,("failed","model_provider_stream_interrupted")),
            (Failed(ModelFailure.RateLimited,known),false,true,("failed","model_provider_rate_limited")),
            (Failed(ModelFailure.Authentication,unknown),true,true,("failed","model_provider_stream_interrupted")),
            (Failed(ModelFailure.ResponseTooLarge,unknown),true,true,("failed","model_provider_response_too_large")),
        })
            Require(Equals(TurnWorker.Ending(end,published,retriesLeft),expected),$"a call ending {end} {(published?"after":"before")} text ends its turn {expected}");
        Console.WriteLine("PASS a call's ending ends its turn as its stop or failure requires, after text as interrupted unless too large, and only a "+
            "transport failure or overload whose input is known and that published nothing is retried, while retries remain");

        // A worker write that must land is retried only after a failure a later attempt may not repeat.
        PostgresException State(string state)=>new("Synthetic","ERROR","ERROR",state);
        foreach(var (failure,temporary) in new (Exception,bool)[]
        {
            (new OperationCanceledException(),true),(new NpgsqlException("Synthetic connection loss"),true),
            (State("40001"),true),(State("40P01"),true),(State("55P03"),true),(State("57014"),true),(State("57P01"),true),
            (State("08006"),true),(State("53300"),true),
            (State("P0001"),false),(State("55000"),false),(State("42501"),false),(State("23514"),false),(new InvalidOperationException(),false)
        })
            Require(AgentWorker.Temporary(failure)==temporary,$"{failure.GetType().Name} {(failure as PostgresException)?.SqlState} is {(temporary?"":"not ")}retried");
        Console.WriteLine("PASS a worker write that must land is retried after a timeout, a lost connection or a conflict the database may not repeat, "+
            "never after a refusal or a violated invariant");
    }

    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
}
