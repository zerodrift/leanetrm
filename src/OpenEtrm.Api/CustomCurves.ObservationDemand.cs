using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Curves;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.Api;

public static partial class CustomCurves
{
    // Calendar availability plans the native samples before loading prices. The
    // same source materialization used by valuation supplies those demands.
    internal static async Task<(ImmutableArray<ObservationWindow> Windows,ImmutableArray<Guid> ExactSeries,ImmutableArray<Guid> ExactObservations)> ObservationDemand(
        NpgsqlConnection connection,PreviewDatabase database,RecipeDefinition definition,PreviewData data,InputFrame frame,
        ImmutableDictionary<string,CurveSourceReference> sources,ImmutableDictionary<string,CalendarReference> calendars,
        ImmutableDictionary<string,CurveScheduleReference> schedules,ScheduleContext context,CancellationToken ct,
        Dictionary<CalendarReference,ReferenceCalendar>? calendarReads=null)
    {
        RequireFrameContext(frame,context);
        var preparation=new RecipePreparation(connection,database,data,frame,context,CalculationContext(frame,data.DefinitionRevision,context),
            await new CustomCurvesStore(database).Read(connection,ct),calendarReads:calendarReads);
        var windows=new HashSet<ObservationWindow>();var exactSeries=new HashSet<Guid>();var exactObservations=new HashSet<Guid>();
        await preparation.CollectObservations(definition,sources,calendars,schedules,Collect,ct);
        return([..windows],[..exactSeries],[..exactObservations]);

        void Collect(InputFrame sample,ImmutableArray<CurveRequest> requests,bool exactFrame,IReadOnlyDictionary<CurveRequest,ContractPriceTermsBinding> priceTerms)
        {
            ct.ThrowIfCancellationRequested();
            var leaves=SourceLeaves(data,sample,requests,ct,priceTerms);
            exactObservations.UnionWith(leaves.Where(leaf=>leaf.ExactObservation is not null).Select(leaf=>leaf.ExactObservation!.Value.Id));
            var ordinary=leaves.Where(leaf=>leaf.ExactObservation is null);
            if(exactFrame){exactSeries.UnionWith(ordinary.Select(leaf=>leaf.Series.Id));return;}
            foreach(var leaf in ordinary)windows.Add(new(leaf.Series.Id,leaf.Source.Code,PreviewData.RoleCode(leaf.Role),leaf.FinalFrom??sample.At,sample.At));
        }
    }
}
