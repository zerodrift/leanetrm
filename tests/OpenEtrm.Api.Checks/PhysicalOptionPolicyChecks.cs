using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;

internal static class PhysicalOptionPolicyChecks
{
    internal static void RequireBindingFlags(JsonElement setup)
    {
        if(setup.GetProperty("pricing").EnumerateArray().Any(choice=>
            !choice.TryGetProperty("requiresFuturesBindings",out var future)||future.ValueKind is not(JsonValueKind.True or JsonValueKind.False)||
            !choice.TryGetProperty("requiresOptionBindings",out var option)||option.ValueKind is not(JsonValueKind.True or JsonValueKind.False)))
            throw new Exception("Every physical setup pricing choice must state both binding capabilities as booleans.");
    }
    internal static void Run()
    {
        var legacy=FuturesOptionValuationPolicy.Release1;
        var old=JsonSerializer.SerializeToNode(legacy,RiskJobs.Json)!.AsObject();
        if(old.Count!=4||old.ContainsKey("physical"))throw new Exception("The legacy option policy must retain its original four-field transport.");
        var model=new PhysicalOptionPolicy(PhysicalOptionPolicy.Version,PhysicalOptionPolicy.FrozenBasis,PhysicalOptionPolicy.ReferenceStopping,
            PhysicalOptionPolicy.ReferenceCalibration,PhysicalOptionPolicy.IntrinsicStrategy);
        var choice=new FuturesOptionPhysicalValuationSelection(Guid.NewGuid(),Guid.NewGuid(),model,FuturesPhysicalBasisValueBinding.ValuationProxy);
        var selected=new FuturesOptionPhysicalValuationPolicy(FuturesOptionPhysicalValuationPolicy.Version,[choice]);
        var policy=legacy with{Physical=selected};
        _=policy.Validate();
        var retained=JsonSerializer.Deserialize<FuturesOptionValuationPolicy>(JsonSerializer.Serialize(policy,RiskJobs.Json),RiskJobs.Json)!;
        if(retained.Physical?.Implementation!=selected.Implementation||!retained.Physical.Options.SequenceEqual(selected.Options))
            throw new Exception("The exact per-held physical strategy must survive retained request serialization.");
        foreach(var invalid in new[]{selected with{Implementation=""},selected with{Options=default},selected with{Options=[choice,choice]},
            selected with{Options=[choice with{TradeId=Guid.Empty}]},selected with{Options=[choice with{BindingId=Guid.Empty}]},
            selected with{Options=[null!]},selected with{Options=[choice with{Model=null!}]},selected with{Options=[choice with{MarkInterpretation=""}]}})
            Reject("physical_option_policy",()=>invalid.Validate());
        Reject("physical_option_policy",()=>(selected with{Options=[choice with{Model=model with{BasisLaw="inferred_basis"}}]}).Validate());
        (selected with{Options=[]}).Validate(); // Exact empty held scope is checked by the source materializer.
        var json=JsonSerializer.SerializeToNode(policy,RiskJobs.Json)!.AsObject();
        var physical=json["physical"]!.AsObject();var savedModel=physical["options"]![0]!["model"]!.AsObject();
        savedModel["unqualifiedLaw"]=true;
        Closed(json);savedModel.Remove("unqualifiedLaw");savedModel.Remove("basisLaw");Closed(json);
        var missing=JsonSerializer.SerializeToNode(policy,RiskJobs.Json)!.AsObject();missing["physical"]!.AsObject().Remove("options");Closed(missing);
        Console.WriteLine("PASS physical option policy: explicit per-held complete strategy, closed nested transport, exact retained choices and unchanged legacy bytes");
    }
    private static void Closed(JsonNode json)
    {try{_=JsonSerializer.Deserialize<FuturesOptionValuationPolicy>(json.ToJsonString(),RiskJobs.Json);}catch(JsonException){return;}throw new Exception("An incomplete or unknown physical option model field was silently accepted.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
