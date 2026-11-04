using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.Api;

public static partial class CustomCurves
{
    // Calendar availability plans the native samples before loading prices. The
    // same source materialization used by valuation supplies those demands.
    internal static async Task<(ImmutableArray<ObservationWindow> Windows,ImmutableArray<Guid> ExactSeries)> ObservationDemand(
        NpgsqlConnection connection,PreviewDatabase database,RecipeDefinition definition,PreviewData data,InputFrame frame,
        ImmutableDictionary<string,CurveSourceReference> sources,ImmutableDictionary<string,CalendarReference> calendars,
        ImmutableDictionary<string,CurveScheduleReference> schedules,ScheduleContext context,CancellationToken ct,
        Dictionary<CalendarReference,ReferenceCalendar>? calendarReads=null)
    {
        RequireFrameContext(frame,context);
        var preparation=new RecipePreparation(connection,database,data,frame,context,CalculationContext(frame,data.DefinitionRevision,context),
            await new CustomCurvesStore(database).Read(connection,ct),calendarReads:calendarReads);
        var windows=new HashSet<ObservationWindow>();var exactSeries=new HashSet<Guid>();
        await preparation.CollectObservations(definition,sources,calendars,schedules,Collect,ct);
        return([..windows],[..exactSeries]);

        void Collect(InputFrame sample,ImmutableArray<CurveRequest> requests,bool exactFrame)
        {
            ct.ThrowIfCancellationRequested();
            var leaves=SourceLeaves(data,sample,requests,ct);
            if(exactFrame){exactSeries.UnionWith(leaves.Select(leaf=>leaf.Series.Id));return;}
            foreach(var leaf in leaves)windows.Add(new(leaf.Series.Id,leaf.Source.Code,PreviewData.RoleCode(leaf.Role),leaf.FinalFrom??sample.At,sample.At));
        }
    }
}
