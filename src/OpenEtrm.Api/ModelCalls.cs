using System.Text.Json;
using Npgsql;

namespace OpenEtrm.Api;

// What provider Test and turns share about a model call (docs/ai-assistant.md §5.3, §6.2, §7.4): the attested key and
// target an opened call carries, its outcome, and the close each records with the call's token and without a request
// context: the outcome, the served identifier and the usage by dimension.
static class ModelCalls
{
    // The attested key an opened call carries, sealed until here. Only the Messages adapter ships, so only a model under
    // its mapping is qualified, and any other is refused as one this release has no confirmed mapping for.
    public static string Key(CredentialCustody custody,JsonElement opened)
    {
        var vendor=opened.GetProperty("vendor").GetString()!;
        if(vendor!="anthropic"||opened.GetProperty("mapping").GetString()!=MessagesAdapter.Mapping)
            throw new ModelAdministrationException("model_mapping_unconfirmed",ModelCatalog.Refusal("model_mapping_unconfirmed"));
        return custody.Open(opened.GetProperty("provider").GetGuid(),vendor,opened.GetProperty("credentialVersion").GetInt32(),
            new(Bytes(opened,"nonce"),Bytes(opened,"ciphertext"),Bytes(opened,"tag")));
    }

    // The identifier an opened call sends, and the other served identifiers its model version accepts.
    public static ModelTarget Target(JsonElement opened)=>
        new(opened.GetProperty("identifier").GetString()!,[..opened.GetProperty("acceptedServed").EnumerateArray().Select(i=>i.GetString()!)]);

    static byte[] Bytes(JsonElement opened,string name)=>Convert.FromHexString(opened.GetProperty(name).GetString()!);

    // A call succeeds only for a well-formed response that ends normally or at the output limit with recognized usage.
    // A stream this program cancelled ends with the outcome its caller names.
    public static string Outcome(ModelEvent end,string cancelled)=>end switch
    {
        ModelCompleted{Stop:ModelStop.EndTurn or ModelStop.OutputLimit,Usage.Status:UsageStatus.Recognized}=>"succeeded",
        ModelCompleted{Stop:ModelStop.EndTurn or ModelStop.OutputLimit}=>"usage_unrecognized",
        ModelCompleted{Stop:ModelStop.Refusal}=>"model_refused",
        ModelCompleted{Stop:ModelStop.ContextWindow}=>"context_window_exceeded",
        // No call offers a tool yet, so a tool request is not the documented response.
        ModelCompleted=>"model_provider_transport",
        ModelFailed{Failure:ModelFailure.Cancelled}=>cancelled,
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

    // Closes a call's record through `close`, close_test or close_turn_call, with the usage and served identifier its ending
    // reported, or unknown usage after a stream that ended without one, as a write that must land. A close after one
    // landed changes nothing.
    public static Task Close(AgentWorker worker,string close,Guid call,byte[] token,string outcome,ModelEvent? end)
    {
        var (usage,served)=end switch
        {
            ModelCompleted c=>(c.Usage,c.Served),
            ModelFailed f=>(f.Usage,f.Served),
            _=>(ModelUsage.Unknown(MessagesAdapter.Mapping),null)
        };
        object[] arguments=[call,token,outcome,served is {} identifier&&ModelTarget.Valid(identifier)?identifier:DBNull.Value,Status(usage.Status),
            ..Count(usage.UncachedInput),..Count(usage.CacheRead),..Count(usage.CacheWrite),..Count(usage.Output),..Count(usage.Reasoning)];
        return worker.Settle(async(connection,ct)=>
        {
            await using var command=ScheduleStore.Command(connection,
                $"SELECT assistant.{close}($1,$2,$3,$4::text,$5,$6::bigint,$7,$8::bigint,$9,$10::bigint,$11,$12::bigint,$13,$14::bigint,$15)",arguments);
            return await command.ExecuteNonQueryAsync(ct);
        });
    }

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
