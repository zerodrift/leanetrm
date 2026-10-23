using System.Collections.Immutable;

namespace OpenEtrm.Api;

// Routing uses the admitted vendor, never the spelling of a model identifier. Each mapping is
// installed by the matching schema migration; there is no cross-provider retry or fallback.
public sealed class ModelAdapters(HttpMessageHandler handler,ModelTransportLimits limits)
{
    readonly MessagesAdapter messages=new(handler,limits);
    readonly ResponsesAdapter openai=new("openai",handler,limits),xai=new("xai",handler,limits),meta=new("meta",handler,limits);
    readonly GenerateContentAdapter google=new(handler,limits);
    public static string? Mapping(string vendor)=>vendor switch
    {
        "anthropic"=>MessagesAdapter.Mapping,"openai"=>"openai_responses_v1","xai"=>"xai_responses_v1","google"=>GenerateContentAdapter.Mapping,"meta"=>"meta_responses_v1",_=>null
    };
    public static void Validate(ModelRequest request,string key)
    {
        switch(request.Model.Vendor)
        {
            case "anthropic":MessagesAdapter.Validate(request,key);break;
            case "openai" or "xai" or "meta":ResponsesAdapter.Validate(request,key);break;
            case "google":GenerateContentAdapter.Validate(request,key);break;
            default:throw new ModelCallException("model_mapping_unconfirmed",ModelCatalog.Refusal("model_mapping_unconfirmed"));
        }
    }
    public IAsyncEnumerable<ModelEvent> Stream(ModelRequest request,string key,CancellationToken ct)
    {
        Validate(request,key);
        return request.Model.Vendor switch
        {
            "anthropic"=>messages.Stream(request,key,ct),"openai"=>openai.Stream(request,key,ct),
            "xai"=>xai.Stream(request,key,ct),"meta"=>meta.Stream(request,key,ct),"google"=>google.Stream(request,key,ct),_=>throw new InvalidOperationException()
        };
    }
    internal static void ValidateRequest(ModelRequest request,string key,ImmutableArray<string> efforts)
    {
        if(!ModelTarget.Valid(request.Model.Identifier))throw new ModelCallException("model_identifier_invalid",ModelCatalog.Refusal("model_identifier_invalid"));
        if(request.Parameters.Reasoning is not {} effort||!efforts.Contains(effort))throw new ModelCallException("parameter_required","Choose one of the model's documented reasoning efforts.");
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Parameters.MaxOutputTokens,1);
        if(request.Messages.IsDefaultOrEmpty||request.Tools.IsDefault||request.Model.AcceptedServed.IsDefault||request.System.Length==0||request.Messages[^1] is ModelReply)
            throw new ArgumentException("A call needs instructions, explicit lists and a user message or tool results.");
        if(!ModelProviders.KeyFormat().IsMatch(key))throw new ModelCallException("credential_unavailable","The provider key cannot be used.");
    }
}
