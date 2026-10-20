using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace OpenEtrm.Api;

// Test names the model and the reasoning effort; the prompt and every other parameter are fixed.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TestRequest(Guid OperationId,Guid ModelId,string? Reasoning);

// Provider Test (docs/ai-assistant.md §9): one fixed synthetic prompt, without workspace data or tools, sent once to
// a model the administrator picks. The test opens in the administrator's context, which pins its price, decrypts the
// attested key and checks the request in one transaction: a refusal records nothing, and no byte is sent before
// the record commits. The response is streamed within the test deadline and the request's. The test's own token
// then closes the record without a request context, whatever ended the stream, so a deadline, an abort or a
// permission lost meanwhile still records the outcome. The answer is what a retry returns, under the
// administrator's current authority.
public sealed class ProviderTests(CredentialCustody custody,AgentWorker worker,MessagesAdapter adapter,ILogger logger)
{
    const string Instruction="This is a connectivity test from OpenETRM's page assistant settings. It carries no workspace data.";
    const string Prompt="Reply with the single word: ready.";
    static readonly TimeSpan CloseScope=TimeSpan.FromSeconds(5),CloseRetry=TimeSpan.FromSeconds(2);

    sealed record Opened(Guid Call,ModelRequest Request,string Key)
    {
        // The key never reaches a log or an exception message through the record's generated text.
        public override string ToString()=>nameof(Opened);
    }

    // `aborted` ends at the request deadline or when the client leaves, `left` only when the client leaves.
    public async Task<JsonElement> Run(PreviewDatabase database,string profile,TestRequest body,CancellationToken aborted,CancellationToken left)
    {
        if(body.OperationId==Guid.Empty||body.ModelId==Guid.Empty||body.Reasoning?.Contains('\0')==true)throw ModelCatalog.Input();
        var token=RandomNumberGenerator.GetBytes(32);
        // The worker is consulted only for an administrator, and without it an earlier test's outcome still replays.
        long? generation=null;
        var opened=await database.WithIdentity(profile,"ManageModelProviders",async(connection,ct)=>
        {
            generation=await Generation(ct);
            return await Open(connection,body,generation,token,ct);
        },aborted);
        if(opened is not null)
        {
            ModelEvent? end=null;
            try
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(aborted);
                deadline.CancelAfter(RuntimeSet.TestDeadline);
                await foreach(var e in adapter.Stream(opened.Request,opened.Key,deadline.Token))end=e;
            }
            finally{await Close(opened.Call,token,end,left.IsCancellationRequested);}
        }
        var outcome=await database.WithIdentity(profile,"ManageModelProviders",(connection,ct)=>Record(connection,body,generation,token,ct),aborted);
        return outcome.GetProperty("test");
    }

    // The worker's current generation; none while another session holds the lock or when the worker cannot start, which
    // refuses a new test but still replays an earlier one.
    async Task<long?> Generation(CancellationToken ct)
    {
        try{return await worker.Generation(ct);}
        catch(NpgsqlException e)
        {
            logger.LogError("The agent worker could not start: {ErrorType}",e.GetType().Name);
            return null;
        }
    }

    // The record of a new test, with the attested key and the request the adapter will send; null when the
    // request identity names an earlier test, which has nothing more to send.
    async Task<Opened?> Open(NpgsqlConnection connection,TestRequest body,long? generation,byte[] token,CancellationToken ct)
    {
        if(!(await Record(connection,body,generation,token,ct)).TryGetProperty("opened",out var opened))return null;
        var vendor=opened.GetProperty("vendor").GetString()!;
        // Only the Messages adapter ships, so only a model under its mapping is qualified.
        if(vendor!="anthropic"||opened.GetProperty("mapping").GetString()!=MessagesAdapter.Mapping)
            throw new InvalidOperationException("No adapter in this release applies the model's usage mapping.");
        var key=custody.Open(opened.GetProperty("provider").GetGuid(),vendor,opened.GetProperty("credentialVersion").GetInt32(),
            new(Bytes(opened,"nonce"),Bytes(opened,"ciphertext"),Bytes(opened,"tag")));
        var request=new ModelRequest(new(opened.GetProperty("identifier").GetString()!,[..opened.GetProperty("acceptedServed").EnumerateArray().Select(i=>i.GetString()!)]),
            Instruction,[new ModelUserText(Prompt)],[],new(opened.GetProperty("maxOutputTokens").GetInt32(),opened.GetProperty("reasoning").GetString(),false));
        MessagesAdapter.Validate(request,key);
        return new(opened.GetProperty("call").GetGuid(),request,key);
    }

    // A new test's record, or the earlier one this request identity names.
    static Task<JsonElement> Record(NpgsqlConnection connection,TestRequest body,long? generation,byte[] token,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT assistant.open_test($1,$2,$3::text,$4,$5,$6::bigint)::text",
            [body.OperationId,body.ModelId,(object?)body.Reasoning??DBNull.Value,RuntimeSet.TestOutputTokens,Convert.ToHexStringLower(SHA256.HashData(token)),
                (object?)generation??DBNull.Value],ct);

    static byte[] Bytes(JsonElement opened,string name)=>Convert.FromHexString(opened.GetProperty(name).GetString()!);

    // Closes the record with the test's token, without a request context. A stream that ended without its final event
    // failed in this program, so the test was interrupted with unknown usage. The answer waits for the close as long as
    // one attempt may take; a close that fails transiently is retried until it lands, in the background once the answer
    // is due, so a brief failure never leaves the test open while this process lives. Any other failure is logged and
    // leaves the test in progress until the next worker start closes it. A close after one landed changes nothing.
    async Task Close(Guid call,byte[] token,ModelEvent? end,bool left)
    {
        var (outcome,usage,served)=end switch
        {
            ModelCompleted c=>(Outcome(c,left),c.Usage,c.Served),
            ModelFailed f=>(Outcome(f,left),f.Usage,f.Served),
            _=>("interrupted",ModelUsage.Unknown(MessagesAdapter.Mapping),null)
        };
        var closing=Closing([call,token,outcome,served is {} identifier&&ModelTarget.Valid(identifier)?identifier:DBNull.Value,Status(usage.Status),
            ..Count(usage.UncachedInput),..Count(usage.CacheRead),..Count(usage.CacheWrite),..Count(usage.Output),..Count(usage.Reasoning)]);
        _=closing.ContinueWith(t=>logger.LogError("A provider test could not be closed: {ErrorType}",t.Exception!.GetBaseException().GetType().Name),
            TaskContinuationOptions.OnlyOnFaulted);
        await Task.WhenAny(closing,Task.Delay(CloseScope));
    }

    async Task Closing(object[] arguments)
    {
        while(true)
        {
            try
            {
                using var scope=new CancellationTokenSource(CloseScope);
                await using var connection=await worker.Connection(scope.Token);
                await using var command=ScheduleStore.Command(connection,"SELECT assistant.close_test($1,$2,$3,$4::text,$5,$6::bigint,$7,$8::bigint,$9,$10::bigint,$11,$12::bigint,$13,$14::bigint,$15)",arguments);
                await command.ExecuteNonQueryAsync(scope.Token);
                return;
            }
            catch(Exception e) when(e is OperationCanceledException or NpgsqlException{IsTransient:true}){}
            await Task.Delay(CloseRetry);
        }
    }

    // A test succeeds only for a well-formed response that ends normally or at the output limit with recognized usage.
    static string Outcome(ModelEvent end,bool left)=>end switch
    {
        ModelCompleted{Stop:ModelStop.EndTurn or ModelStop.OutputLimit,Usage.Status:UsageStatus.Recognized}=>"succeeded",
        ModelCompleted{Stop:ModelStop.EndTurn or ModelStop.OutputLimit}=>"usage_unrecognized",
        ModelCompleted{Stop:ModelStop.Refusal}=>"model_refused",
        ModelCompleted{Stop:ModelStop.ContextWindow}=>"context_window_exceeded",
        // The test offers no tool, so a tool request is not the documented response.
        ModelCompleted=>"model_provider_transport",
        // Only the client leaving cancels a test; the test's deadline and the request's are its elapsed limit.
        ModelFailed{Failure:ModelFailure.Cancelled}=>left?"request_cancelled":"limit_elapsed",
        ModelFailed{Failure:var failure}=>failure switch
        {
            ModelFailure.Authentication=>"model_provider_authentication",
            ModelFailure.RateLimited=>"model_provider_rate_limited",
            ModelFailure.Overloaded=>"model_provider_overloaded",
            ModelFailure.InvalidRequest=>"model_provider_invalid_request",
            ModelFailure.ContextWindow=>"context_window_exceeded",
            ModelFailure.Transport=>"model_provider_transport",
            ModelFailure.ResponseTooLarge=>"model_provider_response_too_large",
            ModelFailure.ModelMismatch=>"model_mismatch",
            _=>throw new ArgumentOutOfRangeException(nameof(end))
        },
        _=>throw new ArgumentException("A call ends with a completion or a failure.",nameof(end))
    };

    static string Status(UsageStatus status)=>status switch
    {
        UsageStatus.Recognized=>"recognized",
        UsageStatus.Unrecognized=>"unrecognized",
        _=>"unknown"
    };

    static object[] Count(TokenCount count)=>[count.Count is {} tokens?tokens:DBNull.Value,count.Source switch
    {
        TokenSource.Reported=>"reported",
        TokenSource.NotBilled=>"not_billed",
        _=>"unknown"
    }];
}
