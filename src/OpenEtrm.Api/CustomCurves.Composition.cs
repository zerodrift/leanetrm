using System.Text.Json.Serialization;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CustomCurveCompositionRequest([property:JsonRequired] RecipeCompositionRequest Composition,[property:JsonRequired] Guid SnapshotId);

public static partial class CustomCurves
{
    static string DescribeComposition(RecipeCompositionRequest request,IReadOnlyDictionary<(Guid,int),CustomCurveVersion> versions)
    {
        var inputs=request.Inputs.Select(i=>$"{i.Weight} × {versions[(i.Source.CurveId,i.Source.Version)].Definition.Name} (v{i.Source.Version})");
        return (request.Aggregation==RecipeEngine.WeightedMean?"Weighted average of ":"Weighted sum of ")+string.Join("; ",inputs)+
            ". Match exact delivery periods, with one value per period in every curve. All dependencies remain connected.";
    }
}
