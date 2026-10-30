using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public static partial class CustomCurves
{
    internal sealed record PublishedProjectionChoice(string Label,CustomCurveProjectionInput Reference,PriceUnit Unit,
        ImmutableArray<ProjectionDelivery> Deliveries=default);
    // Once every contractual fixing is due, the forecast is retained metadata,
    // not a demand for historical forecast samples. Still qualify its exact
    // published shape, quote and mapping under the current authorized context.
    internal static async Task RequireFixedProjection(NpgsqlConnection connection,PreviewDatabase database,
        PreviewData data,InputFrame frame,CustomCurveProjectionInput projection,PriceUnit unit,
        ImmutableArray<FloatingObservation> observations,CancellationToken ct)
    {
        if(frame.ContextId is not {} id)throw Invalid("Published projection qualification requires a retained context.");
        var context=(await new KnowledgeStore(data.DefinitionRevision.CatalogId).Read(connection,id,ct)).Context;
        var versions=await new CustomCurvesStore(database).Read(connection,ct);
        var resolved=CalculationContext(frame,data.DefinitionRevision,context);
        var preparation=new RecipePreparation(connection,database,data,frame,context,resolved,versions);
        var topology=await preparation.Topology(new(projection.CatalogId,projection.CurveId,projection.Version,projection.OutputNode),ct);
        var outputs=topology.Outputs;
        var qualified=projection.Selection==CustomCurveProjectionBinding.SupportedSelection?
            outputs.Count(output=>output.Position==projection.Position&&output.Unit==unit)==1&&outputs.Count(output=>output.Position==projection.Position)==1:
            observations.All(observation=>outputs.Count(output=>output.DeliveryFrom==observation.From&&output.DeliveryTo==observation.To)==1&&
                outputs.Any(output=>output.DeliveryFrom==observation.From&&output.DeliveryTo==observation.To&&output.Unit==unit));
        if(!qualified)throw new MarketDataException("custom_projection","The exact published projection must retain its qualified output unit and observation mapping.");
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
        if(saved.Length==0)return [];
        var sourceData=await database.Read(connection,ct,data.DefinitionRevision.RevisionId,data,includeCalendarDays:false,observationScope:ObservationReadScope.FactHistory);
        var choices=ImmutableArray.CreateBuilder<PublishedProjectionChoice>();
        var resolved=CalculationContext(frame,data.DefinitionRevision,context);
        var preparation=new RecipePreparation(connection,database,sourceData,frame,context,resolved,versions);
        foreach(var version in saved)
        {
            ct.ThrowIfCancellationRequested();RecipeResult result;ConnectedRecipeGraph graph;
            try{graph=await preparation.Published(new(data.DefinitionRevision.CatalogId,version.Id,version.Version,version.Definition.Output),ct);result=EvaluateGraph(graph,resolved,database,ct);}
            catch(MarketDataException error)when(error.Code is "recipe_input" or "recipe_definition" or "recipe_reference" or "custom_curve_definition" or "missing_observation" or "price_domain" or "missing_constituent" or "source_expired")
            {continue;}
            foreach(var group in result.Points.GroupBy(p=>p.Position).Where(g=>g.Count()==1))
            {
                var point=group.Single();
                choices.Add(new($"{version.Definition.Name} · version {version.Version}, position {point.Position}",
                    new(data.DefinitionRevision.CatalogId,version.Id,version.Version,version.Definition.Output,point.Position,
                        CustomCurveProjectionBinding.SupportedSelection,CustomCurveProjectionBinding.SupportedImplementation),point.Price.Unit));
            }
            var deliveries=ProjectionMapping(graph).DailyCoverage;
            if(!deliveries.IsEmpty&&
                result.Points.Select(p=>p.Price.Unit).Distinct().Count()==1)
                choices.Add(new($"{version.Definition.Name} · version {version.Version} · exact daily delivery mapping",
                    new(data.DefinitionRevision.CatalogId,version.Id,version.Version,version.Definition.Output,null,
                        CustomCurveProjectionBinding.DeliverySelection,CustomCurveProjectionBinding.SupportedImplementation),result.Points[0].Price.Unit,deliveries));
        }
        return choices.ToImmutable();
    }
}
