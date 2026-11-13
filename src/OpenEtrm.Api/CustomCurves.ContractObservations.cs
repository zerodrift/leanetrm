using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

public static partial class CustomCurves
{
    // The resolved windows of every contract observation node and the source samples they read. A day before the context
    // date reads the historical sample of each contract a term selects that day; the context date and later days read the
    // live context sample of the contract selected for that day.
    internal sealed record ObservationPlan(ImmutableDictionary<string,ImmutableArray<RecipeObservationWindow>> Windows,
        ImmutableArray<(string Source,DateOnly Date,ContractKey Contract)> Samples);

    // Windows resolve from each source's retained contract universe at the context's UTC time of day and, for business days,
    // from the node's exact pinned calendar.
    static async Task<ObservationPlan> ContractObservations(RecipeDefinition definition,PreviewData data,
        ImmutableDictionary<string,CurveSourceReference> sourceReferences,DateTimeOffset at,Func<string,Task<DateSet>> pinnedCalendar)
    {
        var nodes=new List<(RecipeNode,RecipeSource,DateSet?)>();
        foreach(var node in definition.Nodes.Where(n=>n.Kind==RecipeEngine.ContractObservations))
            nodes.Add((node,NativeSource(data,sourceReferences[node.Inputs["source"]]),
                node.Parameters.GetValueOrDefault("days")==RecipeEngine.BusinessDays?await pinnedCalendar(node.Id):null));
        return PlanObservations(nodes,at);
    }

    internal static ObservationPlan PlanObservations(IEnumerable<(RecipeNode Node,RecipeSource Source,DateSet? BusinessDays)> nodes,DateTimeOffset at)
    {
        var context=DateOnly.FromDateTime(at.UtcDateTime);var time=TimeOnly.FromDateTime(at.UtcDateTime);
        var windows=ImmutableDictionary.CreateBuilder<string,ImmutableArray<RecipeObservationWindow>>();
        var samples=new HashSet<(string,DateOnly,ContractKey)>();
        foreach(var (node,source,businessDays) in nodes)
        {
            var sourceNode=node.Inputs["source"];
            var resolved=RecipeEngine.ObservationWindows(node,source,businessDays,time);
            windows.Add(node.Id,resolved);
            samples.UnionWith(RecipeEngine.ObservationContracts(node,source,resolved,time).Select(o=>(sourceNode,o.Day<context?o.Day:context,o.Contract.Contract.Key)));
        }
        // One historical frame is read per past observation day; as for date sequences, a preparation reads at most 366 of them.
        if(samples.Where(s=>s.Item2<context).Select(s=>s.Item2).Distinct().Count()>366)
            throw Invalid("A preview can read at most 366 past observation days across its contract observation operations.");
        return new(windows.ToImmutable(),[..samples]);
    }

    // A past observation day without its retained sample refuses the whole average; it is never taken over fewer days.
    static async Task<MarketSnapshot> ObservedHistory(ObservationPlan plan,string sourceNode,DateOnly sampleDay,DateOnly context,Func<Task<MarketSnapshot>> read)
    {
        try{return await read();}
        catch(MarketDataException error)when(error.Code=="missing_observation"&&sampleDay<context&&plan.Samples.Any(s=>s.Source==sourceNode&&s.Date==sampleDay))
        {throw new MarketDataException("recipe_input",$"Observation day {sampleDay:yyyy-MM-dd} has no retained sample of a contract it observes.");}
    }
}
