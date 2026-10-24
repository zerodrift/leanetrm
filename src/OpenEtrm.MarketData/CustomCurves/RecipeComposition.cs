using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace OpenEtrm.MarketData.CustomCurves;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecipeCompositionInput([property:JsonRequired] PublishedRecipeReference Source,[property:JsonRequired] string Weight);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecipeCompositionRequest([property:JsonRequired] string Code,[property:JsonRequired] string Name,
    [property:JsonRequired] string Aggregation,[property:JsonRequired] string Alignment,
    [property:JsonRequired] ImmutableArray<RecipeCompositionInput> Inputs);

// The guided editor and agent use the same ordinary recipe contract. This
// operation assembles references and explicit choices; it never prices a panel.
public static class RecipeComposition
{
    public static RecipeDefinition Build(RecipeCompositionRequest request)
    {
        if(request is null||request.Inputs.IsDefaultOrEmpty||request.Inputs.Length>30||request.Inputs.Any(i=>i is null))
            throw new MarketDataException("recipe_definition","Choose one to 30 published curves and supply an explicit weight for each.");
        var nodes=ImmutableArray.CreateBuilder<RecipeNode>();var connections=ImmutableDictionary.CreateBuilder<string,string>();
        var weights=ImmutableArray.CreateBuilder<RecipePanelWeight>();
        for(var i=0;i<request.Inputs.Length;i++)
        {
            var id="curve_"+(i+1).ToString(System.Globalization.CultureInfo.InvariantCulture);var input=request.Inputs[i];
            nodes.Add(new(id,"published_source",ImmutableDictionary<string,string>.Empty.Add("selection","all_output_points_v1"),[],PublishedSource:input.Source));
            connections.Add(id,id);weights.Add(new(id,input.Weight));
        }
        nodes.Add(new("combine","weighted_panels",ImmutableDictionary<string,string>.Empty.Add("aggregation",request.Aggregation).Add("alignment",request.Alignment),
            connections.ToImmutable(),PanelWeights:weights.ToImmutable()));
        nodes.Add(new("output","output",[],ImmutableDictionary<string,string>.Empty.Add("values","combine")));
        var definition=new RecipeDefinition(request.Code,request.Name,nodes.ToImmutable(),"output");
        RecipeEngine.Validate(definition);return definition;
    }
}
