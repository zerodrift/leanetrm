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
        PreviewData data,InputFrame frame,CustomCurveProjectionInput projection,PriceUnit unit,ImmutableArray<ProjectionDelivery> queries,CancellationToken ct)
    {
        if(frame.ContextId is not {} id)throw Invalid("Published projection qualification requires a retained context.");
        var context=(await new KnowledgeStore(data.DefinitionRevision.CatalogId).Read(connection,id,ct)).Context;
        var versions=await new CustomCurvesStore(database).Read(connection,ct);
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
    // Choices are qualified by the backend under the same exact financial view
    // as the editor. A missing/incompatible output never becomes an ordinary mark.
    internal static async Task<ImmutableArray<PublishedProjectionChoice>> ProjectionChoices(NpgsqlConnection connection,
        PreviewDatabase database,PreviewData data,InputFrame frame,CancellationToken ct)
    {
        if(frame.ContextId is not {} id)throw Invalid("Published projection choices require the editor's retained context.");
        var context=(await new KnowledgeStore(data.DefinitionRevision.CatalogId).Read(connection,id,ct)).Context;
        var versions=await new CustomCurvesStore(database).Read(connection,ct);
        var saved=versions.Where(v=>v.State=="published"&&v.EngineVersion==RecipeEngine.Version&&
            v.Sources.Values.All(s=>s.CatalogId==data.DefinitionRevision.CatalogId&&s.RevisionId==data.DefinitionRevision.RevisionId)).ToArray();
        var contextHeads=ContextVersions.Latest(saved,v=>v.Id,v=>v.Version).ToDictionary(v=>v.Id,v=>v.Version);
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
