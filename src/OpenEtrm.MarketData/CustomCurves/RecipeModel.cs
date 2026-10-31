using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.CustomCurves;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecipeContractWeight(int OutputPosition,string SourcePort,Guid ContractId,string Weight);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecipePanelWeight([property:JsonRequired] string SourcePort,[property:JsonRequired] string Weight);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PublishedRecipeReference(Guid CatalogId,Guid CurveId,int Version,string OutputNode);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecipeNode(string Id,string Kind,ImmutableDictionary<string,string> Parameters,ImmutableDictionary<string,string> Inputs,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<RecipeContractWeight> ContractWeights=default,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] PublishedRecipeReference? PublishedSource=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<RecipePanelWeight> PanelWeights=default);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecipeDefinition(string Code,string Name,ImmutableArray<RecipeNode> Nodes,string Output)
{
    // Whether `other` states this same graph: its code, name, output and each node's content, in any order of statement.
    public bool SameGraph(RecipeDefinition other)=>JsonNode.DeepEquals(Evidence(),other.Evidence());
    JsonNode Evidence()=>JsonSerializer.SerializeToNode(new{Code,Name,Output,
        Nodes=Nodes.OrderBy(n=>n.Id,StringComparer.Ordinal).Select(ConnectedRecipeDeclaration.NodeEvidence).ToArray()})!;
}
public sealed record RecipeContract(MarketContract Contract,DateTimeOffset EligibleFrom);
public sealed record RecipeSample(DateOnly Date,DateTimeOffset At,ContractKey Contract,UnitPrice Price,
    ImmutableArray<ObservationKey> Observations,ImmutableArray<QuoteFactor> Factors,bool Fixed);
public sealed record RecipeSource(CurveKey Key,PriceUnit Unit,ImmutableArray<RecipeContract> Contracts);
public sealed record RecipeInputs(DateOnly ContextDate,DateTimeOffset ValuationAt,
    ImmutableDictionary<string,RecipeSource> Sources,ImmutableDictionary<string,ImmutableArray<DateOnly>> Dates);
// Explicit output intervals from a consumer or a downstream recipe. Sampling instants are supplied even
// for unconsumed cells; missing price samples never acquire substitute values.
public sealed record RecipeDelivery(DateOnly From,DateOnly To);
public sealed record RecipeOutputDemand(ImmutableHashSet<RecipeDelivery> Deliveries,
    ImmutableDictionary<DateOnly,DateTimeOffset> SamplingInstants);
public sealed record RecipePoint(string Label,int Position,ExactUnitPrice Price,ImmutableArray<ContractKey> Contracts,
    ImmutableArray<DateOnly> Dates,int SampleCount);
public sealed record RecipeResult(ImmutableArray<RecipePoint> Points,ImmutableArray<RecipeSample> Inputs,ImmutableArray<QuoteFactor> Factors);
