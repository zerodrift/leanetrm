using System.Collections.Immutable;
using Npgsql;

namespace OpenEtrm.Api;

// Immutable metadata reuse is limited to one operation and one knowledge view.
// Observation visibility and current authority are checked on every read.
internal sealed class MarketInputReadScope(PreviewDatabase database,NpgsqlConnection connection,Guid? contextId)
{
    private readonly Dictionary<(CatalogueSource Source,ReferenceReadScope References,bool CalendarDays),PreviewData> metadata=[];
    internal async Task<PreviewData> Read(InputFrame frame,CatalogueSource source,ImmutableArray<Guid> pins,
        ReferenceReadScope references,ObservationReadScope observations,bool calendarDays,CancellationToken ct)
    {
        if(frame.ContextId!=contextId)throw new InvalidOperationException("An input read scope cannot cross knowledge views.");
        if(observations!=ObservationReadScope.ExactPins)
            return await database.Read(connection,ct,ppaBundle:source,observationIds:pins,referenceScope:references,
                observationScope:observations,includeCalendarDays:calendarDays);
        var key=(source,references,calendarDays);
        if(!metadata.TryGetValue(key,out var data))
        {
            data=await database.Read(connection,ct,ppaBundle:source,observationIds:[],referenceScope:references,includeCalendarDays:calendarDays);
            // Bound memory across long histories with many immutable versions.
            // Eviction changes only read work, never the selected financial inputs.
            if(metadata.Count==32)metadata.Remove(metadata.Keys.First());
            metadata.Add(key,data);
        }
        else await MarketBundle.Require(connection,source.Apply(database.Manifest),ct);
        return await database.ReadObservationWindows(connection,data,[],ct,pins);
    }
}
