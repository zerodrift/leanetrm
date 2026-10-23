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
public sealed class ProviderTests(CredentialCustody custody,AgentWorker worker,ModelAdapters adapter,ILogger logger)
{
    const string Instruction="This is a connectivity test from OpenETRM's page assistant settings. It carries no workspace data.";
    const string Prompt="Reply with the single word: ready.";

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
            generation=await worker.Generation(ct);
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

    // The record of a new test, with the attested key and the request the adapter will send; null when the
    // request identity names an earlier test, which has nothing more to send.
    async Task<Opened?> Open(NpgsqlConnection connection,TestRequest body,long? generation,byte[] token,CancellationToken ct)
    {
        if(!(await Record(connection,body,generation,token,ct)).TryGetProperty("opened",out var opened))return null;
        var key=ModelCalls.Key(custody,opened);
        var request=new ModelRequest(ModelCalls.Target(opened),Instruction,[new ModelUserText(Prompt)],[],
            new(opened.GetProperty("maxOutputTokens").GetInt32(),opened.GetProperty("reasoning").GetString(),false));
        ModelAdapters.Validate(request,key);
        return new(opened.GetProperty("call").GetGuid(),request,key);
    }

    // A new test's record, or the earlier one this request identity names.
    static Task<JsonElement> Record(NpgsqlConnection connection,TestRequest body,long? generation,byte[] token,CancellationToken ct)=>
        ModelProviders.Read(connection,"SELECT assistant.open_test($1,$2,$3::text,$4,$5,$6::bigint)::text",
            [body.OperationId,body.ModelId,(object?)body.Reasoning??DBNull.Value,RuntimeSet.TestOutputTokens,Convert.ToHexStringLower(SHA256.HashData(token)),
                (object?)generation??DBNull.Value],ct);

    // Closes the record with the test's token, without a request context. A stream that ended without its final event
    // failed in this program, so the test was interrupted with unknown usage. The answer waits for the close as long as
    // one attempt may take; a close that fails transiently is retried until it lands, in the background once the answer
    // is due, so a brief failure never leaves the test open while this process lives. Any other failure is logged and
    // leaves the test in progress until the next worker start closes it. Only the client leaving cancels a test; the
    // test's deadline and the request's are its elapsed limit.
    async Task Close(Guid call,byte[] token,ModelEvent? end,bool left)
    {
        var outcome=end is null?"interrupted":ModelCalls.Outcome(end,left?"request_cancelled":"limit_elapsed");
        var closing=ModelCalls.CloseTest(worker,call,token,outcome,end);
        _=closing.ContinueWith(t=>logger.LogError("A provider test could not be closed: {ErrorType}",t.Exception!.GetBaseException().GetType().Name),
            TaskContinuationOptions.OnlyOnFaulted);
        await Task.WhenAny(closing,Task.Delay(RuntimeSet.WorkerWrite));
    }
}
