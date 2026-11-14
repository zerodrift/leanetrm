using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public static partial class CustomCurves
{
    internal sealed record PublishedProjectionChoice(string Label,CustomCurveProjectionInput Reference,PriceUnit Unit,
        ImmutableArray<ProjectionDelivery> Deliveries=default)
    {
        public bool SelectedInContext{get;init;}
        public bool Covers(IEnumerable<ProjectionDelivery> queries)
        {
            if(Reference.Selection==CustomCurveProjectionBinding.SupportedSelection)return true;
            try{_=CurvePriceMapping.RequiredIntervals(Reference.Selection,Deliveries,queries);return true;}
            catch(MarketDataException error)when(error.Code=="curve_coverage"){return false;}
        }
    }
    // Once every contractual fixing is due, the forecast is retained metadata,
    // not a demand for historical forecast samples. Still qualify its exact
    // published shape, quote and mapping under the current authorized context.
    internal static Task RequireFixedProjection(NpgsqlConnection connection,PreviewDatabase database,
        PreviewData data,InputFrame frame,CustomCurveProjectionInput projection,PriceUnit unit,
        ImmutableArray<FloatingObservation> observations,CancellationToken ct)=>
        RequireProjection(connection,database,data,frame,projection,unit,[..observations.Select(observation=>new ProjectionDelivery(observation.From,observation.To))],ct);
    internal static async Task RequireProjection(NpgsqlConnection connection,PreviewDatabase database,
        PreviewData data,InputFrame frame,CustomCurveProjectionInput projection,PriceUnit unit,ImmutableArray<ProjectionDelivery> queries,CancellationToken ct,
        SavedVersions? savedVersions=null)
    {
        if(frame.ContextId is not {} id)throw Invalid("Published projection qualification requires a retained context.");
        var context=(await new KnowledgeStore(data.DefinitionRevision.CatalogId).Read(connection,id,ct)).Context;
        var versions=await (savedVersions?.Read(connection,ct)??new CustomCurvesStore(database).Read(connection,ct));
        RequireNoContractObservations(versions,projection);
        var resolved=CalculationContext(frame,data.DefinitionRevision,context);
        var preparation=new RecipePreparation(connection,database,data,frame,context,resolved,versions);
        var topology=await preparation.Topology(new(projection.CatalogId,projection.CurveId,projection.Version,projection.OutputNode),ct);
        if(!ProjectionShapeMatches(topology.Outputs,projection,unit,queries))
            throw new MarketDataException("custom_projection","The exact published projection must retain its qualified output unit and observation mapping.");
    }
    public static bool ProjectionShapeMatches(ImmutableArray<RecipeOutputShape> outputs,CustomCurveProjectionInput projection,PriceUnit unit,
        ImmutableArray<ProjectionDelivery> queries)
    {
        if(projection.Selection==CustomCurveProjectionBinding.SupportedSelection)
            return outputs.Count(output=>output.Position==projection.Position&&output.Unit==unit)==1&&outputs.Count(output=>output.Position==projection.Position)==1;
        try
        {
            _=CurvePriceMapping.RequiredIntervals(projection.Selection,outputs.Select(output=>new ProjectionDelivery(output.DeliveryFrom,output.DeliveryTo)),queries);
            return !outputs.IsEmpty&&outputs.All(output=>output.Unit==unit);
        }
        catch(MarketDataException error)when(error.Code=="curve_coverage"){return false;}
    }
    // A trade names its projection in its own terms, so admission refuses one that reads contract observation terms before
    // any other term is qualified.
    internal static async Task RequireTradeProjections(NpgsqlConnection connection,PreviewDatabase database,
        IEnumerable<CustomCurveProjectionInput?> projections,CancellationToken ct)
    {
        var named=projections.OfType<CustomCurveProjectionInput>().ToArray();
        if(named.Length==0)return;
        var versions=await new CustomCurvesStore(database).Read(connection,ct);
        foreach(var projection in named)RequireNoContractObservations(versions,projection);
    }
    static void RequireNoContractObservations(ImmutableArray<CustomCurveVersion> versions,CustomCurveProjectionInput projection)
    {
        if(ReadsContractObservations(versions,projection.CurveId,projection.Version))
            throw new MarketDataException("custom_projection","A curve that reads contract observation terms has no qualified trade projection.");
    }
    // D391 qualifies contract observation terms for curve preparation only: a curve that reads them, itself or through any
    // published source, is no trade projection until a consumer is qualified.
    static bool ReadsContractObservations(ImmutableArray<CustomCurveVersion> versions,Guid curve,int version)
    {
        var visited=new HashSet<(Guid,int)>();
        var pending=new Stack<(Guid Curve,int Version)>([(curve,version)]);
        while(pending.TryPop(out var next))
        {
            if(!visited.Add(next))continue;
            var definition=versions.FirstOrDefault(v=>v.Id==next.Curve&&v.Version==next.Version)?.Definition;
            if(definition is null)continue;
            if(definition.Nodes.Any(n=>n.Kind==RecipeEngine.ContractObservations))return true;
            foreach(var source in definition.Nodes.Where(n=>n.Kind=="published_source"))pending.Push((source.PublishedSource!.CurveId,source.PublishedSource.Version));
        }
        return false;
    }
    // Choices are qualified by the backend under the same exact financial view
    // as the editor. A missing/incompatible output never becomes an ordinary mark.
    internal static async Task<ImmutableArray<PublishedProjectionChoice>> ProjectionChoices(NpgsqlConnection connection,
        PreviewDatabase database,PreviewData data,InputFrame frame,CancellationToken ct)
    {
        if(frame.ContextId is not {} id)throw Invalid("Published projection choices require the editor's retained context.");
        var context=(await new KnowledgeStore(data.DefinitionRevision.CatalogId).Read(connection,id,ct)).Context;
        var versions=await new CustomCurvesStore(database).Read(connection,ct);
        var eligible=versions.Where(v=>RecipeEngine.PowerIntervalOutput(v.Definition) is null&&v.State=="published"&&v.EngineVersion==RecipeEngine.Version&&
            v.Sources.Values.All(s=>s.CatalogId==data.DefinitionRevision.CatalogId&&s.RevisionId==data.DefinitionRevision.RevisionId)).ToArray();
        // The version in force is decided before the exclusion, so an older version is never presented as the context's own.
        var contextHeads=ContextVersions.Latest(eligible,v=>v.Id,v=>v.Version).ToDictionary(v=>v.Id,v=>v.Version);
        var saved=eligible.Where(v=>!ReadsContractObservations(versions,v.Id,v.Version)).ToArray();
        if(saved.Length==0)return [];
        var sourceData=data;
        var choices=ImmutableArray.CreateBuilder<PublishedProjectionChoice>();
        var resolved=CalculationContext(frame,data.DefinitionRevision,context);
        var preparation=new RecipePreparation(connection,database,sourceData,frame,context,resolved,versions);
        foreach(var version in saved)
        {
            ct.ThrowIfCancellationRequested();RecipeTopology topology;
            try{topology=await preparation.Topology(new(data.DefinitionRevision.CatalogId,version.Id,version.Version,version.Definition.Output),ct);}
            catch(MarketDataException error)when(error.Code is "recipe_input" or "recipe_definition" or "recipe_reference" or "custom_curve_definition" or "missing_observation" or "price_domain" or "missing_constituent" or "source_expired")
            {continue;}
            foreach(var group in topology.Outputs.GroupBy(p=>p.Position).Where(g=>g.Count()==1))
            {
                var point=group.Single();
                choices.Add(new($"{version.Definition.Name} · version {version.Version}, position {point.Position}",
                    new(data.DefinitionRevision.CatalogId,version.Id,version.Version,version.Definition.Output,point.Position,
                        CustomCurveProjectionBinding.SupportedSelection,CustomCurveProjectionBinding.SupportedImplementation),point.Unit));
            }
            var deliveries=topology.Outputs.Select(output=>new ProjectionDelivery(output.DeliveryFrom,output.DeliveryTo)).OrderBy(interval=>interval.From).ThenBy(interval=>interval.To).ToImmutableArray();
            var disjoint=!deliveries.IsEmpty&&deliveries.Zip(deliveries.Skip(1)).All(pair=>pair.First.To<=pair.Second.From);
            if(disjoint&&topology.Outputs.Select(output=>output.Unit).Distinct().Count()==1)
            {
                choices.Add(new($"{version.Definition.Name} · version {version.Version} · containing delivery interval",
                    new(data.DefinitionRevision.CatalogId,version.Id,version.Version,version.Definition.Output,null,
                        CustomCurveProjectionBinding.ContainingDeliverySelection,CustomCurveProjectionBinding.SupportedImplementation),topology.Outputs[0].Unit,deliveries));
                if(deliveries.All(interval=>interval.To.DayNumber-interval.From.DayNumber==1))
                choices.Add(new($"{version.Definition.Name} · version {version.Version} · exact daily delivery mapping",
                    new(data.DefinitionRevision.CatalogId,version.Id,version.Version,version.Definition.Output,null,
                        CustomCurveProjectionBinding.DeliverySelection,CustomCurveProjectionBinding.SupportedImplementation),topology.Outputs[0].Unit,deliveries));
            }
        }
        return [..choices.Select(choice=>choice with{SelectedInContext=contextHeads[choice.Reference.CurveId]==choice.Reference.Version})];
    }
}
