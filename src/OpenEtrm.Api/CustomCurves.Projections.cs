using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public static partial class CustomCurves
{
    internal sealed record PublishedProjectionChoice(string Label,CustomCurveProjectionInput Reference,PriceUnit Unit);
    // Choices are qualified by the backend under the same exact financial view
    // as the editor. A missing/incompatible output never becomes an ordinary mark.
    internal static async Task<ImmutableArray<PublishedProjectionChoice>> ProjectionChoices(NpgsqlConnection connection,
        PreviewDatabase database,PreviewData data,InputFrame frame,CancellationToken ct)
    {
        if(frame.ContextId is not {} id)throw Invalid("Published projection choices require the editor's retained context.");
        var context=(await new KnowledgeStore(data.DefinitionRevision.CatalogId).Read(connection,id,ct)).Context;
        var saved=(await new CustomCurvesStore(database).Read(connection,ct)).Where(v=>v.State=="published"&&v.EngineVersion==RecipeEngine.Version&&
            v.Sources.Values.All(s=>s.CatalogId==data.DefinitionRevision.CatalogId&&s.RevisionId==data.DefinitionRevision.RevisionId)).ToArray();
        if(saved.Length==0)return [];
        var sourceData=await database.Read(connection,ct,data.DefinitionRevision.RevisionId,data,includeCalendarDays:false,observationScope:ObservationReadScope.FactHistory);
        var choices=ImmutableArray.CreateBuilder<PublishedProjectionChoice>();
        foreach(var version in saved)
        {
            ct.ThrowIfCancellationRequested();RecipeResult result;
            try{result=await EvaluateDefinition(connection,database,version.Definition,sourceData,frame,version.Sources,version.Calendars,version.Schedules,context,ct);}
            catch(MarketDataException error)when(error.Code is "recipe_input" or "recipe_definition" or "recipe_reference" or "custom_curve_definition" or "missing_observation" or "price_domain" or "missing_constituent")
            {continue;}
            foreach(var group in result.Points.GroupBy(p=>p.Position).Where(g=>g.Count()==1))
            {
                var point=group.Single();
                choices.Add(new($"{version.Definition.Name} · version {version.Version}, position {point.Position}",
                    new(data.DefinitionRevision.CatalogId,version.Id,version.Version,version.Definition.Output,point.Position,
                        CustomCurveProjectionBinding.SupportedSelection,CustomCurveProjectionBinding.SupportedImplementation),point.Price.Unit));
            }
        }
        return choices.ToImmutable();
    }
}
