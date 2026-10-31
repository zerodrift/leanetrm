using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.Api;

public static partial class CustomCurves
{
    // One owner prepares each exact published version once under this context.
    // Imported panels keep graph references; no calculated price is materialized
    // into a replacement market observation or fictitious contract.
    private sealed class RecipePreparation(NpgsqlConnection connection,PreviewDatabase database,
        PreviewData data,InputFrame frame,ScheduleContext context,ResolvedCalculationContext resolved,
        ImmutableArray<CustomCurveVersion> versions,IReadOnlyDictionary<(Guid,int),ImmutableHashSet<RecipeDelivery>?>? demands=null)
    {
        readonly Dictionary<(Guid,int),ConnectedRecipeGraph> completed=[];
        readonly HashSet<(Guid,int)> active=[];
        readonly Dictionary<(Guid,int),RecipeTopology> topologies=[];
        readonly HashSet<(Guid,int)> planning=[];
        readonly List<ConnectedRecipeSample> samples=[];
        public ImmutableArray<ConnectedRecipeSample> Samples=>RetainSamples(samples);
        public int SourceCompilations{get;private set;}
        public int DeclaredSourceNodes{get;private set;}

        public async Task<RecipeTopology> Topology(PublishedRecipeReference reference,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();var key=(reference.CurveId,reference.Version);
            if(topologies.TryGetValue(key,out var prior))return prior;
            if(reference.CatalogId!=data.DefinitionRevision.CatalogId||planning.Count>32||topologies.Count+planning.Count>=256||!planning.Add(key))
                throw Invalid("Published topology requires an acyclic exact graph within the preparation bounds.");
            try
            {
                var version=versions.SingleOrDefault(v=>v.Id==reference.CurveId&&v.Version==reference.Version);
                if(version is null||version.State!="published"||version.EngineVersion!=RecipeEngine.Version||version.Definition.Output!=reference.OutputNode||version.PublishedAt is null||version.PublishedAt>resolved.KnownAt)
                    throw Invalid("The exact published topology is unavailable in this context.");
                await new CustomCurvesStore(database).RequirePublishedSource(connection,reference,data.DefinitionRevision.RevisionId,ct);
                var parents=new Dictionary<string,RecipeTopology>();
                foreach(var node in version.Definition.Nodes.Where(n=>n.Kind=="published_source"))parents.Add(node.Id,await Topology(node.PublishedSource!,ct));
                var metadata=await Inputs(connection,database,version.Definition,data,frame,version.Sources,version.Calendars,version.Schedules,context,ct,topologyOnly:true);
                var instants=metadata.Values.Dates.Values.SelectMany(d=>d).Append(metadata.Values.ContextDate).Distinct()
                    .ToImmutableDictionary(d=>d,d=>new DateTimeOffset(d.ToDateTime(TimeOnly.FromDateTime(frame.At.UtcDateTime),DateTimeKind.Utc)));
                var result=RecipeEngine.Topology(version.Definition,metadata.Values,parents,instants,ct);topologies.Add(key,result);return result;
            }
            finally{planning.Remove(key);}
        }

        public async Task<ConnectedRecipeGraph> Published(PublishedRecipeReference reference,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(reference.CatalogId!=data.DefinitionRevision.CatalogId)throw Invalid("Published sources must use this exact market catalog.");
            var key=(reference.CurveId,reference.Version);
            ImmutableHashSet<RecipeDelivery>? deliveries=null;
            if(demands is not null&&!demands.TryGetValue(key,out deliveries))throw Invalid("Every published source requires its coalesced consumer demand.");
            if(completed.TryGetValue(key,out var graph))
            {if(versions.Single(v=>v.Id==reference.CurveId&&v.Version==reference.Version).Definition.Output!=reference.OutputNode)throw Invalid("Choose the version's exact published output.");return graph;}
            if(active.Count>32||completed.Count+active.Count>=256)throw Invalid("Published source preparation supports 32 dependency edges and 256 exact versions.");
            if(!active.Add(key))throw Invalid("Published curve dependencies must be acyclic.");
            try
            {
                var version=versions.SingleOrDefault(v=>v.Id==reference.CurveId&&v.Version==reference.Version);
                if(version is null||version.State!="published"||version.EngineVersion!=RecipeEngine.Version||
                    version.Definition.Output!=reference.OutputNode||version.PublishedAt is null||version.PublishedAt>resolved.KnownAt||
                    version.Sources.Values.Any(s=>s.CatalogId!=reference.CatalogId||s.RevisionId!=data.DefinitionRevision.RevisionId))
                    throw Invalid("The exact published source or one of its references is unavailable in this context.");
                await new CustomCurvesStore(database).RequirePublishedSource(connection,reference,data.DefinitionRevision.RevisionId,ct);
                graph=await Definition(version.Definition,new(reference.CatalogId,reference.CurveId,reference.Version,version.EngineVersion),
                    version.Sources,version.Calendars,version.Schedules,ct,deliveries);
                completed.Add(key,graph);return graph;
            }
            finally{active.Remove(key);}
        }

        public async Task<ConnectedRecipeGraph> Definition(RecipeDefinition definition,ConnectedRecipeIdentity identity,
            ImmutableDictionary<string,CurveSourceReference> sources,ImmutableDictionary<string,CalendarReference> calendars,
            ImmutableDictionary<string,CurveScheduleReference> schedules,CancellationToken ct,ImmutableHashSet<RecipeDelivery>? deliveries=null)
        {
            Validate(definition);
            var published=ImmutableDictionary.CreateBuilder<string,ConnectedRecipeGraph>();
            foreach(var node in definition.Nodes.Where(n=>n.Kind=="published_source"))
                published.Add(node.Id,await Published(node.PublishedSource!,ct));
            var topology=deliveries is null?null:await Topology(new(identity.CatalogId,identity.CurveId,identity.Version,definition.Output),ct);
            var materialized=await Inputs(connection,database,definition,data,frame,sources,calendars,schedules,context,ct,deliveries,topology);
            var graph=RecipeEngine.Connect(definition,materialized.Values,identity,materialized.Samples,resolved,ct,materialized.References,published,materialized.Demand);
            samples.AddRange(materialized.Samples);
            SourceCompilations+=graph.SourceCompilations;DeclaredSourceNodes+=graph.DeclaredSourceNodes;
            return graph;
        }
    }
}
