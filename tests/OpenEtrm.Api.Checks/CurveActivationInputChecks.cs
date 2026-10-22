using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;

internal static class CurveActivationInputChecks
{
    public static void Run()
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web){AllowDuplicateProperties=false};
        var request=JsonSerializer.SerializeToNode(new{requestId=Guid.NewGuid(),curveId=Guid.NewGuid(),expectedRevision=0,
            policy=CurveActivations.Policy,reason="Synthetic explicit activation",segments=new[]{new{effectiveFrom="0001-01-01",effectiveTo="2021-01-01",version=1,outputNode="output"}}})!;
        void Reject<T>(JsonNode value)
        {
            try{JsonSerializer.Deserialize<T>(value,options);}
            catch(JsonException){return;}
            throw new Exception("An omitted required input acquired a value during deserialization.");
        }
        var complete=request.Deserialize<CurveActivationRequest>(options)!;
        if(complete.ExpectedRevision!=0||complete.Segments[0].EffectiveFrom!=DateOnly.MinValue)
            throw new Exception("Explicit zero revision or minimum date was changed.");
        var missing=request.DeepClone();missing.AsObject().Remove("expectedRevision");Reject<CurveActivationRequest>(missing);
        foreach(var name in new[]{"effectiveFrom","effectiveTo","version","outputNode"})
        {
            missing=request.DeepClone();missing["segments"]![0]!.AsObject().Remove(name);Reject<CurveActivationRequest>(missing);
        }
        var projection=JsonSerializer.SerializeToNode(new{catalogId=Guid.NewGuid(),curveId=Guid.NewGuid(),version=1,outputNode="output",
            position=(int?)null,selection="exact_delivery_interval_v1",implementation="published_custom_curve_projection_v1"})!;
        if(projection.Deserialize<CustomCurveProjectionInput>(options)!.Position is not null)throw new Exception("Explicit null coordinate changed.");
        projection.AsObject().Remove("position");Reject<CustomCurveProjectionInput>(projection);
        Console.WriteLine("PASS curve activation inputs: explicit zero/minimum dates/null coordinates survive; omitted required fields reject");
    }
}
